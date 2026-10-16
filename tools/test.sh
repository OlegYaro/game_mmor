#!/usr/bin/env bash
# Runs Core.Tests. COVERAGE=1 also collects coverage (Cobertura) into TestResults/ and, unless a --filter is given,
# leaves out Category=Benchmark and Category=Simulation.
# Extra arguments are passed to `dotnet test` (e.g. --filter "Category!=Benchmark").
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
cd "$ROOT"
args=(tests/Core.Tests/Core.Tests.csproj -c "${CONFIGURATION:-Debug}" --nologo)
if [ "${COVERAGE:-0}" = "1" ]; then
    args+=(--collect:"XPlat Code Coverage" --settings "$ROOT/tests/Core.Tests/coverage.runsettings" --results-directory "$ROOT/TestResults")
    # Without a filter of the caller, coverage skips the timed benchmarks and the bot simulation (run them on their own).
    has_filter=0
    for arg in "$@"; do
        case "$arg" in --filter|--filter=*) has_filter=1 ;; esac
    done
    if [ "$has_filter" = "0" ]; then
        args+=(--filter "Category!=Benchmark&Category!=Simulation")
    fi
fi
dotnet test "${args[@]}" "$@"
