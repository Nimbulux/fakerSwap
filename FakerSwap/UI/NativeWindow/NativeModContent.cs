using System;
using System.Collections.Generic;
using FakerSwap.Flow;

namespace FakerSwap.UI.NativeWindow
{
    /// <summary>
    /// Content renderer for the standalone window (ported structure from Sinmai-Assist's
    /// NativeModContent, rewritten for the FakerSwap pages).
    ///
    /// Runs on the window thread only.  It reads <see cref="WindowState.Current"/> (immutable
    /// snapshot published by the game thread) and plain files (log tail); it never touches Unity
    /// or a game object.  All actions are posted back to the game thread by <see cref="UiActions"/>.
    /// </summary>
    internal static class NativeModContent
    {
        private static string _selectedPath;
        private static string _toast;
        private static int _toastUntil;
        private static int _lastSourceVersion = -1;
        private static bool _unlockDialog;

        // area crawl panel (window-thread local UI state; committed to ConfigData via MainThread)
        private static bool _areaExpanded = false;
        private static string _areaSearch = "";
        private const int AreaSearchId = 9202;
        private static readonly HashSet<int> _areaSel = new HashSet<int>();
        /// <summary>Area ids in the order they are displayed this frame (used to keep CrawlAreaIds ordered).</summary>
        private static readonly List<int> _areaOrder = new List<int>();
        private static bool _areaSelDirty;
        private static int _areaSelSentAt;
        private static bool _areaFallback;
        private static bool _areaFallbackDirty;
        private static int _areaFallbackSentAt;

        // settings panel (window-thread local UI state)
        private static bool _settingsExpanded = false;
        private static string _fillerText = "";
        private static string _areaTimeoutText = "";
        private static int _openDropdownField;
        private static string[] _dropdownOptions;
        private static int _dropdownSelected;
        private static int _dropdownX;
        private static int _dropdownY;
        private static int _dropdownW;
        private static int _dropdownControlY;

        // pending playlist panel (window-thread local UI state; collapsed by default)
        private static bool _queueExpanded;

        public static void Release()
        {
            _selectedPath = null;
            _toast = null;
            _toastUntil = 0;
            _lastSourceVersion = -1;
            _unlockDialog = false;
            _areaExpanded = false;
            _areaSearch = "";
            _areaSel.Clear();
            _areaSelDirty = false;
            _areaFallback = false;
            _areaFallbackDirty = false;
            _settingsExpanded = false;
            _fillerText = "";
            _areaTimeoutText = "";
            _openDropdownField = 0;
            _dropdownOptions = null;
            _queueExpanded = false;
            AboutPanel.Release();
        }

        /// <summary>True while an animation (About windmill) needs a fast repaint timer.</summary>
        public static bool WantsFastTicks { get { return AboutPanel.Animating; } }

        /// <summary>Window-thread global key hook (ESC closes the secret menu).</summary>
        public static bool HandleKey(int virtualKey)
        {
            return AboutPanel.HandleKey(virtualKey);
        }

        public static void Render(NativeCanvas c)
        {
            ConsumeNotice();
            var s = WindowState.Current;
            if (s == null)
            {
                c.Text("正在读取状态…", c.Pad, c.Pad, c.Width - c.Pad * 2, c.LineH, Theme.Muted);
                return;
            }

            if (s.SourceVersion != _lastSourceVersion)
            {
                _lastSourceVersion = s.SourceVersion;
                ReconcileSelection(s);
            }
            else if (string.IsNullOrEmpty(_selectedPath) && !string.IsNullOrEmpty(s.SourcePath))
            {
                ReconcileSelection(s);
            }

            if (_unlockDialog && !s.SafeNoWrite) _unlockDialog = false;
            if (_openDropdownField != 0 && _unlockDialog) _openDropdownField = 0;

            SyncAreaState(s);

            // While a confirmation dialog / dropdown is up the page below must not react to
            // clicks: park the pending click, draw the page, then hand it to the overlay.
            bool secret = AboutPanel.SecretOpen;
            if (secret)
            {
                _unlockDialog = false;
                _openDropdownField = 0;
            }
            bool modal = _unlockDialog;
            bool dropdown = _openDropdownField != 0;
            bool suppress = modal || dropdown;
            bool savedClick = c.HasClick;
            int savedClickX = c.ClickX;
            int savedClickY = c.ClickY;
            if (suppress) c.HasClick = false;

            DrawHeader(c, s);

            int top = c.S(58);
            int bottom = c.S(42);
            c.BeginContent(top, c.Height - bottom);
            int height;
            try
            {
                if (secret) height = AboutPanel.DrawSecret(c, s);
                else if (s.Phase == (int)FlowPhase.AwaitingConfirm) height = DrawConfirm(c, s);
                else if (IsTaskPhase(s.Phase)) height = DrawTask(c, s);
                else height = DrawHome(c, s);
            }
            catch (Exception e)
            {
                height = c.Pad + c.LineH * 3;
                c.Text("此页面渲染失败：" + e.Message, c.Pad, 0, c.Width - c.Pad * 2, c.LineH, Theme.Bad);
                c.Text("请查看 MelonLoader 控制台获取堆栈；其它按钮仍可用。", c.Pad, c.LineH,
                    c.Width - c.Pad * 2, c.LineH, Theme.Muted);
            }
            c.EndContent(height);
            c.DrawScrollbar();
            DrawFooter(c, s);

            if (suppress)
            {
                c.HasClick = savedClick;
                c.ClickX = savedClickX;
                c.ClickY = savedClickY;
                if (modal) DrawUnlockDialog(c);
                else DrawDropdownOverlay(c);
            }
        }

        // ================================================================ chrome

        private static void DrawHeader(NativeCanvas c, WindowSnapshot s)
        {
            int pad = c.Pad;
            int y = c.S(6);

            int lockW = c.S(104);
            int lockH = c.S(24);
            int lockY = y + c.S(1);
            int lockX = c.Width - pad - lockW;

            int titleW = c.Width - pad * 2 - c.S(120);
            if (s.SafeNoWrite)
            {
                string roText = "● 只读模式（不写账号）";
                int roW = Math.Max(c.S(150), c.MeasureText(roText, true) + c.S(18));
                int roX = lockX - c.S(8) - roW;
                c.Fill(roX, lockY, roW, lockH, Theme.WarnDark);
                c.Border(roX, lockY, roW, lockH, Theme.Warn);
                c.Text(roText, roX, lockY, roW, lockH, Theme.Warn, true, true);
                titleW -= roW + c.S(8);
            }

            c.TextTitle("FakerSwap 账号转生", pad, y, titleW, c.S(26), Theme.Text);
            string lockText = s.LockActive ? "● 占用机台" : "○ 机台空闲";
            c.Fill(lockX, lockY, lockW, lockH, s.LockActive ? Theme.BadDark : Theme.PanelAlt);
            c.Border(lockX, lockY, lockW, lockH, s.LockActive ? Theme.Bad : Theme.Border);
            c.Text(lockText, lockX, lockY, lockW, lockH, s.LockActive ? Theme.Bad : Theme.Muted, true, true);

            int y2 = c.S(32);
            string phase = string.IsNullOrEmpty(s.PhaseName) ? "未知阶段" : s.PhaseName;
            uint phaseColor = PhaseColor(s.Phase);
            int pillW = Math.Max(c.S(76), c.MeasureText(phase, true) + c.S(20));
            c.Fill(pad, y2, pillW, c.S(21), Theme.Panel);
            c.Border(pad, y2, pillW, c.S(21), phaseColor);
            c.Text(phase, pad, y2, pillW, c.S(21), phaseColor, true, true);

            int stageX = pad + pillW + c.S(12);
            int stageW = c.Width - pad * 2 - pillW - c.S(12) - c.S(190);
            c.Text("游戏阶段：" + s.GameStage, stageX, y2, stageW, c.S(21), Theme.Muted);

            if (s.LockActive)
            {
                string locked = "已锁定 " + FormatDuration(s.LockSeconds);
                TextRight(c, locked, c.Width - pad, y2, c.S(21), Theme.Warn, true, true);
            }

            c.Separator(pad, c.S(54), c.Width - pad * 2);
        }

        private static void DrawFooter(NativeCanvas c, WindowSnapshot s)
        {
            int pad = c.Pad;
            int y = c.Height - c.S(34);
            int h = c.S(24);
            c.Separator(pad, y - c.S(5), c.Width - pad * 2);

            int hideW = c.S(88);
            int hideX = c.Width - pad - hideW;
            if (c.Button("隐藏窗口", hideX, y, hideW, h)) UiActions.HideWindow();

            int topW = c.S(78);
            int topX = hideX - c.S(8) - topW;
            if (c.Button(s.TopMost ? "置顶：开" : "置顶：关", topX, y, topW, h, s.TopMost))
                UiActions.SetTopMost(!s.TopMost);

            int leftLimit = topX;
            if (s.SafeNoWrite)
            {
                int roW = c.S(100);
                int roX = topX - c.S(8) - roW;
                if (c.Button("解除只读…", roX, y, roW, h, true)) _unlockDialog = true;
                leftLimit = roX;
            }

            string toast = TakeToast();
            string left;
            uint color;
            if (!string.IsNullOrEmpty(toast))
            {
                left = toast;
                color = Theme.Warn;
            }
            else
            {
                left = "FakerSwap " + s.VersionText + "  |  F8 显示/隐藏  |  滚轮 滚动";
                color = Theme.Muted;
            }
            c.TextSmall(left, pad, y, Math.Max(c.S(80), leftLimit - pad - c.S(8)), h, color);
        }

        // ================================================================ home page

        private static int DrawHome(NativeCanvas c, WindowSnapshot s)
        {
            int pad = c.Pad;
            int x = pad;
            int width = c.Width - pad * 2 - c.S(10);
            int y = 0;

            bool sourceBusy = s.Phase == (int)FlowPhase.SourceLoginPending || s.Phase == (int)FlowPhase.SourceReading;
            bool hasSource = !string.IsNullOrEmpty(s.SourcePath);

            // ---- status card -----------------------------------------------------------
            string headline;
            uint headColor;
            bool borderAccent;
            if (s.Phase == (int)FlowPhase.Failed)
            {
                headline = "任务失败";
            headColor = Theme.Bad;
                borderAccent = true;
            }
            else if (s.Phase == (int)FlowPhase.Finished)
            {
                headline = "上次任务已完成";
                headColor = Theme.Good;
                borderAccent = true;
            }
            else if (sourceBusy)
            {
                headline = s.Phase == (int)FlowPhase.SourceLoginPending ? "请刷入源账号卡" : "正在读取源账号数据…";
                headColor = Theme.Warn;
                borderAccent = true;
            }
            else if (hasSource)
            {
                if (s.SafeNoWrite)
                {
                    headline = "源数据已就绪 · 只读模式不能转生";
                    headColor = Theme.Warn;
                }
                else
                {
                    headline = "源数据已就绪，可以开始转生";
                    headColor = Theme.Good;
                }
                borderAccent = true;
            }
            else if (s.Phase == (int)FlowPhase.NeedSource)
            {
                headline = "请选择源数据";
                headColor = Theme.Accent;
                borderAccent = true;
            }
            else
            {
                headline = s.SafeNoWrite ? "只读模式：只能获取源数据" : "当前不在任务中";
                headColor = s.SafeNoWrite ? Theme.Warn : Theme.Text;
                borderAccent = s.SafeNoWrite;
            }

            int cardH = c.S(68);
            c.Fill(x, y, width, cardH, Theme.Panel);
            c.Border(x, y, width, cardH, borderAccent ? headColor : Theme.Border);
            c.TextTitle(headline, x + c.S(12), y + c.S(6), width - c.S(24), c.S(30), headColor);
            string sub;
            if (s.Phase == (int)FlowPhase.Failed && !string.IsNullOrEmpty(s.LastError))
                sub = "失败原因：" + s.LastError;
            else
                sub = "阶段：" + s.PhaseName +
                      (string.IsNullOrEmpty(s.Status) || s.Status == s.PhaseName ? "" : "   ·   " + s.Status);
            c.Text(sub, x + c.S(12), y + c.S(42), width - c.S(24), c.S(22), Theme.Muted);
            y += cardH + c.S(8);

            // ---- cabinet lock info ------------------------------------------------------
            string lockLine = s.LockActive
                ? "机台锁：已占用（输入被拦截，仅保留刷卡）"
                : "机台锁：未占用 — 游戏可正常操作，不会有横幅提示";
            c.Text(lockLine, x, y, width, c.S(22), s.LockActive ? Theme.Warn : Theme.Muted);
            y += c.S(24);

            // ---- read-only (SafeNoWrite) explanation ------------------------------------
            if (s.SafeNoWrite)
            {
                int roH = c.S(64);
                c.Fill(x, y, width, roH, Theme.PanelAlt);
                c.Border(x, y, width, roH, Theme.WarnDark);
                c.Text("当前 SafeNoWrite=true：只能获取源数据、不能执行转生。", x + c.S(10), y + c.S(4),
                    width - c.S(20), c.S(22), Theme.Warn);
                c.TextSmall("确认流程无误后，在 FakerSwap/config.json 里把 SafeNoWrite 改成 false，再重启游戏。",
                    x + c.S(10), y + c.S(30), width - c.S(20), c.S(18), Theme.Muted);
                c.TextSmall("只读期间插件不会写入任何账号数据；顶栏徽章、状态卡与进度页都会标明。",
                    x + c.S(10), y + c.S(46), width - c.S(20), c.S(16), Theme.Muted);
                y += roH + c.S(8);
            }

            // ---- source-login alert -----------------------------------------------------
            if (sourceBusy)
            {
                int alertH = c.S(60);
                c.Fill(x, y, width, alertH, Theme.PanelAlt);
                c.Border(x, y, width, alertH, Theme.WarnDark);
                c.Text("请在机台上刷入源账号卡，读取完成后会自动保存备份并回到本页。", x + c.S(10), y + c.S(4),
                    width - c.S(160), c.S(22), Theme.Warn);
                c.Text(s.PhaseName + " · " + s.Status, x + c.S(10), y + c.S(30), width - c.S(160), c.S(22), Theme.Muted);
                if (c.Button("取消本次操作", x + width - c.S(140), y + c.S(16), c.S(130), c.S(30))) UiActions.Abort();
                y += alertH + c.S(8);
            }

            // ---- loaded source + start --------------------------------------------------
            bool twoColumns = c.Width >= c.S(960);
            if (hasSource) y = DrawSourceCard(c, s, x, y, width, !twoColumns);

            // ---- source data: two alternatives ------------------------------------------
            c.Text("源数据（二选一）", x, y, width, c.S(24), Theme.Accent, false, true);
            y += c.S(22);

            // Two columns need ~960px of client width; below that stack them so nothing is ever
            // clipped.  The layout is recomputed from the real client width on every paint, so a
            // runtime resize (WM_SIZE -> InvalidateRect) reflows immediately.
            if (twoColumns)
            {
                int gapX = c.S(12);
                int listW = (width - gapX) * 62 / 100;
                int rightX = x + listW + gapX;
                int rightW = width - listW - gapX;
                int listBottom = DrawBackupList(c, s, x, y, listW);
                int rightBottom = DrawAcquirePanel(c, s, rightX, y, rightW);
                y = Math.Max(listBottom, rightBottom) + c.S(8);
            }
            else
            {
                y = DrawBackupList(c, s, x, y, width) + c.S(6);
                y = DrawAcquirePanel(c, s, x, y, width) + c.S(8);
            }

            y = DrawSettingsPanel(c, s, x, y, width);
            y = DrawAreaPanel(c, s, x, y, width);
            y = AboutPanel.Draw(c, s, x, y, width);
            return y + pad;
        }

        // ================================================================ area crawl panel

        /// <summary>Keeps the window-thread selection in sync with the game-thread snapshot unless
        /// the operator just changed it (then it commits on the next snapshot echo).</summary>
        private static void SyncAreaState(WindowSnapshot s)
        {
            int now = Environment.TickCount;
            if (_areaSelDirty && unchecked(now - _areaSelSentAt) > 1200 && SameAreaSet(s.SelectedAreaIds))
                _areaSelDirty = false;
            if (!_areaSelDirty)
            {
                _areaSel.Clear();
                if (s.SelectedAreaIds != null)
                {
                    for (int i = 0; i < s.SelectedAreaIds.Length; i++) _areaSel.Add(s.SelectedAreaIds[i]);
                }
            }

            if (_areaFallbackDirty && unchecked(now - _areaFallbackSentAt) > 1200 && s.CrawlFallback == _areaFallback)
                _areaFallbackDirty = false;
            if (!_areaFallbackDirty) _areaFallback = s.CrawlFallback;
        }

        private static bool SameAreaSet(int[] ids)
        {
            if (ids == null) return _areaSel.Count == 0;
            if (_areaSel.Count != ids.Length) return false;
            for (int i = 0; i < ids.Length; i++)
                if (!_areaSel.Contains(ids[i])) return false;
            return true;
        }

        private static int[] AreaSelectionArray()
        {
            // Preserve the display order the operator sees (CrawlAreaIds 的顺序就是跑区顺序)。
            var result = new List<int>(_areaSel.Count);
            for (int i = 0; i < _areaOrder.Count; i++)
            {
                int id = _areaOrder[i];
                if (!_areaSel.Contains(id) || result.Contains(id)) continue;
                result.Add(id);
            }
            foreach (int id in _areaSel)
            {
                if (!result.Contains(id)) result.Add(id);
            }
            return result.ToArray();
        }

        private static void CommitAreaSelection()
        {
            _areaSelDirty = true;
            _areaSelSentAt = Environment.TickCount;
            UiActions.SetCrawlSelection(AreaSelectionArray());
        }

        private static void SelectAllAreas(WindowSnapshot s, bool select)
        {
            _areaSel.Clear();
            if (select && s.Areas != null)
            {
                for (int i = 0; i < s.Areas.Length; i++)
                    if (s.Areas[i] != null) _areaSel.Add(s.Areas[i].Id);
            }
            CommitAreaSelection();
            Toast("区域选择：已选 " + _areaSel.Count + " 个");
        }

        private static void SelectIncompleteAreas(WindowSnapshot s)
        {
            _areaSel.Clear();
            if (s.Areas != null)
            {
                for (int i = 0; i < s.Areas.Length; i++)
                {
                    var area = s.Areas[i];
                    if (area == null || area.IsComplete || area.IsLock || area.IsInfinity) continue;
                    _areaSel.Add(area.Id);
                }
            }
            CommitAreaSelection();
            Toast("仅未完成：已选 " + _areaSel.Count + " 个");
        }

        private static int DrawAreaPanel(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            int headerH = c.S(30);
            c.Fill(x, y, width, headerH, Theme.Panel);
            c.Border(x, y, width, headerH, Theme.Border);
            c.Text("区域（爬图）", x + c.S(8), y, c.S(110), headerH, Theme.Accent, false, true);

            string summary = "已选 " + _areaSel.Count + " / " + (s.Areas != null ? s.Areas.Length : 0) +
                             "   ·   已完成 " + s.CrawlDoneCount +
                             "   ·   完成 " + s.CrawlCompletedCount;
            c.TextSmall(summary, x + c.S(116), y + c.S(7), width - c.S(300), c.S(18), Theme.Muted);

            int btnW = c.S(64);
            if (c.Button(_areaExpanded ? "收起" : "展开", x + width - btnW - c.S(6), y + c.S(3), btnW, c.S(24)))
                _areaExpanded = !_areaExpanded;
            y += headerH + c.S(4);

            if (!_areaExpanded) return y + c.S(4);

            string current = string.IsNullOrEmpty(s.CrawlCurrentArea) ? "—" : s.CrawlCurrentArea;
            string status = "当前区域：" + current +
                            "   ·   本轮已爬 " + s.CrawlDoneThisRound + " 个" +
                            "   ·   勾选区完成 " + s.CrawlCompletedCount + " / " + _areaSel.Count +
                            (s.AtAreaSelect ? "   ·   正在区域选择页" : "   ·   未进选区页（用离线列表勾选）");
            c.TextSmall(status, x, y, width, c.S(18), s.AtAreaSelect ? Theme.Warn : Theme.Muted);
            y += c.S(20);
            c.TextSmall("跑区建议：设置里选「C·真实时长」，填充轨已默认拉满 AP（地图距离有加成）；课题曲普通 80% 过、挑战曲按规则。",
                x, y, width, c.S(18), Theme.Muted);
            y += c.S(22);

            // ---- dedicated crawl (no source data needed) ----------------------------------
            bool crawling = s.CrawlOnly && s.Busy;
            bool canStart = s.LoggedIn && !s.SafeNoWrite && !s.Busy;
            if (crawling)
            {
                if (c.Button("停止跑区", x, y, c.S(104), c.S(26), false)) UiActions.StopCrawl();
                c.TextSmall("跑区中：credit " + s.CurrentCredit + " · 勾选区完成 " + s.CrawlCompletedCount +
                            "/" + _areaSel.Count + " · " + s.GameStage,
                    x + c.S(112), y, Math.Max(0, width - c.S(112)), c.S(26), Theme.Warn);
            }
            else
            {
                if (c.Button("开始跑区（不用源数据）", x, y, c.S(176), c.S(26), true, canStart)) UiActions.StartCrawl();
                string why = !s.LoggedIn ? "需先刷卡登录" :
                             s.SafeNoWrite ? "只读模式不能跑区（先解除只读）" :
                             s.Busy ? "已有任务在运行" :
                             "每 credit 3 轨性隐 BASIC（默认 100.5%/AP），勾选区跑完自动停";
                c.TextSmall(why, x + c.S(184), y, Math.Max(0, width - c.S(184)), c.S(26), Theme.Muted);
            }
            y += c.S(30);

            int gap = c.S(6);
            int rowW = (width - gap * 4) / 5;
            if (c.Button("全选", x, y, rowW, c.S(26))) SelectAllAreas(s, true);
            if (c.Button("全不选", x + (rowW + gap), y, rowW, c.S(26))) SelectAllAreas(s, false);
            if (c.Button("仅未完成", x + (rowW + gap) * 2, y, rowW, c.S(26))) SelectIncompleteAreas(s);
            if (c.Button("刷新列表", x + (rowW + gap) * 3, y, rowW, c.S(26))) UiActions.RefreshAreas();
            if (c.Button("清空已跑", x + (rowW + gap) * 4, y, rowW, c.S(26)))
            {
                UiActions.ClearCrawlDone();
                Toast("已清空「本轮已跑」记录");
            }
            y += c.S(30);

            bool newFallback = c.Toggle(_areaFallback, "爬完所有勾选区后，从头再来继续爬", x, y, width, c.CtrlH);
            if (newFallback != _areaFallback)
            {
                _areaFallback = newFallback;
                _areaFallbackDirty = true;
                _areaFallbackSentAt = Environment.TickCount;
                UiActions.SetCrawlFallback(newFallback);
            }
            y += c.CtrlH + c.S(6);

            var areas = s.Areas ?? new AreaEntry[0];
            if (areas.Length == 0)
            {
                int emptyH = c.S(46);
                c.Fill(x, y, width, emptyH, Theme.PanelAlt);
                c.Border(x, y, width, emptyH, Theme.Border);
                var message = string.IsNullOrEmpty(s.AreaError) ? "区域列表暂不可用（等待 nav 接口）" : s.AreaError;
                c.TextSmall(message, x + c.S(10), y + c.S(4), width - c.S(20), c.S(18), Theme.Warn);
                c.TextSmall("区域列表来自游戏主数据/缓存，Title 阶段也能勾选；登录后自动补上进度与锁定状态。",
                    x + c.S(10), y + c.S(22), width - c.S(20), c.S(16), Theme.Muted);
                return y + emptyH + c.S(8);
            }

            // search: id / name / fuzzy (e.g. "104", "#5500", "天界")
            int searchW = Math.Min(width / 2, c.S(320));
            _areaSearch = c.TextField(_areaSearch, AreaSearchId, x, y, searchW, c.S(26));
            if (c.Button("清空", x + searchW + gap, y, c.S(56), c.S(26))) _areaSearch = "";
            int matchCount = CountMatchingAreas(areas);
            c.TextSmall("id / 名称 / 模糊搜索   ·   匹配 " + matchCount + " / " + areas.Length,
                x + searchW + c.S(64) + gap, y, Math.Max(0, width - searchW - c.S(64) - gap), c.S(26), Theme.Muted);
            y += c.S(30);

            int rowH = c.S(28);
            _areaOrder.Clear();
            int drawnRows = 0;
            for (int i = 0; i < areas.Length; i++)
            {
                var area = areas[i];
                if (area == null) continue;
                _areaOrder.Add(area.Id);
                if (!AreaMatches(area)) continue;
                drawnRows++;
                bool selected = _areaSel.Contains(area.Id);
                bool hover = c.HoverAt(x, y, width, rowH);
                c.Fill(x, y, width, rowH,
                    selected ? Theme.Selected : (hover ? Theme.Panel : (i % 2 == 0 ? Theme.PanelAlt : Theme.Background)));
                if (selected) c.Border(x, y, width, rowH, Theme.AccentDark);

                int box = c.S(15);
                c.CheckMark(selected, x + c.S(8), y + (rowH - box) / 2, box);

                int nameX = x + c.S(8) + box + c.S(8);
                int stateW = c.S(70);
                int progW = c.S(92);
                c.Text(area.Name, nameX, y, width - (nameX - x) - stateW - progW - c.S(10), rowH,
                    area.IsLock ? Theme.Muted : Theme.Text);
                if (!string.IsNullOrEmpty(area.StateText))
                {
                    c.TextSmall(area.StateText, x + width - progW - stateW - c.S(4), y, stateW, rowH,
                        area.IsComplete ? Theme.Good
                        : area.IsLock ? Theme.Muted
                        : area.IsEvent ? Theme.Warn
                        : area.IsCurrent ? Theme.Accent
                        : Theme.Muted);
                }
                c.TextSmall(area.ProgressText, x + width - progW - c.S(6), y, progW, rowH, Theme.Muted);

                if (c.RowClick(x, y, width, rowH))
                {
                    if (selected) _areaSel.Remove(area.Id);
                    else _areaSel.Add(area.Id);
                    CommitAreaSelection();
                }
                y += rowH;
            }
            if (drawnRows == 0)
            {
                c.TextSmall("没有匹配的区域（试试清空搜索）", x, y, width, c.S(22), Theme.Muted);
                y += c.S(24);
            }
            y += c.S(6);
            return y;
        }

        private static bool AreaMatches(AreaEntry area)
        {
            return area != null && TextMatch.Matches(_areaSearch, area.Id, area.Name, "");
        }

        private static int CountMatchingAreas(AreaEntry[] areas)
        {
            int count = 0;
            for (int i = 0; i < areas.Length; i++)
                if (AreaMatches(areas[i])) count++;
            return count;
        }

        /// <summary>Loaded-source card.  <paramref name="compact"/> is used by the single-column
        /// layout so the second alternative stays visible at 900x700.</summary>
        private static int DrawSourceCard(NativeCanvas c, WindowSnapshot s, int x, int y, int width, bool compact)
        {
            int height = compact ? c.S(72) : c.S(118);
            c.Fill(x, y, width, height, Theme.Panel);
            c.Border(x, y, width, height, Theme.AccentDark);

            int textW = width - c.S(300);
            c.Text("已载入源数据", x + c.S(12), y + c.S(4), textW, c.S(22), Theme.Accent, false, true);
            if (compact)
            {
                c.Text(s.SourceDisplay, x + c.S(12), y + c.S(22), textW, c.S(20), Theme.Text, false, true);
                c.TextSmall("文件：" + s.SourceFileName + "   ·   " + s.SourceRecords + " 条   ·   R" +
                            s.SourceRating + "   ·   " + s.SourceFormat,
                    x + c.S(12), y + c.S(42), textW, c.S(16), Theme.Muted);
            }
            else
            {
                c.Text(s.SourceDisplay, x + c.S(12), y + c.S(28), textW, c.S(24), Theme.Text, false, true);
                c.TextSmall("文件：" + s.SourceFileName + "   ·   " + s.SourceRecords + " 条   ·   R" +
                            s.SourceRating + "   ·   " + s.SourceFormat,
                    x + c.S(12), y + c.S(58), textW, c.S(18), Theme.Muted);
                c.TextSmall("保存时间：" + s.SourceSavedAt, x + c.S(12), y + c.S(78), textW, c.S(18), Theme.Muted);
            }

            int actionW = c.S(150);
            int actionX = x + width - actionW - c.S(12);
            bool readOnly = s.SafeNoWrite;
            bool canStart = s.Phase == (int)FlowPhase.SourceReady && !readOnly;
            string startLabel = readOnly ? "开始转生（只读）" : (canStart ? "开始转生 →" : "开始转生");
            bool startClicked = c.Button(startLabel, actionX, y + c.S(12), actionW, c.S(36), true, canStart);
            if (startClicked)
            {
                UiActions.StartTransfer();
            }
            else if (readOnly && c.RowClick(actionX, y + c.S(12), actionW, c.S(36)))
            {
                Toast("只读模式：不能开始转生；请把 config.json 的 SafeNoWrite 改成 false 并重启游戏");
            }
            if (c.Button("清除", actionX - c.S(8) - c.S(66), y + c.S(12), c.S(64), c.S(36), false,
                    !s.Busy)) UiActions.ClearSource();

            string hint;
            if (readOnly)
                hint = "只读模式：即使源数据就绪也不会写账号；解除只读需改 config.json 并重启游戏。";
            else if (canStart)
                hint = "点击后：登录目标账号 → 读取数据 → 生成计划 → 二次确认。";
            else
                hint = "提示：当前阶段（" + s.PhaseName + "）不能开始；请重新载入源数据。";
            c.TextSmall(hint, x + c.S(12), y + (compact ? c.S(56) : c.S(98)), textW, c.S(18),
                readOnly ? Theme.Warn : Theme.Muted);
            return y + height + c.S(8);
        }

        private static int DrawBackupList(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            c.Text("① 使用备份文件", x, y, width - c.S(64), c.LineH, Theme.Text, false, true);
            if (c.Button("刷新", x + width - c.S(58), y - c.S(2), c.S(58), c.S(24))) UiActions.RefreshSources();
            y += c.LineH + c.S(2);

            var sources = s.Sources;
            if (!string.IsNullOrEmpty(s.SourcesError))
            {
                c.Text("读取失败：" + s.SourcesError, x, y, width, c.LineH, Theme.Bad);
                y += c.LineH;
            }

            if (sources == null || sources.Length == 0)
            {
                int emptyH = c.S(70);
                c.Fill(x, y, width, emptyH, Theme.PanelAlt);
                c.Border(x, y, width, emptyH, Theme.Border);
                c.Text("没有找到备份文件。", x + c.S(10), y + c.S(6), width - c.S(20), c.S(22), Theme.Muted);
                c.TextSmall("先用源账号登录一次生成备份，或把 Astral JSON 放进 FakerSwap/Source。", x + c.S(10),
                    y + c.S(30), width - c.S(20), c.S(18), Theme.Muted);
                c.TextSmall("目录：" + FakerSwap.Core.Fs.BackupDir, x + c.S(10), y + c.S(50), width - c.S(20),
                    c.S(16), Theme.Muted);
                return y + emptyH;
            }

            // Columns: the username cell is sized for 8 full-width characters (maimai's limit;
            // the sample "<目标账号>" is 8 chars).  Everything that is saved goes to the
            // Rating / 条数 / 时间 cells, whose values are right-aligned so the numbers line up.
            int nameW = c.MeasureText("ＭＭＭＭＭＭＭＭ", false) + c.S(8);
            int cellPad = c.S(10);
            int rightBlock = Math.Max(c.S(240), width - nameW - c.S(20));
            int ratingW = rightBlock * 30 / 100;
            int countW = rightBlock * 22 / 100;
            int timeW = rightBlock - ratingW - countW;
            int ratingX = x + c.S(8) + nameW;
            int countX = ratingX + ratingW;
            int timeX = countX + countW;
            int ratingRight = ratingX + ratingW - cellPad;
            int countRight = countX + countW - cellPad;
            int timeRight = timeX + timeW - cellPad;

            c.TextSmall("用户名", x + c.S(8), y, nameW, c.S(16), Theme.Muted);
            TextRight(c, "Rating", ratingRight, y, c.S(16), Theme.Muted, false, true);
            TextRight(c, "条数", countRight, y, c.S(16), Theme.Muted, false, true);
            TextRight(c, "时间", timeRight, y, c.S(16), Theme.Muted, false, true);
            y += c.S(18);

            int rowH = c.S(30);
            int selectedIndex = FindIndex(sources, _selectedPath);
            for (int i = 0; i < sources.Length; i++)
            {
                var e = sources[i];
                bool selected = i == selectedIndex;
                bool loaded = !string.IsNullOrEmpty(s.SourcePath) && s.SourcePath == e.Path;
                bool hover = c.HoverAt(x, y, width, rowH);
                uint fill = selected ? Theme.Selected : (hover ? Theme.Panel : (i % 2 == 0 ? Theme.PanelAlt : Theme.Background));
                c.Fill(x, y, width, rowH, fill);
                if (selected) c.Border(x, y, width, rowH, Theme.Accent);
                c.Text(e.Name, x + c.S(8), y, nameW, rowH, loaded ? Theme.Good : Theme.Text);
                TextRight(c, "R" + e.Rating, ratingRight, y, rowH, Theme.Text, false, false);
                TextRight(c, e.Count + " 条", countRight, y, rowH, Theme.Muted, false, true);
                TextRight(c, e.SavedAt, timeRight, y, rowH, Theme.Muted, false, true);
                if (c.RowClick(x, y, width, rowH))
                {
                    _selectedPath = e.Path;
                    Toast("已选择：" + e.Title + "（" + e.Count + " 条）");
                }
                y += rowH;
            }

            y += c.S(6);
            var selectedEntry = selectedIndex >= 0 ? sources[selectedIndex] : null;
            string selText = selectedEntry != null
                ? "已选：" + selectedEntry.Title + "   ·   " + selectedEntry.Count + " 条   ·   " + selectedEntry.SavedAt
                : "点击一行选择文件";
            c.TextSmall(selText, x, y, width, c.S(18), selectedEntry != null ? Theme.Text : Theme.Muted);
            y += c.S(22);

            if (c.Button("使用此文件", x, y, width, c.S(34), true, selectedEntry != null && !s.Busy) && selectedEntry != null)
                UiActions.UseSource(selectedEntry.Path);
            y += c.S(38);
            return y;
        }

        private static int DrawAcquirePanel(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            int pad = c.S(10);
            string body = "让源账号在机台刷卡登录一次，插件读取全量成绩并自动保存成备份文件。";
            int bodyW = width - pad * 2;
            int bodyH = EstimateWrapHeight(c, body, bodyW);
            int height = c.S(8) + c.LineH + c.S(2) + bodyH + c.S(6) + c.S(34) + c.S(8) + 3 * c.S(18) + c.S(8);

            c.Fill(x, y, width, height, Theme.Panel);
            c.Border(x, y, width, height, Theme.Border);

            int cy = y + c.S(8);
            c.Text("② 登录获取源数据", x + pad, cy, bodyW, c.LineH, Theme.Text, false, true);
            cy += c.LineH + c.S(2);
            cy += c.TextWrap(body, x + pad, cy, bodyW, Theme.Muted);
            cy += c.S(6);
            if (c.Button("登录并读取源数据", x + pad, cy, bodyW, c.S(34), true, !s.Busy)) UiActions.BeginSourceAcquire();
            cy += c.S(34) + c.S(8);
            c.TextSmall("· 会要求刷入源账号卡。", x + pad, cy, bodyW, c.S(18), Theme.Muted);
            cy += c.S(18);
            c.TextSmall("· 只读源账号数据，不会修改。", x + pad, cy, bodyW, c.S(18), Theme.Muted);
            cy += c.S(18);
            c.TextSmall("· 读取后保存到 FakerSwap/Backup。", x + pad, cy, bodyW, c.S(18), Theme.Muted);
            cy += c.S(18);
            return cy + c.S(8);
        }

        // ================================================================ settings panel

        private static string PlayRealismLabel(string value)
        {
            return value == "Judge" ? "Judge（秒跳）" : "Duration（真实时长）";
        }

        private static string PlayRealismShort(string value)
        {
            return value == "Judge" ? "B·秒跳" : "C·真实时长";
        }

        private static string LongSongModeShort(string mode)
        {
            if (string.Equals(mode, "Skip", StringComparison.OrdinalIgnoreCase)) return "跳过";
            if (string.Equals(mode, "DirectWrite", StringComparison.OrdinalIgnoreCase)) return "直写";
            return "正常跑";
        }

        private static int LongSongModeIndex(string mode)
        {
            if (string.Equals(mode, "Skip", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(mode, "DirectWrite", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }

        private static string LevelShort(int level)
        {
            switch (level)
            {
                case 0: return "BASIC";
                case 1: return "ADVANCED";
                case 2: return "EXPERT";
                case 3: return "MASTER";
                case 4: return "Re:MASTER";
                default: return "LV" + level;
            }
        }

        private static int WriteModeIndex(string mode)
        {
            if (mode == "Payload") return 1;
            if (mode == "Memory") return 2;
            return 0;
        }

        private static int DrawSettingsPanel(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            int headerH = c.S(30);
            c.Fill(x, y, width, headerH, Theme.Panel);
            c.Border(x, y, width, headerH, Theme.Border);
            c.Text("设置", x + c.S(8), y, c.S(56), headerH, Theme.Accent, false, true);
            string summary = PlayRealismShort(s.PlayRealism) + "   ·   " + LevelShort(s.MinLevel) + " ~ " +
                             LevelShort(s.MaxLevel) + "   ·   长版 " + LongSongModeShort(s.LongSongMode) +
                             (s.OverwriteScores ? "   ·   覆盖成绩 开" : "   ·   覆盖成绩 关");
            c.TextSmall(summary, x + c.S(58), y + c.S(7), width - c.S(272), c.S(18), Theme.Muted);
            if (c.Button(_settingsExpanded ? "收起" : "展开", x + width - c.S(70), y + c.S(3), c.S(64), c.S(24)))
                _settingsExpanded = !_settingsExpanded;
            y += headerH + c.S(6);
            if (!_settingsExpanded) return y + c.S(2);

            int gap = c.S(14);
            int colW = (width - gap) / 2;
            int rightX = x + colW + gap;
            int leftY = y;
            int rightY = y;

            // 左列：玩法 / 难度 / 长版
            leftY = SettingsDropdownRow(c, 1, "游玩模式", PlayRealismShort(s.PlayRealism),
                new[] { "C·真实时长", "B·秒跳" }, s.PlayRealism == "Judge" ? 1 : 0, x, leftY, colW);
            leftY = SettingsDropdownRow(c, 2, "难度下限", LevelShort(s.MinLevel),
                new[] { "BASIC", "ADVANCED", "EXPERT", "MASTER", "Re:MASTER" }, s.MinLevel, x, leftY, colW);
            leftY = SettingsDropdownRow(c, 3, "难度上限", LevelShort(s.MaxLevel),
                new[] { "BASIC", "ADVANCED", "EXPERT", "MASTER", "Re:MASTER" }, s.MaxLevel, x, leftY, colW);
            leftY = SettingsDropdownRow(c, 4, "长版歌曲", LongSongModeShort(s.LongSongMode),
                new[] { "正常跑", "跳过", "直写" }, LongSongModeIndex(s.LongSongMode), x, leftY, colW);
            leftY = SettingsToggleRow(c, "覆盖已有成绩（不同分强制重写）", s.OverwriteScores,
                UiActions.SetOverwriteScores, x, leftY, colW);
            leftY = SettingsToggleRow(c, "达成率镜像（.9xxx 转 .0xxx 进位）", s.AchievementMirrorUp,
                UiActions.SetAchievementMirrorUp, x, leftY, colW);
            leftY = SettingsNumberRow(c, "游玩次数覆盖 %（0=不改）", s.PlayCountPercent, 0, 100, 5,
                UiActions.SetPlayCountPercent, x, leftY, colW);
            leftY = SettingsNumberRow(c, "DX 加分下限", s.DxBonusMin, 0, 50, 1,
                UiActions.SetDxBonusMin, x, leftY, colW);
            leftY = SettingsNumberRow(c, "DX 加分上限", s.DxBonusMax, 0, 50, 1,
                UiActions.SetDxBonusMax, x, leftY, colW);

            // 右列：安全 / 选票
            rightY = SettingsToggleRow(c, "机台锁", s.LockInput,
                UiActions.SetLockInput, rightX, rightY, colW);
            rightY = SettingsToggleRow(c, "开始前二次确认", s.ConfirmBeforeStart,
                UiActions.SetConfirmBeforeStart, rightX, rightY, colW);
            rightY = SettingsToggleRow(c, "选票保持（每次用上次手选的票）", s.TicketUseKeep,
                UiActions.SetTicketUseKeep, rightX, rightY, colW);
            rightY = SettingsTicketRow(c, s, rightX, rightY, colW);
            y = Math.Max(leftY, rightY);
            c.TextSmall("* 改动立即写入 FakerSwap/config.json；多数项下一批/下次开始任务生效。Dev/实验项仅测试用。",
                x, y, width, c.S(18), Theme.Muted);
            y += c.S(24);
            return y;
        }

        private static int SettingsToggleRow(NativeCanvas c, string label, bool value, Action<bool> setter,
            int x, int y, int width)
        {
            c.TextSmall(label, x, y + c.S(5), width - c.S(92), c.S(18), Theme.Muted);
            bool next = c.Toggle(value, value ? "开" : "关", x + width - c.S(86), y + c.S(1), c.S(86), c.S(26));
            if (next != value && setter != null) setter(next);
            return y + c.S(32);
        }

        /// <summary>数值设置行：[-] 值 [+]（无键盘输入，避免窗口焦点问题）。</summary>
        private static int SettingsNumberRow(NativeCanvas c, string label, int value, int min, int max, int step,
            Action<int> setter, int x, int y, int width)
        {
            c.TextSmall(label, x, y + c.S(5), width - c.S(150), c.S(18), Theme.Muted);
            int btn = c.S(30);
            int valueW = c.S(64);
            int total = btn * 2 + valueW + c.S(8);
            int bx = x + width - total;
            if (c.Button("-", bx, y + c.S(1), btn, c.S(26)) && setter != null)
                setter(Math.Max(min, value - step));
            c.Text(value.ToString(), bx + btn + c.S(4), y, valueW, c.S(26), Theme.Text, true, false);
            if (c.Button("+", bx + btn + valueW + c.S(8), y + c.S(1), btn, c.S(26)) && setter != null)
                setter(Math.Min(max, value + step));
            return y + c.S(32);
        }

        /// <summary>选票：默认不选票；「下次用票」让下一次选票页交给人工，选完自动记住票 ID。</summary>
        private static int SettingsTicketRow(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            string state;
            if (s.TicketUseKeep)
                state = "保持：用票 " + (s.TicketLastManualId > 0 ? "#" + s.TicketLastManualId : "（还没手选过）");
            else if (s.TicketUseNext)
                state = "下次：手动选票一次";
            else
                state = "默认：不选票（TimeUp）";
            c.TextSmall("选票：" + state, x, y + c.S(5), width - c.S(96), c.S(18), Theme.Muted);
            if (c.Button(s.TicketUseNext ? "取消" : "下次用票", x + width - c.S(90), y + c.S(1), c.S(90), c.S(26), s.TicketUseNext))
                UiActions.SetTicketUseNext(!s.TicketUseNext);
            return y + c.S(32);
        }

        private static int SettingsSafeNoWriteRow(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            bool value = s.SafeNoWrite;
            c.TextSmall("只读模式（不写账号）", x, y + c.S(5), width - c.S(92), c.S(18), Theme.Muted);
            bool next = c.Toggle(value, value ? "开" : "关", x + width - c.S(86), y + c.S(1), c.S(86), c.S(26));
            if (next != value)
            {
                if (next) UiActions.EnableReadOnly();
                else _unlockDialog = true; // turning writes ON needs the two-step confirmation
            }
            return y + c.S(32);
        }

        private static int SettingsCrawlFallbackRow(NativeCanvas c, int x, int y, int width)
        {
            c.TextSmall("爬完再来一轮", x, y + c.S(5), width - c.S(92), c.S(18), Theme.Muted);
            bool next = c.Toggle(_areaFallback, _areaFallback ? "开" : "关", x + width - c.S(86), y + c.S(1),
                c.S(86), c.S(26));
            if (next != _areaFallback)
            {
                _areaFallback = next;
                _areaFallbackDirty = true;
                _areaFallbackSentAt = Environment.TickCount;
                UiActions.SetCrawlFallback(next);
            }
            return y + c.S(32);
        }

        private static int SettingsBatchRow(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            c.TextSmall("批大小（2 会被服务器拒）", x, y + c.S(5), width - c.S(150), c.S(18), Theme.Muted);
            int bw = (c.S(144) - c.S(6)) / 2;
            int bx = x + width - c.S(144);
            if (c.Button("1 首", bx, y + c.S(1), bw, c.S(26), s.BatchSize == 1)) UiActions.SetBatchSize(1);
            if (c.Button("3 首", bx + bw + c.S(6), y + c.S(1), bw, c.S(26), s.BatchSize == 3)) UiActions.SetBatchSize(3);
            return y + c.S(32);
        }

        private static int SettingsDropdownRow(NativeCanvas c, int fieldId, string label, string valueText,
            string[] options, int selected, int x, int y, int width)
        {
            c.TextSmall(label, x, y + c.S(5), width - c.S(154), c.S(18), Theme.Muted);
            int bw = c.S(150);
            int bx = x + width - bw;
            int by = y + c.S(1);
            int bh = c.S(26);
            bool hover = c.HoverAt(bx, by, bw, bh);
            c.Fill(bx, by, bw, bh, hover ? Theme.Panel : Theme.PanelAlt);
            c.Border(bx, by, bw, bh, hover ? Theme.AccentDark : Theme.Border);
            c.Text(valueText, bx + c.S(6), by, bw - c.S(24), bh, Theme.Text);
            c.TextSmall("▼", bx + bw - c.S(18), by, c.S(16), bh, Theme.Muted);
            if (c.RowClick(bx, by, bw, bh))
            {
                _openDropdownField = fieldId;
                _dropdownOptions = options;
                _dropdownSelected = selected;
                _dropdownX = bx;
                _dropdownW = bw;
                _dropdownControlY = by;
                _dropdownY = by + bh + c.S(2);
            }
            return y + c.S(32);
        }

        private static void DrawDropdownOverlay(NativeCanvas c)
        {
            string[] options = _dropdownOptions;
            if (options == null || options.Length == 0)
            {
                _openDropdownField = 0;
                return;
            }
            int itemH = c.S(24);
            int listH = options.Length * itemH + c.S(8);
            int x = _dropdownX;
            int w = _dropdownW;
            // _dropdownY/_dropdownControlY 是**内容坐标**：弹层在 EndContent 之后画，c.Fill 的 ScreenY 不再加 scroll 偏移，
            // 必须先用 ContentToScreenY 换成客户区 Y，否则列表会画到窗口外（实机：点了没反应）。
            int y = c.ContentToScreenY(_dropdownY);
            int controlY = c.ContentToScreenY(_dropdownControlY);
            if (y + listH > c.Height - c.S(8)) y = controlY - listH - c.S(2);
            if (y < c.S(8)) y = c.S(8);

            c.Fill(x - c.S(1), y - c.S(1), w + c.S(2), listH + c.S(2), Theme.Border);
            c.Fill(x, y, w, listH, Theme.Panel);
            int iy = y + c.S(4);
            for (int i = 0; i < options.Length; i++)
            {
                bool hover = c.HoverAt(x, iy, w, itemH);
                if (i == _dropdownSelected) c.Fill(x, iy, w, itemH, Theme.Selected);
                else if (hover) c.Fill(x, iy, w, itemH, Theme.PanelAlt);
                c.Text(options[i], x + c.S(8), iy, w - c.S(16), itemH, Theme.Text);
                if (c.RowClick(x, iy, w, itemH))
                {
                    CommitDropdown(i);
                    return;
                }
                iy += itemH;
            }

            if (c.HasClick)
            {
                // Click outside the list: just close.
                c.HasClick = false;
                _openDropdownField = 0;
            }
        }

        private static void CommitDropdown(int index)
        {
            int field = _openDropdownField;
            _openDropdownField = 0;
            switch (field)
            {
                case 1:
                    UiActions.SetPlayRealism(index == 1 ? "Judge" : "Duration");
                    break;
                case 2:
                    UiActions.SetWriteMode(index == 2 ? "Memory" : index == 1 ? "Payload" : "Both");
                    break;
                case 3:
                    UiActions.SetMinLevel(index);
                    break;
                case 4:
                    UiActions.SetMaxLevel(index);
                    break;
                case 5:
                    UiActions.SetLongSongMode(index == 1 ? "Skip" : index == 2 ? "DirectWrite" : "PlayWithOne");
                    break;
            }
        }

        // ================================================================ confirm page

        private static int DrawConfirm(NativeCanvas c, WindowSnapshot s)
        {
            int pad = c.Pad;
            int x = pad;
            int width = c.Width - pad * 2 - c.S(10);
            int y = 0;

            c.TextTitle("二次确认", x, y, width, c.S(30), Theme.Warn);
            y += c.S(34);
            c.Text("确认后立即开始自动转生，过程中机台会被锁定、输入被拦截。", x, y, width, c.LineH, Theme.Muted);
            y += c.S(30);

            string[] labels =
            {
                "源快照", "待转生条数", "批次数", "预估总时长", "难度范围", "每批数量", "写入方式", "游玩模式"
            };
            string estimate = string.IsNullOrEmpty(s.EstimateText) ? FormatDuration(s.EstimateSeconds) : s.EstimateText;
            string[] values =
            {
                s.SourceDisplay,
                s.PlanTotal + " 条" + (s.PlanDone > 0 ? "（已完成 " + s.PlanDone + "）" : ""),
                s.PlanCredits + " 批 × " + s.BatchSize + " 首",
                estimate,
                LevelName(s.MinLevel) + " ~ " + LevelName(s.MaxLevel),
                s.BatchSize + " 首/批",
                s.WriteMode,
                PlayRealismLabel(s.PlayRealism)
            };

            int rowH = c.S(28);
            int boxH = c.S(10) + labels.Length * rowH + c.S(6);
            c.Fill(x, y, width, boxH, Theme.Panel);
            c.Border(x, y, width, boxH, Theme.Border);
            int ry = y + c.S(10);
            for (int i = 0; i < labels.Length; i++)
            {
                c.Text(labels[i], x + c.S(14), ry, c.S(110), rowH, Theme.Muted);
                c.Text(values[i], x + c.S(136), ry, width - c.S(150), rowH, Theme.Text);
                ry += rowH;
            }
            y += boxH + c.S(12);

            int warnH = c.S(66);
            c.Fill(x, y, width, warnH, Theme.PanelAlt);
            c.Border(x, y, width, warnH, Theme.WarnDark);
            c.Text("请确认源账号与目标账号没有搞反；开始后计划不可修改。", x + c.S(10), y + c.S(4),
                width - c.S(20), c.S(22), Theme.Warn);
            c.TextSmall("每 3 首一批：选曲 → 结算 → 改成绩 → 上传 → 登出 → 续关登录，循环到最后一批。", x + c.S(10),
                y + c.S(28), width - c.S(20), c.S(18), Theme.Muted);
            c.TextSmall("随时可以点「中止任务」：不续关，打完当前 credit 剩余轨后登出收尾。", x + c.S(10),
                y + c.S(48), width - c.S(20), c.S(18), Theme.Muted);
            y += warnH + c.S(14);

            int gap = c.S(10);
            int bw = (width - gap) / 2;
            if (c.Button("取消（返回源数据）", x, y, bw, c.S(40))) UiActions.CancelStart();
            if (c.Button("确认开始转生", x + bw + gap, y, bw, c.S(40), true)) UiActions.ConfirmStart();
            y += c.S(48);

            c.TextSmall("提示：确认前请确保没有人在机台旁操作，房间不要太吵（会读卡）。", x, y, width, c.S(18), Theme.Muted);
            y += c.S(22);
            return y + pad;
        }

        // ================================================================ task / progress page

        private static int DrawTask(NativeCanvas c, WindowSnapshot s)
        {
            int pad = c.Pad;
            int x = pad;
            int width = c.Width - pad * 2 - c.S(10);
            int y = 0;

            string headline;
            uint color;
            if (s.Phase == (int)FlowPhase.Stopping)
            {
                headline = "正在中止…";
                color = Theme.Warn;
            }
            else if (s.Phase == (int)FlowPhase.Transferring)
            {
                headline = "正在转生";
                color = Theme.Accent;
            }
            else
            {
                headline = "准备中：" + s.PhaseName;
                color = Theme.Accent;
            }

            int headlineW = width;
            if (s.SafeNoWrite)
            {
                string roText = "只读（不写账号）";
                int pw = c.MeasureText(roText, true) + c.S(16);
                int px = x + width - pw;
                c.Fill(px, y + c.S(3), pw, c.S(23), Theme.WarnDark);
                c.Border(px, y + c.S(3), pw, c.S(23), Theme.Warn);
                c.Text(roText, px, y + c.S(3), pw, c.S(23), Theme.Warn, true, true);
                headlineW -= pw + c.S(8);
            }
            c.TextTitle(headline, x, y, headlineW, c.S(30), color);
            y += c.S(34);
            c.Text(s.Status, x, y, width, c.LineH, Theme.Text);
            y += c.S(28);

            // progress bar
            int barH = c.S(24);
            double progress = s.Progress;
            c.ProgressBar(x, y, width, barH, progress,
                s.Phase == (int)FlowPhase.Stopping ? Theme.Warn : Theme.Accent, Theme.Track);
            string pct = (progress * 100.0).ToString("0.0") + "%";
            c.Text(pct, x + c.S(8), y, width - c.S(16), barH, Theme.Text, true, true);
            y += barH + c.S(6);
            c.TextSmall("已完成 " + s.PlanDone + " / " + s.PlanTotal + " 条   ·   " + s.PlanCredits + " 批",
                x, y, width - c.S(150), c.S(18), Theme.Muted);
            if (s.SafeNoWrite)
                TextRight(c, "只读 · 不写账号", x + width, y, c.S(18), Theme.Warn, true, true);
            y += c.S(24);

            // info cells (2 columns)
            int gap = c.S(8);
            int colW = (width - gap) / 2;
            int cellH = c.S(46);
            string creditText = s.CurrentCredit > 0 ? s.CurrentCredit + " / " + s.PlanCredits : "—";
            string trackText = s.CurrentTrack > 0 ? s.CurrentTrack + " / 3" : "—";
            Cell(c, x, y, colW, cellH, "当前批次", creditText, Theme.Text);
            Cell(c, x + colW + gap, y, colW, cellH, "当前轨", trackText, Theme.Text);
            y += cellH + c.S(6);
            Cell(c, x, y, colW, cellH, "已用时", s.ElapsedText, Theme.Text);
            Cell(c, x + colW + gap, y, colW, cellH, "剩余估算", s.RemainingText, Theme.Text);
            y += cellH + c.S(8);

            // current song
            string song = string.IsNullOrEmpty(s.CurrentSong) ? "（等待 Flow 提供当前曲目）" : s.CurrentSong;
            c.Fill(x, y, width, c.S(30), Theme.PanelAlt);
            c.Border(x, y, width, c.S(30), Theme.Border);
            c.Text("当前曲目：" + song, x + c.S(10), y, width - c.S(20), c.S(30),
                string.IsNullOrEmpty(s.CurrentSong) ? Theme.Muted : Theme.Text);
            y += c.S(34);

            // pending playlist (collapsed to one summary line by default)
            y = DrawQueuePanel(c, s, x, y, width);
            y += c.S(6);

            string stage = "游戏阶段：" + s.GameStage + (s.LockActive ? "   ·   机台已锁定" : "");
            if (!string.IsNullOrEmpty(s.CrawlCurrentArea))
                stage += "   ·   区域：" + s.CrawlCurrentArea + "（本轮 " + s.CrawlDoneThisRound + " 个）";
            c.TextSmall(stage, x, y, width, c.S(18), s.LockActive ? Theme.Warn : Theme.Muted);
            y += c.S(24);

            // abort
            if (c.DangerButton("中止任务", x, y, width, c.S(42), s.Phase != (int)FlowPhase.Stopping))
                UiActions.Abort();
            y += c.S(44);
            c.TextSmall("中止后：不续关；当前 credit 剩余轨用性隐填充并跳过（不改成绩），登出后结束。", x, y, width,
                c.S(18), Theme.Muted);
            y += c.S(26);

            // log tail
            c.Text("日志（最近 10 行）", x, y, width, c.S(20), Theme.Accent, false, true);
            y += c.S(24);
            string[] lines = LogTail.Get(10);
            int lineH = c.S(18);
            int logH = lineH * Math.Max(1, lines.Length) + c.S(12);
            c.Fill(x, y, width, logH, Theme.Input);
            c.Border(x, y, width, logH, Theme.Border);
            int ly = y + c.S(6);
            if (lines.Length == 0)
            {
                c.TextSmall("（暂无日志）", x + c.S(8), ly, width - c.S(16), lineH, Theme.Muted);
            }
            else
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    c.TextSmall(lines[i], x + c.S(8), ly, width - c.S(16), lineH, Theme.Muted);
                    ly += lineH;
                }
            }
            y += logH + c.S(8);
            return y + pad;
        }

        private static void Cell(NativeCanvas c, int x, int y, int w, int h, string label, string value, uint color)
        {
            c.Fill(x, y, w, h, Theme.PanelAlt);
            c.Border(x, y, w, h, Theme.Border);
            c.TextSmall(label, x + c.S(8), y + c.S(3), w - c.S(16), c.S(16), Theme.Muted);
            c.Text(value, x + c.S(8), y + c.S(18), w - c.S(16), c.S(24), color, false, true);
        }

        // ================================================================ pending playlist

        /// <summary>
        /// Collapsible "待办曲目" playlist for the task page.  Collapsed it is one summary row
        /// ("N 批 / M 首（★课题 K）▸"); clicking anywhere on that row toggles the per-credit list
        /// with each planned chart and its progress.  Everything is drawn from the immutable
        /// <see cref="WindowSnapshot"/> copy, so this never touches the flow / Unity objects.
        /// </summary>
        private static int DrawQueuePanel(NativeCanvas c, WindowSnapshot s, int x, int y, int width)
        {
            int headerH = c.S(30);
            var batches = s.Queue;

            if (batches == null || batches.Length == 0)
            {
                c.Fill(x, y, width, headerH, Theme.Panel);
                c.Border(x, y, width, headerH, Theme.Border);
                c.Text("待办曲目", x + c.S(8), y, c.S(88), headerH, Theme.Accent, false, true);
                c.TextSmall("尚未生成队列（进入转生阶段后显示每一批每一首）", x + c.S(96), y,
                    Math.Max(c.S(120), width - c.S(104)), headerH, Theme.Muted);
                return y + headerH;
            }

            int total = Math.Max(0, s.QueueTotalSlots);
            int done = Math.Max(0, s.QueueDoneSlots);

            // ---- summary row (the whole row is the expand / collapse hit target) -------
            bool hover = c.HoverAt(x, y, width, headerH);
            c.Fill(x, y, width, headerH, hover ? Theme.Panel : Theme.PanelAlt);
            c.Border(x, y, width, headerH, hover || _queueExpanded ? Theme.AccentDark : Theme.Border);
            c.Text("待办曲目", x + c.S(8), y, c.S(90), headerH, Theme.Accent, false, true);

            string summary = batches.Length + " 批 / " + total + " 首";
            int tx = x + c.S(100);
            c.TextSmall(summary, tx, y, Math.Max(c.S(80), width - c.S(248)), headerH, Theme.Text);
            int mx = tx + c.MeasureSmall(summary, false);
            string mission = "（★课题 " + s.QueueMissionSlots + "）";
            c.TextSmall(mission, mx, y, Math.Max(c.S(60), width - (mx - x) - c.S(150)), headerH,
                s.QueueMissionSlots > 0 ? Theme.Warn : Theme.Muted);

            string state = "";
            if (s.QueueRunning && s.QueueCurrentIndex >= 0)
                state = " · 进行中 " + (s.QueueCurrentIndex + 1) + "/" + batches.Length;
            else if (total > 0 && done >= total)
                state = " · 已完成 " + done + "/" + total;
            else if (done > 0)
                state = " · 已玩 " + done + "/" + total;
            if (state.Length > 0)
            {
                int sx = mx + c.MeasureSmall(mission, false);
                int arrowX = x + width - c.S(26);
                if (arrowX - sx > c.S(60))
                    c.TextSmall(state, sx, y, arrowX - sx, headerH,
                        s.QueueRunning ? Theme.Accent : Theme.Muted);
            }

            c.Text(_queueExpanded ? "▾" : "▸", x + width - c.S(24), y, c.S(18), headerH,
                hover ? Theme.Text : Theme.Muted, true, true);
            if (c.RowClick(x, y, width, headerH)) _queueExpanded = !_queueExpanded;

            y += headerH + c.S(4);
            if (!_queueExpanded) return y;

            // ---- expanded: one row per credit -----------------------------------------
            // Row layout:  [✓/▶/·] 3/33 [2/3] 11813-3 12015-4 ★11976-2
            int rowH = c.S(24);
            int count = batches.Length;
            for (int i = 0; i < count; i++)
            {
                var batch = batches[i];
                if (batch == null) continue;

                bool rowHover = c.HoverAt(x, y, width, rowH);
                c.Fill(x, y, width, rowH, batch.IsCurrent
                    ? Theme.Selected
                    : (rowHover ? Theme.Panel : (i % 2 == 0 ? Theme.PanelAlt : Theme.Background)));
                if (batch.IsCurrent) c.Border(x, y, width, rowH, Theme.AccentDark);

                bool active = batch.IsCurrent || batch.IsDone;
                c.Text(batch.IsDone ? "✓" : (batch.IsCurrent ? "▶" : "·"),
                    x + c.S(8), y, c.S(14), rowH,
                    batch.IsDone ? Theme.Good : (batch.IsCurrent ? Theme.Accent : Theme.Muted), false, active);
                c.Text((batch.Index + 1) + "/" + count, x + c.S(24), y, c.S(48), rowH,
                    batch.IsDone ? Theme.Good : Theme.Text);
                c.TextSmall("[" + batch.DoneCount + "/" + batch.Count + "]", x + c.S(76), y, c.S(46), rowH,
                    batch.Count > 0 && batch.DoneCount >= batch.Count ? Theme.Good : Theme.Muted);

                var slots = batch.Slots;
                if (slots != null)
                {
                    int sx = x + c.S(128);
                    int limit = x + width - c.S(8);
                    for (int k = 0; k < slots.Length; k++)
                    {
                        var slot = slots[k];
                        if (slot == null) continue;
                        string label = slot.Label;
                        int labelW = c.MeasureSmall(label, false);
                        if (sx + labelW > limit)
                        {
                            if (limit - sx > c.S(6)) c.TextSmall("…", sx, y, limit - sx, rowH, Theme.Muted);
                            break;
                        }
                        c.TextSmall(label, sx, y, labelW + c.S(2), rowH,
                            slot.IsMission ? Theme.Warn
                            : (batch.IsDone || slot.IsDone ? Theme.Muted : Theme.Text));
                        sx += labelW + c.S(10);
                    }
                }
                y += rowH;
            }

            c.TextSmall("★ 课题曲   ·   ▶ 当前批   ·   ✓ 已完成；点击上方摘要行收起", x, y + c.S(2), width,
                c.S(16), Theme.Muted);
            return y + c.S(22);
        }

        // ================================================================ unlock confirmation

        private static void DrawUnlockDialog(NativeCanvas c)
        {
            int w = Math.Min(c.Width - c.S(40), c.S(620));
            int tw = w - c.S(36);
            const string text1 = "解除后插件恢复写入账号的能力：可以执行转生、上传成绩。上次事故（2026-11-14）就是一次强制 DataSave 覆盖了空档。";
            const string text2 = "操作：把 FakerSwap/config.json 的 SafeNoWrite 改成 false（立即写盘）。需要重启游戏才会真正生效，只读徽章在重启前不会消失。";
            int h1 = EstimateWrapHeight(c, text1, tw);
            int h2 = EstimateWrapHeight(c, text2, tw);
            int h = c.S(12) + c.S(34) + h1 + c.S(8) + h2 + c.S(16) + c.S(36) + c.S(16);
            int x = (c.Width - w) / 2;
            int y = (c.Height - h) / 2;
            if (y < c.S(8)) y = c.S(8);

            c.Fill(0, 0, c.Width, c.Height, Theme.Input);
            c.Fill(x, y, w, h, Theme.Panel);
            c.Border(x, y, w, h, Theme.Warn);

            c.TextTitle("解除只读模式？", x + c.S(18), y + c.S(10), tw, c.S(30), Theme.Warn);
            int ty = y + c.S(12) + c.S(34);
            ty += c.TextWrap(text1, x + c.S(18), ty, tw, Theme.Text);
            ty += c.S(8);
            ty += c.TextWrap(text2, x + c.S(18), ty, tw, Theme.Muted);

            int by = y + h - c.S(16) - c.S(36);
            int bw = (w - c.S(54)) / 2;
            if (c.Button("取消（保持只读）", x + c.S(18), by, bw, c.S(36))) _unlockDialog = false;
            if (c.Button("确认解除并写 config", x + c.S(36) + bw, by, bw, c.S(36), true))
            {
                _unlockDialog = false;
                UiActions.DisableSafeNoWriteOnDisk();
            }
        }

        // ================================================================ helpers

        private static bool IsTaskPhase(int phase)
        {
            return phase == (int)FlowPhase.TargetLoginPending ||
                   phase == (int)FlowPhase.TargetReading ||
                   phase == (int)FlowPhase.Planning ||
                   phase == (int)FlowPhase.Transferring ||
                   phase == (int)FlowPhase.Stopping;
        }

        private static string PhaseName(int phase)
        {
            if (phase == (int)FlowPhase.Idle) return "空闲";
            if (phase == (int)FlowPhase.NeedSource) return "选择源数据";
            if (phase == (int)FlowPhase.SourceLoginPending) return "等待源账号刷卡";
            if (phase == (int)FlowPhase.SourceReading) return "读取源数据";
            if (phase == (int)FlowPhase.SourceReady) return "源数据就绪";
            if (phase == (int)FlowPhase.TargetLoginPending) return "等待目标账号刷卡";
            if (phase == (int)FlowPhase.TargetReading) return "读取目标账号";
            if (phase == (int)FlowPhase.Planning) return "生成计划";
            if (phase == (int)FlowPhase.AwaitingConfirm) return "等待二次确认";
            if (phase == (int)FlowPhase.Transferring) return "转生中";
            if (phase == (int)FlowPhase.Stopping) return "正在中止";
            if (phase == (int)FlowPhase.Finished) return "已完成";
            if (phase == (int)FlowPhase.Failed) return "失败";
            return "阶段 " + phase;
        }

        private static uint PhaseColor(int phase)
        {
            if (phase == (int)FlowPhase.Failed) return Theme.Bad;
            if (phase == (int)FlowPhase.Finished || phase == (int)FlowPhase.SourceReady) return Theme.Good;
            if (phase == (int)FlowPhase.Stopping) return Theme.Warn;
            if (phase == (int)FlowPhase.SourceLoginPending || phase == (int)FlowPhase.TargetLoginPending ||
                phase == (int)FlowPhase.AwaitingConfirm || phase == (int)FlowPhase.NeedSource) return Theme.Warn;
            if (phase == (int)FlowPhase.Transferring || phase == (int)FlowPhase.SourceReading ||
                phase == (int)FlowPhase.TargetReading || phase == (int)FlowPhase.Planning) return Theme.Accent;
            return Theme.Muted;
        }

        private static string LevelName(int level)
        {
            switch (level)
            {
                case 0: return "BASIC";
                case 1: return "ADVANCED";
                case 2: return "EXPERT（红）";
                case 3: return "MASTER（紫）";
                case 4: return "Re:MASTER（白）";
                default: return "难度 " + level;
            }
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int total = (int)Math.Round(seconds);
            if (total < 60) return total + " 秒";
            if (total < 3600) return (total / 60) + " 分 " + (total % 60) + " 秒";
            return (total / 3600) + " 小时 " + (total % 3600 / 60) + " 分";
        }

        private static void TextRight(NativeCanvas c, string text, int right, int y, int h, uint color, bool bold,
            bool small)
        {
            if (string.IsNullOrEmpty(text)) return;
            int w = small ? c.MeasureSmall(text, bold) : c.MeasureText(text, bold);
            if (w <= 0) return;
            if (small) c.TextSmall(text, right - w, y, w + c.S(4), h, color, bold);
            else c.Text(text, right - w, y, w + c.S(4), h, color, false, bold);
        }

        private static int EstimateWrapHeight(NativeCanvas c, string text, int width)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return c.FontHeight + c.S(4);
            int total = c.MeasureText(text, false);
            int lines = Math.Max(1, (total + width - 1) / width);
            return lines * (c.FontHeight + c.S(4));
        }

        private static int FindIndex(SourceEntry[] entries, string path)
        {
            if (entries == null || string.IsNullOrEmpty(path)) return -1;
            for (int i = 0; i < entries.Length; i++)
                if (entries[i] != null && entries[i].Path == path) return i;
            return -1;
        }

        private static void ReconcileSelection(WindowSnapshot s)
        {
            if (!string.IsNullOrEmpty(_selectedPath) && FindIndex(s.Sources, _selectedPath) >= 0) return;
            if (!string.IsNullOrEmpty(s.SourcePath) && FindIndex(s.Sources, s.SourcePath) >= 0)
            {
                _selectedPath = s.SourcePath;
                return;
            }
            if (string.IsNullOrEmpty(_selectedPath)) return;
            _selectedPath = null;
        }

        private static void ConsumeNotice()
        {
            string notice = WindowRequests.TakeNotice();
            if (!string.IsNullOrEmpty(notice)) Toast(notice);
        }

        private static void Toast(string text)
        {
            _toast = text;
            _toastUntil = Environment.TickCount + 5000;
        }

        private static string TakeToast()
        {
            if (_toast == null) return null;
            if (Environment.TickCount > _toastUntil)
            {
                _toast = null;
                return null;
            }
            string text = _toast;
            _toast = null;
            return text;
        }
    }
}
