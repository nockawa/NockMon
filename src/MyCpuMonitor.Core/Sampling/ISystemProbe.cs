namespace MyCpuMonitor.Core.Sampling;

/// <summary>Reads raw cumulative system counters. Called from the sampler thread only.</summary>
public interface ISystemProbe : IDisposable
{
    int LogicalProcessorCount { get; }

    void Collect(RawSample sample);
}
