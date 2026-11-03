# Claude Usage widget for NockMon

A [NockMon](https://github.com/nockawa/NockMon) plugin that shows the **Weekly** panel of
[claude-usage](https://github.com/nockawa/ClaudeUsage) as a widget:

- **Used** vs. **Elapsed** share of the 7-day window
- **Trend**: burn over the last 3 h in %/h, with claude-usage's arrows (↑↑ ↑ → ↓ ↓↓) against the even burn until the reset
- **Ahead / On pace / Behind**, and by how much
- **Resets in** countdown
- **Pace** (burn over the last 24 h) vs. **Target** (what you can burn per day until the reset)
- **Service status**: a warning icon at the bottom right, yellow to red, while a Claude service is not fully functional; click it for the affected services and open incidents
- **Claude news**: a bell at the bottom right counts unread Claude and Anthropic news; click it for the list

## News

Sources: the [Claude Platform release notes](https://platform.claude.com/docs/en/release-notes/overview), Claude
Code's [What's new](https://code.claude.com/docs/en/whats-new) and
[changelog](https://code.claude.com/docs/en/changelog), checked every 30 minutes. Each widget shows the categories
ticked in its settings:

| Category | Default |
|---|---|
| Models, pricing and plans: launches, retirements, price changes, Pro and Max plans | on |
| Claude Code: weekly highlights | on |
| Claude Code: every release (almost daily) | off |
| API features, SDKs and CLI, Managed Agents, Enterprise and admin, Cloud providers | off |

Release notes are sorted by the documentation each one links to (and a few phrases), so a launch or a price change
isn't drowned among admin and cloud-provider updates. What was published before the widget first checked a source
counts as read. An item you open is read; **Mark all as read** dismisses the list.

## Service status

The public [status.claude.com](https://status.claude.com) summary, as claude-usage reads it, checked every 2 minutes
(conditional requests: an unchanged page costs a 304). The icon's color is the worst of the page's indicator, its
components and its open incidents:

| Color | When |
|---|---|
| Yellow | Degraded performance, maintenance, minor incident (or an incident of no declared impact) |
| Orange | Partial outage, major incident |
| Red | Major outage, critical incident |

No icon while everything is operational, or when the page couldn't be read for 30 minutes. Each widget can turn it
off in its settings.

## Install

In NockMon: toolbar **Plugins** › Browse › *Claude Usage* › Install. Then, in edit mode,
**Add widget › Claude weekly usage**.

## How it reads the usage

Same as claude-usage: the OAuth token the Claude CLI stored (`CLAUDE_CODE_OAUTH_TOKEN`, else
`~/.claude/.credentials.json`) and Anthropic's **unofficial** usage endpoint, which can change without notice. While
claude-usage runs, its fresh cache is read instead of calling the endpoint again. The token never leaves your machine
except as the `Authorization` header to Anthropic's API.

## Develop

This plugin is also the complete example of the
[plugin guide](https://github.com/nockawa/NockMon/blob/main/docs/plugins.md): a widget with settings, shared
background pollers, a flyout, state kept in the plugin's data folder, and the packaging.

```sh
dotnet build samples/ClaudeUsage.NockMon
nockmon --plugin samples/ClaudeUsage.NockMon/bin/Debug/net10.0-windows10.0.19041.0
dotnet pack samples/ClaudeUsage.NockMon -c Release -o artifacts
```

Released by pushing a tag `claude-usage-v<version>` (`.github/workflows/claude-usage-plugin.yml`).

Public domain ([Unlicense](https://github.com/nockawa/NockMon/blob/main/samples/LICENSE)): copy it to start your own
plugin. Not affiliated with Anthropic.
