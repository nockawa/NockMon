using System.IO.Pipes;
using NockMon.Core.Agents;
using NockMon.Shell;

namespace NockMon.Agents;

/// <summary>
/// "NockMon.exe mcp": the MCP stdio server for AI agents, relayed to the running instance's pipe. Same as the dotnet
/// tool's ("nockmon mcp"), for installs without the tool (the installer's PATH entry makes it "nockmon mcp"). Runs before
/// elevation and never starts the app (no UAC prompt behind an agent's back). Works in this GUI-subsystem executable:
/// MCP clients pass pipes as its standard handles.
/// </summary>
internal static class McpRelay
{
    public const string Argument = "mcp";

    public static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        // Identification: the server may identify the relay, not act as it.
        using var pipe = new NamedPipeClientStream(".", AgentPipeServer.DefaultPipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(3000);
            if (!AgentPipeServer.IsOwnedByCurrentUser(pipe))
                throw new UnauthorizedAccessException(); // Another account's pipe: a squatter while NockMon is not running.
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("NockMon mcp: the agent pipe belongs to another account, not to your NockMon: refusing to connect.");
            return 1;
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            Console.Error.WriteLine(SingleInstance.IsAnotherInstanceRunning()
                ? "NockMon mcp: AI agent access is off. Turn it on in NockMon (toolbar, AI agents button), then reconnect."
                : "NockMon mcp: NockMon is not running. Start it, turn on AI agent access, then reconnect.");
            return 1;
        }

        // Byte relay both ways; the MCP session runs in the app. Ends when either side closes.
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        await Task.WhenAny(CopyAsync(stdin, pipe), CopyAsync(pipe, stdout));
        return 0;
    }

    private static async Task CopyAsync(Stream from, Stream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read));
                await to.FlushAsync();
            }
        }
        catch (IOException)
        {
            // Broken pipe: the other side is gone.
        }
    }
}
