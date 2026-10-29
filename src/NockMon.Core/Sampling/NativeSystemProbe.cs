using System.Diagnostics;
using System.Runtime.InteropServices;
using NockMon.Core.Model;
using NockMon.Core.Native;

namespace NockMon.Core.Sampling;

/// <summary>
/// Full scan: one NtQuerySystemInformation call for all processes (~13 ms with ~600 processes / 15k threads: the
/// kernel walks every thread). System: per-group processor counters and GetPerformanceInfo (&lt; 0.1 ms).
/// Watched processes: query handles (~15 µs per process, I/O counters being most of it).
/// Allocation-free in steady state: the native buffer is reused and image names are only materialized for new processes.
/// </summary>
public sealed unsafe class NativeSystemProbe : ISystemProbe
{
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly ushort[] _groupProcessorCounts;
    private readonly Dictionary<ProcessKey, NameEntry> _names = new();
    private byte* _buffer;
    private uint _bufferLength;
    private SystemProcessorPerformanceInformation* _coreBuffer;
    private long _generation;

    private struct NameEntry
    {
        public string Name;
        public long Generation;
    }

    public NativeSystemProbe()
    {
        var groups = Kernel32.GetActiveProcessorGroupCount();
        _groupProcessorCounts = new ushort[groups];
        var maxPerGroup = 0;
        for (ushort g = 0; g < groups; g++)
        {
            _groupProcessorCounts[g] = (ushort)Kernel32.GetActiveProcessorCount(g);
            LogicalProcessorCount += _groupProcessorCounts[g];
            maxPerGroup = Math.Max(maxPerGroup, _groupProcessorCounts[g]);
        }
        _coreBuffer = (SystemProcessorPerformanceInformation*)NativeMemory.Alloc(
            (nuint)(maxPerGroup * sizeof(SystemProcessorPerformanceInformation)));
        Grow(1 << 20);
        _poolLength = 256 * 1024;
        _poolBuffer = (byte*)NativeMemory.Alloc(_poolLength);
        _cpuPerformance = new double[LogicalProcessorCount];
        _counters = new ProcessorPerformanceCounters(LogicalProcessorCount);
        // Memory lists need this privilege (held by elevated tokens, disabled by default).
        _memoryListsAvailable = AdvApi32.EnablePrivilege("SeProfileSingleProcessPrivilege");
    }

    private readonly ProcessorPerformanceCounters _counters;
    private readonly double[] _cpuPerformance;
    private readonly bool _memoryListsAvailable;
    private byte* _poolBuffer;
    private uint _poolLength;

    public int LogicalProcessorCount { get; }

    public TimeSpan Now => Stopwatch.GetElapsedTime(_startTimestamp);

    public void CollectSystem(RawSample sample)
    {
        ObjectDisposedException.ThrowIf(_buffer == null, this);
        sample.Elapsed = Now;
        sample.WallTimeUtc = DateTime.UtcNow;
        CollectCores(sample);
        CollectMemory(sample);
        CollectKernel(sample);
    }

    private void CollectKernel(RawSample sample)
    {
        ref var k = ref sample.Kernel;
        SystemPerformanceInformation perf;
        uint returned;
        if (NtDll.NtQuerySystemInformation(NtDll.SystemPerformanceInformation, &perf, (uint)sizeof(SystemPerformanceInformation), &returned) >= 0)
        {
            k.ContextSwitches = perf.ContextSwitches;
            k.SystemCalls = perf.SystemCalls;
            k.PageFaults = perf.PageFaultCount;
            k.PageReads = perf.PageReadCount;
        }

        uint maximum, minimum, current;
        if (NtDll.NtQueryTimerResolution(&maximum, &minimum, &current) >= 0)
            k.TimerResolution = current;

        k.PerformancePercent = _counters.Collect(_cpuPerformance);
        k.NominalMhz = _counters.NominalMhz;
        for (var i = 0; i < sample.Cores.Length; i++)
            sample.Cores[i].PerformancePercent = _cpuPerformance[i];

        ref var m = ref sample.Memory;
        m.Zeroed = m.Free = m.Standby = m.Modified = -1;
        SystemMemoryListInformation lists;
        if (_memoryListsAvailable && NtDll.NtQuerySystemInformation(NtDll.SystemMemoryListInformation, &lists, (uint)sizeof(SystemMemoryListInformation), &returned) >= 0)
        {
            ulong standby = 0;
            for (var i = 0; i < 8; i++)
                standby += lists.PageCountByPriority[i];
            long page = Environment.SystemPageSize;
            m.Zeroed = (long)lists.ZeroPageCount * page;
            m.Free = (long)lists.FreePageCount * page;
            m.Standby = (long)standby * page;
            m.Modified = (long)lists.ModifiedPageCount * page;
        }
    }

    /// <summary>
    /// NtQueryObject(ObjectTypesInformation): OBJECT_TYPES_INFORMATION { ULONG NumberOfTypes; OBJECT_TYPE_INFORMATION[] }
    /// with entries pointer-aligned, each followed by its name buffer (MaximumLength). Validated against the counts used
    /// to diagnose this machine's token/job leak. Names are cached per type index.
    /// </summary>
    private void CollectObjectTypes(RawSample sample)
    {
        uint returned;
        if (NtDll.NtQueryObject(0, NtDll.ObjectTypesInformation, _objectTypesBuffer, ObjectTypesLength, &returned) < 0)
        {
            sample.ObjectTypeCount = 0;
            return;
        }
        var count = (int)*(uint*)_objectTypesBuffer;
        if (sample.ObjectTypes.Length < count)
            sample.ObjectTypes = new RawObjectType[count];
        var entry = _objectTypesBuffer + sizeof(nint);
        for (var i = 0; i < count; i++)
        {
            ref readonly var name = ref *(UnicodeString*)entry;
            ref var t = ref sample.ObjectTypes[i];
            if (i >= _objectTypeNames.Count)
                _objectTypeNames.Add(new string((char*)name.Buffer, 0, name.Length / 2));
            t.Name = _objectTypeNames[i];
            t.Objects = *(uint*)(entry + 16);
            t.Handles = *(uint*)(entry + 20);
            t.PeakObjects = *(uint*)(entry + 40);
            t.PeakHandles = *(uint*)(entry + 44);
            entry = (byte*)(((nint)(entry + ObjectTypeInformationSize + name.MaximumLength) + (sizeof(nint) - 1)) & ~(nint)(sizeof(nint) - 1));
        }
        sample.ObjectTypeCount = count;
    }

    private const uint ObjectTypesLength = 64 * 1024;
    private const int ObjectTypeInformationSize = 104; // OBJECT_TYPE_INFORMATION on x64.
    private readonly byte* _objectTypesBuffer = (byte*)NativeMemory.Alloc(ObjectTypesLength);
    private readonly List<string> _objectTypeNames = [];

    private void CollectPoolTags(RawSample sample)
    {
        uint required;
        int status;
        while ((status = NtDll.NtQuerySystemInformation(NtDll.SystemPoolTagInformation, _poolBuffer, _poolLength, &required)) == NtDll.STATUS_INFO_LENGTH_MISMATCH)
        {
            NativeMemory.Free(_poolBuffer);
            _poolLength = Math.Max(required + required / 4, _poolLength * 2);
            _poolBuffer = (byte*)NativeMemory.Alloc(_poolLength);
        }
        if (status < 0)
        {
            sample.PoolTagCount = 0;
            return;
        }

        var count = (int)*(uint*)_poolBuffer;
        if (sample.PoolTags.Length < count)
            sample.PoolTags = new RawPoolTag[count + count / 4];
        var tags = (SystemPoolTag*)(_poolBuffer + 8);
        for (var i = 0; i < count; i++)
        {
            ref readonly var t = ref tags[i];
            sample.PoolTags[i] = new RawPoolTag
            {
                Tag = t.Tag,
                PagedAllocs = t.PagedAllocs,
                PagedFrees = t.PagedFrees,
                PagedUsed = (long)t.PagedUsed,
                NonPagedAllocs = t.NonPagedAllocs,
                NonPagedFrees = t.NonPagedFrees,
                NonPagedUsed = (long)t.NonPagedUsed,
            };
        }
        sample.PoolTagCount = count;
    }

    public bool TryOpenProcess(ProcessKey key, out nint handle)
    {
        handle = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION | Kernel32.SYNCHRONIZE, false, key.Pid);
        if (handle == 0)
            return false;
        long creation, exit, kernel, user;
        if (Kernel32.GetProcessTimes(handle, &creation, &exit, &kernel, &user) && creation == key.CreateTime)
            return true;
        // Exited and PID reused between the scan and now: not the process we were asked for.
        Kernel32.CloseHandle(handle);
        handle = 0;
        return false;
    }

    public bool TryReadProcess(nint handle, ref RawProcess counters)
    {
        if (Kernel32.WaitForSingleObject(handle, 0) == Kernel32.WAIT_OBJECT_0)
            return false;

        long creation, exit, kernel, user;
        if (!Kernel32.GetProcessTimes(handle, &creation, &exit, &kernel, &user))
            return false;
        counters.KernelTime = kernel;
        counters.UserTime = user;

        ProcessMemoryCountersEx memory;
        memory.cb = (uint)sizeof(ProcessMemoryCountersEx);
        if (Kernel32.GetProcessMemoryInfo(handle, &memory, memory.cb))
        {
            counters.WorkingSet = (long)memory.WorkingSetSize;
            counters.PrivateBytes = (long)memory.PagefileUsage; // Same source as the scan's PagefileUsage.
            counters.PagedPool = (long)memory.QuotaPagedPoolUsage;
            counters.NonPagedPool = (long)memory.QuotaNonPagedPoolUsage;
            counters.PageFaults = memory.PageFaultCount;
        }

        IoCounters io;
        if (Kernel32.GetProcessIoCounters(handle, &io))
        {
            counters.ReadOps = (long)io.ReadOperationCount;
            counters.WriteOps = (long)io.WriteOperationCount;
            counters.OtherOps = (long)io.OtherOperationCount;
            counters.ReadBytes = (long)io.ReadTransferCount;
            counters.WriteBytes = (long)io.WriteTransferCount;
            counters.OtherBytes = (long)io.OtherTransferCount;
        }

        uint handles;
        if (Kernel32.GetProcessHandleCount(handle, &handles))
            counters.Handles = (int)handles;
        return true;
    }

    public void CloseProcess(nint handle) => Kernel32.CloseHandle(handle);

    public void CollectProcesses(RawSample sample)
    {
        ObjectDisposedException.ThrowIf(_buffer == null, this);
        sample.ProcessesElapsed = Now;
        CollectPoolTags(sample);
        CollectObjectTypes(sample);
        while (true)
        {
            uint required;
            var status = NtDll.NtQuerySystemInformation(NtDll.SystemProcessInformation, _buffer, _bufferLength, &required);
            if (status == NtDll.STATUS_INFO_LENGTH_MISMATCH)
            {
                // The process list can grow between calls: take headroom.
                Grow(Math.Max(required + required / 4, _bufferLength * 2));
                continue;
            }
            if (status < 0)
                throw new InvalidOperationException($"NtQuerySystemInformation(SystemProcessInformation) failed: 0x{status:X8}");
            break;
        }

        _generation++;
        var count = 0;
        var ready = 0;
        var entry = _buffer;
        while (true)
        {
            ref readonly var spi = ref *(SystemProcessInformation*)entry;
            // SYSTEM_THREAD_INFORMATION records (80 bytes) follow; ThreadState at +68, Ready = 1.
            var threads = entry + sizeof(SystemProcessInformation);
            for (var t = 0; t < spi.NumberOfThreads; t++)
            {
                if (*(uint*)(threads + t * 80 + 68) == 1)
                    ready++;
            }
            sample.EnsureProcessCapacity(count + 1);
            ref var p = ref sample.Processes[count++];
            p.Pid = (int)spi.UniqueProcessId;
            p.ParentPid = (int)spi.InheritedFromUniqueProcessId;
            p.CreateTime = spi.CreateTime;
            p.Name = ResolveName(new ProcessKey(p.Pid, p.CreateTime), in spi.ImageName);
            p.UserTime = spi.UserTime;
            p.KernelTime = spi.KernelTime;
            p.WorkingSet = (long)spi.WorkingSetSize;
            p.PrivateWorkingSet = spi.WorkingSetPrivateSize;
            p.PrivateBytes = (long)spi.PagefileUsage;
            p.VirtualSize = (long)spi.VirtualSize;
            p.PagedPool = (long)spi.QuotaPagedPoolUsage;
            p.NonPagedPool = (long)spi.QuotaNonPagedPoolUsage;
            p.ReadOps = spi.ReadOperationCount;
            p.WriteOps = spi.WriteOperationCount;
            p.OtherOps = spi.OtherOperationCount;
            p.ReadBytes = spi.ReadTransferCount;
            p.WriteBytes = spi.WriteTransferCount;
            p.OtherBytes = spi.OtherTransferCount;
            p.PageFaults = spi.PageFaultCount;
            p.HardFaults = spi.HardFaultCount;
            p.Handles = (int)spi.HandleCount;
            p.Threads = (int)spi.NumberOfThreads;
            p.SessionId = (int)spi.SessionId;
            p.BasePriority = spi.BasePriority;

            if (spi.NextEntryOffset == 0)
                break;
            entry += spi.NextEntryOffset;
        }
        sample.ProcessCount = count;
        sample.ReadyThreads = ready;

        foreach (var (key, name) in _names)
        {
            if (name.Generation != _generation)
                _names.Remove(key);
        }
    }

    private string ResolveName(ProcessKey key, in UnicodeString imageName)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_names, key, out var exists);
        if (!exists)
        {
            entry.Name = imageName.Buffer == 0
                ? key.Pid == 0 ? "Idle" : "System"
                : new string((char*)imageName.Buffer, 0, imageName.Length / sizeof(char));
        }
        entry.Generation = _generation;
        return entry.Name;
    }

    private void CollectCores(RawSample sample)
    {
        sample.EnsureCoreCount(LogicalProcessorCount);
        var offset = 0;
        for (ushort group = 0; group < _groupProcessorCounts.Length; group++)
        {
            var count = _groupProcessorCounts[group];
            var length = (uint)(count * sizeof(SystemProcessorPerformanceInformation));
            uint returned;
            var status = NtDll.NtQuerySystemInformationEx(NtDll.SystemProcessorPerformanceInformation,
                &group, sizeof(ushort), _coreBuffer, length, &returned);
            if (status < 0)
                throw new InvalidOperationException($"NtQuerySystemInformationEx(SystemProcessorPerformanceInformation) failed: 0x{status:X8}");

            for (var i = 0; i < count; i++)
            {
                ref readonly var src = ref _coreBuffer[i];
                ref var dst = ref sample.Cores[offset + i];
                dst.KernelTime = src.KernelTime;
                dst.UserTime = src.UserTime;
                dst.IdleTime = src.IdleTime;
                dst.DpcTime = src.DpcTime;
                dst.InterruptTime = src.InterruptTime;
                dst.InterruptCount = src.InterruptCount;
            }
            offset += count;
        }
    }

    private static void CollectMemory(RawSample sample)
    {
        PerformanceInformation info;
        info.cb = (uint)sizeof(PerformanceInformation);
        if (!Kernel32.GetPerformanceInfo(&info, info.cb))
            throw new InvalidOperationException($"GetPerformanceInfo failed: {Marshal.GetLastPInvokeError()}");

        var page = (long)info.PageSize;
        ref var m = ref sample.Memory;
        m.PhysicalTotal = (long)info.PhysicalTotal * page;
        m.PhysicalAvailable = (long)info.PhysicalAvailable * page;
        m.CommitTotal = (long)info.CommitTotal * page;
        m.CommitLimit = (long)info.CommitLimit * page;
        m.SystemCache = (long)info.SystemCache * page;
        m.KernelPaged = (long)info.KernelPaged * page;
        m.KernelNonPaged = (long)info.KernelNonpaged * page;
        m.Handles = (int)info.HandleCount;
        m.Processes = (int)info.ProcessCount;
        m.Threads = (int)info.ThreadCount;
    }

    private void Grow(uint length)
    {
        NativeMemory.Free(_buffer);
        _buffer = (byte*)NativeMemory.Alloc(length);
        _bufferLength = length;
    }

    public void Dispose()
    {
        NativeMemory.Free(_buffer);
        NativeMemory.Free(_coreBuffer);
        NativeMemory.Free(_poolBuffer);
        NativeMemory.Free(_objectTypesBuffer);
        _buffer = null;
        _coreBuffer = null;
        _poolBuffer = null;
        _counters.Dispose();
    }
}
