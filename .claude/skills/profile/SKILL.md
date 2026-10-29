---
name: profile
description: Profile the steady state of the NockMon WinUI app with dotTrace sampling (CPU time), generate XML reports, and analyze hot spots per pipeline stage
argument-hint: <name> [--warmup S] [--collect S] [--measure ThreadCycleTime|ThreadTime|PerformanceCounter] [--compare <baseline-report.xml>] [--filter <regex>] [--callers <method>]
---

# dotTrace profiling (NockMon)

Adapted from the Typhon `/profile` skill. Steady-state CPU profiling of a GUI app that is idle most of the time.

## Tools

- `dottrace`: global tool (`JetBrains.dotTrace.GlobalTools`), needs `DOTNET_ROLL_FORWARD=LatestMajor` (set by the script).
- `Reporter.exe`: `%USERPROFILE%\.nuget\packages\jetbrains.dottrace.commandlinetools.windows-x64\<latest>\tools\Reporter.exe` (found automatically).
- `tools/profiling/profile.sh <name> [warmup=15] [collect=30] [measure=ThreadTime]`: builds Release with `-p:ProfilingBuild=true`
  (optimized, honors `NOCKMON_SKIP_ELEVATION=1` since profilers run non-elevated), launches under dottrace with collection off,
  starts collecting after the warm-up via `##dotTrace["start"]` on stdin, snapshots, closes the app, then writes
  `tools/profiling/out/<name>-report.xml` (app frames, `app-pattern.xml`) and `<name>-all.xml` (all frames).
- `tools/profiling/analyze_profile.py <report.xml> [--top N] [--filter X] [--callers M] [--compare other.xml] [--json]`:
  self/inclusive hot spots and a **pipeline breakdown** (probe, rates, history, chart draw, table refresh, status).
- `tools/profiling/thread-cpu.ps1 -ProcessId <pid> [-Seconds 30]`: ground truth per-thread CPU (ms/s) of a running instance.

## Workflow

1. `bash tools/profiling/profile.sh <name> 60 30 ThreadCycleTime` (60 s warm-up: tiered JIT keeps recompiling for ~20–40 s;
   the first 20 s cost ~3× steady state).
2. `python tools/profiling/analyze_profile.py tools/profiling/out/<name>-all.xml --top 25`, then the `-report.xml` for the breakdown.
3. Cross-check totals against process CPU (`thread-cpu.ps1`, or `TotalProcessorTime` delta) before drawing conclusions.
4. Comparison: `analyze_profile.py out/<baseline>-report.xml --compare out/<name>-report.xml`.

## Pitfalls (measured on this project)

- **Use `ThreadCycleTime`.** `ThreadTime` advances in 15.6 ms scheduler quanta: small functions read 0 and random ones read 16/47 ms.
- **dotTrace only sees threads with managed frames.** WinUI composition/render and D3D threads are invisible; compare its total with the
  process CPU. Native attribution needs ETW (WPR/WPA, admin).
- `IApplicationStaticsMethods.Start` self time = UI thread native work (XAML layout, render, message pump) under `Application.Start`.
- `WaitHandle.WaitOne` self cycles are wait/wake transitions of the sampler thread, not busy time.
- Sampling misattributes memory stalls and under-credits bursty parallel work: confirm candidates with Stopwatch A/B.
- `Process.Threads[].StartAddress` always resolves to ntdll (`RtlUserThreadStart`): no module attribution that way.
