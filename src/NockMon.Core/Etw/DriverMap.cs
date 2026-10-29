using System.Runtime.InteropServices;

namespace NockMon.Core.Etw;

/// <summary>
/// Kernel address → loaded driver image (EnumDeviceDrivers; real addresses need elevation). Sorted bases, binary
/// search. Refreshed on a schedule (newly loaded drivers) by swapping one immutable snapshot, so resolving from another
/// thread never mixes two lists.
/// </summary>
public sealed unsafe partial class DriverMap
{
    private sealed record Snapshot(ulong[] Bases, string[] Names);

    private volatile Snapshot _snapshot = new([], []);

    public int Count => _snapshot.Bases.Length;

    public void Refresh()
    {
        uint needed;
        EnumDeviceDrivers(null, 0, &needed);
        var bases = new nint[needed / (uint)sizeof(nint) + 64];
        fixed (nint* p = bases)
        {
            if (!EnumDeviceDrivers(p, (uint)(bases.Length * sizeof(nint)), &needed))
                return;
        }
        var count = (int)Math.Min(needed / (uint)sizeof(nint), (uint)bases.Length);
        var entries = new List<(ulong Base, string Name)>(count);
        var name = stackalloc char[260];
        for (var i = 0; i < count; i++)
        {
            if (bases[i] == 0)
                continue;
            var length = GetDeviceDriverBaseNameW(bases[i], name, 260);
            entries.Add(((ulong)bases[i], length > 0 ? new string(name, 0, (int)length) : $"0x{(ulong)bases[i]:X}"));
        }
        entries.Sort((a, b) => a.Base.CompareTo(b.Base));
        _snapshot = new Snapshot(entries.Select(e => e.Base).ToArray(), entries.Select(e => e.Name).ToArray());
    }

    /// <summary>Driver containing <paramref name="address"/> (the closest base at or below it), or "unknown".</summary>
    public string Resolve(ulong address)
    {
        var snapshot = _snapshot;
        var index = Array.BinarySearch(snapshot.Bases, address);
        if (index < 0)
            index = ~index - 1;
        return index >= 0 ? snapshot.Names[index] : "unknown";
    }

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDeviceDrivers(nint* imageBases, uint cb, uint* needed);

    [LibraryImport("psapi.dll")]
    private static partial uint GetDeviceDriverBaseNameW(nint imageBase, char* baseName, uint size);
}
