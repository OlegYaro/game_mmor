# Dot-sourced by every tools/*.ps1 script: . "$PSScriptRoot/env.ps1"
# Uses the project-local toolchain in .tools/ (nothing global).
$script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:T = Join-Path $Root '.tools'

if (Test-Path (Join-Path $T 'dotnet')) {
    $env:DOTNET_ROOT = Join-Path $T 'dotnet'
    $env:DOTNET_CLI_HOME = Join-Path $T 'dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $T 'nuget'
    $env:PATH = (Join-Path $T 'dotnet') + [IO.Path]::PathSeparator + (Join-Path $T 'bin') + [IO.Path]::PathSeparator + $env:PATH
    if ($env:GODOT_BIN) {
        $script:Godot = $env:GODOT_BIN
    } else {
        $found = Get-ChildItem -Path (Join-Path $T 'godot') -Recurse -Filter 'Godot*_mono*.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch 'console' } | Select-Object -First 1
        $script:Godot = if ($found) { $found.FullName } else { 'godot' }
    }
} elseif ($env:CI) {
    # CI without .tools: system dotnet (actions/setup-dotnet) and Godot from setup-godot.
    $script:Godot = if ($env:GODOT_BIN) { $env:GODOT_BIN } elseif ($env:GODOT) { $env:GODOT } else { 'godot' }
} else {
    throw "env.ps1: $T\dotnet not found. Set up .tools first (see README.md)."
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

# Godot keeps user:// data under HOME / APPDATA; keep them inside the project.
$script:GodotHome = if (Test-Path $T) { Join-Path $T 'godot-home' } else { Join-Path ([IO.Path]::GetTempPath()) 'godot-home' }
New-Item -ItemType Directory -Force -Path $GodotHome, (Join-Path $T 'logs') | Out-Null

# Run Godot with a project-local profile. Usage: Invoke-Godot <args...>
function Invoke-Godot {
    $oldHome = $env:HOME; $oldAppData = $env:APPDATA; $oldUser = $env:USERPROFILE
    $env:HOME = $GodotHome; $env:APPDATA = $GodotHome; $env:USERPROFILE = $GodotHome
    try { & $script:Godot @args; return $LASTEXITCODE }
    finally { $env:HOME = $oldHome; $env:APPDATA = $oldAppData; $env:USERPROFILE = $oldUser }
}
