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

## Releasing

NockMon ships as an Inno Setup installer (GitHub release and winget), plus the `nockmon` command line as a dotnet tool
(nuget.org), which starts the installed app and relays MCP. NockMon runs as administrator, so the app itself only ships
where non-administrators cannot modify it (Program Files): no single-file, portable or dotnet tool copy of the app, which
would run from a folder the user's non-elevated processes can write. A release is a tag on a commit of `main`:

```powershell
git tag v1.2.0      # v<major>.<minor>.<patch>, or v1.2.0-beta.1 for a prerelease
git push origin v1.2.0
```

The [Release workflow](../.github/workflows/release.yml) then tests, builds every artifact with the tag's version, and,
once approved on the `release` environment, publishes:

1. **GitHub release** `v<version>`: the installers and `SHA256SUMS.txt`, with generated notes.
2. **nuget.org**, through [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (no
   stored API key): `NockMon` (the tool); `NockMon.Sdk` only when its version is new.
3. **winget**, in a job of its own (a second approval): a pull request to
   [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) (stable versions, when the `WINGET_TOKEN` secret is
   set), merged by its moderators. `wingetcreate` is pinned by version and SHA-256, and runs without the release's write
   token or OIDC.

A tag on a commit that is not on `main` fails the build. Actions are pinned by commit hash.

Pull requests to `main` and manual runs build and test the same artifacts (downloadable from the run) and publish
nothing. Every publishing step can be re-run.

One-time setup:

| Where | What |
|---|---|
| nuget.org, your name → **Trusted Publishing** | A policy: repository owner `nockawa`, repository `NockMon`, workflow file `release.yml`, environment `release`. |
| nuget.org, same page | A second policy for the sample plugin: workflow file `claude-usage-plugin.yml`, environment `release`. |
| GitHub, **Settings → Environments** | Environment `release`, with yourself as required reviewer and deployments limited to tags `v*` and `claude-usage-v*`. |
| GitHub, **Settings → Rules → Rulesets** | A tag ruleset on `v*` and `claude-usage-v*` restricting updates and deletions: a published tag cannot be moved. |
| GitHub, environment `release` → secrets | `WINGET_TOKEN`: a classic personal access token with the `public_repo` scope (wingetcreate forks winget-pkgs and opens the pull request with it). |

To build locally, the same script the workflow runs:

```powershell
winget install JRSoftware.InnoSetup           # once: the installer compiler
pwsh tools/release/build.ps1 -Version 1.2.0   # -Architectures x64 / -SkipInstaller / -SkipTool
```

Without `-Version`, the version of `src/NockMon.Launcher/NockMon.Launcher.csproj`. The script writes to
`artifacts/release/<version>/`:

| Output | What |
|---|---|
| `NockMon-<version>-<arch>-setup.exe` | Installer of the self-contained app (`installer/NockMon.iss`): per machine in Program Files only (another folder is refused), Start menu shortcut, optional desktop shortcut and PATH entry, closes a running NockMon on upgrade; on uninstall asks it to exit (`NockMon.exe --exit`) so its kernel trace session stops. |
| `nuget/` | The dotnet tool `NockMon`: a small `net10.0` command line that starts the installed app elevated (found through the installer's HKLM entry), brings an existing instance to the front, and runs the MCP relay. Plus the plugin SDK package. |
| `SHA256SUMS.txt` | Hashes of the installers. |
| `winget/manifests/` | The winget manifests (installer URLs of the GitHub release, hashes). |

Try a local tool package before publishing (it starts the installed NockMon):

```powershell
dotnet tool install -g NockMon --add-source artifacts/release/<version>/nuget --version <version>
```

The application icon (`src/NockMon.App/Assets/NockMon.ico`, and `docs/images/icon.png`) is generated by
`python tools/icon/make_icon.py` (Pillow), in the style of the live taskbar icon.

**The plugin SDK** has its own version, in `src/NockMon.Sdk/NockMon.Sdk.csproj`: bump it to publish it with the next
release. Its major.minor is the plugin API version (`PluginApi.Version` and `NockMonPluginApiVersion` in
`build/NockMon.Sdk.props`): keep them in sync.

## Repository layout

| Path | Contents |
|---|---|
| `src/NockMon.App` | The WinUI 3 application: window, board, widgets (Win2D rendering), editors, tray, plugins runtime, AI agents host. |
| `src/NockMon.Core` | Everything testable without UI: sampling (native APIs, kernel ETW session, GPU through D3DKMT and NVML), history, metrics, layouts and their persistence, plugin store and feeds, built-in documentation, MCP tools and server. |
| `src/NockMon.Sdk` | The plugin SDK: interfaces and the MSBuild files that make a project a plugin. |
| `src/NockMon.Launcher` | The `nockmon` dotnet tool: starts the installed app, and relays MCP over stdio (the app relays too, for installs without the tool: `NockMon.exe mcp`). |
| `tests/NockMon.Core.Tests` | Unit and integration tests of the core. |
| `samples` | Example plugins (public domain, not in the solution): `ClaudeUsage.NockMon`, published on nuget.org by its own workflow. |
| `.github/workflows` | The release workflow (tags `v*` publish, pull requests build and test) and the sample plugin's (tags `claude-usage-v*`). |
| `installer` | The Inno Setup script. |
| `tools/release` | The release build script. |
| `tools/icon` | The application icon generator. |
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
| `NOCKMON_DATA_DIR` | Uses another data folder instead of `%ProgramData%\NockMon\<user SID>` (tests, profiling, alternative profiles). Honored by Debug builds and non-elevated processes only: any non-elevated process could set it to redirect an elevated release build. |
| `NOCKMON_DEBUG_COMPACT=1` | Enters compact mode 4 s after start and leaves it at 8 s, logging window geometry to `compact-debug.log` in the data folder. |

A build with `-p:ProfilingBuild=true` is optimized and honors `NOCKMON_SKIP_ELEVATION=1`, so profilers can run it
non-elevated. Never ship it.
