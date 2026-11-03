# Samples

[← README](../README.md)

Complete NockMon plugins, to read alongside the [plugin guide](../docs/plugins.md) or to copy as a starting point. They
reference `NockMon.Sdk` from nuget.org, as any plugin does, and are not part of `NockMon.slnx`.

| Sample | Shows |
|---|---|
| [HardwareSensors.NockMon](HardwareSensors.NockMon) | First-party plugin `NockMon.HardwareSensors`: the CPU, motherboard, GPU and memory temperatures, fans, voltages and power (LibreHardwareMonitor) as metrics NockMon records and charts (API 1.1 metric source), a table widget, a background reader with per-hardware intervals. |
| [ServiceStatus.NockMon](ServiceStatus.NockMon) | First-party plugin `NockMon.ServiceStatus`: the status pages of online services (Statuspage, incident.io, status.io, Slack, Google, AWS, Azure, Heroku, Azure DevOps), a widget adapting to its size, a settings editor with per-service filters and pages added by URL, a shared poller with per-page schedules and conditional requests, an opt-in metric source (API 1.1), and unit tests on captured pages. |
| [ClaudeUsage.NockMon](ClaudeUsage.NockMon) | Your Claude subscription's weekly usage: a widget with a setting, a background poller shared by every instance, HTTP, a history kept in the plugin's data folder, the plugin package (published on nuget.org). |

Everything under `samples/` is public domain ([Unlicense](LICENSE)), unlike the rest of the repository (MIT): copy
freely, no attribution needed.
