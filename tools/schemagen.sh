#!/usr/bin/env bash
# Regenerates data/schema/*.json from the Core content types (ADR 0008).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
cd "$ROOT"
dotnet run --project tools/SchemaGen/SchemaGen.csproj -c "${CONFIGURATION:-Debug}" --nologo "$@"
