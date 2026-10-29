using System.Runtime.InteropServices;

namespace NockMon.Core.Gpu;

/// <summary>
/// NVIDIA Management Library, loaded from the display driver's DriverStore folder (System32\nvml.dll is only a loader
/// without the API exports). Function pointers resolved once; absent on machines without an NVIDIA driver.
/// ~40 µs per device call; per-process utilization ~0.7 ms.
/// </summary>
internal sealed unsafe class Nvml : IDisposable
{
    private readonly nint _library;
    private readonly delegate* unmanaged<int> _shutdown;
    private readonly delegate* unmanaged<uint*, int> _getCount;
    private readonly delegate* unmanaged<uint, nint*, int> _getHandle;
    private readonly delegate* unmanaged<nint, byte*, uint, int> _getName;
    private readonly delegate* unmanaged<nint, uint*, int> _getUtilization;
    private readonly delegate* unmanaged<nint, uint*, uint*, int> _getEncoder;
    private readonly delegate* unmanaged<nint, uint*, uint*, int> _getDecoder;
    private readonly delegate* unmanaged<nint, int, uint*, int> _getTemperature;
    private readonly delegate* unmanaged<nint, uint*, int> _getPower;
    private readonly delegate* unmanaged<nint, uint*, int> _getFan;
    private readonly delegate* unmanaged<nint, int, uint*, int> _getClock;
    private readonly delegate* unmanaged<nint, byte*, uint*, ulong, int> _getProcessUtilization;

    private Nvml(nint library)
    {
        _library = library;
        nint Get(string name) => NativeLibrary.GetExport(library, name);
        _shutdown = (delegate* unmanaged<int>)Get("nvmlShutdown");
        _getCount = (delegate* unmanaged<uint*, int>)Get("nvmlDeviceGetCount_v2");
        _getHandle = (delegate* unmanaged<uint, nint*, int>)Get("nvmlDeviceGetHandleByIndex_v2");
        _getName = (delegate* unmanaged<nint, byte*, uint, int>)Get("nvmlDeviceGetName");
        _getUtilization = (delegate* unmanaged<nint, uint*, int>)Get("nvmlDeviceGetUtilizationRates");
        _getEncoder = (delegate* unmanaged<nint, uint*, uint*, int>)Get("nvmlDeviceGetEncoderUtilization");
        _getDecoder = (delegate* unmanaged<nint, uint*, uint*, int>)Get("nvmlDeviceGetDecoderUtilization");
        _getTemperature = (delegate* unmanaged<nint, int, uint*, int>)Get("nvmlDeviceGetTemperature");
        _getPower = (delegate* unmanaged<nint, uint*, int>)Get("nvmlDeviceGetPowerUsage");
        _getFan = (delegate* unmanaged<nint, uint*, int>)Get("nvmlDeviceGetFanSpeed");
        _getClock = (delegate* unmanaged<nint, int, uint*, int>)Get("nvmlDeviceGetClockInfo");
        _getProcessUtilization = (delegate* unmanaged<nint, byte*, uint*, ulong, int>)Get("nvmlDeviceGetProcessUtilization");
    }

    /// <summary>Loads and initializes NVML, or null when unavailable.</summary>
    public static Nvml? TryLoad()
    {
        try
        {
            var repository = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
            var path = Directory.Exists(repository)
                ? Directory.EnumerateDirectories(repository, "nv*dispi*")
                    .Select(d => Path.Combine(d, "nvml.dll"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (path is null || !NativeLibrary.TryLoad(path, out var library))
                return null;
            var init = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "nvmlInit_v2");
            if (init() != 0)
            {
                NativeLibrary.Free(library);
                return null;
            }
            return new Nvml(library);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public List<(nint Device, string Name)> Devices()
    {
        uint count;
        var devices = new List<(nint, string)>();
        if (_getCount(&count) != 0)
            return devices;
        var name = stackalloc byte[96];
        for (uint i = 0; i < count; i++)
        {
            nint device;
            if (_getHandle(i, &device) == 0 && _getName(device, name, 96) == 0)
                devices.Add((device, Marshal.PtrToStringAnsi((nint)name) ?? $"NVIDIA {i}"));
        }
        return devices;
    }

    public readonly record struct DeviceSample(double Gpu, double MemoryController, double Encoder, double Decoder,
        double TemperatureC, double PowerW, double FanPercent, double CoreMhz, double MemoryMhz);

    public DeviceSample Sample(nint device)
    {
        uint* u = stackalloc uint[2];
        uint encoder = 0, decoder = 0, period = 0, temperature = 0, power = 0, fan = 0, core = 0, memory = 0;
        var ok = _getUtilization(device, u) == 0;
        var encOk = _getEncoder(device, &encoder, &period) == 0;
        var decOk = _getDecoder(device, &decoder, &period) == 0;
        var tempOk = _getTemperature(device, 0, &temperature) == 0;
        var powerOk = _getPower(device, &power) == 0;
        var fanOk = _getFan(device, &fan) == 0;
        var coreOk = _getClock(device, 1, &core) == 0; // NVML_CLOCK_SM
        var memOk = _getClock(device, 2, &memory) == 0; // NVML_CLOCK_MEM
        return new DeviceSample(
            ok ? u[0] : double.NaN, ok ? u[1] : double.NaN, encOk ? encoder : double.NaN, decOk ? decoder : double.NaN,
            tempOk ? temperature : double.NaN, powerOk ? power / 1000.0 : double.NaN, fanOk ? fan : double.NaN,
            coreOk ? core : double.NaN, memOk ? memory : double.NaN);
    }

    /// <summary>Per-process SM utilization (%) sampled since <paramref name="sinceMicroseconds"/> (Unix µs); max per PID.</summary>
    public void ProcessUtilization(nint device, ulong sinceMicroseconds, Dictionary<int, double> result)
    {
        const int sampleSize = 32; // nvmlProcessUtilizationSample_t: pid, timeStamp(8), sm, mem, enc, dec.
        uint count = 0;
        _getProcessUtilization(device, null, &count, sinceMicroseconds);
        if (count == 0)
            return;
        count += 8;
        var buffer = stackalloc byte[(int)count * sampleSize];
        if (_getProcessUtilization(device, buffer, &count, sinceMicroseconds) != 0)
            return;
        for (var i = 0; i < count; i++)
        {
            var sample = buffer + i * sampleSize;
            var pid = (int)*(uint*)sample;
            var sm = *(uint*)(sample + 16);
            result[pid] = Math.Max(result.GetValueOrDefault(pid), sm);
        }
    }

    public void Dispose()
    {
        _shutdown();
        NativeLibrary.Free(_library);
    }
}
