using System.Globalization;
using System.Text.Json.Nodes;
using LibreHardwareMonitor.Hardware;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NockMon.Sdk;

namespace HardwareSensors.NockMon;

/// <summary>Every sensor of the chosen kinds, grouped by hardware, with its current value.</summary>
public sealed class SensorsWidgetPlugin : IWidgetPlugin
{
    internal const string KindsSetting = "kinds";
    internal const string HideIdleFansSetting = "hideIdleFans";

    /// <summary>The kinds offered, with their default (shown on a new widget).</summary>
    internal static readonly (SensorType Type, string Name, bool Default)[] Kinds =
    [
        (SensorType.Temperature, "Temperatures", true),
        (SensorType.Fan, "Fan speeds", true),
        (SensorType.Control, "Fan duty", false),
        (SensorType.Voltage, "Voltages", false),
        (SensorType.Power, "Power", true),
        (SensorType.Current, "Currents", false),
    ];

    public string Id => "table";
    public string DisplayName => "Hardware sensors";
    public string Summary => "Temperatures, fan speeds, voltages and power of the CPU, motherboard, GPUs and memory, grouped by hardware.";

    public string Details => """
        The machine's sensors, read by **LibreHardwareMonitor**: CPU temperatures and power, the motherboard's sensor chip (temperatures, fans, voltages), GPUs and memory modules. Choose the kinds shown in the settings.

        - Every sensor is also a NockMon metric: add it to a **Chart**, **Value** or **Value list** (series source **Sensors and plugins**), with its history.
        - Not repeated here: what NockMon reads itself. Drive temperatures (source **Sensors and plugins**, group of the drive), and for NVIDIA GPUs the temperature, fans and power (source **GPU**). The hot spot, memory junction and core voltage come from this plugin.
        - The motherboard chip names its sensors generically ("Temperature #2", "Fan #3"): rename them per series (the series label) once you know which is which, from the BIOS or by watching them.
        - Fans reading 0 rpm are hidden by default (headers with nothing connected, or fans stopped at low temperature).

        **Requirements:** NockMon runs as administrator (done). CPU and motherboard sensors need the **PawnIO** driver: `winget install namazso.PawnIO`, then restart NockMon. Read-only: the plugin never changes fan speeds.
        """;

    public WidgetSize DefaultSize => new(5, 8);
    public WidgetSize MinSize => new(3, 3);

    public IPluginWidget CreateWidget(IWidgetContext context) => new SensorsWidget(context);

    public IPluginSettingsEditor? CreateSettingsEditor(IWidgetContext context) => new SettingsEditor(context);

    internal static HashSet<SensorType> SelectedKinds(IWidgetContext context) =>
        context.Settings[KindsSetting] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var name) && Enum.TryParse<SensorType>(name, out var t) ? t : (SensorType?)null)
                .OfType<SensorType>().ToHashSet()
            : Kinds.Where(k => k.Default).Select(k => k.Type).ToHashSet();

    internal static bool HideIdleFans(IWidgetContext context) =>
        context.Settings[HideIdleFansSetting] is not JsonValue v || !v.TryGetValue<bool>(out var hide) || hide;

    private sealed class SettingsEditor : IPluginSettingsEditor
    {
        private readonly IWidgetContext _context;
        private readonly List<(SensorType Type, CheckBox Box)> _kinds = [];
        private readonly CheckBox _hideIdle;

        public SettingsEditor(IWidgetContext context)
        {
            _context = context;
            var selected = SelectedKinds(context);
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = "Show", FontWeight = FontWeights.SemiBold });
            foreach (var (type, name, _) in Kinds)
            {
                var box = new CheckBox { Content = name, IsChecked = selected.Contains(type) };
                _kinds.Add((type, box));
                panel.Children.Add(box);
            }
            _hideIdle = new CheckBox { Content = "Hide fans at 0 rpm", IsChecked = HideIdleFans(context), Margin = new Thickness(0, 8, 0, 0) };
            panel.Children.Add(_hideIdle);
            View = panel;
        }

        public UIElement View { get; }

        public string? Apply()
        {
            var kinds = _kinds.Where(k => k.Box.IsChecked == true).Select(k => (JsonNode)k.Type.ToString()).ToArray();
            if (kinds.Length == 0)
                return "Choose at least one kind of sensor.";
            _context.Settings[KindsSetting] = new JsonArray(kinds);
            _context.Settings[HideIdleFansSetting] = _hideIdle.IsChecked == true;
            return null;
        }
    }
}

/// <summary>
/// The table: a header row per piece of hardware, then name and value per sensor. Rows are built when the sensor list
/// (or a fan's idle state) changes; each refresh only assigns the values that changed.
/// </summary>
internal sealed class SensorsWidget : IPluginWidget
{
    private readonly IWidgetContext _context;
    private readonly HashSet<SensorType> _kinds;
    private readonly bool _hideIdleFans;
    private readonly StackPanel _rows = new() { Spacing = 1, Padding = new Thickness(8, 2, 8, 6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(8, 2, 8, 4) };
    private readonly List<(int Index, TextBlock Value)> _cells = [];
    private IReadOnlyList<Sensor>? _builtFor;
    private string _builtKey = "";

    public SensorsWidget(IWidgetContext context)
    {
        _context = context;
        _kinds = SensorsWidgetPlugin.SelectedKinds(context);
        _hideIdleFans = SensorsWidgetPlugin.HideIdleFans(context);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(_status);
        root.Children.Add(scroll);
        View = root;
        Refresh();
    }

    public UIElement View { get; }

    public void Refresh()
    {
        var status = SensorService.Status;
        _status.Text = status ?? "";
        _status.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        if (!SensorService.IsReady)
            return;

        var sensors = SensorService.Sensors;
        var shown = sensors.Select((s, i) => (Sensor: s, Index: i))
            .Where(x => _kinds.Contains(x.Sensor.Type) && !(_hideIdleFans && x.Sensor.Type == SensorType.Fan && !(SensorService.Value(x.Index) > 0)))
            .ToList();
        var key = string.Join(',', shown.Select(x => x.Index)); // Rebuilt when a fan starts or stops.
        if (!ReferenceEquals(sensors, _builtFor) || key != _builtKey)
            Build(sensors, shown, key);

        foreach (var (index, cell) in _cells)
        {
            var text = Format(SensorService.Value(index), sensors[index].Metric.Unit);
            if (cell.Text != text)
                cell.Text = text;
        }
    }

    private void Build(IReadOnlyList<Sensor> sensors, List<(Sensor Sensor, int Index)> shown, string key)
    {
        _builtFor = sensors;
        _builtKey = key;
        _rows.Children.Clear();
        _cells.Clear();
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        foreach (var group in shown.GroupBy(x => x.Sensor.Metric.Group))
        {
            _rows.Children.Add(new TextBlock
            {
                Text = group.Key, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, _rows.Children.Count == 0 ? 0 : 6, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            foreach (var (sensor, index) in group)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock { Text = sensor.Metric.ShortName, FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis };
                var value = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
                ToolTipService.SetToolTip(name, $"{sensor.Metric.Name}\n{sensor.Metric.Id}");
                Grid.SetColumn(value, 1);
                row.Children.Add(name);
                row.Children.Add(value);
                _rows.Children.Add(row);
                _cells.Add((index, value));
            }
        }
        if (shown.Count == 0)
            _rows.Children.Add(new TextBlock { Text = "No sensor of the chosen kinds.", FontSize = 12, Foreground = secondary });
    }

    private static string Format(double value, PluginMetricUnit unit)
    {
        if (double.IsNaN(value))
            return "–";
        var c = CultureInfo.CurrentCulture;
        return unit switch
        {
            PluginMetricUnit.Celsius => value.ToString("0.0", c) + " °C",
            PluginMetricUnit.Rpm => value.ToString("#,0", c) + " rpm",
            PluginMetricUnit.Percent => value.ToString("0", c) + " %",
            PluginMetricUnit.Volts => value.ToString("0.000", c) + " V",
            PluginMetricUnit.Amperes => value.ToString("0.00", c) + " A",
            PluginMetricUnit.Watts => value.ToString("0.0", c) + " W",
            _ => value.ToString("0.##", c),
        };
    }

    public void Dispose()
    {
    }
}
