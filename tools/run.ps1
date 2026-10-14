# Syncs data, builds and launches the game. Extra args go to Godot.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
& "$PSScriptRoot/sync-data.ps1"
dotnet build (Join-Path $Root 'src/Game/Game.csproj') --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
exit (Invoke-Godot --path (Join-Path $Root 'src/Game') @args)
