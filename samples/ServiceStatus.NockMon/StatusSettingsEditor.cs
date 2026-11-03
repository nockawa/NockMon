using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Sdk;
using ServiceStatus.NockMon.Status;

namespace ServiceStatus.NockMon;

/// <summary>
/// The services to follow (a searchable list of the catalog), each one's filter, which are recorded in NockMon's history
/// (plugin-wide), and pages added by URL.
/// </summary>
internal sealed class StatusSettingsEditor : IPluginSettingsEditor
{
    private readonly IWidgetContext _context;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBox _search = new() { PlaceholderText = "Search the services", MinWidth = 240 };
    private readonly TextBox _url = new() { PlaceholderText = "status.example.com", MinWidth = 240 };
    private readonly Button _add = new() { Content = "Add", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _addResult = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly List<Item> _items = [];
    private readonly Dictionary<string, Watched> _watched;

    public StatusSettingsEditor(IWidgetContext context)
    {
        _context = context;
        var followed = StatusWidgetPlugin.Services(context).ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        _watched = Watchlist.Load(context.DataDirectory).ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Catalog.Load(context.DataDirectory))
            AddItem(entry, followed.TryGetValue(entry.Id, out var f), f?.Filter);
        _search.TextChanged += (_, _) => Filter();
        _add.Click += async (_, _) => await AddByUrlAsync();

        View = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Heading("Services", 0),
                Hint("Tick the services to follow. Filter: the components and regions that count (some pages list hundreds that are routinely degraded). History: NockMon records the service's state (0 operational, 1 maintenance, 2 degraded, 3 partial, 4 major outage) for charts and AI agents. History is for every widget, and keeps the service read when no widget shows it."),
                _search,
                new ScrollViewer { Content = _list, MaxHeight = 340, Padding = new Thickness(0, 0, 12, 0) },
                Heading("Add a status page", 10),
                Hint("Any Atlassian Statuspage or incident.io page. It joins the list of every widget of this plugin."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _url, _add } },
                _addResult,
            },
        };
    }

    public UIElement View { get; }

    public string? Apply()
    {
        var followed = _items.Where(i => i.Box.IsChecked == true).Select(i => new StatusWidgetPlugin.Followed(i.Entry.Id, i.Filter)).ToList();
        StatusWidgetPlugin.SetServices(_context, followed);
        // Watched under this widget's filter when this widget follows the service, else under the filter it had.
        var watched = _items.Where(i => i.History.IsChecked == true).Select(i => new Watched(
            i.Entry.Id,
            i.Box.IsChecked == true || !_watched.TryGetValue(i.Entry.Id, out var w) ? i.Filter ?? i.Entry.DefaultFilter : w.Filter,
            true)).ToList();
        try
        {
            Background.Configure(_context.DataDirectory, watched);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Couldn't save which services are watched: " + e.Message;
        }
        return null;
    }

    private void AddItem(CatalogEntry entry, bool isChecked, StatusFilter? filter, int index = -1)
    {
        _watched.TryGetValue(entry.Id, out var watched);
        var item = new Item(entry, filter, new CheckBox { Content = entry.Name, IsChecked = isChecked, MinWidth = 0 }, watched, this);
        _items.Insert(index < 0 ? _items.Count : index, item);
        _list.Children.Insert(index < 0 ? _list.Children.Count : index, item.View);
    }

    private void Filter()
    {
        var text = _search.Text.Trim();
        foreach (var item in _items)
            item.View.Visibility = text.Length == 0 || item.Entry.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async Task AddByUrlAsync()
    {
        _add.IsEnabled = false;
        _addResult.Text = "Looking…";
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var (entry, message) = await Detector.DetectAsync(_url.Text,
                async (uri, ct) => (await StatusHttp.GetAsync(uri.AbsoluteUri, null, null, ct)).Body, cancel.Token);
            _addResult.Text = message;
            if (entry is null)
                return;
            var known = _items.FirstOrDefault(i => i.Entry.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)
                                                   || i.Entry.Url.TrimEnd('/').Equals(entry.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                known.Box.IsChecked = true;
                _addResult.Text = $"{known.Entry.Name} is already in the list: ticked.";
                return;
            }
            Catalog.AddUser(_context.DataDirectory, entry);
            var index = _items.FindIndex(i => string.Compare(i.Entry.Name, entry.Name, StringComparison.CurrentCultureIgnoreCase) > 0);
            AddItem(entry, true, null, index);
            _search.Text = "";
            _url.Text = "";
            _addResult.Text = $"{message} {entry.Name} added and ticked.";
        }
        catch (OperationCanceledException)
        {
            _addResult.Text = "No answer in time.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _addResult.Text = "Couldn't save it: " + e.Message;
        }
        finally
        {
            _add.IsEnabled = true;
        }
    }

    private static TextBlock Heading(string text, double top) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 0) };

    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 };

    /// <summary>A catalog entry: its box, a Filter link while ticked, and the filter editor it opens.</summary>
    private sealed class Item
    {
        private readonly StatusSettingsEditor _owner;
        private readonly HyperlinkButton _toggle = new() { Content = "Filter…", Padding = new Thickness(4, 0, 4, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _editor = new() { Spacing = 4, Margin = new Thickness(28, 0, 0, 6), Visibility = Visibility.Collapsed };
        private readonly TextBlock _summary = new() { FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private bool _built;

        public Item(CatalogEntry entry, StatusFilter? filter, CheckBox box, Watched? watched, StatusSettingsEditor owner)
        {
            Entry = entry;
            Filter = filter;
            Box = box;
            _owner = owner;
            History = new CheckBox { Content = "History", IsChecked = watched?.History == true, MinWidth = 0, FontSize = 12, Padding = new Thickness(6, 5, 0, 0) };
            ToolTipService.SetToolTip(History, $"Record {entry.Name}'s state in NockMon's history (charts, value widgets, AI agents), for every widget. It is then read even when no widget shows it.");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { box, _toggle, History, _summary } };
            if (entry.Custom)
                ToolTipService.SetToolTip(box, $"Added by URL: {entry.Url}");
            View = new StackPanel { Children = { row, _editor } };
            box.Checked += (_, _) => Update();
            box.Unchecked += (_, _) => Update();
            _toggle.Click += async (_, _) =>
            {
                _editor.Visibility = _editor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                if (_editor.Visibility == Visibility.Visible && !_built)
                {
                    _built = true;
                    await BuildAsync();
                }
            };
            Update();
        }

        public CatalogEntry Entry { get; }
        public CheckBox Box { get; }
        public CheckBox History { get; }
        public StackPanel View { get; }

        /// <summary>The widget's own filter; null: the catalog's default.</summary>
        public StatusFilter? Filter { get; private set; }

        private StatusFilter Effective => Filter ?? Entry.DefaultFilter;

        private void Update()
        {
            var on = Box.IsChecked == true;
            _toggle.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _summary.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on)
                _editor.Visibility = Visibility.Collapsed;
            var f = Effective;
            var parts = new List<string>();
            if (f.Include.Count > 0)
                parts.Add(f.Include.Count == 1 ? $"only {f.Include[0]}" : $"{f.Include.Count} selected");
            if (f.Exclude.Count > 0)
                parts.Add($"except {string.Join(", ", f.Exclude)}");
            if (f.Regions.Count > 0)
                parts.Add($"regions {string.Join(", ", f.Regions)}");
            _summary.Text = parts.Count == 0 ? "everything" : string.Join(" · ", parts);
        }

        private async Task BuildAsync()
        {
            var lists = Entry.Kind is ProviderKind.Statuspage or ProviderKind.IncidentIo;
            if (lists)
            {
                _editor.Children.Add(Hint("Reading the page's components…"));
                Snapshot? snapshot = null;
                try
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    snapshot = await StatusPoller.ReadOnceAsync(_owner._context.DataDirectory, Entry, cancel.Token);
                }
                catch (OperationCanceledException)
                {
                }
                _editor.Children.Clear();
                if (snapshot?.Reading is { ListsComponents: true, Components.Count: > 0 } reading)
                {
                    BuildChecklist(reading);
                    return;
                }
                _editor.Children.Add(Hint("The page couldn't be read: patterns instead."));
            }
            BuildPatterns();
        }

        /// <summary>The page's groups and ungrouped components: ticking a group selects its members.</summary>
        private void BuildChecklist(ServiceReading reading)
        {
            var tops = new List<string>();
            foreach (var c in reading.Components)
            {
                var top = c.Group ?? c.Name;
                if (!tops.Contains(top, StringComparer.OrdinalIgnoreCase))
                    tops.Add(top);
            }
            var filter = Effective;
            var boxes = new List<CheckBox>();
            var panel = new StackPanel { Spacing = 0 };
            foreach (var top in tops)
            {
                var probe = new ComponentReading(top, top, Severity.Operational);
                var box = new CheckBox { Content = top, IsChecked = filter.Matches(probe), MinHeight = 28, Padding = new Thickness(6, 4, 0, 0), FontSize = 12 };
                box.Checked += (_, _) => Changed();
                box.Unchecked += (_, _) => Changed();
                boxes.Add(box);
                panel.Children.Add(box);
            }
            var reset = new HyperlinkButton { Content = "Default", Padding = new Thickness(0), FontSize = 12 };
            reset.Click += (_, _) =>
            {
                Filter = null;
                foreach (var box in boxes)
                    box.IsChecked = Entry.DefaultFilter.Matches(new ComponentReading((string)box.Content, (string)box.Content, Severity.Operational));
                Filter = null; // The boxes' events set one: the default it is.
                Update();
            };
            _editor.Children.Add(Hint(tops.Count == reading.Components.Count ? "Components that count:" : "Components (and groups) that count:"));
            _editor.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 220, Padding = new Thickness(0, 0, 12, 0) });
            _editor.Children.Add(reset);

            void Changed()
            {
                var ticked = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList();
                Filter = new StatusFilter(ticked.Count == boxes.Count ? [] : ticked, [], Effective.Regions);
                Update();
            }
        }

        /// <summary>Feeds that only list what is affected (Slack, Google, AWS, Azure): patterns on products and regions.</summary>
        private void BuildPatterns()
        {
            var filter = Effective;
            var only = new TextBox { Header = "Only these products or services (patterns, ; separated)", Text = string.Join("; ", filter.Include), PlaceholderText = "All", FontSize = 12 };
            var except = new TextBox { Header = "Except", Text = string.Join("; ", filter.Exclude), PlaceholderText = "None", FontSize = 12 };
            _editor.Children.Add(only);
            _editor.Children.Add(except);
            TextBox? regions = null;
            if (Entry.Regional)
            {
                regions = new TextBox
                {
                    Header = "Regions (patterns, ; separated)",
                    Text = string.Join("; ", filter.Regions),
                    PlaceholderText = Entry.Kind == ProviderKind.Aws ? "us-east-1; eu-*" : "us-central1; europe-*",
                    FontSize = 12,
                };
                _editor.Children.Add(regions);
            }
            _editor.Children.Add(Hint("* and ? wildcards, on names as the page writes them. An incident that names no product or region always counts."));
            void Changed()
            {
                Filter = new StatusFilter(Glob.Split(only.Text), Glob.Split(except.Text), regions is null ? [] : Glob.Split(regions.Text));
                Update();
            }
            only.TextChanged += (_, _) => Changed();
            except.TextChanged += (_, _) => Changed();
            if (regions is not null)
                regions.TextChanged += (_, _) => Changed();
        }
    }
}
