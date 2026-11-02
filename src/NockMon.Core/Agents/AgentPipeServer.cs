using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Server;

namespace NockMon.Core.Agents;

/// <summary>
/// Serves the MCP tools on a named pipe, one MCP session per connection. Agents reach it through <c>nockmon mcp</c>,
/// a stdio relay that MCP clients start (Claude Code, VS Code…): no TCP port, no HTTP.
/// <para>
/// Security: the app is elevated, so a default pipe would carry a High integrity label and an administrators-only DACL,
/// refusing the non-elevated relay. The pipe is created with an explicit descriptor instead: the current user only,
/// network logons denied, remote clients rejected, Medium integrity label (Low integrity sandboxes can neither read
/// nor write). The pipe is owned by the user (the relay checks that owner: another account's pipe is an impostor), but
/// the user's non-elevated processes get no FILE_CREATE_PIPE_INSTANCE (no extra instances stealing connections) and no
/// WRITE_DAC (OWNER RIGHTS replaces the owner's implicit rights).
/// An instance is created with FILE_FLAG_FIRST_PIPE_INSTANCE whenever none of ours exists: if another process already
/// owns the name (a squatter impersonating NockMon), the server refuses to start rather than share it.
/// </para>
/// </summary>
public sealed partial class AgentPipeServer : IDisposable
{
    private readonly Func<McpServerOptions> _options;
    private readonly string _pipeName;
    private readonly Lock _lock = new();
    private readonly List<McpServer> _sessions = [];
    private readonly List<NamedPipeServerStream> _connected = [];
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private int _instances; // Our pipe instances alive (listening or serving): none means the name is free.

    /// <param name="options">Options of a new session (a fresh tool collection each time).</param>
    /// <param name="pipeName">Pipe name (tests); <see cref="DefaultPipeName"/> by default.</param>
    public AgentPipeServer(Func<McpServerOptions> options, string? pipeName = null)
    {
        _options = options;
        _pipeName = pipeName ?? DefaultPipeName;
    }

    /// <summary>Per user: an elevated and a non-elevated process of the same user compute the same name. The relay duplicates it.</summary>
    public static string DefaultPipeName { get; } = $"NockMon.Mcp.{WindowsIdentity.GetCurrent().User!.Value}";

    /// <summary>Raised (any thread) when a session starts or ends.</summary>
    public event Action? SessionsChanged;

    /// <summary>Null while serving or stopped normally; otherwise why the server is not running.</summary>
    public string? Error { get; private set; }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Connected clients, by the name they gave at initialization ("claude-code"), "agent" until then.</summary>
    public IReadOnlyList<string> Clients
    {
        get
        {
            lock (_lock)
                return _sessions.Select(s => s.ClientInfo?.Title ?? s.ClientInfo?.Name ?? "agent").ToList();
        }
    }

    public void Start()
    {
        if (IsRunning)
            return;
        Error = null;
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptAsync(_stop.Token));
    }

    public void Stop()
    {
        if (_stop is null)
            return;
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        // Access turned off ends the sessions now, even one whose tool call waits (on a dialog) and ignores cancellation.
        lock (_lock)
        {
            foreach (var pipe in _connected)
                pipe.Dispose();
        }
        _stop.Dispose();
        _stop = null;
        _loop = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(_pipeName, first: Volatile.Read(ref _instances) == 0);
                Interlocked.Increment(ref _instances);
            }
            catch (Exception e)
            {
                Error = e.Message;
                SessionsChanged?.Invoke();
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReleaseAsync(pipe).ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await ReleaseAsync(pipe).ConfigureAwait(false); // Client gone before the connection completed.
                continue;
            }
            _ = ServeAsync(pipe, cancel);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        McpServer? server = null;
        lock (_lock)
            _connected.Add(pipe);
        try
        {
            server = McpServer.Create(new StreamServerTransport(pipe, pipe, "nockmon"), _options());
            lock (_lock)
                _sessions.Add(server);
            SessionsChanged?.Invoke();
            await server.RunAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Client disconnected abruptly.
        }
        finally
        {
            if (server is not null)
            {
                lock (_lock)
                    _sessions.Remove(server);
                await server.DisposeAsync().ConfigureAwait(false);
            }
            lock (_lock)
                _connected.Remove(pipe);
            await ReleaseAsync(pipe).ConfigureAwait(false);
            SessionsChanged?.Invoke();
        }
    }

    private async Task ReleaseAsync(NamedPipeServerStream pipe)
    {
        await pipe.DisposeAsync().ConfigureAwait(false);
        Interlocked.Decrement(ref _instances);
    }

    private const uint PIPE_ACCESS_DUPLEX = 0x3;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint PIPE_TYPE_BYTE = 0x0;
    private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x8;
    private const uint PIPE_UNLIMITED_INSTANCES = 255;
    private const uint SDDL_REVISION_1 = 1;
    private const int ERROR_ACCESS_DENIED = 5;

    /// <summary>Read and write, nothing else: FILE_GENERIC_READ | FILE_GENERIC_WRITE without FILE_CREATE_PIPE_INSTANCE.</summary>
    private const string ClientAccess = "0x12019b";

    /// <summary>
    /// Owned by the user; deny network logons (first: a network logon also carries the user's SID); the user reads and
    /// writes, OWNER RIGHTS likewise (no implicit WRITE_DAC); Medium mandatory label, no write or read up. Elevated, only
    /// administrators (enabled in the elevated token alone) and SYSTEM may create instances; not elevated (tests,
    /// profiling builds), the user must, and nothing separates it from its clients anyway.
    /// </summary>
    internal static string SecurityDescriptor(string userSid, bool elevated) =>
        $"O:{userSid}D:P(D;;GA;;;NU)(A;;{(elevated ? ClientAccess : "GA")};;;{userSid})(A;;{ClientAccess};;;OW)" +
        $"{(elevated ? "(A;;GA;;;BA)" : "")}(A;;GA;;;SY)S:(ML;;NWNR;;;ME)";

    private static unsafe NamedPipeServerStream CreatePipe(string pipeName, bool first)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(SecurityDescriptor(sid, Environment.IsPrivilegedProcess), SDDL_REVISION_1, out var descriptor, 0))
            throw new IOException($"Pipe security descriptor rejected (error {Marshal.GetLastPInvokeError()}).");
        try
        {
            var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), SecurityDescriptor = descriptor };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + pipeName,
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | (first ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0),
                PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, PIPE_UNLIMITED_INSTANCES, 65536, 65536, 0, &attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new IOException(first && error == ERROR_ACCESS_DENIED
                    ? "Another process already uses NockMon's agent pipe (another NockMon instance, or a program impersonating it)."
                    : $"Cannot create the agent pipe (error {error}).");
            }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string stringSecurityDescriptor, uint revision, out nint securityDescriptor, nint securityDescriptorSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize,
        uint inBufferSize, uint defaultTimeout, SecurityAttributes* securityAttributes);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
