using System;
using System.Collections.Generic;
using System.Text;

namespace FakerSwap.UI.NativeWindow
{
    /// <summary>
    /// GDI drawing surface used by the standalone window thread.  Ported from Sinmai-Assist's
    /// NativeCanvas and extended with a few controls (title font, progress bar, badges).
    /// Never touches Unity or game objects - it only paints into the memory DC.
    /// </summary>
    internal sealed class NativeCanvas
    {
        private readonly Dictionary<uint, IntPtr> _brushes = new Dictionary<uint, IntPtr>();
        private readonly Dictionary<uint, IntPtr> _pens = new Dictionary<uint, IntPtr>();

        private IntPtr _font;
        private IntPtr _fontBold;
        private IntPtr _fontSmall;
        private IntPtr _fontTitle;
        private double _scale = 1.0;

        public IntPtr Dc;
        public int Width;
        public int Height;
        public int ContentTop;
        public int ContentBottom;
        public int ContentHeight;
        public int ScrollY;
        public int MaxScroll;
        private bool _inContent;

        public int MouseX;
        public int MouseY;
        public bool MouseDown;
        public bool HasClick;
        public bool ClickConsumed;
        public int ClickX;
        public int ClickY;

        private int _wheelDelta;
        private int _focusId;
        private string _editText = string.Empty;
        private int _caret;
        private int _commitId;
        private string _commitText;
        private int _focusLeft;
        private int _focusTop;
        private int _focusRight;
        private int _focusBottom;

        public int FontHeight { get; private set; }

        public double Scale => _scale;
        public int Pad => S(14);
        public int LineH => S(22);
        public int CtrlH => S(28);
        public int Gap => S(6);

        public NativeCanvas(double dpiScale)
        {
            _scale = dpiScale <= 0.0 ? 1.0 : dpiScale;
            CreateFonts();
        }

        public int S(int value) => (int)Math.Round(value * _scale);

        public void UpdateScale(double dpiScale)
        {
            _scale = dpiScale <= 0.0 ? 1.0 : dpiScale;
            DeleteFonts();
            CreateFonts();
        }

        private void CreateFonts()
        {
            _font = CreateFont(-S(15), Win32.FW_NORMAL);
            _fontBold = CreateFont(-S(15), Win32.FW_BOLD);
            _fontSmall = CreateFont(-S(12), Win32.FW_NORMAL);
            _fontTitle = CreateFont(-S(20), Win32.FW_BOLD);
            FontHeight = S(18);
        }

        private static IntPtr CreateFont(int height, int weight)
        {
            string face = Fonts.Family;
            IntPtr h = Win32.CreateFontW(height, 0, 0, 0, weight, 0u, 0u, 0u,
                Win32.DEFAULT_CHARSET, 0u, 0u, Win32.CLEARTYPE_QUALITY, 0u, face);
            if (h == IntPtr.Zero) h = Win32.CreateFontW(height, 0, 0, 0, weight, 0u, 0u, 0u,
                Win32.DEFAULT_CHARSET, 0u, 0u, Win32.CLEARTYPE_QUALITY, 0u, "SimSun");
            if (h == IntPtr.Zero) h = Win32.CreateFontW(height, 0, 0, 0, weight, 0u, 0u, 0u,
                Win32.DEFAULT_CHARSET, 0u, 0u, Win32.CLEARTYPE_QUALITY, 0u, "Segoe UI");
            return h;
        }

        private void DeleteFonts()
        {
            if (_font != IntPtr.Zero) Win32.DeleteObject(_font);
            if (_fontBold != IntPtr.Zero) Win32.DeleteObject(_fontBold);
            if (_fontSmall != IntPtr.Zero) Win32.DeleteObject(_fontSmall);
            if (_fontTitle != IntPtr.Zero) Win32.DeleteObject(_fontTitle);
            _font = IntPtr.Zero;
            _fontBold = IntPtr.Zero;
            _fontSmall = IntPtr.Zero;
            _fontTitle = IntPtr.Zero;
        }

        private IntPtr GetBrush(uint color)
        {
            IntPtr value;
            if (_brushes.TryGetValue(color, out value)) return value;
            value = Win32.CreateSolidBrush(color);
            _brushes[color] = value;
            return value;
        }

        private IntPtr GetPen(uint color)
        {
            IntPtr value;
            if (_pens.TryGetValue(color, out value)) return value;
            value = Win32.CreatePen(0, 1, color);
            _pens[color] = value;
            return value;
        }

        public void Dispose()
        {
            DeleteFonts();
            foreach (var value in _brushes.Values) Win32.DeleteObject(value);
            foreach (var value in _pens.Values) Win32.DeleteObject(value);
            _brushes.Clear();
            _pens.Clear();
        }

        // ---------------------------------------------------------------- input

        public void OnMouseMove(int x, int y)
        {
            MouseX = x;
            MouseY = y;
        }

        public void OnMouseDown(int x, int y)
        {
            MouseX = x;
            MouseY = y;
            MouseDown = true;
            HasClick = true;
            ClickX = x;
            ClickY = y;
        }

        public void OnMouseUp()
        {
            MouseDown = false;
        }

        public void OnWheel(int delta)
        {
            _wheelDelta += delta;
        }

        public void OnChar(char c)
        {
            if (_focusId == 0 || char.IsControl(c) || _editText.Length >= 256) return;
            if (_caret < 0) _caret = 0;
            if (_caret > _editText.Length) _caret = _editText.Length;
            _editText = _editText.Insert(_caret, c.ToString());
            _caret++;
        }

        public void OnKeyDown(int virtualKey)
        {
            if (_focusId == 0) return;
            switch (virtualKey)
            {
                case Win32.VK_BACK:
                    if (_caret > 0 && _editText.Length > 0)
                    {
                        _editText = _editText.Remove(_caret - 1, 1);
                        _caret--;
                    }
                    break;
                case Win32.VK_DELETE:
                    if (_caret >= 0 && _caret < _editText.Length) _editText = _editText.Remove(_caret, 1);
                    break;
                case Win32.VK_LEFT:
                    if (_caret > 0) _caret--;
                    break;
                case Win32.VK_RIGHT:
                    if (_caret < _editText.Length) _caret++;
                    break;
                case Win32.VK_HOME:
                    _caret = 0;
                    break;
                case Win32.VK_END:
                    _caret = _editText.Length;
                    break;
                case Win32.VK_TAB:
                case Win32.VK_RETURN:
                case Win32.VK_ESCAPE:
                    CommitFocus();
                    break;
            }
        }

        public void CommitFocus()
        {
            if (_focusId == 0) return;
            _commitId = _focusId;
            _commitText = _editText;
            _focusId = 0;
            _editText = string.Empty;
            _caret = 0;
        }

        /// <summary>True while the given text field owns the keyboard focus.</summary>
        public bool IsFocus(int id)
        {
            return _focusId == id;
        }

        private bool IsInsideFocus(int x, int y)
        {
            return x >= _focusLeft && x < _focusRight && y >= _focusTop && y < _focusBottom;
        }

        // ---------------------------------------------------------------- frame / content

        public int ScreenY(int logicalY)
        {
            if (!_inContent) return logicalY;
            return ContentTop + logicalY - ScrollY;
        }

        /// <summary>内容坐标 → 客户区 Y（即使已经 EndContent 也按 scroll 偏移换算；弹层要用它）。</summary>
        public int ContentToScreenY(int logicalY)
        {
            return ContentTop + logicalY - ScrollY;
        }

        public void BeginFrame(int width, int height)
        {
            Width = width;
            Height = height;
            ClickConsumed = false;
            _inContent = false;
            if (_wheelDelta != 0)
            {
                int step = S(48);
                ScrollY -= _wheelDelta / 120 * step;
                _wheelDelta = 0;
            }
            if (ScrollY < 0) ScrollY = 0;
            if (ScrollY > MaxScroll) ScrollY = MaxScroll;
        }

        public void EndFrame()
        {
            if (HasClick) CommitFocus();
            HasClick = false;
        }

        public void BeginContent(int top, int bottom)
        {
            ContentTop = top;
            ContentBottom = bottom;
            _inContent = true;
            Win32.IntersectClipRect(Dc, 0, top, Math.Max(top, Width), bottom);
        }

        public void EndContent(int contentHeight)
        {
            _inContent = false;
            Win32.SelectClipRgn(Dc, IntPtr.Zero);
            ContentHeight = contentHeight;
            int view = ContentBottom - ContentTop;
            MaxScroll = Math.Max(0, contentHeight - view);
            if (ScrollY > MaxScroll) ScrollY = MaxScroll;
            if (ScrollY < 0) ScrollY = 0;
        }

        // ---------------------------------------------------------------- primitives

        public void Fill(int x, int y, int w, int h, uint color)
        {
            if (w <= 0 || h <= 0) return;
            int sy = ScreenY(y);
            if (_inContent && (sy >= ContentBottom || sy + h <= ContentTop)) return;
            var rect = default(Win32.RECT);
            rect.left = x;
            rect.top = sy;
            rect.right = x + w;
            rect.bottom = sy + h;
            Win32.FillRect(Dc, ref rect, GetBrush(color));
        }

        public void Border(int x, int y, int w, int h, uint color)
        {
            if (w <= 0 || h <= 0) return;
            int sy = ScreenY(y);
            if (_inContent && (sy >= ContentBottom || sy + h <= ContentTop)) return;
            var rect = default(Win32.RECT);
            rect.left = x;
            rect.top = sy;
            rect.right = x + w;
            rect.bottom = sy + h;
            Win32.FrameRect(Dc, ref rect, GetBrush(color));
        }

        public void Line(int x1, int y1, int x2, int y2, uint color)
        {
            IntPtr old = Win32.SelectObject(Dc, GetPen(color));
            Win32.MoveToEx(Dc, x1, ScreenY(y1), IntPtr.Zero);
            Win32.LineTo(Dc, x2, ScreenY(y2));
            Win32.SelectObject(Dc, old);
        }

        public void Separator(int x, int y, int w)
        {
            Fill(x, y, w, Math.Max(1, S(1)), Theme.Border);
        }

        private void DrawText(IntPtr font, string text, int x, int y, int w, int h, uint color, bool center)
        {
            if (w <= 0 || h <= 0) return;
            int sy = ScreenY(y);
            if (_inContent && (sy >= ContentBottom || sy + h <= ContentTop)) return;
            if (text == null) text = string.Empty;
            IntPtr old = Win32.SelectObject(Dc, font);
            Win32.SetTextColor(Dc, color);
            Win32.SetBkMode(Dc, 1);
            var rect = default(Win32.RECT);
            rect.left = x;
            rect.top = sy;
            rect.right = x + w;
            rect.bottom = sy + h;
            uint format = Win32.DT_LEFT | Win32.DT_SINGLELINE | Win32.DT_VCENTER |
                          Win32.DT_NOPREFIX | Win32.DT_END_ELLIPSIS;
            if (center) format |= Win32.DT_CENTER;
            Win32.DrawTextW(Dc, text, -1, ref rect, format);
            Win32.SelectObject(Dc, old);
        }

        public void Text(string text, int x, int y, int w, int h, uint color)
        {
            DrawText(_font, text, x, y, w, h, color, false);
        }

        public void Text(string text, int x, int y, int w, int h, uint color, bool center, bool bold)
        {
            DrawText(bold ? _fontBold : _font, text, x, y, w, h, color, center);
        }

        public void TextSmall(string text, int x, int y, int w, int h, uint color, bool bold = false)
        {
            DrawText(bold ? _fontBold : _fontSmall, text, x, y, w, h, color, false);
        }

        public void TextTitle(string text, int x, int y, int w, int h, uint color, bool center = false)
        {
            DrawText(_fontTitle, text, x, y, w, h, color, center);
        }

        public int MeasureText(string text, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            IntPtr old = Win32.SelectObject(Dc, bold ? _fontBold : _font);
            Win32.SIZE size;
            Win32.GetTextExtentPoint32W(Dc, text, text.Length, out size);
            Win32.SelectObject(Dc, old);
            return size.cx;
        }

        public int MeasureSmall(string text, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            IntPtr old = Win32.SelectObject(Dc, bold ? _fontBold : _fontSmall);
            Win32.SIZE size;
            Win32.GetTextExtentPoint32W(Dc, text, text.Length, out size);
            Win32.SelectObject(Dc, old);
            return size.cx;
        }

        /// <summary>Wraps text into the given width; returns the used height.</summary>
        public int TextWrap(string text, int x, int y, int width, uint color)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return 0;
            int lineHeight = FontHeight + S(4);
            var line = new StringBuilder();
            int lines = 0;
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    Text(line.ToString(), x, y + lines * lineHeight, width, lineHeight, color);
                    lines++;
                    line.Length = 0;
                    continue;
                }
                line.Append(ch);
                if (MeasureText(line.ToString(), false) > width)
                {
                    line.Length--;
                    Text(line.ToString(), x, y + lines * lineHeight, width, lineHeight, color);
                    lines++;
                    line.Length = 0;
                    line.Append(ch);
                }
            }
            if (line.Length > 0)
            {
                Text(line.ToString(), x, y + lines * lineHeight, width, lineHeight, color);
                lines++;
            }
            return lines * lineHeight;
        }

        public void ProgressBar(int x, int y, int w, int h, double ratio, uint fill, uint track)
        {
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            Fill(x, y, w, h, track);
            int innerW = Math.Max(1, w - S(4));
            int filled = (int)Math.Round(innerW * ratio);
            if (filled > 0) Fill(x + S(2), y + S(2), filled, Math.Max(1, h - S(4)), fill);
            Border(x, y, w, h, Theme.Border);
        }

        public void DrawScrollbar()
        {
            if (MaxScroll <= 0) return;
            int x = Width - S(8);
            int view = ContentBottom - ContentTop;
            if (view <= 0) return;
            Fill(x, ContentTop, S(5), view, Theme.PanelAlt);
            int total = Math.Max(view, ContentHeight);
            int thumb = Math.Max(S(24), view * view / total);
            int travel = view - thumb;
            int y = ContentTop + (MaxScroll > 0 ? ScrollY * travel / MaxScroll : 0);
            Fill(x, y, S(5), thumb, Theme.AccentDark);
        }

        // ---------------------------------------------------------------- controls

        private bool HitTest(int x, int y, int w, int h)
        {
            if (!HasClick) return false;
            int sy = ScreenY(y);
            if (ClickX < x || ClickX >= x + w) return false;
            if (ClickY < sy || ClickY >= sy + h) return false;
            if (_inContent && (ClickY < ContentTop || ClickY >= ContentBottom)) return false;
            HasClick = false;
            ClickConsumed = true;
            return true;
        }

        private bool IsHover(int x, int y, int w, int h)
        {
            int sy = ScreenY(y);
            return MouseX >= x && MouseX < x + w && MouseY >= sy && MouseY < sy + h;
        }

        /// <summary>Public hit-test used by list rows.</summary>
        public bool RowClick(int x, int y, int w, int h)
        {
            return HitTest(x, y, w, h);
        }

        /// <summary>Public hover-test used by list rows.</summary>
        public bool HoverAt(int x, int y, int w, int h)
        {
            return IsHover(x, y, w, h);
        }

        public bool Button(string label, int x, int y, int w, int h)
        {
            return Button(label, x, y, w, h, false);
        }

        public bool Button(string label, int x, int y, int w, int h, bool primary)
        {
            return Button(label, x, y, w, h, primary, true);
        }

        public bool Button(string label, int x, int y, int w, int h, bool primary, bool enabled)
        {
            bool hover = enabled && IsHover(x, y, w, h);
            uint fill;
            uint border;
            uint text;
            if (!enabled)
            {
                fill = Theme.PanelAlt;
                border = Theme.Disabled;
                text = Theme.Muted;
            }
            else if (primary)
            {
                fill = hover ? Theme.Accent : Theme.AccentDark;
                border = Theme.Accent;
                text = Theme.Text;
            }
            else
            {
                fill = hover ? Theme.Panel : Theme.PanelAlt;
                border = hover ? Theme.AccentDark : Theme.Border;
                text = Theme.Text;
            }
            Fill(x, y, w, h, fill);
            Border(x, y, w, h, border);
            Text(label, x + S(4), y, w - S(8), h, text, true, primary);
            bool clicked = enabled && HitTest(x, y, w, h);
            if (clicked) CommitFocus();
            return clicked;
        }

        /// <summary>Danger-styled button (abort).</summary>
        public bool DangerButton(string label, int x, int y, int w, int h, bool enabled)
        {
            bool hover = enabled && IsHover(x, y, w, h);
            Fill(x, y, w, h, !enabled ? Theme.PanelAlt : (hover ? Theme.Bad : Theme.BadDark));
            Border(x, y, w, h, enabled ? Theme.Bad : Theme.Disabled);
            Text(label, x + S(4), y, w - S(8), h, enabled ? Theme.Text : Theme.Muted, true, true);
            bool clicked = enabled && HitTest(x, y, w, h);
            if (clicked) CommitFocus();
            return clicked;
        }

        public bool Toggle(bool value, string label, int x, int y, int w, int h)
        {
            int box = S(16);
            int by = y + (h - box) / 2;
            Fill(x, by, box, box, value ? Theme.AccentDark : Theme.Input);
            Border(x, by, box, box, value ? Theme.Accent : Theme.Border);
            if (value)
            {
                IntPtr old = Win32.SelectObject(Dc, GetPen(Theme.Text));
                int sy = ScreenY(by);
                Win32.MoveToEx(Dc, x + S(4), sy + box / 2, IntPtr.Zero);
                Win32.LineTo(Dc, x + box / 2, sy + box - S(4));
                Win32.LineTo(Dc, x + box - S(4), sy + S(4));
                Win32.SelectObject(Dc, old);
            }
            Text(label, x + box + S(8), y, w - box - S(8), h, Theme.Text);
            bool clicked = HitTest(x, y, w, h);
            if (clicked) CommitFocus();
            return clicked ? !value : value;
        }

        /// <summary>Draws only the checkbox square (used by list rows that handle their own click).</summary>
        public void CheckMark(bool value, int x, int y, int size)
        {
            if (size <= 0) return;
            Fill(x, y, size, size, value ? Theme.AccentDark : Theme.Input);
            Border(x, y, size, size, value ? Theme.Accent : Theme.Border);
            if (!value) return;
            IntPtr old = Win32.SelectObject(Dc, GetPen(Theme.Text));
            int sy = ScreenY(y);
            Win32.MoveToEx(Dc, x + S(4), sy + size / 2, IntPtr.Zero);
            Win32.LineTo(Dc, x + size / 2, sy + size - S(3));
            Win32.LineTo(Dc, x + size - S(3), sy + S(3));
            Win32.SelectObject(Dc, old);
        }

        public string TextField(string value, int id, int x, int y, int w, int h)
        {
            int sy = ScreenY(y);
            if (HasClick && _focusId != 0 && _focusId != id && !IsInsideFocus(ClickX, ClickY)) CommitFocus();
            if (HitTest(x, y, w, h))
            {
                CommitFocus();
                _focusId = id;
                _editText = value ?? string.Empty;
                _caret = _editText.Length;
                _focusLeft = x;
                _focusTop = sy;
                _focusRight = x + w;
                _focusBottom = sy + h;
            }
            Fill(x, y, w, h, Theme.Input);
            Border(x, y, w, h, _focusId == id ? Theme.Accent : Theme.Border);
            string text = value ?? string.Empty;
            int caret = -1;
            if (_focusId == id)
            {
                text = _editText;
                caret = _caret;
            }
            else if (_commitId == id)
            {
                text = _commitText ?? string.Empty;
                _commitId = 0;
                _commitText = null;
            }
            Text(text, x + S(6), y, w - S(12), h, Theme.Text);
            if (caret >= 0)
            {
                string head = caret <= 0 ? string.Empty : text.Substring(0, Math.Min(caret, text.Length));
                int cx = x + S(6) + MeasureText(head, false);
                if (cx > x + w - S(4)) cx = x + w - S(4);
                Fill(cx, y + S(4), Math.Max(1, S(1)), h - S(8), Theme.Text);
            }
            return text;
        }
    }
}
