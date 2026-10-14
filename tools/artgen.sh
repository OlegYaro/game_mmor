#!/usr/bin/env bash
# Regenerates the placeholder art: src/Game/art/*.png and data/art/atlas.json (ADR 0010).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
cd "$ROOT"
dotnet run --project tools/ArtGen/ArtGen.csproj -c "${CONFIGURATION:-Debug}" --nologo "$@"
