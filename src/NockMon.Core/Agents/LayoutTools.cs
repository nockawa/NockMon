using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace NockMon.Core.Agents;

/// <summary>Runs work on the UI thread (the layout session and the board live there).</summary>
public interface IUiDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> action);
}

/// <summary>
/// The MCP tools that read and change layouts (dashboards). Each call runs <see cref="LayoutAgent"/> on the UI thread;
/// changes land in an edit session the user reviews (Done keeps, Cancel discards).
/// </summary>
public sealed class LayoutTools(LayoutAgent agent, IUiDispatcher ui)
{
    public const string Instructions = """
        Layouts (dashboards): NockMon shows a board of widgets on a grid of cells, organized in layouts (the user switches
        between them) and pages (tabs). get_layout gives the current layout as JSON with widget ids; list_widget_kinds
        gives every widget kind with an example JSON (the layout file format, "$type" first). Changes (add_widget,
        update_widget, remove_widget, pages) open an edit session in NockMon: the user sees them live, then clicks Done
        to keep them or Cancel to discard them. You cannot save: tell the user to review and click Done. get_edit_state
        tells whether they did. Switching, creating, renaming or deleting layouts waits until no session is pending.
        Omit "bounds" to place a widget at the first free spot; "placement" gives the visible area in cells.
        """;

    public IEnumerable<McpServerTool> CreateTools() => typeof(LayoutTools)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .Select(m => McpServerTool.Create(m, this, new McpServerToolCreateOptions { SerializerOptions = AgentJson.Options }));

    private static string Client(McpServer server) => server.ClientInfo?.Title ?? server.ClientInfo?.Name ?? "An AI agent";

    [McpServerTool(Name = "list_layouts", Title = "Layouts", ReadOnly = true, OpenWorld = false)]
    [Description("The layouts (name, page and widget counts), which one is shown and on which page, the edit state, and the visible board area in cells.")]
    public Task<LayoutsInfo> ListLayouts() => ui.InvokeAsync(agent.ListLayouts);

    [McpServerTool(Name = "get_layout", Title = "Layout JSON", ReadOnly = true, OpenWorld = false)]
    [Description("A layout as JSON: pages (with index, name, icon) and their widgets (id, \"$type\", title, bounds in cells, settings), " +
                 "plus the visible placement area and the edit state. Pending edits are included for the current layout.")]
    public Task<JsonObject> GetLayout([Description("Layout name; omit for the current one.")] string? name = null) =>
        ui.InvokeAsync(() => agent.GetLayout(name));

    [McpServerTool(Name = "list_widget_kinds", Title = "Widget kinds", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Every widget kind (plugin widgets included): name, default and minimum size in cells, what it shows, and an example " +
                 "widget JSON to start from (charts take series like {\"kind\":\"system\",\"metric\":\"Cpu\"} or " +
                 "{\"kind\":\"process\",\"field\":\"Cpu\",\"pattern\":\"chrome\"}).")]
    public Task<IReadOnlyList<WidgetKindInfo>> ListWidgetKinds() => ui.InvokeAsync(agent.ListWidgetKinds);

    [McpServerTool(Name = "get_edit_state", Title = "Edit state", ReadOnly = true, OpenWorld = false)]
    [Description("Whether changes are pending review in NockMon, and what happened to the last session (saved with Done, discarded with Cancel).")]
    public Task<EditState> GetEditState() => ui.InvokeAsync(agent.State);

    [McpServerTool(Name = "add_widget", Title = "Add a widget", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a widget to the current layout (pending review). The widget is JSON in the layout format: start from a " +
                 "list_widget_kinds example. Omit bounds for the first free spot, or give {\"column\",\"row\",\"columnSpan\",\"rowSpan\"}. " +
                 "The page being edited is shown to the user.")]
    public Task<EditResult> AddWidget(
        McpServer server,
        [Description("Widget JSON, \"$type\" first, e.g. {\"$type\":\"value\",\"title\":\"Chrome CPU\",\"source\":{\"kind\":\"process\",\"field\":\"Cpu\",\"pattern\":\"chrome\"}}.")] JsonElement widget,
        [Description("Page index (get_layout); omit for the page shown.")] int? page = null) =>
        ui.InvokeAsync(() => agent.AddWidget(widget.GetRawText(), page, Client(server)));

    [McpServerTool(Name = "update_widget", Title = "Change a widget", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Replaces a widget's settings (pending review): pass the full widget JSON (from get_layout, modified). Omit bounds " +
                 "to keep its place; give bounds to move or resize it. Works for stack items too.")]
    public Task<EditResult> UpdateWidget(
        McpServer server,
        [Description("Widget id (get_layout).")] Guid id,
        [Description("The widget's new JSON, \"$type\" first.")] JsonElement widget) =>
        ui.InvokeAsync(() => agent.UpdateWidget(id, widget.GetRawText(), Client(server)));

    [McpServerTool(Name = "remove_widget", Title = "Remove a widget", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Removes a widget from the current layout (pending review: Cancel brings it back).")]
    public Task<EditResult> RemoveWidget(McpServer server, [Description("Widget id (get_layout).")] Guid id) =>
        ui.InvokeAsync(() => agent.RemoveWidget(id, Client(server)));

    [McpServerTool(Name = "add_page", Title = "Add a page", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a page (tab) to the current layout (pending review). With several pages, NockMon shows a tab rail on the left.")]
    public Task<EditResult> AddPage(
        McpServer server,
        [Description("Page name (the tab's tooltip).")] string name,
        [Description("Fluent UI System Icons name: board, data_area, gauge, top_speed, developer_board, server, games, cloud, storage, wifi_1…")] string? icon = null) =>
        ui.InvokeAsync(() => agent.AddPage(name, icon, Client(server)));

    [McpServerTool(Name = "update_page", Title = "Rename a page", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Renames a page or changes its icon (pending review).")]
    public Task<EditResult> UpdatePage(
        McpServer server,
        [Description("Page index (get_layout).")] int page,
        [Description("New name; omit to keep it.")] string? name = null,
        [Description("New icon (Fluent UI System Icons name); omit to keep it.")] string? icon = null) =>
        ui.InvokeAsync(() => agent.UpdatePage(page, name, icon, Client(server)));

    [McpServerTool(Name = "remove_page", Title = "Remove a page", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Removes a page and its widgets (pending review: Cancel brings them back). The last page cannot be removed.")]
    public Task<EditResult> RemovePage(McpServer server, [Description("Page index (get_layout).")] int page) =>
        ui.InvokeAsync(() => agent.RemovePage(page, Client(server)));

    [McpServerTool(Name = "switch_layout", Title = "Show a layout", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Shows another layout. Refused while an edit session is pending.")]
    public Task<LayoutsInfo> SwitchLayout([Description("Layout name (list_layouts).")] string name) =>
        ui.InvokeAsync(() => agent.SwitchLayout(name));

    [McpServerTool(Name = "create_layout", Title = "Create a layout", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a layout and shows it: empty, or a copy of the current one. Then fill it with add_widget. Refused while an edit session is pending.")]
    public Task<LayoutsInfo> CreateLayout(
        [Description("New layout name.")] string name,
        [Description("Start from a copy of the current layout instead of an empty one.")] bool copyCurrent = false) =>
        ui.InvokeAsync(() => agent.CreateLayout(name, copyCurrent));

    [McpServerTool(Name = "rename_layout", Title = "Rename the layout", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Renames the current layout. Refused while an edit session is pending.")]
    public Task<LayoutsInfo> RenameLayout([Description("New name.")] string name) =>
        ui.InvokeAsync(() => agent.RenameLayout(name));

    [McpServerTool(Name = "delete_layout", Title = "Delete a layout", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes a layout. NockMon asks the user to confirm; the result says whether they accepted. Refused while an edit session is pending.")]
    public async Task<DeleteResult> DeleteLayout(McpServer server, [Description("Layout name (list_layouts).")] string name)
    {
        var (deleted, layouts) = await (await ui.InvokeAsync(() => agent.DeleteLayoutAsync(name, Client(server)))).ConfigureAwait(false);
        return new DeleteResult(deleted, deleted ? $"Layout '{name}' deleted." : "The user declined: nothing was deleted.", layouts);
    }
}

public sealed record DeleteResult(bool Deleted, string Message, LayoutsInfo Layouts);
