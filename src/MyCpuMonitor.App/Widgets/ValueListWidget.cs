using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Monitoring;

namespace MyCpuMonitor.Widgets;

/// <summary>Label / latest value rows for a list of series, read in one engine lock.</summary>
public sealed partial class ValueListWidget : UserControl, IMonitorWidget
{
    private readonly MonitorService _monitor;
    private readonly SeriesRef[] _items;
    private readonly TextBlock[] _values;
    private readonly float[] _latest;
    private readonly bool[] _failed;

    public ValueListWidget(ValueListWidgetConfig config, MonitorService monitor)
    {
        _monitor = monitor;
        _items = config.Items.ToArray();
        _values = new TextBlock[_items.Length];
        _latest = new float[_items.Length];
        _failed = new bool[_items.Length];

        var grid = new Grid { Padding = new Thickness(8, 0, 8, 6), ColumnSpacing = 12, RowSpacing = 1 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        for (var i = 0; i < _items.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = _items[i].DefaultLabel, FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(label, _items[i].Info.DisplayName);
            Grid.SetRow(label, i);
            grid.Children.Add(label);
            _values[i] = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(_values[i], i);
            Grid.SetColumn(_values[i], 1);
            grid.Children.Add(_values[i]);
        }
        Content = new ScrollViewer { Content = grid };
    }

    public event Action? ConfigChanged
    {
        add { }
        remove { }
    }

    public void Refresh()
    {
        _monitor.Engine.Read(this, static (w, _, history) =>
        {
            for (var i = 0; i < w._items.Length; i++)
            {
                try
                {
                    w._failed[i] = history.Read(w._items[i], w._latest.AsSpan(i, 1)) == 0;
                }
                catch (ArgumentException)
                {
                    w._failed[i] = true;
                }
            }
            return 0;
        });

        for (var i = 0; i < _items.Length; i++)
        {
            var text = _failed[i] ? "–" : _monitor.Format(_latest[i], _items[i].Info.Unit);
            if (_values[i].Text != text)
                _values[i].Text = text;
        }
    }
}
