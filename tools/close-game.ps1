param([int]$WaitSec = 0)

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class GameWin {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);

  public static IntPtr FindUnityWindow(out string info) {
    IntPtr found = IntPtr.Zero;
    var lines = new List<string>();
    EnumWindows((h,l) => {
      var c = new StringBuilder(256); GetClassNameW(h, c, 256);
      if (!IsWindowVisible(h)) return true;
      if (c.ToString() == "UnityWndClass") {
        var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
        lines.Add(h + "|" + t);
        if (found == IntPtr.Zero) found = h;
      }
      return true;
    }, IntPtr.Zero);
    info = string.Join("; ", lines.ToArray());
    return found;
  }

  public static void Esc(IntPtr h) {
    SetForegroundWindow(h);
    System.Threading.Thread.Sleep(250);
    keybd_event(0x1B, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(100);
    keybd_event(0x1B, 0, 2, UIntPtr.Zero);
  }

  public static void CloseWindow(IntPtr h) {
    // Graceful WM_CLOSE: Unity runs its normal quit path (OnApplicationQuit -> mod shutdown).
    PostMessageW(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
  }
}
'@

$p = Get-Process Sinmai -ErrorAction SilentlyContinue
if (-not $p) {
    Write-Output "game not running"
    exit 0
}
$info = ""
$h = [GameWin]::FindUnityWindow([ref]$info)
Write-Output "pid=$($p.Id) windows=[$info]"
if ($h -eq [IntPtr]::Zero) {
    Write-Output "UnityWndClass window not found; trying MainWindowHandle"
    $h = $p.MainWindowHandle
}
if ($h -eq [IntPtr]::Zero) {
    Write-Output "no window to target"
    exit 1
}
[GameWin]::Esc($h)
Write-Output "ESC sent to $h, waiting..."
for ($i = 0; $i -lt 8; $i++) {
    Start-Sleep -Milliseconds 500
    if (-not (Get-Process Sinmai -ErrorAction SilentlyContinue)) {
        Write-Output "game closed (ESC)"
        exit 0
    }
}
Write-Output "ESC ignored -> sending WM_CLOSE (graceful quit)"
[GameWin]::CloseWindow($h)
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Milliseconds 500
    if (-not (Get-Process Sinmai -ErrorAction SilentlyContinue)) {
        Write-Output "game closed (WM_CLOSE)"
        exit 0
    }
}
Write-Output "STILL RUNNING (send ESC again or close manually)"
exit 1
