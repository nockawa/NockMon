# NockMon code review

- **Date:** 2026-11-02
- **Commit:** `0167ca9` (branch `feature/folder-widget-process-menu`)
- **Scope:** the whole repo: `src/`, `samples/`, `tests/`, `.github/workflows`, `installer/`, `tools/`. This is the first review of the project.
- **Method:** 7 reviewers ran in parallel at high effort, one per area: sampling and native calls; ETW, GPU, history and folders; MCP and elevation; plugins, layouts and persistence; app shell; widgets and editors; build, release and tests. Their findings were deduplicated and re-ranked.
- **Spot-checked in the source while merging:** C1, H3, H5, M1, M2, M15, M17, M18, M24, L1.

**Legend**
- **Size:** S < 1 h · M 1–4 h · L > 4 h.
- **Kind:** **FACT** means provable from the code; **OPINION** means a judgement or an unconfirmed hypothesis; **SEC** means a security issue.
- **Severity:**
  - **Critical:** silent elevation of privilege.
  - **High:** an exploitable weakness, or a user locked out.
  - **Medium:** a crash, data loss, wrong data, or a real cost under plausible conditions.
  - **Low:** an edge case, polish, or defense in depth.

## Summary

| Severity | Count | Rough effort |
|---|---|---|
| Critical | 1 | 1–2 days |
| High | 5 | ~1 day |
| Medium | 36 | ~4–5 days |
| Low (bugs) | 55 | ~3–4 days |
| Low (performance / allocation) | 30 | ~3 days |
| Testability | 17 | ~4 days |
| Design | 18 | open-ended |

**Main theme:** the app always runs elevated, yet it reads code, configuration and data paths from locations that a non-admin process can write. It also exposes an IPC channel whose client side is not authenticated. Most of the serious findings come from that boundary (C1, H1–H3, M1–M3, several Lows).

**Suggested order:**
1. C1 and H1–H5. These are the elevation boundary, the release pipeline, and the compact-mode lock-out.
2. The crash and data-loss Mediums: M13, M15, M17–M21.
3. The rest, by area.

## Progress (2026-11-02, branch `feature/folder-widget-process-menu`)

| | Items |
|---|---|
| **Fixed** | C1, H1–H5; every Medium except M31; every Low except those below; P1, P3, P5–P9, P13, P14, P16; A1–A6, A8–A14; T8, T9 (pipe descriptor), T10, T15; D7, D11, D13, D16 (parallel tests, x64-only pull requests), D17. Tests were added with most fixes (272 in all). |
| **Disproved** | M11: a new elevated test shows bytes written through to disk are attributed to the writing process. Kept as a regression test. |
| **Accepted as is** | L45: the workspace-vs-screen offset in the off-monitor check is a taskbar's size. P2: the pool-tag sort runs once per scan, on a small row type. P11: the process events widget only formats when events arrive, and cells are assigned only when their text changes. A7: the tray tooltip string is still built each sample, but nothing reaches the shell unless it changed. |
| **Not done** | M31, L17 and L5: need a mixed-DPI setup, a live theme switch, and more than 64 logical processors. L22, L28 and L34 (M each). P4, P10, P12, P15. T1–T7, T11–T14, T16, T17. D1–D6, D8–D10, D12, D14, D15. |

**C1 / H1 decisions:**
- Data now lives in `%ProgramData%\NockMon\<user SID>`, with an admin-only ACL checked at every start.
- A folder planted there before NockMon first ran is moved aside, and the user is warned.
- The old `%LOCALAPPDATA%` data is copied over once, without following junctions or symbolic links.
- `NOCKMON_DATA_DIR` is ignored by elevated release builds.
- The dotnet tool no longer carries the app: it starts the installed one, found through the installer's HKLM entry.
- The single-file download is dropped.

The hot sampling path itself is in good shape. Its struct layouts match phnt and the SDKs, it allocates nothing in steady state, and it publishes snapshots without races (see [What was verified OK](#what-was-verified-ok)).

---

## Critical

| # | Finding | Location | Kind | Size |
|---|---|---|---|---|
| C1 | **The elevated process loads code and configuration from folders the user can write.** `%LOCALAPPDATA%\NockMon` (`Plugins\**`, `state.json`, the `settings.json` `DevPluginFolders` list) and `NOCKMON_DATA_DIR` (settable through HKCU\Environment) can all be changed at medium integrity. The next start then loads a planted DLL into the high-integrity process, with no UAC prompt: a silent, persistent elevation of privilege. The same root also redirects the elevated process's own writes (the fixed `*.tmp` in `WriteAtomically`, the plugin store, `crash.log`); with a junction, those become arbitrary file creation or deletion as admin. AI agents can drive these writes through `create_layout` / `rename_layout` / `delete_layout`. **Fix:** keep plugins, plugin state and dev folders under an admin-only DACL (ProgramData, or a protected ACL checked at each start); refuse reparse points; ignore `NOCKMON_DATA_DIR` in release builds. | `src/NockMon.App/Plugins/PluginRuntime.cs:39,68-98` · `src/NockMon.Core/Persistence/AppPaths.cs:6-9,41-46` · `src/NockMon.Core/Settings/AppSettings.cs:45` · `src/NockMon.App/App.xaml.cs:49` | SEC FACT | L |

## High

| # | Finding | Location | Kind | Size |
|---|---|---|---|---|
| H1 | **The elevated binaries run from folders the user can write.** The single-file build extracts to `%TEMP%\.net\NockMon\<id>`, and the host reuses that folder without checking it. The dotnet tool's launcher elevates `app\NockMon.exe` from `%USERPROFILE%\.dotnet\tools\.store`. Replacing a DLL or the exe in either folder gives code execution behind a genuine NockMon UAC prompt. `README.md:67-69` understates this. **Fix:** set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to an admin-only folder, or drop self-extraction; have the launcher copy the payload to an admin-only `%ProgramData%\NockMon\<ver>` (or verify a signature) before elevating. | `src/NockMon.App/NockMon.App.csproj:33-42` · `src/NockMon.Launcher/NockMon.Launcher.csproj:47-58` · `src/NockMon.Launcher/Program.cs:23,31-36` | SEC FACT | M |
| H2 | **The installer accepts a non-default folder.** It allows one through `DisableDirPage=auto`, `/DIR=` and `winget --location`. A folder like `C:\NockMon` inherits *Authenticated Users: Modify*, so the elevated exe can be replaced. The `addtopath` task then puts that writable folder on the machine PATH, where SYSTEM services search for DLLs. **Fix:** set `DisableDirPage=yes` and enforce `{autopf}`, or reset the ACL (`icacls /inheritance:r`, Admins/SYSTEM full, Users read and execute). | `installer/NockMon.iss:43-45,77-78` | SEC FACT | S |
| H3 | **The MCP relay talks to whoever owns the pipe name.** It does not check the server's owner (`PipeOptions.CurrentUserOnly`) and uses the default impersonation level. When NockMon is not running, or agent access is off, another local principal can create `\\.\pipe\NockMon.Mcp.<SID>` first. The relay, which the AI client starts automatically, then feeds the agent instructions, tools and results controlled by that impostor (prompt injection into an agent that has shell access). An impostor holding SeImpersonate can also impersonate the user. `docs/mcp.md:154-155` claims this cannot happen. **Fix:** put `O:{userSid}` in the SDDL; use `CurrentUserOnly` and `TokenImpersonationLevel.Identification` in both relays; optionally check the server process (`GetNamedPipeServerProcessId`). | `src/NockMon.App/Agents/McpRelay.cs:21` · `src/NockMon.Launcher/Program.cs:57` | SEC FACT | S |
| H4 | **The release job runs an unpinned download while holding all the release credentials.** `wingetcreate.exe` comes from `aka.ms/.../latest` with no hash check, and runs in the `publish` job. That job sets `id-token: write`, a `GH_TOKEN` with `contents: write` and `WINGET_TOKEN` at job level. A compromised download could replace release assets or mint a nuget.org key through OIDC. **Fix:** a separate winget job with no `id-token` and only `contents: read`; pin the version and its SHA256; give each step only the secret it needs. | `.github/workflows/release.yml:75-80,127-134` | SEC FACT | S |
| H5 | **An agent edit session started in compact mode locks the user out.** `AgentEditStarted` calls `SetEditing(true)`, but the Done/Cancel toolbar is collapsed in compact mode. Every way out of compact (Escape, double-tap, the tray menu) goes through `SetCompact`, which returns early when `IsEditing`. The only way out is to quit the app. **Fix:** block only `compact && IsEditing`, and leave compact mode in `AgentEditStarted`. | `src/NockMon.App/MainWindow.xaml.cs:317,340,346` · `src/NockMon.App/MainWindow.Agents.cs:135` | FACT | S |

## Medium

### Security

| # | Finding | Location | Kind | Size |
|---|---|---|---|---|
| M1 | **The pipe DACL gives the user GA.** GA includes `FILE_CREATE_PIPE_INSTANCE`, `WRITE_DAC` and `WRITE_OWNER`, and the mandatory label (NW at Medium) does not stop a Medium caller. Any medium process of the user can add server instances, stealing relay connections round-robin, or rewrite the DACL or label. The test at `AgentTests.cs:252-258` locks this string in. **Fix:** grant `0x12019b` instead. | `src/NockMon.Core/Agents/AgentPipeServer.cs:166` | SEC FACT | S |
| M2 | **`--plugin <folder>` crosses the elevation boundary.** `ElevationGate` and the launcher forward every argument. A medium process can run `NockMon.exe --plugin <its folder>`; the user sees a normal NockMon UAC prompt, and approving it loads the attacker's code at high integrity. **Fix:** whitelist the arguments that may cross elevation; accept `--plugin` only if the process was already elevated. | `src/NockMon.App/Shell/ElevationGate.cs:49-50` · `src/NockMon.Launcher/Program.cs:37-38` · `src/NockMon.App/App.xaml.cs:49` | SEC FACT | S |
| M3 | **Folder paths from agents are scanned with the elevated token.** This gives an existence oracle and the size history of folders protected by ACLs (other profiles, `System32\config`). Environment variables are expanded in the elevated environment. The scan starts while the edit is still pending, so Cancel does not stop it. **Fix:** check access against the linked (non-elevated) token, or impersonate the pipe client. | `src/NockMon.Core/Agents/LayoutAgent.cs:406-411` · `src/NockMon.App/MainWindow.xaml.cs:123` | SEC FACT | M |
| M4 | **Plugin feeds accept plain `http`, and packages are never verified** (no hash, no repository signature). A man-in-the-middle can swap code that will run elevated. **Fix:** HTTPS only; verify the repository signature or the catalog hash. | `src/NockMon.Core/Plugins/PluginFeeds.cs:38` · `src/NockMon.App/Plugins/PluginsDialog.cs:154` | SEC FACT | M |
| M5 | **The "verified by nuget.org" badge is trusted from any feed's JSON**, so a private or hostile feed can show it. **Fix:** honour it only when the feed is nuget.org. | `src/NockMon.Core/Plugins/PluginFeeds.cs:75` · `src/NockMon.App/Plugins/PluginsDialog.cs:264,311-313` | SEC FACT | S |
| M6 | **Dependency confusion in plugin updates.** Listings are merged by id across feeds and the highest version wins; the recorded `Source` is ignored, although `PluginStore.cs:9` says it is used for updates. A package squatting the id on nuget.org therefore "updates" a private plugin. **Fix:** take updates only from the recorded source. | `src/NockMon.App/Plugins/PluginsDialog.cs:246-253,449-452` | SEC FACT | S |
| M7 | **A release can come from any tagged commit.** Nothing checks that the commit is an ancestor of `main`, and no ruleset protects the tags. `v1.1.0` was cut from a commit that was not on main, and the tag was moved after a failed run. **Fix:** a `git merge-base --is-ancestor` step, plus a tag ruleset on `v*` and `claude-usage-v*` that blocks update and delete. | `.github/workflows/release.yml:8-9,65-67` | SEC FACT | S |
| M8 | **Actions are pinned by tag, not SHA.** This includes `NuGet/login@v1` in jobs with `id-token: write`. **Fix:** pin by SHA and let Dependabot (`github-actions`) update them. | `.github/workflows/release.yml:28,30,58,82,105` · `.github/workflows/claude-usage-plugin.yml:154,156,176,190,196` | SEC FACT | S |

### Bugs

| # | Finding | Location | Kind | Size |
|---|---|---|---|---|
| M9 | **The uninstaller force-kills the app (`taskkill /f`).** The ETW session `NockMon-Kernel` then survives until reboot, holding a system-logger slot and its kernel buffers (see M10). The command also kills the dotnet tool's copy of the app. **Fix:** close gracefully, wait, then run `logman stop NockMon-Kernel -ets`. | `installer/NockMon.iss:86` | FACT | S |
| M10 | **The ETW session sets no `BufferSizeMB`.** TraceEvent's default is max(64, 2×cores) MB, preallocated as minimum buffers: at least 64 MB of kernel memory for a "cheap" monitor, kept by an orphaned session. **Fix:** `BufferSizeMB = 8–16`, and watch `EventsLost`. | `src/NockMon.Core/Etw/EtwKernelMonitor.cs:76` | FACT | S |
| M11 | **Per-process disk bytes may all be attributed to pid −1.** TraceEvent maps the DiskIO pid through the thread map, and the `Thread` keyword is not enabled. Cached writes also land on System (lazy writer). No test checks per-process disk bytes. **Fix:** enable `Thread`, or report "unattributed"; add a write-through test that checks the test's own pid. | `src/NockMon.Core/Etw/EtwKernelMonitor.cs:77-80,141-143,156` | OPINION (needs a test) | S |
| M12 | **FolderScanner does I/O while holding `_gate`** (`Directory.Exists`, creating the FileSystemWatcher). The UI thread takes `_gate` in `SetWatched` / `Latest` / `NextScan`, so an unreachable network share freezes the UI for tens of seconds. **Fix:** do the I/O outside the lock and publish the result under it. | `src/NockMon.Core/Folders/FolderScanner.cs:93,98,188,220,230-241` | FACT | S |
| M13 | **A corrupt `state.json` crashes every start.** A bad id makes `SafeName` throw `InvalidDataException`, and `"plugins": null` causes an NRE. `Initialize` catches only IO and access errors, and `List()` has no guard. **Fix:** validate entries when reading and drop the bad ones. | `src/NockMon.Core/Plugins/PluginStore.cs:119-124,148` · `src/NockMon.App/Plugins/PluginRuntime.cs:82,90` | FACT | S |
| M14 | **Concurrent plugin installs race.** Each card can install in parallel; the read-modify-write of `state.json` and the shared `.tmp` collide, losing an entry and throwing an NRE in `Store.Find(...)!`. **Fix:** serialize store mutations. | `src/NockMon.App/Plugins/PluginsDialog.cs:290,337` · `src/NockMon.App/Plugins/PluginRuntime.cs:116,124` · `src/NockMon.Core/Plugins/PluginStore.cs:59-62` | FACT | S |
| M15 | **`App.UnhandledException` never sets `Handled`.** Any unguarded UI exception kills the app, including a plugin exception in its own event handler or dispatcher callback. This contradicts `docs/plugins.md` ("Errors are contained"). **Fix:** mark it handled when the faulting frames belong to the plugin load context (and fault that widget); offer a guarded `Post`. | `src/NockMon.App/App.xaml.cs:40-44` | FACT | M |
| M16 | **A plugin's `SaveSettings` raises `ConfigChanged` on the caller's thread.** Called from a background task, it saves the layout and touches XAML off the UI thread. **Fix:** marshal to the dispatcher, and document the threading. | `src/NockMon.App/Plugins/PluginWidgetView.cs:18,87` | FACT | S |
| M17 | **`Clipboard.SetContent` is not guarded.** It throws `CLIPBRD_E_CANT_OPEN` when another process holds the clipboard (clipboard managers, rdpclip), and M15 turns that into a crash. **Fix:** catch it and show a warning. | `src/NockMon.App/Widgets/ProcessMenu.cs:153-155` · `src/NockMon.App/Widgets/ProcessDetailsDialog.cs:99` | FACT | S |
| M18 | **`LayoutStore.Rename` deletes the old file before saving the new one**, so a failure or crash in between loses the layout. **Fix:** save first, then delete. | `src/NockMon.Core/Persistence/LayoutStore.cs:97-98` | FACT | S |
| M19 | **Layouts are not validated after deserializing.** Null widgets, items or series cause NREs later on the UI thread. Nested stacks, zero or negative spans, `GridRect.Right` int overflow (which breaks `Intersects`) and `CellSize <= 0` all get through. **Fix:** a validate-and-repair pass after loading. | `src/NockMon.Core/Persistence/LayoutStore.cs:17-25` · `src/NockMon.Core/Layout/LayoutDocument.cs:121-125,283` | FACT | M |
| M20 | **"Delete layout" can delete the wrong layout.** It confirms the current layout, then `_session.Delete()` deletes whatever is current after the await. Agent tools can switch or create a layout while the dialog is open, and the deletion cannot be undone. **Fix:** capture the name before the await and delete by name. | `src/NockMon.App/MainWindow.xaml.cs:825-829` | FACT | S |
| M21 | **`DeleteTab` removes by an index captured before the await.** If an agent changes the tabs during the dialog, the wrong tab and its widgets are deleted. **Fix:** look the page up again after the await. | `src/NockMon.App/MainWindow.xaml.cs:600-607` | FACT | S |
| M22 | **Double-clicking a folder widget also toggles compact mode.** Its `DoubleTapped` handler does not set `e.Handled`, so the board receives the event as well as Explorer opening. | `src/NockMon.App/Widgets/FolderWidget.cs:73` · `src/NockMon.App/MainWindow.xaml.cs:338` | FACT | S |
| M23 | **Window placement is lost at Windows shutdown or logoff.** It is saved only on `Closing` and on compact toggles, and `WM_ENDSESSION` is not handled. For a monitor that is left running, that is the normal way it exits. **Fix:** save in the existing subclass on `WM_ENDSESSION`, or debounce on `AppWindow.Changed`. | `src/NockMon.App/MainWindow.xaml.cs:229-237,302` · `src/NockMon.App/Shell/TrayIcon.cs:136` | FACT | S |
| M24 | **Stacked charts use the wrong timestamps for shorter series.** `stackBase[..s.Count]` aligns each series on its oldest sample, while `ComputeMax` aligns on the newest. Folder series are shorter, so they are stacked against the wrong timestamps and the axis max is wrong. **Fix:** `stackBase[^s.Count..]`. | `src/NockMon.App/Widgets/ChartRenderer.cs:56,206` | FACT | S |
| M25 | **"Copy command lines" can freeze the UI.** It runs the full `ProcessDetails.Query` (FileVersionInfo, token, an LSA lookup that can go to the network) per instance on the UI thread, with no cap; svchost ×80 freezes it. **Fix:** a `ReadCommandLine(key)` in Core, run in `Task.Run`. | `src/NockMon.App/Widgets/ProcessMenu.cs:140-141` | FACT | S |
| M26 | **The SDK writes the plugin manifest JSON by hand in MSBuild**, escaping only `\` and `"`. A newline in `Description` or a quote in the project URL produces invalid JSON, and the plugin fails to load. **Fix:** write the file with System.Text.Json from an inline task. | `src/NockMon.Sdk/build/NockMon.Sdk.targets:37-46` | FACT | S |
| M27 | **The SDK is pushed with `--skip-duplicate` on every release.** SDK changes made without a version bump are silently not published, so the nuget.org SDK diverges from the one shipped. **Fix:** fail CI when `src/NockMon.Sdk/**` changed but `<Version>` did not. | `tools/release/build.ps1:222` · `.github/workflows/release.yml:122` | FACT | S |
| M28 | **`AgentPipeServer.Stop` does not wait for sessions.** If a handler is hung (for example on a pending delete dialog) and the user turns access off then on, `FIRST_PIPE_INSTANCE` fails with a false "impersonating" error and the server stays down. **Fix:** track the sessions, then cancel and await them with a timeout. | `src/NockMon.Core/Agents/AgentPipeServer.cs:67-82,118,146-148` | FACT | M |
| M29 | **Sample plugin: one short news item disables a source.** `IndexOf(". ", 20)` throws on text shorter than 20 characters, and the source then returns null forever, retrying every 5 minutes. | `samples/ClaudeUsage.NockMon/NewsFeeds.cs:168` · `NewsService.cs:139` | FACT | S |
| M30 | **Sample plugin: switching pages re-fetches immediately.** Re-creating the last widget (page switch, edit) restarts the poller, which fetches at once and ignores `FetchedAt`. This hits the rate-limited endpoint (429). **Fix:** wait out the rest of the interval. | `samples/ClaudeUsage.NockMon/UsageService.cs:53-59,128-133,352-360` · `NewsService.cs:48-54` | FACT | S |
| M31 | **Restoring onto a monitor with a different DPI probably rescales the window.** The `WM_DPICHANGED` that follows is likely to change the restored size. The usual workaround is to call `SetWindowPlacement` twice. | `src/NockMon.App/Shell/WindowPlacement.cs:64-78` | OPINION | S |

### Performance and memory

| # | Finding | Location | Kind | Size |
|---|---|---|---|---|
| M32 | **`UpsampleScans` sums all `ScanCount` scans (1,800 at defaults) per matching name, whatever the length requested.** `ValueListWidget` reads one sample per item per tick, on the UI thread, under the engine lock, so this blocks the sampler. **Fix:** binary-search `_scanSampleIndex` and sum only the scans covered; cache pattern → names. | `src/NockMon.Core/History/HistoryStore.cs:401-418,441-458` · `src/NockMon.App/Widgets/ValueListWidget.cs:60` | FACT | M |
| M33 | **Each image name costs about 86 KB of history, kept for an hour after it exits.** That is 12 dense float rings × the scan capacity: about 20–25 MB for 250 names. Every uniquely named short-lived exe pays it. Retention has no upper bound: at 24 h it is about 2 MB per name, on the LOH. **Fix:** sparse or paged rings, or one contiguous array per name. | `src/NockMon.Core/History/HistoryStore.cs:46-50,259-260` · `src/NockMon.Core/Settings/AppSettings.cs:70` | FACT | M |
| M34 | **ChartRenderer's polygon cache grows without bound.** It keeps one array per exact point count and never frees any; it grows while history fills and on each resize or interval change, and it misses (and allocates) every frame while history fills. **Fix:** bucket the lengths, padding with the last vertex. | `src/NockMon.App/Widgets/ChartRenderer.cs:130-139,162-164` | FACT | S |
| M35 | **A minimized window still refreshes every sample.** `AppWindow.IsVisible` is true when minimized, so the full refresh and Win2D invalidation keep running. **Fix:** also skip when the presenter state is `Minimized`. | `src/NockMon.App/MainWindow.xaml.cs:157` | FACT | S |
| M36 | **SmallMultiples draws each cell separately, every frame.** Each cell builds 2 geometries and a path builder and makes 2 `DrawText(string)` calls; a 64-CPU grid is about 256 geometries and 128 text layouts per frame. **Fix:** one path builder for all cells, one fill and one stroke per layer. | `src/NockMon.App/Widgets/SmallMultiples.cs:53-62` | OPINION | M |

## Low: bugs and hardening

| # | Finding | Location | Kind | Size |
|---|---|---|---|---|
| L1 | `_objectTypesBuffer` is `readonly` and never nulled, so a second `Dispose` frees it twice. | `src/NockMon.Core/Sampling/NativeSystemProbe.cs:145,391` | FACT | S |
| L2 | `GetPerformanceInfo` is declared without `SetLastError`, so the error reported is stale. | `src/NockMon.Core/Native/NativeMethods.cs:38` · `NativeSystemProbe.cs:363` | FACT | S |
| L3 | `_samplesUntilScan` is written under `_lock` and read under `_sampleLock`, so the reset in `Configure` can be lost (the first scan is delayed). | `src/NockMon.Core/Sampling/SamplingEngine.cs:123,171-202` | FACT | S |
| L4 | Fixed 64 KB buffers with no growth loop: processor frequencies (many CPUs × P-states silently gives NaN) and object types. | `src/NockMon.Core/Native/ProcessorPerformanceCounters.cs:19,56` · `NativeSystemProbe.cs:118,143` | FACT | S |
| L5 | With more than 64 logical processors, the non-`Ex` frequency query may return only the current processor group. | `src/NockMon.Core/Native/ProcessorPerformanceCounters.cs:56` | OPINION | M |
| L6 | A `SampleFailed` handler that throws kills the sampler thread. | `src/NockMon.Core/Sampling/SamplingEngine.cs:264-267` | FACT | S |
| L7 | `SeDebugPrivilege` is enabled only on the first process action, so the set of watchable processes changes mid-session; processes that fail to open are dropped silently. **Fix:** enable it at startup. | `src/NockMon.Core/Sampling/WatchedProcessTracker.cs:85-87` · `src/NockMon.Core/Processes/ProcessControl.cs:35,193` | FACT | S |
| L8 | A grouped row's key is (first PID, minimum CreateTime), which is not a real key; the icon lookup works only because the list is in creation order, and a miss is cached as null. | `src/NockMon.Core/Sampling/ProcessQuery.cs:87-92` · `src/NockMon.App/Widgets/ProcessTableWidget.cs:76` | FACT | S |
| L9 | `Translate` and `GetVersionInfo` can throw other exceptions, and the discarded Details task swallows them: "Details…" silently does nothing. | `src/NockMon.Core/Processes/ProcessDetails.cs:45,115` · `src/NockMon.App/Widgets/ProcessMenu.cs:60` | FACT | S |
| L10 | `GetPriority` returns `(PriorityClass)0` on failure. | `src/NockMon.Core/Processes/ProcessControl.cs:111` | FACT | S |
| L11 | No upper clamp on `HistoryMinutes` or the intervals: a hand-edited settings file can cause OOM at startup. | `src/NockMon.Core/Settings/AppSettings.cs:68-71` | FACT | S |
| L12 | The guards miss Registry, Memory Compression and Secure System (PID > 4), and protected critical processes (`IsCritical` is null): End and Suspend stay enabled, and only "access denied" follows. | `src/NockMon.App/Widgets/ProcessMenu.cs:49-52` | FACT | S |
| L13 | End tree checks the critical flag on the root only, not on descendants. | `src/NockMon.App/Widgets/ProcessMenu.cs:241-263` | OPINION | S |
| L14 | Create dump is enabled for NockMon itself (in-process `MiniDumpWriteDump` can deadlock). | `src/NockMon.App/Widgets/ProcessMenu.cs:80` | OPINION | S |
| L15 | Open file location: the `Process` is not disposed and `Win32Exception` is not caught. | `src/NockMon.App/Widgets/ProcessMenu.cs:121` | FACT | S |
| L16 | The driver and process-events widgets leave status text in their cells after the status clears. | `src/NockMon.App/Widgets/DriverTableWidget.cs:51-73` · `ProcessEventsWidget.cs:49-58` | FACT | S |
| L17 | Theme brushes are fetched once, so they are stale after a Windows light/dark switch. | `src/NockMon.App/Widgets/CellTable.cs:41,52,194` · `StackWidget.cs:151,223` · `ValueListWidget.cs:30` | OPINION | S |
| L18 | Every `OperationCanceledException` is treated as "removed" without advancing `Due`, so an SMB abort makes the folder rescan in a loop. | `src/NockMon.Core/Folders/FolderScanner.cs:169-179` | FACT | S |
| L19 | A FileSystemWatcher callback after `Dispose` calls `Set` on a disposed event: a crash at exit. | `src/NockMon.Core/Folders/FolderScanner.cs:204,269-271` | FACT | S |
| L20 | `Status = null` is set after the ETW thread starts, so it can overwrite an immediate failure. | `src/NockMon.Core/Etw/EtwKernelMonitor.cs:82-84` | FACT | S |
| L21 | If an NVML export is missing, `nvmlShutdown` is not called and the library is not freed. | `src/NockMon.Core/Gpu/Nvml.cs:26-42,65` | FACT | S |
| L22 | NVML and D3DKMT devices are matched by name, so two identical GPUs can swap metrics. **Fix:** match on PCI bus. | `src/NockMon.Core/Gpu/GpuProbe.cs:45` | FACT | M |
| L23 | Reading PDH engine names has no `PDH_MORE_DATA` retry, and `Substring` fails on short names: engines become "Engine N" for the whole session. | `src/NockMon.Core/Gpu/D3dkmt.cs:135-143,161` | FACT | S |
| L24 | No `longPathAware`: `GetCompressedFileSizeW` fails beyond 260 characters and falls back to the logical size. | `src/NockMon.Core/Folders/FolderScan.cs:143` · `src/NockMon.App/app.manifest` | FACT | S |
| L25 | `FolderScope` equality is case-sensitive on the raw path, so `C:\Temp` and `c:\temp\` are two scans with two histories. | `src/NockMon.Core/Folders/FolderScope.cs:11` | FACT | S |
| L26 | An address past the last driver resolves to that driver (the image size is not checked). | `src/NockMon.Core/Etw/DriverMap.cs:46-49` | FACT | S |
| L27 | Size on disk counts NTFS-resident files as one cluster and counts hard links several times. | `src/NockMon.Core/Folders/FolderScan.cs:147` | FACT | S |
| L28 | Folder history is float32: a 1 TB folder only resolves about 64 KB of change. | `src/NockMon.App/Widgets/FolderWidget.cs:122` · `HistoryStore` | FACT | M |
| L29 | The `FolderScanner` field initializer starts a thread before `options.Validate()` runs, so the thread leaks when validation throws. | `src/NockMon.Core/Sampling/SamplingEngine.cs:55,61` | FACT | S |
| L30 | SEC: after an IOException, the only listening pipe instance is disposed before the next exists, so the name is briefly free to squat. | `src/NockMon.Core/Agents/AgentPipeServer.cs:113-116` | SEC FACT | S |
| L31 | SEC: pipe denial of service. Any `CreatePipe` failure stops the server permanently; the SDK's `ReadLineAsync` has no size limit (memory growth in the elevated process); the label is NW only, so Low-integrity processes can open it read-only. | `src/NockMon.Core/Agents/AgentPipeServer.cs:97-101,127,178` | SEC FACT | M |
| L32 | SEC: the client-chosen `ClientInfo` name is shown unescaped and unbounded in the elevated app's dialogs, which allows UI spoofing. | `src/NockMon.Core/Agents/LayoutTools.cs:36` · `LayoutAgent.cs:235` · `src/NockMon.App/MainWindow.Agents.cs:136` | SEC FACT | S |
| L33 | The agent delete confirmation resolves the name before the await; a rename plus a create during the dialog targets another layout. | `src/NockMon.Core/Agents/LayoutAgent.cs:233-237` | FACT | S |
| L34 | With over-the-shoulder elevation, the pipe name and DACL use the admin's SID, so the user's relay never connects. | `src/NockMon.Core/Agents/AgentPipeServer.cs:38` · `src/NockMon.App/Shell/ElevationGate.cs:43-50` | FACT | M |
| L35 | The elevated child inherits an arbitrary current directory (a fallback for DLL search). | `src/NockMon.App/Shell/ElevationGate.cs:47` · `src/NockMon.Launcher/Program.cs:35` | OPINION | S |
| L36 | SEC: a manifest assembly named `"C:x.dll"` passes the check and is loaded from outside the plugin folder. | `src/NockMon.Core/Plugins/PluginManifest.cs:37` · `src/NockMon.App/Plugins/PluginRuntime.cs:187-188` | SEC FACT | S |
| L37 | SEC: the id and version inside the nupkg are not compared with the listing, so a package can install under another plugin's id. | `src/NockMon.App/Plugins/PluginRuntime.cs:115-116` · `src/NockMon.Core/Plugins/PluginStore.cs:39-61` | SEC FACT | S |
| L38 | Ids and versions with trailing dots or spaces map to the same folder; version `"..."` resolves to the plugin folder itself, which `Directory.Delete` then wipes. | `src/NockMon.Core/Plugins/PluginStore.cs:54,116,119-124` | FACT | S |
| L39 | No flush before the rename in `WriteAtomically`, so a power loss can leave an empty file (settings silently reset). | `src/NockMon.Core/Persistence/AppPaths.cs:45` | FACT | S |
| L40 | `SettingsStore` catches only `JsonException`: an IO error (antivirus or sync lock) crashes startup. | `src/NockMon.Core/Persistence/SettingsStore.cs:18` | FACT | S |
| L41 | In Scroll mode the columns are `int.MaxValue`, so new widgets are always placed on row 0, moving right forever. | `src/NockMon.Core/Layout/BoardGeometry.cs:69-76` · `src/NockMon.App/Board/BoardView.xaml.cs:151-153` | FACT | S |
| L42 | The SDK's drop pattern and its pack exclusions disagree (Win2D), so the dev folder and the package differ. | `src/NockMon.Sdk/build/NockMon.Sdk.targets:19,54` | FACT | S |
| L43 | Sample: static `_history` is mutated without a lock; the news loop dies on `UnauthorizedAccess` and never restarts. | `samples/ClaudeUsage.NockMon/UsageService.cs:202-210,300` · `NewsService.cs:198` | FACT | S |
| L44 | SEC sample: links of any URI scheme from fetched markdown are launched by the elevated process. **Fix:** https only. | `samples/ClaudeUsage.NockMon/NewsBell.cs:76` | SEC FACT | S |
| L45 | `MonitorFromRect` is given workspace coordinates, so the check is offset by a top or left taskbar. | `src/NockMon.App/Shell/WindowPlacement.cs:69` | FACT | S |
| L46 | The tray icon is sized from the window's DPI, not the taskbar's. | `src/NockMon.App/Shell/BarIcon.cs:16` | FACT | S |
| L47 | `_monitor.Updated` is never unsubscribed, and the tray is disposed before the engine stops: a late tick leaks an HICON. | `src/NockMon.App/MainWindow.xaml.cs:39,60` · `src/NockMon.App/App.xaml.cs:52` | OPINION | S |
| L48 | The async void dialog handlers do not catch the "one ContentDialog at a time" exception, and agent confirmations can collide with them. **Fix:** one shared guarded helper. | `src/NockMon.App/MainWindow.xaml.cs:424,573,584,592,600,804-829` | OPINION | S |
| L49 | The `CreateDIBSection` result is not checked, and the `CreateBitmap` mask content is undefined. | `src/NockMon.App/Shell/BarIcon.cs:30-34` | FACT | S |
| L50 | The window icon's HICON is never destroyed and not regenerated when the DPI changes. | `src/NockMon.App/MainWindow.xaml.cs:55-56` | FACT | S |
| L51 | SEC: the version output is pasted into `run:` (script injection, low impact). **Fix:** pass it through `env:`. | `.github/workflows/release.yml:56` · `claude-usage-plugin.yml:174` | SEC FACT | S |
| L52 | Inno Setup is not pinned, and the `.iss` is UTF-8 without a BOM, which needs ISCC 6.3 or later ("Loïc" garbles otherwise). | `tools/release/build.ps1:184-193` | FACT | S |
| L53 | `docs/building.md` is out of date: the release environment's tag policy and the second trusted-publishing policy are missing. | `docs/building.md:64-66` | FACT | S |
| L54 | The launcher version is still 1.0.0 after 1.1.0, so PR artifacts are mislabelled. | `src/NockMon.Launcher/NockMon.Launcher.csproj:23` | FACT | S |
| L55 | On a single-process row, "Chart" charts every process with the same name, but the menu label does not say so. | `src/NockMon.App/Widgets/ProcessMenu.cs:66` | OPINION | S |

## Low: performance and allocation

| # | Finding | Location | Size |
|---|---|---|---|
| P1 | Each table sorts about 600 large `ProcessRow` structs by value per scan, calling `GetValue` twice per compare, under the engine lock. **Fix:** sort an array of keys plus an array of indices, or select the top N. | `src/NockMon.Core/Sampling/ProcessQuery.cs:70,123` | M |
| P2 | The same pattern sorts about 3,000 pool tags under the lock. | `src/NockMon.Core/Sampling/PoolTagQuery.cs:24` | S |
| P3 | Every scan globs every unwatched process against every pattern. **Fix:** a negative cache by `ProcessKey`. | `src/NockMon.Core/Sampling/WatchedProcessTracker.cs:83-85` | S |
| P4 | MCP queries read from the window start to the newest sample, and sort or run LINQ inside `engine.Read`, which blocks the sampler. | `src/NockMon.Core/Agents/MonitorQueries.cs:91-94,165-169,237-248` | M |
| P5 | `ListLayouts` parses every layout twice, on the UI thread. | `src/NockMon.Core/Agents/LayoutAgent.cs:61-65` · `src/NockMon.Core/Persistence/LayoutStore.cs:93-97` | S |
| P6 | A static initializer probes every PATH folder on the UI thread. | `src/NockMon.App/MainWindow.Agents.cs:16` | S |
| P7 | The tray creates 4 GDI objects and makes a synchronous `Shell_NotifyIcon` call every sample, even when nothing changed. | `src/NockMon.App/MainWindow.xaml.cs:211-227` · `src/NockMon.App/Shell/TrayIcon.cs:74-83` | S |
| P8 | In edit mode, every `SizeChanged` rebuilds the grid geometry (up to 20,000 rectangles). | `src/NockMon.App/Board/BoardView.xaml.cs:209-233` | S |
| P9 | FileSystemWatcher reports both Size and LastWrite, and does not filter include/exclude, so `.git` and `node_modules` churn triggers rescans. | `src/NockMon.Core/Folders/FolderScanner.cs:233-240` | S |
| P10 | Each DPC/ISR event hashes the driver name case-insensitively (up to ~100k events/s). **Fix:** use a driver int id. | `src/NockMon.Core/Etw/EtwKernelMonitor.cs:186,192,215-223` | S |
| P11 | The process events widget copies the event ring and re-formats every row each sample. | `src/NockMon.App/Widgets/ProcessEventsWidget.cs:55-77` | M |
| P12 | `CellTable` is not virtualized: up to 500 rows are formatted per scan. | `src/NockMon.App/Widgets/CellTable.cs` · `ProcessTableWidget.cs:70-78` | M |
| P13 | End tree runs one breadth-first search per instance, under the engine lock. **Fix:** build a parent→children map once. | `src/NockMon.App/Widgets/ProcessMenu.cs:241-252` | S |
| P14 | `Directory.Exists` runs on the UI thread, which blocks for seconds on an offline UNC path. | `src/NockMon.App/Editing/FolderScopeEditor.cs:53,67` · `FolderWidget.cs:180` | S |
| P15 | Icons are extracted on the UI thread the first time each exe appears. | `src/NockMon.App/Widgets/ProcessIcons.cs:35` | M |
| P16 | Plugins: a new `NuGetFeed` per search loses the service index; `RestartRequired` parses `state.json` per call; `FolderFeed` opens every nupkg per search; `Find` is a linear scan. | `src/NockMon.App/Plugins/PluginsDialog.cs:233` · `PluginRuntime.cs:52,101` · `src/NockMon.Core/Plugins/PluginFeeds.cs:157-172` | S |
| A1 | History: a boxed `foreach` enumerator per sample; `new int[scans]` on every read (always above 1024 at defaults); `_patterns` is never pruned. | `src/NockMon.Core/History/HistoryStore.cs:35,122,163,349` | S |
| A2 | The GPU probe allocates a new `Dictionary` per scan and a new `Adapters` array per call. | `src/NockMon.Core/Gpu/GpuProbe.cs:55,87` | S |
| A3 | `DriverMap` is rebuilt every minute even when nothing changed. | `src/NockMon.Core/Etw/DriverMap.cs:18-40` | S |
| A4 | `ObjectTypeSeries.Info` builds a string on every call, and is read on every render. | `src/NockMon.Core/Metrics/SeriesRef.cs:62` · `src/NockMon.App/Widgets/ChartRenderer.cs:40` · `CpuGridWidget.cs:88` | S |
| A5 | `MetricFormatter` allocates 2 strings per formatted value. | `src/NockMon.Core/Metrics/MetricFormatter.cs:20-31,52` | S |
| A6 | Each sample allocates a lambda and a WinRT `DispatcherQueueHandler`. | `src/NockMon.App/Monitoring/MonitorService.cs:60` | S |
| A7 | The tray tooltip strings are rebuilt every sample, even when hidden. | `src/NockMon.App/MainWindow.xaml.cs:224-225` | S |
| A8 | A stack's auto-cycle rebuilds its tooltip tree, and the timer runs while hidden in the tray. | `src/NockMon.App/Board/WidgetHost.xaml.cs:81-87` | S |
| A9 | Per-frame chart allocations: an interface enumerator per sample, a `ChartStyle` record, a text layout per legend entry, closures. | `src/NockMon.App/Widgets/ChartRenderer.cs:204,264` · `ChartWidget.cs:46` · `CpuGridWidget.cs:77` · `GpuEnginesWidget.cs:49` | S |
| A10 | `FolderWidget` rebuilds its tooltip and Largest list every sample. | `src/NockMon.App/Widgets/FolderWidget.cs:111-112` | S |
| A11 | `ProcessIcons` calls `ToLowerInvariant` per row per scan, and its icon dictionary has no bound. | `src/NockMon.App/Widgets/ProcessIcons.cs:33` | S |
| A12 | A plugin package is held about 3 times in memory with no size cap (memory DoS by a hostile feed). | `src/NockMon.Core/Plugins/PluginFeeds.cs:86` · `PluginPackage.cs:30-32` | S |
| A13 | `MonitorService.IsUiActive` is dead code. | `src/NockMon.App/Monitoring/MonitorService.cs:30` | S |
| A14 | `EtwTests` leaves a 1 MB `%TEMP%\nockmon-etw.bin` behind. | `tests/NockMon.Core.Tests/EtwTests.cs:165` | S |

## Testability gaps

| # | Gap | Location | Size |
|---|---|---|---|
| T1 | **Pure logic sits in the untested App project.** It should move to Core with tests: <br>• chart math (`TopPoints`, `ComputeMax`, `FitRange`, stack alignment; a test would have caught M24) <br>• the process-menu guard rules, End tree plan and group targets <br>• the FolderWidget and ProcessIcons helpers <br>• the WidgetEditor validators and TabRail reordering <br>• tab wrap-around, the WindowPlacement mapping and tray normalization <br>• the "find a free spot" logic, duplicated 5 times (`MainWindow.xaml.cs:480,724,739,756,782`) | `src/NockMon.App/**` | L |
| T2 | The ETW accumulate and harvest logic is reachable only through a live elevated session. **Fix:** extract a `KernelEventAccumulator` and test it with synthetic events. | `src/NockMon.Core/Etw/EtwKernelMonitor.cs:145-270` | S |
| T3 | The `SamplingEngine` thread is untested: resync after a stall, `SampleFailed`, `Configure` racing `SampleOnce`, `Read` running during sampling. | `src/NockMon.Core/Sampling/SamplingEngine.cs:136-155,259-279` | M |
| T4 | `FolderScanner` is hard-wired to `Stopwatch`, its own thread and a real watcher, so tests sleep. **Fix:** inject a `TimeProvider` and add a `RunOnce`. | `src/NockMon.Core/Folders/FolderScanner.cs` · `tests/.../FolderTests.cs:199` | M |
| T5 | `PluginRuntime` is static, so load, ALC resolution and `Initialize` ordering are untested. Also untested: a corrupt `state.json`, hostile layout JSON, `Rename` crash safety, concurrent installs, the SDK manifest output. | `src/NockMon.App/Plugins/PluginRuntime.cs:34-63` | M |
| T6 | The sample plugin has no tests, although its parsers are pure functions (a test would have caught M29). | `samples/ClaudeUsage.NockMon` | S |
| T7 | `KernelSession_DeliversDisk…` asserts nothing about disk events, and fixed sleeps add 4 s to every run. | `tests/NockMon.Core.Tests/EtwTests.cs:151,162,166` | S |
| T8 | Likely flaky: a < 3 ms per `CollectSystem` assertion inside the parallel test pool, and a ±5 % memory tolerance. | `tests/NockMon.Core.Tests/KernelMetricsTests.cs:163,185-194` | S |
| T9 | Pipe security: the test locks in GA (M1). Nothing tests that another process cannot add an instance, or that the relay refuses a server with a foreign owner. The relay (duplicated in two places) and the Stop/Start path (M28) are untested. | `tests/NockMon.Core.Tests/AgentTests.cs:252-258` | M |
| T10 | Struct size assertions are missing for SystemPoolTag (40), MemoryList (176), ProcessMemoryCountersEx (80), PerformanceInformation (104), IoCounters (48) and TokenPrivileges (16); D3DKMT and NVML use magic offsets. | `tests/NockMon.Core.Tests/NativeSystemProbeTests.cs:19-28` | S |
| T11 | Buffer parsing is mixed in with the syscalls. **Fix:** extract `Parse(ReadOnlySpan<byte>)` functions and test them with synthetic buffers. | `src/NockMon.Core/Sampling/NativeSystemProbe.cs:115-183,265-274` · `ProcessorPerformanceCounters.cs:59-80` | M |
| T12 | No steady-state allocation test for `SampleOnce`, `ProcessScan` or `WatchedProcessTracker.Tick`. | `tests/NockMon.Core.Tests` | S |
| T13 | The fake probe uses the PID as the handle, so it cannot model PID reuse; the tracker has no PID-reuse test. | `tests/NockMon.Core.Tests/Fakes.cs:91` | M |
| T14 | Global state leaks between tests: `CurrentCulture` is not restored, and `PluginCatalog` registrations are not removed. **Fix:** set `"culture": "invariant"` in `xunit.runner.json`. | `NamePatternAndFormatterTests.cs:32` · `ObjectTypeAndGpuTests.cs:205` · `PluginTests.cs:258` | S |
| T15 | The SDK API version lives in 3 places with no test that they match; make it one MSBuild property. | `src/NockMon.Sdk/NockMon.Sdk.csproj:20` · `build/NockMon.Sdk.props:6` · `WidgetPlugin.cs:11` | S |
| T16 | No installer or package smoke test in CI: silent install, check ACLs and PATH, uninstall, check that the process, PATH entry and ETW session are gone. | `.github/workflows/release.yml` | M |
| T17 | `MonitorService`'s coalescing cannot be tested because it uses `DispatcherQueue` directly. **Fix:** inject the enqueue function. | `src/NockMon.App/Monitoring/MonitorService.cs:56-68` | S |

## Design (medium / long term)

| # | Recommendation | Location | Size |
|---|---|---|---|
| D1 | **Shrink what runs elevated.** Host the MCP server in a non-elevated broker that only receives snapshots; that removes the H3 / M1 / M3 class by design. In the long run, consider plugins out of process (hard with WinUI). Meanwhile, document that settings written by agents reach elevated plugin code as untrusted input. | Architecture | L |
| D2 | **Authenticode signing.** An always-elevated app shows "Unknown publisher" in the UAC prompt, so users cannot spot a tampered binary. Already discussed and deferred for cost; `actions/attest-build-provenance` is a free partial step. | Release | M |
| D3 | **`MainWindow` is a god class** (856 + 162 lines: tray, compact, placement, tabs, layouts, widget commands, agents). Extract `WindowChrome` and `BoardCommands`. | `src/NockMon.App/MainWindow.xaml.cs` | L |
| D4 | **`LayoutSession` has no version or identity check.** Agent changes interleave with the user's pending dialogs (M20, M21, L33), and `BoardView.ReplaceWidget` silently drops an edit when `IndexOf` returns −1. Add a `LayoutSession.Version` that handlers check again after each await. | `src/NockMon.Core/Layout/LayoutSession.cs` · `src/NockMon.App/Board/BoardView.xaml.cs:92-94` | M |
| D5 | **Widget lifetime depends on XAML `Unloaded`.** `Rebuild` does not dispose widgets, and `Unloaded` is not guaranteed at window close, so a plugin's `Destroy` can be skipped. Make `IMonitorWidget` `IDisposable`. | `src/NockMon.App/Board/BoardView.xaml.cs:130-132` | M |
| D6 | **Widgets talk to the window through static events** (`ProcessMenu.ChartRequested` / `MessageRaised`, `PluginWidgetView.OpenPluginsRequested`). Pass a command sink through `WidgetContentFactory` instead. | `src/NockMon.App/MainWindow.xaml.cs:64-66` | M |
| D7 | **Plugins share the host's whole TPA**, including private dependencies (NuGet.Versioning, MCP, TraceEvent). Plugins silently bind to the host's versions, which contradicts `docs/plugins.md`, and a host dependency bump can break them. Use an allowlist: SDK, WinUI, WinRT, Win2D and the shared framework. | `src/NockMon.App/Plugins/PluginRuntime.cs:283-287` | M |
| D8 | **The ETW keyword set is fixed at start**, so DPC and ISR, the most expensive flags, cost even when no widget uses them. Derive the flags from the layout. | `src/NockMon.Core/Etw/EtwKernelMonitor.cs:77-80` | L |
| D9 | **`HistoryStore` has 7 ring families** with separate lifetime rules, and a retention change wipes all history. Extract a generic ring and series type before the next source is added. | `src/NockMon.Core/History/HistoryStore.cs` · `SamplingEngine.cs:122` | L |
| D10 | **Native buffer handling is inconsistent** (some buffers grow, some are fixed) and mixes magic offsets with typed structs. Use one `QueryGrowing` helper and explicit structs with size asserts. | `src/NockMon.Core/Sampling` · `src/NockMon.Core/Native` | M |
| D11 | **`Snapshot.Processes` exposes the whole backing array**, so callers can read past `Count`. Expose only `ProcessSpan`. | `src/NockMon.Core/Sampling/Snapshot.cs:377` | S |
| D12 | **`SampleOnce` mixes two locks** whose field ownership is implicit. Document the rule, or split the method. | `src/NockMon.Core/Sampling/SamplingEngine.cs:157-243` | S |
| D13 | **The layouts are x64-only** in an AnyCPU Core library, guarded only by a test. Add a 64-bit check in the probe constructor. | `src/NockMon.Core/Native/NativeMethods.cs:207` | S |
| D14 | **P/Invokes are duplicated** (`OpenProcess`, `GetPriorityClass`), and `ProcessDetails` reopens the process by PID instead of using the handle it already checked. | `src/NockMon.Core/Processes/*` | S |
| D15 | **The launcher and the app duplicate** the pipe name, mutex name, show message and relay code. Share one linked source file. | `src/NockMon.Launcher/Program.cs` · `src/NockMon.App/Agents/McpRelay.cs` | S |
| D16 | **CI takes about 6 minutes.** The app is published 6 times, `dotnet clean` runs around each single-file publish, tests run serially, and there is no NuGet cache. Use a matrix per architecture, a parallel test job, lock files with `setup-dotnet` cache, and x64 only on PRs. | `tools/release/build.ps1:197-223` · `.github/workflows/release.yml:51-56` | M |
| D17 | **The sample plugin builds against the nuget.org SDK 1.0.0**, so SDK changes are never checked against a real plugin. Add a CI step that packs the SDK locally and builds the sample against it. | `samples/ClaudeUsage.NockMon/ClaudeUsage.NockMon.csproj` | S |
| D18 | **`ProcessMenu`'s guards and the End-tree plan should be a Core "process action plan"**, so they can be tested (see T1) and reused by a future MCP process tool. | `src/NockMon.App/Widgets/ProcessMenu.cs:48-52,92-97,239-261` | M |

## What was verified OK

The reviewers looked at these and found no issue:
- **Native layouts:** `SYSTEM_PROCESS_INFORMATION` (256), the thread record (80, ThreadState at +68), `OBJECT_TYPE_INFORMATION` (104), `SYSTEM_POOLTAG` (40), every `SYSTEM_PERFORMANCE_INFORMATION` offset, and the D3DKMT and NVML offsets all match phnt and the SDKs.
- **Sampling:** the front, back and spare snapshot tables rotate without races, including on back-to-back scans. Rates are computed outside the lock and swapped in under it. The probe, `SampleProcessor`, `Tick` and the queries allocate nothing in steady state. 32-bit counters wrap correctly, divisions by zero are guarded, and the schedule never drifts and resyncs after a stall.
- **PID reuse:** `ProcessKey(Pid, CreateTime)` is re-checked after every `OpenProcess`, so a stale menu can never act on the wrong process.
- **Privileges and dumps:** `EnablePrivilege` checks `ERROR_NOT_ALL_ASSIGNED`, and `MiniDumpWriteDump` is serialized with partial files deleted.
- **Tray and window:** the subclass uses an `[UnmanagedCallersOnly]` pointer plus a `GCHandle` (no delegate can be collected while native code holds it); UIPI filters are in place; the icon is re-added when Explorer restarts; shutdown order is correct; off-monitor placements are rejected.
- **MCP:** a protected DACL that denies network logons, `PIPE_REJECT_REMOTE_CLIENTS`, `FIRST_PIPE_INSTANCE`, and one pipe per user SID. Arguments go through `ArgumentList` (no command injection). The relay never starts the app, and agents never commit.
- **Plugins:** the zip-slip guard is tested; installs extract to `.tmp` and then rename; the load context is non-collectible (no unload leaks); XML loading prohibits DTDs; the API version is checked at install and at load.
- **Release:** top-level `contents: read`; publishing gated by an environment with a required reviewer; nuget.org via OIDC; the tag version is checked against a regex.
- **ETW:** the session name is private, a stale session is stopped at start, and the drivers snapshot is swapped immutably.
