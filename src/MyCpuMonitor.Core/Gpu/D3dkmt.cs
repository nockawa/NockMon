using System.Runtime.InteropServices;

namespace MyCpuMonitor.Core.Gpu;

/// <summary>
/// D3DKMT (graphics kernel) statistics, the source Task Manager reads through PDH. Layouts validated on this machine:
/// D3DKMT_QUERYSTATISTICS is 0x328 bytes (type @0, adapter LUID @4, result union @24, query id @0x320); query types
/// ADAPTER=0, SEGMENT=3, NODE=5. Adapter and segment queries cost ~0.3 µs; a NODE (engine) query ~300 µs, so engines
/// are only queried when used.
/// </summary>
internal static unsafe partial class D3dkmt
{
    public const int QueryAdapter = 0, QuerySegment = 3, QueryNode = 5;
    private const int StatisticsSize = 0x328, ResultOffset = 24, QueryIdOffset = 0x320;
    private const int AdapterRegistryInfo = 8, AdapterPerfData = 62;

    public readonly record struct Adapter(uint Handle, long Luid);

    public static List<Adapter> EnumerateAdapters()
    {
        var header = stackalloc byte[16];
        new Span<byte>(header, 16).Clear();
        if (D3DKMTEnumAdapters2(header) < 0)
            return [];
        var count = *(uint*)header;
        var infos = stackalloc byte[(int)count * 20]; // D3DKMT_ADAPTERINFO
        *(nint*)(header + 8) = (nint)infos;
        if (D3DKMTEnumAdapters2(header) < 0)
            return [];
        var adapters = new List<Adapter>();
        for (var i = 0; i < *(uint*)header; i++)
            adapters.Add(new Adapter(*(uint*)(infos + i * 20), *(long*)(infos + i * 20 + 4)));
        return adapters;
    }

    public static void Close(uint handle) => D3DKMTCloseAdapter(&handle);

    /// <summary>Adapter description ("NVIDIA GeForce RTX 4070 Ti"), or null.</summary>
    public static string? Name(uint handle)
    {
        const int size = 4 * 260 * sizeof(char); // D3DKMT_ADAPTERREGISTRYINFO: four WCHAR[260].
        var buffer = stackalloc byte[size];
        return QueryAdapterInfo(handle, AdapterRegistryInfo, buffer, size) >= 0 ? new string((char*)buffer) : null;
    }

    public static (uint Segments, uint Nodes) Counts(long luid)
    {
        var q = stackalloc byte[StatisticsSize];
        return Query(q, QueryAdapter, luid, 0) ? (*(uint*)(q + ResultOffset), *(uint*)(q + ResultOffset + 4)) : (0, 0);
    }

    /// <summary>D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION: CommitLimit, BytesCommitted, BytesResident, ..., Aperture @+40.</summary>
    public static bool Segment(long luid, uint segment, out ulong limit, out ulong resident, out bool aperture)
    {
        var q = stackalloc byte[StatisticsSize];
        if (!Query(q, QuerySegment, luid, segment))
        {
            (limit, resident, aperture) = (0, 0, false);
            return false;
        }
        limit = *(ulong*)(q + ResultOffset);
        resident = *(ulong*)(q + ResultOffset + 16);
        aperture = *(uint*)(q + ResultOffset + 40) != 0;
        return true;
    }

    /// <summary>Engine running time, 100 ns units (D3DKMT_QUERYSTATISTICS_NODE_INFORMATION.GlobalInformation.RunningTime).</summary>
    public static long NodeRunningTime(long luid, uint node)
    {
        var q = stackalloc byte[StatisticsSize];
        return Query(q, QueryNode, luid, node) ? *(long*)(q + ResultOffset) : -1;
    }

    /// <summary>D3DKMT_ADAPTER_PERFDATA (WDDM 2.4+): memory clock (Hz), fan (rpm), power (‰), temperature (deci-°C).</summary>
    public static bool PerfData(uint handle, out double memoryMhz, out double fanRpm, out double powerPercent, out double temperatureC)
    {
        var data = stackalloc byte[64];
        new Span<byte>(data, 64).Clear();
        if (QueryAdapterInfo(handle, AdapterPerfData, data, 64) < 0)
        {
            (memoryMhz, fanRpm, powerPercent, temperatureC) = (double.NaN, double.NaN, double.NaN, double.NaN);
            return false;
        }
        memoryMhz = *(ulong*)(data + 8) / 1e6;
        fanRpm = *(uint*)(data + 48);
        powerPercent = *(uint*)(data + 52) / 10.0;
        temperatureC = *(uint*)(data + 56) / 10.0;
        return true;
    }

    private static bool Query(byte* q, int type, long luid, uint id)
    {
        new Span<byte>(q, StatisticsSize).Clear();
        *(int*)q = type;
        *(long*)(q + 4) = luid;
        *(uint*)(q + QueryIdOffset) = id;
        return D3DKMTQueryStatistics(q) >= 0;
    }

    private static int QueryAdapterInfo(uint handle, int type, void* data, uint size)
    {
        var q = stackalloc byte[24]; // D3DKMT_QUERYADAPTERINFO
        *(uint*)q = handle;
        *(int*)(q + 4) = type;
        *(nint*)(q + 8) = (nint)data;
        *(uint*)(q + 16) = size;
        return D3DKMTQueryAdapterInfo(q);
    }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTEnumAdapters2(void* enumAdapters);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(void* closeAdapter);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryStatistics(void* statistics);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(void* queryAdapterInfo);
}

/// <summary>
/// Engine type names per adapter LUID and engine index, from the PDH "GPU Engine" instance names
/// (…_luid_0xHIGH_0xLOW_phys_0_eng_N_engtype_NAME). One-time listing (~0.1–1 s): run off the UI and sampler threads.
/// </summary>
internal static unsafe partial class GpuEngineNames
{
    private const uint PdhMoreData = 0x800007D2, PerfDetailWizard = 400;

    public static Dictionary<(long Luid, int Engine), string> Load()
    {
        var names = new Dictionary<(long, int), string>();
        uint counterLength = 0, instanceLength = 0;
        if (PdhEnumObjectItemsW(null, null, "GPU Engine", null, ref counterLength, null, ref instanceLength, PerfDetailWizard, 0) != PdhMoreData)
            return names;
        var counters = new char[counterLength];
        var instances = new char[instanceLength];
        fixed (char* c = counters)
        fixed (char* i = instances)
        {
            if (PdhEnumObjectItemsW(null, null, "GPU Engine", c, ref counterLength, i, ref instanceLength, PerfDetailWizard, 0) != 0)
                return names;
        }
        foreach (var instance in new string(instances).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParse(instance, out var luid, out var engine, out var type))
                names.TryAdd((luid, engine), type);
        }
        return names;
    }

    internal static bool TryParse(string instance, out long luid, out int engine, out string type)
    {
        (luid, engine, type) = (0, 0, "");
        var l = instance.IndexOf("_luid_0x", StringComparison.Ordinal);
        var e = instance.IndexOf("_eng_", StringComparison.Ordinal);
        var t = instance.IndexOf("_engtype_", StringComparison.Ordinal);
        if (l < 0 || e < 0 || t < 0)
            return false;
        var parts = instance.Substring(l + 6, 21).Split('_'); // 0xHHHHHHHH_0xLLLLLLLL
        if (parts.Length != 2
            || !uint.TryParse(parts[0].AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var high)
            || !uint.TryParse(parts[1].AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var low)
            || !int.TryParse(instance.AsSpan(e + 5, t - e - 5), out engine))
            return false;
        luid = ((long)high << 32) | low;
        type = instance[(t + 9)..].Replace('_', ' ').Trim();
        return true;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhEnumObjectItemsW(string? dataSource, string? machine, string objectName, char* counterList, ref uint counterLength,
        char* instanceList, ref uint instanceLength, uint detailLevel, uint flags);
}
