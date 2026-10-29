using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace NockMon.Shell;

/// <summary>
/// The app requires elevation (ETW kernel sessions, full process access).
/// Not elevated: propose a relaunch through UAC, otherwise exit.
/// </summary>
internal static partial class ElevationGate
{
    private const uint MB_YESNO = 0x4, MB_ICONWARNING = 0x30, IDYES = 6;
    private const int ERROR_CANCELLED = 1223;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <returns>true if the current process is elevated and should continue.</returns>
    public static bool EnsureElevated(string[] args)
    {
        if (IsElevated)
            return true;
#if DEBUG || ELEVATION_BYPASS
        // Development/profiling builds only: no UAC prompt per launch (elevation-dependent features degrade).
        if (Environment.GetEnvironmentVariable("NOCKMON_SKIP_ELEVATION") == "1")
            return true;
#endif

        var answer = MessageBoxW(0,
            "NockMon needs administrator rights (kernel tracing, full process access).\n\nRelaunch elevated?",
            "NockMon", MB_YESNO | MB_ICONWARNING);
        if (answer != IDYES)
            return false;

        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            Process.Start(psi);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ERROR_CANCELLED)
        {
            // UAC prompt declined.
        }
        return false;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MessageBoxW(nint hWnd, string text, string caption, uint type);
}
