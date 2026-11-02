using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NockMon.Shell;

namespace NockMon;

public static class Program
{
    /// <summary>Argument of a restart: the new process waits for the old one to exit (single instance).</summary>
    public const string WaitForProcessArgument = "--wait-pid";

    /// <summary>The uninstaller's: asks the running instance to exit cleanly, never starts one.</summary>
    public const string ExitArgument = "--exit";

    [STAThread]
    private static int Main(string[] args)
    {
        // MCP relay for AI agents: no window, no elevation, no single-instance claim.
        if (args is [Agents.McpRelay.Argument, ..])
            return Agents.McpRelay.Run();
        if (args is [ExitArgument])
            return SingleInstance.ExitExisting(TimeSpan.FromSeconds(10)) ? 0 : 1;

        args = WaitForPreviousInstance(args);
        // Before elevation: a second launch must not trigger a UAC prompt.
        if (SingleInstance.IsAnotherInstanceRunning())
        {
            SingleInstance.SignalExisting();
            return 0;
        }
        if (!ElevationGate.EnsureElevated(args))
            return 0;
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalExisting();
            return 0;
        }

        try
        {
            NockMon.Core.Persistence.AppPaths.Prepare();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Win32.MessageBox($"NockMon cannot prepare its data folder ({NockMon.Core.Persistence.AppPaths.Root}):\n{e.Message}", Win32.MB_OK | Win32.MB_ICONERROR);
            return 1;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(static _ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    /// <summary>Starts a new instance (same arguments, same elevation, no UAC prompt) that waits for this one to exit.</summary>
    public static void StartReplacement()
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var arg in WithoutWait(Environment.GetCommandLineArgs()[1..]))
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(WaitForProcessArgument);
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        System.Diagnostics.Process.Start(psi);
    }

    private static string[] WaitForPreviousInstance(string[] args)
    {
        var index = Array.IndexOf(args, WaitForProcessArgument);
        if (index < 0 || index + 1 >= args.Length)
            return args;
        if (int.TryParse(args[index + 1], out var pid))
        {
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(pid);
                previous.WaitForExit(TimeSpan.FromSeconds(20));
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        return WithoutWait(args);
    }

    /// <summary>The arguments minus every <see cref="WaitForProcessArgument"/> pair.</summary>
    private static string[] WithoutWait(string[] args)
    {
        var result = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == WaitForProcessArgument)
                i++;
            else
                result.Add(args[i]);
        }
        return [.. result];
    }
}
