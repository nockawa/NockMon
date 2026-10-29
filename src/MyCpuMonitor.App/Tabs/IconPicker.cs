using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MyCpuMonitor.Tabs;

/// <summary>Picks a Fluent UI System Icon by name: suggestions first, search over the ~2900 names.</summary>
internal static class IconPicker
{
    private const int MaxShown = 240;

    /// <returns>The chosen icon name, or null if cancelled.</returns>
    public static async Task<string?> PickAsync(XamlRoot root, string current)
    {
        var search = new TextBox { PlaceholderText = "Search icons (cpu, chart, game, cloud…)" };
        var grid = new GridView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Height = 360,
            Padding = new Thickness(0),
        };
        var label = new TextBlock { FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var chosen = current;

        void Fill()
        {
            var terms = search.Text.Trim().ToLowerInvariant().Replace(' ', '_');
            var names = terms.Length == 0
                ? FluentIcons.Suggested.Where(FluentIcons.Exists).Concat(FluentIcons.Names.Except(FluentIcons.Suggested))
                : FluentIcons.Names.Where(n => n.Contains(terms, StringComparison.Ordinal)).OrderBy(n => !n.StartsWith(terms, StringComparison.Ordinal)).ThenBy(n => n.Length);
            grid.Items.Clear();
            foreach (var name in names.Take(MaxShown))
            {
                var item = new GridViewItem
                {
                    Tag = name,
                    Width = 44,
                    Height = 44,
                    Content = new FontIcon { FontFamily = FluentIcons.Font, Glyph = FluentIcons.Glyph(name), FontSize = 20 },
                };
                ToolTipService.SetToolTip(item, FluentIcons.DisplayName(name));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, name);
                grid.Items.Add(item);
                if (name == chosen)
                    grid.SelectedItem = item;
            }
            label.Text = grid.Items.Count == 0 ? "No icon matches." : grid.Items.Count == MaxShown ? $"First {MaxShown} matches: refine the search." : $"{grid.Items.Count} icons.";
        }

        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is GridViewItem { Tag: string name })
            {
                chosen = name;
                label.Text = FluentIcons.DisplayName(name);
            }
        };
        search.TextChanged += (_, _) => Fill();
        Fill();

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Tab icon",
            Content = new StackPanel { Spacing = 8, Width = 480, Children = { search, grid, label } },
            PrimaryButtonText = "Use icon",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var picked = false;
        grid.DoubleTapped += (_, _) =>
        {
            picked = true; // Double-click picks, like the primary button.
            dialog.Hide();
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary || picked ? chosen : null;
    }
}
