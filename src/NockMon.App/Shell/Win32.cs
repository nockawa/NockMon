using System.Runtime.InteropServices;

namespace NockMon.Shell;

internal static partial class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    /// <summary>Shows a file's Properties sheet (SHOP_FILEPATH).</summary>
    public static bool ShowFileProperties(nint owner, string path) => SHObjectProperties(owner, 2, path, null);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(nint owner, uint type, string objectName, string? page);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    public static long GetWindowStyle(nint hwnd) => GetWindowLongPtr(hwnd, -16);

    /// <summary>Cursor position in physical screen pixels (same space as AppWindow.Position).</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    public const uint MB_OK = 0x0, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONWARNING = 0x30, IDYES = 6;

    /// <summary>A message box before (or without) any XAML window.</summary>
    public static uint MessageBox(string text, uint type) => MessageBoxW(0, text, "NockMon", type);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MessageBoxW(nint hWnd, string text, string caption, uint type);

}
