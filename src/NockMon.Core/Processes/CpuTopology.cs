using System.Runtime.InteropServices;

namespace NockMon.Core.Processes;

/// <param name="EfficiencyClass">Higher is faster: on a hybrid CPU, P-cores have a higher class than E-cores.</param>
public readonly record struct LogicalProcessor(int Group, int Index, int Core, int EfficiencyClass);

/// <summary>Logical processors with their core and efficiency class (CPU sets), for affinity choices.</summary>
public static unsafe partial class CpuTopology
{
    private static readonly Lazy<LogicalProcessor[]> Processors = new(Read);

    /// <summary>All logical processors, by group then index.</summary>
    public static IReadOnlyList<LogicalProcessor> All => Processors.Value;

    /// <summary>One processor group: affinity masks cover every processor (more than 64 logical processors: several groups).</summary>
    public static bool SingleGroup => Processors.Value.All(p => p.Group == 0);

    /// <summary>P-cores and E-cores: more than one efficiency class.</summary>
    public static bool IsHybrid => Processors.Value.Select(p => p.EfficiencyClass).Distinct().Count() > 1;

    /// <summary>Affinity mask (group 0) of the processors matching <paramref name="predicate"/>.</summary>
    public static ulong Mask(Func<LogicalProcessor, bool> predicate) =>
        Processors.Value.Where(p => p.Group == 0 && p.Index < 64 && predicate(p)).Aggregate(0UL, (mask, p) => mask | 1UL << p.Index);

    public static ulong AllMask => Mask(_ => true);

    /// <summary>The fastest class (P-cores on a hybrid CPU).</summary>
    public static int PerformanceClass => Processors.Value.Max(p => p.EfficiencyClass);

    private static LogicalProcessor[] Read()
    {
        uint size = 0;
        GetSystemCpuSetInformation(null, 0, &size, 0, 0);
        var buffer = new byte[size];
        var result = new List<LogicalProcessor>();
        fixed (byte* data = buffer)
        {
            if (size == 0 || !GetSystemCpuSetInformation(data, size, &size, 0, 0))
                return Fallback();
            // SYSTEM_CPU_SET_INFORMATION: Size, Type, then the CpuSet member (Id, Group, LogicalProcessorIndex,
            // CoreIndex, LastLevelCacheIndex, NumaNodeIndex, EfficiencyClass…). Entries are walked by their Size.
            for (uint offset = 0; offset + 20 <= size;)
            {
                var entry = data + offset;
                var entrySize = *(uint*)entry;
                if (entrySize == 0)
                    break;
                if (*(uint*)(entry + 4) == 0 /* CpuSetInformation */)
                    result.Add(new LogicalProcessor(*(ushort*)(entry + 12), entry[14], entry[15], entry[18]));
                offset += entrySize;
            }
        }
        return result.Count > 0 ? [.. result.OrderBy(p => p.Group).ThenBy(p => p.Index)] : Fallback();
    }

    private static LogicalProcessor[] Fallback() =>
        Enumerable.Range(0, Environment.ProcessorCount).Select(i => new LogicalProcessor(i / 64, i % 64, i, 0)).ToArray();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(void* information, uint bufferLength, uint* returnedLength, nint process, uint flags);
}
