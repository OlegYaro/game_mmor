# Windows variant of hook-build.sh (Claude Code PostToolUse hook): build after .cs edits, print only errors.
$raw = [Console]::In.ReadToEnd()
if ($raw -notmatch '"file_path"\s*:\s*"([^"]*\.cs)"') { exit 0 }
. "$PSScriptRoot/env.ps1"
Set-Location $Root
$out = dotnet build Game.sln --nologo -v q -clp:NoSummary 2>&1
if ($LASTEXITCODE -ne 0) {
    $out | Select-String -Pattern 'error [A-Z]+[0-9]+' | Select-Object -Unique -First 20 | ForEach-Object { [Console]::Error.WriteLine($_.Line) }
    exit 2
}
exit 0
