# Claude Usage widget for NockMon

A [NockMon](https://github.com/nockawa/NockMon) plugin that shows the **Weekly** panel of
[claude-usage](https://github.com/nockawa/ClaudeUsage) as a widget:

- **Used** vs. **Elapsed** share of the 7-day window
- **Ahead / On pace / Behind**, and by how much
- **Resets in** countdown
- **Pace** (burn over the last 24 h) vs. **Target** (what you can burn per day until the reset)

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
[plugin guide](https://github.com/nockawa/NockMon/blob/main/docs/plugins.md): a widget with settings, a shared
background poller, and the packaging.

```sh
dotnet build samples/ClaudeUsage.NockMon
nockmon --plugin samples/ClaudeUsage.NockMon/bin/Debug/net10.0-windows10.0.19041.0
dotnet pack samples/ClaudeUsage.NockMon -c Release -o artifacts
```

Released by pushing a tag `claude-usage-v<version>` (`.github/workflows/claude-usage-plugin.yml`).

Public domain ([Unlicense](https://github.com/nockawa/NockMon/blob/main/samples/LICENSE)): copy it to start your own
plugin. Not affiliated with Anthropic.
