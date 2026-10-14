# Regenerates data/schema/*.json from the Core content types (ADR 0008).
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
Set-Location $Root
$cfg = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Debug' }
dotnet run --project tools/SchemaGen/SchemaGen.csproj -c $cfg --nologo @args
exit $LASTEXITCODE
