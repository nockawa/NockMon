using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NockMon.Sdk;
using ServiceStatus.NockMon.Status;

namespace ServiceStatus.NockMon;

/// <summary>The followed services' status pages: one glyph at the smallest size, up to incidents and their updates.</summary>
public sealed class StatusWidgetPlugin : IWidgetPlugin
{
    internal const string ServicesSetting = "services";

    public string Id => "status";
    public string DisplayName => "Service status";
    public string Summary => "The status of the online services you follow (Claude, OpenAI, GitHub, GitLab, Cloudflare, AWS, Azure, Google Cloud…), from their status pages.";

    public string Details => """
        The status pages of the services you pick in the settings, read every 2 minutes (5 for AWS, Azure and Google, whose feeds are heavier), and shown as much as the widget's size allows:

        - **Smallest**: one icon, colored by the worst state among them, and the number of services with an issue. Click it for the details.
        - **One row high**: the icon and the first service with an issue ("OpenAI: partial outage (+2)").
        - **Medium**: one row per service, worst first, with a line under each that isn't operational (its incident, affected components or maintenance). Click a row for its details.
        - **Large**: each service with an issue, its affected components, its open incidents with their latest update, and maintenance in progress or within a day; then the operational ones.

        Every size shows as much as fits: the operational services fold into one line only when space runs out.

        **Colors:** red major outage, orange partial outage, yellow degraded (or an open incident), blue maintenance (shown, not counted as an issue), grey unknown (no reading for 15 minutes or more: never shown as fine), green operational. A dot marks what changed since the pointer last left the widget.

        **Filters:** per service, the components (or products) and regions that count. Some pages list many components that are routinely degraded (Cloudflare's 300 locations, AWS's regions): by default Cloudflare counts its core services only. A filtered service ignores the page's overall indicator.

        **Add by URL:** any Atlassian Statuspage, incident.io or status.io page (most status pages), for every widget of this plugin.

        **History:** tick it next to a service in the settings, and NockMon records its state as a metric (source **Sensors and plugins**, group **Service status**): 0 operational, 1 maintenance, 2 degraded, 3 partial outage, 4 major outage, missing when unknown. Chart it, or let AI agents read when a service degraded. Plugin-wide: a recorded service is read even when no widget shows it.

        **Sources:** each page's public API, no account, no credentials. Conditional requests where the page supports them. AWS's feed is the unofficial one behind health.aws.amazon.com; Azure's RSS lists widespread incidents only; Azure DevOps reports each service per geography.
        """;

    public WidgetSize DefaultSize => new(3, 3);
    public WidgetSize MinSize => new(1, 1);

    public IPluginWidget CreateWidget(IWidgetContext context) => new StatusWidget(context);

    public IPluginSettingsEditor? CreateSettingsEditor(IWidgetContext context) => new StatusSettingsEditor(context);

    /// <summary>A followed page: its id, and the widget's own filter (null: the catalog's default).</summary>
    internal sealed record Followed(string Id, StatusFilter? Filter);

    internal static List<Followed> Services(IWidgetContext context)
    {
        if (context.Settings[ServicesSetting] is not JsonArray array)
            return [];
        var list = new List<Followed>();
        foreach (var node in array)
        {
            if (node is not JsonObject o || o["id"] is not JsonValue v || !v.TryGetValue<string>(out var id) || id.Length == 0)
                continue;
            var filter = o.ContainsKey("include") || o.ContainsKey("exclude") || o.ContainsKey("regions")
                ? new StatusFilter(Strings(o["include"]), Strings(o["exclude"]), Strings(o["regions"]))
                : null;
            if (list.All(f => !f.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                list.Add(new Followed(id, filter));
        }
        return list;
    }

    internal static void SetServices(IWidgetContext context, IEnumerable<Followed> services) =>
        context.Settings[ServicesSetting] = new JsonArray(services.Select(f =>
        {
            var o = new JsonObject { ["id"] = f.Id };
            if (f.Filter is { } filter)
            {
                o["include"] = new JsonArray(filter.Include.Select(s => (JsonNode)s).ToArray());
                o["exclude"] = new JsonArray(filter.Exclude.Select(s => (JsonNode)s).ToArray());
                o["regions"] = new JsonArray(filter.Regions.Select(s => (JsonNode)s).ToArray());
            }
            return (JsonNode)o;
        }).ToArray());

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];
}

internal sealed class StatusWidget : IPluginWidget
{
    private enum Tier { Tiny, Small, Medium, Large }

    private static readonly TimeSpan Reevaluate = TimeSpan.FromSeconds(30); // States go stale with the clock, not only on reads.

    private readonly (CatalogEntry Entry, StatusFilter Filter)[] _services;
    private readonly int _missing;
    private readonly IDisposable? _subscription;
    private readonly Grid _root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Dictionary<string, Severity> _seen = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ServiceState> _states = [];
    private int _version = -1;
    private DateTimeOffset _evaluatedAt;
    private Tier? _tier;
    private ElementTheme _theme;
    private bool _dirty = true;
    private int _openFlyouts;

    public StatusWidget(IWidgetContext context)
    {
        var catalog = Catalog.Load(context.DataDirectory).ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var followed = StatusWidgetPlugin.Services(context);
        _services = followed.Where(f => catalog.ContainsKey(f.Id))
            .Select(f => (catalog[f.Id], f.Filter ?? catalog[f.Id].DefaultFilter)).ToArray();
        _missing = followed.Count - _services.Length;
        if (_services.Length > 0)
            _subscription = StatusPoller.Subscribe(context.DataDirectory, _services.Select(s => s.Entry).ToArray());
        _root.SizeChanged += (_, _) => _dirty = true; // Another tier, or another fit within it.
        _root.PointerExited += (_, _) => Acknowledge();
        View = _root;
    }

    public UIElement View { get; }

    public void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        if (StatusPoller.Version != _version || now - _evaluatedAt > Reevaluate)
        {
            _version = StatusPoller.Version;
            _evaluatedAt = now;
            var states = _services.Select(s =>
            {
                var snapshot = StatusPoller.Get(s.Entry.Id);
                return Evaluation.Evaluate(s.Entry, s.Filter, snapshot?.Reading, snapshot?.ReadAt, snapshot?.Error, now,
                    snapshot?.StaleAfter ?? StatusPoller.StaleAfter(s.Entry.Kind));
            }).OrderByDescending(s => Evaluation.Rank(s.Severity)).ThenBy(s => s.Entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var state in states)
                _seen.TryAdd(state.Entry.Id, state.Severity); // What the widget showed first is what you saw.
            if (!SameStates(states, _states))
                _dirty = true;
            _states = states;
        }
        if (_root.ActualTheme != _theme)
        {
            _theme = _root.ActualTheme;
            _dirty = true;
        }
        if (_dirty && _openFlyouts == 0) // Redrawing would take the open flyout's anchor away.
            Render();
    }

    public void Dispose() => _subscription?.Dispose();

    private bool Changed(ServiceState state) => _seen.TryGetValue(state.Entry.Id, out var seen) && seen != state.Severity;

    private void Acknowledge()
    {
        if (_openFlyouts > 0)
            return; // The pointer left for the flyout: its closing acknowledges.
        var any = false;
        foreach (var state in _states)
        {
            any |= Changed(state);
            _seen[state.Entry.Id] = state.Severity;
        }
        if (any)
            Render();
    }

    private Tier TierOf()
    {
        double w = _root.ActualWidth, h = _root.ActualHeight;
        return w < 130 || h < 48 ? Tier.Tiny
            : h < 100 ? Tier.Small
            : w < 330 || h < 250 ? Tier.Medium
            : Tier.Large;
    }

    private void Render()
    {
        _dirty = false;
        _tier = TierOf();
        _root.Children.Clear();
        if (_services.Length == 0)
        {
            _root.Children.Add(Padded(StatusViews.Note(
                _missing > 0 ? "The services this widget followed are no longer in the catalog. Pick others in the settings." : "No service followed yet: pick the status pages to follow in the widget's settings.",
                0.7)));
            return;
        }
        var issues = _states.Count(s => s.IsIssue);
        var worst = _states.Count > 0 ? _states[0].Severity : Severity.Unknown;
        switch (_tier)
        {
            case Tier.Tiny:
                _root.Children.Add(Clickable(Glyph(worst, issues), _states));
                break;
            case Tier.Small:
                _root.Children.Add(Clickable(SummaryLine(worst, issues), _states));
                break;
            case Tier.Medium:
                Fit(new Thickness(8, 2, 10, 4),
                    () => Rows(details: true, collapse: false),
                    () => Rows(details: false, collapse: false),
                    () => Rows(details: true, collapse: true),
                    () => Rows(details: false, collapse: true));
                break;
            default:
                Fit(new Thickness(10, 2, 12, 6),
                    () => StatusViews.Details(_states, Changed, collapseOperational: false, _theme),
                    () => StatusViews.Details(_states, Changed, collapseOperational: true, _theme));
                break;
        }
    }

    /// <summary>
    /// The first of <paramref name="layouts"/> (most detailed first) whose content fits the widget's height; the last
    /// one, scrolling, when none does. Laid out at once to know: only on a change, a few times at most.
    /// </summary>
    private void Fit(Thickness padding, params Func<UIElement>[] layouts)
    {
        for (var i = 0; i < layouts.Length; i++)
        {
            var scroll = new ScrollViewer { Padding = padding, Content = layouts[i]() };
            _root.Children.Clear();
            _root.Children.Add(scroll);
            if (i == layouts.Length - 1)
                return;
            _root.UpdateLayout();
            if (scroll.ExtentHeight <= scroll.ViewportHeight + 0.5)
                return;
        }
    }

    private UIElement Glyph(Severity worst, int issues)
    {
        var icon = new FontIcon { Glyph = StatusViews.Glyph(worst), FontSize = 22, Foreground = StatusViews.Brush(worst, _theme) };
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var top = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Children = { icon } };
        if (_states.Any(Changed))
        {
            var marker = StatusViews.ChangeMarker();
            marker.HorizontalAlignment = HorizontalAlignment.Right;
            marker.VerticalAlignment = VerticalAlignment.Top;
            marker.Margin = new Thickness(0, -2, -6, 0);
            top.Children.Add(marker);
        }
        panel.Children.Add(top);
        if (issues > 0)
            panel.Children.Add(new TextBlock { Text = issues.ToString(CultureInfo.CurrentCulture), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
        ToolTipService.SetToolTip(panel, Tooltip());
        return panel;
    }

    private UIElement SummaryLine(Severity worst, int issues)
    {
        var first = _states.FirstOrDefault(s => s.IsIssue);
        var unknown = _states.Count(s => s.Severity == Severity.Unknown);
        var maintenance = _states.Count(s => s.Severity == Severity.Maintenance);
        var text = first is not null
            ? $"{first.Entry.Name}: {Evaluation.Describe(first.Severity).ToLowerInvariant()}{(issues > 1 ? $" (+{issues - 1})" : "")}"
            : unknown == _states.Count ? "Status unknown"
            : "All operational";
        var extra = new List<string>();
        if (first is null && unknown > 0 && unknown < _states.Count)
            extra.Add($"{unknown} unknown");
        if (maintenance > 0)
            extra.Add($"{maintenance} in maintenance");
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        line.Children.Add(new FontIcon { Glyph = StatusViews.Glyph(worst), FontSize = 16, Foreground = StatusViews.Brush(worst, _theme) });
        line.Children.Add(new TextBlock
        {
            Text = extra.Count > 0 ? $"{text} · {string.Join(" · ", extra)}" : text,
            FontSize = 13,
            FontWeight = first is null ? FontWeights.Normal : FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (_states.Any(Changed))
            line.Children.Add(StatusViews.ChangeMarker());
        ToolTipService.SetToolTip(line, Tooltip());
        return line;
    }

    /// <summary>One row per service; <paramref name="details"/>: a line under each that isn't operational (its first
    /// incident, affected components or maintenance); <paramref name="collapse"/>: the operational ones as one line.</summary>
    private StackPanel Rows(bool details, bool collapse)
    {
        var rows = new StackPanel { Spacing = 2 };
        var fine = _states.Where(s => s.Severity == Severity.Operational).ToList();
        collapse &= fine.Count > 1;
        foreach (var state in collapse ? _states.Where(s => s.Severity != Severity.Operational) : _states)
        {
            UIElement row = StatusViews.Row(state, Changed(state), _theme);
            if (details && StatusViews.Brief(state) is { } brief)
            {
                row = new StackPanel
                {
                    Children =
                    {
                        row,
                        new TextBlock
                        {
                            Text = brief, FontSize = 11, Opacity = 0.7, Margin = new Thickness(16, 0, 0, 0),
                            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                        },
                    },
                };
            }
            rows.Children.Add(Clickable(row, [state], stretch: false));
        }
        if (collapse)
        {
            var line = StatusViews.Note($"{fine.Count} operational", 0.65);
            ToolTipService.SetToolTip(line, string.Join(", ", fine.Select(s => s.Entry.Name)));
            line.Margin = new Thickness(6, 2, 0, 0);
            rows.Children.Add(line);
        }
        return rows;
    }

    /// <summary>Content as a flat button whose flyout details <paramref name="states"/>; closing it acknowledges changes.</summary>
    private Button Clickable(UIElement content, IReadOnlyList<ServiceState> states, bool stretch = true)
    {
        // Default placement (top, else another side): Auto fails fast in XAML when the flyout opens (E_INVALIDARG, or a
        // metadata lookup of a null type), on every click.
        var flyout = new Flyout();
        flyout.Opening += (_, _) => flyout.Content = new ScrollViewer
        {
            MaxHeight = 480,
            Width = 380,
            Padding = new Thickness(0, 0, 12, 0),
            Content = StatusViews.Details(states, Changed, collapseOperational: true, _theme),
        };
        flyout.Opened += (_, _) => _openFlyouts++;
        flyout.Closed += (_, _) =>
        {
            _openFlyouts--;
            Acknowledge();
            if (_dirty)
                Render();
        };
        return new Button
        {
            Content = content,
            Flyout = flyout,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(stretch ? 0 : 4, 3, 4, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = stretch ? VerticalAlignment.Stretch : VerticalAlignment.Top,
            HorizontalContentAlignment = stretch ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }

    private string Tooltip()
    {
        var troubled = _states.Where(s => s.Severity != Severity.Operational).ToList();
        if (troubled.Count == 0)
            return $"All {_states.Count} services operational";
        var lines = troubled.Take(8).Select(s => $"{s.Entry.Name}: {Evaluation.Describe(s.Severity)}").ToList();
        if (troubled.Count > 8)
            lines.Add($"+{troubled.Count - 8} more");
        return string.Join("\n", lines);
    }

    private static UIElement Padded(UIElement content) => new Border { Padding = new Thickness(10, 4, 10, 4), Child = content };

    /// <summary>Same services, same severities, same explanations: nothing to redraw.</summary>
    private static bool SameStates(IReadOnlyList<ServiceState> a, IReadOnlyList<ServiceState> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Entry.Id != b[i].Entry.Id || a[i].Severity != b[i].Severity || a[i].ReadAt is null != b[i].ReadAt is null
                || !a[i].Affected.SequenceEqual(b[i].Affected) || !a[i].Incidents.SequenceEqual(b[i].Incidents, IncidentComparer.Instance)
                || !a[i].Maintenances.SequenceEqual(b[i].Maintenances, MaintenanceComparer.Instance) || a[i].Error != b[i].Error)
                return false;
        }
        return true;
    }

    /// <summary>Records holding lists compare those by reference: compare what is shown instead.</summary>
    private sealed class IncidentComparer : IEqualityComparer<IncidentReading>
    {
        public static readonly IncidentComparer Instance = new();

        public bool Equals(IncidentReading? x, IncidentReading? y) =>
            x is not null && y is not null && x.Name == y.Name && x.Status == y.Status && x.Severity == y.Severity
            && x.LatestUpdate == y.LatestUpdate && x.UpdatedAt == y.UpdatedAt && x.Url == y.Url;

        public int GetHashCode(IncidentReading obj) => obj.Name.GetHashCode();
    }

    private sealed class MaintenanceComparer : IEqualityComparer<MaintenanceReading>
    {
        public static readonly MaintenanceComparer Instance = new();

        public bool Equals(MaintenanceReading? x, MaintenanceReading? y) =>
            x is not null && y is not null && x.Name == y.Name && x.InProgress == y.InProgress && x.Start == y.Start && x.End == y.End;

        public int GetHashCode(MaintenanceReading obj) => obj.Name.GetHashCode();
    }
}
