using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ClaudeUsage.NockMon;

/// <summary>
/// Claude's service status, at the widget's bottom right: hidden while every service is operational (or the status is not
/// known); otherwise a warning icon, yellow to red with the severity, and the affected services and open incidents on click.
/// </summary>
internal sealed partial class StatusBadge : Button
{
    private readonly FontIcon _icon = new() { Glyph = "", FontSize = 13 }; // Warning
    private readonly Flyout _flyout = new();
    private ClaudeStatus? _source;
    private ElementTheme _theme;

    public StatusBadge()
    {
        Padding = new Thickness(6, 2, 6, 2);
        MinWidth = 0;
        MinHeight = 0;
        Content = _icon;
        Visibility = Visibility.Collapsed;
        Flyout = _flyout;
        _flyout.Opening += (_, _) => _flyout.Content = _source is { } status ? BuildDetails(status, ActualTheme) : null;
    }

    /// <summary>At each widget refresh; does nothing until the service publishes another reading (or the theme changes).</summary>
    public void Refresh()
    {
        var status = StatusService.Current;
        if (status is not null && DateTimeOffset.UtcNow - status.CheckedAt > StatusService.StaleAfter)
            status = null; // Unknown rather than an old reading.
        if (ReferenceEquals(status, _source) && ActualTheme == _theme)
            return;
        _source = status;
        _theme = ActualTheme;
        var troubled = status is { Severity: > StatusSeverity.Operational };
        Visibility = troubled ? Visibility.Visible : Visibility.Collapsed;
        if (!troubled)
        {
            _flyout.Hide();
            return;
        }
        _icon.Foreground = Color(status!.Severity, _theme);
        ToolTipService.SetToolTip(this, $"Claude status: {status.Description}");
    }

    private static UIElement BuildDetails(ClaudeStatus status, ElementTheme theme)
    {
        var link = new Hyperlink { NavigateUri = new Uri(StatusService.PageUrl) };
        link.Inlines.Add(new Run { Text = "status.claude.com" });
        var source = new TextBlock { FontSize = 12 };
        source.Inlines.Add(link);

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = status.Description, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(source, 1);
        header.Children.Add(source);

        var list = new StackPanel { Spacing = 10 };
        foreach (var c in status.Affected)
        {
            list.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 12, Foreground = Color(c.Severity, theme) },
                    new TextBlock { Text = c.Name, FontSize = 13 },
                    new TextBlock { Text = StatusService.Describe(c.Status), FontSize = 13, Opacity = 0.7 },
                },
            });
        }
        foreach (var incident in status.Incidents)
        {
            var title = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, FontWeight = FontWeights.SemiBold };
            if (incident.Url is { } url)
            {
                // https only (checked when parsed): the link comes from a fetched page, and NockMon runs as administrator.
                var open = new Hyperlink { NavigateUri = new Uri(url) };
                open.Inlines.Add(new Run { Text = incident.Name });
                title.Inlines.Add(open);
            }
            else
            {
                title.Text = incident.Name;
            }
            var meta = incident.StartedAt is { } at
                ? $"{Capitalize(incident.Status)} · since {at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}"
                : Capitalize(incident.Status);
            var entry = new StackPanel
            {
                Spacing = 2,
                Children = { title, new TextBlock { Text = meta, FontSize = 11, Opacity = 0.65 } },
            };
            if (incident.LatestUpdate is { } update)
                entry.Children.Add(new TextBlock { Text = update, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
            list.Children.Add(entry);
        }

        return new StackPanel
        {
            Width = 360,
            Spacing = 10,
            Children = { header, new ScrollViewer { MaxHeight = 420, Content = list, Padding = new Thickness(0, 0, 12, 0) } },
        };
    }

    /// <summary>Yellow, orange, red; darker on light backgrounds so the icon stays visible.</summary>
    private static SolidColorBrush Color(StatusSeverity severity, ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        var (r, g, b) = severity switch
        {
            StatusSeverity.Minor => dark ? (0xF2, 0xC9, 0x4C) : (0xB8, 0x86, 0x00),
            StatusSeverity.Major => dark ? (0xFF, 0x9A, 0x3C) : (0xD0, 0x5E, 0x00),
            _ => dark ? (0xFF, 0x6B, 0x6B) : (0xC4, 0x2B, 0x1C),
        };
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, (byte)r, (byte)g, (byte)b));
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
