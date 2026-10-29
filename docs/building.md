# Building from source

[← README](../README.md)

## Prerequisites

- Windows 10 2004 (19041) or later, x64 or ARM64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`global.json` pins 10.0.402, rolling forward to
  later feature bands).
- Optional: an IDE with WinUI support (Rider, Visual Studio 2026, VS Code with C# Dev Kit).

No Windows App SDK runtime install is needed: the app uses it self-contained.

## Build, run, test

```powershell
git clone https://github.com/nockawa/NockMon.git
cd NockMon
dotnet build NockMon.slnx
dotnet test --solution NockMon.slnx
```

Run the app from source:

```powershell
dotnet run --project src/NockMon.App
```

NockMon needs elevation: started non-elevated, it relaunches itself through a UAC prompt. Run your IDE elevated to
debug it directly, or attach to the elevated process.

The app builds for the machine's architecture by default (`win-x64` or `win-arm64`); pass `-r win-x64` or
`-r win-arm64` to choose.

Tests use xUnit v3 on Microsoft.Testing.Platform (`dotnet test --solution`). Most need no elevation; the ones that
exercise native sampling run against the live machine.

## Packaging

**The tool** (`dotnet tool install -g NockMon`):

```powershell
dotnet pack src/NockMon.Launcher -c Release -o artifacts
```

This produces the `NockMon` tool package and one RID-specific package per architecture (`NockMon.win-x64`,
`NockMon.win-arm64`), each carrying the published app. The tool itself (`nockmon`) is a small `net10.0` launcher: it
starts the WinUI app elevated, brings an existing instance to the front, and runs the MCP stdio relay
(`nockmon mcp`).

Try a local package before publishing:

```powershell
dotnet tool install -g NockMon --add-source ./artifacts --version <version>
```

**The plugin SDK**:

```powershell
dotnet pack src/NockMon.Sdk -c Release -o artifacts
```

Its major.minor version is the plugin API version (`PluginApi.Version` and `NockMonPluginApiVersion` in
`build/NockMon.Sdk.props`): keep them in sync.

## Repository layout

| Path | Contents |
|---|---|
| `src/NockMon.App` | The WinUI 3 application: window, board, widgets (Win2D rendering), editors, tray, plugins runtime, AI agents host. |
| `src/NockMon.Core` | Everything testable without UI: sampling (native APIs, kernel ETW session, GPU through D3DKMT and NVML), history, metrics, layouts and their persistence, plugin store and feeds, built-in documentation, MCP tools and server. |
| `src/NockMon.Sdk` | The plugin SDK: interfaces and the MSBuild files that make a project a plugin. |
| `src/NockMon.Launcher` | The `nockmon` dotnet tool: launcher and MCP stdio relay. |
| `tests/NockMon.Core.Tests` | Unit and integration tests of the core. |
| `tools/profiling` | Scripts to profile the app with dotTrace and analyze the snapshots. |

## Architecture in brief

- **Sampling**: a dedicated thread samples the system at the sample interval (CPU per logical processor, memory,
  watched processes, GPUs) and runs a full process scan at the scan interval. Kernel events (disk, network, DPC/ISR,
  process starts and exits) come from a private real-time ETW session and are harvested at each sample.
- **History**: two-rate ring buffers, in memory, for the configured retention (one hour by default): one slot per sample
  for system, per-CPU and watched-process series, one slot per scan for every process metric aggregated per image
  name (so a new chart shows the past, and series survive process restarts).
- **UI**: the board is a grid of widget hosts; charts and graphs render with Win2D. Widgets read the latest snapshot and
  the history on the UI thread at each sample.
- **Layouts**: JSON documents (`System.Text.Json` source-generated), edited through a session with commit and cancel,
  shared by the UI and the AI agents.
- **MCP**: the official MCP C# SDK over a named pipe with an explicit security descriptor; `nockmon mcp` relays stdio
  to it.

## Development settings

| Variable | Effect |
|---|---|
| `NOCKMON_DATA_DIR` | Uses another data folder instead of `%LOCALAPPDATA%\NockMon` (tests, profiling, alternative profiles). |
| `NOCKMON_DEBUG_COMPACT=1` | Enters compact mode 4 s after start and leaves it at 8 s, logging window geometry to `compact-debug.log` in the data folder. |

A build with `-p:ProfilingBuild=true` is optimized and honors `NOCKMON_SKIP_ELEVATION=1`, so profilers can run it
non-elevated. Never ship it.
