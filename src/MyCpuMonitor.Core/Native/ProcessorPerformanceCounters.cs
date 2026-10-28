using System.Runtime.InteropServices;

namespace MyCpuMonitor.Core.Native;

/// <summary>
/// Effective processor performance (% of nominal frequency, above 100 with turbo) per logical CPU, from
/// SystemProcessorPerformanceDistribution: per-CPU hit counts per performance state; the hit-weighted average of the
/// states' PercentFrequency over an interval. On hardware-managed P-states (HWP) the kernel reports two states (0 and
/// 255) and the same formula still holds: validated against PDH "% Processor Performance" (idle within 2%, full load
/// exact) at ~0.2 ms per query, where PDH costs ~30 ms. Nominal MHz from CallNtPowerInformation, read once.
/// </summary>
internal sealed unsafe partial class ProcessorPerformanceCounters : IDisposable
{
    private const int SystemProcessorPerformanceDistribution = 100;
    private const int ProcessorInformation = 11;

    private byte* _current;
    private byte* _previous;
    private uint _length = 64 * 1024;
    private bool _hasPrevious;
    private double[] _last = [];

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    public ProcessorPerformanceCounters(int logicalProcessors)
    {
        _current = (byte*)NativeMemory.Alloc(_length);
        _previous = (byte*)NativeMemory.Alloc(_length);
        NominalMhz = ReadNominalMhz(logicalProcessors);
    }

    public double NominalMhz { get; }

    /// <summary>
    /// Fills <paramref name="perCpu"/> with % performance since the previous call; returns the average. A CPU that stayed
    /// idle (no hits: idle CPUs do not tick) keeps its last known value; NaN until it has one.
    /// </summary>
    public double Collect(Span<double> perCpu)
    {
        if (_last.Length != perCpu.Length)
        {
            _last = new double[perCpu.Length];
            _last.AsSpan().Fill(double.NaN);
        }
        _last.CopyTo(perCpu);
        uint returned;
        if (NtDll.NtQuerySystemInformation(SystemProcessorPerformanceDistribution, _current, _length, &returned) < 0)
            return double.NaN;

        if (_hasPrevious)
        {
            var count = (int)Math.Min(*(uint*)_current, *(uint*)_previous);
            for (var i = 0; i < count; i++)
            {
                var current = _current + ((uint*)(_current + 4))[i];
                var previous = _previous + ((uint*)(_previous + 4))[i];
                var number = *(uint*)current;
                var states = Math.Min(*(uint*)(current + 4), *(uint*)(previous + 4));
                double hits = 0, weighted = 0;
                for (var s = 0; s < states; s++)
                {
                    // SYSTEM_PROCESSOR_PERFORMANCE_HITCOUNT: ULONGLONG Hits; UCHAR PercentFrequency (16 bytes).
                    var delta = (double)(*(ulong*)(current + 8 + s * 16) - *(ulong*)(previous + 8 + s * 16));
                    hits += delta;
                    weighted += delta * *(current + 8 + s * 16 + 8);
                }
                if (hits <= 0 || number >= perCpu.Length)
                    continue;
                perCpu[(int)number] = _last[(int)number] = weighted / hits;
            }
        }

        var swap = _current; // Pointers cannot be tuple elements.
        _current = _previous;
        _previous = swap;
        _hasPrevious = true;

        double sum = 0;
        var valid = 0;
        foreach (var v in perCpu)
        {
            if (double.IsFinite(v))
            {
                sum += v;
                valid++;
            }
        }
        return valid > 0 ? sum / valid : double.NaN;
    }

    private static double ReadNominalMhz(int logicalProcessors)
    {
        var buffer = new ProcessorPowerInformation[Math.Max(1, logicalProcessors)];
        fixed (ProcessorPowerInformation* p = buffer)
        {
            if (CallNtPowerInformation(ProcessorInformation, null, 0, p, (uint)(buffer.Length * sizeof(ProcessorPowerInformation))) != 0)
                return double.NaN;
        }
        return buffer[0].MaxMhz;
    }

    public void Dispose()
    {
        NativeMemory.Free(_current);
        NativeMemory.Free(_previous);
        _current = _previous = null;
    }

    [LibraryImport("powrprof.dll")]
    private static partial int CallNtPowerInformation(int level, void* input, uint inputLength, void* output, uint outputLength);
}
