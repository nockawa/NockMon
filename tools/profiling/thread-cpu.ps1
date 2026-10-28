# Per-thread CPU of a running process over a window, attributed to the module containing each thread's start address.
# Usage: thread-cpu.ps1 -ProcessId <pid> [-Seconds 30]
param([Parameter(Mandatory)][int]$ProcessId, [int]$Seconds = 30)
$p = Get-Process -Id $ProcessId
$before = @{}; foreach ($t in $p.Threads) { try { $before[$t.Id] = $t.TotalProcessorTime } catch {} }
Start-Sleep -Seconds $Seconds
$p.Refresh()
$modules = $p.Modules | ForEach-Object { [pscustomobject]@{ Name = $_.ModuleName; Base = [int64]$_.BaseAddress; End = [int64]$_.BaseAddress + $_.ModuleMemorySize } }
$rows = foreach ($t in $p.Threads) {
  try { $cpu = $t.TotalProcessorTime } catch { continue }
  $delta = if ($before.ContainsKey($t.Id)) { $cpu - $before[$t.Id] } else { $cpu }
  $addr = [int64]$t.StartAddress
  $mod = ($modules | Where-Object { $addr -ge $_.Base -and $addr -lt $_.End } | Select-Object -First 1).Name
  [pscustomobject]@{ Tid = $t.Id; Module = $mod; MsPerSec = [math]::Round($delta.TotalMilliseconds / $Seconds, 2) }
}
$rows | Where-Object MsPerSec -gt 0 | Sort-Object MsPerSec -Descending | Format-Table -AutoSize
$total = ($rows | Measure-Object MsPerSec -Sum).Sum
"Total: {0:0.0} ms CPU per second" -f $total
$rows | Group-Object Module | ForEach-Object { [pscustomobject]@{ Module = $_.Name; MsPerSec = [math]::Round(($_.Group | Measure-Object MsPerSec -Sum).Sum, 2) } } | Sort-Object MsPerSec -Descending | Format-Table -AutoSize
