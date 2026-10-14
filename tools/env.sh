# Sourced by every tools/*.sh script. Do not execute directly.
# Everything comes from the project-local toolchain in .tools/ (nothing global).
# Sets: ROOT, T, GODOT, GODOT_HOME. Exports DOTNET_*, NUGET_PACKAGES, PATH.

_env_dir="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
ROOT="$(cd "$_env_dir/.." && pwd)"
T="$ROOT/.tools"
unset _env_dir

if [ -d "$T/dotnet" ]; then
    export DOTNET_ROOT="$T/dotnet"
    export DOTNET_CLI_HOME="$T/dotnet-home"
    export NUGET_PACKAGES="$T/nuget"
    export PATH="$T/dotnet:$T/bin:$PATH"
    GODOT="$T/godot/Godot_mono.app/Contents/MacOS/Godot"
elif [ -n "${CI:-}" ]; then
    # CI without .tools: system dotnet (actions/setup-dotnet) and Godot from setup-godot.
    GODOT="${GODOT_BIN:-${GODOT:-godot}}"
else
    echo "env.sh: $T/dotnet not found. Run tools/setup.sh first." >&2
    return 1 2>/dev/null || exit 1
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Godot keeps user:// data and editor settings under HOME; keep them inside the project.
if [ -d "$T" ]; then
    GODOT_HOME="$T/godot-home"
else
    GODOT_HOME="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/godot-home"
fi
mkdir -p "$GODOT_HOME" "$T/logs" 2>/dev/null || true
export GODOT GODOT_HOME

# Run Godot with the project-local HOME. Usage: godot_run <args...>
godot_run() {
    HOME="$GODOT_HOME" "$GODOT" "$@"
}
