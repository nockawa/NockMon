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
    private readonly LayoutSession _session;
    private readonly MonitorService _monitor;
    private bool _updatingPicker;

    public MainWindow(LayoutSession session, MonitorService monitor)
    {
        _session = session;
        _monitor = monitor;
        InitializeComponent();

        Board.ContentFactory = config => WidgetContentFactory.Create(config, _monitor);
        Board.ConfigureRequested += OnConfigureRequested;
        _monitor.Updated += OnMonitorUpdated;

        CpuScalePicker.SelectedIndex = (int)session.Settings.CpuScale;
        IntervalPicker.SelectedItem = IntervalPicker.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => int.Parse((string)i.Tag, CultureInfo.InvariantCulture) == session.Settings.SampleIntervalMs);

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? Win32.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1400 * scale), (int)(860 * scale)));

        foreach (var kind in WidgetCatalog.Kinds)
        {
            var item = new MenuFlyoutItem { Text = WidgetCatalog.DisplayName(kind) };
            item.Click += (_, _) => AddWidget(kind);
            AddWidgetMenu.Items.Add(item);
        }

        _session.CurrentChanged += OnCurrentLayoutChanged;
        OnCurrentLayoutChanged();
    }

    private void OnCurrentLayoutChanged()
    {
        Board.Layout = _session.Current;
        RefreshPicker();
    }

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
        Board.RefreshWidgets();
        var engine = _monitor.Engine;
        var (processes, interval) = engine.Read(0, static (_, s, _) => (s.ProcessCount, s.Interval));
        StatusText.Text = string.Create(CultureInfo.CurrentCulture,
            $"{processes} processes · {_monitor.LogicalProcessors} CPUs · sample {engine.LastSampleDuration.TotalMilliseconds:0.0} ms / {interval.TotalMilliseconds:0} ms");
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
        if (IntervalPicker.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        var ms = int.Parse(tag, CultureInfo.InvariantCulture);
        if (_session.Settings.SampleIntervalMs == ms)
            return;
        _session.Settings.SampleIntervalMs = ms;
        _session.SaveSettings();
        _monitor.Engine.Configure(_session.Settings.SampleInterval, _session.Settings.HistoryRetention);
    }

    private void OnWidgetConfigChanged(object? sender, EventArgs e) => Run(_session.SaveCurrent);

    private async void OnConfigureRequested(WidgetConfig config)
    {
        var context = _monitor.Engine.Read(_monitor.LogicalProcessors,
            static (cores, _, history) => new EditorContext(history.ProcessNames.ToList(), cores));
        var edited = await WidgetEditor.EditAsync(Content.XamlRoot, config, context);
        if (edited is not null)
            Board.ReplaceWidget(config, edited);
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

    private void OnBoardLayoutEdited(object? sender, EventArgs e) => Info.IsOpen = false;

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
