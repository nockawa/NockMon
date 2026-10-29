using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace NockMon.Core.Etw;

/// <summary>
/// Real-time kernel ETW session under a private name (Windows 10+ system logger mode): no conflict with xperf,
/// Process Monitor or other tools using "NT Kernel Logger". Disk I/O (with response time), TCP/UDP, DPC/ISR (with
/// duration), process start/exit. Callbacks run on a dedicated thread and only add into accumulators under a short
/// lock; the sampler harvests (and resets) them. A stale session left by a crash is stopped at start.
/// No ImageLoad keyword: it triggers a rundown of every module of every process; drivers come from EnumDeviceDrivers.
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
    private readonly Queue<ProcessEvent> _events = new();
    private KernelEventTotals _totals;
    private TraceEventSession? _session;
    private Thread? _thread;
    private long _lastDriverRefresh;

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
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
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

            _session = new TraceEventSession(_sessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.NetworkTCPIP
                | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.DeferedProcedureCalls
                | KernelTraceEventParser.Keywords.Interrupt);
            Subscribe(_session.Source.Kernel);
            _thread = new Thread(ProcessEvents) { IsBackground = true, Name = "NockMon ETW" };
            _thread.Start(_session);
            Status = null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
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
        k.ProcessStop += e => AddEvent(new ProcessEvent(e.TimeStamp.ToUniversalTime(), ProcessEventKind.Exit, e.ProcessID, e.ParentID,
            e.ImageFileName, null, e.ExitStatus));
    }

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

            ref var p = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_processes, pid, out _);
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
            ref var p = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_processes, pid, out _);
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
        return ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_driverTotals)[index];
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
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_driverTotals);
            for (var i = 0; i < span.Length; i++)
                span[i] = new DriverTotals { Name = span[i].Name };
        }

        if (Stopwatch.GetElapsedTime(_lastDriverRefresh) > DriverRefresh)
        {
            _drivers.Refresh(); // Atomic snapshot swap: safe while the ETW thread resolves.
            _lastDriverRefresh = Stopwatch.GetTimestamp();
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
