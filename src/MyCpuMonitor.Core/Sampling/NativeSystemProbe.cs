using System.Diagnostics;
using System.Runtime.InteropServices;
using MyCpuMonitor.Core.Model;
using MyCpuMonitor.Core.Native;

namespace MyCpuMonitor.Core.Sampling;

/// <summary>
/// One NtQuerySystemInformation call for all processes (no handles opened), per-group processor
/// performance counters, and GetPerformanceInfo. Allocation-free in steady state: the native buffer is
/// reused and image names are only materialized for processes not seen before.
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

    public void Collect(RawSample sample)
    {
        ObjectDisposedException.ThrowIf(_buffer == null, this);
        sample.Elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
        sample.WallTimeUtc = DateTime.UtcNow;
        CollectProcesses(sample);
        CollectCores(sample);
        CollectMemory(sample);
    }

    private void CollectProcesses(RawSample sample)
    {
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
