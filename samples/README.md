# Samples

[← README](../README.md)

Complete NockMon plugins, to read alongside the [plugin guide](../docs/plugins.md) or to copy as a starting point. They
reference `NockMon.Sdk` from nuget.org, as any plugin does, and are not part of `NockMon.slnx`.

| Sample | Shows |
|---|---|
| [HardwareSensors.NockMon](HardwareSensors.NockMon) | First-party plugin `NockMon.HardwareSensors`: the CPU, motherboard, GPU and memory temperatures, fans, voltages and power (LibreHardwareMonitor) as metrics NockMon records and charts (API 1.1 metric source), a table widget, a background reader with per-hardware intervals. |
| [ClaudeUsage.NockMon](ClaudeUsage.NockMon) | Your Claude subscription's weekly usage: a widget with a setting, a background poller shared by every instance, HTTP, a history kept in the plugin's data folder, the plugin package (published on nuget.org). |

Everything under `samples/` is public domain ([Unlicense](LICENSE)), unlike the rest of the repository (MIT): copy
freely, no attribution needed.
