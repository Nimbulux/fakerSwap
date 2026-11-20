using System;
using System.Runtime.InteropServices;

namespace FakerSwap.UI.NativeWindow
{
    /// <summary>
    /// Minimal raw Win32 surface used by the standalone window.  Ported from
    /// Sinmai-Assist/GUI/NativeWindow (same environment: Unity 2018.4 Mono net35).
    /// </summary>
    internal static class Win32
    {
        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        public struct POINT
        {
            public int x;
            public int y;
        }

        public struct SIZE
        {
            public int cx;
            public int cy;
        }

        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        public struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        // ---- window styles / messages -------------------------------------------------
        public const uint WS_OVERLAPPEDWINDOW = 13565952u;
        public const uint WS_EX_APPWINDOW = 262144u;
        public const uint WS_EX_TOPMOST = 8u;
        public const uint WS_EX_TOOLWINDOW = 128u;

        public const int CW_USEDEFAULT = int.MinValue;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWNOACTIVATE = 4;

        public const uint WM_DESTROY = 2u;
        public const uint WM_SIZE = 5u;
        public const uint WM_PAINT = 15u;
        public const uint WM_CLOSE = 16u;
        public const uint WM_ERASEBKGND = 20u;
        public const uint WM_GETMINMAXINFO = 36u;
        public const uint WM_KEYDOWN = 256u;
        public const uint WM_CHAR = 258u;
        public const uint WM_MOUSEMOVE = 512u;
        public const uint WM_LBUTTONDOWN = 513u;
        public const uint WM_LBUTTONUP = 514u;
        public const uint WM_MOUSEWHEEL = 522u;
        public const uint WM_EXITSIZEMOVE = 562u;
        public const uint WM_TIMER = 275u;
        public const uint WM_APP = 32768u;
        public const uint WM_DPICHANGED = 736u;
        public const uint WM_PRINT = 0x0317u;
        public const uint WM_PRINTCLIENT = 0x0318u;

        // WM_PRINT flags
        public const uint PRF_ERASEBKGND = 0x0002u;
        public const uint PRF_CLIENT = 0x0004u;
        public const uint PRF_NONCLIENT = 0x0008u;
        public const uint PRF_CHILDREN = 0x0010u;

        // DPI awareness contexts (SetThreadDpiAwarenessContext, Win10 1607+)
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = (IntPtr)(-1);
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = (IntPtr)(-2);
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = (IntPtr)(-3);
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

        public const uint ROP_SRCCOPY = 13369376u;
        public const int LOGPIXELSX = 88;
        public const int FW_NORMAL = 400;
        public const int FW_BOLD = 700;
        public const int DEFAULT_CHARSET = 1;
        public const int CLEARTYPE_QUALITY = 5;

        // ---- SetWindowPos -----------------------------------------------------------------
        public static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
        public static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        // ---- DrawText ---------------------------------------------------------------------
        public const uint DT_LEFT = 0u;
        public const uint DT_CENTER = 1u;
        public const uint DT_RIGHT = 2u;
        public const uint DT_VCENTER = 4u;
        public const uint DT_SINGLELINE = 32u;
        public const uint DT_END_ELLIPSIS = 32768u;
        public const uint DT_NOPREFIX = 2048u;

        // ---- keys -------------------------------------------------------------------------
        public const int VK_BACK = 8;
        public const int VK_TAB = 9;
        public const int VK_RETURN = 13;
        public const int VK_ESCAPE = 27;
        public const int VK_END = 35;
        public const int VK_HOME = 36;
        public const int VK_LEFT = 37;
        public const int VK_RIGHT = 39;
        public const int VK_DELETE = 46;

        // ---- system metrics / cursor ids ----------------------------------------------------
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;
        public const int IDC_ARROW = 32512;
        public const int ERROR_CLASS_ALREADY_EXISTS = 1410;

        // ---- user32 -------------------------------------------------------------------------
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DispatchMessageW(ref MSG lpmsg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll")]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern int FillRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll")]
        public static extern int FrameRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint uFormat);

        // ---- kernel32 ---------------------------------------------------------------------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string lpModuleName);

        // ---- gdi32 ------------------------------------------------------------------------
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation,
            int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision,
            uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreatePen(int fnPenStyle, int nWidth, uint color);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, uint rop);

        [DllImport("gdi32.dll")]
        public static extern int SetTextColor(IntPtr hdc, uint color);

        [DllImport("gdi32.dll")]
        public static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int cchString, out SIZE psizl);

        [DllImport("gdi32.dll")]
        public static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        public static extern int SelectClipRgn(IntPtr hdc, IntPtr hrgn);

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int index);

        [DllImport("gdi32.dll")]
        public static extern int MoveToEx(IntPtr hdc, int x, int y, IntPtr lppt);

        [DllImport("gdi32.dll")]
        public static extern int LineTo(IntPtr hdc, int x, int y);

        /// <summary>Filled polygon (uses the currently selected brush for the fill and pen for the outline).</summary>
        [DllImport("gdi32.dll")]
        public static extern bool Polygon(IntPtr hdc, POINT[] lpPoints, int nCount);

        [DllImport("gdi32.dll")]
        public static extern bool SetViewportOrgEx(IntPtr hdc, int x, int y, IntPtr lpPoint);

        // ---- helpers ----------------------------------------------------------------------
        /// <summary>COLORREF: 0x00BBGGRR.</summary>
        public static uint Rgb(int r, int g, int b)
        {
            return (uint)(((b & 0xFF) << 16) | ((g & 0xFF) << 8)) | ((uint)r & 0xFFu);
        }

        public static int GetXFromLParam(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
        public static int GetYFromLParam(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);
    }
}
