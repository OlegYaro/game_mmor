#!/usr/bin/env bash
# Copies the root data/ folder into src/Game/data/ (gitignored, ADR 0002).
# locale/ (translation CSVs) is mirrored to src/Game/locale/ the same way.
# Only *.json files are copied (content and schemas); other files in data/ are ignored.
# Stale files in the destination are removed so it always mirrors data/.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
src="$ROOT/data"
dst="$ROOT/src/Game/data"
[ -d "$src" ] || { echo "sync-data: $src not found" >&2; exit 1; }
rm -rf "$dst"
mkdir -p "$dst"
(cd "$src" && find . -type f -name '*.json' -print0 | while IFS= read -r -d '' f; do
    mkdir -p "$dst/$(dirname "$f")"
    cp "$f" "$dst/$f"
done)
echo "sync-data: $(find "$dst" -type f | wc -l | tr -d ' ') file(s) -> src/Game/data"

# locale/ -> src/Game/locale/ (gitignored)
lsrc="$ROOT/locale"
ldst="$ROOT/src/Game/locale"
rm -rf "$ldst"
if [ -d "$lsrc" ]; then
    mkdir -p "$ldst"
    (cd "$lsrc" && find . -type f -name '*.csv' -print0 | while IFS= read -r -d '' f; do
        mkdir -p "$ldst/$(dirname "$f")"
        cp "$f" "$ldst/$f"
    done)
    echo "sync-data: $(find "$ldst" -type f | wc -l | tr -d ' ') file(s) -> src/Game/locale"
fi
