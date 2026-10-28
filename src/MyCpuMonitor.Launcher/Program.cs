using System.ComponentModel;
using System.Diagnostics;

const int ERROR_CANCELLED = 1223;

var appPath = Path.Combine(AppContext.BaseDirectory, "app", "MyCpuMonitor.exe");
if (!File.Exists(appPath))
{
    Console.Error.WriteLine($"mycpumon: application payload not found at '{appPath}'.");
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
    Console.Error.WriteLine("mycpumon: elevation declined, My CPU Monitor requires administrator rights.");
    return 1;
}
