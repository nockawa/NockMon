# Service Status plugin: assessment

Status: proposal. Written 2026-11-03. **[F]** = verified (code read, or endpoint fetched today with curl), **[O]** = opinion.
**[U]** = from documentation or memory, not re-verified.

## 1. Data sources

### Endpoints (all fetched 2026-11-03 unless marked)

| Protocol | Endpoint | Shape | Validators | Examples |
|---|---|---|---|---|
| Atlassian Statuspage | `{page}/api/v2/summary.json` (also `status.json`, `components.json`, `incidents/unresolved.json`, `scheduled-maintenances/upcoming.json`, `history.atom`) | `page{id,name,url,updated_at}`, `status{indicator,description}`, `components[]{id,name,status,group,group_id,showcase}`, `incidents[]{name,status,impact,started_at,shortlink,incident_updates[]{body}}`, `scheduled_maintenances[]` | weak ETag, `max-age=10`; **304 works** [F] | Claude, GitHub, Cloudflare, npm, Discord, Reddit, DigitalOcean, Vercel, Twilio, Zoom (`zoomstatus.com`), Netlify, Figma, CircleCI, Sentry, MongoDB, Render, Atlassian |
| incident.io | `{page}/api/v1/summary` (documented "widget API") | `page_title, page_url, ongoing_incidents[]{name,status,url,last_update_at,last_update_message,current_worst_impact,affected_components[]{id,name,group_name,current_status}}, in_progress_maintenances[], scheduled_maintenances[]` | none; CDN caches 30 s (docs) | OpenAI, Linear, Supabase, HashiCorp, Perplexity |
| incident.io compat | `{page}/api/v2/summary.json` | Statuspage shape **without `incidents`** (OpenAI, Linear: `page,status,components`; Docker, HashiCorp add `scheduled_maintenances`). `/api/v2/incidents.json` exists (OpenAI: 36 KB, resolved included) | none | Docker (`/api/v1/summary` → 404: widget API disabled, the documented behavior) |
| status.io | `https://status.io/1.0/status/{pageId}` (id in the page's HTML) | `result{status_overall{status,status_code}, status[]{name,status_code,containers[]}, incidents[]{name,datetime_open,current_active,messages[]}, maintenance{active,upcoming}}` | none seen | GitLab (`5b36dc6502d06804c08349f7`) |
| Slack (custom) | `https://slack-status.com/api/v2.0.0/current` (`status.slack.com` 301s there) | `{status, date_updated, active_incidents[]{…, services[]}}`; status `ok` / `active` / … | `max-age=20` | Slack |
| Google (custom) | `https://status.cloud.google.com/incidents.json`; Workspace: `https://www.google.com/appsstatus/dashboard/incidents.json` | array of `{id, begin, end?, modified, external_desc, severity, status_impact, affected_products[], currently_affected_locations[], most_recent_update{text}}`; open = no `end` | Last-Modified, **304 works** [F]; 251 KB raw / 44 KB gzip | GCP, Workspace (307 KB) |
| AWS (custom, unofficial) | `https://health.aws.amazon.com/public/currentevents` | JSON array **in UTF-16 with BOM** (`charset=utf-16`): `{service, service_name, region_name, status:"0".."3", summary, event_log[], impacted_services{}}` | none; gzip 23 KB | AWS |
| AWS RSS | `https://status.aws.amazon.com/rss/all.rss` | RSS 2.0, one item per update; no machine severity, no "resolved" flag | ETag (If-None-Match gave 200: unclear) | AWS |
| Azure RSS | `https://azure.status.microsoft/en-us/status/feed/` | RSS 2.0; **empty channel when no widespread incident** (today: 577 bytes); no severity | Last-Modified, but If-Modified-Since → 200 | Azure |
| Heroku | `https://status.heroku.com/api/v4/current-status` | `status[]{system,status:green/yellow/red}, incidents[], scheduled[]` | ETag, `max-age=300` | Heroku |
| Azure DevOps | `https://status.dev.azure.com/_apis/status/health?api-version=7.1-preview.1` | `status{health,message}, services[]{id, geographies[]{id,name,health}}` | `no-store` | Azure DevOps |
| None | `https://status.nuget.org/` is server-rendered HTML only (no JSON, no feed) [F] | | | NuGet; Stripe (custom, `/api/v2` 404); Microsoft 365 has no anonymous API [U] (Graph Service Communications needs tenant admin) |

Facts that shape the design:
- **[F] Cloudflare's summary is 223 KB, 481 components (PoPs).** Today: 28 PoPs `major_outage`, 33 degraded, 6 under maintenance, **0 incidents**, page indicator `major`. Rerouted PoPs are its normal state. "Worst of all components", the rule the Claude badge uses, would show Cloudflare red permanently. **Component filtering is mandatory, not a nicety**, and the catalog must ship sensible defaults for it.
- **[F] The AWS feed carries an event open since March 2026** (me-central-1, status 3). An unfiltered AWS entry is red forever: **AWS needs a region filter.**
- **[F] `status.claude.com/api/v1/summary` returns 200 with HTML.** Detection must check `Content-Type` and parse, not trust the status code.
- **[F] Redirects are common**: `status.anthropic.com` → `status.claude.com`, `status.zoom.us` → `zoomstatus.com`, `status.fly.io` → `status.flyio.net`, `status.cursor.com` → `status.x.ai/api/compat/summary.json` (403 Cloudflare bot wall for curl).
- **[F] .NET `HttpClient` doesn't decompress by default.** Set `AutomaticDecompression = All`: Cloudflare is 223 KB raw, 38 KB gzip.
- **[F]** The terminal ClaudeUsage parser calls `root.GetProperty("incidents")`, which throws on incident.io compat pages. It's harmless there (Claude and GitHub are Statuspage), but it rules out reusing that parser as is.

### Provider abstraction [O]

```csharp
interface IStatusProvider            // one per protocol, stateless, unit-testable on captured JSON
{
    string Kind { get; }             // "statuspage", "incidentio", "statusio", "slack", "google", "aws", "azure-rss", "heroku", "azdo"
    IReadOnlyList<Uri> Requests(CatalogEntry e);                 // 1 (Statuspage) or 2 (incident.io: compat components + v1 incidents)
    ServiceReading Parse(CatalogEntry e, IReadOnlyList<Body> bodies);  // pure function
}
record ServiceReading(Severity Worst, IReadOnlyList<ComponentReading> Components,
                      IReadOnlyList<IncidentReading> Incidents, IReadOnlyList<MaintenanceReading> Maintenances,
                      DateTimeOffset ReadAt);
```

Catalog entries are data (`kind`, `url`, `pageUrl`, filters). The poller owns HTTP, so a provider never touches the network. That keeps tests on captured fixtures, which is how the existing `StatusService.Parse(JsonElement, …)` is already shaped.

### Severity normalization [O on the policy; mappings F where observed]

`Unknown < Operational < Maintenance < Degraded < PartialOutage < MajorOutage`. Maintenance is its own level (blue) and doesn't count as an issue by default. The ClaudeUsage badge folds it into Minor; I disagree for a multi-service board, where planned work isn't trouble.

| Source | Operational | Maintenance | Degraded | Partial | Major |
|---|---|---|---|---|---|
| Statuspage component | `operational` | `under_maintenance` | `degraded_performance` | `partial_outage` | `major_outage` |
| Statuspage indicator / incident impact | `none` | `maintenance` | `minor` (open incident ≥ Degraded) | `major` | `critical` |
| incident.io | `operational` | `under_maintenance` | `degraded_performance` | `partial_outage` | `full_outage` |
| status.io `status_code` [U for 400/600] | 100 | 200 | 300 | 400; 600 (security) | 500 |
| Google `status_impact` [U for INFORMATION/OUTAGE] | `SERVICE_INFORMATION` (note only) | | | `SERVICE_DISRUPTION` | `SERVICE_OUTAGE` |
| AWS `status` (unofficial, inferred) | 0 | | 1 | 2 | 3 |
| Heroku | green | | yellow | | red |
| Azure RSS | empty | | | any item (no severity in feed) | |

Service severity = max over the **included** components and the open incidents (an incident counts if its affected components are included, or if it lists none). The page `indicator` is used only when the entry has no component filter: it's global. **Unknown** = no successful reading for 3 intervals (minimum 15 min), shown grey and never as green.

## 2. Catalog [O]

- **Built-in**: an embedded JSON resource in the plugin assembly, about 30 entries (`id`, `name`, `kind`, `url`, `pageUrl`, `include`/`exclude` component patterns, `regions` for AWS/GCP, `minInterval`). It's updated by shipping a plugin version through NuGet. That's the mechanism that already exists and is signed off by install.
- **No remote catalog fetch.** It's a URL-injection channel into an elevated process, it buys little (status endpoints change rarely), and it adds a second update path to maintain.
- **User entries**: `DataDirectory/catalog.user.json`, writable by administrators only [F: `%ProgramData%\NockMon\<SID>\PluginData\<id>`]. Same schema; a user entry overrides a built-in one with the same id.
- **Add by URL** auto-detection, in order:
  1. Normalize to https and follow up to 3 redirects.
  2. GET `/api/v1/summary`; JSON with `page_title` → incident.io.
  3. GET `/api/v2/summary.json`; JSON with `page` + `status` → Statuspage, or incident.io compat if `incidents` is absent (then fall back to `/api/v2/incidents.json` for incidents).
  4. HTML containing `status.io/pages/{24 hex}` → status.io.
  5. Otherwise "not supported".

  Require a JSON content type at each step [F: see the Claude HTML 200].

## 3. Polling

- **One process-wide poller** [O], with the refcounted static-service pattern of `UsageService`/`NewsService`/`StatusService` [F]: widgets subscribe their set of entries, and only the union is polled. When the last widget leaves, polling stops; when a widget comes back, the schedule resumes rather than refetching (as `NewsService` does).
- **Per-entry schedule**, not one global tick:
  - Default 2 min for Statuspage, incident.io and Slack.
  - 5 min for the heavy or slow-moving feeds (Google 251 KB, AWS, Azure, Heroku: `max-age=300`).
  - Never faster than the response's `max-age`; never under 60 s (incident.io's CDN serves the same data for 30 s anyway [F]).
- **Conditional GETs**: store ETag and Last-Modified per URL. They work on Statuspage, Cloudflare and Google [F]; incident.io, AWS JSON and Azure DevOps give no validators [F]; Azure ignores If-Modified-Since [F]. A 304 must keep the reading *and* bump `ReadAt`.
- **Jitter and stagger** [O]: spread first fetches evenly across the interval and add ±10 % per cycle, so 20 entries don't fire in the same 100 ms after a resume.
- **Backoff**: exponential per entry from 1 min, capped at 30 min; honor `Retry-After`; on 429, double the entry's interval for the session. One failing entry never blocks the others (the `Task.WhenAll` per-slot rule of the terminal tool [F] is the right one).
- **Offline and resume**: on `NetworkChange.NetworkAvailabilityChanged` → down, pause and let readings go Unknown after the stale threshold. On up, and on resume from sleep, schedule an immediate staggered refresh.
- **Persistence** [O]: save the last readings in `DataDirectory`, so a restart shows them at once, marked stale until refreshed.
- **Cost**: 20 entries at 2 min ≈ 600 to 700 requests/h (incident.io costs 2 per cycle). Statuspage 304s are header-only. incident.io bodies are 0.1 to 7 KB. A full refresh of a heavy entry (Cloudflare 38 KB, Google 44 KB, AWS 23 KB, gzip) happens only on change or every 5 min. Total ≪ 2 MB/h. CPU: one `JsonDocument` parse per changed body; for Cloudflare, about 481 components, well under 1 ms.

## 4. Widget UX

The widget doesn't know its grid span [F: `IWidgetContext` has no size]. It adapts to `View.ActualWidth/ActualHeight` through `SizeChanged`, as `WeeklyWidget` does [F]. A cell is 64 px plus a 4 px gap [F], so n cells ≈ 68n − 4 px; the title bar is optional and costs height.

| Tier (approx. span) | Shows |
|---|---|
| Tiny (1×1, < 100 px) | One glyph colored by the worst severity across subscriptions, plus a count of affected services. Tooltip: the affected list. Click: a flyout with the large view, so even a 1×1 widget is useful. |
| Small (2×1 to 3×2) | Glyph and one line: "All operational" / "OpenAI: partial outage (+2)". |
| Medium (3×3 to 4×6) | One row per service (dot, name, status word), worst first. Operational ones can collapse to "+12 operational" (setting). |
| Large (≥ 5×5) | Each troubled service with its affected components indented. Each open incident: title, status (investigating/identified/monitoring), age, latest update clipped to 3 lines, "Open page" link. Maintenance within 24 h in blue. |

Default size 3×3, minimum 1×1 [O].

**Settings editor** [O]:
- A searchable checklist of the catalog.
- Per checked entry, an expander with its components, pulled from the shared poller's latest reading, so no fetch happens in the editor unless none exists yet. Each component gets include or exclude, defaulting to the catalog's defaults. AWS and GCP get a region picker.
- "Add by URL" with Detect.
- Toggles: "Count maintenance as an issue" (off), "Collapse operational" (on).

Subscriptions are per widget instance, in the layout `Settings` [F: per-instance JsonObject]. Custom entries are plugin-global in `DataDirectory`.

**Notifications.** [F] Windows App SDK app notifications aren't supported for elevated apps; `AppNotificationManager.Show` fails silently (Microsoft Learn, App notifications quickstart). The only working channel from NockMon is a tray balloon (`Shell_NotifyIcon` `NIF_INFO`). The host's `TrayIcon` declares the `szInfo` fields but exposes no API for it [F]. Options:
- **v1, in-widget only**: an accent flash and a "changed since you looked" marker, like the news bell.
- **Later, SDK 1.2**: a host-provided notify API (§6), on transitions only. Two consecutive readings or 5 min debounce, then escalation and resolution. Per-service opt-in.

A plugin creating its own second tray icon is technically possible but bad UX [O].

**Metric source** [O]: a per-service severity of 0 to 4 (unit `Count`, NaN when Unknown) is marginal on charts (a step function). It's genuinely useful for two things: the history ("when did GitHub degrade this afternoon"), and AI agents through `list_metrics` [F: plugin metrics are exposed to agents].

Cost: `IMetricPlugin` is process-lifetime and global [F: started at load, disposed at exit], while subscriptions are per widget. It therefore needs a plugin-global "record history for" set in `DataDirectory`, and it keeps polling with no widget on screen. **Worth it, but v1.1 and opt-in.**

## 5. Security (the plugin runs inside the elevated NockMon process [F])

- **Transport**: https only. Reject `http:` entries, keep default certificate validation, and cap redirects at 3. [F] .NET doesn't follow https→http redirects. Set `UseCookies = false`, `UseDefaultCredentials = false` and `Credentials = null`. No auth headers ever; the User-Agent is `NockMon-ServiceStatus/x`.
- **Size and time caps**: a dedicated `SocketsHttpHandler` with timeouts of 15 s total and 10 s connect.
  - `MaxResponseContentBufferSize` around 2 MB: the largest legitimate body today is 307 KB (Workspace) [F].
  - Verify with a test that the cap counts *decompressed* bytes (gzip-bomb case) [U].
  - Skip bodies above the cap and keep the previous reading.
- **Parsing**:
  - `JsonDocument` with `MaxDepth = 32`. Read every field through `TryGetProperty` and value-kind checks: no `GetProperty` that throws on a schema change.
  - Per-entry limits: 1000 components, 50 incidents, 600-char texts (the same clip as `StatusService`).
  - AWS: decode by charset, because it's UTF-16 [F]. Don't feed it to the UTF-8 `JsonDocument.Parse(Stream)` path.
  - RSS: `XmlReader` with `DtdProcessing.Prohibit` (the default [U]), `XmlResolver = null` and `MaxCharactersInDocument`.
- **Incident text is untrusted**: render it as plain `TextBlock` text only. No WebView2, no markup interpretation, and strip tags and markdown to text. Make links only from structured URL fields (`shortlink`, `url`, `pageUrl`), only `https`, and only to the entry's page host or known shortlink hosts (`stspg.io` [U]). The news bell already applies the https rule [F].
- **User-added URLs, SSRF on a desktop**: low stakes. There are no credentials and the body is only parsed, never executed. Still, reject loopback, link-local (169.254.169.254, the cloud metadata address on VMs) and `file:`, using a `ConnectCallback` that checks the resolved IP; this also blocks DNS rebinding. Private ranges are allowed, since corporate status pages exist.
- **Catalog file**: admin-only `DataDirectory` [F], so a non-admin process can't plant entries.
- **Opening links from an elevated process** [U]: `Hyperlink.NavigateUri` may start the browser with an admin token. The host already does this (Plugins dialog [F]). It's a host-wide question, best solved once in the host (§6), not per plugin.

## 6. Fit with SDK 1.1

What 1.1 already gives [F]: per-instance settings with an editor, a shared admin-only `DataDirectory`, the dispatcher, `Refresh` at the sample rate, metric sources, per-plugin load contexts, and contained exceptions. The static refcounted-service pattern covers "shared background service" without SDK help: the samples prove it [F].

| Gap | Need | Recommendation [O] |
|---|---|---|
| Notifications | Real; impossible elevated through App SDK [F] | **Extend the SDK (1.2)**: `IHostServices.Notify(title, text, severity)` backed by the host tray balloon. Small for the host, and reusable by any plugin (build failures, sensors…). |
| Open URL unelevated | Cross-cutting | SDK 1.2 `OpenUrl(Uri)` that the host launches de-elevated; the Plugins dialog uses it too. |
| HTTP helper | No | Keep it in the plugin. The host's `HttpClient` has a 256 MB buffer for packages [F] and is the wrong policy; per-plugin handlers keep caps local. |
| Widget size / span | No | `SizeChanged` is enough and better (it's px-accurate and accounts for the title bar). |
| Plugin-global settings | Nice to have | `DataDirectory` JSON works; no SDK change. |
| "State" metric unit | Minor | `Count` is acceptable. |

Versioning constraint [F]: a plugin built against SDK 1.2 doesn't load on a 1.1 host. **Ship v1 on 1.1** without notifications, and add them in a later plugin version that requires host ≥ 1.2.

## 7. Relationship with the ClaudeUsage status badge (in progress; read, not touched)

[F] It's landing as `samples/ClaudeUsage.NockMon/StatusService.cs` and `StatusBadge.cs`:
- a Statuspage summary of status.claude.com, polled every 2 min with an ETag and a 1 MB buffer cap;
- `StatusSeverity {Operational, Minor, Major, Critical}` from the worst of the indicator, components and open incidents;
- texts clipped at 600 chars, https-only incident links.

It's a good, small, self-contained design.

**Don't make ClaudeUsage depend on this plugin.** [F] Each plugin has its own `AssemblyLoadContext`, and only host assemblies are shared. A NuGet dependency would load a second copy of the library, with its own static poller: no runtime sharing, just coupling of release cycles. Plugin-to-plugin dependencies don't exist in the SDK. The duplicated cost is one 304 every 2 min. Irrelevant.

**Share source, not binaries** [O]: once both are stable, extract a `samples/Shared/Statuspage/` parser (records plus pure `Parse`), compiled into both plugins via `<Compile Include>`. Two conditions:
- The shared parser returns *raw* per-component and per-incident data. **The "worst of all components" policy stays in the consumer**: right for Claude's 6 components, wrong for Cloudflare's 481 [F].
- Severity scales should map cleanly. Theirs, Minor/Major/Critical, aligns with Degraded/Partial/Major here. The only divergence is maintenance, which they fold into Minor. Worth telling that author now, before the enum ships.

ClaudeUsage keeps its badge: it's contextual to the usage widget, and users shouldn't need a second plugin for it.

## 8. Effort and v1 scope [O]

| Part | Size | Hours |
|---|---|---|
| Model, severity normalization, Statuspage provider, fixture tests | S | 4–6 |
| incident.io provider (v1 summary + compat components, fallback to `incidents.json`) | S | 3–4 |
| Slack, Google (GCP + Workspace) providers | S | 3–4 |
| status.io, Heroku, Azure DevOps providers | S | 3–4 |
| AWS (UTF-16 JSON, region filter) + Azure RSS | M | 5–7 |
| Shared poller (per-entry schedule, validators, backoff, jitter, offline/resume, persistence) + tests | M | 6–8 |
| Catalog (embedded JSON with tuned component defaults, user file, auto-detect) | M | 5–7 |
| Widget, 4 adaptive tiers + flyout | M–L | 10–14 |
| Settings editor (catalog search, component filters, add by URL) | M | 6–10 |
| Metric source (opt-in) | S | 3–4 |
| SDK 1.2 notify + OpenUrl (host + SDK + docs) | M | 5–8 |
| README, `Details` help, csproj, CI publish workflow | S | 2–3 |

**Recommended v1 (≈ 40–50 h)**:
- Providers: Statuspage, incident.io, Slack, Google. Those cover about 25 catalog entries: Claude, OpenAI, GitHub, Cloudflare (core services only by default), npm, Discord, Docker, Vercel, Netlify, DigitalOcean, Twilio, Zoom, Linear, Supabase, Sentry, MongoDB, Render, HashiCorp, CircleCI, Figma, Reddit, Slack, GCP, Workspace.
- The shared poller, the embedded catalog plus add-by-URL for Statuspage and incident.io, the four widget tiers, the settings editor and in-widget change markers.
- SDK 1.1.

**v1.1**: AWS (with regions), Azure, GitLab (status.io), Heroku, Azure DevOps, and the opt-in metric source.

**v2**: notifications and OpenUrl via SDK 1.2.

**Out**: NuGet and Stripe (no machine-readable status; scraping HTML from an elevated process isn't worth it), and Microsoft 365 (tenant auth).
