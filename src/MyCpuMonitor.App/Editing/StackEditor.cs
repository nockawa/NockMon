using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Persistence;

namespace MyCpuMonitor.Editing;

/// <summary>
/// Stack dialog: auto-cycle interval and the items (reorder, remove, configure). Edits a clone. "Configure" on an item
/// applies the dialog and asks the caller to open that item's editor (a dialog cannot open another one).
/// </summary>
internal static class StackEditor
{
    public sealed record Result(StackWidgetConfig Stack, int? ConfigureIndex);

    public static async Task<Result?> EditAsync(XamlRoot root, StackWidgetConfig config)
    {
        var clone = LayoutStore.Clone(config);
        clone.CurrentIndex = config.CurrentIndex;
        var items = clone.Items.ToList();
        var current = items.Count > 0 ? items[Math.Clamp(config.CurrentIndex, 0, items.Count - 1)] : null;
        int? configure = null;

        var cycle = new NumberBox
        {
            Header = "Auto-cycle every (seconds, 0 = off)",
            Value = clone.CycleSeconds,
            Minimum = 0,
            Maximum = 3600,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var list = new StackPanel { Spacing = 4 };
        var panel = new StackPanel
        {
            Spacing = 12,
            MinWidth = 420,
            Children =
            {
                cycle,
                new TextBlock { Text = "Widgets", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                list,
            },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Configure stack",
            Content = new ScrollViewer { Content = panel, MaxHeight = 640, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        void Rebuild()
        {
            list.Children.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                var index = i;
                var item = items[i];
                var row = new Grid { ColumnSpacing = 4 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (var c = 0; c < 4; c++)
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var kind = WidgetCatalog.DisplayName(item);
                row.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(item.Title) ? kind : $"{item.Title}  ({kind.ToLowerInvariant()})",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                AddButton(row, 1, "", "Move up", index > 0, () => (items[index - 1], items[index]) = (items[index], items[index - 1]));
                AddButton(row, 2, "", "Move down", index < items.Count - 1, () => (items[index + 1], items[index]) = (items[index], items[index + 1]));
                AddButton(row, 3, "", "Configure", true, () =>
                {
                    configure = index;
                    dialog.Hide(); // Applied below, like the primary button.
                });
                AddButton(row, 4, "", "Remove", items.Count > 1, () => items.RemoveAt(index));
                list.Children.Add(row);
            }
        }

        void AddButton(Grid row, int column, string glyph, string tooltip, bool enabled, Action action)
        {
            var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(6, 4, 6, 4), IsEnabled = enabled };
            ToolTipService.SetToolTip(button, tooltip);
            button.Click += (_, _) =>
            {
                action();
                Rebuild();
            };
            Grid.SetColumn(button, column);
            row.Children.Add(button);
        }

        Rebuild();
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary && configure is null)
            return null;

        clone.CycleSeconds = double.IsNaN(cycle.Value) ? 0 : (int)cycle.Value;
        clone.Items = items;
        // Keep showing the same widget if it is still there.
        clone.CurrentIndex = configure ?? Math.Max(0, current is null ? 0 : items.IndexOf(current));
        return new Result(clone, configure);
    }
}
