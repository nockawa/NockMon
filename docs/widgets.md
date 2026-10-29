# Widgets and metrics

[← README](../README.md)

- [Widgets](#widgets)
- [Metrics](#metrics)

Every widget, column and metric also documents itself in the app: hover it, or click a widget's `?`.

## Widgets

Sizes are in board cells (64 px plus a 4 px gap by default). *Scan rate* means the process scan interval (2 s by
default); *sample rate* the sample interval (0.5 s by default). See [Settings](user-guide.md#settings).

### Chart

Time series of one or more metrics, from the history. Default size 6×3, minimum 2×1.

| Option | Meaning |
|---|---|
| Series | One or more series: a metric of a source (system, logical CPU, processes by name pattern, kernel object type, GPU). All series share a unit. Each has an optional label and color. |
| Window | Time span shown (default 5 minutes), within the history kept (1 hour by default). |
| Stacked | Draws series on top of each other: for parts of a whole (user + kernel CPU, several processes). |

Only metrics marked *chartable* below have history. Process series are sampled at the fast rate: NockMon keeps a handle
on every matching process.

### Value

One metric's latest value, large, over a sparkline. Default size 2×2, minimum 1×1.

| Option | Meaning |
|---|---|
| Source | Any series a chart can show. |
| Sparkline | Time span of the sparkline (default 60 s). |

An untitled value shows the metric's name on hover.

### Value list

Label / value rows for a list of metrics, compact. Default size 4×7, minimum 2×1.

| Option | Meaning |
|---|---|
| Items | Any series a chart can show, one per row, latest value. *Reset to kernel overview* restores the default set: scheduler, interrupts, memory lists, pools. |

### Per-CPU graphs

One small graph per logical processor: shows how load spreads. A single pegged processor (single-threaded bottleneck,
interrupt-bound device) is invisible in the total. Default size 8×4, minimum 2×2.

| Option | Meaning |
|---|---|
| Metric | Usage, user, kernel, DPC, interrupt time, interrupt rate or effective frequency. |
| Shade kernel time | With usage, draws the kernel part darker. |
| Window | Time span of each graph (default 60 s). |
| Columns | Graphs per row; 0 = automatic. |

With SMT (Hyper-Threading), two logical processors share one physical core: on most machines they are numbered in pairs.

### GPU engines

One graph per engine of a GPU (3D, Compute, Copy, Video decode/encode…), 0–100 % busy, for any vendor. Default size
8×4, minimum 2×2.

| Option | Meaning |
|---|---|
| Adapter | The GPU. |
| Engines | Name pattern (`*` and `?`, `;` between alternatives): `3D; Compute*; Video*`. Each shown engine costs about 0.3 ms per sample. |
| Window | Time span of each graph (default 60 s). |
| Columns | Graphs per row; 0 = automatic. |

Engine names arrive a moment after start ("Engine N" until then).

### Process table

Top processes of the latest scan, sorted by any metric. Default size 10×8, minimum 3×2. Refreshed at the scan rate.

| Option | Meaning |
|---|---|
| Columns | Any [process metric](#process), each with its width. Hover a header for its meaning. |
| Sort | Any column, descending or ascending. Click a header to sort, again to reverse. |
| Rows | Number of processes shown (default 20). |
| Group by name | Sums processes sharing an image name into one row (browsers, `svchost.exe`): the Instances column counts them. |
| Exclude Idle | Hides the System Idle Process, which otherwise tops a CPU sort on a quiet machine. |
| Filter | Keeps matching processes only (name pattern). |

### Process events

Recent process starts and exits, newest first, with command lines and exit codes. Built from kernel events, so it
catches short-lived processes no scan would ever see: scripts, updaters, crash handlers, processes spawned in a loop.
Default size 10×6, minimum 3×2. Needs the [kernel event session](user-guide.md#kernel-events-etw).

| Option | Meaning |
|---|---|
| Rows | Maximum number of events shown (default 30; the last 500 are kept). |
| Starts / exits | Which events to show. |
| Filter | Name pattern. |

Common exit codes: `0x0` success; `0xC0000005` access violation; `0xC0000409` stack buffer overrun / fail-fast;
`0xC00000FD` stack overflow; `0xE0434352` unhandled .NET exception; `0xC000013A` Ctrl+C / console closed;
`0x40010004` terminated by a debugger.

### Drivers (DPC/ISR)

DPC and interrupt activity per driver: which driver burns CPU or causes latency spikes. Time columns are ms per second
(1000 = one processor busy). Default size 8×5, minimum 3×2. Refreshed at the scan rate. Needs the
[kernel event session](user-guide.md#kernel-events-etw).

- **Audio crackles, stutter, mouse lag**: sort by **Longest DPC** (or Longest ISR). Over about 1 ms is the culprit.
- **High DPC or interrupt CPU with no process to blame**: sort by DPC or ISR time.

Columns: see [driver](#driver) below. Options: sort, rows.

### Kernel pool tags

Kernel pool usage per allocation tag: the tool to find a leaking driver. Default size 7×6, minimum 3×2. Refreshed at the
scan rate.

Drivers tag their kernel memory allocations with four characters. To hunt a leak: kernel non-paged (or paged) pool grows
for hours → sort by **Non-paged** (or its growth) → a tag stands out and keeps growing → find the driver owning the tag
(hover the Tag header for how).

Columns: see [poolTag](#pooltag) below. Options: sort, rows.

### Kernel objects

Live kernel objects and handles per object type (Process, Thread, Token, File, Event, Key…), with growth. Default size
8×6, minimum 3×2. Refreshed at the scan rate.

- **Objects far above handles**: references held by the kernel or drivers, not by processes.
- **Process / Thread objects above the running counts**: exited processes kept alive by leaked handles.
- **Sustained positive growth** is a leak: chart the type's objects (Kernel object source) to confirm the trend.

Options: sort, rows.

### Stack

Several widgets sharing one area, one shown at a time. See [Stacks](user-guide.md#stacks).

| Option | Meaning |
|---|---|
| Items | The stacked widgets: reorder, remove, edit. |
| Auto-cycle | Seconds between widgets; 0 = manual. Paused while the pointer is over the stack. |

A stack's minimum size is the largest minimum of its items.

### Plugin widgets

Widgets provided by [plugins](plugins.md). Their options are the plugin's own. A layout that refers to a plugin that
isn't installed keeps the widget's settings and tells you what to install.

## Metrics

The ids below are the ones the [MCP tools](mcp.md) use (`system.Cpu`, `process.WorkingSet`…); widgets show the names.
Units are those the MCP tools report: in the app, values are formatted automatically (bytes in B/KB/MB/GB, CPU as
% of total or logical CPUs depending on the [CPU setting](user-guide.md#settings)).

*Chartable* metrics have history: charts, values and sparklines can show them. The others are table columns or latest
values only.

- **system**: the whole machine.
- **core**: one logical processor (chart target: its index).
- **process**: processes. In charts, the sum over the processes matching a name pattern.
- **gpu**: a GPU adapter. Every vendor reports engine load and memory through the graphics kernel, plus temperature,
  fan speed, memory clock and power percentage when the driver exposes them. NVIDIA GPUs add utilization, power in
  watts, clocks and encoder/decoder load through NVML.
- **objectType**: a kernel object type (chart target: its name, e.g. `Token`).
- **driver**, **poolTag**: columns of the Drivers and Kernel pool tags widgets.

### system

| Id | Name | Unit | Chartable | Meaning |
|---|---|---|---|---|
| `system.Cpu` | CPU | logical CPUs | yes | Total CPU usage: all logical processors, everything that is not idle. |
| `system.CpuUser` | CPU (user) | logical CPUs | yes | CPU time running application code (user mode), all processors. |
| `system.CpuKernel` | CPU (kernel) | logical CPUs | yes | CPU time in the kernel, all processors, DPC and interrupt time included (idle excluded). |
| `system.CpuDpc` | CPU (DPC) | logical CPUs | yes | CPU time spent in deferred procedure calls: driver work queued by interrupts. |
| `system.CpuInterrupt` | CPU (interrupt) | logical CPUs | yes | CPU time spent in interrupt service routines (hardware interrupts). |
| `system.PhysicalUsed` | Physical memory used | MB | yes | RAM in use: total minus available (standby cache excluded, modified pages included). |
| `system.PhysicalAvailable` | Physical memory available | MB | yes | RAM that can be given to processes immediately: free + zeroed + standby (cache). |
| `system.CommitTotal` | Commit charge | MB | yes | Commit charge: memory promised to processes and the kernel, which RAM + page file must back. |
| `system.CommitLimit` | Commit limit | MB | yes | Maximum commit charge: RAM plus the current page file sizes. |
| `system.SystemCache` | System cache | MB | yes | System cache: standby list plus the system working set. |
| `system.KernelPaged` | Kernel paged pool | MB | yes | Paged pool: kernel memory that can be paged out. |
| `system.KernelNonPaged` | Kernel non-paged pool | MB | yes | Non-paged pool: kernel memory that always stays in RAM. |
| `system.Processes` | Processes | count | yes | Number of processes running. |
| `system.Threads` | Threads | count | yes | Number of threads, all processes (kernel threads included). |
| `system.Handles` | Handles | count | yes | Number of open handles, all processes. |
| `system.IoOps` | I/O operations | /s | yes | I/O requests per second of all processes: files (cached included), network, pipes, devices. |
| `system.IoBytes` | I/O throughput | MB/s | yes | Bytes per second moved by all processes' I/O requests. |
| `system.ContextSwitches` | Context switches | /s | yes | Thread switches per second, all processors. |
| `system.SystemCalls` | System calls | /s | yes | Calls into the kernel per second, all processes. |
| `system.Interrupts` | Interrupts | /s | yes | Hardware interrupts per second, all processors (clock interrupts included). |
| `system.PageFaults` | Page faults | /s | yes | Page faults per second, all processes (soft and hard). |
| `system.PageReads` | Hard fault page reads | /s | yes | Disk read operations per second issued to resolve hard page faults. |
| `system.MemoryZeroed` | Zeroed memory | MB | yes | Zeroed page list: free pages already filled with zeros, ready for new allocations. |
| `system.MemoryFree` | Free memory | MB | yes | Free page list: released pages not zeroed yet. |
| `system.MemoryStandby` | Standby memory (cache) | MB | yes | Standby list: cached pages (file data, trimmed process pages), reusable instantly. |
| `system.MemoryModified` | Modified memory | MB | yes | Modified list: dirty pages that must be written to disk before reuse. |
| `system.TimerResolution` | Timer resolution | ms | yes | Current global timer interrupt interval (default 15.625 ms). |
| `system.CpuFrequency` | CPU frequency | MHz | yes | Effective processor frequency, averaged over logical processors. |
| `system.ProcessorQueueLength` | Processor queue length | count | yes | Threads ready to run but waiting for a processor, at the scan instant. |
| `system.DiskRead` | Disk read | MB/s | yes | Physical disk read throughput, all disks. |
| `system.DiskWrite` | Disk write | MB/s | yes | Physical disk write throughput, all disks. |
| `system.DiskOps` | Disk IOPS | /s | yes | Physical disk requests completed per second (IOPS), all disks. |
| `system.DiskLatency` | Disk latency (avg) | ms | yes | Average disk response time over the requests completed in the interval, all disks. |
| `system.NetSend` | Network send | MB/s | yes | TCP and UDP payload bytes sent per second, all processes. |
| `system.NetRecv` | Network receive | MB/s | yes | TCP and UDP payload bytes received per second, all processes. |
| `system.DpcRate` | DPCs | /s | yes | Deferred procedure calls executed per second, all processors. |
| `system.IsrRate` | ISRs | /s | yes | Interrupt service routines executed per second, all processors (as traced by ETW). |
| `system.DpcMax` | Longest DPC | ms | yes | Longest single DPC in the interval: the latency killer. |
| `system.IsrMax` | Longest ISR | ms | yes | Longest single interrupt service routine in the interval. |

### core

| Id | Name | Unit | Chartable | Meaning |
|---|---|---|---|---|
| `core.Usage` | Core usage | logical CPUs | yes | Busy share of one logical processor (1 − idle), DPC and interrupt time included. |
| `core.User` | Core user time | logical CPUs | yes | Share of the processor's time running user-mode code. |
| `core.Kernel` | Core kernel time | logical CPUs | yes | Share of the processor's time in the kernel, DPC and interrupt time included (idle excluded). |
| `core.Dpc` | Core DPC time | logical CPUs | yes | Share of the processor's time running DPCs. |
| `core.Interrupt` | Core interrupt time | logical CPUs | yes | Share of the processor's time in interrupt service routines. |
| `core.InterruptRate` | Core interrupts | /s | yes | Interrupts per second taken by this processor, clock interrupts included. |
| `core.Frequency` | Core frequency | MHz | yes | Effective frequency of this processor: nominal × delivered performance. |

### process

| Id | Name | Unit | Chartable | Meaning |
|---|---|---|---|---|
| `process.Name` | Name |  | no | Executable image name, as reported by the kernel (no path). |
| `process.Pid` | Process ID | count | no | Process identifier. Reused after a process exits. |
| `process.ParentPid` | Parent process ID | count | no | PID of the process that created this one, at creation time. |
| `process.SessionId` | Session | count | no | Terminal Services session: 0 for services, 1+ for logged-on users. |
| `process.BasePriority` | Base priority | count | no | Base scheduling priority of the process (its priority class). |
| `process.StartTime` | Start time | time | no | When the process was created (local time). |
| `process.Instances` | Instances | count | yes | Number of processes aggregated: grouped rows, or processes matching a chart's pattern. |
| `process.Cpu` | CPU | logical CPUs | yes | CPU time (user + kernel) consumed over the interval. |
| `process.CpuUser` | CPU (user) | logical CPUs | no | CPU time spent running the process's own code (user mode). |
| `process.CpuKernel` | CPU (kernel) | logical CPUs | no | CPU time spent in the kernel on behalf of the process (system calls, drivers). |
| `process.PrivateBytes` | Private bytes | MB | yes | Commit charge of the process: private memory it allocated, wherever it lives. |
| `process.WorkingSet` | Working set | MB | yes | Physical memory (RAM) currently mapped by the process, shared pages included. |
| `process.PrivateWorkingSet` | Private working set | MB | no | Private pages of the process resident in RAM (not shared with others). |
| `process.VirtualSize` | Virtual size | MB | no | Address space reserved or committed by the process. |
| `process.PagedPool` | Paged pool | MB | no | Paged kernel pool charged to the process (kernel memory used on its behalf). |
| `process.NonPagedPool` | Non-paged pool | MB | no | Non-paged kernel pool charged to the process (always-resident kernel memory). |
| `process.IoOps` | I/O operations | /s | yes | All I/O requests issued by the process: files, network, pipes, devices. Not disk-only. |
| `process.IoReadOps` | I/O reads | /s | no | Read requests per second (files, sockets, pipes, devices). |
| `process.IoWriteOps` | I/O writes | /s | no | Write requests per second (files, sockets, pipes, devices). |
| `process.IoOtherOps` | I/O other | /s | no | Requests that are neither reads nor writes: device and file system controls, queries. |
| `process.IoBytes` | I/O throughput | MB/s | yes | Bytes transferred by all of the process's I/O requests (files, network, pipes, devices). |
| `process.IoReadBytes` | I/O read throughput | MB/s | no | Bytes read per second by I/O requests (cached file reads and socket receives included). |
| `process.IoWriteBytes` | I/O write throughput | MB/s | no | Bytes written per second by I/O requests (cached file writes and socket sends included). |
| `process.IoOtherBytes` | I/O other throughput | MB/s | no | Bytes moved by control requests (DeviceIoControl buffers, queries). |
| `process.Handles` | Handles | count | yes | Open handles in the process's handle table (files, registry keys, events, threads…). |
| `process.Threads` | Threads | count | yes | Number of threads in the process. |
| `process.PageFaults` | Page faults | /s | no | Page faults per second: soft (resolved in memory) and hard (read from disk). |
| `process.HardFaults` | Hard faults | /s | yes | Page faults resolved by reading from disk (page file or mapped files, executables included). |
| `process.DiskRead` | Disk read | MB/s | no | Bytes read from physical disks for the process. |
| `process.DiskWrite` | Disk write | MB/s | no | Bytes written to physical disks for the process. |
| `process.DiskBytes` | Disk throughput | MB/s | yes | Physical disk read + write throughput attributed to the process. |
| `process.DiskOps` | Disk IOPS | /s | no | Physical disk requests per second (IOPS) attributed to the process. |
| `process.DiskLatency` | Disk latency (avg) | ms | no | Average response time of the process's disk requests. |
| `process.NetSend` | Network send | MB/s | no | TCP and UDP payload bytes sent per second by the process. |
| `process.NetRecv` | Network receive | MB/s | no | TCP and UDP payload bytes received per second by the process. |
| `process.NetBytes` | Network throughput | MB/s | yes | TCP and UDP payload bytes sent + received per second by the process. |
| `process.Gpu` | GPU (NVIDIA) | % | yes | GPU utilization of the process on NVIDIA GPUs (NVML), summed over GPUs. |

### gpu

| Id | Name | Unit | Chartable | Meaning |
|---|---|---|---|---|
| `gpu.Utilization` | GPU utilization | % | yes | NVIDIA: share of time any kernel ran on the GPU. Others: the busiest watched engine. |
| `gpu.MemoryControllerLoad` | GPU memory controller | % | yes | Share of time the GPU memory was being read or written (NVIDIA). |
| `gpu.EncoderLoad` | GPU encoder | % | yes | Video encoder (NVENC) utilization (NVIDIA). |
| `gpu.DecoderLoad` | GPU decoder | % | yes | Video decoder (NVDEC) utilization (NVIDIA). |
| `gpu.DedicatedUsed` | GPU dedicated memory used | MB | yes | Video memory (VRAM) resident on the GPU, all processes. |
| `gpu.DedicatedTotal` | GPU dedicated memory | MB | yes | Size of the GPU's video memory available to Windows. |
| `gpu.SharedUsed` | GPU shared memory used | MB | yes | System RAM used by the GPU (shared, aperture segments). |
| `gpu.SharedTotal` | GPU shared memory | MB | yes | Maximum system RAM the GPU may use (typically half of RAM). |
| `gpu.Temperature` | GPU temperature | °C | yes | GPU core temperature. |
| `gpu.Power` | GPU power | W | yes | Board power draw in watts (NVIDIA). |
| `gpu.PowerPercent` | GPU power (%) | % | yes | Power draw as a percentage, as reported by the graphics kernel. |
| `gpu.FanPercent` | GPU fan | % | yes | Fan speed as a percentage of its maximum (NVIDIA). |
| `gpu.FanRpm` | GPU fan speed | rpm | yes | Fan speed in revolutions per minute. |
| `gpu.CoreClock` | GPU core clock | MHz | yes | Current GPU core (SM) clock (NVIDIA). |
| `gpu.MemoryClock` | GPU memory clock | MHz | yes | Current video memory clock. |

### objectType

| Id | Name | Unit | Chartable | Meaning |
|---|---|---|---|---|
| `objectType.Objects` | Kernel objects: objects | count | yes | Live kernel objects of the type (e.g. Token, Process, File). |
| `objectType.Handles` | Kernel objects: handles | count | yes | Open handles to kernel objects of the type, all processes. |

### driver

| Id | Name | Unit | Chartable | Meaning |
|---|---|---|---|---|
| `driver.DpcTime` | DPC time | ms | no | CPU time in the driver's DPCs, in ms per second (1000 = one processor fully busy). |
| `driver.DpcRate` | DPC rate | /s | no | DPCs per second run for the driver. |
| `driver.DpcMax` | Longest DPC | ms | no | Longest single DPC of the driver in the interval. |
| `driver.IsrTime` | ISR time | ms | no | CPU time in the driver's interrupt service routines, in ms per second (1000 = one processor). |
| `driver.IsrRate` | ISR rate | /s | no | Interrupts per second serviced by the driver. |
| `driver.IsrMax` | Longest ISR | ms | no | Longest single interrupt service routine of the driver in the interval. |

### poolTag

| Id | Name | Unit | Chartable | Meaning |
|---|---|---|---|---|
| `poolTag.NonPagedBytes` | Non-paged | MB | no | Non-paged pool bytes currently allocated under the tag (always in RAM). |
| `poolTag.PagedBytes` | Paged | MB | no | Paged pool bytes currently allocated under the tag. |
| `poolTag.TotalBytes` | Total | MB | no | Paged + non-paged bytes allocated under the tag. |
| `poolTag.NonPagedGrowth` | Non-paged growth | KB/s | no | Change of the tag's non-paged bytes per second since the previous scan. |
| `poolTag.PagedGrowth` | Paged growth | KB/s | no | Change of the tag's paged bytes per second since the previous scan. |
| `poolTag.AllocationRate` | Allocation rate | /s | no | Allocations per second under the tag (paged + non-paged). |
| `poolTag.Outstanding` | Outstanding | count | no | Allocations not freed yet (allocations − frees since boot). |
