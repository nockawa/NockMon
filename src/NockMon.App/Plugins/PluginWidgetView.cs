using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NockMon.Core.Layout;
using NockMon.Sdk;
using NockMon.Widgets;

namespace NockMon.Plugins;

/// <summary>What the host gives a plugin widget instance (or its settings editor).</summary>
internal sealed class PluginWidgetContext(PluginWidgetConfig config, Action? saved) : IWidgetContext
{
    public JsonObject Settings => config.Settings ??= new JsonObject();

    public void SaveSettings() => saved?.Invoke();

    public string DataDirectory
    {
        get
        {
            var directory = PluginRuntime.DataDirectory(config.Plugin);
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public DispatcherQueue Dispatcher { get; } = DispatcherQueue.GetForCurrentThread();

    public Version HostVersion => PluginRuntime.HostVersion;
}

/// <summary>
/// Board content of a plugin widget. The plugin's instance lives while this control is in the visual tree (created on
/// Loaded, disposed on Unloaded). Exceptions from the plugin are contained: the widget shows the error instead.
/// A missing plugin shows a placeholder; the settings stay in the layout.
/// </summary>
public sealed partial class PluginWidgetView : UserControl, IMonitorWidget
{
    /// <summary>The placeholder's "Plugins…" link (handled by the main window).</summary>
    internal static event Action? OpenPluginsRequested;

    private readonly PluginWidgetConfig _config;
    private IPluginWidget? _widget;

    public PluginWidgetView(PluginWidgetConfig config)
    {
        _config = config;
        Loaded += (_, _) => Create();
        Unloaded += (_, _) => Destroy();
    }

    public event Action? ConfigChanged;

    public void Refresh()
    {
        if (_widget is null)
            return;
        try
        {
            _widget.Refresh();
        }
        catch (Exception e)
        {
            Fail("Refresh", e);
        }
    }

    private void Create()
    {
        if (_widget is not null)
            return;
        if (PluginRuntime.Find(_config.Plugin, _config.Widget) is not { } plugin)
        {
            var loaded = PluginRuntime.FindLoaded(_config.Plugin);
            ShowMessage(loaded?.Error is { } error
                ? $"Plugin '{loaded.Title}' failed to load: {error}"
                : loaded is not null
                    ? $"Plugin '{loaded.Title}' has no widget '{_config.Widget}' (removed in this version?)."
                    : $"Plugin '{_config.Plugin}' is not installed.", link: true);
            return;
        }
        try
        {
            _widget = plugin.CreateWidget(new PluginWidgetContext(_config, () => ConfigChanged?.Invoke()));
            Content = _widget.View;
        }
        catch (Exception e)
        {
            _widget = null;
            Fail("Create", e);
        }
    }

    private void Destroy()
    {
        var widget = _widget;
        _widget = null;
        Content = null;
        try
        {
            widget?.Dispose();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Plugin widget {_config.Plugin}/{_config.Widget} Dispose failed: {e}");
        }
    }

    private void Fail(string stage, Exception e)
    {
        Debug.WriteLine($"Plugin widget {_config.Plugin}/{_config.Widget} {stage} failed: {e}");
        Destroy();
        ShowMessage($"The plugin widget failed ({stage}): {e.Message}", link: false, retry: true);
    }

    private void ShowMessage(string message, bool link, bool retry = false)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(8, 4, 8, 8) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources[retry ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"],
        });
        if (link)
        {
            var open = new HyperlinkButton { Content = "Plugins…", Padding = new Thickness(0) };
            open.Click += (_, _) => OpenPluginsRequested?.Invoke();
            panel.Children.Add(open);
        }
        if (retry)
        {
            var again = new HyperlinkButton { Content = "Retry", Padding = new Thickness(0) };
            again.Click += (_, _) => Create();
            panel.Children.Add(again);
        }
        Content = new ScrollViewer { Content = panel };
    }
}
