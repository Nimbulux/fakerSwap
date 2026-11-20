using System;
using System.Globalization;
using System.IO;
using FakerSwap.Core;
using FakerSwap.UI;

namespace FakerSwap.UI.NativeWindow
{
    /// <summary>
    /// "关于" panel for the home page, plus the hidden "神秘菜单" it unlocks.
    ///
    /// The little windmill icon in the header spins by 90° on every click; the accumulated angle is
    /// persisted, and once it reaches 360° (4 clicks) the secret menu page becomes available.
    ///
    /// Window thread only: all state lives in static fields and persistence goes to
    /// <c>FakerSwap/ui-state.txt</c>.  Never touch config.json from here - that file belongs to the
    /// game thread (flow/settings) and is rewritten wholesale.
    /// </summary>
    internal static class AboutPanel
    {
        private const int SpinStepDeg = 90;
        private const int SpinFullDeg = 360;
        private const int SpinMs = 300;

        private static readonly string[] TabNames = { "总览", "曲库", "区域", "收藏", "票券", "工具" };

        // persisted
        private static bool _expanded;
        private static bool _unlocked;
        private static int _deg;
        private static bool _loaded;

        // transient (window thread only)
        private static int _fromDeg;
        private static int _animStart;
        private static bool _animating;
        private static bool _resetScroll;
        private static int _tab;
        private static string _statePath;

        /// <summary>True while the secret-menu page is replacing the home page.</summary>
        public static bool SecretOpen { get; private set; }

        /// <summary>True while the windmill animation needs a faster repaint timer.</summary>
        public static bool Animating { get { return _animating; } }

        /// <summary>Number of completed 90° steps shown to the operator (0..4).</summary>
        private static int Steps
        {
            get
            {
                int steps = _deg / SpinStepDeg;
                return steps < 0 ? 0 : (steps > 4 ? 4 : steps);
            }
        }

        // ================================================================ lifecycle

        public static void Release()
        {
            _animating = false;
            _fromDeg = 0;
            SecretOpen = false;
            _tab = 0;
            _resetScroll = false;
            _loaded = false;
        }

        public static void OpenSecret()
        {
            if (!_unlocked) return;
            SecretOpen = true;
            _tab = 0;
            _resetScroll = true;
        }

        public static void CloseSecret()
        {
            SecretOpen = false;
        }

        /// <summary>ESC / back-key hook; returns true when the key was consumed.</summary>
        public static bool HandleKey(int virtualKey)
        {
            if (SecretOpen && virtualKey == Win32.VK_ESCAPE)
            {
                SecretOpen = false;
                return true;
            }
            return false;
        }

        // ================================================================ about panel (home)

        public static int Draw(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            EnsureLoaded();

            int headerH = c.S(30);
            c.Fill(x, y, width, headerH, Theme.Panel);
            c.Border(x, y, width, headerH, Theme.Border);
            c.Text("关于", x + c.S(8), y, c.S(56), headerH, Theme.Accent, false, true);

            int icon = c.S(24);
            int iconX = x + width - c.S(70) - c.S(8) - icon;
            int iconY = y + (headerH - icon) / 2;

            string summary = "FakerSwap " + (s.VersionText ?? "") + "   ·   MIT   ·   游戏内账号数据工具";
            c.TextSmall(summary, x + c.S(58), y + c.S(7),
                Math.Max(0, iconX - (x + c.S(58)) - c.S(8)), c.S(18), Theme.Muted);

            // The icon is drawn before the collapse button: a click on it consumes the click first.
            DrawIcon(c, iconX, iconY, icon, s);

            if (c.Button(_expanded ? "收起" : "展开", x + width - c.S(70), y + c.S(3), c.S(64), c.S(24)))
            {
                _expanded = !_expanded;
                SaveState();
            }
            y += headerH + c.S(6);
            if (!_expanded) return y + c.S(2);

            int gap = c.S(14);
            int colW = (width - gap) / 2;
            int leftX = x;
            int rightX = x + colW + gap;
            int leftY = y;
            int rightY = y;

            leftY = InfoRow(c, "版本", "FakerSwap " + (s.VersionText ?? ""), leftX, leftY, colW, Theme.Text);
            leftY = InfoRow(c, "作者", BuildInfo.Author, leftX, leftY, colW, Theme.Muted);
            leftY = InfoRow(c, "协议", "MIT（仅供学习与协议研究）", leftX, leftY, colW, Theme.Muted);
            leftY = InfoRow(c, "仓库", "本地 Git 仓库（未配置远程）", leftX, leftY, colW, Theme.Muted);
            leftY = InfoRow(c, "游戏版本", OrDash(s.GameVersion), leftX, leftY, colW, Theme.Muted);
            leftY = InfoRow(c, "Unity", OrDash(s.UnityVersion), leftX, leftY, colW, Theme.Muted);
            leftY = InfoRow(c, "MelonLoader", OrDash(s.LoaderVersion), leftX, leftY, colW, Theme.Muted);

            rightY = InfoRow(c, "曲库", s.MusicCount > 0 ? s.MusicCount + " 首" : "—", rightX, rightY, colW, Theme.Muted);
            rightY = InfoRow(c, "区域", s.AreaCount > 0 ? s.AreaCount + " 个" : "—", rightX, rightY, colW, Theme.Muted);
            rightY = InfoRow(c, "登录", AccountLine(s), rightX, rightY, colW,
                s.LoggedIn ? Theme.Good : Theme.Muted);
            rightY = InfoRow(c, "服务器", OrDash(s.ServerUri), rightX, rightY, colW, Theme.Muted);
            rightY = InfoRow(c, "统计", s.LoggedIn
                ? "游玩 " + s.AccountPlayCount + " 次 · MAP " + s.AccountMapStock + " · POINT " + s.AccountPoint
                : "—", rightX, rightY, colW, Theme.Muted);

            y = Math.Max(leftY, rightY) + c.S(2);
            c.TextSmall("数据来源：游戏自身 DataManager / UserData（未登录时回退主数据与本地缓存）。",
                x, y, width, c.S(18), Theme.Muted);
            y += c.S(22);

            // ---- secret menu entry -------------------------------------------------------
            int boxH = c.S(34);
            c.Fill(x, y, width, boxH, _unlocked ? Theme.WarnDark : Theme.PanelAlt);
            c.Border(x, y, width, boxH, _unlocked ? Theme.Warn : Theme.Border);

            if (_unlocked)
            {
                c.Text("神秘菜单已解锁", x + c.S(10), y, width - c.S(230), boxH, Theme.Warn, false, true);
                if (c.Button("进入神秘菜单 →", x + width - c.S(160), y + c.S(4), c.S(150), c.S(26), true))
                    OpenSecret();
            }
            else
            {
                c.Text("点图标拧 4 下（每下 90°）可解锁一个神秘界面", x + c.S(10), y,
                    width - c.S(170), boxH, Theme.Muted);
                c.TextSmall("已拧 " + Steps + "/4", x + width - c.S(150), y, c.S(140), boxH,
                    Steps > 0 ? Theme.Warn : Theme.Muted);
            }
            y += boxH + c.S(6);
            return y;
        }

        private static string AccountLine(WindowSnapshot s)
        {
            if (!s.LoggedIn) return "未登录";
            string name = string.IsNullOrEmpty(s.AccountName) ? ("uid" + s.AccountId) : s.AccountName;
            return name + "  ·  R" + s.AccountRating + "  ·  最高 R" + s.AccountHighestRating;
        }

        private static string OrDash(string text)
        {
            return string.IsNullOrEmpty(text) ? "—" : text;
        }

        private static int InfoRow(NativeCanvas c, string label, string value, int x, int y, int width, uint color)
        {
            int labelW = c.S(76);
            c.TextSmall(label, x, y + c.S(2), labelW, c.S(18), Theme.Muted);
            c.Text(value, x + labelW, y, Math.Max(0, width - labelW), c.S(20), color);
            return y + c.S(22);
        }

        // ================================================================ windmill icon

        private static void DrawIcon(NativeCanvas c, int x, int y, int size, WindowSnapshot s)
        {
            double angle = CurrentAngle();
            int cx = x + size / 2;
            int cy = y + size / 2;
            int r = Math.Max(6, size / 2 - c.S(2));
            bool hover = c.HoverAt(x, y, size, size);

            uint bg = _unlocked ? Theme.WarnDark : (hover ? Theme.Panel : Theme.PanelAlt);
            uint border = _unlocked ? Theme.Warn : (hover ? Theme.Accent : Theme.Border);
            c.Fill(x, y, size, size, bg);
            c.Border(x, y, size, size, border);

            uint bladeA = _unlocked ? Theme.Warn : Theme.Accent;
            uint bladeB = _unlocked ? Theme.WarnDark : Theme.AccentDark;
            for (int k = 0; k < 4; k++)
            {
                double a0 = (angle + k * 90.0) * Math.PI / 180.0;
                double a1 = (angle + k * 90.0 + 58.0) * Math.PI / 180.0;
                int[] xs =
                {
                    cx,
                    cx + (int)Math.Round(Math.Cos(a0) * r),
                    cx + (int)Math.Round(Math.Cos(a1) * r * 0.62)
                };
                int[] ys =
                {
                    cy,
                    cy + (int)Math.Round(Math.Sin(a0) * r),
                    cy + (int)Math.Round(Math.Sin(a1) * r * 0.62)
                };
                c.Polygon(xs, ys, (k % 2 == 0) ? bladeA : bladeB);
            }

            int hub = Math.Max(2, c.S(4));
            c.Fill(cx - hub / 2, cy - hub / 2, hub, hub, Theme.Text);

            // A marker on the first blade so each 90° step is visibly different.
            double ma = angle * Math.PI / 180.0;
            int mx = cx + (int)Math.Round(Math.Cos(ma) * r * 0.78);
            int my = cy + (int)Math.Round(Math.Sin(ma) * r * 0.78);
            int dot = Math.Max(2, c.S(3));
            c.Fill(mx - dot / 2, my - dot / 2, dot, dot, _unlocked ? Theme.Text : Theme.Warn);

            if (c.RowClick(x, y, size, size)) Spin();
        }

        private static void Spin()
        {
            if (_animating) return;
            _fromDeg = _deg;
            _deg += SpinStepDeg;
            _animStart = Environment.TickCount;
            _animating = true;
            if (!_unlocked && _deg >= SpinFullDeg)
            {
                _unlocked = true;
                Log.Info("AboutPanel: 360° reached - secret menu unlocked");
            }
            SaveState();
        }

        private static double CurrentAngle()
        {
            if (!_animating) return _deg;
            int elapsed = unchecked(Environment.TickCount - _animStart);
            if (elapsed >= SpinMs)
            {
                _animating = false;
                return _deg;
            }
            double t = elapsed / (double)SpinMs;
            double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
            return _fromDeg + SpinStepDeg * eased;
        }

        // ================================================================ secret menu page

        public static int DrawSecret(NativeCanvas c, WindowSnapshot s)
        {
            EnsureLoaded();
            if (_resetScroll)
            {
                _resetScroll = false;
                c.ScrollY = 0;
            }

            int pad = c.Pad;
            int x = pad;
            int width = c.Width - pad * 2 - c.S(10);
            int y = 0;

            c.TextTitle("神秘菜单", x, y, Math.Max(0, width - c.S(96)), c.S(28), Theme.Warn);
            c.TextSmall("FakerSwap 内部功能 · 已拧满 4/4 · 谨慎操作，写账号类功能请先在私服验证",
                x, y + c.S(24), Math.Max(0, width - c.S(96)), c.S(18), Theme.Muted);
            if (c.Button("← 返回", x + width - c.S(84), y, c.S(84), c.S(26))) SecretOpen = false;
            y += c.S(44);

            int gap = c.S(4);
            int tabW = (width - gap * (TabNames.Length - 1)) / TabNames.Length;
            for (int i = 0; i < TabNames.Length; i++)
            {
                if (c.Button(TabNames[i], x + i * (tabW + gap), y, tabW, c.S(26), _tab == i))
                {
                    _tab = i;
                    _resetScroll = true;
                }
            }
            y += c.S(32);
            c.Separator(x, y - c.S(2), width);

            if (s.Busy)
            {
                c.Fill(x, y, width, c.S(28), Theme.WarnDark);
                c.Border(x, y, width, c.S(28), Theme.Warn);
                c.Text("任务进行中：写账号类按钮已禁用，只读信息照常显示。", x + c.S(10), y,
                    width - c.S(20), c.S(28), Theme.Warn);
                y += c.S(34);
            }

            switch (_tab)
            {
                case 0:
                    y = DrawOverview(c, s, x, y, width);
                    break;
                default:
                    y = DrawTodo(c, s, x, y, width);
                    break;
            }
            return y + pad;
        }

        private static int DrawTodo(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            int h = c.S(52);
            c.Fill(x, y, width, h, Theme.PanelAlt);
            c.Border(x, y, width, h, Theme.Border);
            c.Text("「" + TabNames[_tab] + "」标签页施工中", x + c.S(12), y + c.S(6), width - c.S(24),
                c.S(22), Theme.Text);
            c.TextSmall("下一阶段接入：曲库浏览 / 收藏品解锁 / 区域离线选择 / 票券 / 工具。",
                x + c.S(12), y + c.S(28), width - c.S(24), c.S(18), Theme.Muted);
            return y + h;
        }

        private static int DrawOverview(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            int gap = c.S(14);
            int colW = (width - gap) / 2;
            int rightX = x + colW + gap;
            int leftY = y;
            int rightY = y;

            // ---- account card -----------------------------------------------------------
            leftY = CardHeader(c, "账号", leftX: x, leftY, colW);
            leftY = InfoRow(c, "用户名", s.LoggedIn ? OrDash(s.AccountName) : "未登录", x, leftY, colW, Theme.Text);
            leftY = InfoRow(c, "UserID", s.LoggedIn ? s.AccountId.ToString(CultureInfo.InvariantCulture) : "—",
                x, leftY, colW, Theme.Muted);
            leftY = InfoRow(c, "Rating", s.LoggedIn ? "R" + s.AccountRating : "—", x, leftY, colW, Theme.Text);
            leftY = InfoRow(c, "最高 Rating", s.LoggedIn ? "R" + s.AccountHighestRating : "—", x, leftY, colW, Theme.Muted);
            leftY = InfoRow(c, "游玩次数", s.LoggedIn ? s.AccountPlayCount.ToString(CultureInfo.InvariantCulture) : "—",
                x, leftY, colW, Theme.Muted);
            leftY = InfoRow(c, "MAP 库存", s.LoggedIn ? s.AccountMapStock.ToString(CultureInfo.InvariantCulture) : "—",
                x, leftY, colW, Theme.Muted);
            leftY = InfoRow(c, "POINT", s.LoggedIn ? s.AccountPoint.ToString(CultureInfo.InvariantCulture) : "—",
                x, leftY, colW, Theme.Muted);
            leftY += c.S(4);

            // ---- environment card -------------------------------------------------------
            rightY = CardHeader(c, "环境", rightX, rightY, colW);
            rightY = InfoRow(c, "插件", "FakerSwap " + (s.VersionText ?? ""), rightX, rightY, colW, Theme.Text);
            rightY = InfoRow(c, "游戏", OrDash(s.GameVersion), rightX, rightY, colW, Theme.Muted);
            rightY = InfoRow(c, "Unity", OrDash(s.UnityVersion), rightX, rightY, colW, Theme.Muted);
            rightY = InfoRow(c, "MelonLoader", OrDash(s.LoaderVersion), rightX, rightY, colW, Theme.Muted);
            rightY = InfoRow(c, "服务器", OrDash(s.ServerUri), rightX, rightY, colW, Theme.Muted);
            rightY = InfoRow(c, "曲库", s.MusicCount > 0 ? s.MusicCount + " 首" : "—", rightX, rightY, colW, Theme.Muted);
            rightY = InfoRow(c, "区域", s.AreaCount > 0 ? s.AreaCount + " 个" : "—", rightX, rightY, colW, Theme.Muted);
            rightY += c.S(4);

            return Math.Max(leftY, rightY);
        }

        private static int CardHeader(NativeCanvas c, string title, int leftX, int leftY, int colW)
        {
            c.Text(title, leftX, leftY, colW, c.S(22), Theme.Accent, false, true);
            return leftY + c.S(24);
        }

        // ================================================================ persistence

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _statePath = Fs.UiStatePath;
            try
            {
                if (!File.Exists(_statePath)) return;
                foreach (string line in File.ReadAllLines(_statePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "aboutexpanded":
                            _expanded = value == "1";
                            break;
                        case "secretunlocked":
                            _unlocked = value == "1";
                            break;
                        case "aboutdeg":
                            int deg;
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out deg))
                                _deg = Math.Max(0, deg);
                            break;
                    }
                }
                if (_deg >= SpinFullDeg && !_unlocked)
                {
                    // A half-written file must not silently unlock the menu.
                    _deg = SpinFullDeg;
                }
            }
            catch (Exception e)
            {
                Log.Warn("AboutPanel: state load failed: " + e.Message);
            }
        }

        private static void SaveState()
        {
            try
            {
                if (_statePath == null) _statePath = Fs.UiStatePath;
                string dir = Path.GetDirectoryName(_statePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string text = "aboutExpanded=" + (_expanded ? 1 : 0) + Environment.NewLine +
                              "secretUnlocked=" + (_unlocked ? 1 : 0) + Environment.NewLine +
                              "aboutDeg=" + _deg + Environment.NewLine;
                File.WriteAllText(_statePath, text);
            }
            catch (Exception e)
            {
                Log.Warn("AboutPanel: state save failed: " + e.Message);
            }
        }

        /// <summary>Puts the windmill back to 0° (used by the secret menu's 工具 tab).</summary>
        public static void ResetSpin()
        {
            _unlocked = false;
            _deg = 0;
            _fromDeg = 0;
            _animating = false;
            SecretOpen = false;
            SaveState();
        }

        /// <summary>True when the secret menu is currently unlocked (used by the tools tab).</summary>
        public static bool Unlocked { get { return _unlocked; } }
    }
}
