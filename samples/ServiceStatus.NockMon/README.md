# Service status for NockMon

A [NockMon](https://github.com/nockawa/NockMon) widget showing the status of the online services you follow, read from
their public status pages. It shows as much as its size allows:

| Size | Shows |
|---|---|
| Smallest (1×1) | One icon colored by the worst state, and the number of services with an issue. Click it for the details. |
| One row high | The icon and the first service with an issue: "OpenAI: partial outage (+2)". |
| Medium | One row per service, worst first; click a row for its details. |
| Large | Each service with an issue: affected components, open incidents with their latest update, maintenance in progress or within a day. |

Colors: **red** major outage, **orange** partial outage, **yellow** degraded (or an open incident), **blue** maintenance
(shown, not counted as an issue), **grey** unknown (no reading for 15 minutes or more: never shown as fine), **green**
operational. A dot marks what changed since the pointer last left the widget.

## Services

Built in: AWS, Azure, Azure DevOps, Bitbucket, CircleCI, Claude, Cloudflare, Confluence, DigitalOcean, Discord, Docker,
Dropbox, Epic Games, Figma, Fly.io, GitHub, GitLab, Google Cloud, Google Workspace, HashiCorp, Heroku, Jira, Linear,
MongoDB Atlas, Netlify, Notion, npm, OpenAI, Perplexity, Postman, Reddit, Render, RubyGems, Sentry, Slack, Supabase,
Twilio, Vercel, Zoom.

**Add by URL** (widget settings): any Atlassian Statuspage, incident.io or status.io page, which is most status pages.
Added pages are offered to every widget of this plugin.

| Protocol | Pages | Read |
|---|---|---|
| Atlassian Statuspage | most | `/api/v2/summary.json`: components, incidents, maintenance |
| incident.io | OpenAI, Linear, Supabase, HashiCorp, Perplexity, Fly.io | `/api/v2/summary.json` (components) and `/api/v1/summary` (incidents, maintenance) |
| Slack | Slack | `slack-status.com/api/v2.0.0/current` |
| Google | Google Cloud, Google Workspace | `incidents.json`: open incidents, products, locations |
| AWS | AWS | `health.aws.amazon.com/public/currentevents` (unofficial, the feed behind the AWS Health Dashboard) |
| Azure | Azure | The status RSS feed: widespread incidents only |
| status.io | GitLab | `status.io/1.0/status/{page id}`: components, incidents, maintenance |
| Heroku | Heroku | `status.heroku.com/api/v4/current-status`: systems, incidents, maintenance in progress |
| Azure DevOps | Azure DevOps | `status.dev.azure.com/_apis/status/health`: each service per geography |

Not supported: pages without a machine-readable status (NuGet, Stripe), and Microsoft 365 (needs a tenant account).

## Filters

Per widget and per service, in the settings: the components (or groups) that count, and for feed-only services (Slack,
Google, AWS, Azure) patterns on products and regions (`us-east-1; eu-*`). Some pages list many components that are
routinely degraded: Cloudflare lists about 300 locations, and by default counts its core services only. AWS and Google
report incidents worldwide: give them your regions. A filtered service ignores the page's overall indicator.

## History

Tick **History** next to a service, in any widget's settings, and NockMon records its state as a metric. NockMon keeps
its history, and you can chart it, show it in value widgets, or let AI agents read it ("when did GitHub degrade this
afternoon?"). Look for the source **Sensors and plugins**, group **Service status**.

- **Values:** 0 operational, 1 maintenance, 2 degraded, 3 partial outage, 4 major outage. Unknown is missing.
- **Filter:** that of the widget it was ticked in.
- **For all widgets:** History is plugin-wide, and a recorded service is read even when no widget shows it.

## How it reads

- One poller for every widget, reading the pages the widgets follow, every 2 minutes (5 for AWS, Azure and Google), never
  faster than a page's `max-age`. It uses conditional requests where the page supports them, adds jitter, and backs off
  on failure up to 30 minutes, honoring `Retry-After`. It reads again when the network comes back.
- The last readings are kept in the plugin's folder, so a restart shows them at once.
- Inside NockMon's elevated process, every request follows the same rules:
  - https only, no cookies or credentials, and at most 2 MB per page.
  - At most 3 redirects, and no connection to loopback or link-local addresses.
  - Incident text is shown as plain text.
  - Links go only to the page's own hosts, and open in your browser as you, not elevated (through Explorer).

## Requirements

NockMon 1.2 or later (plugin API 1.1: metric sources).

## Develop

```sh
dotnet test --project samples/ServiceStatus.NockMon.Tests
dotnet build samples/ServiceStatus.NockMon
nockmon --plugin samples/ServiceStatus.NockMon/bin/Debug/net10.0-windows10.0.19041.0
```

`Status/` holds everything but the UI (model, providers, catalog, HTTP, poller); the tests compile it as is, on pages
captured from the real services (`Fixtures/`). Released by pushing a tag `service-status-v<version>`
(`.github/workflows/service-status-plugin.yml`).

Public domain ([Unlicense](https://github.com/nockawa/NockMon/blob/main/samples/LICENSE)). Not affiliated with any of
the services listed.
