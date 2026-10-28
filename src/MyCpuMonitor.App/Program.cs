using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MyCpuMonitor.Shell;

namespace MyCpuMonitor;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(static _ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
