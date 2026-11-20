using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FakerSwap.Core;
using FakerSwap.Data;
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
        private const int TabOverview = 0;
        private const int TabSongs = 1;
        private const int TabAreas = 2;
        private const int TabCollection = 3;
        private const int TabTickets = 4;
        private const int TabTools = 5;
        private const int SongPageSize = 14;
        private const int SongSearchId = 9101;

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

        // catalog / unlock page state (window thread only)
        private static int _songPage;
        private static string _songSearch = "";
        private static int _songFilter;
        private static bool _includeMaster = true;
        private static bool _includeRemaster = true;
        private static bool _catalogRequested;
        private static string _unlockNote = "";

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
            _catalogRequested = false;   // refresh the game copy each time the menu is opened
            _songPage = 0;
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
                c.Text("神秘菜单已解锁", x + c.S(10), y, width - c.S(250), boxH, Theme.Warn, false, true);
                if (c.Button("重锁", x + width - c.S(226), y + c.S(4), c.S(56), c.S(26), false))
                {
                    ResetSpin();
                    _unlocked = false;
                }
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
            if (s.SafeNoWrite)
            {
                c.Fill(x, y, width, c.S(28), Theme.BadDark);
                c.Border(x, y, width, c.S(28), Theme.Bad);
                c.Text("只读模式（SafeNoWrite=true）：解锁写入会被拒绝，先在主界面「解除只读」。",
                    x + c.S(10), y, width - c.S(20), c.S(28), Theme.Bad);
                y += c.S(34);
            }

            EnsureCatalog();

            switch (_tab)
            {
                case TabOverview:
                    y = DrawOverview(c, s, x, y, width);
                    break;
                case TabSongs:
                    y = DrawSongs(c, s, x, y, width);
                    break;
                case TabAreas:
                    y = DrawCrawl(c, s, x, y, width);
                    break;
                case TabCollection:
                    y = DrawCollections(c, s, x, y, width);
                    break;
                default:
                    y = DrawTodo(c, s, x, y, width);
                    break;
            }
            return y + pad;
        }

        private static void EnsureCatalog()
        {
            if (_catalogRequested) return;
            _catalogRequested = true;
            try { UiActions.RefreshGameCatalog(); }
            catch (Exception e) { Log.Warn("AboutPanel: catalog refresh request failed: " + e.Message); }
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

        // ================================================================ songs tab

        private static readonly List<int> PageUnlockIds = new List<int>();

        private static int DrawSongs(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            bool writable = !s.SafeNoWrite && !s.Busy;
            var songs = GameCatalog.Songs;
            string error = GameCatalog.Error;
            int gap = c.S(6);
            int rowH = c.S(26);

            // ---- row 1: search / filter / refresh / difficulty toggles ---------------------
            int searchW = Math.Min(width / 3, c.S(260));
            _songSearch = c.TextField(_songSearch, SongSearchId, x, y, searchW, rowH);
            int bx = x + searchW + gap;

            int fw = c.S(82);
            if (c.Button("全部", bx, y, fw, rowH, _songFilter == 0)) { _songFilter = 0; _songPage = 0; }
            bx += fw + gap;
            if (c.Button("未写入", bx, y, fw, rowH, _songFilter == 1)) { _songFilter = 1; _songPage = 0; }
            bx += fw + gap;
            if (c.Button("已写入", bx, y, fw, rowH, _songFilter == 2)) { _songFilter = 2; _songPage = 0; }
            bx += fw + gap;

            if (c.Button("刷新", bx, y, c.S(58), rowH)) UiActions.RefreshGameCatalog();
            bx += c.S(58) + gap;

            int tw = c.S(82);
            _includeMaster = c.Toggle(_includeMaster, "含紫谱", bx, y, tw, rowH);
            bx += tw + gap;
            _includeRemaster = c.Toggle(_includeRemaster, "含白谱", bx, y, tw, rowH);
            y += rowH + gap;

            // ---- row 2: batch actions ------------------------------------------------------
            int aw = c.S(150);
            if (c.Button("解锁本页未写入", x, y, aw, rowH, false, writable && songs.Length > 0))
                UiActions.UnlockSongs(PageUnlockIds.ToArray(), _includeMaster, _includeRemaster);
            if (c.Button("解锁全部未写入", x + aw + gap, y, aw, rowH, true, writable && songs.Length > 0))
                UiActions.UnlockAllSongs(_includeMaster, _includeRemaster);
            if (c.Button("存档上传(登出)", x + (aw + gap) * 2, y, c.S(116), rowH, false, writable))
                UiActions.FlushUnlocks();
            if (c.Button("清空搜索", x + (aw + gap) * 2 + c.S(122), y, c.S(90), rowH))
            {
                _songSearch = "";
                _songPage = 0;
            }
            y += rowH + c.S(4);

            // ---- status --------------------------------------------------------------------
            string status;
            if (songs.Length == 0)
            {
                status = "尚未读取数据" + (string.IsNullOrEmpty(error) ? "（正在读取…）" : "：" + error);
            }
            else
            {
                status = "曲库 " + songs.Length + " 首 · 本体已写入 " + CountInList(songs, 0) +
                         " · 紫 " + CountInList(songs, 1) + " · 白 " + CountInList(songs, 2);
            }
            c.TextSmall(status, x, y, width, c.S(18), Theme.Muted);
            y += c.S(20);

            // ---- column header -------------------------------------------------------------
            int idW = c.S(56);
            int artistW = c.S(168);
            int lvW = c.S(42);
            int flagW = c.S(70);
            int btnW = c.S(60);
            int titleW = width - idW - artistW - lvW * 5 - flagW - btnW - gap * 3;
            if (titleW < c.S(150)) titleW = c.S(150);

            int hx = x;
            c.TextSmall("ID", hx, y, idW, c.S(16), Theme.Muted);
            hx += idW;
            c.TextSmall("曲名", hx, y, titleW, c.S(16), Theme.Muted);
            hx += titleW;
            c.TextSmall("艺术家", hx, y, artistW, c.S(16), Theme.Muted);
            hx += artistW;
            string[] shortLevel = { "B", "A", "E", "M", "R" };
            for (int lv = 0; lv < 5; lv++)
            {
                c.TextSmall(shortLevel[lv], hx, y, lvW, c.S(16), Theme.Muted);
                hx += lvW;
            }
            c.TextSmall("解锁项", hx, y, flagW, c.S(16), Theme.Muted);
            y += c.S(18);

            // ---- rows ----------------------------------------------------------------------
            int total = 0;
            for (int i = 0; i < songs.Length; i++)
                if (SongMatches(songs[i])) total++;

            int maxPage = Math.Max(1, (total + SongPageSize - 1) / SongPageSize);
            if (_songPage >= maxPage) _songPage = maxPage - 1;
            if (_songPage < 0) _songPage = 0;
            int start = _songPage * SongPageSize;

            PageUnlockIds.Clear();
            int passed = 0;
            int drawn = 0;
            for (int i = 0; i < songs.Length; i++)
            {
                var row = songs[i];
                if (!SongMatches(row)) continue;
                if (passed++ < start) continue;
                if (drawn >= SongPageSize) continue;
                drawn++;
                if (row.Id > 0 && !row.BaseInList) PageUnlockIds.Add(row.Id);
                DrawSongRow(c, row, x, y, width, idW, titleW, artistW, lvW, flagW, btnW, gap, writable);
                y += c.S(24);
            }

            if (total == 0)
            {
                c.TextSmall("没有匹配的曲目。", x, y, width, c.S(20), Theme.Muted);
                y += c.S(24);
            }

            // ---- paging --------------------------------------------------------------------
            y += c.S(4);
            int navW = c.S(64);
            if (c.Button("上一页", x, y, navW, rowH, false, _songPage > 0)) _songPage--;
            if (c.Button("下一页", x + navW + gap, y, navW, rowH, false, _songPage < maxPage - 1)) _songPage++;
            c.TextSmall("第 " + (_songPage + 1) + " / " + maxPage + " 页   ·   共 " + total + " 首（每页 " + SongPageSize +
                        "）   ·   勾选/搜索后「解锁本页未写入」", x + (navW + gap) * 2, y, width - (navW + gap) * 2,
                        rowH, Theme.Muted);
            y += rowH + c.S(2);
            c.TextSmall("提示：标准曲（id<10000）的紫/白谱天生解锁；本体解锁写 itemKind=5，紫/白写 6/7。",
                x, y, width, c.S(18), Theme.Muted);
            y += c.S(20);
            return y;
        }

        private static void DrawSongRow(NativeCanvas c, GameCatalog.SongRow row, int x, int y, int width,
            int idW, int titleW, int artistW, int lvW, int flagW, int btnW, int gap, bool writable)
        {
            bool hover = c.HoverAt(x, y, width, c.S(24));
            if (hover) c.Fill(x, y, width, c.S(24), Theme.PanelAlt);

            int hx = x;
            c.TextSmall(row.Id.ToString(CultureInfo.InvariantCulture), hx, y, idW, c.S(24), Theme.Muted);
            hx += idW;
            c.Text(row.Title, hx, y, titleW, c.S(24), Theme.Text);
            hx += titleW;
            c.TextSmall(row.Artist, hx, y, artistW, c.S(24), Theme.Muted);
            hx += artistW;
            for (int lv = 0; lv < 5; lv++)
            {
                string text = row.LevelText(lv);
                uint color = text == "—" ? Theme.Disabled : (lv >= 3 ? Theme.Accent : Theme.Text);
                c.TextSmall(text, hx, y, lvW, c.S(24), color);
                hx += lvW;
            }
            string flag = row.BaseInList ? "✓ 已写入" : "✗ 未写入";
            c.TextSmall(flag, hx, y, flagW, c.S(24), row.BaseInList ? Theme.Good : Theme.Warn);
            hx += flagW + gap;

            if (c.Button("写入", hx, y + c.S(1), btnW, c.S(22), false, writable))
                UiActions.UnlockSongs(new[] { row.Id }, _includeMaster, _includeRemaster);
        }

        private static bool SongMatches(GameCatalog.SongRow row)
        {
            if (row == null) return false;
            if (_songFilter == 1 && row.BaseInList) return false;
            if (_songFilter == 2 && !row.BaseInList) return false;
            return TextMatch.Matches(_songSearch, row.Id, row.Title, row.Artist);
        }

        private static int CountInList(GameCatalog.SongRow[] songs, int kind)
        {
            int count = 0;
            for (int i = 0; i < songs.Length; i++)
            {
                var row = songs[i];
                if (row == null) continue;
                if (kind == 0 ? row.BaseInList : kind == 1 ? row.MasterInList : row.ReMasterInList) count++;
            }
            return count;
        }

        // ================================================================ crawl tab

        private static int DrawCrawl(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            bool writable = !s.SafeNoWrite && !s.Busy;
            bool crawling = s.CrawlOnly && s.Busy;
            int gap = c.S(6);
            int rowH = c.S(26);

            int cardH = c.S(58);
            c.Fill(x, y, width, cardH, crawling ? Theme.Panel : Theme.PanelAlt);
            c.Border(x, y, width, cardH, crawling ? Theme.Warn : Theme.Border);
            c.Text(crawling ? "跑区进行中" : "跑区（专用模式）", x + c.S(10), y + c.S(4),
                width - c.S(20), c.S(22), crawling ? Theme.Warn : Theme.Text, false, true);
            string line = crawling
                ? "credit " + s.CurrentCredit + " · 当前区 " +
                  (string.IsNullOrEmpty(s.CrawlCurrentArea) ? "—" : s.CrawlCurrentArea) +
                  " · 勾选区完成 " + s.CrawlCompletedCount + "/" + s.SelectedAreaIds.Length
                : "不需要源数据：登录后每个 credit 打 3 轨性隐 BASIC（默认 100.5% / AP），自动选区、课题曲、续关、上报";
            c.TextSmall(line, x + c.S(10), y + c.S(28), Math.Max(0, width - c.S(20)), c.S(18), Theme.Muted);
            y += cardH + c.S(6);

            int aw = c.S(150);
            if (crawling)
            {
                if (c.Button("停止跑区", x, y, aw, rowH, false)) UiActions.StopCrawl();
            }
            else if (c.Button("开始跑区", x, y, aw, rowH, true, writable && s.LoggedIn))
            {
                UiActions.StartCrawl();
            }
            if (c.Button("刷新区域列表", x + aw + gap, y, c.S(120), rowH, false, !crawling))
                UiActions.RefreshAreas();
            if (!s.LoggedIn)
            {
                c.TextSmall("未登录：先在机台上刷卡登录（跑区不需要源数据，但需要会话）",
                    x + aw + c.S(126) + gap, y, Math.Max(0, width - aw - c.S(126) - gap), rowH, Theme.Warn);
            }
            else if (s.SafeNoWrite)
            {
                c.TextSmall("只读模式：跑区会写成绩，先解除只读",
                    x + aw + c.S(126) + gap, y, Math.Max(0, width - aw - c.S(126) - gap), rowH, Theme.Bad);
            }
            y += rowH + c.S(8);

            c.TextSmall("勾选要跑的区：主界面「区域（爬图）」面板（Title 阶段也能勾，保存进 config.json 的 CrawlAreaIds）。",
                x, y, width, c.S(18), Theme.Muted);
            y += c.S(20);
            c.TextSmall("结束条件：勾选区全部完成 / 达到 config 的 CrawlCreditLimit（默认 999 credit）/ 你点停止。",
                x, y, width, c.S(18), Theme.Muted);
            y += c.S(20);
            c.TextSmall("跑区建议 C·真实时长；填充轨默认 100.5% AP，地图距离加成拉满；普通课题曲 80% 过、挑战曲按规则。",
                x, y, width, c.S(18), Theme.Muted);
            y += c.S(24);
            return y;
        }

        // ================================================================ collection tab

        private static int DrawCollections(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            bool writable = !s.SafeNoWrite && !s.Busy;
            var infos = GameCatalog.Collections;
            int gap = c.S(6);
            int rowH = c.S(26);

            if (infos.Length == 0)
            {
                string error = GameCatalog.Error;
                c.Fill(x, y, width, c.S(46), Theme.PanelAlt);
                c.Border(x, y, width, c.S(46), Theme.Border);
                c.TextSmall("收藏品数据还没读到" + (string.IsNullOrEmpty(error) ? "（正在读取…）" : "：" + error),
                    x + c.S(10), y + c.S(4), width - c.S(20), c.S(18), Theme.Warn);
                if (c.Button("重新读取", x + width - c.S(110), y + c.S(9), c.S(100), c.S(26))) UiActions.RefreshGameCatalog();
                return y + c.S(54);
            }

            c.TextSmall("来源：DataManager 主数据 vs 当前账号 UserData 收藏列表。写入后由游戏自己的 UpsertUserAll 上传。",
                x, y, width, c.S(18), Theme.Muted);
            y += c.S(22);

            for (int i = 0; i < infos.Length; i++)
            {
                var info = infos[i];
                bool complete = info.Total > 0 && info.Owned >= info.Total;
                int barW = Math.Min(c.S(220), width / 4);
                int nameW = c.S(80);
                int countW = c.S(110);

                c.Fill(x, y, width, rowH + c.S(6), i % 2 == 0 ? Theme.PanelAlt : Theme.Panel);
                c.Text(info.Name, x + c.S(8), y, nameW, rowH + c.S(6), Theme.Text);
                c.TextSmall(info.Owned + " / " + info.Total, x + c.S(8) + nameW, y, countW, rowH + c.S(6),
                    complete ? Theme.Good : Theme.Muted);
                double ratio = info.Total > 0 ? (double)info.Owned / info.Total : 0;
                c.ProgressBar(x + c.S(8) + nameW + countW, y + c.S(9), barW, c.S(12), ratio,
                    complete ? Theme.Good : Theme.AccentDark, Theme.Track);
                if (c.Button(complete ? "已全" : "补全", x + width - c.S(84), y + c.S(5), c.S(76), c.S(24),
                        false, writable && !complete))
                {
                    UiActions.UnlockCollections(1 << i);
                }
                y += rowH + c.S(8);
            }

            y += c.S(4);
            int aw = c.S(150);
            if (c.Button("全部补全", x, y, aw, rowH, true, writable)) UiActions.UnlockAllCollections();
            if (c.Button("存档上传(登出)", x + aw + gap, y, c.S(116), rowH, false, writable)) UiActions.FlushUnlocks();
            y += rowH + c.S(4);
            c.TextSmall("· 称号/头像/姓名框/背景/伙伴走 userItemList（收藏类），不用打歌。",
                x, y, width, c.S(18), Theme.Muted);
            y += c.S(18);
            c.TextSmall("· 写入只在内存里生效；点「存档上传」或等下一次结算，游戏会自己发给服务器。",
                x, y, width, c.S(18), Theme.Muted);
            y += c.S(20);
            return y;
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
