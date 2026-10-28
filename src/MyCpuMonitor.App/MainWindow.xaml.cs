using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyCpuMonitor.Board;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Editing;
using MyCpuMonitor.Monitoring;
using MyCpuMonitor.Shell;
using Windows.Graphics;

namespace MyCpuMonitor;

public sealed partial class MainWindow : Window
{
    private const uint MenuShow = 1, MenuTopmost = 2, MenuCloseToTray = 3, MenuCompact = 4, MenuExit = 5;

    private readonly LayoutSession _session;
    private readonly MonitorService _monitor;
    private readonly nint _hwnd;
    private readonly TrayIcon _tray;
    private readonly float[] _trayHistory = new float[64];
    private bool _updatingPicker;
    private bool _exiting;
    private bool _compact;
    private (PointInt32 Cursor, PointInt32 Window)? _compactDrag;

    public MainWindow(LayoutSession session, MonitorService monitor)
    {
        _session = session;
        _monitor = monitor;
        InitializeComponent();

        Board.ContentFactory = config => WidgetContentFactory.Create(config, _monitor);
        Board.ConfigureRequested += OnConfigureRequested;
        _monitor.Updated += OnMonitorUpdated;

        CpuScalePicker.SelectedIndex = (int)session.Settings.CpuScale;
        SelectByTag(IntervalPicker, session.Settings.SampleIntervalMs);
        SelectByTag(ScanPicker, session.Settings.ScanIntervalMs);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!WindowPlacement.TryRestore(_hwnd, session.Settings.Window))
        {
            var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
            AppWindow.Resize(new SizeInt32((int)(1400 * scale), (int)(860 * scale)));
        }
        AppWindow.Closing += OnClosing;

        // Static bar icon for the window/taskbar; the tray icon is live.
        var size = BarIcon.SmallIconSize(_hwnd);
        var windowIcon = BarIcon.Create(size, [0.3f, 0.5f, 0.4f, 0.7f, 0.6f, 0.9f, 0.5f, 0.8f, 0.6f, 0.4f, 0.7f, 1f, 0.8f, 0.6f]);
        AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(windowIcon));
        _tray = new TrayIcon(_hwnd, BarIcon.Create(size, []), "My CPU Monitor");
        _tray.Activated += ShowFromTray;
        _tray.MenuRequested += OnTrayMenu;
        Closed += (_, _) => _tray.Dispose();
        SingleInstance.Listen(_hwnd, ShowFromTray);
        SetAlwaysOnTop(session.Settings.AlwaysOnTop);

        foreach (var kind in WidgetCatalog.Kinds)
        {
            var item = new MenuFlyoutItem { Text = WidgetCatalog.DisplayName(kind) };
            item.Click += (_, _) => AddWidget(kind);
            AddWidgetMenu.Items.Add(item);
        }

        _session.CurrentChanged += OnCurrentLayoutChanged;
        OnCurrentLayoutChanged();
#if DEBUG
        if (Environment.GetEnvironmentVariable("MYCPUMON_DEBUG_COMPACT") == "1")
            DebugCompactRoundTrip();
#endif
    }

#if DEBUG
    /// <summary>Development: compact on at 4 s, off at 8 s, geometry logged to compact-debug.log in the data folder.</summary>
    private async void DebugCompactRoundTrip()
    {
        var log = Path.Combine(Core.Persistence.AppPaths.Root, "compact-debug.log");
        void Dump(string phase)
        {
            Win32.GetWindowRect(_hwnd, out var w);
            Win32.GetClientRect(_hwnd, out var c);
            var origin = RootGrid.TransformToVisual(null).TransformPoint(default);
            var toolbar = Toolbar.TransformToVisual(null).TransformPoint(default);
            File.AppendAllText(log,
                $"{phase}: window={w.Left},{w.Top},{w.Right},{w.Bottom} client={c.Right}x{c.Bottom} " +
                $"appClient={AppWindow.ClientSize.Width}x{AppWindow.ClientSize.Height} scale={RootGrid.XamlRoot.RasterizationScale} " +
                $"xamlRoot={RootGrid.XamlRoot.Size.Width:0}x{RootGrid.XamlRoot.Size.Height:0} root={RootGrid.ActualWidth:0}x{RootGrid.ActualHeight:0}@{origin.X:0},{origin.Y:0} " +
                $"toolbar@{toolbar.X:0},{toolbar.Y:0} style=0x{Win32.GetWindowStyle(_hwnd):X8} " +
                $"extends={AppWindow.TitleBar.ExtendsContentIntoTitleBar} windowExtends={ExtendsContentIntoTitleBar} " +
                $"hasTitleBar={(AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.HasTitleBar}\n");
        }
        await Task.Delay(3000);
        Dump("normal");
        await Task.Delay(1000);
        SetCompact(true);
        await Task.Delay(2000);
        Dump("compact");
        await Task.Delay(2000);
        SetCompact(false);
        await Task.Delay(2000);
        Dump("restored");
    }
#endif

    private void OnCurrentLayoutChanged()
    {
        Board.Layout = _session.Current;
        RefreshPicker();
        UpdateWatchedPatterns();
    }

    /// <summary>Processes used by charts and value widgets are sampled at the fast rate.</summary>
    private void UpdateWatchedPatterns() => _monitor.Engine.SetWatchedPatterns(LayoutPatterns.Watched(_session.Current));

    private static void SelectByTag(ComboBox picker, int value) =>
        picker.SelectedItem = picker.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => int.Parse((string)i.Tag, CultureInfo.InvariantCulture) == value);

    private void RefreshPicker()
    {
        _updatingPicker = true;
        try
        {
            var names = _session.Names;
            LayoutPicker.ItemsSource = names;
            LayoutPicker.SelectedItem = names.FirstOrDefault(n => n == _session.Current.Name);
        }
        finally
        {
            _updatingPicker = false;
        }
    }

    private void OnLayoutPickerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPicker || LayoutPicker.SelectedItem is not string name || name == _session.Current.Name)
            return;
        Run(() => _session.Switch(name));
    }

    // --- Monitoring -------------------------------------------------------------------------------

    private void OnMonitorUpdated()
    {
        UpdateTray();
        if (!AppWindow.IsVisible)
            return; // Hidden to the tray: only the tray icon is fed.

        Board.RefreshWidgets();
        var engine = _monitor.Engine;
        var options = engine.Options;
        var processes = engine.Read(0, static (_, s, _) => s.ProcessCount);
        StatusText.Text = string.Create(CultureInfo.CurrentCulture,
            $"{processes} processes · {engine.WatchedProcessCount} watched · {_monitor.LogicalProcessors} CPUs · " +
            $"sample {engine.LastSampleDuration.TotalMilliseconds:0.0} ms / {options.Interval.TotalMilliseconds:0} ms · " +
            $"scan {engine.LastScanDuration.TotalMilliseconds:0.0} ms / {(options.Interval * options.ScanEvery).TotalSeconds:0.#} s");
    }

    // --- Tray, visibility, always on top, compact mode --------------------------------------------

    private void UpdateTray()
    {
        var size = BarIcon.SmallIconSize(_hwnd);
        var (count, cpu, used, total) = _monitor.Engine.Read((buffer: _trayHistory, size: size - 2), static (state, s, h) =>
        {
            var n = h.Read(new SystemSeries(SystemMetric.Cpu), state.buffer.AsSpan(0, Math.Min(state.size, state.buffer.Length)));
            return (n, s.System.Cpu, s.System.PhysicalUsed, s.System.PhysicalTotal);
        });
        var logical = Math.Max(1, _monitor.LogicalProcessors);
        var history = _trayHistory.AsSpan(0, count);
        for (var i = 0; i < history.Length; i++)
            history[i] /= logical;

        var tip = string.Create(CultureInfo.CurrentCulture,
            $"My CPU Monitor\nCPU {_monitor.Format(cpu, MetricUnit.Cpu)}\nRAM {MetricFormatter.FormatBytes(used)} / {MetricFormatter.FormatBytes(total)}");
        _tray.Update(BarIcon.Create(size, history), tip);
    }

    private void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        _session.Settings.Window = WindowPlacement.Capture(_hwnd) ?? _session.Settings.Window;
        _session.SaveSettings();
        if (_exiting || !_session.Settings.CloseToTray)
            return;
        args.Cancel = true;
        AppWindow.Hide();
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
        Win32.SetForegroundWindow(_hwnd);
        OnMonitorUpdated(); // Widgets were not refreshed while hidden.
    }

    private void Exit()
    {
        _exiting = true;
        Close();
    }

    private void OnTrayMenu(int x, int y)
    {
        var settings = _session.Settings;
        var choice = _tray.ShowMenu(x, y,
        [
            new(MenuShow, "Show"),
            new(0, "", Separator: true),
            new(MenuTopmost, "Always on top", settings.AlwaysOnTop),
            new(MenuCompact, "Compact mode", _compact),
            new(MenuCloseToTray, "Close button hides to tray", settings.CloseToTray),
            new(0, "", Separator: true),
            new(MenuExit, "Exit"),
        ]);
        switch (choice)
        {
            case MenuShow:
                ShowFromTray();
                break;
            case MenuTopmost:
                SetAlwaysOnTop(!settings.AlwaysOnTop);
                break;
            case MenuCompact:
                SetCompact(!_compact);
                ShowFromTray();
                break;
            case MenuCloseToTray:
                settings.CloseToTray = !settings.CloseToTray;
                _session.SaveSettings();
                break;
            case MenuExit:
                Exit();
                break;
        }
    }

    private void OnTopmostClick(object sender, RoutedEventArgs e) => SetAlwaysOnTop(TopmostButton.IsChecked == true);

    private void SetAlwaysOnTop(bool value)
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = value;
        TopmostButton.IsChecked = value;
        if (_session.Settings.AlwaysOnTop == value)
            return;
        _session.Settings.AlwaysOnTop = value;
        _session.SaveSettings();
    }

    /// <summary>Compact: no toolbar, no title bar (border kept for resizing), the board drags the window.</summary>
    private void SetCompact(bool compact)
    {
        if (_session.IsEditing)
            return;
        _compact = compact;
        Toolbar.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Info.IsOpen = false;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: !compact);
        // WinUI: hiding the title bar sets AppWindow.TitleBar.ExtendsContentIntoTitleBar, and showing it again does not
        // reset it, leaving the content under the restored title bar. Reset it explicitly.
        if (!compact)
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = false;
    }

    private void OnBoardDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (!_session.IsEditing)
            SetCompact(!_compact);
    }

    private void OnRootKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && _compact)
            SetCompact(false);
    }

    // Manual window drag in compact mode (the system move loop would swallow double-clicks and maximize on them).
    private void OnBoardPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_compact || _session.IsEditing || !e.GetCurrentPoint(Board).Properties.IsLeftButtonPressed)
            return;
        if (Win32.GetCursorPos(out var cursor))
            _compactDrag = (new PointInt32(cursor.X, cursor.Y), AppWindow.Position);
    }

    private void OnBoardPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_compactDrag is not { } drag || !Win32.GetCursorPos(out var cursor))
            return;
        int dx = cursor.X - drag.Cursor.X, dy = cursor.Y - drag.Cursor.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < 4 && AppWindow.Position == drag.Window)
            return; // Below the drag threshold: still a click.
        if (Board.PointerCaptures is not { Count: > 0 })
            Board.CapturePointer(e.Pointer);
        AppWindow.Move(new PointInt32(drag.Window.X + dx, drag.Window.Y + dy));
    }

    private void OnBoardPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_compactDrag is null)
            return;
        _compactDrag = null;
        Board.ReleasePointerCaptures();
    }

    private void OnCpuScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CpuScalePicker.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        var scale = Enum.Parse<CpuScale>(tag);
        _monitor.CpuScale = scale;
        if (_session.Settings.CpuScale != scale)
        {
            _session.Settings.CpuScale = scale;
            _session.SaveSettings();
        }
    }

    private void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            ApplySampling(sampleMs: int.Parse(tag, CultureInfo.InvariantCulture), scanMs: null);
    }

    private void OnScanIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScanPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            ApplySampling(sampleMs: null, scanMs: int.Parse(tag, CultureInfo.InvariantCulture));
    }

    private void ApplySampling(int? sampleMs, int? scanMs)
    {
        var settings = _session.Settings;
        if ((sampleMs ?? settings.SampleIntervalMs) == settings.SampleIntervalMs && (scanMs ?? settings.ScanIntervalMs) == settings.ScanIntervalMs)
            return;
        settings.SampleIntervalMs = sampleMs ?? settings.SampleIntervalMs;
        settings.ScanIntervalMs = scanMs ?? settings.ScanIntervalMs;
        _session.SaveSettings();
        _monitor.Engine.Configure(settings.SamplingOptions);
    }

    private void OnWidgetConfigChanged(object? sender, EventArgs e) => Run(_session.SaveCurrent);

    private async void OnConfigureRequested(WidgetConfig config)
    {
        var context = _monitor.Engine.Read(_monitor.LogicalProcessors,
            static (cores, _, history) => new EditorContext(history.ProcessNames.ToList(), cores));
        var edited = await WidgetEditor.EditAsync(Content.XamlRoot, config, context);
        if (edited is not null)
            Board.ReplaceWidget(config, edited); // Raises LayoutEdited, which updates the watched patterns.
    }

    // --- Edit session -----------------------------------------------------------------------------

    private void OnEditClick(object sender, RoutedEventArgs e) => SetEditing(Run(_session.BeginEdit));

    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        if (Run(_session.CommitEdit))
            SetEditing(false);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        SetEditing(false);
        Run(_session.CancelEdit);
    }

    private void SetEditing(bool editing)
    {
        BrowsePanel.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        EditPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        Board.IsEditing = editing;
        if (!editing)
            Info.IsOpen = false;
    }

    private void OnBoardLayoutEdited(object? sender, EventArgs e)
    {
        Info.IsOpen = false;
        UpdateWatchedPatterns();
    }

    private void AddWidget(WidgetKind kind)
    {
        var size = WidgetCatalog.DefaultSize(kind);
        var bounds = Board.PlacementBounds;
        var spot = BoardRules.FindFreeSpot(_session.Current.Widgets, size, bounds.Columns, bounds.Rows)
                   ?? BoardRules.FindFreeSpot(_session.Current.Widgets, WidgetCatalog.MinSize(kind), bounds.Columns, bounds.Rows);
        if (spot is null)
        {
            ShowInfo(InfoBarSeverity.Warning, "No free space",
                $"No room for a {WidgetCatalog.DisplayName(kind).ToLowerInvariant()} in the visible board. Enlarge the window or make room.");
            return;
        }
        _session.Current.Widgets.Add(WidgetCatalog.Create(kind, spot.Value));
        Board.Rebuild();
        UpdateWatchedPatterns();
    }

    // --- Layout management ------------------------------------------------------------------------

    private async void OnNewLayoutClick(object sender, RoutedEventArgs e)
    {
        var name = await Dialogs.PromptNameAsync(Content.XamlRoot, "New empty layout", "", "Create");
        if (name is not null)
            Run(() => _session.CreateEmpty(name));
    }

    private async void OnDuplicateLayoutClick(object sender, RoutedEventArgs e)
    {
        var name = await Dialogs.PromptNameAsync(Content.XamlRoot, "Duplicate layout", _session.Current.Name + " copy", "Duplicate");
        if (name is not null)
            Run(() => _session.Duplicate(name));
    }

    private async void OnRenameLayoutClick(object sender, RoutedEventArgs e)
    {
        var name = await Dialogs.PromptNameAsync(Content.XamlRoot, "Rename layout", _session.Current.Name, "Rename");
        if (name is not null && Run(() => _session.Rename(name)))
            RefreshPicker();
    }

    private async void OnDeleteLayoutClick(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ConfirmAsync(Content.XamlRoot, "Delete layout", $"Delete layout '{_session.Current.Name}'? This cannot be undone.", "Delete"))
            Run(_session.Delete);
    }

    // --- Helpers ----------------------------------------------------------------------------------

    /// <summary>Runs a session operation; user-facing failures are shown in the info bar.</summary>
    private bool Run(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or NotSupportedException or System.Text.Json.JsonException)
        {
            ShowInfo(InfoBarSeverity.Error, "Layout", e.Message);
            RefreshPicker();
            return false;
        }
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        Info.Severity = severity;
        Info.Title = title;
        Info.Message = message;
        Info.IsOpen = true;
    }
}
