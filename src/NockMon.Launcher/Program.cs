using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

const int ERROR_CANCELLED = 1223;
// Same values as NockMon.Shell.SingleInstance in the app.
const string MutexName = @"Local\NockMon.Instance";
const string ShowMessageName = "NockMon.ShowWindow";

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("nockmon: NockMon runs on Windows only.");
    return 1;
}

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

if (FindInstalledApp() is not { } appPath)
{
    Console.Error.WriteLine("""
        nockmon: NockMon is not installed. It runs as administrator, so it installs in Program Files:
          winget install Nockawa.NockMon
        or the installer from https://github.com/nockawa/NockMon/releases
        """);
    return 1;
}

// The app needs elevation; asking UAC here avoids the app's own relaunch prompt.
var psi = new ProcessStartInfo(appPath)
{
    UseShellExecute = true,
    Verb = "runas",
    WorkingDirectory = Path.GetDirectoryName(appPath)!,
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

// The installer records its folder in HKLM under its AppId (installer/NockMon.iss). Both the key and Program Files are
// administrators-only: nothing a non-elevated process could have replaced, unlike this tool's own folder.
[SupportedOSPlatform("windows")]
static string? FindInstalledApp()
{
    const string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8438DA19-4C3D-4C08-AA4F-8D5D11F16551}_is1";
    using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
    using var key = hive.OpenSubKey(uninstallKey);
    if (key?.GetValue("InstallLocation") is not string { Length: > 0 } folder)
        return null;
    var path = Path.GetFullPath(Path.Combine(folder, "NockMon.exe"));
    var programFiles = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) + Path.DirectorySeparatorChar;
    return path.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
}

static async Task<int> RelayMcpAsync()
{
    // Same name as NockMon.Core.Agents.AgentPipeServer.DefaultPipeName: per user, elevated or not.
    var name = $"NockMon.Mcp.{System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value}";
    // Identification: the server may identify the relay, not act as it.
    using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
        System.Security.Principal.TokenImpersonationLevel.Identification);
    try
    {
        await pipe.ConnectAsync(3000);
        // Owned by this user (NockMon sets it), not another account squatting the name while NockMon is not running.
        // Not PipeOptions.CurrentUserOnly: it compares with the token's default owner, the Administrators group when
        // the relay runs elevated (an agent started from an administrator terminal), and would refuse NockMon's pipe.
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (pipe.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) is not System.Security.Principal.SecurityIdentifier owner
            || owner != identity.User)
            throw new UnauthorizedAccessException();
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine("nockmon mcp: the agent pipe belongs to another account, not to your NockMon: refusing to connect.");
        return 1;
    }
    catch (Exception e) when (e is TimeoutException or IOException)
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
