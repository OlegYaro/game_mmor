# Exports the game for macOS and Windows into out/. Syncs data first (ADR 0002).
# Requires src/Game/export_presets.cfg with presets "macOS" and "Windows Desktop".
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
Set-Location $Root
$presets = 'src/Game/export_presets.cfg'
if (-not (Test-Path $presets)) {
    Write-Error "export: $presets not found. Create the presets in the Godot editor (Project > Export) first."
    exit 1
}
& "$PSScriptRoot/sync-data.ps1"
dotnet build src/Game/Game.csproj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$out = Join-Path $Root 'out'
New-Item -ItemType Directory -Force -Path (Join-Path $out 'macos'), (Join-Path $out 'windows') | Out-Null
$null = Invoke-Godot --headless --path src/Game --import
$rc = Invoke-Godot --headless --path src/Game --export-release 'macOS' (Join-Path $out 'macos/Game.zip')
if ($rc -ne 0) { exit $rc }
$rc = Invoke-Godot --headless --path src/Game --export-release 'Windows Desktop' (Join-Path $out 'windows/Game.exe')
if ($rc -ne 0) { exit $rc }
Write-Host "export: done -> $out"
