#!/usr/bin/env bash
# Syncs data, builds and launches the game (not the editor). Extra args go to Godot.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
"$ROOT/tools/sync-data.sh"
dotnet build "$ROOT/src/Game/Game.csproj" --nologo -v q
# Import first: locale/ was just re-synced, so the CSV translations must be (re)generated.
godot_run --headless --path "$ROOT/src/Game" --import >/dev/null 2>&1 || true
godot_run --path "$ROOT/src/Game" "$@"
