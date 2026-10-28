using System.Runtime.InteropServices;

namespace MyCpuMonitor.Shell;

internal static partial class Win32
{
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);
}
