using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NockMon.Core.Metrics;

namespace NockMon.Core.Sampling;

/// <summary>
/// Drive temperatures from the storage driver (IOCTL_STORAGE_QUERY_PROPERTY, StorageDeviceTemperatureProperty): NVMe
/// and most SATA drives, no extra driver, no WMI. Built in, published like a plugin's metrics (charts, values, history,
/// agents). Read on its own timer every 10 s (a drive's temperature changes slowly); drives are looked for again every
/// minute (hot plug).
/// </summary>
public sealed unsafe partial class DriveTemperatureSource : IExternalMetricSource, IDisposable
{
    public const string PluginId = "NockMon";
    public const string SourceId = "drives";

    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(10);
    private const int DiscoverEvery = 6; // Reads.
    private const int MaxDrives = 64;

    private readonly Lock _gate = new();
    private Timer? _timer;
    private int _reads;
    private List<(int Number, int Sensors)> _drives = [];
    private volatile IReadOnlyList<ExternalMetric> _metrics = [];
    private double[] _values = [];

    public string Plugin => PluginId;
    public string Source => SourceId;
    public string DisplayName => "Drives";
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
        lock (_gate) // A slow drive must not overlap two reads.
        {
            if (_timer is null)
                return;
            if (_reads++ % DiscoverEvery == 0)
                Discover();
            var values = _values;
            var i = 0;
            foreach (var (number, sensors) in _drives)
            {
                var temperatures = ReadTemperatures(number);
                for (var s = 0; s < sensors && i < values.Length; s++, i++)
                    Volatile.Write(ref values[i], temperatures is not null && s < temperatures.Length ? temperatures[s] : double.NaN);
            }
        }
    }

    /// <summary>Every physical drive reporting a temperature; a new metric list only when the drives changed.</summary>
    private void Discover()
    {
        var drives = new List<(int Number, int Sensors)>();
        var metrics = new List<ExternalMetric>();
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var number = 0; number < MaxDrives; number++)
        {
            using var drive = Open(number);
            if (drive is null)
                continue;
            if (Query(drive, StorageDeviceTemperatureProperty) is not { } data || ParseTemperatures(data) is not { Length: > 0 } temperatures)
                continue;
            var (model, serial) = Query(drive, StorageDeviceProperty) is { } device ? ParseDevice(device) : ("", "");
            if (model.Length == 0)
                model = $"Drive {number}";
            var key = serial.Length > 0 ? serial : $"drive{number}";
            var group = groups.Add(model) ? model : $"{model} ({key})"; // Two identical drives: told apart.
            for (var s = 0; s < temperatures.Length; s++)
            {
                metrics.Add(new ExternalMetric($"{key}/temperature/{s}", s == 0 ? $"{model} temperature" : $"{model} sensor {s}",
                    s == 0 ? "Temperature" : $"Sensor {s}", MetricUnit.Celsius, group));
            }
            drives.Add((number, temperatures.Length));
        }
        if (metrics.Select(m => m.Id).SequenceEqual(_metrics.Select(m => m.Id)) && drives.SequenceEqual(_drives))
            return;
        var values = new double[metrics.Count];
        Array.Fill(values, double.NaN);
        _values = values;
        _drives = drives;
        _metrics = metrics;
    }

    private static double[]? ReadTemperatures(int number)
    {
        using var drive = Open(number);
        return drive is not null && Query(drive, StorageDeviceTemperatureProperty) is { } data ? ParseTemperatures(data) : null;
    }

    /// <summary>
    /// STORAGE_TEMPERATURE_DATA_DESCRIPTOR: InfoCount (WORD) at 12, then STORAGE_TEMPERATURE_INFO entries of 16 bytes
    /// from 24, each with its temperature (SHORT, °C) at 2.
    /// </summary>
    internal static double[]? ParseTemperatures(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24)
            return null;
        var count = MemoryMarshal.Read<ushort>(data[12..]);
        if (count == 0 || data.Length < 24 + count * 16)
            return null;
        var temperatures = new double[count];
        for (var i = 0; i < count; i++)
            temperatures[i] = MemoryMarshal.Read<short>(data[(24 + i * 16 + 2)..]);
        return temperatures;
    }

    /// <summary>STORAGE_DEVICE_DESCRIPTOR: offsets of the vendor (12), product (16) and serial number (24) strings, 0 when absent.</summary>
    internal static (string Model, string Serial) ParseDevice(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28)
            return ("", "");
        var vendor = Text(data, 12);
        var product = Text(data, 16);
        var model = vendor.Length > 0 && !product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? $"{vendor} {product}" : product;
        return (model, Text(data, 24).TrimEnd('.')); // NVMe EUI-64 serials end with a dot.
    }

    /// <summary>The zero-terminated ASCII string whose offset is at <paramref name="offsetField"/>; empty when absent.</summary>
    private static string Text(ReadOnlySpan<byte> data, int offsetField)
    {
        var offset = MemoryMarshal.Read<int>(data[offsetField..]);
        if (offset <= 0 || offset >= data.Length)
            return "";
        var end = data[offset..].IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? data[offset..] : data.Slice(offset, end)).Trim();
    }

    private static SafeFileHandle? Open(int number)
    {
        // No access rights needed for the property query (FILE_ANY_ACCESS).
        var handle = CreateFileW($@"\\.\PhysicalDrive{number}", 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (!handle.IsInvalid)
            return handle;
        handle.Dispose();
        return null;
    }

    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceTemperatureProperty = 52;
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;

    /// <summary>STORAGE_PROPERTY_QUERY (PropertyStandardQuery): the property, or null when the drive does not report it.</summary>
    private static byte[]? Query(SafeFileHandle drive, int property)
    {
        var query = stackalloc int[3];
        query[0] = property;
        query[1] = 0;
        query[2] = 0;
        var buffer = new byte[1024];
        fixed (byte* output = buffer)
        {
            if (!DeviceIoControl(drive, IoctlStorageQueryProperty, query, 12, output, (uint)buffer.Length, out var returned, 0) || returned < 8)
                return null;
            return buffer[..(int)returned];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize,
        out uint returned, nint overlapped);
}
