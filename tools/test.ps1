# Runs Core.Tests. Set $env:COVERAGE = '1' to also collect coverage into TestResults/ (without a -filter of the
# caller it leaves out Category=Benchmark and Category=Simulation).
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
Set-Location $Root
$cfg = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Debug' }
$dtArgs = @('tests/Core.Tests/Core.Tests.csproj', '-c', $cfg, '--nologo')
if ($env:COVERAGE -eq '1') {
    $dtArgs += @('--collect:XPlat Code Coverage', '--results-directory', (Join-Path $Root 'TestResults'))
    # Without a filter of the caller, coverage skips the timed benchmarks and the bot simulation (run them on their own).
    if (-not ($args | Where-Object { $_ -eq '--filter' -or $_ -like '--filter=*' })) {
        $dtArgs += @('--filter', 'Category!=Benchmark&Category!=Simulation')
    }
}
dotnet test @dtArgs @args
exit $LASTEXITCODE
