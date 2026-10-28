using System.Runtime.InteropServices;
using MyCpuMonitor.Core.Model;

namespace MyCpuMonitor.Core.Sampling;

/// <summary>
/// Turns successive raw cumulative counters into per-interval rates. System counters and process scans have
/// independent baselines (they run at different rates). Allocation-free in steady state.
/// A process absent from the previous scan but present now was created during the interval: its counters started
/// at zero, so its whole counters are the delta.
/// </summary>
public sealed class SampleProcessor
{
    private readonly Dictionary<ProcessKey, Previous> _previous = new();
    private readonly Dictionary<uint, PreviousPoolTag> _previousPoolTags = new();
    private RawCore[] _previousCores = [];
    private RawKernel _previousKernel;
    private TimeSpan _previousSystemElapsed;
    private TimeSpan _previousScanElapsed;
    private bool _hasSystemBaseline;
    private bool _hasScanBaseline;
    private long _generation;

    private struct Previous
    {
        public long UserTime;
        public long KernelTime;
        public long ReadOps;
        public long WriteOps;
        public long OtherOps;
        public long ReadBytes;
        public long WriteBytes;
        public long OtherBytes;
        public uint PageFaults;
        public uint HardFaults;
        public long Generation;
    }

    private struct PreviousPoolTag
    {
        public string Name;
        public long PagedUsed;
        public long NonPagedUsed;
        public uint Allocs;
        public long Generation;
    }

    public void Reset()
    {
        _previous.Clear();
        _previousPoolTags.Clear();
        _hasSystemBaseline = false;
        _hasScanBaseline = false;
    }

    /// <summary>Computes cores and system values (except process I/O sums) of <paramref name="output"/>.</summary>
    /// <returns>false for the first sample after construction/reset (baseline only, output not written).</returns>
    public bool ProcessSystem(RawSample raw, Snapshot output)
    {
        var hadBaseline = _hasSystemBaseline && _previousCores.Length == raw.Cores.Length;
        var dt = raw.Elapsed - _previousSystemElapsed;
        if (hadBaseline && dt <= TimeSpan.Zero)
            throw new ArgumentException("Samples must be strictly increasing in time.", nameof(raw));

        if (hadBaseline)
        {
            ComputeCores(raw, output, dt.TotalSeconds);
            ComputeSystem(raw, output, dt.TotalSeconds);
            output.WallTimeUtc = raw.WallTimeUtc;
            output.Interval = dt;
            output.LogicalProcessors = raw.Cores.Length;
        }

        if (_previousCores.Length != raw.Cores.Length)
            _previousCores = new RawCore[raw.Cores.Length];
        raw.Cores.CopyTo(_previousCores, 0);
        _previousKernel = raw.Kernel;
        _previousSystemElapsed = raw.Elapsed;
        _hasSystemBaseline = true;
        return hadBaseline;
    }

    /// <summary>Computes per-process rates of the scan in <paramref name="raw"/> into <paramref name="output"/>.</summary>
    /// <returns>false for the first scan after construction/reset (baseline only, output not written).</returns>
    public bool ProcessScan(RawSample raw, ProcessTable output)
    {
        var hadBaseline = _hasScanBaseline;
        var dt = raw.ProcessesElapsed - _previousScanElapsed;
        if (hadBaseline && dt <= TimeSpan.Zero)
            throw new ArgumentException("Scans must be strictly increasing in time.", nameof(raw));

        _generation++;
        if (output.Items.Length < raw.ProcessCount)
            output.Items = new ProcessStats[Math.Max(raw.ProcessCount, output.Items.Length * 2)];

        double ticks = dt.Ticks, seconds = dt.TotalSeconds;
        double ioOps = 0, ioBytes = 0;
        for (var i = 0; i < raw.ProcessCount; i++)
        {
            ref readonly var r = ref raw.Processes[i];
            ref var prev = ref CollectionsMarshal.GetValueRefOrAddDefault(_previous, new ProcessKey(r.Pid, r.CreateTime), out _);
            // New entry: prev is all zeros, which is exactly the counters' origin for a process created during the interval.

            if (hadBaseline)
            {
                ref var s = ref output.Items[i];
                s.Pid = r.Pid;
                s.ParentPid = r.ParentPid;
                s.CreateTime = r.CreateTime;
                s.Name = r.Name;
                s.CpuUser = Delta(r.UserTime, prev.UserTime) / ticks;
                s.CpuKernel = Delta(r.KernelTime, prev.KernelTime) / ticks;
                s.Cpu = s.CpuUser + s.CpuKernel;
                s.WorkingSet = r.WorkingSet;
                s.PrivateWorkingSet = r.PrivateWorkingSet;
                s.PrivateBytes = r.PrivateBytes;
                s.VirtualSize = r.VirtualSize;
                s.PagedPool = r.PagedPool;
                s.NonPagedPool = r.NonPagedPool;
                s.IoReadOps = Delta(r.ReadOps, prev.ReadOps) / seconds;
                s.IoWriteOps = Delta(r.WriteOps, prev.WriteOps) / seconds;
                s.IoOtherOps = Delta(r.OtherOps, prev.OtherOps) / seconds;
                s.IoReadBytes = Delta(r.ReadBytes, prev.ReadBytes) / seconds;
                s.IoWriteBytes = Delta(r.WriteBytes, prev.WriteBytes) / seconds;
                s.IoOtherBytes = Delta(r.OtherBytes, prev.OtherBytes) / seconds;
                s.PageFaults = Delta(r.PageFaults, prev.PageFaults) / seconds;
                s.HardFaults = Delta(r.HardFaults, prev.HardFaults) / seconds;
                s.Handles = r.Handles;
                s.Threads = r.Threads;
                s.SessionId = r.SessionId;
                s.BasePriority = r.BasePriority;
                ioOps += s.IoOps;
                ioBytes += s.IoBytes;
            }

            prev.UserTime = r.UserTime;
            prev.KernelTime = r.KernelTime;
            prev.ReadOps = r.ReadOps;
            prev.WriteOps = r.WriteOps;
            prev.OtherOps = r.OtherOps;
            prev.ReadBytes = r.ReadBytes;
            prev.WriteBytes = r.WriteBytes;
            prev.OtherBytes = r.OtherBytes;
            prev.PageFaults = r.PageFaults;
            prev.HardFaults = r.HardFaults;
            prev.Generation = _generation;
        }

        foreach (var (key, prev) in _previous)
        {
            if (prev.Generation != _generation)
                _previous.Remove(key);
        }

        ProcessPoolTags(raw, output, hadBaseline, seconds);

        if (hadBaseline)
        {
            output.Count = raw.ProcessCount;
            output.Interval = dt;
            output.WallTimeUtc = raw.WallTimeUtc;
            output.IoOps = ioOps;
            output.IoBytes = ioBytes;
            output.ReadyThreads = raw.ReadyThreads;
        }
        _previousScanElapsed = raw.ProcessesElapsed;
        _hasScanBaseline = true;
        return hadBaseline;
    }

    /// <summary>Pool tags: sizes, growth (bytes/s) and allocation rate since the previous scan. Tag strings are cached.</summary>
    private void ProcessPoolTags(RawSample raw, ProcessTable output, bool hadBaseline, double seconds)
    {
        if (output.PoolTags.Length < raw.PoolTagCount)
            output.PoolTags = new PoolTagStats[raw.PoolTagCount + raw.PoolTagCount / 4];

        for (var i = 0; i < raw.PoolTagCount; i++)
        {
            ref readonly var t = ref raw.PoolTags[i];
            ref var prev = ref CollectionsMarshal.GetValueRefOrAddDefault(_previousPoolTags, t.Tag, out var existed);
            prev.Name ??= TagName(t.Tag);
            var allocs = unchecked(t.PagedAllocs + t.NonPagedAllocs);
            var frees = unchecked(t.PagedFrees + t.NonPagedFrees);

            ref var s = ref output.PoolTags[i];
            s.Tag = prev.Name;
            s.NonPagedBytes = t.NonPagedUsed;
            s.PagedBytes = t.PagedUsed;
            s.Outstanding = unchecked((int)(allocs - frees));
            var rates = hadBaseline && existed && seconds > 0;
            s.NonPagedGrowth = rates ? (t.NonPagedUsed - prev.NonPagedUsed) / seconds : 0;
            s.PagedGrowth = rates ? (t.PagedUsed - prev.PagedUsed) / seconds : 0;
            s.AllocationRate = rates ? unchecked(allocs - prev.Allocs) / seconds : 0;

            prev.PagedUsed = t.PagedUsed;
            prev.NonPagedUsed = t.NonPagedUsed;
            prev.Allocs = allocs;
            prev.Generation = _generation;
        }
        output.PoolTagCount = raw.PoolTagCount;

        foreach (var (tag, prev) in _previousPoolTags)
        {
            if (prev.Generation != _generation)
                _previousPoolTags.Remove(tag);
        }
    }

    /// <summary>Pool tags are 4 ASCII characters, little-endian ("File" = 0x656C6946).</summary>
    internal static string TagName(uint tag) => string.Create(4, tag, static (span, t) =>
    {
        for (var i = 0; i < 4; i++)
        {
            var c = (char)((t >> (8 * i)) & 0xFF);
            span[i] = c is >= ' ' and <= '~' ? c : '.';
        }
    });

    private void ComputeCores(RawSample raw, Snapshot output, double seconds)
    {
        var nominal = raw.Kernel.NominalMhz;
        if (output.Cores.Length != raw.Cores.Length)
            output.Cores = new CoreStats[raw.Cores.Length];

        for (var i = 0; i < raw.Cores.Length; i++)
        {
            ref readonly var c = ref raw.Cores[i];
            ref readonly var p = ref _previousCores[i];
            var idle = Delta(c.IdleTime, p.IdleTime);
            var kernel = Delta(c.KernelTime, p.KernelTime);
            var user = Delta(c.UserTime, p.UserTime);
            var total = kernel + user;
            ref var s = ref output.Cores[i];
            s.FrequencyMhz = Frequency(nominal, c.PerformancePercent);
            if (total <= 0)
            {
                s = default;
                s.FrequencyMhz = Frequency(nominal, c.PerformancePercent);
                continue;
            }
            s.Usage = Clamp01((total - idle) / total);
            s.User = Clamp01(user / total);
            s.Kernel = Clamp01((kernel - idle) / total);
            s.Dpc = Clamp01(Delta(c.DpcTime, p.DpcTime) / total);
            s.Interrupt = Clamp01(Delta(c.InterruptTime, p.InterruptTime) / total);
            s.InterruptRate = Delta(c.InterruptCount, p.InterruptCount) / seconds;
        }
    }

    /// <summary>Effective MHz = nominal × % processor performance; 0 when PDH is unavailable.</summary>
    private static double Frequency(double nominalMhz, double performancePercent) =>
        double.IsFinite(nominalMhz) && double.IsFinite(performancePercent) ? nominalMhz * performancePercent / 100 : 0;

    private void ComputeSystem(RawSample raw, Snapshot output, double seconds)
    {
        ref var s = ref output.System;
        s.Cpu = s.CpuUser = s.CpuKernel = s.CpuDpc = s.CpuInterrupt = s.Interrupts = 0;
        foreach (ref readonly var c in output.Cores.AsSpan())
        {
            s.Cpu += c.Usage;
            s.CpuUser += c.User;
            s.CpuKernel += c.Kernel;
            s.CpuDpc += c.Dpc;
            s.CpuInterrupt += c.Interrupt;
            s.Interrupts += c.InterruptRate;
        }

        ref readonly var k = ref raw.Kernel;
        ref readonly var pk = ref _previousKernel;
        s.ContextSwitches = Delta(k.ContextSwitches, pk.ContextSwitches) / seconds;
        s.SystemCalls = Delta(k.SystemCalls, pk.SystemCalls) / seconds;
        s.PageFaults = Delta(k.PageFaults, pk.PageFaults) / seconds;
        s.PageReads = Delta(k.PageReads, pk.PageReads) / seconds;
        s.TimerResolutionMs = k.TimerResolution / 10_000.0;
        s.FrequencyMhz = Frequency(k.NominalMhz, k.PerformancePercent);
        ref readonly var m = ref raw.Memory;
        s.PhysicalTotal = m.PhysicalTotal;
        s.PhysicalAvailable = m.PhysicalAvailable;
        s.CommitTotal = m.CommitTotal;
        s.CommitLimit = m.CommitLimit;
        s.SystemCache = m.SystemCache;
        s.KernelPaged = m.KernelPaged;
        s.KernelNonPaged = m.KernelNonPaged;
        s.Processes = m.Processes;
        s.Threads = m.Threads;
        s.Handles = m.Handles;
        s.MemoryZeroed = Math.Max(0, m.Zeroed);
        s.MemoryFree = Math.Max(0, m.Free);
        s.MemoryStandby = Math.Max(0, m.Standby);
        s.MemoryModified = Math.Max(0, m.Modified);
    }

    /// <summary>Cumulative counters never decrease for a given process; clamp defensively.</summary>
    internal static double Delta(long current, long previous) => current >= previous ? current - previous : 0;

    internal static double Delta(uint current, uint previous) => unchecked(current - previous);

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
