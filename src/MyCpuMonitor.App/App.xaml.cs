using Microsoft.UI.Xaml;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Persistence;

namespace MyCpuMonitor;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var session = new LayoutSession(new LayoutStore(AppPaths.Layouts), new SettingsStore(AppPaths.Settings));
        _window = new MainWindow(session);
        _window.Activate();
    }
}
