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

/// <summary>Edits one series: source (system / CPU / process / kernel object / GPU) × metric × target, plus label and color.</summary>
internal sealed class SeriesEditor : StackPanel
{
    private enum Scope { System, Cpu, Process, KernelObject, Gpu }

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
            _scope.Items.Add(new ComboBoxItem { Content = ScopeName(scope), Tag = scope });
        }
        _scope.SelectionChanged += (_, _) => OnScopeChanged();
        _metric.SelectionChanged += (_, _) =>
        {
            UpdateTargets();
            _doc.Show((_metric.SelectedItem as ComboBoxItem)?.Tag is { } metric ? DocOf(metric) : null);
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

        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _scope, _adapter, _metric } });
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _core, _pattern, _objectType, _engine } });
        Children.Add(_doc);
        if (showLabelAndColor)
            Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _label, _color } });

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
            _ => (Scope.System, SystemMetric.Cpu),
        };
        _core.Value = source is CoreSeries cs ? cs.Core : 0;
        if (source is ProcessSeries ps)
            _pattern.Text = ps.Pattern;
        if (source is ObjectTypeSeries os)
            _objectType.Text = os.TypeName;
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
        var previous = (_metric.SelectedItem as ComboBoxItem)?.Content as string;
        _metric.Items.Clear();
        IEnumerable<(object Value, MetricInfo Info)> metrics = SelectedScope switch
        {
            Scope.System => Enum.GetValues<SystemMetric>().Select(m => ((object)m, MetricCatalog.Get(m))),
            Scope.Cpu => Enum.GetValues<CoreMetric>().Select(m => ((object)m, MetricCatalog.Get(m))),
            Scope.Process => MetricCatalog.ProcessHistoryFields.Select(f => ((object)f, MetricCatalog.Get(f))),
            Scope.KernelObject => Enum.GetValues<ObjectTypeMetric>().Select(m => ((object)m, new ObjectTypeSeries("", m).Info with { DisplayName = m.ToString() })),
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

    private void UpdateTargets()
    {
        var scope = _scope.SelectedItem is ComboBoxItem { Tag: Scope s } ? s : Scope.System;
        _core.Visibility = Show(scope == Scope.Cpu);
        _pattern.Visibility = Show(scope == Scope.Process);
        _objectType.Visibility = Show(scope == Scope.KernelObject);
        _adapter.Visibility = Show(scope == Scope.Gpu);
        _engine.Visibility = Show(scope == Scope.Gpu && (_metric.SelectedItem as ComboBoxItem)?.Tag is GpuMetric.Engine);
    }

    private static Doc DocOf(object metric) => metric switch
    {
        SystemMetric m => Docs.Of(m),
        CoreMetric m => Docs.Of(m),
        ProcessField f => Docs.Of(f),
        ObjectTypeMetric m => Docs.Of(m),
        GpuMetric m => Docs.Of(m),
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
