using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

const int ERROR_CANCELLED = 1223;
// Same values as NockMon.Shell.SingleInstance in the app.
const string MutexName = @"Local\NockMon.Instance";
const string ShowMessageName = "NockMon.ShowWindow";

// Already running (possibly elevated, hence access denied): bring it up without a UAC prompt.
if (IsRunning())
{
    PostMessageW(0xFFFF, RegisterWindowMessageW(ShowMessageName), 0, 0);
    return 0;
}

var appPath = Path.Combine(AppContext.BaseDirectory, "app", "NockMon.exe");
if (!File.Exists(appPath))
{
    Console.Error.WriteLine($"nockmon: application payload not found at '{appPath}'.");
    return 1;
}

// The app needs elevation; asking UAC here avoids the app's own relaunch prompt.
var psi = new ProcessStartInfo(appPath)
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
    return 0;
}
catch (Win32Exception e) when (e.NativeErrorCode == ERROR_CANCELLED)
{
    Console.Error.WriteLine("nockmon: elevation declined, NockMon requires administrator rights.");
    return 1;
}

static bool IsRunning()
{
    try
    {
        if (!Mutex.TryOpenExisting(MutexName, out var existing))
            return false;
        existing.Dispose();
        return true;
    }
    catch (UnauthorizedAccessException)
    {
        return true;
    }
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern uint RegisterWindowMessageW(string name);

[DllImport("user32.dll")]
static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
