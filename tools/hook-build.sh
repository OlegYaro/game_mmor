#!/usr/bin/env bash
# Claude Code PostToolUse hook (Edit|Write): after a .cs file changes, build quietly
# and print only compiler errors. Exit code 2 feeds the errors back to Claude.
set -uo pipefail
input="$(cat)"
file="$(printf '%s' "$input" | sed -n 's/.*"file_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
case "$file" in
    *.cs) ;;
    *) exit 0 ;;
esac
source "$(dirname "${BASH_SOURCE[0]}")/env.sh" || exit 0
# A build into this working copy's bin/Debug while its own test run is using it breaks both, so skip then and say so.
# Only processes of this ROOT count: other worktrees and projects have their own bin.
if pgrep -f "$ROOT/tests/Core.Tests" >/dev/null 2>&1; then
    printf '%s\n' '{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"tools/hook-build.sh skipped the build because a test run of this working copy is in progress; compile errors in the edited file were NOT checked."}}'
    exit 0
fi
cd "$ROOT" || exit 0
out="$(dotnet build Game.sln --nologo -v q -clp:NoSummary 2>&1)"
if [ $? -ne 0 ]; then
    printf '%s\n' "$out" | grep -E "error [A-Z]+[0-9]+" | sort -u | head -n 20 >&2
    exit 2
fi
exit 0
