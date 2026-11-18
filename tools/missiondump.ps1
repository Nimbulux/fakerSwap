param(
    [switch]$Watch,
    [string]$Game = $env:MAI_GAME_DIR,
    [string]$SourceFile = ""
)

# Triggers the FakerSwap "missiondump" command (diagnose area-mission chart detection).
# Usage:
#   .\tools\missiondump.ps1            # trigger now (game must be at music-select)
#   .\tools\missiondump.ps1 -Watch     # wait until the game reaches music-select

$ErrorActionPreference = "Continue"
if (-not $Game) { Write-Host "Set MAI_GAME_DIR (or pass -Game <game dir>)" -ForegroundColor Yellow; exit 1 }

$cmd = Join-Path $Game "Package\FakerSwap\command.txt"
$log = Join-Path $Game ("Package\FakerSwap\Logs\" + (Get-Date -Format 'yyyyMMdd') + ".log")
if (-not $SourceFile) {
    $srcDir = Join-Path $Game "Package\FakerSwap\Source"
    $first = Get-ChildItem $srcDir -Filter "*.json" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($first) { $SourceFile = $first.FullName }
}
if (-not $SourceFile) { Write-Host "No source file found (Package\FakerSwap\Source)" -ForegroundColor Yellow; exit 1 }

if ($Watch) {
    Write-Output "waiting for music-select..."
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        $tail = Get-Content $log -Encoding UTF8 -Tail 120 -ErrorAction SilentlyContinue
        if ($tail -match "进入选歌页" -and $tail -notmatch "还没选区") { break }
    }
}

# CommandFile only executes when the content changed, so include a timestamp.
$content = "source-file $SourceFile`nstart`n# dump $(Get-Date -Format 'HH:mm:ss.fff')`nmissiondump`n"
[System.IO.File]::WriteAllText($cmd, $content, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "missiondump command written, waiting 6s..."
Start-Sleep -Seconds 6

Get-Content $log -Encoding UTF8 |
    Select-String -Pattern "missiondump|选歌页分类诊断|_combine|hasCircleTaskMusic|待加入队列|还没选区" |
    Select-Object -Last 60
