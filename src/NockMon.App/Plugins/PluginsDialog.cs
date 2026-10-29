using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NockMon.Core.Layout;
using NockMon.Core.Plugins;
using NuGet.Versioning;

namespace NockMon.Plugins;

/// <summary>
/// Plugins dialog: browse the configured sources (nuget.org by default) and install; see, update and uninstall installed
/// plugins; manage development folders. New plugins are usable at once; updates and removals apply at restart.
/// </summary>
internal sealed class PluginsDialog
{
    private readonly LayoutSession _session;
    private readonly Action _restart;
    private readonly ContentDialog _dialog;
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly InfoBar _restartBar = new() { Severity = InfoBarSeverity.Informational, IsClosable = false, Title = "Restart required", Message = "Some plugin changes take effect when NockMon restarts." };
    private readonly TextBox _search = new() { PlaceholderText = "Search plugins", MinWidth = 300 };
    private readonly CheckBox _prerelease = new() { Content = "Include prerelease", MinWidth = 0 };
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly StackPanel _browseStatus = new() { Spacing = 4 };
    private readonly SelectorBar _tabs = new();
    private Dictionary<string, PluginListing>? _latest;
    private CancellationTokenSource? _searching;
    private bool _loadedNew;

    private PluginsDialog(XamlRoot root, LayoutSession session, Action restart)
    {
        _session = session;
        _restart = restart;
        var restartButton = new Button { Content = "Restart now" };
        restartButton.Click += (_, _) =>
        {
            _dialog!.Hide();
            _restart();
        };
        _restartBar.ActionButton = restartButton;

        foreach (var (text, glyph) in new[] { ("Browse", "\uE721"), ("Installed", "\uE74C"), ("Development", "\uEC7A") })
            _tabs.Items.Add(new SelectorBarItem { Text = text, Icon = new FontIcon { Glyph = glyph } });
        _tabs.SelectionChanged += (_, _) => ShowTab();

        var layout = new Grid { Width = 820, RowSpacing = 8 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Fits the window: title, tabs, restart bar and buttons take about 330 px.
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Clamp(root.Size.Height - 330, 220, 620)) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_page, 1);
        Grid.SetRow(_restartBar, 2);
        layout.Children.Add(_tabs);
        layout.Children.Add(_page);
        layout.Children.Add(_restartBar);

        _dialog = new ContentDialog { XamlRoot = root, Title = "Plugins", Content = layout, CloseButtonText = "Close" };
        _dialog.Resources["ContentDialogMaxWidth"] = 1000.0;
        _dialog.Resources["ContentDialogMaxHeight"] = 900.0;

        _prerelease.IsChecked = session.Settings.PluginPrerelease;
        _prerelease.Click += (_, _) =>
        {
            _session.Settings.PluginPrerelease = _prerelease.IsChecked == true;
            _session.SaveSettings();
            _latest = null;
            _ = SearchAsync();
        };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                _ = SearchAsync();
        };
    }

    /// <summary>Shows the dialog on <paramref name="tab"/> (0 browse, 1 installed, 2 development).</summary>
    /// <returns>true if plugins were loaded: the board should be rebuilt (placeholders become widgets).</returns>
    public static async Task<bool> ShowAsync(XamlRoot root, LayoutSession session, Action restart, int tab = 0)
    {
        var dialog = new PluginsDialog(root, session, restart);
        dialog._tabs.SelectedItem = dialog._tabs.Items[tab];
        dialog.UpdateRestartBar();
        await dialog._dialog.ShowAsync();
        dialog._searching?.Cancel();
        return dialog._loadedNew;
    }

    private void ShowTab()
    {
        var index = _tabs.Items.IndexOf(_tabs.SelectedItem);
        _page.Content = index switch
        {
            1 => InstalledPage(),
            2 => DevelopmentPage(),
            _ => BrowsePage(),
        };
    }

    private void UpdateRestartBar() => _restartBar.IsOpen = PluginRuntime.RestartRequired;

    // ---- Browse -----------------------------------------------------------------------------------------------

    private UIElement? _browsePage;

    private UIElement BrowsePage()
    {
        if (_browsePage is not null)
            return _browsePage;
        var go = new Button { Content = "Search" };
        go.Click += (_, _) => _ = SearchAsync();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _search, go, _prerelease } };

        var page = new StackPanel { Spacing = 10, Padding = new Thickness(0, 0, 16, 0) };
        page.Children.Add(bar);
        page.Children.Add(SourcesEditor());
        page.Children.Add(_browseStatus);
        page.Children.Add(_results);
        _browsePage = new ScrollViewer { Content = page };
        _ = SearchAsync();
        return _browsePage;
    }

    private Expander SourcesEditor()
    {
        var list = new StackPanel { Spacing = 4 };
        var input = new TextBox { PlaceholderText = "NuGet V3 feed URL, or a folder of .nupkg files", MinWidth = 520 };
        var add = new Button { Content = "Add" };
        var error = new TextBlock { Foreground = Brush("SystemFillColorCriticalBrush"), FontSize = 12, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };

        void Fill()
        {
            list.Children.Clear();
            foreach (var source in _session.Settings.PluginSources.ToList())
            {
                var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 }, Padding = new Thickness(6, 4, 6, 4) };
                ToolTipService.SetToolTip(remove, "Remove source");
                remove.Click += (_, _) =>
                {
                    _session.Settings.PluginSources.Remove(source);
                    _session.SaveSettings();
                    _latest = null;
                    Fill();
                    _ = SearchAsync();
                };
                list.Children.Add(Row(new TextBlock { Text = source, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, remove));
            }
        }

        add.Click += (_, _) =>
        {
            var source = input.Text.Trim();
            var isUrl = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
            string? problem = source.Length == 0 ? "Enter a feed URL or a folder."
                : !isUrl && !Directory.Exists(source) ? "Folder not found."
                : _session.Settings.PluginSources.Contains(source, StringComparer.OrdinalIgnoreCase) ? "Already listed."
                : null;
            error.Text = problem ?? "";
            error.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            if (problem is not null)
                return;
            _session.Settings.PluginSources.Add(isUrl ? source : Path.GetFullPath(source));
            _session.SaveSettings();
            input.Text = "";
            _latest = null;
            Fill();
            _ = SearchAsync();
        };

        Fill();
        var reset = new HyperlinkButton { Content = "Restore nuget.org", Padding = new Thickness(0) };
        reset.Click += (_, _) =>
        {
            if (!_session.Settings.PluginSources.Contains(PluginFeeds.NuGetOrg))
            {
                _session.Settings.PluginSources.Insert(0, PluginFeeds.NuGetOrg);
                _session.SaveSettings();
                Fill();
                _ = SearchAsync();
            }
        };
        return new Expander
        {
            Header = "Sources",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Plugins are NuGet packages of type NockMonPlugin. A folder source lists the .nupkg files it contains: handy to try a package before publishing it.",
                        TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush"),
                    },
                    list,
                    Row(input, add),
                    error,
                    reset,
                },
            },
        };
    }

    private async Task SearchAsync()
    {
        _searching?.Cancel();
        var cancel = _searching = new CancellationTokenSource();
        _results.Children.Clear();
        _browseStatus.Children.Clear();
        _browseStatus.Children.Add(Row(new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Secondary("Searching…")));

        var (listings, errors) = await SearchAllAsync(_search.Text.Trim(), cancel.Token);
        if (cancel.IsCancellationRequested)
            return;
        _browseStatus.Children.Clear();
        foreach (var error in errors)
            _browseStatus.Children.Add(new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = true, Message = error });
        if (listings.Count == 0 && errors.Count == 0)
            _browseStatus.Children.Add(Secondary(string.IsNullOrEmpty(_search.Text.Trim())
                ? "No plugins found in the sources."
                : $"No plugins match '{_search.Text.Trim()}'."));
        foreach (var listing in listings)
            _results.Children.Add(ListingCard(listing));
    }

    /// <summary>All sources in parallel, merged by id (highest version wins).</summary>
    private async Task<(List<PluginListing> Listings, List<string> Errors)> SearchAllAsync(string? query, CancellationToken cancel)
    {
        var prerelease = _session.Settings.PluginPrerelease;
        var feeds = _session.Settings.PluginSources.Select(s => PluginFeeds.Create(s, PluginRuntime.Http)).ToList();
        var tasks = feeds.Select(async f =>
        {
            try
            {
                return (Listings: await f.SearchAsync(query, prerelease, cancel), Error: (string?)null);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                return (Listings: (IReadOnlyList<PluginListing>)[], Error: $"{f.Name}: {e.Message}");
            }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        var merged = results.SelectMany(r => r.Listings)
            .GroupBy(l => l.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(l => NuGetVersion.TryParse(l.Version, out var v) ? v : new NuGetVersion(0, 0, 0))!)
            .OrderByDescending(l => l.Downloads ?? 0)
            .ThenBy(l => l.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (string.IsNullOrEmpty(query))
            _latest = merged.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
        return (merged, results.Select(r => r.Error).OfType<string>().ToList());
    }

    private UIElement ListingCard(PluginListing listing)
    {
        var details = new List<string> { listing.Id };
        if (listing.Authors.Length > 0)
            details.Add($"by {listing.Authors}");
        if (listing.Downloads is { } downloads)
            details.Add($"{downloads:#,0} downloads");
        details.Add(listing.Verified ? "✓ verified owner" : listing.Feed is NuGetFeed ? "owner not verified" : listing.Feed.Name);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Title(listing.Title, listing.Version));
        text.Children.Add(Secondary(string.Join("  ·  ", details)));
        if (listing.Description.Length > 0)
            text.Children.Add(new TextBlock { Text = listing.Description, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis });
        if (Link(listing.ProjectUrl) is { } link)
            text.Children.Add(link);

        var status = Secondary("");
        status.Visibility = Visibility.Collapsed;
        text.Children.Add(status);

        var actions = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        void Fill()
        {
            actions.Children.Clear();
            var installed = PluginRuntime.Store.Find(listing.Id);
            var newer = installed is not null && IsNewer(listing.Version, installed.Version);
            if (installed is not null && !installed.PendingRemoval && !newer)
            {
                actions.Children.Add(new Button { Content = "Installed", IsEnabled = false, MinWidth = 110 });
                return;
            }
            var install = new Button { Content = newer ? $"Update to {listing.Version}" : "Install", MinWidth = 110, Style = AccentStyle() };
            install.Flyout = ConfirmInstall(listing, async () =>
            {
                actions.Children.Clear();
                actions.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24 });
                var (message, ok) = await InstallAsync(listing);
                status.Text = message;
                status.Foreground = ok ? Brush("TextFillColorSecondaryBrush") : Brush("SystemFillColorCriticalBrush");
                status.Visibility = Visibility.Visible;
                Fill();
            });
            actions.Children.Add(install);
        }
        Fill();
        return Card(Icon(listing.IconUrl), text, actions);
    }

    /// <summary>The trust warning: plugins run in this elevated process.</summary>
    private static Flyout ConfirmInstall(PluginListing listing, Func<Task> install)
    {
        var confirm = new Button { Content = "Install", Style = AccentStyle(), HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout();
        var owner = listing.Verified
            ? "Its owner is verified by nuget.org (reserved id prefix)."
            : listing.Feed is NuGetFeed ? "Its owner is not verified by nuget.org." : $"It comes from {listing.Feed.Name}.";
        flyout.Content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 380,
            Children =
            {
                new TextBlock { Text = $"Install {listing.Title} {listing.Version}?", FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = $"A plugin runs inside NockMon with administrator rights: it can do anything on this computer. Install only plugins from authors you trust.\n\n{owner}",
                    TextWrapping = TextWrapping.Wrap,
                },
                confirm,
            },
        };
        confirm.Click += async (_, _) =>
        {
            flyout.Hide();
            await install();
        };
        return flyout;
    }

    private async Task<(string Message, bool Ok)> InstallAsync(PluginListing listing)
    {
        try
        {
            var usable = await PluginRuntime.InstallAsync(listing, CancellationToken.None);
            _loadedNew |= usable;
            UpdateRestartBar();
            if (usable)
            {
                var widgets = PluginCatalog.Widgets.Where(w => string.Equals(w.Plugin, listing.Id, StringComparison.OrdinalIgnoreCase)).Select(w => w.DisplayName);
                return ($"Installed. In edit mode, Add widget › {string.Join(", ", widgets)}.", true);
            }
            var loaded = PluginRuntime.FindLoaded(listing.Id);
            return PluginRuntime.RestartRequired
                ? ($"Installed {listing.Version}: restart to use it.", true)
                : loaded?.Error is { } error
                    ? ($"Installed, but it failed to load: {error}", false)
                    : ("Installed.", true);
        }
        catch (Exception e)
        {
            return ($"Install failed: {e.Message}", false);
        }
    }

    // ---- Installed --------------------------------------------------------------------------------------------

    private UIElement InstalledPage()
    {
        var page = new StackPanel { Spacing = 8, Padding = new Thickness(0, 0, 16, 0) };
        var installed = PluginRuntime.Store.List();
        if (installed.Count == 0)
        {
            page.Children.Add(Secondary("No plugins installed."));
            var browse = new HyperlinkButton { Content = "Browse plugins", Padding = new Thickness(0) };
            browse.Click += (_, _) => _tabs.SelectedItem = _tabs.Items[0];
            page.Children.Add(browse);
            return new ScrollViewer { Content = page };
        }

        var updates = new List<(InstalledPlugin Plugin, StackPanel Actions)>();
        foreach (var plugin in installed)
        {
            var loaded = PluginRuntime.Loaded.FirstOrDefault(l => l.DevelopmentFolder is null && string.Equals(l.Manifest?.Id, plugin.Id, StringComparison.OrdinalIgnoreCase))
                         ?? PluginRuntime.Loaded.FirstOrDefault(l => l.DevelopmentFolder is null && l.Directory == plugin.Directory);
            var manifest = loaded?.Manifest ?? TryManifest(plugin.Directory);

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(Title(manifest?.Title ?? plugin.Id, plugin.Version));
            text.Children.Add(Secondary(string.Join("  ·  ", new[] { plugin.Id, manifest?.Authors is { Length: > 0 } a ? $"by {a}" : null, $"from {plugin.Source}" }.OfType<string>())));
            if (manifest?.Description is { Length: > 0 } description)
                text.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
            if (Link(manifest?.ProjectUrl) is { } link)
                text.Children.Add(link);
            text.Children.Add(StatusLine(plugin, loaded));

            var actions = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
            if (plugin.PendingRemoval)
            {
                var undo = new Button { Content = "Undo uninstall", MinWidth = 110 };
                undo.Click += (_, _) =>
                {
                    PluginRuntime.Store.CancelUninstall(plugin.Id);
                    Refresh();
                };
                actions.Children.Add(undo);
            }
            else
            {
                var uninstall = new Button { Content = "Uninstall", MinWidth = 110 };
                uninstall.Flyout = Confirm($"Uninstall {manifest?.Title ?? plugin.Id}?",
                    "It is removed at the next start. Its widgets stay in your layouts as placeholders (settings kept) until it is reinstalled.",
                    "Uninstall", () =>
                    {
                        PluginRuntime.Store.Uninstall(plugin.Id);
                        Refresh();
                    });
                actions.Children.Add(uninstall);
                updates.Add((plugin, actions));
            }
            page.Children.Add(Card(Icon(null), text, actions));
        }
        _ = ShowUpdatesAsync(updates);
        return new ScrollViewer { Content = page };

        void Refresh()
        {
            UpdateRestartBar();
            ShowTab();
        }
    }

    private static UIElement StatusLine(InstalledPlugin plugin, LoadedPlugin? loaded)
    {
        var (text, critical) = plugin.PendingRemoval ? ("Uninstalled: removed at the next start.", false)
            : loaded is null ? ("Installed: loads at the next start.", false)
            : loaded.Error is { } error ? ($"Failed to load: {error}", true)
            : loaded.Manifest?.Version is { } running && !string.Equals(Normalize(running), Normalize(plugin.Version), StringComparison.OrdinalIgnoreCase)
                ? ($"Running {running}; {plugin.Version} starts at the next start.", false)
            : ($"Loaded · widgets: {string.Join(", ", loaded.Widgets.Select(w => w.DisplayName))}", false);
        var line = Secondary(text);
        if (critical)
            line.Foreground = Brush("SystemFillColorCriticalBrush");
        return line;
    }

    /// <summary>Adds "Update to x" to the plugins a source has a newer version of.</summary>
    private async Task ShowUpdatesAsync(List<(InstalledPlugin Plugin, StackPanel Actions)> plugins)
    {
        if (plugins.Count == 0)
            return;
        if (_latest is null)
            await SearchAllAsync(null, CancellationToken.None);
        foreach (var (plugin, actions) in plugins)
        {
            if (_latest?.GetValueOrDefault(plugin.Id) is not { } listing || !IsNewer(listing.Version, plugin.Version))
                continue;
            var update = new Button { Content = $"Update to {listing.Version}", MinWidth = 110, Style = AccentStyle() };
            update.Flyout = ConfirmInstall(listing, async () =>
            {
                update.IsEnabled = false;
                var (message, ok) = await InstallAsync(listing);
                if (!ok)
                {
                    update.IsEnabled = true;
                    actions.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 200, FontSize = 12, Foreground = Brush("SystemFillColorCriticalBrush") });
                    return;
                }
                ShowTab();
            });
            actions.Children.Insert(0, update);
        }
    }

    // ---- Development ------------------------------------------------------------------------------------------

    private UIElement DevelopmentPage()
    {
        var page = new StackPanel { Spacing = 10, Padding = new Thickness(0, 0, 16, 0) };
        page.Children.Add(Help.DocView.Render("""
            Load a plugin straight from its build output, without packaging it. To write one:

            - Create a class library targeting `net10.0-windows10.0.19041.0` and reference the `NockMon.Sdk` package with `PrivateAssets="all"`.
            - Implement `IWidgetPlugin` (one class per widget kind) and build: the output folder gets a `nockmon-plugin.json`.
            - Add that folder below, or start NockMon with `nockmon --plugin <folder>`.

            A development plugin is loaded from a copy of the folder: rebuild any time, then restart to reload it. It takes precedence over an installed copy of the same plugin. To test the package itself, `dotnet pack` it and add the output folder as a source in Browse.
            """));

        var settings = _session.Settings;
        var folders = PluginRuntime.CommandLineFolders.Select(f => (Folder: f, CommandLine: true))
            .Concat(settings.DevPluginFolders.Where(f => !PluginRuntime.CommandLineFolders.Contains(f, StringComparer.OrdinalIgnoreCase)).Select(f => (Folder: f, CommandLine: false)));
        foreach (var (folder, commandLine) in folders)
        {
            var loaded = PluginRuntime.Loaded.FirstOrDefault(l => string.Equals(l.DevelopmentFolder, folder, StringComparison.OrdinalIgnoreCase));
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = folder, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var status = loaded is null ? Secondary("Loads at the next start.")
                : loaded.Error is { } failure ? Secondary($"{loaded.Title}: {failure}")
                : Secondary($"{loaded.Title} {loaded.Manifest?.Version} · widgets: {string.Join(", ", loaded.Widgets.Select(w => w.DisplayName))}" + (commandLine ? "  ·  from the command line" : ""));
            if (loaded?.Error is not null)
                status.Foreground = Brush("SystemFillColorCriticalBrush");
            text.Children.Add(status);

            var actions = new StackPanel { Spacing = 4 };
            if (!commandLine)
            {
                var remove = new Button { Content = "Remove", MinWidth = 90 };
                remove.Click += (_, _) =>
                {
                    settings.DevPluginFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
                    _session.SaveSettings();
                    if (loaded is { Error: null })
                        PluginRuntime.MarkRestartRequired();
                    UpdateRestartBar();
                    ShowTab();
                };
                actions.Children.Add(remove);
            }
            page.Children.Add(Card(new FontIcon { Glyph = "\uEC7A", FontSize = 24 }, text, actions));
        }

        var input = new TextBox { PlaceholderText = @"Build output folder, e.g. C:\src\MyPlugin\bin\Debug\net10.0-windows10.0.19041.0", MinWidth = 600 };
        var add = new Button { Content = "Add folder" };
        var error = new TextBlock { Foreground = Brush("SystemFillColorCriticalBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        add.Click += (_, _) =>
        {
            var folder = input.Text.Trim().Trim('"');
            string? problem = folder.Length == 0 ? "Enter a folder."
                : !Directory.Exists(folder) ? "Folder not found."
                : !File.Exists(Path.Combine(folder, PluginManifest.FileName)) ? $"No {PluginManifest.FileName} in this folder: build the plugin project (it must reference NockMon.Sdk) and use its output folder."
                : null;
            if (problem is null)
            {
                folder = Path.GetFullPath(folder);
                if (settings.DevPluginFolders.Contains(folder, StringComparer.OrdinalIgnoreCase) || PluginRuntime.CommandLineFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    problem = "Already listed.";
            }
            error.Text = problem ?? "";
            error.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            if (problem is not null)
                return;
            settings.DevPluginFolders.Add(folder);
            _session.SaveSettings();
            var loaded = PluginRuntime.LoadDevelopment(folder);
            _loadedNew |= loaded.Error is null && loaded.DevelopmentFolder == folder;
            UpdateRestartBar();
            ShowTab();
        };
        page.Children.Add(Row(input, add));
        page.Children.Add(error);
        return new ScrollViewer { Content = page };
    }

    // ---- Building blocks --------------------------------------------------------------------------------------

    private static PluginManifest? TryManifest(string directory)
    {
        try
        {
            return PluginManifest.Load(directory);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static bool IsNewer(string candidate, string current) =>
        NuGetVersion.TryParse(candidate, out var a) && NuGetVersion.TryParse(current, out var b) && a > b;

    private static string Normalize(string version) => NuGetVersion.TryParse(version, out var v) ? v.ToNormalizedString() : version;

    private static Border Card(FrameworkElement icon, FrameworkElement text, FrameworkElement actions)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(actions, 2);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(actions);
        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"),
            Background = Brush("CardBackgroundFillColorDefaultBrush"),
            Child = grid,
        };
    }

    private static FrameworkElement Icon(string? url)
    {
        var fallback = new FontIcon { Glyph = "\uEA86", FontSize = 28, Foreground = Brush("TextFillColorSecondaryBrush") };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return fallback;
        var host = new ContentControl { Width = 40, Height = 40 };
        var image = new Image { Width = 40, Height = 40, Source = new BitmapImage(uri) { DecodePixelWidth = 80 } };
        image.ImageFailed += (_, _) => host.Content = fallback;
        host.Content = image;
        return host;
    }

    private static TextBlock Title(string title, string version)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15 });
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + version, Foreground = Brush("TextFillColorSecondaryBrush") });
        return block;
    }

    private static TextBlock Secondary(string text) =>
        new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextFillColorSecondaryBrush") };

    private static HyperlinkButton? Link(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? new HyperlinkButton { Content = uri.Host + uri.AbsolutePath.TrimEnd('/'), NavigateUri = uri, Padding = new Thickness(0), FontSize = 12 }
            : null;

    private static Flyout Confirm(string title, string message, string action, Action confirmed)
    {
        var button = new Button { Content = action, HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 360,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    button,
                },
            },
        };
        button.Click += (_, _) =>
        {
            flyout.Hide();
            confirmed();
        };
        return flyout;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }

    private static Style AccentStyle() => (Style)Application.Current.Resources["AccentButtonStyle"];

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
