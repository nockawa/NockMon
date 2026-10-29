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

    public AgentAccess(LayoutSession session, SamplingEngine engine, DispatcherQueue dispatcher)
    {
        _session = session;
        _dispatcher = dispatcher;
        var queries = new MonitorQueries(engine, () => _session.Settings.AgentCommandLines);
        var version = Plugins.PluginRuntime.HostVersion.ToString(3);
        _server = new AgentPipeServer(() => new MonitorTools(queries).CreateServerOptions(version));
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
}
