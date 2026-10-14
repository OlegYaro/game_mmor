# Windowed map rendering benchmark (NFR-01, M1 acceptance; not headless: headless does not render).
# Opens a window for ~1 minute, scrolls a revealed 512² map corner to corner at every zoom and sweeps it at the
# widest zoom with vsync off, prints "BENCH avg_fps=.. min_fps=.. p1_low=..". Exit code 0 when avg_fps >= 60.
# Log: .tools/logs/bench.log. Extra args go to the game, e.g. --bench-shots=C:\abs\dir, --bench-shots-only.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
Set-Location $Root

$limit = if ($env:BENCH_TIMEOUT) { [int]$env:BENCH_TIMEOUT } else { 240 }
$logDir = Join-Path $T 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'bench.log'
$out = Join-Path $logDir 'bench.out'
Remove-Item -Force -ErrorAction SilentlyContinue $log, $out

& "$PSScriptRoot/sync-data.ps1"
Write-Host 'bench: dotnet build'
dotnet build src/Game/Game.csproj -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { exit 1 }

function Start-Limited([string[]]$GodotArgs, [string]$StdOut) {
    $oldHome = $env:HOME; $oldAppData = $env:APPDATA; $oldUser = $env:USERPROFILE
    $env:HOME = $GodotHome; $env:APPDATA = $GodotHome; $env:USERPROFILE = $GodotHome
    try {
        $p = Start-Process -FilePath $Godot -ArgumentList $GodotArgs -NoNewWindow -PassThru -RedirectStandardOutput $StdOut
        $null = $p.Handle
        if (-not $p.WaitForExit($limit * 1000)) {
            $p.Kill($true)
            Write-Host "bench: timeout after ${limit}s"
            return 124
        }
        $p.WaitForExit()
        return [int]$p.ExitCode
    } finally { $env:HOME = $oldHome; $env:APPDATA = $oldAppData; $env:USERPROFILE = $oldUser }
}

$null = Start-Limited @('--headless', '--path', 'src/Game', '--import') (Join-Path $logDir 'import.out')
Write-Host "bench: run (window, limit ${limit}s)"
$code = Start-Limited (@('--path', 'src/Game', '--disable-vsync', '--log-file', $log, 'res://World/RenderBench.tscn', '--', '--bench-render') + $args) $out

Get-Content $out | Where-Object { $_ -match '^BENCH' }
if (Select-String -Path $log, $out -Pattern 'ERROR:|UNHANDLED_EXCEPTION' -Quiet) {
    Write-Host 'bench: error markers in log'
    if ($code -eq 0) { $code = 1 }
}
if ($code -ne 0) { Write-Host "bench: FAIL (exit code $code), log: $log"; exit $code }
Write-Host 'bench: OK'
