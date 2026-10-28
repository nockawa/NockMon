using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MyCpuMonitor.Core.Etw;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Persistence;
using MyCpuMonitor.Core.Sampling;
using MyCpuMonitor.Monitoring;

namespace MyCpuMonitor;

public partial class App : Application
{
    private Window? _window;
    private NativeSystemProbe? _probe;
    private EtwKernelMonitor? _events;
    private SamplingEngine? _engine;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var session = new LayoutSession(new LayoutStore(AppPaths.Layouts), new SettingsStore(AppPaths.Settings));
        var settings = session.Settings;
        _probe = new NativeSystemProbe();
        _events = new EtwKernelMonitor();
        _events.Start(); // Reports its own failure (e.g. not elevated) through Status.
        // A kernel session outlives its process: stop it on any managed exit path. A hard kill cannot be caught; the
        // next start stops the stale session.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _events.Dispose();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _events.Dispose();
        UnhandledException += (_, _) => _events.Dispose();
        _engine = new SamplingEngine(_probe, settings.SamplingOptions, _events);
        var monitor = new MonitorService(_engine, DispatcherQueue.GetForCurrentThread(), settings.CpuScale);

        _window = new MainWindow(session, monitor);
        _window.Closed += (_, _) => Shutdown();
        _window.Activate();
        _engine.Start();
    }

    private void Shutdown()
    {
        _engine?.Dispose();
        _events?.Dispose(); // Stops the kernel session: it would otherwise outlive the process.
        _probe?.Dispose();
    }
}
