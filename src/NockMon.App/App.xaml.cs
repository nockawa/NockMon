using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NockMon.Core.Etw;
using NockMon.Core.Gpu;
using NockMon.Core.Layout;
using NockMon.Core.Persistence;
using NockMon.Core.Sampling;
using NockMon.Monitoring;

namespace NockMon;

public partial class App : Application
{
    private Window? _window;
    private NativeSystemProbe? _probe;
    private EtwKernelMonitor? _events;
    private SamplingEngine? _engine;
    private GpuProbe? _gpu;

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
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogCrash(e.ExceptionObject as Exception);
            _events.Dispose();
        };
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            _events.Dispose();
        };
        _gpu = new GpuProbe();
        _engine = new SamplingEngine(_probe, settings.SamplingOptions, _events, _gpu);
        var monitor = new MonitorService(_engine, DispatcherQueue.GetForCurrentThread(), settings.CpuScale);
        // Before the window: layouts referencing plugin widgets find them registered.
        Plugins.PluginRuntime.Initialize(settings.DevPluginFolders, Environment.GetCommandLineArgs());

        _window = new MainWindow(session, monitor);
        _window.Closed += (_, _) => Shutdown();
        _window.Activate();
        _engine.Start();
    }

    /// <summary>Appends the exception to crash.log in the data folder (a plugin or a WinUI error would otherwise leave no trace).</summary>
    private static void LogCrash(Exception? exception)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.Root, "crash.log"), $"{DateTime.Now:O} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private void Shutdown()
    {
        _engine?.Dispose();
        _events?.Dispose(); // Stops the kernel session: it would otherwise outlive the process.
        _gpu?.Dispose();
        _probe?.Dispose();
    }
}
