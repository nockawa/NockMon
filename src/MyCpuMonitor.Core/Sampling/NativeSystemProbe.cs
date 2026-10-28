using System.Diagnostics;
using System.Runtime.InteropServices;
using MyCpuMonitor.Core.Model;
using MyCpuMonitor.Core.Native;

namespace MyCpuMonitor.Core.Sampling;

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
    }

    public int LogicalProcessorCount { get; }

    public TimeSpan Now => Stopwatch.GetElapsedTime(_startTimestamp);

    public void CollectSystem(RawSample sample)
    {
        ObjectDisposedException.ThrowIf(_buffer == null, this);
        sample.Elapsed = Now;
        sample.WallTimeUtc = DateTime.UtcNow;
        CollectCores(sample);
        CollectMemory(sample);
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
        var entry = _buffer;
        while (true)
        {
            ref readonly var spi = ref *(SystemProcessInformation*)entry;
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
        _buffer = null;
        _coreBuffer = null;
    }
}
