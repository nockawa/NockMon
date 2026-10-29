using MyCpuMonitor.Core.Metrics;

namespace MyCpuMonitor.Core.Help;

/// <summary>
/// In-app documentation of every widget kind and metric. Written from this app's implementation (API, formula, rate),
/// not from generic definitions: keep it in sync when a source changes. Every enum value must have an entry (tested).
/// </summary>
public static partial class Docs
{
    private const string ScanRate =
        "**Rate:** process scan (2 s by default). In charts and values, processes matched by the series' pattern are " +
        "read at the sample rate (0.5 s by default).";

    private const string ScanOnly = "**Rate:** process scan (2 s by default).";

    private const string CpuScaleNote =
        "Shown as **% of the machine** (100 % = every logical processor busy, like Task Manager) or as **CPUs** " +
        "(1.00 = one logical processor fully busy), per the CPU scale toggle in the toolbar.";

    private const string EtwProcessNote =
        "**Source:** kernel ETW events (private real-time session, needs administrator rights), summed per process " +
        "over the scan interval.";

    private static Doc D(string title, string summary, string details) => new(title, summary, details);

    public static Doc Of(ProcessField field)
    {
        var title = MetricCatalog.Get(field).DisplayName;
        return field switch
        {
            ProcessField.Name => D(title,
                "Executable image name, as reported by the kernel (no path).",
                """
                The image name from the kernel's process list (`NtQuerySystemInformation`). The icon is the executable's.

                - **Idle** (PID 0) is not a process: its CPU is the idle time of every processor. Use "Exclude Idle" to hide it.
                - **System** (PID 4) hosts kernel threads: drivers, the cache manager, the memory manager. Its CPU and disk activity is the kernel's own work.
                - **Registry**, **Secure System**, **Memory Compression** are minimal processes: the registry hives, VBS (virtualization-based security) and the compression store.

                With "Group by name", processes sharing a name form one row whose values are summed.
                """ + "\n\n" + ScanOnly),
            ProcessField.Pid => D(title,
                "Process identifier. Reused after a process exits.",
                """
                PIDs are multiples of 4 and recycled quickly. This app tells processes apart by PID **and** creation time, so a reused PID is never mixed with the previous process.

                Empty in grouped rows.
                """),
            ProcessField.ParentPid => D(title,
                "PID of the process that created this one, at creation time.",
                """
                Recorded once at creation (`InheritedFromUniqueProcessId`). The parent may have exited since, and its PID may have been reused: the PID can then point to an **unrelated** process. Compare start times to be sure (a parent cannot start after its child).

                Tools that launch through a broker (services, `svchost.exe`, the shell) make the parent the broker, not the user-visible launcher.
                """),
            ProcessField.SessionId => D(title,
                "Terminal Services session: 0 for services, 1+ for logged-on users.",
                """
                - **0**: services and system processes (session 0 isolation: no interactive desktop).
                - **1, 2…**: one per interactive logon (console, Remote Desktop, fast user switching).
                """),
            ProcessField.BasePriority => D(title,
                "Base scheduling priority of the process (its priority class).",
                """
                Threads are scheduled from their own dynamic priority, derived from this base (boosts for I/O completion, foreground, etc.).

                - **4** Idle, **6** Below normal, **8** Normal, **10** Above normal, **13** High, **24** Realtime.

                Realtime processes can starve the rest of the system, input included.
                """),
            ProcessField.StartTime => D(title,
                "When the process was created (local time).",
                "Creation time recorded by the kernel. Empty in grouped rows."),
            ProcessField.Instances => D(title,
                "Number of processes aggregated: grouped rows, or processes matching a chart's pattern.",
                """
                1 for an ungrouped row. With "Group by name", how many processes share the name. As a chart series, how many running processes match the pattern: a step up or down shows a process starting or exiting.

                A count that grows steadily (e.g. `conhost.exe`, `WerFault.exe`, browser helpers) reveals processes spawned in a loop and never reaped.
                """ + "\n\n" + ScanRate),
            ProcessField.Cpu => D(title,
                "CPU time (user + kernel) consumed over the interval.",
                $"""
                Change of the process's user + kernel time divided by the interval. {CpuScaleNote}

                - DPC and interrupt time is **not** charged to processes (see System CPU (DPC) / (interrupt) and the Drivers widget). A machine can be busy while no process shows CPU.
                - Times are accounted at clock-tick granularity: very short intervals are noisy for processes with brief bursts.
                - **Idle** shows the idle time of all processors.

                **Source:** `NtQuerySystemInformation(SystemProcessInformation)`, the same data as Task Manager.

                {ScanRate}
                """),
            ProcessField.CpuUser => D(title,
                "CPU time spent running the process's own code (user mode).",
                $"""
                Application code, runtimes, libraries: everything outside the kernel. {CpuScaleNote}

                {ScanOnly}
                """),
            ProcessField.CpuKernel => D(title,
                "CPU time spent in the kernel on behalf of the process (system calls, drivers).",
                $"""
                System calls, file system and network stack work, page fault handling, and filter drivers (antivirus, backup, encryption) called in the process's context. {CpuScaleNote}

                A high kernel share points at heavy I/O or system call rates, page fault churn, or an expensive filter driver; compare with I/O operations and page faults.

                {ScanOnly}
                """),
            ProcessField.PrivateBytes => D(title,
                "Commit charge of the process: private memory it allocated, wherever it lives.",
                """
                Private committed memory: heaps, stacks, private `VirtualAlloc` commits, copy-on-write pages. Counted whether in RAM, in the page file, or never touched yet. Task Manager calls it "Commit size".

                - **The leak indicator:** steady growth that never comes back down.
                - It does **not** include shared memory (DLL code, mapped files) or the kernel memory used for the process (see paged/non-paged pool).
                - It is what consumes the system commit charge: when that reaches the commit limit, allocations fail.
                """ + "\n\n" + ScanRate),
            ProcessField.WorkingSet => D(title,
                "Physical memory (RAM) currently mapped by the process, shared pages included.",
                """
                Pages of the process resident in RAM: private pages **plus** shared ones (DLL and EXE code, mapped files, shared sections).

                - Shared pages count in every process that maps them: the sum over processes exceeds the memory used.
                - Windows trims working sets under memory pressure; trimmed pages go to the standby/modified lists and fault back cheaply (soft faults).
                - Use private bytes for leaks, working set for "what is in RAM now".
                """ + "\n\n" + ScanRate),
            ProcessField.PrivateWorkingSet => D(title,
                "Private pages of the process resident in RAM (not shared with others).",
                """
                The RAM only this process uses. Close to Task Manager's "Memory" column. Freeing the process would release about this much RAM (plus its kernel memory).

                The gap with private bytes is private memory that is committed but not resident (paged out, compressed, or never touched).
                """ + "\n\n" + ScanOnly),
            ProcessField.VirtualSize => D(title,
                "Address space reserved or committed by the process.",
                """
                Reserved plus committed virtual address space. Costs nothing by itself: 64-bit processes routinely show terabytes (CFG bitmap, runtime reservations, GPU mappings).

                Only meaningful for **32-bit** processes, which fail allocations when it nears 2 GB (4 GB if large-address aware).
                """ + "\n\n" + ScanOnly),
            ProcessField.PagedPool => D(title,
                "Paged kernel pool charged to the process (kernel memory used on its behalf).",
                """
                Quota charged to the process for kernel allocations made for it: object bodies and names, registry, security descriptors, file system structures.

                Growth along with handles usually means the process leaks handles to objects whose memory is charged to it.
                """ + "\n\n" + ScanOnly),
            ProcessField.NonPagedPool => D(title,
                "Non-paged kernel pool charged to the process (always-resident kernel memory).",
                """
                Quota charged to the process for non-paged allocations: processes, threads, events, sockets and other objects that must stay in RAM.

                Non-paged pool is physical memory that can never be paged out. A process whose figure keeps growing leaks kernel objects (often threads, events or sockets): check its handles.
                """ + "\n\n" + ScanOnly),
            ProcessField.IoOps => D(title,
                "All I/O requests issued by the process: files, network, pipes, devices. Not disk-only.",
                """
                Read, write and other (control) requests issued through handles: `ReadFile`/`WriteFile`/`DeviceIoControl` on files, sockets, named pipes, devices.

                - A file read served from the cache counts, though it never reaches the disk; a cached write counts once, while the later disk write is done by the System process.
                - For physical disk activity, use the **Disk** metrics (ETW).
                """ + "\n\n" + ScanRate),
            ProcessField.IoReadOps => D(title,
                "Read requests per second (files, sockets, pipes, devices).",
                "Read requests issued by the process, cached or not. See I/O operations.\n\n" + ScanOnly),
            ProcessField.IoWriteOps => D(title,
                "Write requests per second (files, sockets, pipes, devices).",
                "Write requests issued by the process, cached or not. See I/O operations.\n\n" + ScanOnly),
            ProcessField.IoOtherOps => D(title,
                "Requests that are neither reads nor writes: device and file system controls, queries.",
                """
                `DeviceIoControl`, file system controls, information queries, directory enumeration, network control calls.

                A high rate with little read/write traffic often comes from polling: hardware monitoring tools, anti-cheat, drivers' user-mode helpers.
                """ + "\n\n" + ScanOnly),
            ProcessField.IoBytes => D(title,
                "Bytes transferred by all of the process's I/O requests (files, network, pipes, devices).",
                """
                Data moved by read, write and other requests. Includes cached file I/O and socket traffic, so it is neither disk throughput nor network throughput: use the **Disk** and **Network** metrics for those.
                """ + "\n\n" + ScanRate),
            ProcessField.IoReadBytes => D(title,
                "Bytes read per second by I/O requests (cached file reads and socket receives included).",
                "See I/O throughput.\n\n" + ScanOnly),
            ProcessField.IoWriteBytes => D(title,
                "Bytes written per second by I/O requests (cached file writes and socket sends included).",
                "See I/O throughput.\n\n" + ScanOnly),
            ProcessField.IoOtherBytes => D(title,
                "Bytes moved by control requests (DeviceIoControl buffers, queries).",
                "Input and output buffers of non read/write requests. See I/O other.\n\n" + ScanOnly),
            ProcessField.Handles => D(title,
                "Open handles in the process's handle table (files, registry keys, events, threads…).",
                """
                Every open kernel object reference: files, registry keys, events, mutexes, sections, threads, processes, tokens.

                - **The handle leak indicator:** steady growth. A few hundred to a few thousand is normal; tens of thousands is suspicious (except System and some services).
                - Leaked handles to exited processes or threads keep those objects alive: see the Kernel objects widget (Process objects above the process count).
                """ + "\n\n" + ScanRate),
            ProcessField.Threads => D(title,
                "Number of threads in the process.",
                """
                Thread pools grow and shrink with load; a count that only grows (hundreds, thousands) suggests threads blocked forever or created per request.

                **Rate:** process scan, also for watched processes (not available through a process handle).
                """),
            ProcessField.PageFaults => D(title,
                "Page faults per second: soft (resolved in memory) and hard (read from disk).",
                """
                A fault occurs when the process touches a page not in its working set.

                - **Soft faults** are resolved without I/O (microseconds): first touch of new memory (demand-zero), copy-on-write, a page still on the standby/modified list after a trim, a shared page already in RAM.
                - High rates (100 k/s and more) usually mean memory churn: large blocks allocated and freed repeatedly, each first touch zeroed by the kernel.
                - See hard faults for the expensive ones.
                """ + "\n\n" + ScanOnly),
            ProcessField.HardFaults => D(title,
                "Page faults resolved by reading from disk (page file or mapped files, executables included).",
                """
                The fault needed a disk read: a paged-out page from the page file, or file data accessed through a mapping (memory-mapped files, and EXE/DLL code at load).

                - Bursts at application start are normal (image loading).
                - Sustained hard faults with low available memory is **thrashing**: the working sets do not fit in RAM.

                **Rate:** process scan, also for watched processes (not available through a process handle).
                """),
            ProcessField.DiskRead => D(title,
                "Bytes read from physical disks for the process.",
                "Physical disk reads issued for the process (after the file cache: cache hits do not count). See Disk throughput.\n\n" + EtwProcessNote + "\n\n" + ScanOnly),
            ProcessField.DiskWrite => D(title,
                "Bytes written to physical disks for the process.",
                "Physical disk writes issued by the process. Buffered (cached) writes are flushed later by the cache manager's lazy writer and show under **System**, not here. See Disk throughput.\n\n" + EtwProcessNote + "\n\n" + ScanOnly),
            ProcessField.DiskBytes => D(title,
                "Physical disk read + write throughput attributed to the process.",
                """
                I/O as it reaches the disk driver, unlike the I/O counters: file cache hits are excluded.

                - Delayed writes of cached files, page file writes and prefetching are done by the kernel and attributed to **System** (PID 4).
                - Reading through memory-mapped files shows as hard faults and disk reads of the process that faulted.
                """ + "\n\n" + EtwProcessNote + "\n\n" + ScanOnly),
            ProcessField.DiskOps => D(title,
                "Physical disk requests per second (IOPS) attributed to the process.",
                "Number of disk read and write requests completed. Many small requests at a low throughput means random I/O, the costly kind on hard drives.\n\n" + EtwProcessNote + "\n\n" + ScanOnly),
            ProcessField.DiskLatency => D(title,
                "Average response time of the process's disk requests.",
                """
                Mean time from issue to completion at the disk driver, over the requests completed in the interval: includes queueing in the storage stack.

                - Typical: NVMe SSD well under 1 ms, SATA SSD about 1 ms, hard drive 5–15 ms.
                - Tens of milliseconds means a saturated or failing device, or heavy queueing (look at the system IOPS).
                - 0 when the process did no disk I/O.
                """ + "\n\n" + EtwProcessNote + "\n\n" + ScanOnly),
            ProcessField.NetSend => D(title,
                "TCP and UDP payload bytes sent per second by the process.",
                "See Network throughput.\n\n" + EtwProcessNote + "\n\n" + ScanOnly),
            ProcessField.NetRecv => D(title,
                "TCP and UDP payload bytes received per second by the process.",
                "See Network throughput.\n\n" + EtwProcessNote + "\n\n" + ScanOnly),
            ProcessField.NetBytes => D(title,
                "TCP and UDP payload bytes sent + received per second by the process.",
                """
                Payload as seen by the TCP/IP stack (IPv4 and IPv6), attributed to the owning process. Bytes per second, binary units.

                - Excludes protocol headers and TCP retransmissions, so it is a bit below the adapter's traffic.
                - Not counted: traffic not handled by the host's TCP/IP stack for a process (Hyper-V/WSL virtual machines' traffic, raw sockets, ICMP).
                - QUIC (HTTP/3) is UDP: counted.
                """ + "\n\n" + EtwProcessNote + "\n\n" + ScanOnly),
            ProcessField.Gpu => D(title,
                "GPU utilization of the process on NVIDIA GPUs (NVML), summed over GPUs.",
                """
                Per-process utilization samples from the NVIDIA driver (`nvmlDeviceGetProcessUtilization`): the share of time the GPU executed work of this process (3D and compute).

                - NVIDIA only; 0 on other GPUs.
                - The driver samples sparsely: brief work can show as 0.
                - For engine-level detail of any vendor, use the GPU engines widget.

                **Rate:** process scan.
                """),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
    }
}
