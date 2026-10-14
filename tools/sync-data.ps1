# Copies data/ into src/Game/data/ (gitignored, ADR 0002). Only *.json files are copied.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
$src = Join-Path $Root 'data'
$dst = Join-Path $Root 'src/Game/data'
if (-not (Test-Path $src)) { throw "sync-data: $src not found" }
if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
New-Item -ItemType Directory -Force -Path $dst | Out-Null
$count = 0
Get-ChildItem -Path $src -Recurse -File -Filter '*.json' | ForEach-Object {
    $rel = [IO.Path]::GetRelativePath($src, $_.FullName)
    $target = Join-Path $dst $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item $_.FullName $target
    $count++
}
Write-Host "sync-data: $count file(s) -> src/Game/data"

# locale/ -> src/Game/locale/ (gitignored)
$lsrc = Join-Path $Root 'locale'
$ldst = Join-Path $Root 'src/Game/locale'
if (Test-Path $ldst) { Remove-Item -Recurse -Force $ldst }
if (Test-Path $lsrc) {
    New-Item -ItemType Directory -Force -Path $ldst | Out-Null
    $lcount = 0
    Get-ChildItem -Path $lsrc -Recurse -File -Filter '*.csv' | ForEach-Object {
        $rel = [IO.Path]::GetRelativePath($lsrc, $_.FullName)
        $target = Join-Path $ldst $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item $_.FullName $target
        $lcount++
    }
    Write-Host "sync-data: $lcount file(s) -> src/Game/locale"
}
