using Microsoft.UI.Dispatching;
using NockMon.Core.Agents;
using NockMon.Core.Layout;
using NockMon.Core.Sampling;

namespace NockMon.Agents;

/// <summary>
/// The AI agent server (MCP over a named pipe) as the user configured it: runs while <c>AgentAccess</c> is on, reports
/// its state on the UI thread.
/// </summary>
public sealed class AgentAccess : IDisposable
{
    private readonly LayoutSession _session;
    private readonly DispatcherQueue _dispatcher;
    private readonly AgentPipeServer _server;

    /// <param name="layoutHost">The window, for the layout tools (called on the UI thread).</param>
    public AgentAccess(LayoutSession session, SamplingEngine engine, DispatcherQueue dispatcher, ILayoutHost layoutHost)
    {
        _session = session;
        _dispatcher = dispatcher;
        var queries = new MonitorQueries(engine, () => _session.Settings.AgentCommandLines);
        var layouts = new LayoutTools(new LayoutAgent(session, layoutHost), new UiDispatcher(dispatcher));
        var version = Plugins.PluginRuntime.HostVersion.ToString(3);
        _server = new AgentPipeServer(() => new MonitorTools(queries).CreateServerOptions(version, layouts));
        _server.SessionsChanged += () => _dispatcher.TryEnqueue(() => Changed?.Invoke());
        Apply();
    }

    /// <summary>Raised on the UI thread when the server starts, stops, fails, or a client connects or leaves.</summary>
    public event Action? Changed;

    public bool Enabled
    {
        get => _session.Settings.AgentAccess;
        set
        {
            if (_session.Settings.AgentAccess == value)
                return;
            _session.Settings.AgentAccess = value;
            _session.SaveSettings();
            Apply();
        }
    }

    public bool ShareCommandLines
    {
        get => _session.Settings.AgentCommandLines;
        set
        {
            _session.Settings.AgentCommandLines = value;
            _session.SaveSettings();
        }
    }

    /// <summary>Agents may change layouts (through an edit session the user reviews).</summary>
    public bool AllowLayoutChanges
    {
        get => _session.Settings.AgentLayoutChanges;
        set
        {
            _session.Settings.AgentLayoutChanges = value;
            _session.SaveSettings();
        }
    }

    public IReadOnlyList<string> Clients => _server.Clients;

    /// <summary>Why the server is not running although enabled (pipe name taken), or null.</summary>
    public string? Error => Enabled ? _server.Error : null;

    private void Apply()
    {
        if (Enabled)
            _server.Start();
        else
            _server.Stop();
        Changed?.Invoke();
    }

    public void Dispose() => _server.Dispose();

    /// <summary>Runs agent work on the UI thread (inline when already on it).</summary>
    private sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            if (queue.HasThreadAccess)
                return Task.FromResult(action());
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryEnqueue(() =>
                {
                    try
                    {
                        done.SetResult(action());
                    }
                    catch (Exception e)
                    {
                        done.SetException(e);
                    }
                }))
                done.SetException(new InvalidOperationException("NockMon is shutting down."));
            return done.Task;
        }
    }
}
