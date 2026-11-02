# Hardware sensors for NockMon

The machine's sensors as NockMon metrics, read by [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor):
CPU temperatures and power, the motherboard's sensor chip (temperatures, fan speeds and duty, voltages), GPUs and memory
modules. Package `NockMon.HardwareSensors`, first-party: NockMon offers it in its Plugins dialog.

Not repeated: what NockMon reads itself. Drive temperatures (built in, through the storage driver), and for NVIDIA GPUs
the temperature, fans and power (NVML). The plugin adds the NVIDIA hot spot, memory junction and core voltage.

- **Metrics**: every sensor can be charted, shown in a value widget or a value list (series source **Plugin**), with
  NockMon's history, and read by AI agents.
- **Hardware sensors** widget: the sensors of the chosen kinds, grouped by hardware, with their current values.

## Requirements

- NockMon 1.2 or later (plugin API 1.1).
- CPU and motherboard sensors need the **PawnIO** driver: `winget install namazso.PawnIO`, then restart NockMon. Without
  it, GPUs and memory still report.

Read-only: the plugin never changes fan speeds.

## Notes

- Hardware is read on its own interval: CPU and motherboard every second, AMD and Intel GPUs every 2 s, NVIDIA GPUs every
  5 s (one read costs about 80 ms through NVAPI), memory every 10 s.
- Motherboard sensor chips name their sensors generically ("Temperature #2", "Fan #3"): label your series once you know
  which is which.
- Metric ids are LibreHardwareMonitor's sensor identifiers (`/amdcpu/0/temperature/2`): stable on a machine, so layouts
  keep working across sessions.

Public domain ([Unlicense](../LICENSE)), except LibreHardwareMonitorLib (MPL-2.0), shipped in the package.
