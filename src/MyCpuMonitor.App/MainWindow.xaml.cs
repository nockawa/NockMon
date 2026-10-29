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
    private readonly Tabs.TabRail _rail = new();
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
        Board.CloneRequested += CloneWidget;
        Board.UnstackRequested += Unstack;
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

        Plugins.PluginWidgetView.OpenPluginsRequested += () => OpenPlugins(0);
        SetUpTabs();

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
        UpdateRail();
        RefreshPicker();
        UpdateWatchedPatterns();
    }

    /// <summary>Processes used by charts and value widgets are sampled at the fast rate.</summary>
    private void UpdateWatchedPatterns()
    {
        _monitor.Engine.SetWatchedPatterns(LayoutPatterns.Watched(_session.Current));
        _monitor.Engine.SetWatchedGpuEngines(LayoutPatterns.GpuEngines(_session.Current, _monitor.Engine.GpuAdapters));
    }

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
            $"scan {engine.LastScanDuration.TotalMilliseconds:0.0} ms / {(options.Interval * options.ScanEvery).TotalSeconds:0.#} s · {EtwStatus(engine)}");
    }

    private static string EtwStatus(Core.Sampling.SamplingEngine engine) => engine.KernelEvents switch
    {
        { Status: null, EventsLost: > 0 } events => $"ETW on ({events.EventsLost:N0} events lost)",
        { Status: null } => "ETW on",
        _ => "ETW off",
    };

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
        UpdateRail();
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
        if (config is StackWidgetConfig stack)
        {
            await ConfigureStack(stack);
            return;
        }
        var edited = await WidgetEditor.EditAsync(Content.XamlRoot, config, EditorContext());
        if (edited is not null)
            Board.ReplaceWidget(config, edited); // Raises LayoutEdited, which updates the watched patterns.
    }

    private EditorContext EditorContext() =>
        _monitor.Engine.Read((cores: _monitor.LogicalProcessors, gpus: _monitor.Engine.GpuAdapters),
            static (state, _, history) => new EditorContext(history.ProcessNames.ToList(), state.cores, history.ObjectTypeNames.ToList(), state.gpus));

    /// <summary>
    /// Stack dialog; when it asks to configure an item, that item's editor, then back to the stack dialog (applied or
    /// cancelled alike) until the stack dialog is closed.
    /// </summary>
    private async Task ConfigureStack(StackWidgetConfig stack)
    {
        while (true)
        {
            if (await StackEditor.EditAsync(Content.XamlRoot, stack) is not { } result)
                return;
            var edited = result.Stack;
            if (edited.Items.Count == 1)
            {
                // A single item does not need a stack.
                var single = edited.Items[0];
                single.Bounds = edited.Bounds;
                Board.ReplaceWidget(stack, single);
                return;
            }
            foreach (var item in edited.Items)
                item.Bounds = edited.Bounds;
            Board.ReplaceWidget(stack, edited);
            stack = edited;

            if (result.ConfigureIndex is not { } index)
                return;
            var configured = await WidgetEditor.EditAsync(Content.XamlRoot, edited.Items[index], EditorContext());
            if (configured is null)
                continue;
            configured.Bounds = edited.Bounds;
            edited.Items[index] = configured;
            Board.ReplaceWidget(edited, edited); // Recreates the content.
        }
    }

    /// <summary>Moves a stack's shown item out to the first free spot (stack size, else the item's minimum).</summary>
    private void Unstack(StackWidgetConfig stack)
    {
        var item = stack.Items[stack.CurrentIndex];
        var bounds = Board.PlacementBounds;
        var spot = BoardRules.FindFreeSpot(_session.Current.Widgets, new GridSize(stack.Bounds.ColumnSpan, stack.Bounds.RowSpan), bounds.Columns, bounds.Rows)
                   ?? BoardRules.FindFreeSpot(_session.Current.Widgets, WidgetCatalog.MinSize(item), bounds.Columns, bounds.Rows);
        if (spot is null)
        {
            ShowInfo(InfoBarSeverity.Warning, "No free space", "No room to unstack this widget in the visible board. Enlarge the window or make room.");
            return;
        }
        WidgetStacking.Unstack(_session.Current, stack, stack.CurrentIndex, spot.Value);
        Board.Rebuild();
    }

    // --- Tabs (pages of the layout) --------------------------------------------------------------

    private void SetUpTabs()
    {
        BoardArea.Children.Insert(0, _rail);
        _rail.PageSelected += SwitchPage;
        _rail.AddRequested += AddTab;
        _rail.RenameRequested += RenameTab;
        _rail.IconRequested += ChangeTabIcon;
        _rail.DuplicateRequested += index => ChangePages(() => LayoutPages.Duplicate(_session.Current, index), rebuild: true);
        _rail.DeleteRequested += DeleteTab;
        _rail.MoveRequested += (from, to) => ChangePages(() => LayoutPages.Move(_session.Current, from, to), rebuild: false);
        Board.PageDropTarget = _rail.TabAt;
        Board.PageDropHover += _rail.Highlight;
        Board.MoveToPageRequested += MoveWidgetToTab;

        // Ctrl+Tab / Ctrl+Shift+Tab: next / previous tab; Ctrl+1…9: tab n. Work in compact mode too.
        RootGrid.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        Accelerator(Windows.System.VirtualKey.Tab, Windows.System.VirtualKeyModifiers.Control, () => CycleTab(1));
        Accelerator(Windows.System.VirtualKey.Tab, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, () => CycleTab(-1));
        for (var i = 0; i < 9; i++)
        {
            var index = i;
            Accelerator(Windows.System.VirtualKey.Number1 + i, Windows.System.VirtualKeyModifiers.Control, () => SwitchPage(index));
        }
    }

    private void Accelerator(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>The rail shows with several pages, or in edit mode (to add one); never in compact mode.</summary>
    private void UpdateRail()
    {
        _rail.Show(_session.Current);
        _rail.Visibility = !_compact && (_session.Current.Pages.Count > 1 || _session.IsEditing) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CycleTab(int direction)
    {
        var count = _session.Current.Pages.Count;
        if (count > 1)
            SwitchPage(((_session.Current.ActivePage + direction) % count + count) % count);
    }

    /// <summary>Shows another page. Its widgets read the history: what happened while it was hidden is there.</summary>
    private void SwitchPage(int index)
    {
        var layout = _session.Current;
        if (index == layout.ActivePage || index < 0 || index >= layout.Pages.Count)
        {
            _rail.Show(layout); // Re-selects the shown tab.
            return;
        }
        layout.ActivePage = index;
        Info.IsOpen = false;
        Board.Rebuild();
        _rail.Show(layout);
        Run(_session.SaveCurrent); // Remembers the tab (part of the session while editing).
    }

    /// <summary>Applies a page change, refreshes the rail (and the board), persists outside an edit session.</summary>
    private void ChangePages(Action change, bool rebuild)
    {
        if (!Run(change))
            return;
        if (rebuild)
            Board.Rebuild();
        UpdateRail();
        UpdateWatchedPatterns();
        Run(_session.SaveCurrent);
    }

    private void OnAddTabClick(object sender, RoutedEventArgs e) => AddTab();

    private async void AddTab()
    {
        var name = await Dialogs.PromptNameAsync(Content.XamlRoot, "New tab", LayoutPages.NewName(_session.Current), "Add", "Tab name");
        if (name is null)
            return;
        ChangePages(() => LayoutPages.Add(_session.Current, name), rebuild: true);
        if (_session.Current.Pages.Count == 2)
            ShowInfo(InfoBarSeverity.Informational, "Tabs",
                "Switch with the rail on the left, Ctrl+Tab or Ctrl+1…9. Right-click a tab to rename it, change its icon or delete it; in edit mode, drop a widget on a tab to move it there.");
    }

    private async void RenameTab(int index)
    {
        var page = _session.Current.Pages[index];
        var name = await Dialogs.PromptNameAsync(Content.XamlRoot, "Rename tab", page.Name, "Rename", "Tab name");
        if (name is not null)
            ChangePages(() => page.Name = name, rebuild: false);
    }

    private async void ChangeTabIcon(int index)
    {
        var page = _session.Current.Pages[index];
        var icon = await Tabs.IconPicker.PickAsync(Content.XamlRoot, page.Icon);
        if (icon is not null)
            ChangePages(() => page.Icon = icon, rebuild: false);
    }

    private async void DeleteTab(int index)
    {
        var page = _session.Current.Pages[index];
        var count = page.Widgets.Count;
        if (count > 0 && !await Dialogs.ConfirmAsync(Content.XamlRoot, "Delete tab",
                $"Delete '{page.Name}' and its {count} widget{(count == 1 ? "" : "s")}?", "Delete"))
            return;
        ChangePages(() => LayoutPages.Remove(_session.Current, index), rebuild: true);
    }

    private void MoveWidgetToTab(WidgetConfig widget, int index)
    {
        var bounds = Board.PlacementBounds;
        var page = _session.Current.Pages[index];
        if (!LayoutPages.MoveWidget(_session.Current, widget, index, bounds.Columns, bounds.Rows))
        {
            ShowInfo(InfoBarSeverity.Warning, "No free space", $"No room for this widget on '{page.Name}'. Make room there first.");
            return;
        }
        Board.Rebuild();
        UpdateWatchedPatterns();
        ShowInfo(InfoBarSeverity.Success, "Moved", $"Moved to '{page.Name}'.");
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
        UpdateRail();
    }

    private void OnBoardLayoutEdited(object? sender, EventArgs e)
    {
        Info.IsOpen = false;
        UpdateWatchedPatterns();
    }

    /// <summary>Built-in kinds, then the loaded plugins' widgets (they change as plugins are installed).</summary>
    private void OnAddWidgetMenuOpening(object? sender, object e)
    {
        AddWidgetMenu.Items.Clear();
        foreach (var kind in WidgetCatalog.Kinds)
        {
            var item = new MenuFlyoutItem { Text = WidgetCatalog.DisplayName(kind) };
            ToolTipService.SetToolTip(item, Help.DocView.Tooltip(Core.Help.Docs.Of(kind)));
            item.Click += (_, _) => AddWidget(kind);
            AddWidgetMenu.Items.Add(item);
        }
        AddWidgetMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var widget in PluginCatalog.Widgets.OrderBy(w => w.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuFlyoutItem { Text = widget.DisplayName, Icon = new FontIcon { Glyph = "" } };
            ToolTipService.SetToolTip(item, Help.DocView.Tooltip(new Core.Help.Doc($"{widget.DisplayName} ({widget.PluginTitle})", widget.Summary, "")));
            item.Click += (_, _) => AddWidget(widget);
            AddWidgetMenu.Items.Add(item);
        }
        var more = new MenuFlyoutItem { Text = "Get plugins…", Icon = new FontIcon { Glyph = "" } };
        more.Click += (_, _) => OpenPlugins(0);
        AddWidgetMenu.Items.Add(more);
    }

    private void OnPluginsClick(object sender, RoutedEventArgs e) => OpenPlugins(0);

    private bool _pluginsOpen;

    private async void OpenPlugins(int tab)
    {
        if (_pluginsOpen)
            return;
        _pluginsOpen = true;
        try
        {
            if (await Plugins.PluginsDialog.ShowAsync(Content.XamlRoot, _session, Restart, tab))
                Board.Rebuild(); // Placeholders of newly loaded plugins become widgets.
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is open: WinUI shows one at a time.
            ShowInfo(InfoBarSeverity.Warning, "Plugins", "Close the open dialog first.");
        }
        finally
        {
            _pluginsOpen = false;
        }
    }

    /// <summary>Restarts the app (same elevation, no prompt): plugin updates and removals take effect.</summary>
    private void Restart()
    {
        try
        {
            Program.StartReplacement();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "Restart", ex.Message);
            return;
        }
        Exit();
    }

    private void AddWidget(PluginWidgetInfo widget)
    {
        var bounds = Board.PlacementBounds;
        var spot = BoardRules.FindFreeSpot(_session.Current.Widgets, widget.DefaultSize, bounds.Columns, bounds.Rows)
                   ?? BoardRules.FindFreeSpot(_session.Current.Widgets, widget.MinSize, bounds.Columns, bounds.Rows);
        if (spot is null)
        {
            ShowInfo(InfoBarSeverity.Warning, "No free space", $"No room for {widget.DisplayName} in the visible board. Enlarge the window or make room.");
            return;
        }
        _session.Current.Widgets.Add(WidgetCatalog.Create(widget, spot.Value));
        Board.Rebuild();
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

    /// <summary>Copy of a widget (same settings and size) at the first free spot of the visible board.</summary>
    private void CloneWidget(WidgetConfig config)
    {
        var size = new GridSize(config.Bounds.ColumnSpan, config.Bounds.RowSpan);
        var bounds = Board.PlacementBounds;
        var spot = BoardRules.FindFreeSpot(_session.Current.Widgets, size, bounds.Columns, bounds.Rows);
        if (spot is null)
        {
            ShowInfo(InfoBarSeverity.Warning, "No free space",
                $"No room for a {size.Columns}×{size.Rows} copy in the visible board. Enlarge the window or make room.");
            return;
        }
        var clone = Core.Persistence.LayoutStore.Clone(config);
        clone.Id = Guid.NewGuid();
        if (clone is StackWidgetConfig stack)
        {
            foreach (var item in stack.Items)
                item.Id = Guid.NewGuid();
        }
        clone.Bounds = spot.Value;
        _session.Current.Widgets.Add(clone);
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
