using System.Runtime.InteropServices;
using NockMon.Core.Settings;

namespace NockMon.Shell;

/// <summary>
/// Saves/restores a window through GET/SetWindowPlacement: the restored rectangle and the maximized state are kept
/// separately (un-maximizing returns to the saved size), in physical workspace coordinates (per-monitor DPI safe).
/// A placement no longer on any monitor (display unplugged) is ignored.
/// </summary>
internal static partial class WindowPlacement
{
    private const uint SW_SHOWNORMAL = 1;
    private const uint SW_SHOWMINIMIZED = 2;
    private const uint SW_SHOWMAXIMIZED = 3;
    private const uint WPF_RESTORETOMAXIMIZED = 0x2;
    private const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    public static WindowPlacementSettings? Capture(nint hwnd)
    {
        var p = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(hwnd, ref p))
            return null;
        var r = p.NormalPosition;
        return new WindowPlacementSettings
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Right,
            Bottom = r.Bottom,
            // Minimized at close: reopen in the state it would restore to.
            Maximized = p.ShowCmd == SW_SHOWMAXIMIZED || (p.ShowCmd == SW_SHOWMINIMIZED && (p.Flags & WPF_RESTORETOMAXIMIZED) != 0),
        };
    }

    /// <returns>false if the placement is invalid or off every monitor (the caller applies its default).</returns>
    public static bool TryRestore(nint hwnd, WindowPlacementSettings? settings)
    {
        if (settings is not { IsValid: true })
            return false;
        var rect = new Rect { Left = settings.Left, Top = settings.Top, Right = settings.Right, Bottom = settings.Bottom };
        if (MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL) == 0)
            return false;

        var p = new Placement
        {
            Length = (uint)Marshal.SizeOf<Placement>(),
            ShowCmd = settings.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL,
            NormalPosition = rect,
        };
        return SetWindowPlacement(hwnd, ref p);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint hwnd, ref Placement placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint hwnd, ref Placement placement);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(ref Rect rect, uint flags);
}
