using System.Runtime.InteropServices;
using System.Text;
using NockMon.Core.Native;

namespace NockMon.Core.Etw;

/// <summary>
/// Kernel address → loaded driver image (SystemModuleInformation: base, size and name of each module; real addresses
/// need elevation). Sorted bases, binary search, and the address must fall inside the image. Refreshed on a schedule
/// (newly loaded drivers) by swapping one immutable snapshot, so resolving from another thread never mixes two lists;
/// an unchanged module list keeps the current snapshot.
/// </summary>
public sealed unsafe class DriverMap
{
    private const int SystemModuleInformation = 11;
    private const int ModuleSize = 296; // RTL_PROCESS_MODULE_INFORMATION on x64.
    private const int ImageBaseOffset = 16, ImageSizeOffset = 24, FileNameOffsetOffset = 38, FullPathOffset = 40, FullPathLength = 256;

    internal sealed record Snapshot(ulong[] Bases, uint[] Sizes, string[] Names);

    private volatile Snapshot _snapshot = new([], [], []);

    public int Count => _snapshot.Bases.Length;

    internal Snapshot Current => _snapshot;

    public void Refresh()
    {
        uint needed = 0;
        NtDll.NtQuerySystemInformation(SystemModuleInformation, null, 0, &needed);
        if (needed == 0)
            return;
        var length = needed + 16 * ModuleSize; // Drivers may load between the two calls.
        var buffer = (byte*)NativeMemory.Alloc(length);
        try
        {
            if (NtDll.NtQuerySystemInformation(SystemModuleInformation, buffer, length, &needed) < 0)
                return;
            var count = (int)Math.Min(*(uint*)buffer, (length - 8) / ModuleSize);
            var modules = buffer + 8; // ULONG count, then pointer-aligned entries.
            var current = _snapshot;
            if (Unchanged(current, modules, count))
                return;

            var entries = new (ulong Base, uint Size, string Name)[count];
            for (var i = 0; i < count; i++)
            {
                var m = modules + i * ModuleSize;
                var path = new ReadOnlySpan<byte>(m + FullPathOffset, FullPathLength);
                var end = path.IndexOf((byte)0);
                var start = Math.Min(*(ushort*)(m + FileNameOffsetOffset), (ushort)(end < 0 ? FullPathLength : end));
                var name = Encoding.Latin1.GetString(path[start..(end < 0 ? FullPathLength : end)]);
                entries[i] = (*(ulong*)(m + ImageBaseOffset), *(uint*)(m + ImageSizeOffset), name);
            }
            Array.Sort(entries, (a, b) => a.Base.CompareTo(b.Base));
            _snapshot = new Snapshot(entries.Select(e => e.Base).ToArray(), entries.Select(e => e.Size).ToArray(), entries.Select(e => e.Name).ToArray());
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>Same bases as the current snapshot (any order): nothing to rebuild.</summary>
    private static bool Unchanged(Snapshot current, byte* modules, int count)
    {
        if (count != current.Bases.Length)
            return false;
        for (var i = 0; i < count; i++)
        {
            if (Array.BinarySearch(current.Bases, *(ulong*)(modules + i * ModuleSize + ImageBaseOffset)) < 0)
                return false;
        }
        return true;
    }

    /// <summary>Driver whose image contains <paramref name="address"/>, or "unknown".</summary>
    public string Resolve(ulong address)
    {
        var snapshot = _snapshot;
        var index = Array.BinarySearch(snapshot.Bases, address);
        if (index < 0)
            index = ~index - 1;
        return index >= 0 && address - snapshot.Bases[index] < snapshot.Sizes[index] ? snapshot.Names[index] : "unknown";
    }
}
