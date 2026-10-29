using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Persistence;
using MyCpuMonitor.Help;
using Docs = MyCpuMonitor.Core.Help.Docs;

namespace MyCpuMonitor.Editing;

/// <summary>Configuration dialog per widget kind. Edits a clone; returns it on Apply, null on cancel.</summary>
internal static class WidgetEditor
{
    private static readonly (string Label, int Seconds)[] Windows =
        [("30 s", 30), ("1 min", 60), ("5 min", 300), ("15 min", 900), ("30 min", 1800), ("1 h", 3600)];

    public static async Task<WidgetConfig?> EditAsync(XamlRoot root, WidgetConfig config, EditorContext context)
    {
        var clone = LayoutStore.Clone(config);
        var panel = new StackPanel { Spacing = 12, MinWidth = 480 };
        var about = Docs.Of(clone);
        panel.Children.Add(new Expander
        {
            Header = new TextBlock { Text = about.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) },
            Content = DocView.Render(about.Details),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        });
        var title = new TextBox { Header = "Title", Text = clone.Title ?? "", PlaceholderText = "No title" };
        panel.Children.Add(title);

        Func<string?> apply = clone switch
        {
            ProcessTableWidgetConfig t => BuildTable(panel, t),
            ChartWidgetConfig c => BuildChart(panel, c, context),
            ValueWidgetConfig v => BuildValue(panel, v, context),
            CpuGridWidgetConfig g => BuildCpuGrid(panel, g),
            PoolTagTableWidgetConfig p => BuildPoolTags(panel, p),
            ValueListWidgetConfig l => BuildValueList(panel, l, context),
            DriverTableWidgetConfig d => BuildDrivers(panel, d),
            ProcessEventsWidgetConfig e => BuildProcessEvents(panel, e),
            GpuEnginesWidgetConfig g => BuildGpuEngines(panel, g, context),
            PluginWidgetConfig p => BuildPlugin(panel, p),
            ObjectTypesWidgetConfig o => BuildSortedTable(panel, o.TopCount, o.SortBy, o.Descending, Docs.Of,
                (top, sort, desc) => (o.TopCount, o.SortBy, o.Descending) = (top, sort, desc)),
            _ => throw new ArgumentException($"Unknown widget {clone.GetType().Name}.", nameof(config)),
        };

        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Configure {(clone is PluginWidgetConfig ? WidgetCatalog.DisplayName(clone) : WidgetCatalog.DisplayName(clone).ToLowerInvariant())}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 640, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var message = apply();
            if (message is null)
            {
                clone.Title = string.IsNullOrWhiteSpace(title.Text) ? null : title.Text.Trim();
                return;
            }
            error.Message = message;
            error.IsOpen = true;
            args.Cancel = true;
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? clone : null;
    }

    private static Func<string?> BuildTable(StackPanel panel, ProcessTableWidgetConfig config)
    {
        var top = new NumberBox { Header = "Rows", Value = config.TopCount, Minimum = 1, Maximum = 500, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120 };
        var sort = new ComboBox { Header = "Sort by", MinWidth = 200 };
        foreach (var field in Enum.GetValues<ProcessField>())
            sort.Items.Add(new ComboBoxItem { Content = MetricCatalog.Get(field).DisplayName, Tag = field });
        sort.SelectedIndex = (int)config.SortBy;
        var descending = new ToggleSwitch { Header = "Descending", IsOn = config.Descending };
        var group = new ToggleSwitch { Header = "Group by name", IsOn = config.GroupByName };
        var excludeIdle = new ToggleSwitch { Header = "Exclude Idle", IsOn = config.ExcludeIdle };
        var filter = new TextBox { Header = "Filter (name pattern)", Text = config.Filter ?? "", PlaceholderText = "All processes" };

        panel.Children.Add(Row(top, sort, descending));
        panel.Children.Add(DocView.Attach(sort, f => Docs.Of((ProcessField)f)));
        panel.Children.Add(Row(group, excludeIdle));
        panel.Children.Add(filter);

        var columns = new ColumnListEditor(config.Columns);
        panel.Children.Add(new TextBlock { Text = "Columns (hover for details)", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(columns);

        return () =>
        {
            var selected = columns.Build();
            if (selected.Count == 0)
                return "Select at least one column.";
            config.TopCount = double.IsNaN(top.Value) ? 20 : (int)top.Value;
            config.SortBy = (ProcessField)((ComboBoxItem)sort.SelectedItem).Tag;
            config.Descending = descending.IsOn;
            config.GroupByName = group.IsOn;
            config.ExcludeIdle = excludeIdle.IsOn;
            config.Filter = string.IsNullOrWhiteSpace(filter.Text) ? null : filter.Text.Trim();
            config.Columns = selected;
            return null;
        };
    }

    private static Func<string?> BuildChart(StackPanel panel, ChartWidgetConfig config, EditorContext context)
    {
        var window = WindowPicker("Time window", config.WindowSeconds);
        var stacked = new ToggleSwitch { Header = "Stacked", IsOn = config.Stacked };
        panel.Children.Add(Row(window, stacked));

        var list = new StackPanel { Spacing = 8 };
        var editors = new List<SeriesEditor>();

        void AddEditor(ChartSeriesConfig series)
        {
            var editor = new SeriesEditor(series.Source, series.Label, series.Color, context, showLabelAndColor: true);
            var remove = new Button { Content = "Remove series", HorizontalAlignment = HorizontalAlignment.Right };
            var card = new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Child = new StackPanel { Spacing = 8, Children = { editor, remove } },
            };
            remove.Click += (_, _) =>
            {
                editors.Remove(editor);
                list.Children.Remove(card);
            };
            editors.Add(editor);
            list.Children.Add(card);
        }

        foreach (var series in config.Series)
            AddEditor(series);
        var add = new Button { Content = "Add series" };
        add.Click += (_, _) => AddEditor(new ChartSeriesConfig { Source = new SystemSeries(SystemMetric.Cpu) });

        panel.Children.Add(new TextBlock { Text = "Series", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(list);
        panel.Children.Add(add);

        return () =>
        {
            if (editors.Count == 0)
                return "Add at least one series.";
            var series = new List<ChartSeriesConfig>();
            foreach (var editor in editors)
            {
                var source = editor.Build(out var error);
                if (source is null)
                    return error;
                series.Add(new ChartSeriesConfig { Source = source, Label = editor.Label, Color = editor.Color });
            }
            var units = series.Select(s => s.Source.Info.Unit).Distinct().ToList();
            if (units.Count > 1)
                return $"All series of a chart must share a unit (found {string.Join(", ", units)}). Use one chart per unit.";

            config.WindowSeconds = (int)((ComboBoxItem)window.SelectedItem).Tag;
            config.Stacked = stacked.IsOn;
            config.Series = series;
            return null;
        };
    }

    private static Func<string?> BuildValue(StackPanel panel, ValueWidgetConfig config, EditorContext context)
    {
        var editor = new SeriesEditor(config.Source, null, null, context, showLabelAndColor: false);
        var window = WindowPicker("Sparkline window", config.SparklineSeconds);
        panel.Children.Add(editor);
        panel.Children.Add(window);
        return () =>
        {
            var source = editor.Build(out var error);
            if (source is null)
                return error;
            config.Source = source;
            config.SparklineSeconds = (int)((ComboBoxItem)window.SelectedItem).Tag;
            return null;
        };
    }

    private static Func<string?> BuildCpuGrid(StackPanel panel, CpuGridWidgetConfig config)
    {
        var metric = new ComboBox { Header = "Metric", MinWidth = 220 };
        foreach (var m in Enum.GetValues<CoreMetric>())
            metric.Items.Add(new ComboBoxItem { Content = MetricCatalog.Get(m).DisplayName, Tag = m });
        metric.SelectedIndex = (int)config.Metric;
        var kernel = new ToggleSwitch { Header = "Shade kernel time (usage only)", IsOn = config.ShowKernel };
        metric.SelectionChanged += (_, _) => kernel.IsEnabled = (CoreMetric)((ComboBoxItem)metric.SelectedItem).Tag == CoreMetric.Usage;
        kernel.IsEnabled = config.Metric == CoreMetric.Usage;
        var window = WindowPicker("Time window", config.WindowSeconds);
        var columns = new NumberBox
        {
            Header = "Columns (0 = automatic)", Value = config.Columns, Minimum = 0, Maximum = 256,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 160,
        };

        panel.Children.Add(Row(metric, window));
        panel.Children.Add(DocView.Attach(metric, m => Docs.Of((CoreMetric)m)));
        panel.Children.Add(Row(kernel, columns));
        return () =>
        {
            config.Metric = (CoreMetric)((ComboBoxItem)metric.SelectedItem).Tag;
            config.ShowKernel = kernel.IsOn;
            config.WindowSeconds = (int)((ComboBoxItem)window.SelectedItem).Tag;
            config.Columns = double.IsNaN(columns.Value) ? 0 : (int)columns.Value;
            return null;
        };
    }

    private static Func<string?> BuildPoolTags(StackPanel panel, PoolTagTableWidgetConfig config)
    {
        var top = new NumberBox { Header = "Rows", Value = config.TopCount, Minimum = 1, Maximum = 500, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120 };
        var sort = new ComboBox { Header = "Sort by", MinWidth = 200 };
        foreach (var field in Enum.GetValues<Core.Sampling.PoolTagField>())
            sort.Items.Add(new ComboBoxItem { Content = Docs.Of(field).Title, Tag = field });
        sort.SelectedIndex = (int)config.SortBy;
        var descending = new ToggleSwitch { Header = "Descending", IsOn = config.Descending };
        panel.Children.Add(Row(top, sort, descending));
        panel.Children.Add(DocView.Attach(sort, f => Docs.Of((Core.Sampling.PoolTagField)f)));
        return () =>
        {
            config.TopCount = double.IsNaN(top.Value) ? 20 : (int)top.Value;
            config.SortBy = (Core.Sampling.PoolTagField)((ComboBoxItem)sort.SelectedItem).Tag;
            config.Descending = descending.IsOn;
            return null;
        };
    }

    private static Func<string?> BuildGpuEngines(StackPanel panel, GpuEnginesWidgetConfig config, EditorContext context)
    {
        if (context.Gpus.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No GPU found." });
            return () => null;
        }
        var adapter = new ComboBox { Header = "GPU", MinWidth = 280 };
        foreach (var gpu in context.Gpus)
            adapter.Items.Add(new ComboBoxItem { Content = gpu.Name, Tag = gpu });
        adapter.SelectedItem = adapter.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((Core.Gpu.GpuAdapterInfo)i.Tag).Name == config.Adapter) ?? adapter.Items[0];
        var pattern = new TextBox { Header = "Engines (name pattern)", Text = config.EnginePattern, MinWidth = 200 };
        var engines = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        void ShowEngines() => engines.Text = "Engines: " + string.Join(", ", ((Core.Gpu.GpuAdapterInfo)((ComboBoxItem)adapter.SelectedItem).Tag).Engines);
        adapter.SelectionChanged += (_, _) => ShowEngines();
        ShowEngines();
        var window = WindowPicker("Time window", config.WindowSeconds);
        var columns = new NumberBox
        {
            Header = "Columns (0 = automatic)", Value = config.Columns, Minimum = 0, Maximum = 64,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 160,
        };
        panel.Children.Add(Row(adapter, pattern));
        panel.Children.Add(engines);
        panel.Children.Add(new TextBlock { Text = "Each shown engine is queried every sample (~0.3 ms each).", Opacity = 0.7, FontSize = 12 });
        panel.Children.Add(Row(window, columns));
        return () =>
        {
            if (string.IsNullOrWhiteSpace(pattern.Text))
                return "Enter an engine pattern (* for all).";
            config.Adapter = ((Core.Gpu.GpuAdapterInfo)((ComboBoxItem)adapter.SelectedItem).Tag).Name;
            config.EnginePattern = pattern.Text.Trim();
            config.WindowSeconds = (int)((ComboBoxItem)window.SelectedItem).Tag;
            config.Columns = double.IsNaN(columns.Value) ? 0 : (int)columns.Value;
            return null;
        };
    }

    /// <summary>Rows / sort field / order editor for tables sorted by an enum field.</summary>
    private static Func<string?> BuildSortedTable<TField>(StackPanel panel, int topCount, TField sortBy, bool isDescending,
        Func<TField, Core.Help.Doc> doc, Action<int, TField, bool> apply)
        where TField : struct, Enum
    {
        var top = new NumberBox { Header = "Rows", Value = topCount, Minimum = 1, Maximum = 500, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120 };
        var sort = new ComboBox { Header = "Sort by", MinWidth = 200 };
        foreach (var field in Enum.GetValues<TField>())
            sort.Items.Add(new ComboBoxItem { Content = doc(field).Title, Tag = field });
        sort.SelectedItem = sort.Items.Cast<ComboBoxItem>().First(i => Equals(i.Tag, sortBy));
        var descending = new ToggleSwitch { Header = "Descending", IsOn = isDescending };
        panel.Children.Add(Row(top, sort, descending));
        panel.Children.Add(DocView.Attach(sort, f => doc((TField)f)));
        return () =>
        {
            apply(double.IsNaN(top.Value) ? topCount : (int)top.Value, (TField)((ComboBoxItem)sort.SelectedItem).Tag, descending.IsOn);
            return null;
        };
    }

    private static Func<string?> BuildDrivers(StackPanel panel, DriverTableWidgetConfig config)
    {
        var top = new NumberBox { Header = "Rows", Value = config.TopCount, Minimum = 1, Maximum = 200, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120 };
        var sort = new ComboBox { Header = "Sort by", MinWidth = 200 };
        foreach (var field in Enum.GetValues<Core.Sampling.DriverField>())
            sort.Items.Add(new ComboBoxItem { Content = Docs.Of(field).Title, Tag = field });
        sort.SelectedIndex = (int)config.SortBy;
        var descending = new ToggleSwitch { Header = "Descending", IsOn = config.Descending };
        panel.Children.Add(Row(top, sort, descending));
        panel.Children.Add(DocView.Attach(sort, f => Docs.Of((Core.Sampling.DriverField)f)));
        return () =>
        {
            config.TopCount = double.IsNaN(top.Value) ? 15 : (int)top.Value;
            config.SortBy = (Core.Sampling.DriverField)((ComboBoxItem)sort.SelectedItem).Tag;
            config.Descending = descending.IsOn;
            return null;
        };
    }

    private static Func<string?> BuildProcessEvents(StackPanel panel, ProcessEventsWidgetConfig config)
    {
        var rows = new NumberBox { Header = "Rows", Value = config.MaxRows, Minimum = 1, Maximum = 500, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120 };
        var starts = new ToggleSwitch { Header = "Starts", IsOn = config.ShowStarts };
        var exits = new ToggleSwitch { Header = "Exits", IsOn = config.ShowExits };
        var filter = new TextBox { Header = "Filter (name pattern)", Text = config.Filter ?? "", PlaceholderText = "All processes" };
        panel.Children.Add(Row(rows, starts, exits));
        panel.Children.Add(filter);
        return () =>
        {
            if (!starts.IsOn && !exits.IsOn)
                return "Show starts, exits, or both.";
            config.MaxRows = double.IsNaN(rows.Value) ? 30 : (int)rows.Value;
            config.ShowStarts = starts.IsOn;
            config.ShowExits = exits.IsOn;
            config.Filter = string.IsNullOrWhiteSpace(filter.Text) ? null : filter.Text.Trim();
            return null;
        };
    }

    private static Func<string?> BuildValueList(StackPanel panel, ValueListWidgetConfig config, EditorContext context)
    {
        var list = new StackPanel { Spacing = 8 };
        var editors = new List<SeriesEditor>();

        void AddEditor(SeriesRef source)
        {
            var editor = new SeriesEditor(source, null, null, context, showLabelAndColor: false);
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, VerticalAlignment = VerticalAlignment.Bottom };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { editor, remove } };
            remove.Click += (_, _) =>
            {
                editors.Remove(editor);
                list.Children.Remove(row);
            };
            editors.Add(editor);
            list.Children.Add(row);
        }

        void Fill(IEnumerable<SeriesRef> items)
        {
            editors.Clear();
            list.Children.Clear();
            foreach (var item in items)
                AddEditor(item);
        }

        Fill(config.Items);
        var add = new Button { Content = "Add value" };
        add.Click += (_, _) => AddEditor(new SystemSeries(SystemMetric.Cpu));
        var reset = new Button { Content = "Reset to kernel overview" };
        reset.Click += (_, _) => Fill(ValueListWidgetConfig.KernelOverview());
        panel.Children.Add(list);
        panel.Children.Add(Row(add, reset));

        return () =>
        {
            var items = new List<SeriesRef>();
            foreach (var editor in editors)
            {
                var source = editor.Build(out var error);
                if (source is null)
                    return error;
                items.Add(source);
            }
            config.Items = items;
            return null;
        };
    }

    /// <summary>The plugin's own settings editor, if it has one; its errors are contained like the widget's.</summary>
    private static Func<string?> BuildPlugin(StackPanel panel, PluginWidgetConfig config)
    {
        if (Plugins.PluginRuntime.Find(config.Plugin, config.Widget) is not { } plugin)
        {
            panel.Children.Add(new TextBlock { Text = $"Plugin '{config.Plugin}' is not loaded: its settings are kept as they are.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
            return () => null;
        }
        Sdk.IPluginSettingsEditor? editor;
        try
        {
            editor = plugin.CreateSettingsEditor(new Plugins.PluginWidgetContext(config, saved: null));
        }
        catch (Exception e)
        {
            panel.Children.Add(new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = true, Title = "Plugin settings", Message = e.Message });
            return () => null;
        }
        if (editor is not null)
            panel.Children.Add(editor.View);
        return () =>
        {
            try
            {
                return editor?.Apply();
            }
            catch (Exception e)
            {
                return $"Plugin settings: {e.Message}";
            }
        };
    }

    private static ComboBox WindowPicker(string header, int seconds)
    {
        var picker = new ComboBox { Header = header, MinWidth = 140 };
        foreach (var (label, s) in Windows)
            picker.Items.Add(new ComboBoxItem { Content = label, Tag = s });
        picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == seconds) ?? picker.Items[2];
        return picker;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }
}

/// <summary>All process fields as checkable rows; visible columns first in display order, reorderable with up/down.</summary>
internal sealed class ColumnListEditor : StackPanel
{
    private readonly List<(ProcessField Field, CheckBox Check, double Width, Grid Row)> _rows = [];

    public ColumnListEditor(IReadOnlyList<TableColumnConfig> current)
    {
        Spacing = 2;
        var widths = current.ToDictionary(c => c.Field, c => c.Width);
        var order = current.Select(c => c.Field).Concat(Enum.GetValues<ProcessField>().Except(current.Select(c => c.Field)));
        foreach (var field in order)
            AddRow(field, widths.ContainsKey(field), widths.GetValueOrDefault(field, MetricCatalog.DefaultColumnWidth(field)));
    }

    public List<TableColumnConfig> Build() =>
        _rows.Where(r => r.Check.IsChecked == true).Select(r => new TableColumnConfig { Field = r.Field, Width = r.Width }).ToList();

    private void AddRow(ProcessField field, bool visible, double width)
    {
        var check = new CheckBox { Content = MetricCatalog.Get(field).DisplayName, IsChecked = visible, MinWidth = 0 };
        ToolTipService.SetToolTip(check, DocView.Tooltip(Docs.Of(field), details: true));
        var up = new Button { Content = new FontIcon { Glyph = "", FontSize = 10 }, Padding = new Thickness(6, 4, 6, 4) };
        var down = new Button { Content = new FontIcon { Glyph = "", FontSize = 10 }, Padding = new Thickness(6, 4, 6, 4) };
        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(check);
        Grid.SetColumn(up, 1);
        Grid.SetColumn(down, 2);
        row.Children.Add(up);
        row.Children.Add(down);
        up.Click += (_, _) => Move(row, -1);
        down.Click += (_, _) => Move(row, +1);
        _rows.Add((field, check, width, row));
        Children.Add(row);
    }

    private void Move(Grid row, int delta)
    {
        var index = _rows.FindIndex(r => r.Row == row);
        var target = index + delta;
        if (target < 0 || target >= _rows.Count)
            return;
        (_rows[index], _rows[target]) = (_rows[target], _rows[index]);
        Children.Move((uint)index, (uint)target);
    }
}
