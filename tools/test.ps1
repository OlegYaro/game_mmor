# Runs Core.Tests. Set $env:COVERAGE = '1' to also collect coverage into TestResults/.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
Set-Location $Root
$cfg = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Debug' }
$dtArgs = @('tests/Core.Tests/Core.Tests.csproj', '-c', $cfg, '--nologo')
if ($env:COVERAGE -eq '1') {
    $dtArgs += @('--collect:XPlat Code Coverage', '--results-directory', (Join-Path $Root 'TestResults'))
}
dotnet test @dtArgs @args
exit $LASTEXITCODE
