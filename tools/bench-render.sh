#!/usr/bin/env bash
# Windowed map rendering benchmark (NFR-01, M1 acceptance; not headless: headless does not render).
# Opens a window for ~1 minute, scrolls a revealed 512² map corner to corner at every zoom and sweeps it at the
# widest zoom with vsync off, prints "BENCH avg_fps=.. min_fps=.. p1_low=..". Exit code 0 when avg_fps >= 60.
# Log: .tools/logs/bench.log. Extra args go to the game, e.g. --bench-shots=/abs/dir (review screenshots),
# --bench-shots-only, --bench-size=1024.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
cd "$ROOT"

LIMIT="${BENCH_TIMEOUT:-240}"
LOG_DIR="$ROOT/.tools/logs"
[ -d "$ROOT/.tools" ] || LOG_DIR="$ROOT/TestResults"
LOG="$LOG_DIR/bench.log"
mkdir -p "$LOG_DIR"
rm -f "$LOG"

run_limited() {
    if command -v timeout >/dev/null 2>&1; then
        timeout "$LIMIT" "$@"
    else
        perl -e 'alarm shift; exec @ARGV' "$LIMIT" "$@"
    fi
}

"$ROOT/tools/sync-data.sh"
echo "bench: dotnet build"
dotnet build "$ROOT/src/Game/Game.csproj" -c "${CONFIGURATION:-Debug}" --nologo -v q
run_limited env HOME="$GODOT_HOME" "$GODOT" --headless --path src/Game --import >"$LOG_DIR/import.out" 2>&1 || true

echo "bench: run (window, limit ${LIMIT}s)"
set +e
run_limited env HOME="$GODOT_HOME" "$GODOT" --path src/Game --disable-vsync ${BENCH_GODOT_ARGS:-} --log-file "$LOG" res://World/RenderBench.tscn -- --bench-render "$@" >"$LOG_DIR/bench.out" 2>&1
code=$?
set -e

grep -E "^BENCH" "$LOG_DIR/bench.out" || true
if grep -n -E "ERROR:|UNHANDLED_EXCEPTION" "$LOG" "$LOG_DIR/bench.out" 2>/dev/null; then
    echo "bench: error markers in log" >&2
    [ "$code" -eq 0 ] && code=1
fi
if [ "$code" -ne 0 ]; then
    echo "bench: FAIL (exit code $code), log: $LOG" >&2
    exit "$code"
fi
echo "bench: OK"
