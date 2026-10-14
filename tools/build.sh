#!/usr/bin/env bash
# Builds the whole solution (warnings are errors in Core/tests, see Directory.Build.props).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
cd "$ROOT"
dotnet build Game.sln -c "${CONFIGURATION:-Debug}" --nologo "$@"
