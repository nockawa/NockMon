using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;

namespace MyCpuMonitor.Core.Help;

public static partial class Docs
{
    private const string PatternSyntax =
        "Name patterns: `*` and `?` wildcards, `;` between alternatives, case-insensitive; a name without wildcard also " +
        "matches with `.exe` (`devenv; chrome*`).";

    public static Doc Of(WidgetKind kind)
    {
        var title = WidgetCatalog.DisplayName(kind);
        return kind switch
        {
            WidgetKind.ProcessTable => D(title,
                "Top processes sorted by any metric. Click a column header to sort, again to reverse.",
                $"""
                Lists the top N processes of the latest process scan, sorted by the chosen metric. Every process metric can be a column: hover a header for its meaning.

                - **Group by name** sums processes sharing an image name into one row (browsers, `svchost.exe`): the Instances column counts them.
                - **Exclude Idle** hides the System Idle Process, which otherwise tops a CPU sort on a quiet machine.
                - **Filter** keeps matching processes only. {PatternSyntax}

                Refreshed at the process scan rate (2 s by default, toolbar setting).
                """),
            WidgetKind.Chart => D(title,
                "Time series of one or more metrics, from the history.",
                $"""
                Each series is a metric of a source: the **system**, one **logical CPU**, the **processes** matching a name pattern (summed), a **kernel object** type, or a **GPU**. All series of a chart must share a unit.

                - **Stacked** draws series on top of each other: for parts of a whole (user + kernel CPU, several processes).
                - History is kept for an hour by default (settings); the chart shows the chosen window of it.
                - Process series are sampled at the fast rate: the app keeps a handle on every matching process. {PatternSyntax}
                - Hover the metric picker's entries in the editor for what each metric means.
                """),
            WidgetKind.Value => D(title,
                "One metric's latest value, large, over a sparkline.",
                "Any metric a chart can show. The sparkline covers the chosen window. Untitled values show the metric name on hover."),
            WidgetKind.CpuGrid => D(title,
                "One small graph per logical processor.",
                """
                Shows how load spreads over processors: a single pegged processor (single-threaded bottleneck, interrupt-bound device) is invisible in the total.

                - Metric: usage, user, kernel, DPC, interrupt time, interrupt rate or effective frequency.
                - With usage, **Shade kernel time** draws the kernel part darker.
                - With SMT (Hyper-Threading), two logical processors share one physical core: they are numbered in pairs on most machines.
                """),
            WidgetKind.PoolTags => D(title,
                "Kernel pool usage per allocation tag: the tool to find a leaking driver.",
                """
                Drivers tag their kernel memory allocations with four characters. This table shows, per tag, the paged and non-paged bytes in use, their growth, allocation rate and live allocations.

                How to hunt a leak: kernel non-paged (or paged) pool grows for hours → sort by **Non-paged** (or growth) → a tag stands out and keeps growing → find the driver owning the tag (hover the Tag header).

                Refreshed at the process scan rate.
                """),
            WidgetKind.ValueList => D(title,
                "Label / value rows for a list of metrics, compact.",
                "Any metrics a chart can show, one per row, latest value. \"Reset to kernel overview\" restores the default set: scheduler, interrupts, memory lists, pools. Hover a label for its meaning."),
            WidgetKind.Drivers => D(title,
                "DPC and interrupt activity per driver: which driver burns CPU or causes latency spikes.",
                """
                Built from kernel ETW events with their duration, per driver owning the routine. Time columns are ms per second (1000 = one processor busy).

                - **Audio crackles, stutter, mouse lag:** sort by **Max DPC** (or Max ISR). Over about 1 ms is the culprit.
                - **High CPU (DPC) / (interrupt) with no process to blame:** sort by DPC or ISR time.

                Needs administrator rights (kernel ETW session). Refreshed at the process scan rate.
                """),
            WidgetKind.ProcessEvents => D(title,
                "Recent process starts and exits, newest first, with command lines and exit codes.",
                $"""
                From kernel ETW process events: catches short-lived processes that no scan would ever see (scripts, updaters, crash handlers, processes spawned in a loop).

                - **Exit codes** in hex: `0x0` success; `0xC0000005` access violation; `0xC0000409` stack buffer overrun / fail-fast; `0xC00000FD` stack overflow; `0xE0434352` unhandled .NET exception; `0xC000013A` Ctrl+C / console closed; `0x40010004` terminated by a debugger.
                - The last 500 events are kept. {PatternSyntax}

                Needs administrator rights (kernel ETW session).
                """),
            WidgetKind.ObjectTypes => D(title,
                "Live kernel objects and handles per object type, with growth: kernel object leaks.",
                """
                Every kernel resource (process, thread, token, file, event, registry key…) is an object. This table shows the totals per type.

                - **Objects far above handles**: references held by the kernel or drivers, not by processes.
                - **Process / Thread objects above the running counts**: exited processes kept alive by leaked handles.
                - Sustained positive growth is a leak: chart the type's objects (Kernel object source) to confirm the trend.

                Refreshed at the process scan rate.
                """),
            WidgetKind.GpuEngines => D(title,
                "One small graph per engine of a GPU (3D, Compute, Copy, Video…), 0–100 % busy.",
                $"""
                Engine running time from the graphics kernel, like Task Manager, for any vendor. Shows what a GPU actually does: rendering (3D), CUDA/DirectML (Compute), transfers (Copy), video playback (Decode), streaming (Encode).

                - Engines are filtered by a name pattern; each shown engine costs about 0.3 ms per sample to query.
                - Engine names arrive a moment after start ("Engine N" until then).
                """),
            WidgetKind.Stack => D(title,
                "Several widgets sharing one area, one shown at a time.",
                """
                Navigate with the edge arrows, the dots, the mouse wheel over the dots or arrows, or **Ctrl + wheel** anywhere. Optional auto-cycle (paused while the pointer is over the stack).

                In edit mode: drop a widget onto another to stack it; "Unstack" moves the shown widget out; configure to reorder, remove, or edit items.
                """),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>Documentation of a series' metric; for a process series, the metric's (the pattern is the user's).</summary>
    public static Doc Of(SeriesRef series) => series switch
    {
        SystemSeries s => Of(s.Metric),
        CoreSeries s => Of(s.Metric),
        ProcessSeries s => Of(s.Field),
        ObjectTypeSeries s => Of(s.Metric),
        GpuSeries s => Of(s.Metric),
        _ => throw new ArgumentException($"Unknown series {series.GetType().Name}.", nameof(series)),
    };
}
