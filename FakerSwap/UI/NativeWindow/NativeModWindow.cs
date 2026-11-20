using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FakerSwap.Core;

namespace FakerSwap.UI.NativeWindow
{
    /// <summary>
    /// The standalone Win32 window.  It lives on its own background thread with its own message
    /// pump and paints with GDI; the game keeps running in parallel (the window is shown with
    /// SW_SHOWNOACTIVATE so it never steals focus or blocks the cabinet).
    ///
    /// The window thread must never touch a Unity / game object.  Everything it displays comes
    /// from <see cref="WindowState.Current"/> (an immutable snapshot published by the game thread)
    /// or from plain file IO (log tail, window bounds).
    /// </summary>
    internal static class NativeModWindow
    {
        private const uint WM_APP_SHOW = Win32.WM_APP + 1;
        private const uint WM_APP_SHUTDOWN = Win32.WM_APP + 2;
        private const uint WM_APP_TOPMOST = Win32.WM_APP + 3;

        private const string ClassName = "FakerSwapStandaloneWindow";
        private const string WindowTitleIdle = "FakerSwap · 账号转生控制台";
        private const string WindowTitleLocked = "FakerSwap · 占用机台中";

        // Minimum layout size (logical pixels).  Anything smaller used to clip the second column.
        private const int MinWidth = 900;
        private const int MinHeight = 700;
        private const int SaveBoundsTicks = 16; // ~2s at 120ms timer

        private static readonly object Sync = new object();

        /// <summary>FakerSwap/window.txt - resolved on every use so a late working-directory
        /// change cannot strand the file somewhere unexpected.</summary>
        private static string BoundsPath
        {
            get { return Path.Combine(Fs.Root, "window.txt"); }
        }

        private static Thread _thread;
        private static Win32.WndProcDelegate _wndProc;
        private static IntPtr _handle = IntPtr.Zero;
        private static NativeCanvas _canvas;
        private static IntPtr _classBrush = IntPtr.Zero;
        private static IntPtr _hInstance = IntPtr.Zero;
        private static double _scale = 1.0;
        private static bool _topMost;
        private static bool _positioned;
        private static bool _renderError;
        private static string _lastTitle = "";
        private static int _generation;
        private static int _timerTicks;
        private static uint _timerInterval = 120;
        private static string _lastSavedBounds = "";

        private static int _defaultX = 60;
        private static int _defaultY = 60;
        private static int _defaultW = 820;
        private static int _defaultH = 780;

        private static volatile bool _failed;
        private static volatile bool _ready;
        private static volatile bool _visible;
        private static volatile bool _showRequested;

        /// <summary>
        /// DPI awareness used by the window thread:
        ///   0 = UNAWARE (default; matches the game / most test tools),
        ///   1 = PER_MONITOR_AWARE_V2 (physical pixels, crisp but test tools may crop),
        ///   2 = leave the inherited process awareness alone.
        /// Kept internal so the screenshot harness can compare the three modes.
        /// </summary>
        internal static int DpiMode;

        public static bool Failed { get { return _failed; } }
        public static bool IsReady { get { return _ready && !_failed; } }
        public static bool IsVisible { get { return _visible; } }
        public static bool TopMost { get { return _topMost; } }

        /// <summary>Called on the game main thread (UIHost.Initialize).</summary>
        public static void Start(bool topMost, bool visible, int x, int y, int width, int height)
        {
            lock (Sync)
            {
                if (_thread != null) return;
                _topMost = topMost;
                _defaultX = x;
                _defaultY = y;
                _defaultW = width;
                _defaultH = height;
                _failed = false;
                _renderError = false;
                _lastTitle = "";
                _lastSavedBounds = "";
                _timerTicks = 0;
                _generation++;
                int generation = _generation;
                _showRequested = visible;
                _thread = new Thread(() => ThreadMain(generation))
                {
                    IsBackground = true,
                    Name = "FakerSwap.Window"
                };
                try { _thread.SetApartmentState(ApartmentState.STA); }
                catch (Exception) { }
                try
                {
                    _thread.Start();
                    Log.Info("standalone window: thread started");
                }
                catch (Exception e)
                {
                    _failed = true;
                    _thread = null;
                    Log.Error("standalone window: failed to start thread", e);
                }
            }
        }

        public static void SetVisible(bool show)
        {
            if (_showRequested == show) return;
            _showRequested = show;
            var handle = _handle;
            if (handle == IntPtr.Zero) return;
            Win32.PostMessageW(handle, WM_APP_SHOW, show ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
        }

        public static void ToggleVisible()
        {
            SetVisible(!_visible);
        }

        public static void SetTopMost(bool topMost)
        {
            _topMost = topMost;
            var handle = _handle;
            if (handle == IntPtr.Zero) return;
            Win32.PostMessageW(handle, WM_APP_TOPMOST, topMost ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Stop the thread and unregister the window class.  Safe to call twice.</summary>
        public static void Shutdown()
        {
            Thread thread;
            lock (Sync) thread = _thread;
            SaveBoundsIfChanged();
            try
            {
                var handle = _handle;
                if (handle != IntPtr.Zero) Win32.PostMessageW(handle, WM_APP_SHUTDOWN, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception) { }
            if (thread != null && thread.IsAlive)
            {
                try { thread.Join(1200); }
                catch (Exception) { }
            }
            lock (Sync)
            {
                _thread = null;
                _handle = IntPtr.Zero;
                _ready = false;
                _visible = false;
                _showRequested = false;
                _positioned = false;
                _failed = false;
                _renderError = false;
                _scale = 1.0;
                _lastTitle = "";
                _lastSavedBounds = "";
                _timerTicks = 0;
                _canvas = null;
            }
            try { NativeModContent.Release(); }
            catch (Exception) { }
            Log.Info("standalone window: shutdown");
        }

        // ---------------------------------------------------------------- window thread

        private static void ThreadMain(int generation)
        {
            try
            {
                MakeThreadDpiUnaware();
                _hInstance = Win32.GetModuleHandleW(null);
                _wndProc = WndProc;
                _classBrush = Win32.CreateSolidBrush(Theme.Background);

                var wc = default(Win32.WNDCLASSEXW);
                wc.cbSize = (uint)Marshal.SizeOf(typeof(Win32.WNDCLASSEXW));
                wc.style = 3u; // CS_HREDRAW | CS_VREDRAW
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
                wc.hInstance = _hInstance;
                wc.hCursor = Win32.LoadCursorW(IntPtr.Zero, (IntPtr)Win32.IDC_ARROW);
                wc.hIcon = Win32.LoadIconW(IntPtr.Zero, (IntPtr)Win32.IDC_ARROW);
                wc.hbrBackground = _classBrush;
                wc.lpszClassName = ClassName;

                if (Win32.RegisterClassExW(ref wc) == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Win32.ERROR_CLASS_ALREADY_EXISTS)
                    {
                        _failed = true;
                        Log.Error("standalone window: RegisterClassEx failed, error " + err);
                        return;
                    }
                    Log.Warn("standalone window: window class already registered, reusing it");
                }

                uint exStyle = Win32.WS_EX_APPWINDOW | (_topMost ? Win32.WS_EX_TOPMOST : 0u);
                _handle = Win32.CreateWindowExW(exStyle, ClassName, BuildTitle(), Win32.WS_OVERLAPPEDWINDOW,
                    Win32.CW_USEDEFAULT, Win32.CW_USEDEFAULT, 820, 780,
                    IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
                if (_handle == IntPtr.Zero)
                {
                    _failed = true;
                    Log.Error("standalone window: CreateWindowEx failed, error " + Marshal.GetLastWin32Error());
                    return;
                }

                _scale = GetDpiScale(_handle);
                _canvas = new NativeCanvas(_scale);
                ApplyBounds();
                Win32.SetTimer(_handle, (IntPtr)1, 120u, IntPtr.Zero);
                _ready = true;
                UpdateTitle(true);
                Log.Info("standalone window: created " + DescribeWindow() + ", scale=" + _scale.ToString("0.###"));
                if (_showRequested) ApplyShow(true);

                Win32.MSG msg;
                while (Win32.GetMessageW(out msg, IntPtr.Zero, 0u, 0u) > 0)
                {
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessageW(ref msg);
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Log.Error("standalone window: thread crashed", e);
            }
            finally
            {
                _ready = false;
                _visible = false;
                try
                {
                    if (_handle != IntPtr.Zero)
                    {
                        Win32.KillTimer(_handle, (IntPtr)1);
                        if (_canvas != null) _canvas.Dispose();
                        _canvas = null;
                        Win32.DestroyWindow(_handle);
                    }
                }
                catch (Exception) { }
                if (_classBrush != IntPtr.Zero)
                {
                    Win32.DeleteObject(_classBrush);
                    _classBrush = IntPtr.Zero;
                }
                try { Win32.UnregisterClassW(ClassName, _hInstance); }
                catch (Exception) { }
                lock (Sync)
                {
                    if (_generation == generation)
                    {
                        _handle = IntPtr.Zero;
                        _thread = null;
                        _positioned = false;
                    }
                }
            }
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_APP_SHOW:
                        ApplyShow(wParam != IntPtr.Zero);
                        return IntPtr.Zero;
                    case WM_APP_TOPMOST:
                        ApplyTopMost(wParam != IntPtr.Zero);
                        return IntPtr.Zero;
                    case WM_APP_SHUTDOWN:
                        SaveBoundsIfChanged();
                        Win32.DestroyWindow(hWnd);
                        return IntPtr.Zero;
                    case Win32.WM_CLOSE:
                        // Closing the window only hides it; the mod keeps running and F8 reopens it.
                        SaveBoundsIfChanged();
                        Win32.ShowWindow(hWnd, Win32.SW_HIDE);
                        _visible = false;
                        _showRequested = false;
                        return IntPtr.Zero;
                    case Win32.WM_ERASEBKGND:
                        return (IntPtr)1;
                    case Win32.WM_PAINT:
                        Paint(hWnd);
                        return IntPtr.Zero;
                    case Win32.WM_PRINT:
                    {
                        uint flags = (uint)lParam.ToInt64();
                        // Let the default proc paint the non-client frame first, then our client.
                        IntPtr result = Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
                        if ((flags & Win32.PRF_CLIENT) != 0)
                        {
                            int ox = 0;
                            int oy = 0;
                            if ((flags & Win32.PRF_NONCLIENT) != 0) GetClientOffset(hWnd, out ox, out oy);
                            RenderTo(hWnd, wParam, ox, oy, true);
                        }
                        return result;
                    }
                    case Win32.WM_PRINTCLIENT:
                        RenderTo(hWnd, wParam, 0, 0, true);
                        return IntPtr.Zero;
                    case Win32.WM_SIZE:
                        Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    case Win32.WM_TIMER:
                        UpdateTitle(false);
                        if (++_timerTicks >= SaveBoundsTicks || !File.Exists(BoundsPath))
                        {
                            _timerTicks = 0;
                            SaveBoundsIfChanged();
                        }
                        // Animations (About windmill) need ~60fps; idle only needs the 120ms refresh.
                        uint wanted = NativeModContent.WantsFastTicks ? 16u : 120u;
                        if (wanted != _timerInterval)
                        {
                            _timerInterval = wanted;
                            Win32.SetTimer(hWnd, (IntPtr)1, wanted, IntPtr.Zero);
                        }
                        if (_visible) Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    case Win32.WM_MOUSEMOVE:
                        if (_canvas != null) _canvas.OnMouseMove(Win32.GetXFromLParam(lParam), Win32.GetYFromLParam(lParam));
                        Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    case Win32.WM_LBUTTONDOWN:
                        if (_canvas != null) _canvas.OnMouseDown(Win32.GetXFromLParam(lParam), Win32.GetYFromLParam(lParam));
                        Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    case Win32.WM_LBUTTONUP:
                        if (_canvas != null) _canvas.OnMouseUp();
                        Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    case Win32.WM_MOUSEWHEEL:
                        if (_canvas != null)
                        {
                            short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                            _canvas.OnWheel(delta);
                        }
                        Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    case Win32.WM_CHAR:
                        if (_canvas != null) _canvas.OnChar((char)(wParam.ToInt64() & 0xFFFF));
                        Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    case Win32.WM_KEYDOWN:
                    {
                        int virtualKey = (int)(wParam.ToInt64() & 0xFFFF);
                        if (NativeModContent.HandleKey(virtualKey))
                        {
                            Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                            return IntPtr.Zero;
                        }
                        if (_canvas != null) _canvas.OnKeyDown(virtualKey);
                        Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    }
                    case Win32.WM_GETMINMAXINFO:
                    {
                        var info = (Win32.MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(Win32.MINMAXINFO));
                        info.ptMinTrackSize.x = (int)Math.Round(MinWidth * _scale);
                        info.ptMinTrackSize.y = (int)Math.Round(MinHeight * _scale);
                        Marshal.StructureToPtr(info, lParam, false);
                        return IntPtr.Zero;
                    }
                    case Win32.WM_DPICHANGED:
                    {
                        uint dpi = (uint)(wParam.ToInt64() & 0xFFFF);
                        if (dpi != 0)
                        {
                            _scale = dpi / 96.0;
                            if (_canvas != null) _canvas.UpdateScale(_scale);
                        }
                        var rect = (Win32.RECT)Marshal.PtrToStructure(lParam, typeof(Win32.RECT));
                        Win32.SetWindowPos(hWnd, IntPtr.Zero, rect.left, rect.top,
                            rect.right - rect.left, rect.bottom - rect.top, 20u);
                        Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    }
                    case Win32.WM_EXITSIZEMOVE:
                        SaveBoundsIfChanged();
                        return IntPtr.Zero;
                    case Win32.WM_DESTROY:
                        SaveBoundsIfChanged();
                        Win32.PostQuitMessage(0);
                        return IntPtr.Zero;
                }
            }
            catch (Exception e)
            {
                Log.Error("standalone window: message 0x" + msg.ToString("X") + " failed", e);
            }
            return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // ---------------------------------------------------------------- helpers

        private static string BuildTitle()
        {
            var snapshot = WindowState.Current;
            return snapshot != null && snapshot.LockActive ? WindowTitleLocked : WindowTitleIdle;
        }

        private static void UpdateTitle(bool force)
        {
            if (_handle == IntPtr.Zero) return;
            string title = BuildTitle();
            if (!force && title == _lastTitle) return;
            _lastTitle = title;
            Win32.SetWindowTextW(_handle, title);
        }

        private static void ApplyShow(bool show)
        {
            if (_handle == IntPtr.Zero) return;
            _showRequested = show;
            if (show)
            {
                if (!_positioned) ApplyBounds();
                UpdateTitle(true);
                Win32.ShowWindow(_handle, Win32.SW_SHOWNOACTIVATE);
                _visible = true;
                Win32.InvalidateRect(_handle, IntPtr.Zero, false);
            }
            else
            {
                SaveBoundsIfChanged();
                Win32.ShowWindow(_handle, Win32.SW_HIDE);
                _visible = false;
            }
        }

        private static void ApplyTopMost(bool topMost)
        {
            _topMost = topMost;
            if (_handle == IntPtr.Zero) return;
            Win32.SetWindowPos(_handle, topMost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST,
                0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }

        private static void Paint(IntPtr hWnd)
        {
            Win32.PAINTSTRUCT paint;
            Win32.BeginPaint(hWnd, out paint);
            try
            {
                RenderTo(hWnd, paint.hdc, 0, 0, false);
            }
            finally
            {
                Win32.EndPaint(hWnd, ref paint);
            }
        }

        /// <summary>
        /// Draws one frame into an arbitrary DC.  Used by WM_PAINT and by WM_PRINT /
        /// WM_PRINTCLIENT so PrintWindow (the test tool) captures the same pixels.
        /// </summary>
        private static void RenderTo(IntPtr hWnd, IntPtr targetDc, int offsetX, int offsetY, bool capture)
        {
            IntPtr memDc = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                Win32.RECT client;
                Win32.GetClientRect(hWnd, out client);
                int width = Math.Max(1, client.right - client.left);
                int height = Math.Max(1, client.bottom - client.top);
                memDc = Win32.CreateCompatibleDC(targetDc);
                bitmap = Win32.CreateCompatibleBitmap(targetDc, width, height);
                oldBitmap = Win32.SelectObject(memDc, bitmap);
                if (_canvas != null)
                {
                    _canvas.Dc = memDc;
                    _canvas.BeginFrame(width, height);
                    _canvas.Fill(0, 0, width, height, Theme.Background);
                    NativeModContent.Render(_canvas);
                    _canvas.EndFrame();
                    Win32.BitBlt(targetDc, offsetX, offsetY, width, height, memDc, 0, 0, Win32.ROP_SRCCOPY);
                    if (_canvas.ClickConsumed) Win32.InvalidateRect(hWnd, IntPtr.Zero, false);
                }
            }
            catch (Exception e)
            {
                if (!_renderError)
                {
                    _renderError = true;
                    Log.Error("standalone window: rendering failed (using in-game fallback panel)", e);
                }
                _failed = true;
                if (!capture)
                {
                    try
                    {
                        Win32.ShowWindow(hWnd, Win32.SW_HIDE);
                        _visible = false;
                        _showRequested = false;
                    }
                    catch (Exception) { }
                }
            }
            finally
            {
                try
                {
                    if (memDc != IntPtr.Zero)
                    {
                        if (oldBitmap != IntPtr.Zero) Win32.SelectObject(memDc, oldBitmap);
                        if (bitmap != IntPtr.Zero) Win32.DeleteObject(bitmap);
                        Win32.DeleteDC(memDc);
                    }
                }
                catch (Exception) { }
            }
        }

        private static void MakeThreadDpiUnaware()
        {
            try
            {
                IntPtr context;
                string name;
                if (DpiMode == 1)
                {
                    context = Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2;
                    name = "PER_MONITOR_AWARE_V2";
                }
                else if (DpiMode == 2)
                {
                    Log.Info("standalone window: keeping inherited DPI awareness");
                    return;
                }
                else if (DpiMode == 3)
                {
                    context = Win32.DPI_AWARENESS_CONTEXT_SYSTEM_AWARE;
                    name = "SYSTEM_AWARE";
                }
                else
                {
                    context = Win32.DPI_AWARENESS_CONTEXT_UNAWARE;
                    name = "UNAWARE";
                }
                IntPtr result = Win32.SetThreadDpiAwarenessContext(context);
                Log.Info("standalone window: thread DPI awareness -> " + name + " (ok=" + (result != IntPtr.Zero) + ")");
            }
            catch (Exception)
            {
                // Windows without SetThreadDpiAwarenessContext (< 10 1607): keep inherited awareness.
                Log.Info("standalone window: SetThreadDpiAwarenessContext unavailable, keeping inherited DPI awareness");
            }
        }

        private static void GetClientOffset(IntPtr hWnd, out int x, out int y)
        {
            x = 0;
            y = 0;
            try
            {
                var pt = default(Win32.POINT);
                if (!Win32.ClientToScreen(hWnd, ref pt)) return;
                Win32.RECT window;
                if (!Win32.GetWindowRect(hWnd, out window)) return;
                x = pt.x - window.left;
                y = pt.y - window.top;
                if (x < 0) x = 0;
                if (y < 0) y = 0;
            }
            catch (Exception) { }
        }

        private static string DescribeWindow()
        {
            try
            {
                Win32.RECT client;
                Win32.GetClientRect(_handle, out client);
                Win32.RECT window;
                Win32.GetWindowRect(_handle, out window);
                return "client " + (client.right - client.left) + "x" + (client.bottom - client.top) +
                       ", window " + window.left + "," + window.top + " " +
                       (window.right - window.left) + "x" + (window.bottom - window.top);
            }
            catch (Exception)
            {
                return "client ?";
            }
        }

        private static double GetDpiScale(IntPtr hWnd)
        {
            try
            {
                uint dpi = Win32.GetDpiForWindow(hWnd);
                if (dpi != 0) return dpi / 96.0;
            }
            catch (Exception) { }
            try
            {
                IntPtr dc = Win32.GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    int dpi = Win32.GetDeviceCaps(dc, Win32.LOGPIXELSX);
                    Win32.ReleaseDC(IntPtr.Zero, dc);
                    if (dpi > 0) return dpi / 96.0;
                }
            }
            catch (Exception) { }
            return 1.0;
        }

        private static void ApplyBounds()
        {
            if (_handle == IntPtr.Zero) return;

            int x, y, width, height;
            bool loaded = TryLoadBounds(out x, out y, out width, out height) && IsBoundsSane(x, y, width, height);
            if (!loaded)
            {
                // Config values are logical (96 DPI) pixels: the window thread is DPI-unaware so
                // Windows scales the whole window consistently with the rest of the desktop.
                width = (int)Math.Round(Math.Max(MinWidth, _defaultW) * _scale);
                height = (int)Math.Round(Math.Max(MinHeight, _defaultH) * _scale);
                x = (int)Math.Round(_defaultX * _scale);
                y = (int)Math.Round(_defaultY * _scale);
                int screenW = Win32.GetSystemMetrics(0);
                int screenH = Win32.GetSystemMetrics(1);
                if (x < 0) x = 40;
                if (y < 0) y = 40;
                if (screenW > 0 && x + width > screenW) x = Math.Max(0, screenW - width - 24);
                if (screenH > 0 && y + height > screenH) y = Math.Max(0, screenH - height - 60);
                Log.Info("standalone window: no usable " + BoundsPath +
                         ", falling back to config bounds " + x + "," + y + " " + width + "x" + height);
            }
            else
            {
                width = Math.Max(MinWidth, width);
                height = Math.Max(MinHeight, height);
                Log.Info("standalone window: bounds loaded from " + BoundsPath +
                         " -> " + x + "," + y + " " + width + "x" + height);
            }

            Win32.SetWindowPos(_handle, IntPtr.Zero, x, y, width, height, 20u);
            _positioned = true;
        }

        private static bool IsBoundsSane(int x, int y, int width, int height)
        {
            if (width < MinWidth || height < MinHeight) return false;
            if (x <= -30000 || y <= -30000 || x > 30000 || y > 30000) return false;

            int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
            int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
            int vw = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
            int vh = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) return true;

            const int margin = 80;
            return x + width > vx + margin && x < vx + vw - margin &&
                   y + height > vy + margin && y < vy + vh - margin;
        }

        private static bool TryLoadBounds(out int x, out int y, out int width, out int height)
        {
            x = 0;
            y = 0;
            width = 0;
            height = 0;
            try
            {
                if (!File.Exists(BoundsPath)) return false;
                foreach (string line in File.ReadAllLines(BoundsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    int value;
                    if (!int.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out value)) continue;
                    switch (key)
                    {
                        case "x": x = value; break;
                        case "y": y = value; break;
                        case "w": width = value; break;
                        case "h": height = value; break;
                    }
                }
                return width > 0 && height > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void SaveBoundsIfChanged()
        {
            var handle = _handle;
            if (handle == IntPtr.Zero) return;
            try
            {
                Win32.RECT rect;
                if (!Win32.GetWindowRect(handle, out rect)) return;
                int width = rect.right - rect.left;
                int height = rect.bottom - rect.top;
                if (width < 200 || height < 200) return;
                string text = "x=" + rect.left + Environment.NewLine +
                              "y=" + rect.top + Environment.NewLine +
                              "w=" + width + Environment.NewLine +
                              "h=" + height + Environment.NewLine;
                if (text == _lastSavedBounds && File.Exists(BoundsPath)) return;
                string dir = Path.GetDirectoryName(BoundsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(BoundsPath, text);
                _lastSavedBounds = text;
                Log.Info("standalone window: bounds saved to " + BoundsPath +
                         " (" + rect.left + "," + rect.top + " " + width + "x" + height + ")");
            }
            catch (Exception) { }
        }
    }
}
