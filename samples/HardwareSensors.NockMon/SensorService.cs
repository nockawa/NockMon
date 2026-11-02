using System.Diagnostics;
using System.Text;
using LibreHardwareMonitor.Hardware;
using NockMon.Sdk;

namespace HardwareSensors.NockMon;

/// <summary>A sensor as the plugin publishes it, and where it comes from.</summary>
internal sealed record Sensor(PluginMetric Metric, SensorType Type, int Hardware, ISensor Source);

/// <summary>A piece of hardware read on its own schedule, and what one read costs.</summary>
internal sealed class HardwareReader(IHardware hardware, TimeSpan interval)
{
    public IHardware Hardware { get; } = hardware;
    public TimeSpan Interval { get; } = interval;
    public long Due;
    public double LastCostMs;
}

/// <summary>
/// Reads the sensors on its own background thread, shared by the metric source and every widget. Opening the library
/// takes seconds (hardware detection), and one read of everything about 100 ms, so each piece of hardware is read on its
/// own interval (<see cref="IntervalOf"/>): the CPU and motherboard every second, GPUs every 2 to 5 s, memory every
/// 10 s. Read-only: no fan control.
/// </summary>
internal static class SensorService
{
    private static readonly Lock Gate = new();
    private static Thread? _thread;
    private static volatile bool _stopping;
    private static volatile IReadOnlyList<Sensor> _sensors = [];
    private static volatile IReadOnlyList<PluginMetric> _metrics = [];
    private static volatile IReadOnlyList<HardwareReader> _readers = [];
    private static double[] _values = [];

    /// <summary>Null while fine; otherwise what limits the readings (no PawnIO driver, the library failed).</summary>
    public static string? Status { get; private set; } = "Detecting the hardware…";

    public static bool IsReady { get; private set; }

    /// <summary>The published sensors: the same list instance until the hardware changes.</summary>
    public static IReadOnlyList<PluginMetric> Metrics => _metrics;

    public static IReadOnlyList<Sensor> Sensors => _sensors;

    public static IReadOnlyList<HardwareReader> Readers => _readers;

    /// <summary>Latest value of a sensor (index in <see cref="Sensors"/>), NaN when unknown.</summary>
    public static double Value(int index)
    {
        var values = _values;
        return (uint)index < (uint)values.Length ? Volatile.Read(ref values[index]) : double.NaN;
    }

    public static bool TryRead(IReadOnlyList<PluginMetric> metrics, Span<double> values)
    {
        if (!ReferenceEquals(metrics, _metrics))
            return false;
        var current = _values;
        for (var i = 0; i < values.Length && i < current.Length; i++)
            values[i] = Volatile.Read(ref current[i]); // Doubles are written whole: no torn value.
        return true;
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_thread is not null)
                return;
            _stopping = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "Hardware sensors", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    public static void Stop()
    {
        Thread? thread;
        lock (Gate)
        {
            thread = _thread;
            _thread = null;
            _stopping = true;
        }
        thread?.Join(TimeSpan.FromSeconds(5));
    }

    private static void Run()
    {
        var computer = new Computer
        {
            // No drives: NockMon reads their temperatures itself (storage driver, no PawnIO).
            IsCpuEnabled = true, IsMotherboardEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true,
            IsControllerEnabled = true, IsPsuEnabled = true, IsBatteryEnabled = true,
        };
        try
        {
            computer.Open();
            Discover(computer);
            Status = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled
                ? null
                : "The PawnIO driver is not installed: CPU and motherboard sensors are missing. Install it (winget install namazso.PawnIO), then restart NockMon.";
            IsReady = true;
            while (!_stopping)
            {
                Poll();
                Thread.Sleep(250);
            }
        }
        catch (Exception e)
        {
            Status = $"Hardware sensors failed: {e.Message}";
            IsReady = true;
        }
        finally
        {
            try
            {
                computer.Close();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Closing the sensors failed: {e.Message}");
            }
        }
    }

    /// <summary>Every piece of hardware with sensors worth publishing, and those sensors.</summary>
    private static void Discover(Computer computer)
    {
        var readers = new List<HardwareReader>();
        var sensors = new List<Sensor>();
        void Visit(IHardware hardware)
        {
            hardware.Update(); // First read: the sensors and their values appear.
            var kept = hardware.Sensors.Where(s => Keep(s) && !(hardware.HardwareType == HardwareType.GpuNvidia && FromNvml(s))).ToList();
            if (kept.Count > 0)
            {
                var index = readers.Count;
                readers.Add(new HardwareReader(hardware, IntervalOf(hardware.HardwareType)));
                var group = Clean(hardware.Name);
                var prefix = ShortName(hardware);
                foreach (var s in kept)
                {
                    var name = Clean(s.Name);
                    sensors.Add(new Sensor(
                        new PluginMetric(s.Identifier.ToString(), prefix.Length == 0 ? name : $"{prefix} {name}", name, UnitOf(s.SensorType), group),
                        s.SensorType, index, s));
                }
            }
            foreach (var sub in hardware.SubHardware)
                Visit(sub);
        }
        foreach (var hardware in computer.Hardware)
            Visit(hardware);

        _values = new double[sensors.Count];
        Array.Fill(_values, double.NaN);
        _readers = readers;
        _sensors = sensors;
        _metrics = sensors.Select(s => s.Metric).ToArray();
        Publish();
    }

    /// <summary>Reads the hardware whose interval elapsed (each timed), then publishes their sensors' values.</summary>
    private static void Poll()
    {
        var now = Stopwatch.GetTimestamp();
        var readers = _readers;
        for (var i = 0; i < readers.Count; i++)
        {
            var reader = readers[i];
            if (reader.Due > now)
                continue;
            var start = Stopwatch.GetTimestamp();
            try
            {
                reader.Hardware.Update();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"{reader.Hardware.Name}: {e.Message}");
            }
            reader.LastCostMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            reader.Due = now + (long)(reader.Interval.TotalSeconds * Stopwatch.Frequency);
        }
        Publish();
    }

    private static void Publish()
    {
        var sensors = _sensors;
        var values = _values;
        for (var i = 0; i < sensors.Count && i < values.Length; i++)
            Volatile.Write(ref values[i], sensors[i].Source.Value is { } v ? v : double.NaN);
    }

    /// <summary>
    /// The quantities NockMon does not already measure (no loads, clocks, data or throughput), minus constants reported
    /// as sensors (memory and drive temperature limits).
    /// </summary>
    private static bool Keep(ISensor sensor)
    {
        if (sensor.SensorType is not (SensorType.Temperature or SensorType.Fan or SensorType.Control or SensorType.Voltage
            or SensorType.Current or SensorType.Power))
            return false;
        var name = sensor.Name;
        return !(sensor.SensorType == SensorType.Temperature &&
                 (name.Contains("Limit", StringComparison.OrdinalIgnoreCase) || name.Contains("Resolution", StringComparison.OrdinalIgnoreCase) ||
                  name.Equals("Warning Temperature", StringComparison.OrdinalIgnoreCase) ||
                  name.Equals("Critical Temperature", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// How often each kind of hardware is read. Measured on a Ryzen 9 7950X with an RTX 4070 Ti (median of one read): CPU
    /// 4 ms, motherboard chip 2 ms, AMD iGPU 6 ms, NVMe drive 3 ms, DDR5 module 1 ms, but NVIDIA GPU 80 ms (NVAPI): read
    /// every second, it alone would take 8 % of a core.
    /// </summary>
    /// <summary>
    /// What NockMon itself reads from NVIDIA GPUs through NVML (GPU temperature, fan speed and duty, board power): not
    /// published twice. NVML lacks the hot spot and memory junction temperatures and the core voltage: those stay.
    /// </summary>
    private static bool FromNvml(ISensor sensor) =>
        sensor.SensorType is SensorType.Fan or SensorType.Control or SensorType.Power ||
        (sensor.SensorType == SensorType.Temperature && sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase));

    private static TimeSpan IntervalOf(HardwareType type) => type switch
    {
        HardwareType.Cpu or HardwareType.Motherboard or HardwareType.SuperIO => TimeSpan.FromSeconds(1),
        HardwareType.GpuAmd or HardwareType.GpuIntel => TimeSpan.FromSeconds(2),
        HardwareType.GpuNvidia => TimeSpan.FromSeconds(5),
        HardwareType.Memory => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(5),
    };

    private static PluginMetricUnit UnitOf(SensorType type) => type switch
    {
        SensorType.Temperature => PluginMetricUnit.Celsius,
        SensorType.Fan => PluginMetricUnit.Rpm,
        SensorType.Control => PluginMetricUnit.Percent,
        SensorType.Voltage => PluginMetricUnit.Volts,
        SensorType.Current => PluginMetricUnit.Amperes,
        SensorType.Power => PluginMetricUnit.Watts,
        _ => PluginMetricUnit.Count,
    };

    /// <summary>The prefix of a sensor's full name: the hardware without vendor words ("RTX 4070 Ti", "NCT6799D"); none for memory modules.</summary>
    private static string ShortName(IHardware hardware)
    {
        if (hardware.HardwareType == HardwareType.Memory)
            return ""; // Its sensors are named "DIMM #1" already; the module name can be unreadable.
        var name = Clean(hardware.Name);
        foreach (var vendor in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon(TM) ", "AMD ", "Intel(R) ", "Intel ", "Nuvoton ", "ITE " })
        {
            if (name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
                return name[vendor.Length..];
        }
        return name;
    }

    /// <summary>Printable characters only, spaces collapsed (some modules report garbage in their names).</summary>
    internal static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c) || c == '�' || (c is >= '\u0080' and < ' '))
                continue;
            if (char.IsWhiteSpace(c) && (builder.Length == 0 || builder[^1] == ' '))
                continue;
            builder.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }
        return builder.ToString().Trim();
    }
}
