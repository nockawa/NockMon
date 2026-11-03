using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using ServiceStatus.NockMon.Status;

namespace ServiceStatus.NockMon;

/// <summary>The pieces the widget is built from: colors, glyphs, a service's row, the detailed view.</summary>
internal static class StatusViews
{
    private const int MaxComponentsShown = 8;

    /// <summary>Green, blue, yellow, orange, red, grey; darker on light backgrounds so text stays readable.</summary>
    public static SolidColorBrush Brush(Severity severity, ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        var (r, g, b) = severity switch
        {
            Severity.Operational => dark ? (0x8F, 0xCF, 0xA8) : (0x3E, 0x8E, 0x5E),
            Severity.Maintenance => dark ? (0x7F, 0xB6, 0xF0) : (0x2B, 0x6C, 0xB8),
            Severity.Degraded => dark ? (0xF2, 0xC9, 0x4C) : (0xB8, 0x86, 0x00),
            Severity.PartialOutage => dark ? (0xFF, 0x9A, 0x3C) : (0xD0, 0x5E, 0x00),
            Severity.MajorOutage => dark ? (0xFF, 0x6B, 0x6B) : (0xC4, 0x2B, 0x1C),
            _ => dark ? (0x9A, 0xA0, 0xA8) : (0x80, 0x86, 0x8E),
        };
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, (byte)r, (byte)g, (byte)b));
    }

    public static string Glyph(Severity severity) => severity switch
    {
        Severity.Operational => "", // CheckMark
        Severity.Maintenance => "", // Repair
        Severity.Unknown => "", // Unknown
        _ => "", // Warning
    };

    /// <summary>One line per service: dot, name, a change marker, the status word.</summary>
    public static Grid Row(ServiceState state, bool changed, ElementTheme theme)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brush = Brush(state.Severity, theme);
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = state.Entry.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        if (changed)
            name.Children.Add(ChangeMarker());
        var word = new TextBlock
        {
            Text = Evaluation.Describe(state.Severity),
            FontSize = 12,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Place(row, new TextBlock { Text = "●", FontSize = 10, Foreground = brush, VerticalAlignment = VerticalAlignment.Center }, 0);
        Place(row, name, 1);
        Place(row, word, 2);
        return row;
    }

    /// <summary>One line on why a service isn't operational: its worst incident, affected components, maintenance.</summary>
    public static string? Brief(ServiceState state)
    {
        if (state.Severity == Severity.Unknown)
            return state.ReadAt is { } at ? $"No reading since {Time(at)}" : state.Error is { } e ? e : "Not read yet";
        if (state.Incidents.FirstOrDefault() is { } incident)
            return state.Incidents.Count > 1 ? $"{incident.Name} (+{state.Incidents.Count - 1})" : incident.Name;
        if (state.Affected.Count > 0)
            return string.Join(", ", state.Affected.Take(3).Select(c => c.Name)) + (state.Affected.Count > 3 ? $" +{state.Affected.Count - 3}" : "");
        if (state.Maintenances.FirstOrDefault() is { } maintenance)
            return maintenance.Name;
        return null;
    }

    /// <summary>"Changed since you last looked": cleared when the pointer leaves the widget, or a flyout closes.</summary>
    public static FrameworkElement ChangeMarker()
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(dot, "Changed since you last looked");
        return dot;
    }

    /// <summary>
    /// The services that need explaining (issue, maintenance, unknown), each with its affected components, open incidents
    /// and maintenances; then the operational ones, as rows or as one line.
    /// </summary>
    public static StackPanel Details(IReadOnlyList<ServiceState> states, Func<ServiceState, bool> changed, bool collapseOperational, ElementTheme theme)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var state in states.Where(s => s.Severity != Severity.Operational))
            panel.Children.Add(Service(state, changed(state), theme));
        var fine = states.Where(s => s.Severity == Severity.Operational).ToList();
        if (fine.Count == 0)
            return panel;
        if (collapseOperational && fine.Count > 1)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{fine.Count} operational: {string.Join(", ", fine.Select(s => s.Entry.Name))}",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        else
        {
            var rows = new StackPanel { Spacing = 3 };
            foreach (var state in fine)
                rows.Children.Add(Row(state, changed(state), theme));
            panel.Children.Add(rows);
        }
        return panel;
    }

    private static StackPanel Service(ServiceState state, bool changed, ElementTheme theme)
    {
        var panel = new StackPanel { Spacing = 4 };
        var header = Row(state, changed, theme);
        if (state.Entry.PageUrl is { } page && Uri.TryCreate(page, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            var link = Link(uri);
            link.Inlines.Add(new Run { Text = state.Entry.Name });
            var name = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Inlines.Add(link);
            ToolTipService.SetToolTip(name, "Open the status page");
            ((StackPanel)header.Children[1]).Children[0] = name;
        }
        panel.Children.Add(header);

        var body = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 0, 0) };
        if (state.Severity == Severity.Unknown)
        {
            body.Children.Add(Note(state.ReadAt is { } at
                ? $"No reading since {Time(at)}{(state.Error is { } e ? $": {e}" : ".")}"
                : state.Error is { } error ? $"Not read: {error}" : "Not read yet.", 0.7));
        }
        foreach (var c in state.Affected.Take(MaxComponentsShown))
            body.Children.Add(Component(c, theme));
        if (state.Affected.Count > MaxComponentsShown)
            body.Children.Add(Note($"+{state.Affected.Count - MaxComponentsShown} more components", 0.6));
        foreach (var incident in state.Incidents)
            body.Children.Add(Incident(incident, theme));
        foreach (var maintenance in state.Maintenances)
            body.Children.Add(Maintenance(maintenance, theme));
        if (body.Children.Count > 0)
            panel.Children.Add(body);
        return panel;
    }

    private static StackPanel Component(ComponentReading c, ElementTheme theme) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children =
        {
            new TextBlock { Text = c.Group is { } g && !c.Name.Contains(g, StringComparison.OrdinalIgnoreCase) ? $"{c.Name} ({g})" : c.Name, FontSize = 12, Opacity = 0.85 },
            new TextBlock { Text = Evaluation.Describe(c.Severity), FontSize = 12, Foreground = Brush(c.Severity, theme) },
        },
    };

    private static StackPanel Incident(IncidentReading incident, ElementTheme theme)
    {
        var title = Titled(incident.Name, incident.Url);
        title.Foreground = Brush(incident.Severity, theme);
        var meta = new List<string>();
        if (incident.Status is { Length: > 0 } status && status != "ongoing")
            meta.Add(char.ToUpperInvariant(status[0]) + status[1..].Replace('_', ' '));
        if (incident.StartedAt is { } started)
            meta.Add($"since {Time(started)}");
        else if (incident.UpdatedAt is { } updated)
            meta.Add($"updated {Time(updated)}");
        if (incident.Locations.Count > 0)
            meta.Add(string.Join(", ", incident.Locations.Take(4)) + (incident.Locations.Count > 4 ? "…" : ""));
        var panel = new StackPanel { Spacing = 1, Children = { title } };
        if (meta.Count > 0)
            panel.Children.Add(Note(string.Join(" · ", meta), 0.65, 11));
        if (incident.LatestUpdate is { } update)
            panel.Children.Add(new TextBlock { Text = update, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.WordEllipsis, Opacity = 0.85 });
        return panel;
    }

    private static StackPanel Maintenance(MaintenanceReading maintenance, ElementTheme theme)
    {
        var title = Titled(maintenance.Name, maintenance.Url);
        title.Foreground = Brush(Severity.Maintenance, theme);
        var when = maintenance.InProgress
            ? maintenance.End is { } end ? $"In progress, until {Time(end)}" : "In progress"
            : maintenance.Start is { } start ? $"Scheduled {Time(start)}{(maintenance.End is { } e ? $" to {Time(e)}" : "")}" : "Scheduled";
        return new StackPanel { Spacing = 1, Children = { title, Note(when, 0.65, 11) } };
    }

    private static TextBlock Titled(string text, string? url)
    {
        var title = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            // https only, and only to the page's hosts (checked when parsed): NockMon runs as administrator.
            var link = Link(uri);
            link.Inlines.Add(new Run { Text = text });
            title.Inlines.Add(link);
        }
        else
        {
            title.Text = text;
        }
        return title;
    }

    /// <summary>A link opened as the user (<see cref="Browser"/>): NavigateUri would start the browser as administrator.</summary>
    private static Hyperlink Link(Uri uri)
    {
        var link = new Hyperlink();
        ToolTipService.SetToolTip(link, uri.AbsoluteUri);
        link.Click += (_, _) => Browser.Open(uri);
        return link;
    }

    public static TextBlock Note(string text, double opacity, double size = 12) =>
        new() { Text = text, FontSize = size, Opacity = opacity, TextWrapping = TextWrapping.Wrap };

    /// <summary>Local time; with the day when not today.</summary>
    public static string Time(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("ddd ", CultureInfo.CurrentCulture) + local.ToString("t", CultureInfo.CurrentCulture);
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
