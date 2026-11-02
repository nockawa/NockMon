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

        var answer = Win32.MessageBox("NockMon needs administrator rights (kernel tracing, full process access).\n\nRelaunch elevated?",
            Win32.MB_YESNO | Win32.MB_ICONWARNING);
        if (answer != Win32.IDYES)
            return false;

        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            // Not the caller's folder: the current directory is a DLL search location, and the caller may be anywhere.
            WorkingDirectory = AppContext.BaseDirectory,
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
}
