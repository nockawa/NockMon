using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NockMon.Core.Agents;
using NockMon.Core.Layout;
using NockMon.Core.Metrics;
using NockMon.Core.Persistence;

namespace NockMon.Core.Tests;

internal sealed class FakeLayoutHost : ILayoutHost, IUiDispatcher
{
    public GridSize PlacementBounds { get; set; } = new(20, 12);
    public int LogicalProcessors => 4;
    public bool AllowChanges { get; set; } = true;
    public bool Confirm { get; set; }
    public List<string> EditsStarted { get; } = [];
    public int Changes { get; private set; }
    public string? Confirmed { get; private set; }

    public bool IsValidIcon(string name) => name is "board" or "gauge" or "server";
    public void AgentEditStarted(string client) => EditsStarted.Add(client);
    public void LayoutChanged() => Changes++;

    public Task<bool> ConfirmAsync(string title, string message, string accept)
    {
        Confirmed = message;
        return Task.FromResult(Confirm);
    }

    public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
}

public sealed class LayoutAgentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nockmon-agent-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLayoutHost _host = new();
    private readonly LayoutSession _session;
    private readonly LayoutAgent _agent;

    /// <summary>The default layout: a 10×8 table at (0,0), two 8×4 charts at (10,0) and (10,4). Visible board 20×12.</summary>
    public LayoutAgentTests()
    {
        _session = new LayoutSession(new LayoutStore(Path.Combine(_dir, "Layouts")), new SettingsStore(Path.Combine(_dir, "settings.json")));
        _agent = new LayoutAgent(_session, _host);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private const string Value = """{"$type":"value","title":"Chrome CPU","source":{"kind":"process","field":"Cpu","pattern":"chrome"}}""";

    [Fact]
    public void AddWidget_PlacesAtTheFirstFreeSpot_InAnEditSessionTheUserDecides()
    {
        var result = _agent.AddWidget(Value, page: null, "claude-code");

        Assert.Equal(new GridRect(18, 0, 2, 2), result.Bounds); // Right of the charts: the first free 2×2, row by row.
        Assert.True(_session.IsEditing);
        Assert.Equal(["claude-code"], _host.EditsStarted);
        Assert.Equal("agent", result.EditState.By);
        Assert.Equal(4, _session.Current.Widgets.Count);

        _agent.AddWidget(Value, null, "claude-code"); // Same session: no second start.
        Assert.Single(_host.EditsStarted);

        _session.CancelEdit(); // The user clicks Cancel.
        Assert.Equal(3, _session.Current.Widgets.Count);
        Assert.Contains("Cancel", _agent.State().LastOutcome);
        Assert.False(_agent.State().Editing);
    }

    [Fact]
    public void CommittedChangesAreSaved()
    {
        var id = _session.Current.Widgets[1].Id;
        _agent.RemoveWidget(id, "agent");
        _session.CommitEdit(); // The user clicks Done.

        Assert.Contains("Done", _agent.State().LastOutcome);
        var reloaded = new LayoutStore(Path.Combine(_dir, "Layouts")).Load("Default");
        Assert.DoesNotContain(reloaded.Widgets, w => w.Id == id);
    }

    [Fact]
    public void ExplicitBounds_AreChecked_WithAFreeSpotSuggested()
    {
        var overlapping = Value.Replace("\"title\"", "\"bounds\":{\"column\":2,\"row\":2,\"columnSpan\":2,\"rowSpan\":2},\"title\"");
        var e = Assert.Throws<AgentException>(() => _agent.AddWidget(overlapping, null, "a"));
        Assert.Contains("column 18, row 0, 2×2", e.Message);
        Assert.False(_session.IsEditing); // Nothing applied: no session left open.

        var tooSmall = """{"$type":"processTable","bounds":{"column":0,"row":8,"columnSpan":2,"rowSpan":1}}""";
        Assert.Contains("at least 3×2", Assert.Throws<AgentException>(() => _agent.AddWidget(tooSmall, null, "a")).Message);

        var fits = Value.Replace("\"title\"", "\"bounds\":{\"column\":18,\"row\":8,\"columnSpan\":2,\"rowSpan\":2},\"title\"");
        Assert.Equal(new GridRect(18, 8, 2, 2), _agent.AddWidget(fits, null, "a").Bounds);
    }

    [Fact]
    public void UpdateWidget_KeepsOrMovesTheWidget()
    {
        var chart = _session.Current.Widgets[1];
        var json = LayoutStore.SerializeWidget(chart).Replace("\"title\": \"CPU\"", "\"title\": \"Processor\"");
        var kept = _agent.UpdateWidget(chart.Id, json, "a");
        Assert.Equal(chart.Bounds, kept.Bounds);
        Assert.Equal("Processor", _session.Current.Widgets[1].Title);
        Assert.Equal(chart.Id, _session.Current.Widgets[1].Id);

        var text = json.Replace("\"column\": 10,", "\"column\": 0,").Replace("\"row\": 0,", "\"row\": 8,");
        Assert.Equal(new GridRect(0, 8, 8, 4), _agent.UpdateWidget(chart.Id, text, "a").Bounds);
        Assert.Throws<AgentException>(() => _agent.UpdateWidget(Guid.NewGuid(), json, "a"));
    }

    [Fact]
    public void InvalidWidgets_AreRejectedWithAHint()
    {
        Assert.Contains("list_widget_kinds", Assert.Throws<AgentException>(() => _agent.AddWidget("""{"$type":"gauge"}""", null, "a")).Message);
        Assert.Contains("Core 9", Assert.Throws<AgentException>(() =>
            _agent.AddWidget("""{"$type":"value","source":{"kind":"core","metric":"Usage","core":9}}""", null, "a")).Message);
        Assert.Contains("no history", Assert.Throws<AgentException>(() =>
            _agent.AddWidget("""{"$type":"value","source":{"kind":"process","field":"VirtualSize","pattern":"x"}}""", null, "a")).Message);
        Assert.Contains("not loaded", Assert.Throws<AgentException>(() =>
            _agent.AddWidget("""{"$type":"plugin","plugin":"Nope","widget":"w"}""", null, "a")).Message);
        Assert.False(_session.IsEditing);
    }

    [Fact]
    public void EveryKindExample_CanBeAdded()
    {
        _host.PlacementBounds = new GridSize(int.MaxValue, int.MaxValue);
        var kinds = _agent.ListWidgetKinds(); // Built-in kinds, plus plugin widgets registered by other tests.
        foreach (var kind in kinds)
        {
            var result = _agent.AddWidget(kind.Example.ToJsonString(), null, "a");
            Assert.True(result.Bounds!.Value.ColumnSpan >= kind.MinColumns, kind.Kind);
        }
        Assert.Equal(3 + kinds.Count, _session.Current.Widgets.Count);
        Assert.True(kinds.Count >= WidgetCatalog.Kinds.Count);
    }

    [Fact]
    public void Pages_AreEditedInTheSession()
    {
        var added = _agent.AddPage("Kernel", "gauge", "a");
        Assert.Equal(1, added.Page);
        Assert.Equal(1, _session.Current.ActivePage); // Shown to the user.
        Assert.Contains("Unknown icon", Assert.Throws<AgentException>(() => _agent.AddPage("X", "nope", "a")).Message);

        _agent.AddWidget(Value, page: 1, "a");
        Assert.Single(_session.Current.Pages[1].Widgets);
        _agent.UpdatePage(1, "Drivers", null, "a");
        Assert.Equal("Drivers", _session.Current.Pages[1].Name);

        _agent.RemovePage(1, "a");
        Assert.Contains("at least one page", Assert.Throws<AgentException>(() => _agent.RemovePage(0, "a")).Message);
        Assert.Throws<AgentException>(() => _agent.UpdatePage(3, "x", null, "a"));
    }

    [Fact]
    public async Task LayoutManagement_WaitsForTheSession_AndDeletionAsksTheUser()
    {
        _agent.AddWidget(Value, null, "a");
        Assert.Contains("Done", Assert.Throws<AgentException>(() => _agent.CreateLayout("Gaming", false)).Message);
        _session.CommitEdit();

        var created = _agent.CreateLayout("Gaming", copyCurrent: false);
        Assert.Equal("Gaming", created.Current);
        Assert.Equal(2, created.Layouts.Count);
        Assert.Empty(_session.Current.Widgets);

        _host.Confirm = false;
        var (deleted, _) = await _agent.DeleteLayoutAsync("default", "Claude Code");
        Assert.False(deleted);
        Assert.Contains("Claude Code wants to delete the layout 'Default'", _host.Confirmed);

        _host.Confirm = true;
        (deleted, var layouts) = await _agent.DeleteLayoutAsync("Default", "Claude Code");
        Assert.True(deleted);
        Assert.Equal(["Gaming"], layouts.Layouts.Select(l => l.Name));
        Assert.Throws<AgentException>(() => _agent.SwitchLayout("Default"));
    }

    [Fact]
    public void ChangesOff_RefusesWrites_ButNotReads()
    {
        _host.AllowChanges = false;
        Assert.Contains("off", Assert.Throws<AgentException>(() => _agent.AddWidget(Value, null, "a")).Message);
        Assert.Throws<AgentException>(() => _agent.CreateLayout("X", false));
        Assert.Equal("Default", _agent.ListLayouts().Current);
        Assert.Equal(3, _agent.GetLayout(null)["pages"]![0]!["widgets"]!.AsArray().Count);
        Assert.False(_session.IsEditing);
    }

    [Fact]
    public void GetLayout_IndexesPagesAndShowsPendingEdits()
    {
        _agent.AddPage("Second", null, "a");
        var json = _agent.GetLayout(null);
        Assert.Equal(1, json["pages"]![1]!["index"]!.GetValue<int>());
        Assert.Equal(20, json["placement"]!["columns"]!.GetValue<int>());
        Assert.True(json["editState"]!["editing"]!.GetValue<bool>());
        Assert.Throws<AgentException>(() => _agent.GetLayout("Missing"));
    }

    [Fact]
    public async Task Mcp_LayoutToolsWithTheClientName()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var engine = new Sampling.SamplingEngine(new FakeProbe(1, () => Raw.Sample(0, [])), new Sampling.SamplingOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)));
        var options = new MonitorTools(new MonitorQueries(engine, () => false)).CreateServerOptions("1.0", new LayoutTools(_agent, _host));
        await using var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
        using var stop = new CancellationTokenSource();
        _ = server.RunAsync(stop.Token);
        var cancel = TestContext.Current.CancellationToken;

        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
                         new McpClientOptions { ClientInfo = new Implementation { Name = "claude-code", Title = "Claude Code", Version = "1" } }, cancellationToken: cancel))
        {
            Assert.Contains("Done", client.ServerInstructions);
            var tools = await client.ListToolsAsync(cancellationToken: cancel);
            Assert.Equal(8 + 14, tools.Count);
            Assert.True(tools.Single(t => t.Name == "delete_layout").ProtocolTool.Annotations?.DestructiveHint);

            var add = await client.CallToolAsync("add_widget",
                new Dictionary<string, object?> { ["widget"] = JsonSerializer.Deserialize<JsonElement>(Value) }, cancellationToken: cancel);
            Assert.NotEqual(true, add.IsError);
            Assert.Contains("Chrome CPU", string.Concat(add.Content.OfType<TextContentBlock>().Select(c => c.Text)));
            Assert.Equal(["Claude Code"], _host.EditsStarted);

            var layout = await client.CallToolAsync("get_layout", cancellationToken: cancel);
            Assert.Contains("\"$type\":\"value\"", string.Concat(layout.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        }
        stop.Cancel();
        engine.Dispose();
    }
}
