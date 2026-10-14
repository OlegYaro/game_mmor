# Regenerates the placeholder art: src/Game/art/*.png and data/art/atlas.json (ADR 0010).
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
Set-Location $Root
$cfg = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Debug' }
dotnet run --project tools/ArtGen/ArtGen.csproj -c $cfg --nologo @args
exit $LASTEXITCODE
