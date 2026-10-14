#!/usr/bin/env bash
# Runs Core.Tests. COVERAGE=1 also collects coverage (Cobertura) into TestResults/.
# Extra arguments are passed to `dotnet test` (e.g. --filter "Category!=Benchmark").
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
cd "$ROOT"
args=(tests/Core.Tests/Core.Tests.csproj -c "${CONFIGURATION:-Debug}" --nologo)
if [ "${COVERAGE:-0}" = "1" ]; then
    args+=(--collect:"XPlat Code Coverage" --results-directory "$ROOT/TestResults")
fi
dotnet test "${args[@]}" "$@"
