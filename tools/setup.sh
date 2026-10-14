#!/usr/bin/env bash
# One-time (idempotent) setup of the project-local toolchain in .tools/. macOS arm64 only for now.
# Installs: .NET SDK, Godot 4.7.2 mono + export templates, csharp-ls. Nothing global is touched.
set -euo pipefail

GODOT_VERSION="4.7.2"
GODOT_BASE="https://github.com/godotengine/godot/releases/download/${GODOT_VERSION}-stable"
GODOT_ZIP="Godot_v${GODOT_VERSION}-stable_mono_macos.universal.zip"
GODOT_TPZ="Godot_v${GODOT_VERSION}-stable_mono_export_templates.tpz"
DOTNET_CHANNEL="10.0"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
T="$ROOT/.tools"

if [ "$(uname -s)-$(uname -m)" != "Darwin-arm64" ]; then
    echo "setup: only macOS arm64 is supported for now" >&2
    exit 1
fi

mkdir -p "$T" "$T/downloads" "$T/logs" "$T/godot-home"

fetch_verified() { # fetch_verified <file name> ; checks SHA512 against the release SHA512-SUMS.txt
    local name="$1" dest="$T/downloads/$1"
    if [ ! -f "$T/downloads/SHA512-SUMS.txt" ]; then
        curl -fsSL "$GODOT_BASE/SHA512-SUMS.txt" -o "$T/downloads/SHA512-SUMS.txt"
    fi
    local expected actual
    expected="$(awk -v n="$name" '$2 == n { print $1 }' "$T/downloads/SHA512-SUMS.txt")"
    [ -n "$expected" ] || { echo "setup: no checksum for $name" >&2; exit 1; }
    if [ ! -f "$dest" ]; then
        echo "setup: downloading $name"
        curl -fSL "$GODOT_BASE/$name" -o "$dest"
    fi
    actual="$(shasum -a 512 "$dest" | awk '{ print $1 }')"
    if [ "$actual" != "$expected" ]; then
        rm -f "$dest"
        echo "setup: checksum mismatch for $name" >&2
        exit 1
    fi
}

# --- .NET SDK ---
if [ ! -x "$T/dotnet/dotnet" ]; then
    echo "setup: installing .NET SDK ($DOTNET_CHANNEL)"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$T/downloads/dotnet-install.sh"
    bash "$T/downloads/dotnet-install.sh" --channel "$DOTNET_CHANNEL" --install-dir "$T/dotnet" --no-path
fi

# --- Godot editor (self-contained: ._sc_ makes it keep editor data next to the app) ---
if [ ! -x "$T/godot/Godot_mono.app/Contents/MacOS/Godot" ]; then
    fetch_verified "$GODOT_ZIP"
    mkdir -p "$T/godot"
    unzip -q -o "$T/downloads/$GODOT_ZIP" -d "$T/godot"
fi
touch "$T/godot/Godot_mono.app/Contents/MacOS/._sc_"

# --- Export templates ---
TPL_DIR="$T/godot/editor_data/export_templates/${GODOT_VERSION}.stable.mono"
if [ ! -d "$TPL_DIR" ]; then
    fetch_verified "$GODOT_TPZ"
    tmp="$(mktemp -d "$T/downloads/tpz.XXXXXX")"
    unzip -q -o "$T/downloads/$GODOT_TPZ" -d "$tmp"
    mkdir -p "$TPL_DIR"
    cp -R "$tmp/templates/." "$TPL_DIR/"
    rm -rf "$tmp"
fi

# --- csharp-ls (LSP for Claude Code) ---
source "$ROOT/tools/env.sh"
if [ ! -x "$T/bin/csharp-ls" ]; then
    dotnet tool install csharp-ls --tool-path "$T/bin"
fi

echo "setup: done"
dotnet --version
"$GODOT" --version || true
