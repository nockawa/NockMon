using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NockMon.Sdk;

namespace ClaudeUsage.NockMon;

/// <summary>The Weekly panel of claude-usage: used vs. elapsed, pace status, reset countdown, burn pace vs. target.</summary>
public sealed class WeeklyWidgetPlugin : IWidgetPlugin
{
    internal const string RefreshSetting = "refreshMinutes";
    internal const string NewsSetting = "newsCategories";
    internal const int DefaultRefreshMinutes = 10;

    public string Id => "weekly";
    public string DisplayName => "Claude weekly usage";
    public string Summary => "Your Claude subscription's 7-day window: used vs. elapsed, whether you are ahead or behind, and the pace to stay within it.";

    public string Details => """
        The **Weekly** panel of claude-usage, as a widget.

        - **Used**: share of the weekly limit consumed. **Elapsed**: share of the 7-day window gone by.
        - **Trend** (after Used): your burn over the last 3 hours, in % of the limit per hour, and an arrow comparing it with the even burn that reaches the limit exactly at the reset: **→** within ±15 %, **↑** above, **↑↑** above 1.5×, **↓** below, **↓↓** below half (or no burn). Hover it for both rates.
        - **Ahead / On pace / Behind**: used compared with elapsed (±5 points). Ahead means you will hit the limit before the reset at this rate.
        - **Resets in**: time until the window restarts.
        - **Pace**: your burn over the last 24 h, in % of the limit per day ("avg" until a day of history exists: the average since the window opened). **Target**: what you can burn per day until the reset without hitting the limit. Red when pace is above target.

        **Source:** the OAuth token the Claude CLI stored (`CLAUDE_CODE_OAUTH_TOKEN`, else `~/.claude/.credentials.json`) and Anthropic's **unofficial** usage endpoint, the one the CLI uses: it can change without notice. Polled every few minutes (setting); while claude-usage runs, its fresh cache is used instead. All widgets share one poller.

        **News:** a bell at the bottom right counts the unread Claude and Anthropic news in the categories ticked in the settings (by default: models, pricing and plans; Claude Code's weekly highlights). Click it for the list: an item you open is read, **Mark all as read** dismisses them. Sources: the Claude Platform release notes, sorted into categories by the documentation they link to; Claude Code's What's new and changelog. Checked every 30 minutes; what was published before the widget first checked a source counts as read.
        """;

    public WidgetSize DefaultSize => new(4, 4);
    public WidgetSize MinSize => new(3, 3);

    public IPluginWidget CreateWidget(IWidgetContext context) => new WeeklyWidget(context);

    public IPluginSettingsEditor? CreateSettingsEditor(IWidgetContext context) => new SettingsEditor(context);

    internal static int RefreshMinutes(IWidgetContext context) =>
        context.Settings[RefreshSetting] is { } node && node.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? Math.Clamp(node.GetValue<int>(), 2, 120)
            : DefaultRefreshMinutes;

    /// <summary>The ticked news categories; the defaults until the settings were saved once.</summary>
    internal static IReadOnlySet<NewsCategory> NewsCategories(IWidgetContext context) =>
        context.Settings[NewsSetting] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var name) && Enum.TryParse<NewsCategory>(name, out var c) ? c : (NewsCategory?)null)
                .OfType<NewsCategory>().ToHashSet()
            : NewsFeeds.Categories.Where(c => c.Default).Select(c => c.Category).ToHashSet();

    private sealed class SettingsEditor : IPluginSettingsEditor
    {
        private readonly IWidgetContext _context;
        private readonly NumberBox _minutes;
        private readonly List<(NewsCategory Category, CheckBox Box)> _news = [];

        public SettingsEditor(IWidgetContext context)
        {
            _context = context;
            _minutes = new NumberBox
            {
                Header = "Refresh every (minutes)",
                Value = RefreshMinutes(context),
                Minimum = 2,
                Maximum = 120,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 160,
            };
            var panel = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    _minutes,
                    Hint("The usage endpoint is rate limited: a few minutes is plenty (the window spans a week). Widgets share one poller at the shortest interval."),
                    new TextBlock { Text = "Claude news", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) },
                    Hint("A bell at the bottom right counts the unread news of the ticked categories. None ticked: no bell."),
                },
            };
            var ticked = NewsCategories(context);
            foreach (var (category, label, hint, _) in NewsFeeds.Categories)
            {
                var box = new CheckBox { Content = label, IsChecked = ticked.Contains(category), MinHeight = 28, Padding = new Thickness(6, 4, 0, 0) };
                ToolTipService.SetToolTip(box, hint);
                _news.Add((category, box));
                panel.Children.Add(box);
            }
            View = panel;
        }

        public UIElement View { get; }

        public string? Apply()
        {
            if (double.IsNaN(_minutes.Value))
                return "Enter a refresh interval in minutes.";
            _context.Settings[RefreshSetting] = (int)Math.Clamp(_minutes.Value, 2, 120);
            _context.Settings[NewsSetting] = new JsonArray(_news.Where(n => n.Box.IsChecked == true).Select(n => (JsonNode)n.Category.ToString()).ToArray());
            return null;
        }

        private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 };
    }
}

internal sealed class WeeklyWidget : IPluginWidget
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);
    /// <summary>Narrower, a bar tells nothing: the trend then moves under the Used row.</summary>
    private const double MinBarWidth = 60;

    private readonly IDisposable _subscription;
    private readonly IDisposable? _newsSubscription;
    private readonly NewsBell _bell;
    private readonly Grid _bars = new() { ColumnSpacing = 8, RowSpacing = 4 };
    private readonly TextBlock _usedLabel = Label("Used");
    private readonly TextBlock _elapsedLabel = Label("Elapsed");
    private readonly Bar _used = new();
    private readonly Bar _elapsed = new();
    private readonly TextBlock _usedText = Value();
    private readonly TextBlock _trend = Value();
    private readonly TextBlock _elapsedText = Value();
    private readonly TextBlock _status = new() { FontWeight = FontWeights.SemiBold, FontSize = 13 };
    private readonly TextBlock _statusBy = Value();
    private readonly TextBlock _resets = Value();
    private readonly TextBlock _pace = Value();
    private readonly TextBlock _paceBasis = new() { FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _target = Value();
    private readonly TextBlock _footer = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.65 };
    private readonly UIElement[] _paceRows;
    private readonly StackPanel _data;
    private readonly TextBlock _empty = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Text = "Reading your Claude usage…" };

    public WeeklyWidget(IWidgetContext context)
    {
        _subscription = UsageService.Subscribe(context.DataDirectory, TimeSpan.FromMinutes(WeeklyWidgetPlugin.RefreshMinutes(context)));
        var news = WeeklyWidgetPlugin.NewsCategories(context);
        _newsSubscription = news.Count > 0 ? NewsService.Subscribe(context.DataDirectory, news) : null;
        _bell = new NewsBell(news);

        _bars.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bars.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bars.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddRow(_bars, 0, _usedLabel, _used, _usedText, _trend);
        AddRow(_bars, 1, _elapsedLabel, _elapsed, _elapsedText);
        _trend.Visibility = Visibility.Collapsed;
        // The grid's width comes from the widget, not from where the trend sits: no feedback loop.
        _bars.SizeChanged += (_, _) => PlaceTrend();
        _trend.SizeChanged += (_, _) => PlaceTrend();
        _usedText.SizeChanged += (_, _) => PlaceTrend();

        var status = new Grid { ColumnSpacing = 8 };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_statusBy, 1);
        _statusBy.HorizontalAlignment = HorizontalAlignment.Left;
        _statusBy.VerticalAlignment = VerticalAlignment.Center;
        status.Children.Add(_status);
        status.Children.Add(_statusBy);

        var details = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(details, 0, Label("Resets in"), _resets);
        var pace = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _pace, _paceBasis } };
        var paceLabel = Label("Pace");
        var targetLabel = Label("Target");
        AddRow(details, 1, paceLabel, pace);
        AddRow(details, 2, targetLabel, _target);
        _paceRows = [paceLabel, pace, targetLabel, _target];
        foreach (var v in new[] { _resets, _target })
            v.HorizontalAlignment = HorizontalAlignment.Left;

        _data = new StackPanel { Spacing = 6, Children = { _bars, status, details } };
        var content = new ScrollViewer
        {
            Padding = new Thickness(10, 0, 10, 2),
            Content = new StackPanel { Spacing = 6, Children = { _empty, _data, _footer } },
        };
        View = new Grid { Children = { content, _bell } };
        _data.Visibility = Visibility.Collapsed;
    }

    public UIElement View { get; }

    public void Refresh()
    {
        _bell.Refresh();
        // The footer's text stops before the bell (bottom right) while it shows.
        var footerRight = _bell.Visibility == Visibility.Visible ? 40 : 0;
        if (_footer.Margin.Right != footerRight)
            _footer.Margin = new Thickness(0, 0, footerRight, 0);

        var state = UsageService.State;
        var now = DateTimeOffset.UtcNow;
        UpdateFooter(state, now);
        if (state.Weekly is not { } w)
        {
            if (state.Error is { } error)
                Set(_empty, error);
            return;
        }
        _empty.Visibility = Visibility.Collapsed;
        _data.Visibility = Visibility.Visible;

        // Elapsed and the countdown move with the clock, between readings.
        var left = w.ResetsAt - now;
        var elapsedPct = Math.Clamp((Window - left).TotalSeconds / Window.TotalSeconds * 100, 0, 100);
        var delta = w.UtilPct - elapsedPct;
        var palette = Palette.For(View);
        var (word, brush) = delta > 5 ? ("AHEAD", palette.Ahead)
            : delta < -5 ? ("BEHIND", palette.Behind)
            : ("ON PACE", palette.OnPace);

        _used.Set(w.UtilPct, brush);
        _elapsed.Set(elapsedPct, palette.Elapsed);
        Set(_usedText, Percent(w.UtilPct));
        UpdateTrend(state.Trend, palette);
        Set(_elapsedText, Percent(elapsedPct));
        Set(_status, "● " + word);
        _status.Foreground = brush;
        Set(_statusBy, $"by {Percent(Math.Abs(delta))}");
        Set(_resets, Duration(left));

        var showPace = w.UtilPct < 100;
        foreach (var row in _paceRows)
            row.Visibility = showPace ? Visibility.Visible : Visibility.Collapsed;
        if (!showPace)
            return;
        var daysLeft = Math.Max(left.TotalDays, 0);
        var target = daysLeft > 0 ? (100 - w.UtilPct) / daysLeft : 0;
        var elapsedDays = (Window - left).TotalDays;
        var (current, basis) = state.Pace is { } p
            ? (p.PctPerDay, p.HoursObserved >= 23.5 ? "24h" : $"{p.HoursObserved:F0}h")
            : (elapsedDays > 0 ? w.UtilPct / elapsedDays : 0, "avg");
        Set(_pace, $"{current.ToString("F1", CultureInfo.CurrentCulture)} %/d");
        _pace.Foreground = current > target ? palette.Ahead : palette.Behind;
        Set(_paceBasis, $"({basis})");
        Set(_target, $"{target.ToString("F1", CultureInfo.CurrentCulture)} %/d");
    }

    private void UpdateTrend(Trend? trend, Palette palette)
    {
        _trend.Visibility = trend is null ? Visibility.Collapsed : Visibility.Visible;
        if (trend is null)
            return;
        var (arrow, brush) = trend.Bucket switch
        {
            >= 2 => ("↑↑", palette.Ahead),
            1 => ("↑", palette.Ahead),
            0 => ("→", palette.OnPace),
            -1 => ("↓", palette.Behind),
            _ => ("↓↓", palette.Behind),
        };
        Set(_trend, $"{arrow} {trend.PctPerHour.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture)} %/h");
        _trend.Foreground = brush;
        _trend.FontWeight = Math.Abs(trend.Bucket) >= 2 ? FontWeights.SemiBold : FontWeights.Normal;
        var tip = $"Burn over the last 3 h: {Rate(trend.PctPerHour)}. Even burn until the reset: {Rate(trend.TargetPctPerHour)}.";
        if (ToolTipService.GetToolTip(_trend) as string != tip)
            ToolTipService.SetToolTip(_trend, tip);

        static string Rate(double value) => value.ToString("F2", CultureInfo.CurrentCulture) + " %/h";
    }

    /// <summary>The trend after the Used value while the bar keeps <see cref="MinBarWidth"/>, else on its own line under it.</summary>
    private void PlaceTrend()
    {
        if (_bars.ActualWidth <= 0)
            return;
        var labels = Math.Max(_usedLabel.ActualWidth, _elapsedLabel.ActualWidth);
        var values = Math.Max(_usedText.ActualWidth, _elapsedText.ActualWidth);
        var bar = _bars.ActualWidth - labels - values - _trend.ActualWidth - 3 * _bars.ColumnSpacing;
        var below = _trend.Visibility == Visibility.Visible && bar < MinBarWidth;
        if (below == (Grid.GetRow(_trend) == 1))
            return;

        if (below)
            _bars.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });
        else
            _bars.RowDefinitions.RemoveAt(1);
        foreach (var cell in new FrameworkElement[] { _elapsedLabel, _elapsed, _elapsedText })
            Grid.SetRow(cell, below ? 2 : 1);
        Grid.SetRow(_trend, below ? 1 : 0);
        Grid.SetColumn(_trend, below ? 1 : 3);
        Grid.SetColumnSpan(_trend, below ? 2 : 1);
    }

    private void UpdateFooter(UsageState state, DateTimeOffset now)
    {
        var updated = state.FetchedAt is { } at ? $"Updated {Ago(now - at)}" : null;
        var text = state.Error is { } error && state.Weekly is not null
            ? $"{error} {(updated is null ? "" : $"Showing the reading {Ago(now - state.FetchedAt!.Value)}.")}"
            : updated ?? "";
        Set(_footer, text);
        _footer.Foreground = state.Error is not null && state.Weekly is not null ? Palette.For(View).OnPace : Brush("TextFillColorSecondaryBrush");
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _newsSubscription?.Dispose();
    }

    private static void AddRow(Grid grid, int row, params FrameworkElement[] cells)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < cells.Length; c++)
        {
            Grid.SetRow(cells[c], row);
            Grid.SetColumn(cells[c], c);
            cells[c].VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(cells[c]);
        }
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Opacity = 0.75 };

    private static TextBlock Value() => new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };

    private static void Set(TextBlock block, string text)
    {
        if (block.Text != text)
            block.Text = text;
    }

    private static string Percent(double value) => value.ToString("F1", CultureInfo.CurrentCulture) + " %";

    private static string Duration(TimeSpan ts) =>
        ts.TotalSeconds <= 0 ? "now"
        : ts.TotalDays >= 1 ? $"{(int)ts.TotalDays}d {ts.Hours}h"
        : ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
        : ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m"
        : $"{(int)ts.TotalSeconds}s";

    private static string Ago(TimeSpan ts) =>
        ts.TotalMinutes < 1 ? "just now" : ts.TotalHours < 1 ? $"{(int)ts.TotalMinutes} min ago" : $"{(int)ts.TotalHours} h {ts.Minutes} min ago";

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>
    /// Soft status colors, per theme: muted rose / sage / sand instead of the system's saturated signal colors, darker
    /// on light backgrounds and lighter on dark ones so text stays readable.
    /// </summary>
    private sealed record Palette(Brush Ahead, Brush Behind, Brush OnPace, Brush Elapsed)
    {
        private static readonly Palette Light = new(Solid(0xC9, 0x6B, 0x6B), Solid(0x5E, 0x9E, 0x78), Solid(0xC0, 0x92, 0x3F), Solid(0xA9, 0xAE, 0xB8));
        private static readonly Palette Dark = new(Solid(0xE8, 0x9A, 0x9A), Solid(0x8F, 0xCF, 0xA8), Solid(0xE6, 0xC2, 0x7E), Solid(0x7C, 0x82, 0x8E));

        public static Palette For(UIElement element) =>
            element is FrameworkElement { ActualTheme: ElementTheme.Dark } ? Dark : Light;

        private static SolidColorBrush Solid(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(0xFF, r, g, b));
    }

    /// <summary>Rounded progress bar: a track and a fill sized by star columns.</summary>
    private sealed partial class Bar : Grid
    {
        private readonly ColumnDefinition _filled = new() { Width = new GridLength(0, GridUnitType.Star) };
        private readonly ColumnDefinition _rest = new() { Width = new GridLength(1, GridUnitType.Star) };
        private readonly Border _fill = new() { CornerRadius = new CornerRadius(4) };

        public Bar()
        {
            Height = 8;
            MinWidth = 40;
            ColumnDefinitions.Add(_filled);
            ColumnDefinitions.Add(_rest);
            var track = new Border { CornerRadius = new CornerRadius(4), Opacity = 0.35, Background = (Brush)Application.Current.Resources["ControlStrongFillColorDisabledBrush"] };
            SetColumnSpan(track, 2);
            Children.Add(track);
            Children.Add(_fill);
        }

        public void Set(double percent, Brush brush)
        {
            var p = Math.Clamp(percent, 0, 100);
            if (_filled.Width.Value != p)
            {
                _filled.Width = new GridLength(p, GridUnitType.Star);
                _rest.Width = new GridLength(100 - p, GridUnitType.Star);
            }
            _fill.Background = brush;
        }
    }
}
