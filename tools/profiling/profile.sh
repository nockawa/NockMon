#!/usr/bin/env bash
# Profiles the steady state of the WinUI app with dotTrace sampling (CPU time only).
# Usage: tools/profiling/profile.sh <name> [warmup_seconds=15] [collect_seconds=30] [time_measurement=ThreadTime]
# Output: tools/profiling/out/<name>.dtp, <name>-report.xml (app frames), <name>-all.xml (all frames).
set -euo pipefail

NAME=${1:?snapshot name required}
WARMUP=${2:-15}
COLLECT=${3:-30}
MEASURE=${4:-ThreadTime}

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
PROF="$ROOT/tools/profiling"
OUT="$PROF/out"
BUILD="$OUT/build"
REPORTER=$(ls -d "$USERPROFILE"/.nuget/packages/jetbrains.dottrace.commandlinetools.windows-x64/*/tools/Reporter.exe | sort -V | tail -1)
mkdir -p "$OUT"

echo "== Build (Release, ProfilingBuild)"
dotnet build "$ROOT/src/NockMon.App" -c Release -r win-x64 -p:ProfilingBuild=true -o "$BUILD" -v q -nologo | grep -E " error |Build succeeded" || true
[ -f "$BUILD/NockMon.exe" ] || { echo "build failed"; exit 1; }

echo "== Profile: ${WARMUP}s warm-up, ${COLLECT}s collection ($MEASURE)"
export DOTNET_ROLL_FORWARD=LatestMajor NOCKMON_SKIP_ELEVATION=1
{
  sleep "$WARMUP"
  echo '##dotTrace["start"]'
  sleep "$COLLECT"
  echo '##dotTrace["get-snapshot"]'
  sleep 5
  echo '##dotTrace["disconnect"]'
} | dottrace start --profiling-type=Sampling --time-measurement="$MEASURE" \
      --collect-data-from-start=off --service-input=stdin --service-output=on \
      --save-to="$OUT/$NAME.dtp" --overwrite -- "$BUILD/NockMon.exe" &
DT=$!

sleep $((WARMUP + COLLECT + 8))
# The app keeps running after disconnect: close it.
powershell.exe -NoProfile -Command "Get-Process NockMon -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '*profiling*' } | Stop-Process" || true
wait $DT || true

SNAP=$(ls -t "$OUT"/"$NAME"*.dtp 2>/dev/null | head -1)
[ -n "$SNAP" ] || { echo "no snapshot produced"; exit 1; }
echo "== Report ($SNAP)"
"$REPORTER" report "$SNAP" --pattern="$PROF/app-pattern.xml" --save-to="$OUT/$NAME-report.xml" --overwrite --save-signature >/dev/null
"$REPORTER" report "$SNAP" --pattern="$PROF/all-frames-pattern.xml" --save-to="$OUT/$NAME-all.xml" --overwrite --save-signature >/dev/null
echo "Reports: $OUT/$NAME-report.xml, $OUT/$NAME-all.xml"
