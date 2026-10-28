using System.Runtime.InteropServices;
using MyCpuMonitor.Core.Model;

namespace MyCpuMonitor.Core.Sampling;

/// <summary>
/// Turns successive raw cumulative counters into per-interval rates. Allocation-free in steady state.
/// A process absent from the previous sample but present now was created during the interval: its
/// counters started at zero, so its whole counters are the delta.
/// </summary>
public sealed class SampleProcessor
{
    private readonly Dictionary<ProcessKey, Previous> _previous = new();
    private RawCore[] _previousCores = [];
    private TimeSpan _previousElapsed;
    private bool _hasPrevious;
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

    public void Reset()
    {
        _previous.Clear();
        _hasPrevious = false;
    }

    /// <summary>Computes <paramref name="output"/> from <paramref name="raw"/>.</summary>
    /// <returns>false for the first sample after construction/reset (baseline only, output not written).</returns>
    public bool Process(RawSample raw, Snapshot output)
    {
        var hadPrevious = _hasPrevious;
        var dt = raw.Elapsed - _previousElapsed;
        if (hadPrevious && dt <= TimeSpan.Zero)
            throw new ArgumentException("Samples must be strictly increasing in time.", nameof(raw));

        _generation++;
        if (output.Processes.Length < raw.ProcessCount)
            output.Processes = new ProcessStats[Math.Max(raw.ProcessCount, output.Processes.Length * 2)];

        double ticks = dt.Ticks, seconds = dt.TotalSeconds;
        double ioOps = 0, ioBytes = 0;
        for (var i = 0; i < raw.ProcessCount; i++)
        {
            ref readonly var r = ref raw.Processes[i];
            ref var prev = ref CollectionsMarshal.GetValueRefOrAddDefault(_previous, new ProcessKey(r.Pid, r.CreateTime), out _);
            // New entry: prev is all zeros, which is exactly the counters' origin for a process created during the interval.

            if (hadPrevious)
            {
                ref var s = ref output.Processes[i];
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

        if (hadPrevious && _previousCores.Length == raw.Cores.Length)
        {
            output.ProcessCount = raw.ProcessCount;
            ComputeCores(raw, output, seconds);
            ComputeSystem(raw, output, ioOps, ioBytes);
            output.WallTimeUtc = raw.WallTimeUtc;
            output.Interval = dt;
            output.LogicalProcessors = raw.Cores.Length;
        }
        else
        {
            hadPrevious = false;
        }

        if (_previousCores.Length != raw.Cores.Length)
            _previousCores = new RawCore[raw.Cores.Length];
        raw.Cores.CopyTo(_previousCores, 0);
        _previousElapsed = raw.Elapsed;
        _hasPrevious = true;
        return hadPrevious;
    }

    private void ComputeCores(RawSample raw, Snapshot output, double seconds)
    {
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
            if (total <= 0)
            {
                s = default;
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

    private static void ComputeSystem(RawSample raw, Snapshot output, double ioOps, double ioBytes)
    {
        ref var s = ref output.System;
        s = default;
        foreach (ref readonly var c in output.Cores.AsSpan())
        {
            s.Cpu += c.Usage;
            s.CpuUser += c.User;
            s.CpuKernel += c.Kernel;
            s.CpuDpc += c.Dpc;
            s.CpuInterrupt += c.Interrupt;
        }
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
        s.IoOps = ioOps;
        s.IoBytes = ioBytes;
    }

    /// <summary>Cumulative counters never decrease for a given process; clamp defensively.</summary>
    private static double Delta(long current, long previous) => current >= previous ? current - previous : 0;

    private static double Delta(uint current, uint previous) => unchecked(current - previous);

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
