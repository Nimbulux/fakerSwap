param(
    [ValidateSet("shot", "start", "stop", "status", "log")]
    [string]$Action = "status",
    [string]$Out = "shot.png",
    [int]$WaitSec = 0,
    [string]$Game = $env:MAI_GAME_DIR
)

$ErrorActionPreference = "Continue"

function Shot([string]$path) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $b.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "saved $path ($($b.Width)x$($b.Height))"
}

switch ($Action) {
    "shot"   { if ($WaitSec) { Start-Sleep -Seconds $WaitSec }; Shot $Out }
    "start"  {
        if (Get-Process sinmai -ErrorAction SilentlyContinue) { Write-Output "already running"; break }
        if (-not $Game) { Write-Output "set MAI_GAME_DIR (or -Game) to launch the game"; break }
        Start-Process -FilePath "cmd.exe" -ArgumentList '/c', "`"$Game\启动.bat`"" -WorkingDirectory $Game -WindowStyle Minimized
        Write-Output "launching..."
        if ($WaitSec) { Start-Sleep -Seconds $WaitSec }
        Get-Process sinmai, amdaemon -ErrorAction SilentlyContinue | Select-Object Id, ProcessName | Format-Table -AutoSize
    }
    "stop"   {
        cmd /c "taskkill /f /im Sinmai.exe" 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        if (Get-Process sinmai -ErrorAction SilentlyContinue) { Write-Output "STILL RUNNING (protected by driver - close it manually)" }
        else { Write-Output "stopped" }
    }
    "status" {
        $p = Get-Process sinmai -ErrorAction SilentlyContinue
        if ($p) { Write-Output "GAME RUNNING pid=$($p.Id) title=$($p.MainWindowTitle)" } else { Write-Output "GAME CLOSED" }
    }
    "log"    {
        if (-not $Game) { Write-Output "set MAI_GAME_DIR to read logs"; break }
        $f = "$Game\Package\FakerSwap\Logs\$(Get-Date -Format 'yyyyMMdd').log"
        if (Test-Path $f) { Get-Content $f -Tail 60 } else { Write-Output "no log yet: $f" }
    }
}
