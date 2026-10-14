#!/usr/bin/env bash
# Syncs data, builds and launches the game (not the editor). Extra args go to Godot.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
"$ROOT/tools/sync-data.sh"
dotnet build "$ROOT/src/Game/Game.csproj" --nologo -v q
godot_run --path "$ROOT/src/Game" "$@"
