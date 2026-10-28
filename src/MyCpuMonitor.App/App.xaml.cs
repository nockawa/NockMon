using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Persistence;
using MyCpuMonitor.Core.Sampling;
using MyCpuMonitor.Monitoring;

namespace MyCpuMonitor;

public partial class App : Application
{
    private Window? _window;
    private NativeSystemProbe? _probe;
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
        _engine = new SamplingEngine(_probe, settings.SamplingOptions);
        var monitor = new MonitorService(_engine, DispatcherQueue.GetForCurrentThread(), settings.CpuScale);

        _window = new MainWindow(session, monitor);
        _window.Closed += (_, _) => Shutdown();
        _window.Activate();
        _engine.Start();
    }

    private void Shutdown()
    {
        _engine?.Dispose();
        _probe?.Dispose();
    }
}
