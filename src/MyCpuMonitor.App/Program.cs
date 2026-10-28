using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MyCpuMonitor.Shell;

namespace MyCpuMonitor;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!ElevationGate.EnsureElevated(args))
            return 0;

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
