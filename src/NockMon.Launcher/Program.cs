using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

const int ERROR_CANCELLED = 1223;
// Same values as NockMon.Shell.SingleInstance in the app.
const string MutexName = @"Local\NockMon.Instance";
const string ShowMessageName = "NockMon.ShowWindow";

// "nockmon mcp": MCP server for AI agents (stdio), relayed to the running app. Never starts the app (no UAC prompt
// behind an agent's back).
if (args is ["mcp", ..])
    return await RelayMcpAsync();

// Already running (possibly elevated, hence access denied): bring it up without a UAC prompt.
if (IsRunning())
{
    PostMessageW(0xFFFF, RegisterWindowMessageW(ShowMessageName), 0, 0);
    return 0;
}

var appPath = Path.Combine(AppContext.BaseDirectory, "app", "NockMon.exe");
if (!File.Exists(appPath))
{
    Console.Error.WriteLine($"nockmon: application payload not found at '{appPath}'.");
    return 1;
}

// The app needs elevation; asking UAC here avoids the app's own relaunch prompt.
var psi = new ProcessStartInfo(appPath)
{
    UseShellExecute = true,
    Verb = "runas",
    WorkingDirectory = Environment.CurrentDirectory,
};
foreach (var arg in args)
    psi.ArgumentList.Add(arg);

try
{
    Process.Start(psi);
    return 0;
}
catch (Win32Exception e) when (e.NativeErrorCode == ERROR_CANCELLED)
{
    Console.Error.WriteLine("nockmon: elevation declined, NockMon requires administrator rights.");
    return 1;
}

static async Task<int> RelayMcpAsync()
{
    if (!OperatingSystem.IsWindows())
        return 1;
    // Same name as NockMon.Core.Agents.AgentPipeServer.DefaultPipeName: per user, elevated or not.
    var name = $"NockMon.Mcp.{System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value}";
    using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
        await pipe.ConnectAsync(3000);
    }
    catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(IsRunning()
            ? "nockmon mcp: AI agent access is off. Turn it on in NockMon (toolbar, AI agents button), then reconnect."
            : "nockmon mcp: NockMon is not running. Start it (nockmon), turn on AI agent access, then reconnect.");
        return 1;
    }

    // Byte relay both ways; the MCP session runs in the app. Ends when either side closes.
    var stdin = Console.OpenStandardInput();
    var stdout = Console.OpenStandardOutput();
    await Task.WhenAny(CopyAsync(stdin, pipe), CopyAsync(pipe, stdout));
    return 0;
}

static async Task CopyAsync(Stream from, Stream to)
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

static bool IsRunning()
{
    try
    {
        if (!Mutex.TryOpenExisting(MutexName, out var existing))
            return false;
        existing.Dispose();
        return true;
    }
    catch (UnauthorizedAccessException)
    {
        return true;
    }
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern uint RegisterWindowMessageW(string name);

[DllImport("user32.dll")]
static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
