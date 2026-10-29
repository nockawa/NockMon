using NockMon.Core.Gpu;
using NockMon.Core.Metrics;
using NockMon.Core.Sampling;

namespace NockMon.Core.Help;

public static partial class Docs
{
    private const string NvmlOnly = "NVIDIA GPUs only (NVML, the driver's management library); empty on other GPUs.";

    private const string PerfDataNote =
        "From the graphics kernel's adapter performance data (`D3DKMT`, WDDM 2.4+ drivers) when the driver reports it";

    private const string DriverNote =
        "**Source:** kernel ETW DPC/ISR events with their duration; the routine address is mapped to the driver image " +
        "loaded there. Over the scan interval.";

    private const string ObjectTypeNote =
        "**Source:** `NtQueryObject(ObjectTypesInformation)`, the object manager's per-type totals. **Rate:** process scan.";

    private const string PoolTagNote =
        "**Source:** `NtQuerySystemInformation(SystemPoolTagInformation)`. **Rate:** process scan.";

    public static Doc Of(GpuMetric metric)
    {
        var title = MetricCatalog.Get(metric).DisplayName;
        return metric switch
        {
            GpuMetric.Utilization => D(title,
                "NVIDIA: share of time any kernel ran on the GPU. Others: the busiest watched engine.",
                """
                - **NVIDIA (NVML):** percentage of the driver's sample period during which one or more kernels (3D or compute, CUDA included) executed. It says the GPU was *busy*, not how many of its units were: 100 % can still leave most SMs idle.
                - **Other GPUs:** the busiest engine among those polled for an engine series or the GPU engines widget; empty when none is.
                """ + "\n\n" + SampleRate),
            GpuMetric.Engine => D(title,
                "Busy share of the busiest engine matching the pattern (3D, Compute, Copy, Video…).",
                """
                Engine running time from the graphics kernel (`D3DKMT` node statistics), the source of Task Manager's GPU graphs: works for every vendor.

                - Common engines: **3D** (graphics, often compute too), **Compute**, **Copy** (transfers over PCIe), **Video Decode**, **Video Encode**, **Video Processing**. Names come from the driver.
                - Pattern: `*` and `?` wildcards, `;` between alternatives (`Compute*; 3D`).
                - Each engine is polled only while used, at about 0.3 ms per engine per sample.
                """ + "\n\n" + SampleRate),
            GpuMetric.MemoryControllerLoad => D(title,
                "Share of time the GPU memory was being read or written (NVIDIA).",
                $"High values with moderate utilization mean memory-bandwidth-bound work.\n\n{NvmlOnly}\n\n{SampleRate}"),
            GpuMetric.EncoderLoad => D(title,
                "Video encoder (NVENC) utilization (NVIDIA).",
                $"Streaming, recording, remote desktop and video calls use it.\n\n{NvmlOnly}\n\n{SampleRate}"),
            GpuMetric.DecoderLoad => D(title,
                "Video decoder (NVDEC) utilization (NVIDIA).",
                $"Hardware video playback uses it.\n\n{NvmlOnly}\n\n{SampleRate}"),
            GpuMetric.DedicatedUsed => D(title,
                "Video memory (VRAM) resident on the GPU, all processes.",
                """
                Bytes resident in the GPU's own memory segments, from the graphics kernel (`D3DKMT` segment statistics), as Task Manager shows it.

                When it nears the total, the driver evicts allocations to system memory (shared), which costs performance.
                """ + "\n\n" + SampleRate),
            GpuMetric.DedicatedTotal => D(title,
                "Size of the GPU's video memory available to Windows.",
                "Sum of the dedicated segments' limits (`D3DKMT`). Slightly below the card's advertised memory (reserved by the driver and firmware).\n\n" + SampleRate),
            GpuMetric.SharedUsed => D(title,
                "System RAM used by the GPU (shared, aperture segments).",
                """
                Allocations the GPU accesses in system memory over PCIe: integrated GPUs use only this; discrete GPUs use it for staging buffers and for what does not fit in VRAM.

                On a discrete GPU, growth when VRAM is full means spill-over: slower rendering or compute.
                """ + "\n\n" + SampleRate),
            GpuMetric.SharedTotal => D(title,
                "Maximum system RAM the GPU may use (typically half of RAM).",
                "Sum of the bounded aperture segments' limits (`D3DKMT`).\n\n" + SampleRate),
            GpuMetric.Temperature => D(title,
                "GPU core temperature.",
                $"NVML on NVIDIA; otherwise {PerfDataNote}. Most GPUs throttle from about 80–90 °C.\n\n{SampleRate}"),
            GpuMetric.Power => D(title,
                "Board power draw in watts (NVIDIA).",
                $"Whole-board power as measured by the card. Pinned at the power limit under load, the clocks drop: power-limited.\n\n{NvmlOnly}\n\n{SampleRate}"),
            GpuMetric.PowerPercent => D(title,
                "Power draw as a percentage, as reported by the graphics kernel.",
                $"{PerfDataNote}; empty otherwise. The reference (TDP or limit) is the driver's.\n\n{SampleRate}"),
            GpuMetric.FanPercent => D(title,
                "Fan speed as a percentage of its maximum (NVIDIA).",
                $"The driver's target speed, not always the measured one. 0 % at idle is normal on cards with fan stop.\n\n{NvmlOnly}\n\n{SampleRate}"),
            GpuMetric.FanRpm => D(title,
                "Fan speed in revolutions per minute.",
                $"{PerfDataNote}; empty otherwise.\n\n{SampleRate}"),
            GpuMetric.CoreClock => D(title,
                "Current GPU core (SM) clock (NVIDIA).",
                $"Drops at idle, boosts under load until a power or thermal limit.\n\n{NvmlOnly}\n\n{SampleRate}"),
            GpuMetric.MemoryClock => D(title,
                "Current video memory clock.",
                $"NVML on NVIDIA; otherwise {PerfDataNote}. Vendors differ in convention (actual clock vs. effective data rate).\n\n{SampleRate}"),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
        };
    }

    public static Doc Of(DriverField field) => field switch
    {
        DriverField.Name => D("Driver",
            "Driver image whose code the DPC or ISR routine belongs to.",
            """
            The routine address is mapped to the loaded driver containing it (`EnumDeviceDrivers`, refreshed every minute).

            - **Wdf01000.sys** is the driver framework (KMDF): it runs the routines of the drivers built on it, hiding which one.
            - **ntoskrnl.exe**: kernel timers and housekeeping. **ndis.sys** / **tcpip.sys**: network. **storport.sys** / **stornvme.sys**: storage. **nvlddmkm.sys** / **amdkmdag.sys** / **igdkmd64.sys**: GPU. **ACPI.sys**: firmware events (battery, thermal, embedded controller). **USBXHCI.sys**: USB. **HDAudBus.sys**: audio.
            - "unknown": address outside every driver known at the last refresh.
            """ + "\n\n" + DriverNote),
        DriverField.DpcTime => D("DPC time",
            "CPU time in the driver's DPCs, in ms per second (1000 = one processor fully busy).",
            "The driver's share of CPU (DPC). Sustained tens of ms/s is a lot for anything but a busy network or storage path.\n\n" + DriverNote),
        DriverField.DpcRate => D("DPC rate",
            "DPCs per second run for the driver.",
            "High rates with short DPCs are usually fine (network packets, storage completions). High rates on an idle machine point at polling or a chatty device.\n\n" + DriverNote),
        DriverField.DpcMax => D("Longest DPC",
            "Longest single DPC of the driver in the interval.",
            "What causes audio crackles and stutter: above about **1 ms** is a problem for real-time audio; healthy drivers stay well under 0.1 ms. Sort by this column to find the culprit.\n\n" + DriverNote),
        DriverField.IsrTime => D("ISR time",
            "CPU time in the driver's interrupt service routines, in ms per second (1000 = one processor).",
            "Should be tiny: ISRs only acknowledge the device and queue a DPC.\n\n" + DriverNote),
        DriverField.IsrRate => D("ISR rate",
            "Interrupts per second serviced by the driver.",
            "Thousands per second on an idle machine is an interrupt storm (stuck device, broken driver).\n\n" + DriverNote),
        DriverField.IsrMax => D("Longest ISR",
            "Longest single interrupt service routine of the driver in the interval.",
            "Should be a few microseconds to tens of microseconds; longer ISRs delay every DPC and thread on that processor.\n\n" + DriverNote),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    public static Doc Of(ObjectTypeField field) => field switch
    {
        ObjectTypeField.Name => D("Type",
            "Kernel object type (Process, Thread, Token, File, Event, Key, Section…).",
            """
            Every kernel resource is an object of a type managed by the object manager. A few to watch:

            - **Process** / **Thread**: more objects than running processes/threads means exited ones kept alive by open handles ("zombies"), each holding kernel memory.
            - **Token**: security contexts; leaks typically come from services impersonating clients without closing tokens.
            - **Job**, **Silo**: process groups, containers, sandboxes.
            - **File**, **Key** (registry), **Event**, **Section** (shared memory, mapped files), **ALPC Port** (local RPC), **EtwRegistration**.
            """ + "\n\n" + ObjectTypeNote),
        ObjectTypeField.Objects => D("Objects",
            "Live objects of the type.",
            "Objects exist while referenced, by handles or by the kernel itself: far more objects than handles means the kernel (or drivers) holds them. Sustained growth is a leak.\n\n" + ObjectTypeNote),
        ObjectTypeField.Handles => D("Handles",
            "Open handles to objects of the type, all processes.",
            "Growth here, with objects growing alike, is a user-mode handle leak: find the process with a process table sorted by handles.\n\n" + ObjectTypeNote),
        ObjectTypeField.ObjectGrowth => D("Object growth",
            "Change of the object count per minute, since the previous scan.",
            "Noisy from one scan to the next; what matters is a value that stays positive for minutes. Chart \"objects\" of the type (Kernel object source) to see the trend.\n\n" + ObjectTypeNote),
        ObjectTypeField.HandleGrowth => D("Handle growth",
            "Change of the handle count per minute, since the previous scan.",
            "See object growth.\n\n" + ObjectTypeNote),
        ObjectTypeField.PeakObjects => D("Peak objects",
            "Highest object count of the type since boot.",
            "Compared with the current count, shows whether a past spike or leak was released.\n\n" + ObjectTypeNote),
        ObjectTypeField.PeakHandles => D("Peak handles",
            "Highest handle count of the type since boot.",
            ObjectTypeNote),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    public static Doc Of(ObjectTypeMetric metric) => metric switch
    {
        ObjectTypeMetric.Objects => D("Objects",
            "Live kernel objects of the type (e.g. Token, Process, File).",
            Of(ObjectTypeField.Objects).Details + "\n\nEnter the type name as the Kernel objects widget lists it (suggestions are offered)."),
        ObjectTypeMetric.Handles => D("Handles",
            "Open handles to kernel objects of the type, all processes.",
            Of(ObjectTypeField.Handles).Details),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };

    public static Doc Of(PoolTagField field) => field switch
    {
        PoolTagField.Tag => D("Tag",
            "Four-character tag the allocating driver gave its kernel pool allocations.",
            """
            Each pool allocation carries a tag chosen by its owner (`File`, `Ntfx`, `Toke`, `Proc`, `Thre`, `NDnd`…). Unprintable characters show as dots.

            Finding the owner of an unknown tag:

            - `findstr /m /l /s <tag> %SystemRoot%\System32\drivers\*.sys` lists drivers containing it (case-sensitive; tags are often stored reversed in code, so try both orders).
            - The Windows Driver Kit ships `pooltag.txt`, a list of known tags.
            """ + "\n\n" + PoolTagNote),
        PoolTagField.NonPagedBytes => D("Non-paged",
            "Non-paged pool bytes currently allocated under the tag (always in RAM).",
            "The tag's share of the kernel non-paged pool. A tag that only grows is a leak; the driver owning the tag is the suspect.\n\n" + PoolTagNote),
        PoolTagField.PagedBytes => D("Paged",
            "Paged pool bytes currently allocated under the tag.",
            "The tag's share of the kernel paged pool.\n\n" + PoolTagNote),
        PoolTagField.TotalBytes => D("Total",
            "Paged + non-paged bytes allocated under the tag.",
            PoolTagNote),
        PoolTagField.NonPagedGrowth => D("Non-paged growth",
            "Change of the tag's non-paged bytes per second since the previous scan.",
            "Noisy scan to scan; a leak is growth that stays positive for minutes and never returns.\n\n" + PoolTagNote),
        PoolTagField.PagedGrowth => D("Paged growth",
            "Change of the tag's paged bytes per second since the previous scan.",
            "See non-paged growth.\n\n" + PoolTagNote),
        PoolTagField.AllocationRate => D("Allocation rate",
            "Allocations per second under the tag (paged + non-paged).",
            "Allocation churn: a high rate costs CPU in the pool allocator even without growth. Network and file system tags are typically the busiest.\n\n" + PoolTagNote),
        PoolTagField.Outstanding => D("Outstanding",
            "Allocations not freed yet (allocations − frees since boot).",
            "Number of live allocations. Growing together with the bytes: a leak of many objects; bytes growing alone: fewer, bigger allocations.\n\n" + PoolTagNote),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };
}
