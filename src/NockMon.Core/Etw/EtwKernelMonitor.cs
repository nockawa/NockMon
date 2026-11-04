using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace NockMon.Core.Etw;

/// <summary>
/// Real-time kernel ETW session under a private name (Windows 10+ system logger mode): no conflict with xperf,
/// Process Monitor or other tools using "NT Kernel Logger". Disk I/O (with response time), TCP/UDP, DPC/ISR (with
/// duration), process start/exit. Callbacks run on a dedicated thread and only add into accumulators under a short
/// lock; the sampler harvests (and resets) them. A stale session left by a crash is stopped at start.
/// No ImageLoad keyword: it triggers a rundown of every module of every process; drivers come from EnumDeviceDrivers.
/// <para>
/// The same session takes the .NET runtime provider (every .NET Framework and .NET process emits it, without any setting
/// in the process): GCs, their suspensions, heap size after each GC, allocation ticks (one per ~100 KB allocated: the
/// Verbose level), exceptions thrown and lock contentions.
/// </para>
/// </summary>
/// <param name="sessionName">ETW session name. Tests use their own: starting stops a session of the same name, which
/// would be a running NockMon's.</param>
public sealed class EtwKernelMonitor(string sessionName = EtwKernelMonitor.SessionName) : IKernelEventSource
{
    public const string SessionName = "NockMon-Kernel";
    private const int MaxEvents = 500;
    private static readonly TimeSpan DriverRefresh = TimeSpan.FromMinutes(1);

    private readonly string _sessionName = sessionName;
    private readonly Lock _lock = new();
    private readonly DriverMap _drivers = new();
    private readonly Dictionary<string, int> _driverIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DriverTotals> _driverTotals = [];
    private readonly Dictionary<int, ProcessIoTotals> _processes = new();
    private readonly Dictionary<int, RuntimeState> _runtimes = new();
    private readonly Queue<ProcessEvent> _events = new();
    private KernelEventTotals _totals;
    private TraceEventSession? _session;
    private Thread? _thread;
    private long _lastDriverRefresh;

    /// <summary>A .NET process's totals, the suspension in progress, and the allocations since its last GC.</summary>
    internal struct RuntimeState
    {
        public RuntimeTotals Totals;
        public double SuspendedAtMs;
        public bool Suspended;
        public bool Exited;
        /// <summary>Whether <see cref="RuntimeTotals.HeapBytes"/> comes from a GC (the base of the next freed amount).</summary>
        public bool HasHeap;
        public long AllocatedSinceGc;
    }

    private const ClrTraceEventParser.Keywords RuntimeKeywords =
        ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Exception | ClrTraceEventParser.Keywords.Contention;

    private volatile string? _status = "Not started.";
    private long _eventsLost;

    /// <summary>Set by the ETW thread too (session stopped by another program).</summary>
    public string? Status
    {
        get => _status;
        private set => _status = value;
    }

    /// <summary>Queries the session: the last known count once it is gone (the query would throw).</summary>
    public long EventsLost
    {
        get
        {
            if (_session is not { } session || _status is not null)
                return _eventsLost;
            try
            {
                return _eventsLost = session.EventsLost;
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                return _eventsLost; // Stopped meanwhile: the ETW thread reports it.
            }
        }
    }

    public void Start()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Status = "Kernel events need administrator rights.";
            return;
        }
        try
        {
            TraceEventSession.GetActiveSession(_sessionName)?.Stop();
            _drivers.Refresh();
            _lastDriverRefresh = Stopwatch.GetTimestamp();

            // TraceEvent's default preallocates max(64 MB, 2 MB per logical processor) of kernel buffers, kept by an
            // orphaned session until reboot; events are consumed as they come, so a few MB suffice (EventsLost tells).
            _session = new TraceEventSession(_sessionName) { StopOnDispose = true, BufferSizeMB = 16 };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.NetworkTCPIP
                | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.DeferedProcedureCalls
                | KernelTraceEventParser.Keywords.Interrupt);
            Subscribe(_session.Source.Kernel);
            // After the kernel provider (system logger mode first). Without it, the kernel events still work.
            try
            {
                _session.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, (ulong)RuntimeKeywords);
                Subscribe(_session.Source.Clr);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or COMException)
            {
                Debug.WriteLine($".NET runtime events unavailable: {e.Message}");
            }
            Status = null; // Before the thread: a session failing at once reports it, not overwritten here.
            _thread = new Thread(ProcessEvents) { IsBackground = true, Name = "NockMon ETW" };
            _thread.Start(_session);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            Status = $"Kernel events unavailable: {e.Message}";
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>
    /// The ETW thread. <c>Process</c> returns when <see cref="Dispose"/> stops the session; when another program stops
    /// it, it returns or throws (ERROR_WMI_INSTANCE_NOT_FOUND). Either way the app keeps running and reports it.
    /// </summary>
    private void ProcessEvents(object? state)
    {
        var session = (TraceEventSession)state!;
        string? failure = null;
        try
        {
            session.Source.Process();
        }
        catch (Exception e) // Unhandled here, any exception ends the process: report it instead.
        {
            failure = e.Message;
        }
        if (Volatile.Read(ref _session) != session)
            return; // Disposed: a normal stop.
        // The name may now belong to the session of whoever stopped ours: never stop it on dispose.
        session.StopOnDispose = false;
        Status = failure is null
            ? "The kernel event session was stopped by another program. Restart NockMon to resume."
            : $"The kernel event session failed ({failure.TrimEnd('.')}). Restart NockMon to resume.";
    }

    private void Subscribe(KernelTraceEventParser k)
    {
        k.DiskIORead += OnDiskRead;
        k.DiskIOWrite += OnDiskWrite;
        k.TcpIpSend += e => OnNet(e.ProcessID, e.size, send: true);
        k.TcpIpRecv += e => OnNet(e.ProcessID, e.size, send: false);
        k.TcpIpSendIPV6 += e => OnNet(e.ProcessID, e.size, send: true);
        k.TcpIpRecvIPV6 += e => OnNet(e.ProcessID, e.size, send: false);
        k.UdpIpSend += e => OnNet(e.ProcessID, e.size, send: true);
        k.UdpIpRecv += e => OnNet(e.ProcessID, e.size, send: false);
        k.UdpIpSendIPV6 += e => OnNet(e.ProcessID, e.size, send: true);
        k.UdpIpRecvIPV6 += e => OnNet(e.ProcessID, e.size, send: false);
        k.PerfInfoDPC += e => OnDpc(e.Routine, e.ElapsedTimeMSec);
        k.PerfInfoThreadedDPC += e => OnDpc(e.Routine, e.ElapsedTimeMSec);
        k.PerfInfoTimerDPC += e => OnDpc(e.Routine, e.ElapsedTimeMSec);
        k.PerfInfoISR += e => OnIsr(e.Routine, e.ElapsedTimeMSec);
        k.ProcessStart += e => AddEvent(new ProcessEvent(e.TimeStamp.ToUniversalTime(), ProcessEventKind.Start, e.ProcessID, e.ParentID,
            e.ImageFileName, e.CommandLine, null));
        k.ProcessStop += e =>
        {
            AddEvent(new ProcessEvent(e.TimeStamp.ToUniversalTime(), ProcessEventKind.Exit, e.ProcessID, e.ParentID,
                e.ImageFileName, null, e.ExitStatus));
            lock (_lock)
            {
                ref var r = ref CollectionsMarshal.GetValueRefOrNullRef(_runtimes, e.ProcessID);
                if (!Unsafe.IsNullRef(ref r))
                    r.Exited = true; // Reported once more, then forgotten (its PID can be reused).
            }
        };
    }

    private void Subscribe(ClrTraceEventParser clr)
    {
        clr.GCStart += e =>
        {
            lock (_lock)
            {
                ref var r = ref Runtime(e.ProcessID);
                r.Totals.Gcs++;
                if (e.Depth == 2)
                    r.Totals.Gen2Gcs++;
            }
        };
        // A GC suspends the managed threads once (blocking GC) or several times (background GC phases): every
        // suspension for a GC is a pause. Suspensions for the debugger or shutdown are not.
        clr.GCSuspendEEStart += e =>
        {
            if (e.Reason is not (GCSuspendEEReason.SuspendForGC or GCSuspendEEReason.SuspendForGCPrep))
                return;
            lock (_lock)
            {
                ref var r = ref Runtime(e.ProcessID);
                r.Suspended = true;
                r.SuspendedAtMs = e.TimeStampRelativeMSec;
            }
        };
        clr.GCRestartEEStop += e =>
        {
            lock (_lock)
            {
                ref var r = ref Runtime(e.ProcessID);
                if (!r.Suspended)
                    return;
                r.Suspended = false;
                var pause = Math.Max(0, e.TimeStampRelativeMSec - r.SuspendedAtMs);
                r.Totals.PauseMs += pause;
                r.Totals.PauseMaxMs = Math.Max(r.Totals.PauseMaxMs, pause);
            }
        };
        clr.GCHeapStats += e =>
        {
            // Generation 3 is the large object heap, 4 (version 2: .NET 5+) the pinned object heap.
            var v2 = e.Version >= 2;
            var heap = e.GenerationSize0 + e.GenerationSize1 + e.GenerationSize2 + e.GenerationSize3 + (v2 ? e.GenerationSize4 : 0);
            var promoted = e.TotalPromotedSize0 + e.TotalPromotedSize1 + e.TotalPromotedSize2 + e.TotalPromotedSize3 + (v2 ? e.TotalPromotedSize4 : 0);
            lock (_lock)
                OnHeapStats(ref Runtime(e.ProcessID), heap, promoted);
        };
        clr.GCAllocationTick += e =>
        {
            var bytes = e.Version >= 2 ? e.AllocationAmount64 : e.AllocationAmount;
            lock (_lock)
            {
                ref var r = ref Runtime(e.ProcessID);
                r.Totals.AllocatedBytes += bytes;
                r.AllocatedSinceGc += bytes;
            }
        };
        clr.ExceptionStart += e =>
        {
            lock (_lock)
                Runtime(e.ProcessID).Totals.Exceptions++;
        };
        clr.ContentionStart += e =>
        {
            lock (_lock)
                Runtime(e.ProcessID).Totals.Contentions++;
        };
    }

    /// <summary>
    /// The end of a GC: what it freed is the heap before it (after the previous GC, plus what was allocated since) minus
    /// the heap after it. Unknown for the first GC seen (no heap before). Not clamped: rounding errors cancel out.
    /// </summary>
    internal static void OnHeapStats(ref RuntimeState r, long heap, long promoted)
    {
        if (r.HasHeap)
            r.Totals.FreedBytes += r.Totals.HeapBytes + r.AllocatedSinceGc - heap;
        r.Totals.PromotedBytes += promoted;
        r.Totals.HeapBytes = heap;
        r.HasHeap = true;
        r.AllocatedSinceGc = 0;
    }

    /// <summary>State of a .NET process (caller holds the lock).</summary>
    private ref RuntimeState Runtime(int pid) => ref CollectionsMarshal.GetValueRefOrAddDefault(_runtimes, pid, out _);

    private void OnDiskRead(DiskIOTraceData e) => OnDisk(e.ProcessID, e.TransferSize, e.ElapsedTimeMSec, read: true);

    private void OnDiskWrite(DiskIOTraceData e) => OnDisk(e.ProcessID, e.TransferSize, e.ElapsedTimeMSec, read: false);

    private void OnDisk(int pid, int bytes, double latencyMs, bool read)
    {
        lock (_lock)
        {
            if (read)
                _totals.DiskReadBytes += bytes;
            else
                _totals.DiskWriteBytes += bytes;
            _totals.DiskOps++;
            _totals.DiskLatencySumMs += latencyMs;

            ref var p = ref CollectionsMarshal.GetValueRefOrAddDefault(_processes, pid, out _);
            if (read)
                p.DiskReadBytes += bytes;
            else
                p.DiskWriteBytes += bytes;
            p.DiskOps++;
            p.DiskLatencySumMs += latencyMs;
        }
    }

    private void OnNet(int pid, int bytes, bool send)
    {
        lock (_lock)
        {
            ref var p = ref CollectionsMarshal.GetValueRefOrAddDefault(_processes, pid, out _);
            if (send)
            {
                _totals.NetSendBytes += bytes;
                p.NetSendBytes += bytes;
            }
            else
            {
                _totals.NetRecvBytes += bytes;
                p.NetRecvBytes += bytes;
            }
        }
    }

    private void OnDpc(ulong routine, double ms)
    {
        var name = _drivers.Resolve(routine);
        lock (_lock)
        {
            _totals.DpcCount++;
            _totals.DpcTimeMs += ms;
            _totals.DpcMaxMs = Math.Max(_totals.DpcMaxMs, ms);
            ref var d = ref Driver(name);
            d.DpcCount++;
            d.DpcTimeMs += ms;
            d.DpcMaxMs = Math.Max(d.DpcMaxMs, ms);
        }
    }

    private void OnIsr(ulong routine, double ms)
    {
        var name = _drivers.Resolve(routine);
        lock (_lock)
        {
            _totals.IsrCount++;
            _totals.IsrTimeMs += ms;
            _totals.IsrMaxMs = Math.Max(_totals.IsrMaxMs, ms);
            ref var d = ref Driver(name);
            d.IsrCount++;
            d.IsrTimeMs += ms;
            d.IsrMaxMs = Math.Max(d.IsrMaxMs, ms);
        }
    }

    /// <summary>Accumulator of a driver (caller holds the lock).</summary>
    private ref DriverTotals Driver(string name)
    {
        if (!_driverIndex.TryGetValue(name, out var index))
        {
            index = _driverTotals.Count;
            _driverIndex[name] = index;
            _driverTotals.Add(new DriverTotals { Name = name });
        }
        return ref CollectionsMarshal.AsSpan(_driverTotals)[index];
    }

    private void AddEvent(ProcessEvent e)
    {
        lock (_lock)
        {
            _events.Enqueue(e);
            while (_events.Count > MaxEvents)
                _events.Dequeue();
        }
    }

    public void HarvestSample(out KernelEventTotals totals)
    {
        lock (_lock)
        {
            totals = _totals;
            _totals = default;
        }
    }

    public void HarvestScan(Dictionary<int, ProcessIoTotals> processes, List<DriverTotals> drivers)
    {
        processes.Clear();
        drivers.Clear();
        lock (_lock)
        {
            foreach (var (pid, totals) in _processes)
                processes[pid] = totals;
            _processes.Clear();

            foreach (var d in _driverTotals)
            {
                if (d.DpcCount > 0 || d.IsrCount > 0)
                    drivers.Add(d);
            }
            var span = CollectionsMarshal.AsSpan(_driverTotals);
            for (var i = 0; i < span.Length; i++)
                span[i] = new DriverTotals { Name = span[i].Name };
        }

        if (Stopwatch.GetElapsedTime(_lastDriverRefresh) > DriverRefresh)
        {
            _drivers.Refresh(); // Atomic snapshot swap: safe while the ETW thread resolves.
            _lastDriverRefresh = Stopwatch.GetTimestamp();
        }
    }

    public void HarvestRuntime(Dictionary<int, RuntimeTotals> processes)
    {
        processes.Clear();
        lock (_lock)
        {
            foreach (var (pid, state) in _runtimes)
            {
                processes[pid] = state.Totals;
                if (state.Exited)
                {
                    _runtimes.Remove(pid);
                    continue;
                }
                ref var r = ref CollectionsMarshal.GetValueRefOrNullRef(_runtimes, pid);
                r.Totals = new RuntimeTotals { HeapBytes = r.Totals.HeapBytes };
            }
        }
    }

    public void CopyRecentEvents(List<ProcessEvent> destination)
    {
        destination.Clear();
        lock (_lock)
            destination.AddRange(_events);
    }

    /// <summary>Stops the session (idempotent: called from several exit paths).</summary>
    public void Dispose()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
            return;
        session.Dispose(); // Stops the session; Process() returns.
        _thread?.Join(TimeSpan.FromSeconds(5));
    }
}
