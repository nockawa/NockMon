using NockMon.Core.Model;

namespace NockMon.Core.Sampling;

/// <summary>Reads raw cumulative system counters. Called from the sampler thread only.</summary>
public interface ISystemProbe : IDisposable
{
    int LogicalProcessorCount { get; }

    /// <summary>Monotonic clock shared by all reads (same origin as <see cref="RawSample.Elapsed"/>).</summary>
    TimeSpan Now { get; }

    /// <summary>Cheap system-wide counters: sets <see cref="RawSample.Elapsed"/>, wall time, cores and memory.</summary>
    void CollectSystem(RawSample sample);

    /// <summary>Full process scan (expensive): sets <see cref="RawSample.ProcessesElapsed"/> and the process list.</summary>
    void CollectProcesses(RawSample sample);

    /// <summary>Opens a query handle on a process, verifying its identity (PIDs are reused).</summary>
    bool TryOpenProcess(ProcessKey key, out nint handle);

    /// <summary>
    /// Reads the per-process counters available through a handle: CPU times, memory, I/O, page faults, handle count.
    /// Other fields of <paramref name="counters"/> are left untouched.
    /// </summary>
    /// <returns>false once the process has exited.</returns>
    bool TryReadProcess(nint handle, ref RawProcess counters);

    void CloseProcess(nint handle);
}
