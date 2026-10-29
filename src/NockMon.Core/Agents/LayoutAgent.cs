using System.Text.Json;
using System.Text.Json.Nodes;
using NockMon.Core.Help;
using NockMon.Core.Layout;
using NockMon.Core.Metrics;
using NockMon.Core.Persistence;

namespace NockMon.Core.Agents;

/// <summary>What <see cref="LayoutAgent"/> needs from the window. Every member is called on the UI thread.</summary>
public interface ILayoutHost
{
    /// <summary>Cells widgets can be placed in (the visible board; unbounded when it scrolls).</summary>
    GridSize PlacementBounds { get; }

    int LogicalProcessors { get; }

    /// <summary>The user allows agents to change layouts (off: the write tools refuse).</summary>
    bool AllowChanges { get; }

    bool IsValidIcon(string name);

    /// <summary>An agent started an edit session: show edit mode and who is editing.</summary>
    void AgentEditStarted(string client);

    /// <summary>The current layout changed (widgets, pages, shown page): rebuild what shows it.</summary>
    void LayoutChanged();

    /// <summary>Asks the user (dialog); true if they accept.</summary>
    Task<bool> ConfirmAsync(string title, string message, string accept);
}

/// <summary>
/// Layout operations for AI agents, with the same rules as the UI. Changes to the current layout go through the edit
/// session: the first change starts it (edit mode, visible), the user reviews and clicks Done (kept) or Cancel
/// (discarded). Agents never commit. Layout management (switch, create, rename, delete) is refused while a session is
/// pending, like in the UI; deletion asks the user. All methods run on the UI thread.
/// </summary>
public sealed class LayoutAgent
{
    private readonly LayoutSession _session;
    private readonly ILayoutHost _host;
    private string? _editingClient;
    private string? _lastOutcome;

    public LayoutAgent(LayoutSession session, ILayoutHost host)
    {
        _session = session;
        _host = host;
        session.EditEnded += committed =>
        {
            if (_editingClient is not null)
                _lastOutcome = committed ? "saved: the user clicked Done" : "discarded: the user clicked Cancel";
            _editingClient = null;
        };
    }

    public LayoutsInfo ListLayouts()
    {
        var current = _session.Current;
        var layouts = _session.Names.Select(name =>
        {
            var layout = TryPeek(name);
            return new LayoutSummary(name, layout?.Pages.Count ?? 0, layout?.AllWidgets.Count() ?? 0);
        }).ToList();
        return new LayoutsInfo(current.Name, current.ActivePage, State(), layouts, Board(current));
    }

    /// <summary>A layout as JSON (the file format, with page indexes), plus the placement area.</summary>
    public JsonObject GetLayout(string? name)
    {
        var layout = string.IsNullOrWhiteSpace(name) ? _session.Current : Run(() => _session.Peek(name.Trim()));
        var json = JsonNode.Parse(LayoutStore.Serialize(layout))!.AsObject();
        if (json["pages"] is JsonArray pages)
        {
            for (var i = 0; i < pages.Count; i++)
                pages[i]!.AsObject().Insert(0, "index", i);
        }
        json["placement"] = JsonSerializer.SerializeToNode(Board(layout), AgentJson.Options);
        json["editState"] = JsonSerializer.SerializeToNode(State(), AgentJson.Options);
        return json;
    }

    public IReadOnlyList<WidgetKindInfo> ListWidgetKinds()
    {
        var kinds = WidgetCatalog.Kinds.Select(kind =>
        {
            var size = WidgetCatalog.DefaultSize(kind);
            var min = WidgetCatalog.MinSize(kind);
            var example = WidgetCatalog.Create(kind, new GridRect(0, 0, size.Columns, size.Rows));
            return new WidgetKindInfo(kind.ToString(), WidgetCatalog.DisplayName(kind), size.Columns, size.Rows, min.Columns, min.Rows,
                Docs.Of(kind).Summary, Example(example));
        });
        var plugins = PluginCatalog.Widgets.Select(w => new WidgetKindInfo("Plugin", $"{w.DisplayName} ({w.PluginTitle})",
            w.DefaultSize.Columns, w.DefaultSize.Rows, w.MinSize.Columns, w.MinSize.Rows, w.Summary,
            Example(WidgetCatalog.Create(w, new GridRect(0, 0, w.DefaultSize.Columns, w.DefaultSize.Rows)))));
        return kinds.Concat(plugins).ToList();
    }

    public EditResult AddWidget(string widgetJson, int? page, string client)
    {
        EnsureAllowed();
        var widget = ParseWidget(widgetJson);
        var (index, target) = Page(page);
        widget.Id = Guid.NewGuid();
        if (widget is StackWidgetConfig stack)
        {
            foreach (var item in stack.Items)
                item.Id = Guid.NewGuid();
        }
        widget.Bounds = Place(target.Widgets, widget, null, widget.Bounds);

        BeginEdit(client);
        target.Widgets.Add(widget);
        Show(index);
        return Result($"Added {Describe(widget)} to page {index} ('{target.Name}') at column {widget.Bounds.Column}, row {widget.Bounds.Row}.", widget, index);
    }

    /// <summary>Replaces a widget (top level or stack item) by <paramref name="widgetJson"/>; omitted bounds keep the current ones.</summary>
    public EditResult UpdateWidget(Guid id, string widgetJson, string client)
    {
        EnsureAllowed();
        var widget = ParseWidget(widgetJson);
        var (index, page, owner, old) = Find(id);
        widget.Id = id;
        if (owner is null)
        {
            // Omitted bounds keep the widget where it is, unless the new settings need more room.
            var min = WidgetCatalog.MinSize(widget);
            var requested = HasSize(widget.Bounds) ? widget.Bounds
                : old.Bounds.ColumnSpan >= min.Columns && old.Bounds.RowSpan >= min.Rows ? old.Bounds : default;
            widget.Bounds = Place(page.Widgets, widget, old, requested);
        }
        else
        {
            if (widget is StackWidgetConfig)
                throw new AgentException("Stacks cannot be nested.");
            widget.Bounds = old.Bounds; // A stack item's bounds are the stack's.
        }

        BeginEdit(client);
        var list = owner?.Items ?? page.Widgets;
        list[list.IndexOf(old)] = widget;
        Show(index);
        return Result($"Updated {Describe(widget)} on page {index} ('{page.Name}').", widget, index);
    }

    public EditResult RemoveWidget(Guid id, string client)
    {
        EnsureAllowed();
        var (index, page, owner, old) = Find(id);
        if (owner is not null)
            throw new AgentException("This widget is inside a stack: update the stack (update_widget with its items) instead.");
        BeginEdit(client);
        page.Widgets.Remove(old);
        Show(index);
        return Result($"Removed {Describe(old)} from page {index} ('{page.Name}').", null, index);
    }

    public EditResult AddPage(string name, string? icon, string client)
    {
        EnsureAllowed();
        name = RequireName(name, "page");
        icon = Icon(icon) ?? LayoutPage.DefaultIcon;
        BeginEdit(client);
        LayoutPages.Add(_session.Current, name, icon);
        var index = _session.Current.Pages.Count - 1;
        Show(index);
        return Result($"Added page {index} ('{name}').", null, index);
    }

    public EditResult UpdatePage(int index, string? name, string? icon, string client)
    {
        EnsureAllowed();
        var (i, page) = Page(index);
        var newName = string.IsNullOrWhiteSpace(name) ? null : RequireName(name, "page");
        var newIcon = Icon(icon);
        BeginEdit(client);
        page.Name = newName ?? page.Name;
        page.Icon = newIcon ?? page.Icon;
        Show(i);
        return Result($"Updated page {i} ('{page.Name}', icon {page.Icon}).", null, i);
    }

    public EditResult RemovePage(int index, string client)
    {
        EnsureAllowed();
        var (i, page) = Page(index);
        if (_session.Current.Pages.Count == 1)
            throw new AgentException("A layout keeps at least one page.");
        BeginEdit(client);
        LayoutPages.Remove(_session.Current, i);
        Show(_session.Current.ActivePage);
        return Result($"Removed page {i} ('{page.Name}') and its {page.Widgets.Count} widget(s).", null, _session.Current.ActivePage);
    }

    public LayoutsInfo SwitchLayout(string name)
    {
        EnsureAllowed();
        EnsureNotEditing();
        Run(() => _session.Switch(Existing(name)));
        return ListLayouts();
    }

    public LayoutsInfo CreateLayout(string name, bool copyCurrent)
    {
        EnsureAllowed();
        EnsureNotEditing();
        name = RequireName(name, "layout");
        Run(() =>
        {
            if (copyCurrent)
                _session.Duplicate(name);
            else
                _session.CreateEmpty(name);
        });
        return ListLayouts();
    }

    public LayoutsInfo RenameLayout(string newName)
    {
        EnsureAllowed();
        EnsureNotEditing();
        Run(() => _session.Rename(RequireName(newName, "layout")));
        _host.LayoutChanged();
        return ListLayouts();
    }

    /// <summary>Asks the user first; returns the layouts afterwards and whether it was deleted.</summary>
    public async Task<(bool Deleted, LayoutsInfo Layouts)> DeleteLayoutAsync(string name, string client)
    {
        EnsureAllowed();
        name = Existing(name);
        EnsureNotEditing();
        if (!await _host.ConfirmAsync("Delete layout", $"{client} wants to delete the layout '{name}'. This cannot be undone.", "Delete"))
            return (false, ListLayouts());
        Run(() => _session.Delete(name));
        return (true, ListLayouts());
    }

    public EditState State() => new(_session.IsEditing, _session.IsEditing ? _editingClient is not null ? "agent" : "user" : null,
        _session.IsEditing ? "Changes are pending: the user reviews them in NockMon, then clicks Done to keep them or Cancel to discard them." : null,
        _lastOutcome);

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private void EnsureAllowed()
    {
        if (!_host.AllowChanges)
            throw new AgentException("Layout changes by agents are off. The user can allow them in NockMon (toolbar, AI agents button).");
    }

    /// <summary>Layout management waits for the pending session, like in the UI.</summary>
    private void EnsureNotEditing()
    {
        if (_session.IsEditing)
            throw new AgentException("An edit session is pending: the user must click Done (keep) or Cancel (discard) in NockMon first.");
    }

    private void BeginEdit(string client)
    {
        if (!_session.IsEditing)
        {
            _session.BeginEdit();
            _editingClient = client;
            _lastOutcome = null;
            _host.AgentEditStarted(client);
        }
        else
        {
            _editingClient ??= client; // Joins the user's session: their Done/Cancel decides for both.
        }
    }

    private void Show(int page)
    {
        _session.Current.ActivePage = page;
        _host.LayoutChanged();
    }

    private EditResult Result(string message, WidgetConfig? widget, int page) =>
        new(message, widget?.Id, widget?.Bounds, page, State());

    private (int Index, LayoutPage Page) Page(int? index)
    {
        var layout = _session.Current;
        var i = index ?? layout.ActivePage;
        if (i < 0 || i >= layout.Pages.Count)
            throw new AgentException($"Page {i} does not exist: the layout has {layout.Pages.Count} page(s), 0 to {layout.Pages.Count - 1}.");
        return (i, layout.Pages[i]);
    }

    private (int Page, LayoutPage Owner, StackWidgetConfig? Stack, WidgetConfig Widget) Find(Guid id)
    {
        var pages = _session.Current.Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            foreach (var w in pages[i].Widgets)
            {
                if (w.Id == id)
                    return (i, pages[i], null, w);
                if (w is StackWidgetConfig stack && stack.Items.FirstOrDefault(item => item.Id == id) is { } item)
                    return (i, pages[i], stack, item);
            }
        }
        throw new AgentException($"No widget {id} in the current layout '{_session.Current.Name}' (get_layout lists the ids).");
    }

    /// <summary>Validated bounds: <paramref name="requested"/> when it has a size (checked), otherwise the first free spot.</summary>
    private GridRect Place(List<WidgetConfig> widgets, WidgetConfig widget, WidgetConfig? replacing, GridRect requested)
    {
        var min = WidgetCatalog.MinSize(widget);
        var area = _host.PlacementBounds;
        var others = widgets.Where(w => !ReferenceEquals(w, replacing)).ToList();
        if (HasSize(requested))
        {
            if (requested.ColumnSpan < min.Columns || requested.RowSpan < min.Rows)
                throw new AgentException($"{Describe(widget)} needs at least {min.Columns}×{min.Rows} cells (columnSpan × rowSpan).");
            if (BoardRules.CanPlace(others, null, requested, area.Columns, area.Rows))
                return requested;
            var free = BoardRules.FindFreeSpot(others, new GridSize(requested.ColumnSpan, requested.RowSpan), area.Columns, area.Rows);
            throw new AgentException($"Bounds {Text(requested)} overlap another widget or leave the visible board ({Area(area)}). " +
                (free is { } f ? $"A free spot of that size: {Text(f)}." : "No free spot of that size: make room, or use a smaller size."));
        }
        return BoardRules.FindFreeSpot(others, DefaultSize(widget), area.Columns, area.Rows)
               ?? BoardRules.FindFreeSpot(others, min, area.Columns, area.Rows)
               ?? throw new AgentException($"No free spot for {Describe(widget)} on this page ({Area(area)}): remove or shrink widgets first.");
    }

    private static GridSize DefaultSize(WidgetConfig widget) => widget switch
    {
        PluginWidgetConfig p => PluginCatalog.Find(p)?.DefaultSize ?? WidgetCatalog.MinSize(widget),
        StackWidgetConfig => WidgetCatalog.MinSize(widget),
        _ => WidgetCatalog.DefaultSize(WidgetCatalog.KindOf(widget)),
    };

    private WidgetConfig ParseWidget(string json)
    {
        WidgetConfig widget;
        try
        {
            widget = LayoutStore.DeserializeWidget(json);
        }
        catch (JsonException e)
        {
            throw new AgentException($"Invalid widget JSON: {e.Message} The format is the layout file's (list_widget_kinds gives an example per kind; \"$type\" first).");
        }
        catch (NotSupportedException e)
        {
            throw new AgentException($"Invalid widget JSON: {e.Message} Put \"$type\" first.");
        }
        Validate(widget);
        return widget;
    }

    /// <summary>Series the widgets would fail to read (the history throws on them).</summary>
    private void Validate(WidgetConfig widget)
    {
        if (widget is StackWidgetConfig stack)
        {
            if (stack.Items.Count < 2)
                throw new AgentException("A stack needs at least 2 items.");
            foreach (var item in stack.Items)
            {
                if (item is StackWidgetConfig)
                    throw new AgentException("Stacks cannot be nested.");
                Validate(item);
            }
            return;
        }
        IEnumerable<SeriesRef> series = widget switch
        {
            ChartWidgetConfig c => c.Series.Select(s => s.Source),
            ValueWidgetConfig v => [v.Source],
            ValueListWidgetConfig l => l.Items,
            _ => [],
        };
        foreach (var s in series)
        {
            switch (s)
            {
                case null:
                    throw new AgentException("A series has no source.");
                case CoreSeries c when c.Core < 0 || c.Core >= _host.LogicalProcessors:
                    throw new AgentException($"Core {c.Core} does not exist: 0 to {_host.LogicalProcessors - 1}.");
                case ProcessSeries p when !MetricCatalog.Get(p.Field).HasHistory:
                    throw new AgentException($"Process field {p.Field} cannot be charted (no history). Chartable: {string.Join(", ", MetricCatalog.ProcessHistoryFields)}.");
                case ProcessSeries p when string.IsNullOrWhiteSpace(p.Pattern):
                    throw new AgentException("A process series needs a pattern (image name, wildcards allowed).");
            }
        }
        if (widget is PluginWidgetConfig plugin && PluginCatalog.Find(plugin) is null)
            throw new AgentException($"Plugin widget '{plugin.Plugin}/{plugin.Widget}' is not loaded (list_widget_kinds lists the available ones).");
    }

    private string? Icon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
            return null;
        var name = icon.Trim().ToLowerInvariant().Replace(' ', '_');
        return _host.IsValidIcon(name)
            ? name
            : throw new AgentException($"Unknown icon '{icon}': a Fluent UI System Icons name such as board, data_area, gauge, top_speed, developer_board, server, games, cloud.");
    }

    private string Existing(string name) =>
        _session.Names.FirstOrDefault(n => string.Equals(n, name?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new AgentException($"No layout '{name}'. Layouts: {string.Join(", ", _session.Names)}.");

    private static string RequireName(string? name, string what) =>
        string.IsNullOrWhiteSpace(name) ? throw new AgentException($"The {what} name cannot be empty.") : name.Trim();

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            throw new AgentException(e.Message);
        }
    }

    private static T Run<T>(Func<T> action)
    {
        var result = default(T)!;
        Run(() => { result = action(); });
        return result;
    }

    private LayoutDocument? TryPeek(string name)
    {
        try
        {
            return _session.Peek(name);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private BoardInfo Board(LayoutDocument layout)
    {
        var area = _host.PlacementBounds;
        var scroll = layout.Board.Overflow == BoardOverflow.Scroll;
        return new BoardInfo(scroll ? null : area.Columns, scroll ? null : area.Rows, layout.Board.CellSize, layout.Board.Overflow.ToString());
    }

    private static bool HasSize(GridRect r) => r.ColumnSpan > 0 && r.RowSpan > 0;

    /// <summary>"'CPU vs Edge' (Chart)", or "'Drivers (DPC/ISR)'" when the title is the kind's name.</summary>
    private static string Describe(WidgetConfig w)
    {
        var kind = WidgetCatalog.DisplayName(w);
        return w.Title is { Length: > 0 } title && !kind.StartsWith(title, StringComparison.OrdinalIgnoreCase) ? $"'{title}' ({kind})" : $"'{kind}'";
    }

    private static string Text(GridRect r) => $"column {r.Column}, row {r.Row}, {r.ColumnSpan}×{r.RowSpan}";

    private static string Area(GridSize a) => a.Columns == int.MaxValue ? "unbounded, it scrolls" : $"{a.Columns}×{a.Rows} cells visible";

    private static JsonNode Example(WidgetConfig widget)
    {
        var json = JsonNode.Parse(LayoutStore.SerializeWidget(widget))!.AsObject();
        json.Remove("id"); // Assigned on add.
        json.Remove("bounds"); // Omitted: the first free spot (an example at 0,0 would collide).
        return json;
    }
}

// --- Results -------------------------------------------------------------------------------------------------------

public sealed record LayoutsInfo(string Current, int ActivePage, EditState EditState, IReadOnlyList<LayoutSummary> Layouts, BoardInfo Placement);

public sealed record LayoutSummary(string Name, int Pages, int Widgets);

/// <param name="Columns">Visible columns widgets can be placed in; null when the board scrolls (unbounded).</param>
public sealed record BoardInfo(int? Columns, int? Rows, double CellSize, string Overflow);

/// <param name="By">Who started the pending session: "agent" or "user"; null when not editing.</param>
/// <param name="LastOutcome">What happened to the last agent session, once it ended.</param>
public sealed record EditState(bool Editing, string? By, string? Note, string? LastOutcome);

public sealed record WidgetKindInfo(
    string Kind, string Name, int DefaultColumns, int DefaultRows, int MinColumns, int MinRows, string Summary, JsonNode Example);

public sealed record EditResult(string Message, Guid? WidgetId, GridRect? Bounds, int Page, EditState EditState);

/// <summary>Serializer options of the agent results (camelCase, enums as names, nulls omitted, relaxed escaping).</summary>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
}
