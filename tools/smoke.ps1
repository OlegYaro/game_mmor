# Headless smoke test (ADR 0011). Passes only if: exit code 0, log has SMOKE_OK,
# log has no "ERROR:" and no UNHANDLED_EXCEPTION. Extra args are passed to the game after "--".
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
Set-Location $Root

$limit = if ($env:SMOKE_TIMEOUT) { [int]$env:SMOKE_TIMEOUT } else { 60 }
$logDir = Join-Path $T 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = if ($env:SMOKE_LOG) { $env:SMOKE_LOG } else { Join-Path $logDir 'smoke.log' }
$out = Join-Path $logDir 'smoke.out'
Remove-Item -Force -ErrorAction SilentlyContinue $log, $out

& "$PSScriptRoot/sync-data.ps1"

Write-Host 'smoke: dotnet build'
dotnet build Game.sln -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { exit 1 }

function Start-Limited([string[]]$GodotArgs, [string]$StdOut) {
    $oldHome = $env:HOME; $oldAppData = $env:APPDATA; $oldUser = $env:USERPROFILE
    $env:HOME = $GodotHome; $env:APPDATA = $GodotHome; $env:USERPROFILE = $GodotHome
    try {
        $p = Start-Process -FilePath $Godot -ArgumentList $GodotArgs -NoNewWindow -PassThru -RedirectStandardOutput $StdOut
        $null = $p.Handle  # cache the handle, otherwise ExitCode can be $null
        if (-not $p.WaitForExit($limit * 1000)) {
            $p.Kill($true)
            Write-Host "smoke: timeout after ${limit}s"
            return 124
        }
        $p.WaitForExit()
        return [int]$p.ExitCode
    } finally { $env:HOME = $oldHome; $env:APPDATA = $oldAppData; $env:USERPROFILE = $oldUser }
}

Write-Host 'smoke: godot --import'
$null = Start-Limited @('--headless', '--path', 'src/Game', '--import') (Join-Path $logDir 'import.out')

Write-Host "smoke: run (limit ${limit}s)"
$code = Start-Limited (@('--headless', '--path', 'src/Game', '--log-file', $log, '--', '--smoke') + $args) $out

$text = ''
foreach ($f in @($log, $out)) { if (Test-Path $f) { $text += (Get-Content -Raw $f) } }
$fail = $false
if ($code -ne 0) { Write-Host "smoke: FAIL exit code $code"; $fail = $true }
if ($text -notmatch 'SMOKE_OK') { Write-Host 'smoke: FAIL SMOKE_OK not found'; $fail = $true }
if ($text -match 'ERROR:|UNHANDLED_EXCEPTION') { Write-Host 'smoke: FAIL error markers in log'; $fail = $true }
if ($fail) {
    if (Test-Path $log) { Get-Content $log -Tail 30 }
    exit 1
}
Write-Host 'smoke: OK'
