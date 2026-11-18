param(
    [string]$Type,
    [switch]$Search,
    [switch]$Fields,
    [switch]$Props,
    [switch]$Methods,
    [switch]$Base,
    [string]$Assembly = $(if ($env:MAI_GAME_DIR) { Join-Path $env:MAI_GAME_DIR "Package\Sinmai_Data\Managed\Assembly-CSharp.dll" } else { "" })
)

$ErrorActionPreference = "Stop"
if (-not $Assembly) {
    Write-Host "Set MAI_GAME_DIR (game folder) or pass -Assembly <path to Assembly-CSharp.dll>" -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path $Assembly)) { throw "not found: $Assembly" }

$cecil = Join-Path $PSScriptRoot "..\AquaMai\Libs\Mono.Cecil.dll"
if (Test-Path $cecil) { Add-Type -Path $cecil }
else { throw "Mono.Cecil not found at $cecil" }

$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Assembly)

function ShowType($t, [int]$depth) {
    if ($null -eq $t) { return }
    $pad = "".PadLeft($depth * 2)
    $kind = "class"
    if ($t.IsInterface) { $kind = "interface" } elseif ($t.IsEnum) { $kind = "enum" }
    Write-Output "$pad== $($t.FullName) ($kind)"
    if ($Fields) {
        foreach ($f in $t.Fields) {
            if ($f.Name -eq "value__") { continue }
            $vis = "f*"
            if ($f.IsPublic) { $vis = "f+" } elseif ($f.IsPrivate) { $vis = "f-" }
            Write-Output "$pad   $vis $($f.FieldType.Name) $($f.Name)"
        }
    }
    if ($Props) {
        foreach ($p in $t.Properties) { Write-Output "$pad   p $($p.PropertyType.Name) $($p.Name)" }
    }
    if ($Methods) {
        foreach ($m in $t.Methods) {
            $ps = @()
            foreach ($p in $m.Parameters) { $ps += ($p.ParameterType.Name + " " + $p.Name) }
            Write-Output "$pad   m $($m.ReturnType.Name) $($m.Name)($($ps -join ', '))"
        }
    }
    if ($Base -and $t.BaseType) { ShowType ($t.BaseType.Resolve()) ($depth + 1) }
}

if ($Search) {
    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($t in $asm.MainModule.GetTypes()) {
        if ($t.FullName -like "*$Type*") { $hits.Add($t.FullName) }
    }
    $hits | Sort-Object -Unique | Select-Object -First 80
    exit 0
}

$found = $null
foreach ($t in $asm.MainModule.GetTypes()) {
    if ($t.FullName -eq $Type) { $found = $t; break }
}
if (-not $found) { Write-Output "NOT FOUND: $Type"; exit 1 }
ShowType $found 0
