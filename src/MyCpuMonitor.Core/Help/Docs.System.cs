using MyCpuMonitor.Core.Metrics;

namespace MyCpuMonitor.Core.Help;

public static partial class Docs
{
    private const string SampleRate = "**Rate:** sample interval (0.5 s by default).";

    private const string EtwSystemNote =
        "**Source:** kernel ETW events (private real-time session, needs administrator rights), all processes.";

    private const string MemoryListsNote =
        "**Source:** `NtQuerySystemInformation(SystemMemoryListInformation)`, which needs the profiling privilege of an " +
        "elevated process (0 otherwise).";

    public static Doc Of(SystemMetric metric)
    {
        var title = MetricCatalog.Get(metric).DisplayName;
        return metric switch
        {
            SystemMetric.Cpu => D(title,
                "Total CPU usage: all logical processors, everything that is not idle.",
                $"""
                Sum over logical processors of their busy share (1 − idle), from the per-processor counters (`SystemProcessorPerformanceInformation`). {CpuScaleNote}

                Includes DPC and interrupt time, which processes are not charged for: the total can exceed the sum of processes.

                {SampleRate}
                """),
            SystemMetric.CpuUser => D(title,
                "CPU time running application code (user mode), all processors.",
                $"{CpuScaleNote}\n\n{SampleRate}"),
            SystemMetric.CpuKernel => D(title,
                "CPU time in the kernel, all processors, DPC and interrupt time included (idle excluded).",
                $"""
                System calls, drivers, file system and network stacks, memory management, plus DPCs and interrupts. {CpuScaleNote}

                A high kernel share with low process kernel time points at DPCs/interrupts: see the DPC and interrupt metrics and the Drivers widget.

                {SampleRate}
                """),
            SystemMetric.CpuDpc => D(title,
                "CPU time spent in deferred procedure calls: driver work queued by interrupts.",
                $"""
                Drivers do the bulk of interrupt handling in DPCs (network packets, storage completions, GPU, USB, timers). DPCs run above every thread: while one runs, nothing else can use that processor.

                - Normally well under 1–2 % of one processor. More points at a driver: the **Drivers** widget names it.
                - Long single DPCs cause audio dropouts and stutter even at low total time: see **Longest DPC**.

                {CpuScaleNote}

                {SampleRate}
                """),
            SystemMetric.CpuInterrupt => D(title,
                "CPU time spent in interrupt service routines (hardware interrupts).",
                $"""
                Time in ISRs, which should only acknowledge the device and queue a DPC. Normally tiny; significant values point at a misbehaving device or driver (interrupt storm): see the **Drivers** widget.

                {CpuScaleNote}

                {SampleRate}
                """),
            SystemMetric.PhysicalUsed => D(title,
                "RAM in use: total minus available (standby cache excluded, modified pages included).",
                """
                Physical memory that cannot be handed out without first writing it or taking it from a working set: process working sets, kernel memory, modified pages.

                The file cache (standby list) is **not** counted here: Windows deliberately fills free RAM with it.

                **Source:** `GetPerformanceInfo`.
                """ + "\n\n" + SampleRate),
            SystemMetric.PhysicalAvailable => D(title,
                "RAM that can be given to processes immediately: free + zeroed + standby (cache).",
                """
                Standby pages hold cached data but are reused instantly when needed, so they count as available.

                - Below a few hundred MB (or ~5 %), Windows trims working sets aggressively and hard faults rise.

                **Source:** `GetPerformanceInfo`.
                """ + "\n\n" + SampleRate),
            SystemMetric.CommitTotal => D(title,
                "Commit charge: memory promised to processes and the kernel, which RAM + page file must back.",
                """
                Sum of private committed memory of all processes (private bytes), paged pool, and other kernel commitments. Committed memory need not be in RAM: it has a guaranteed place in RAM or the page file.

                - When it reaches the **commit limit**, allocations fail ("out of memory") even if RAM looks free.
                - Steady growth over hours is a leak somewhere: sort a process table by private bytes.

                **Source:** `GetPerformanceInfo`.
                """ + "\n\n" + SampleRate),
            SystemMetric.CommitLimit => D(title,
                "Maximum commit charge: RAM plus the current page file sizes.",
                """
                Grows when a system-managed page file expands (up to its maximum). Without a page file, it is about the RAM size, and the commit charge, not RAM usage, becomes the limit.

                **Source:** `GetPerformanceInfo`.
                """ + "\n\n" + SampleRate),
            SystemMetric.SystemCache => D(title,
                "System cache: standby list plus the system working set.",
                """
                As defined by `GetPerformanceInfo`: the standby list (cached file data and trimmed pages) plus the system working set (cache manager views, paged pool and pageable kernel code in RAM).

                A large value is healthy: it is RAM put to use and reclaimed on demand.
                """ + "\n\n" + SampleRate),
            SystemMetric.KernelPaged => D(title,
                "Paged pool: kernel memory that can be paged out.",
                """
                Used by the kernel and drivers for pageable data: registry, object names, security descriptors, file system structures, section objects.

                Growth over hours is a leak: the **Kernel pool tags** widget finds the tag, hence the driver.

                **Source:** `GetPerformanceInfo`.
                """ + "\n\n" + SampleRate),
            SystemMetric.KernelNonPaged => D(title,
                "Non-paged pool: kernel memory that always stays in RAM.",
                """
                Used for data accessed at high IRQL or by devices: network buffers, I/O requests, processes, threads, synchronization objects.

                - Typically a few hundred MB. Continuous growth is a driver or object leak: the **Kernel pool tags** widget finds the tag, the **Kernel objects** widget the object type.
                - It is RAM taken away from everything else, permanently.

                **Source:** `GetPerformanceInfo`.
                """ + "\n\n" + SampleRate),
            SystemMetric.Processes => D(title,
                "Number of processes running.",
                "From `GetPerformanceInfo`. Exited processes still referenced by handles are not counted here but remain as kernel objects (see the Kernel objects widget: Process objects).\n\n" + SampleRate),
            SystemMetric.Threads => D(title,
                "Number of threads, all processes (kernel threads included).",
                "From `GetPerformanceInfo`.\n\n" + SampleRate),
            SystemMetric.Handles => D(title,
                "Number of open handles, all processes.",
                "From `GetPerformanceInfo`. Continuous growth is a handle leak: sort a process table by handles to find the process.\n\n" + SampleRate),
            SystemMetric.IoOps => D(title,
                "I/O requests per second of all processes: files (cached included), network, pipes, devices.",
                "Sum of the processes' I/O operations (see the process metric). Not disk activity: use Disk IOPS.\n\n" + ScanOnly),
            SystemMetric.IoBytes => D(title,
                "Bytes per second moved by all processes' I/O requests.",
                "Sum of the processes' I/O throughput (see the process metric). Not disk nor network throughput.\n\n" + ScanOnly),
            SystemMetric.ContextSwitches => D(title,
                "Thread switches per second, all processors.",
                """
                Each time a processor switches from one thread to another (a thread blocks, is preempted, or its quantum ends).

                - An idle desktop does a few thousand to tens of thousands per second.
                - Hundreds of thousands per second with moderate CPU means threads ping-ponging: lock contention, producer/consumer handoffs, high-frequency timers or polling. Each switch costs microseconds and cache warmth.

                **Source:** `NtQuerySystemInformation(SystemPerformanceInformation)`.
                """ + "\n\n" + SampleRate),
            SystemMetric.SystemCalls => D(title,
                "Calls into the kernel per second, all processes.",
                """
                Transitions from user mode into kernel services (file and registry calls, waits, memory operations…).

                Very high rates with high kernel CPU point at an application calling the kernel in a tight loop (polling, tiny reads or writes).

                **Source:** `NtQuerySystemInformation(SystemPerformanceInformation)`.
                """ + "\n\n" + SampleRate),
            SystemMetric.Interrupts => D(title,
                "Hardware interrupts per second, all processors (clock interrupts included).",
                """
                Sum of the per-processor interrupt counts. Clock interrupts depend on the timer resolution: at 1 ms instead of 15.6 ms, each processor takes up to 1000 per second instead of 64.

                A sudden rise without matching activity is an interrupt storm: the **Drivers** widget shows which ISR runs.
                """ + "\n\n" + SampleRate),
            SystemMetric.PageFaults => D(title,
                "Page faults per second, all processes (soft and hard).",
                "Most are soft faults, resolved in memory in microseconds (see the process metric). Hard fault page reads shows the costly ones.\n\n**Source:** `NtQuerySystemInformation(SystemPerformanceInformation)`.\n\n" + SampleRate),
            SystemMetric.PageReads => D(title,
                "Disk read operations per second issued to resolve hard page faults.",
                """
                Reads from the page file or from mapped files (including executables) to bring pages back into memory. One read can bring several pages (clustering).

                Sustained values with low available memory is thrashing. Bursts at application start are image loading.

                **Source:** `NtQuerySystemInformation(SystemPerformanceInformation)`.
                """ + "\n\n" + SampleRate),
            SystemMetric.MemoryZeroed => D(title,
                "Zeroed page list: free pages already filled with zeros, ready for new allocations.",
                """
                New private memory must be handed out zeroed (security). The zero page thread zeroes free pages in the background; when this list is empty, pages are zeroed on demand at fault time.
                """ + "\n\n" + MemoryListsNote + "\n\n" + SampleRate),
            SystemMetric.MemoryFree => D(title,
                "Free page list: released pages not zeroed yet.",
                """
                Pages released by exiting processes or freed memory, waiting for the zero page thread. Usually small: free + zeroed is RAM nobody uses, which Windows prefers to fill with cache (standby).
                """ + "\n\n" + MemoryListsNote + "\n\n" + SampleRate),
            SystemMetric.MemoryStandby => D(title,
                "Standby list: cached pages (file data, trimmed process pages), reusable instantly.",
                """
                Clean pages that still hold valid data: file cache, prefetched (SuperFetch/SysMain) data, pages trimmed from working sets. A process faulting on one gets it back without I/O (soft fault); any allocation can take one without I/O.

                Large standby is good: it is RAM working as cache. It shrinking toward zero under load means memory pressure.
                """ + "\n\n" + MemoryListsNote + "\n\n" + SampleRate),
            SystemMetric.MemoryModified => D(title,
                "Modified list: dirty pages that must be written to disk before reuse.",
                """
                Pages removed from working sets that hold data not yet on disk: to the page file (private data) or to their file (mapped files). The modified page writer flushes them in the background, then they move to standby.

                A large and growing modified list means writes cannot keep up, or there is no page file to write private pages to: that memory is then stuck.
                """ + "\n\n" + MemoryListsNote + "\n\n" + SampleRate),
            SystemMetric.TimerResolution => D(title,
                "Current global timer interrupt interval (default 15.625 ms).",
                """
                Applications lower it with `timeBeginPeriod` or `NtSetTimerResolution` (browsers playing media, games, audio and communication apps) for precise sleeps and timers. The lowest request wins system-wide.

                - At 1 ms (or 0.5 ms) every processor wakes up 16 to 31 times more often: more interrupts, less deep sleep, more power.
                - Since Windows 11, requests of minimized or occluded windows can be ignored.
                - To find who requested it: `powercfg /energy` (report section "Platform Timer Resolution").

                **Source:** `NtQueryTimerResolution`.
                """ + "\n\n" + SampleRate),
            SystemMetric.CpuFrequency => D(title,
                "Effective processor frequency, averaged over logical processors.",
                """
                Nominal frequency × the processor performance actually delivered, measured from the kernel's per-processor performance-state statistics (`SystemProcessorPerformanceDistribution`, validated against the "% Processor Performance" counter).

                - Above nominal: turbo boost. Well below under load: thermal or power throttling, or a power plan capping it.
                - A processor that stayed idle keeps its last value (idle processors report no performance samples).
                """ + "\n\n" + SampleRate),
            SystemMetric.ProcessorQueueLength => D(title,
                "Threads ready to run but waiting for a processor, at the scan instant.",
                """
                Count of threads in the Ready state in the process list. A snapshot, not an average: it jumps around.

                - Sustained above about **2 per logical processor**, the CPU is saturated: threads wait for their turn, latency rises.
                - Some ready threads are normal even at moderate load (wake-ups are bursty).

                **Rate:** process scan.
                """),
            SystemMetric.DiskRead => D(title,
                "Physical disk read throughput, all disks.",
                "Reads reaching the disk drivers (file cache hits excluded).\n\n" + EtwSystemNote + "\n\n" + SampleRate),
            SystemMetric.DiskWrite => D(title,
                "Physical disk write throughput, all disks.",
                "Writes reaching the disk drivers, lazy writer and page file writes included.\n\n" + EtwSystemNote + "\n\n" + SampleRate),
            SystemMetric.DiskOps => D(title,
                "Physical disk requests completed per second (IOPS), all disks.",
                "A hard drive tops out around 100–200 random IOPS; SSDs at tens or hundreds of thousands.\n\n" + EtwSystemNote + "\n\n" + SampleRate),
            SystemMetric.DiskLatency => D(title,
                "Average disk response time over the requests completed in the interval, all disks.",
                """
                Issue-to-completion time at the disk driver, queueing in the storage stack included.

                - NVMe SSD well under 1 ms, SATA SSD about 1 ms, hard drive 5–15 ms.
                - Tens of milliseconds: saturated or failing device, or deep queues.
                """ + "\n\n" + EtwSystemNote + "\n\n" + SampleRate),
            SystemMetric.NetSend => D(title,
                "TCP and UDP payload bytes sent per second, all processes.",
                "Headers and retransmissions excluded; virtual machines' traffic not included. See the process metric.\n\n" + EtwSystemNote + "\n\n" + SampleRate),
            SystemMetric.NetRecv => D(title,
                "TCP and UDP payload bytes received per second, all processes.",
                "Headers excluded; virtual machines' traffic not included. See the process metric.\n\n" + EtwSystemNote + "\n\n" + SampleRate),
            SystemMetric.DpcRate => D(title,
                "Deferred procedure calls executed per second, all processors.",
                """
                Normal DPCs, threaded DPCs and timer DPCs. Network and storage traffic drive it up. For time spent, see CPU (DPC); per driver, the **Drivers** widget.
                """ + "\n\n" + EtwSystemNote + "\n\n" + SampleRate),
            SystemMetric.IsrRate => D(title,
                "Interrupt service routines executed per second, all processors (as traced by ETW).",
                "Per driver, see the **Drivers** widget.\n\n" + EtwSystemNote + "\n\n" + SampleRate),
            SystemMetric.DpcMax => D(title,
                "Longest single DPC in the interval: the latency killer.",
                """
                While a DPC runs, no thread can run on that processor: audio buffers underrun (crackles), frames are late (stutter), input lags.

                - Healthy: below about 0.1 ms. Over **1 ms** causes audible dropouts in real-time audio.
                - The **Drivers** widget, sorted by Max DPC, names the driver (often network, Wi-Fi, storage, GPU or ACPI).
                """ + "\n\n" + EtwSystemNote + "\n\n" + SampleRate),
            SystemMetric.IsrMax => D(title,
                "Longest single interrupt service routine in the interval.",
                """
                ISRs run above DPCs and should take microseconds (they defer the work to a DPC). Long ones are rarer but worse; the **Drivers** widget, sorted by Max ISR, names the driver.
                """ + "\n\n" + EtwSystemNote + "\n\n" + SampleRate),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
        };
    }

    public static Doc Of(CoreMetric metric)
    {
        var title = MetricCatalog.Get(metric).DisplayName;
        const string perCpu = "Per logical processor: with SMT (Hyper-Threading), two logical processors share one physical core.";
        return metric switch
        {
            CoreMetric.Usage => D(title,
                "Busy share of one logical processor (1 − idle), DPC and interrupt time included.",
                $"""
                {perCpu}

                One processor pegged while the others idle is a single-threaded bottleneck, invisible in the total CPU of a many-core machine. Interrupt-heavy devices often load one processor more than the others.

                **Source:** `NtQuerySystemInformationEx(SystemProcessorPerformanceInformation)`, per processor group.

                {SampleRate}
                """),
            CoreMetric.User => D(title,
                "Share of the processor's time running user-mode code.",
                $"{perCpu}\n\n{SampleRate}"),
            CoreMetric.Kernel => D(title,
                "Share of the processor's time in the kernel, DPC and interrupt time included (idle excluded).",
                $"{perCpu}\n\nIn the per-CPU graphs, \"Shade kernel time\" draws this part darker inside the usage.\n\n{SampleRate}"),
            CoreMetric.Dpc => D(title,
                "Share of the processor's time running DPCs.",
                $"""
                {perCpu}

                DPCs usually run on the processor that took the interrupt: a device whose interrupts target one processor concentrates its DPC load there (network adapters spread it with RSS).

                {SampleRate}
                """),
            CoreMetric.Interrupt => D(title,
                "Share of the processor's time in interrupt service routines.",
                $"{perCpu}\n\n{SampleRate}"),
            CoreMetric.InterruptRate => D(title,
                "Interrupts per second taken by this processor, clock interrupts included.",
                $"""
                {perCpu}

                Shows interrupt affinity: which processors service which devices. An idle processor still takes clock interrupts unless the tick is suppressed in deep idle.

                {SampleRate}
                """),
            CoreMetric.Frequency => D(title,
                "Effective frequency of this processor: nominal × delivered performance.",
                $"""
                {perCpu}

                Turbo shows above nominal; preferred ("favored") cores reach higher. A processor that stayed idle keeps its last value.

                **Source:** `SystemProcessorPerformanceDistribution` (per-processor performance-state statistics).

                {SampleRate}
                """),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
        };
    }
}
