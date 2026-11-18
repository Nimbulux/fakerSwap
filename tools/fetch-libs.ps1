param(
    [string]$Game = $env:MAI_GAME_DIR
)

$ErrorActionPreference = "Stop"
if (-not $Game) {
    Write-Host "需要游戏目录：设置环境变量 MAI_GAME_DIR，或传 -Game <游戏目录>" -ForegroundColor Yellow
    Write-Host "例如: `$env:MAI_GAME_DIR = 'D:\maimai\SDEZ_1.65.00'" -ForegroundColor Yellow
    exit 1
}

$managed = Join-Path $Game "Package\Sinmai_Data\Managed"
$melon = Join-Path $Game "Package\MelonLoader\net35"
$libs = Join-Path $PSScriptRoot "..\FakerSwap\Libs"

if (-not (Test-Path $managed)) { throw "找不到 $managed（确认游戏目录和 MelonLoader 安装）" }
if (-not (Test-Path $melon)) { throw "找不到 $melon（MelonLoader net35 不存在）" }

New-Item -ItemType Directory -Force -Path $libs | Out-Null

$copied = 0
foreach ($f in Get-ChildItem $managed -Filter "*.dll") {
    Copy-Item $f.FullName (Join-Path $libs $f.Name) -Force
    $copied++
}
foreach ($name in @("MelonLoader.dll", "0Harmony.dll")) {
    $src = Join-Path $melon $name
    if (-not (Test-Path $src)) { throw "缺少 $src" }
    Copy-Item $src (Join-Path $libs $name) -Force
    $copied++
}
Write-Host "已复制 $copied 个引用 DLL 到 $libs（这些 DLL 不入库，仅本地编译用）" -ForegroundColor Green
