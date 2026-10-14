# Builds the whole solution.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
Set-Location $Root
$cfg = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Debug' }
dotnet build Game.sln -c $cfg --nologo @args
exit $LASTEXITCODE
