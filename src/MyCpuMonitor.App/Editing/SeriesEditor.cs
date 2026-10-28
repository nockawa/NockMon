using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Widgets;

namespace MyCpuMonitor.Editing;

public sealed record EditorContext(IReadOnlyList<string> ProcessNames, int CoreCount);

/// <summary>Edits one series: scope (system / core / process) × metric × target, plus label and color.</summary>
internal sealed class SeriesEditor : StackPanel
{
    private enum Scope { System, Core, Process }

    private readonly EditorContext _context;
    private readonly ComboBox _scope = new() { Header = "Source", MinWidth = 120 };
    private readonly ComboBox _metric = new() { Header = "Metric", MinWidth = 220 };
    private readonly NumberBox _core = new() { Header = "Core", Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 100 };
    private readonly AutoSuggestBox _pattern = new() { Header = "Process name pattern", PlaceholderText = "devenv; chrome*", MinWidth = 220 };
    private readonly TextBox _label = new() { Header = "Label", PlaceholderText = "Automatic", MinWidth = 160 };
    private readonly ComboBox _color = new() { Header = "Color", MinWidth = 120 };

    public SeriesEditor(SeriesRef source, string? label, string? color, EditorContext context, bool showLabelAndColor)
    {
        _context = context;
        Spacing = 8;
        _core.Maximum = Math.Max(0, context.CoreCount - 1);

        foreach (var scope in Enum.GetValues<Scope>())
            _scope.Items.Add(new ComboBoxItem { Content = scope.ToString(), Tag = scope });
        _scope.SelectionChanged += (_, _) => OnScopeChanged();

        _pattern.TextChanged += (box, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            var last = box.Text.Split(';').Last().Trim();
            box.ItemsSource = _context.ProcessNames
                .Where(n => n.Contains(last, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
        };
        _pattern.SuggestionChosen += (box, args) =>
        {
            var parts = box.Text.Split(';').Select(p => p.Trim()).ToList();
            parts[^1] = (string)args.SelectedItem;
            box.Text = string.Join("; ", parts);
        };

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

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _scope, _metric } };
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _core, _pattern } };
        Children.Add(row1);
        Children.Add(row2);
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
            case Scope.Core when metric is CoreMetric m:
                var core = double.IsNaN(_core.Value) ? -1 : (int)_core.Value;
                if (core < 0 || core >= _context.CoreCount)
                {
                    error = $"Core must be between 0 and {_context.CoreCount - 1}.";
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
            default:
                error = "Select a metric.";
                return null;
        }
    }

    private Scope SelectedScope => (Scope)((ComboBoxItem)_scope.SelectedItem).Tag;

    private void Load(SeriesRef source)
    {
        var (scope, metric) = source switch
        {
            SystemSeries s => (Scope.System, (object)s.Metric),
            CoreSeries s => (Scope.Core, s.Metric),
            ProcessSeries s => (Scope.Process, s.Field),
            _ => (Scope.System, SystemMetric.Cpu),
        };
        if (source is CoreSeries cs)
            _core.Value = cs.Core;
        else
            _core.Value = 0;
        if (source is ProcessSeries ps)
            _pattern.Text = ps.Pattern;

        _scope.SelectedIndex = (int)scope; // Fills the metric list.
        _metric.SelectedItem = _metric.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, metric)) ?? _metric.Items.FirstOrDefault();
    }

    private void OnScopeChanged()
    {
        var previous = (_metric.SelectedItem as ComboBoxItem)?.Content as string;
        _metric.Items.Clear();
        IEnumerable<(object Value, MetricInfo Info)> metrics = SelectedScope switch
        {
            Scope.System => Enum.GetValues<SystemMetric>().Select(m => ((object)m, MetricCatalog.Get(m))),
            Scope.Core => Enum.GetValues<CoreMetric>().Select(m => ((object)m, MetricCatalog.Get(m))),
            _ => MetricCatalog.ProcessHistoryFields.Select(f => ((object)f, MetricCatalog.Get(f))),
        };
        foreach (var (value, info) in metrics)
            _metric.Items.Add(new ComboBoxItem { Content = info.DisplayName, Tag = value });
        _metric.SelectedItem = _metric.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == previous) ?? _metric.Items[0];

        _core.Visibility = SelectedScope == Scope.Core ? Visibility.Visible : Visibility.Collapsed;
        _pattern.Visibility = SelectedScope == Scope.Process ? Visibility.Visible : Visibility.Collapsed;
    }
}
