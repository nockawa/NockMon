using System.Diagnostics;
using System.Runtime.InteropServices;
using NockMon.Core.Metrics;

namespace NockMon.Core.Sampling;

/// <summary>
/// Network traffic per adapter (physical and virtual: Wi-Fi, Ethernet, vEthernet, VPN), from the interface counters
/// (GetIfTable2): bytes received and sent per second on the wire, all protocols. Built in, published like a plugin's
/// metrics (charts, values, history, agents). Read every second on its own timer; adapters are looked for again every
/// 10 s (connected, disconnected, created). Only connected adapters, without loopback, tunnels and filter layers (which
/// repeat their adapter's traffic).
/// </summary>
public sealed unsafe partial class NetworkAdapterSource : IExternalMetricSource, IDisposable
{
    public const string PluginId = "NockMon";
    public const string SourceId = "network";

    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(1);
    private const int DiscoverEvery = 10; // Reads.

    private readonly Lock _gate = new();
    private Timer? _timer;
    private int _reads;
    private Adapter[] _adapters = [];
    private volatile IReadOnlyList<ExternalMetric> _metrics = [];
    private double[] _values = [];

    /// <summary>An adapter and its previous counters (0 timestamp: none yet).</summary>
    private sealed class Adapter(long luid)
    {
        public readonly long Luid = luid;
        public ulong In, Out;
        public long Timestamp;
    }

    public string Plugin => PluginId;
    public string Source => SourceId;
    public string DisplayName => "Network adapters";
    public IReadOnlyList<ExternalMetric> Metrics => _metrics;

    public void Start()
    {
        lock (_gate)
            _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, ReadInterval);
    }

    public bool TryRead(IReadOnlyList<ExternalMetric> metrics, Span<double> values)
    {
        if (!ReferenceEquals(metrics, _metrics))
            return false;
        var current = _values;
        for (var i = 0; i < values.Length && i < current.Length; i++)
            values[i] = Volatile.Read(ref current[i]);
        return true;
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (_timer is null)
                return;
            if (GetIfTable2(out var table) != 0)
                return;
            try
            {
                var count = *(uint*)table;
                var rows = (byte*)table + 8;
                if (_reads++ % DiscoverEvery == 0)
                    Discover(rows, count);
                var values = _values;
                var now = Stopwatch.GetTimestamp();
                for (var a = 0; a < _adapters.Length; a++)
                {
                    var adapter = _adapters[a];
                    var row = Find(rows, count, adapter.Luid);
                    if (row is null)
                    {
                        adapter.Timestamp = 0;
                        Volatile.Write(ref values[2 * a], double.NaN);
                        Volatile.Write(ref values[2 * a + 1], double.NaN);
                        continue;
                    }
                    var (inOctets, outOctets) = (*(ulong*)(row + InOctetsOffset), *(ulong*)(row + OutOctetsOffset));
                    if (adapter.Timestamp != 0 && Stopwatch.GetElapsedTime(adapter.Timestamp, now).TotalSeconds is var seconds and > 0)
                    {
                        // A reset counter (adapter restarted) reads as no traffic, not as a huge negative.
                        Volatile.Write(ref values[2 * a], inOctets >= adapter.In ? (inOctets - adapter.In) / seconds : 0);
                        Volatile.Write(ref values[2 * a + 1], outOctets >= adapter.Out ? (outOctets - adapter.Out) / seconds : 0);
                    }
                    (adapter.In, adapter.Out, adapter.Timestamp) = (inOctets, outOctets, now);
                }
            }
            finally
            {
                FreeMibTable(table);
            }
        }
    }

    /// <summary>The adapters worth publishing; a new metric list only when they changed (counters kept for those that stay).</summary>
    private void Discover(byte* rows, uint count)
    {
        var adapters = new List<Adapter>();
        var metrics = new List<ExternalMetric>();
        for (var i = 0; i < count; i++)
        {
            var row = rows + i * (long)RowSize;
            if (!IsPublished(*(uint*)(row + TypeOffset), row[FlagsOffset], *(uint*)(row + OperStatusOffset)))
                continue;
            var luid = *(long*)row;
            var guid = *(Guid*)(row + GuidOffset);
            var alias = new string((char*)(row + AliasOffset));
            if (alias.Length == 0)
                alias = new string((char*)(row + DescriptionOffset));
            var key = guid.ToString("D");
            metrics.Add(new ExternalMetric($"{key}/receive", $"{alias} receive", "Receive", MetricUnit.BytesPerSecond, alias));
            metrics.Add(new ExternalMetric($"{key}/send", $"{alias} send", "Send", MetricUnit.BytesPerSecond, alias));
            adapters.Add(Array.Find(_adapters, a => a.Luid == luid) ?? new Adapter(luid));
        }
        if (metrics.Select(m => (m.Id, m.Name)).SequenceEqual(_metrics.Select(m => (m.Id, m.Name))))
            return;
        var values = new double[metrics.Count];
        Array.Fill(values, double.NaN);
        _values = values;
        _adapters = [.. adapters];
        _metrics = metrics;
    }

    /// <summary>Connected (operational status up), not loopback (24) nor tunnel (131), not a filter layer (flags bit 1).</summary>
    internal static bool IsPublished(uint type, byte flags, uint operStatus) =>
        operStatus == 1 && type is not (24 or 131) && (flags & 2) == 0;

    private static byte* Find(byte* rows, uint count, long luid)
    {
        for (var i = 0; i < count; i++)
        {
            var row = rows + i * (long)RowSize;
            if (*(long*)row == luid)
                return row;
        }
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    // MIB_IF_ROW2 (x64): InterfaceLuid @0, InterfaceGuid @12, Alias WCHAR[257] @28, Description WCHAR[257] @542, Type @1128,
    // InterfaceAndOperStatusFlags (bit fields in one byte) @1152, OperStatus @1156, InOctets @1208, OutOctets @1280; 1352 bytes.
    // MIB_IF_TABLE2: NumEntries, then the rows from 8.
    internal const int RowSize = 1352, GuidOffset = 12, AliasOffset = 28, DescriptionOffset = 542, TypeOffset = 1128, FlagsOffset = 1152,
        OperStatusOffset = 1156, InOctetsOffset = 1208, OutOctetsOffset = 1280;

    [LibraryImport("iphlpapi.dll")]
    internal static partial uint GetIfTable2(out nint table);

    [LibraryImport("iphlpapi.dll")]
    internal static partial void FreeMibTable(nint memory);
}
