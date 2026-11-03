using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Gpu;
using NockMon.Core.Help;
using NockMon.Core.Metrics;
using NockMon.Help;
using NockMon.Widgets;

namespace NockMon.Editing;

public sealed record EditorContext(
    IReadOnlyList<string> ProcessNames,
    int CoreCount,
    IReadOnlyList<string> ObjectTypeNames,
    IReadOnlyList<GpuAdapterInfo> Gpus);

/// <summary>
/// Edits one series: source (system / CPU / process / kernel object / GPU / folder / plugin) × metric × target, plus
/// label and color.
/// </summary>
internal sealed class SeriesEditor : StackPanel
{
    private enum Scope { System, Cpu, Process, KernelObject, Gpu, Folder, Plugin }

    /// <summary>A plugin metric in the metric list (records: compared by value when reloading a series).</summary>
    private sealed record PluginChoice(string Plugin, string Source, string Metric);

    private readonly ComboBox _group = new() { Header = "Group", MinWidth = 260 };
    private PluginSeries? _initialPlugin;

    private readonly EditorContext _context;
    private readonly ComboBox _scope = new() { Header = "Source", MinWidth = 140 };
    private readonly ComboBox _metric = new() { Header = "Metric", MinWidth = 220 };
    private readonly NumberBox _core = new() { Header = "CPU", Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 100 };
    private readonly AutoSuggestBox _pattern = new() { Header = "Process name pattern", PlaceholderText = "devenv; chrome*", MinWidth = 220 };
    private readonly AutoSuggestBox _objectType = new() { Header = "Object type", PlaceholderText = "Token, File, Job…", MinWidth = 220 };
    private readonly ComboBox _adapter = new() { Header = "GPU", MinWidth = 260 };
    private readonly AutoSuggestBox _engine = new() { Header = "Engine pattern", PlaceholderText = "Compute*; 3D; Video*", MinWidth = 200 };
    private readonly TextBox _label = new() { Header = "Label", PlaceholderText = "Automatic", MinWidth = 160 };
    private readonly ComboBox _color = new() { Header = "Color", MinWidth = 120 };
    private readonly FolderScopeEditor _folder = new(new Core.Folders.FolderScope(""));
    private readonly DocPanel _doc = new();

    public SeriesEditor(SeriesRef source, string? label, string? color, EditorContext context, bool showLabelAndColor)
    {
        _context = context;
        Spacing = 8;
        _core.Maximum = Math.Max(0, context.CoreCount - 1);

        foreach (var scope in Enum.GetValues<Scope>())
        {
            if (scope == Scope.Gpu && context.Gpus.Count == 0)
                continue;
            if (scope == Scope.Plugin && ExternalMetricCatalog.Sources.All(s => s.Metrics.Count == 0) && source is not PluginSeries)
                continue;
            _scope.Items.Add(new ComboBoxItem { Content = ScopeName(scope), Tag = scope });
        }
        _scope.SelectionChanged += (_, _) => OnScopeChanged();
        _metric.SelectionChanged += (_, _) =>
        {
            UpdateTargets();
            _doc.Show((_metric.SelectedItem as ComboBoxItem)?.Tag is { } metric ? DocOf(metric) : null);
        };
        _group.SelectionChanged += (_, _) =>
        {
            if (SelectedScope == Scope.Plugin)
                FillPluginMetrics();
        };
        foreach (var gpu in context.Gpus)
            _adapter.Items.Add(new ComboBoxItem { Content = gpu.HasNvml ? $"{gpu.Name} (NVML)" : gpu.Name, Tag = gpu });

        Suggest(_pattern, () => _context.ProcessNames, multiple: true);
        Suggest(_objectType, () => _context.ObjectTypeNames, multiple: false);
        Suggest(_engine, () => (_adapter.SelectedItem as ComboBoxItem)?.Tag is GpuAdapterInfo a ? a.Engines : [], multiple: true);

        _color.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = null });
        foreach (var c in Palette.Series)
        {
            var hex = Palette.ToHex(c);
            _color.Items.Add(new ComboBoxItem
            {
                Tag = hex,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 12, Height = 12, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(c) },
                        new TextBlock { Text = hex },
                    },
                },
            });
        }

        Children.Add(new WrapPanel { Children = { _scope, _adapter, _group, _metric } });
        Children.Add(new WrapPanel { Children = { _core, _pattern, _objectType, _engine } });
        Children.Add(_folder);
        Children.Add(_doc);
        if (showLabelAndColor)
            Children.Add(new WrapPanel { Children = { _label, _color } });

        Load(source);
        _label.Text = label ?? "";
        _color.SelectedItem = _color.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, color, StringComparison.OrdinalIgnoreCase))
                              ?? _color.Items[0];
    }

    public string? Label => string.IsNullOrWhiteSpace(_label.Text) ? null : _label.Text.Trim();
    public string? Color => (_color.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <returns>The series, or null with <paramref name="error"/> set.</returns>
    public SeriesRef? Build(out string? error)
    {
        error = null;
        var metric = (_metric.SelectedItem as ComboBoxItem)?.Tag;
        switch (SelectedScope)
        {
            case Scope.System when metric is SystemMetric m:
                return new SystemSeries(m);
            case Scope.Cpu when metric is CoreMetric m:
                var core = double.IsNaN(_core.Value) ? -1 : (int)_core.Value;
                if (core < 0 || core >= _context.CoreCount)
                {
                    error = $"CPU must be between 0 and {_context.CoreCount - 1}.";
                    return null;
                }
                return new CoreSeries(m, core);
            case Scope.Process when metric is ProcessField f:
                if (string.IsNullOrWhiteSpace(_pattern.Text))
                {
                    error = "Enter a process name pattern.";
                    return null;
                }
                return new ProcessSeries(f, _pattern.Text.Trim());
            case Scope.KernelObject when metric is ObjectTypeMetric m:
                if (string.IsNullOrWhiteSpace(_objectType.Text))
                {
                    error = "Enter an object type.";
                    return null;
                }
                return new ObjectTypeSeries(_objectType.Text.Trim(), m);
            case Scope.Gpu when metric is GpuMetric m:
                if ((_adapter.SelectedItem as ComboBoxItem)?.Tag is not GpuAdapterInfo adapter)
                {
                    error = "Select a GPU.";
                    return null;
                }
                if (m == GpuMetric.Engine && string.IsNullOrWhiteSpace(_engine.Text))
                {
                    error = "Enter an engine pattern (e.g. Compute*).";
                    return null;
                }
                return new GpuSeries(m, adapter.Name, m == GpuMetric.Engine ? _engine.Text.Trim() : null);
            case Scope.Folder when metric is FolderMetric m:
                if (_folder.Build(out error) is not { } folder)
                    return null;
                return new FolderSeries(m, folder.Path, folder.Recursive, folder.Include, folder.Exclude, folder.SizeOnDisk);
            case Scope.Plugin when metric is PluginChoice c:
                return new PluginSeries(c.Plugin, c.Source, c.Metric);
            default:
                error = "Select a metric.";
                return null;
        }
    }

    private Scope SelectedScope => (Scope)((ComboBoxItem)_scope.SelectedItem).Tag;

    private static string ScopeName(Scope scope) => scope switch
    {
        Scope.Cpu => "CPU (logical)",
        Scope.KernelObject => "Kernel object",
        Scope.Gpu => "GPU",
        Scope.Plugin => "Sensors and plugins",
        _ => scope.ToString(),
    };

    private void Load(SeriesRef source)
    {
        var (scope, metric) = source switch
        {
            SystemSeries s => (Scope.System, (object)s.Metric),
            CoreSeries s => (Scope.Cpu, s.Metric),
            ProcessSeries s => (Scope.Process, s.Field),
            ObjectTypeSeries s => (Scope.KernelObject, s.Metric),
            GpuSeries s => (Scope.Gpu, s.Metric),
            FolderSeries s => (Scope.Folder, s.Metric),
            PluginSeries s => (Scope.Plugin, new PluginChoice(s.Plugin, s.Source, s.Metric)),
            _ => (Scope.System, SystemMetric.Cpu),
        };
        _core.Value = source is CoreSeries cs ? cs.Core : 0;
        if (source is ProcessSeries ps)
            _pattern.Text = ps.Pattern;
        if (source is ObjectTypeSeries os)
            _objectType.Text = os.TypeName;
        if (source is FolderSeries fs)
            _folder.Load(fs.Scope);
        _initialPlugin = source as PluginSeries;
        if (source is GpuSeries gs)
        {
            _adapter.SelectedItem = _adapter.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((GpuAdapterInfo)i.Tag).Name == gs.Adapter);
            _engine.Text = gs.Engine ?? "";
        }
        _adapter.SelectedIndex = _adapter.SelectedIndex < 0 && _adapter.Items.Count > 0 ? 0 : _adapter.SelectedIndex;

        _scope.SelectedItem = _scope.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (Scope)i.Tag == scope) ?? _scope.Items[0]; // Fills the metrics.
        _metric.SelectedItem = _metric.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, metric)) ?? _metric.Items.FirstOrDefault();
        UpdateTargets();
    }

    private void OnScopeChanged()
    {
        if (SelectedScope == Scope.Plugin)
        {
            FillPluginGroups();
            UpdateTargets();
            return;
        }
        var previous = (_metric.SelectedItem as ComboBoxItem)?.Content as string;
        _metric.Items.Clear();
        IEnumerable<(object Value, MetricInfo Info)> metrics = SelectedScope switch
        {
            Scope.System => Enum.GetValues<SystemMetric>().Select(m => ((object)m, MetricCatalog.Get(m))),
            Scope.Cpu => Enum.GetValues<CoreMetric>().Select(m => ((object)m, MetricCatalog.Get(m))),
            Scope.Process => MetricCatalog.ProcessHistoryFields.Select(f => ((object)f, MetricCatalog.Get(f))),
            Scope.KernelObject => Enum.GetValues<ObjectTypeMetric>().Select(m => ((object)m, new ObjectTypeSeries("", m).Info with { DisplayName = m.ToString() })),
            Scope.Folder => Enum.GetValues<FolderMetric>().Select(m => ((object)m, Core.Folders.FolderScope.Info(m))),
            _ => Enum.GetValues<GpuMetric>().Select(m => ((object)m, MetricCatalog.Get(m))),
        };
        foreach (var (value, info) in metrics)
        {
            var item = new ComboBoxItem { Content = info.DisplayName, Tag = value };
            ToolTipService.SetToolTip(item, DocView.Tooltip(DocOf(value)));
            _metric.Items.Add(item);
        }
        _metric.SelectedItem = _metric.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == previous) ?? _metric.Items[0];
        UpdateTargets();
    }

    /// <summary>The groups of the plugins' metrics ("source: group" when several sources), the series' own group first selected.</summary>
    private void FillPluginGroups()
    {
        _group.Items.Clear();
        var sources = ExternalMetricCatalog.Sources;
        foreach (var source in sources)
        {
            foreach (var group in source.Metrics.Select(m => m.Group).Distinct())
            {
                _group.Items.Add(new ComboBoxItem
                {
                    Content = sources.Count > 1 ? $"{source.DisplayName}: {group}" : group,
                    Tag = (source, group),
                });
            }
        }
        var initial = _initialPlugin is { } p ? ExternalMetricCatalog.Find(p.Plugin, p.Source, p.Metric) : null;
        _group.SelectedItem = _group.Items.Cast<ComboBoxItem>().FirstOrDefault(i =>
                                  i.Tag is (ExternalSourceInfo s, string g) && initial is not null && g == initial.Group &&
                                  string.Equals(s.Plugin, _initialPlugin!.Plugin, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(s.Source, _initialPlugin.Source, StringComparison.OrdinalIgnoreCase))
                              ?? _group.Items.FirstOrDefault();
        FillPluginMetrics();
    }

    private void FillPluginMetrics()
    {
        _metric.Items.Clear();
        if (_group.SelectedItem is ComboBoxItem { Tag: (ExternalSourceInfo source, string group) })
        {
            foreach (var m in source.Metrics.Where(m => m.Group == group))
            {
                var choice = new PluginChoice(source.Plugin, source.Source, m.Id);
                var item = new ComboBoxItem { Content = m.ShortName, Tag = choice };
                ToolTipService.SetToolTip(item, DocView.Tooltip(DocOf(choice)));
                _metric.Items.Add(item);
            }
        }
        if (_initialPlugin is { } p && _metric.Items.Count == 0)
        {
            // The plugin is not loaded: keep the series as it is.
            _metric.Items.Add(new ComboBoxItem { Content = p.Metric, Tag = new PluginChoice(p.Plugin, p.Source, p.Metric) });
        }
        var wanted = _initialPlugin is { } q ? new PluginChoice(q.Plugin, q.Source, q.Metric) : null;
        _metric.SelectedItem = _metric.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, wanted)) ?? _metric.Items.FirstOrDefault();
    }

    private void UpdateTargets()
    {
        var scope = _scope.SelectedItem is ComboBoxItem { Tag: Scope s } ? s : Scope.System;
        _core.Visibility = Show(scope == Scope.Cpu);
        _pattern.Visibility = Show(scope == Scope.Process);
        _objectType.Visibility = Show(scope == Scope.KernelObject);
        _adapter.Visibility = Show(scope == Scope.Gpu);
        _engine.Visibility = Show(scope == Scope.Gpu && (_metric.SelectedItem as ComboBoxItem)?.Tag is GpuMetric.Engine);
        _folder.Visibility = Show(scope == Scope.Folder);
        _group.Visibility = Show(scope == Scope.Plugin);
    }

    private static Doc DocOf(object metric) => metric switch
    {
        SystemMetric m => Docs.Of(m),
        CoreMetric m => Docs.Of(m),
        ProcessField f => Docs.Of(f),
        ObjectTypeMetric m => Docs.Of(m),
        GpuMetric m => Docs.Of(m),
        FolderMetric m => Docs.Of(m),
        PluginChoice c => Docs.Of(new PluginSeries(c.Plugin, c.Source, c.Metric)),
        _ => throw new ArgumentException($"Unknown metric {metric}.", nameof(metric)),
    };

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Autocompletes from <paramref name="source"/>; with <paramref name="multiple"/>, the last ';'-separated term.</summary>
    private static void Suggest(AutoSuggestBox box, Func<IEnumerable<string>> source, bool multiple)
    {
        box.TextChanged += (b, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            var term = multiple ? b.Text.Split(';').Last().Trim() : b.Text.Trim();
            b.ItemsSource = source()
                .Where(n => n.Contains(term, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
        };
        box.SuggestionChosen += (b, args) =>
        {
            if (!multiple)
            {
                b.Text = (string)args.SelectedItem;
                return;
            }
            var parts = b.Text.Split(';').Select(p => p.Trim()).ToList();
            parts[^1] = (string)args.SelectedItem;
            b.Text = string.Join("; ", parts);
        };
    }
}
