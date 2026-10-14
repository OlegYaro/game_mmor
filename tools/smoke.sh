#!/usr/bin/env bash
# Headless smoke test (ADR 0011). Passes only if: exit code 0, log has SMOKE_OK,
# log has no "ERROR:" and no UNHANDLED_EXCEPTION. Extra args (e.g. --smoke-throw)
# are passed to the game after "--" (used to verify that failures are detected).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
cd "$ROOT"

LIMIT="${SMOKE_TIMEOUT:-60}"
LOG_DIR="$ROOT/.tools/logs"
[ -d "$ROOT/.tools" ] || LOG_DIR="$ROOT/TestResults"
LOG="${SMOKE_LOG:-$LOG_DIR/smoke.log}"
mkdir -p "$(dirname "$LOG")" "$LOG_DIR"
rm -f "$LOG"

run_limited() {
    if command -v timeout >/dev/null 2>&1; then
        timeout "$LIMIT" "$@"
    else
        perl -e 'alarm shift; exec @ARGV' "$LIMIT" "$@"
    fi
}

echo "smoke: dotnet build"
dotnet build Game.sln -c Debug --nologo -v q

# A clean checkout has no .godot/ cache: import first (do NOT use --build-solutions --quit here).
echo "smoke: godot --import"
run_limited env HOME="$GODOT_HOME" "$GODOT" --headless --path src/Game --import >"$LOG_DIR/import.out" 2>&1 || true

echo "smoke: run (limit ${LIMIT}s)"
set +e
run_limited env HOME="$GODOT_HOME" "$GODOT" --headless --path src/Game --log-file "$LOG" -- --smoke "$@" >"$LOG_DIR/smoke.out" 2>&1
code=$?
set -e

# Console output is scanned too: stdout can hold lines the log file misses.
fail=0
[ "$code" -eq 0 ] || { echo "smoke: FAIL exit code $code" >&2; fail=1; }
grep -q "SMOKE_OK" "$LOG" "$LOG_DIR/smoke.out" 2>/dev/null || { echo "smoke: FAIL SMOKE_OK not found" >&2; fail=1; }
if grep -n -E "ERROR:|UNHANDLED_EXCEPTION" "$LOG" "$LOG_DIR/smoke.out" 2>/dev/null; then
    echo "smoke: FAIL error markers in log" >&2; fail=1
fi
if [ "$fail" -ne 0 ]; then
    echo "--- last log lines ($LOG) ---" >&2
    tail -n 30 "$LOG" >&2 2>/dev/null || true
    exit 1
fi
echo "smoke: OK"
