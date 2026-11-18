param(
    [string]$Action = "rect",
    [int]$X = 0,
    [int]$Y = 0,
    [int]$Clicks = 1,
    [string]$Out = ""
)

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class UIW {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public struct POINT { public int X, Y; }
  public static void MsgClick(int sx, int sy) { var p = new POINT { X = sx, Y = sy }; ScreenToClient(Found, ref p); IntPtr lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)); SendMessage(Found, 0x0200, IntPtr.Zero, lp); System.Threading.Thread.Sleep(60); SendMessage(Found, 0x0201, (IntPtr)1, lp); System.Threading.Thread.Sleep(80); SendMessage(Found, 0x0202, IntPtr.Zero, lp); }
  public static void MsgWheel(int sx, int sy, int n) { IntPtr lp = (IntPtr)((sy << 16) | (sx & 0xFFFF)); for (int i=0;i<n;i++){ SendMessage(Found, 0x020A, (IntPtr)(unchecked((int)(-120)) << 16), lp); System.Threading.Thread.Sleep(70);} }
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr Found = IntPtr.Zero;
  public static string Find(string part) {
    Found = IntPtr.Zero;
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      var sb = new StringBuilder(512);
      GetWindowText(h, sb, sb.Capacity);
      if (sb.ToString().IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) { Found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return Found == IntPtr.Zero ? "" : (Found.ToInt64().ToString());
  }
  public static string RectStr() { RECT r; GetWindowRect(Found, out r); return r.Left + "," + r.Top + "," + (r.Right-r.Left) + "x" + (r.Bottom-r.Top); }
  public static void Focus() { ShowWindow(Found, 9); SetWindowPos(Found, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0040); System.Threading.Thread.Sleep(120); SetWindowPos(Found, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0040); BringWindowToTop(Found); SetForegroundWindow(Found); System.Threading.Thread.Sleep(250); }
  public static void MinConsoles() { EnumWindows((h, l) => { if (!IsWindowVisible(h)) return true; var sb = new StringBuilder(64); GetClassName(h, sb, sb.Capacity); if (sb.ToString() == "ConsoleWindowClass") ShowWindow(h, 6); return true; }, IntPtr.Zero); }
  public static void Click(int x, int y) { Focus(); SetCursorPos(x,y); System.Threading.Thread.Sleep(150); mouse_event(0x0002,0,0,0,UIntPtr.Zero); System.Threading.Thread.Sleep(80); mouse_event(0x0004,0,0,0,UIntPtr.Zero); }
  public static void Wheel(int x, int y, int n) { Focus(); SetCursorPos(x,y); System.Threading.Thread.Sleep(150); for (int i=0;i<n;i++){ mouse_event(0x0800,0,0,unchecked((uint)-120),UIntPtr.Zero); System.Threading.Thread.Sleep(90);} }
}
'@
[void][UIW]::SetProcessDPIAware()
$hwnd = [UIW]::Find('FakerSwap')
if ($hwnd -eq '') { "window not found"; exit 1 }
switch ($Action) {
    "rect" { "hwnd=$hwnd rect=$([UIW]::RectStr())" }
    "focus" { [UIW]::MinConsoles(); [UIW]::Focus(); "focused (consoles minimized)" }
    "click" { [UIW]::Click($X, $Y); "clicked $X,$Y" }
    "msgclick" { [UIW]::MsgClick($X, $Y); "msgclick $X,$Y" }
    "msgwheel" { [UIW]::MsgWheel($X, $Y, $Clicks); "msgwheel $Clicks at $X,$Y" }
    "wheel" { [UIW]::Wheel($X, $Y, $Clicks); "wheel $Clicks at $X,$Y" }
    "shot" {
        if (-not $Out) { $Out = "screens\ui.png" }
        & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\game.ps1" shot -Out $Out
    }
    default { "unknown action" }
}
