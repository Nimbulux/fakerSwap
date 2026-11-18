param(
    [string]$Game = $env:MAI_GAME_DIR,
    [switch]$NoCopy,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "FakerSwap"

Write-Host "== building FakerSwap ==" -ForegroundColor Cyan
& dotnet build (Join-Path $proj "FakerSwap.csproj") -c Release -v m
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$dll = Join-Path $root "Output\FakerSwap.dll"
if (-not (Test-Path $dll)) { throw "missing $dll" }

if (-not $NoCopy) {
    if (-not $Game) {
        Write-Host "未设置 MAI_GAME_DIR，跳过部署（只构建到 Output\）" -ForegroundColor Yellow
    } else {
        $mods = Join-Path $Game "Package\Mods"
        if (-not (Test-Path $mods)) { throw "找不到 $mods" }
        Copy-Item $dll (Join-Path $mods "FakerSwap.dll") -Force
        Write-Host "== copied to $mods ==" -ForegroundColor Green
    }
}

if ($Run -and $Game) {
    Write-Host "== launching game ==" -ForegroundColor Yellow
    Start-Process -FilePath (Join-Path $Game "启动.bat") -WorkingDirectory $Game
}
