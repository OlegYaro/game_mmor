#!/usr/bin/env bash
# Exports the game for macOS and Windows into out/ using the local export templates.
# Always syncs data first (ADR 0002). Requires src/Game/export_presets.cfg with presets
# named "macOS" and "Windows Desktop".
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
cd "$ROOT"

presets="src/Game/export_presets.cfg"
if [ ! -f "$presets" ]; then
    echo "export: $presets not found. Create the presets in the Godot editor (Project > Export) first." >&2
    exit 1
fi

"$ROOT/tools/sync-data.sh"
dotnet build src/Game/Game.csproj -c Release --nologo -v q

out="$ROOT/out"
mkdir -p "$out/macos" "$out/windows"
godot_run --headless --path src/Game --import >/dev/null 2>&1 || true
godot_run --headless --path src/Game --export-release "macOS" "$out/macos/Game.zip"
godot_run --headless --path src/Game --export-release "Windows Desktop" "$out/windows/Game.exe"
echo "export: done -> $out"
