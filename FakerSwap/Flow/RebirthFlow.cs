using System;
using System.Collections.Generic;
using FakerSwap.Core;
using FakerSwap.Data;
using Manager;

namespace FakerSwap.Flow
{
    public enum FlowPhase
    {
        Idle = 0,
        /// <summary>Waiting for the operator to pick a backup file or allow a live source login.</summary>
        NeedSource,
        /// <summary>We asked the operator to swipe the SOURCE card; the game is at the entry screen.</summary>
        SourceLoginPending,
        /// <summary>Source account logged in, waiting for the song-select screen to snapshot the data.</summary>
        SourceReading,
        /// <summary>Snapshot taken and written to disk.</summary>
        SourceReady,
        /// <summary>We asked the operator to swipe the TARGET card.</summary>
        TargetLoginPending,
        /// <summary>Target logged in, waiting for song select to read its current state.</summary>
        TargetReading,
        /// <summary>Building the transfer plan + time estimate.</summary>
        Planning,
        /// <summary>Estimate is ready and we wait for the operator's second confirmation.</summary>
        AwaitingConfirm,
        /// <summary>Actually transferring records, credit by credit.</summary>
        Transferring,
        /// <summary>Stop requested; finishing the current credit and logging out.</summary>
        Stopping,
        Finished,
        Failed
    }

    public sealed class TransferPlan
    {
        public List<TransferItem> Items = new List<TransferItem>();
        public List<int> SkippedMusicIds = new List<int>();
        public TimeSpan Estimate;
        public int Credits;
        public string EstimateText = "";
        public int Missions;
        /// <summary>Source charts the target account already had at the same/better achievement.</summary>
        public int AlreadyDone;

        public int Total => Items.Count;
        public int Done
        {
            get
            {
                int n = 0;
                foreach (var i in Items) if (i.Done) n++;
                return n;
            }
        }
    }

    /// <summary>
    /// The "account rebirth" state machine.
    ///
    ///   Idle / NeedSource
    ///     -> [BeginSourceAcquire] SourceLoginPending -> SourceReading -> (logout) -> SourceReady
    ///     -> [StartTransfer] TargetLoginPending -> TargetReading -> Planning -> AwaitingConfirm
    ///     -> [ConfirmStart] Transferring: {login -> gap -> 3 tracks -> result -> continue/end ->
    ///                       DataSave upload -> logout -> re-login} x N
    ///     -> Finished
    ///   Any error / abort / quit -> wrap-up: guaranteed logout attempt + MachineLock.Off().
    ///
    /// Hard rules (see docs/architecture.md + docs/upload-protocol.md):
    ///   * a credit is 3 tracks or 1 track, NEVER exactly 2 playlogs;
    ///   * within a credit the tracks are at least <c>TrackGapSec</c> apart and the upload happens
    ///     at least <c>UploadCooldownSec</c> after the login that owns the session;
    ///   * a session always ends with the game's own DataSave/logout chain - we never kill anything;
    ///   * aborting mid-credit: current track is skipped without modification, the remaining tracks
    ///     of that credit are played as <c>Config.FillerMusicId</c> BASIC fillers (never modified)
    ///     and the credit is allowed to finish so the already-modified tracks get uploaded.
    /// </summary>
    public static class RebirthFlow
    {
        // ---------------------------------------------------------------- public state

        public static FlowPhase Phase { get; private set; } = FlowPhase.Idle;
        public static string StatusText { get; private set; } = "空闲";
        public static string LastError { get; private set; } = "";
        public static SourceSnapshot Source { get; private set; }
        public static TransferPlan Plan { get; private set; }
        public static int CurrentCredit { get; private set; }
        public static int CurrentTrack { get; private set; }
        public static TransferItem CurrentItem { get; private set; }
        public static int CurrentMusicId { get { return CurrentItem != null ? CurrentItem.MusicId : -1; } }

        /// <summary>Optional per-account swipe codes.  Empty -> falls back to <c>ModConfig.AimeCode</c>.</summary>
        public static string SourceAccessCode = "";
        public static string TargetAccessCode = "";

        public static event Action Changed;

        public static bool IsBusy => Phase != FlowPhase.Idle && Phase != FlowPhase.SourceReady
                                     && Phase != FlowPhase.Finished && Phase != FlowPhase.Failed;

        public static bool IsAwaitingOperator => Phase == FlowPhase.NeedSource || Phase == FlowPhase.AwaitingConfirm;

        // ---------------------------------------------------------------- internal state

        private enum Step
        {
            None = 0,
            WaitLogin,
            WaitMusicSelect,
            ReadLive,
            Planning,
            WaitConfirm,
            // credit engine
            BatchBegin,
            BatchWaitMusicSelect,
            BatchWaitGap,
            BatchSelect,
            BatchStart,
            BatchPlay,
            BatchResult,
            BatchDecide,
            BatchWaitUpload,
            BatchAfterCredit,
            BatchWaitLogout,
            BatchWaitRelogin
        }

        private enum SlotKind { Transfer, Filler, Mission }

        private sealed class Slot
        {
            public bool SkipPlay;   // 中止后保留占位轨：选曲后立刻跳过（空成绩），不挂机播
            public TransferItem Item;
            public SlotKind Kind;
        }

        private static Step _step = Step.None;
        private static double _stepAt;
        private static double _phaseAt;

        private static int _monitor;
        private static int _targetRating;
        /// <summary>Target account records read right after login (used to skip already-better charts).</summary>
        private static List<ScoreRecord> _targetRecords;
        private static long _sourceUserId;
        private static string _targetName = "";
        private static double _loginWarnAt = -100;
        private static bool _loginWarned;
        private static double _loginTraceAt = -1;
        private static int _swipeAttempts;
        private static bool _swipeCapWarned;
        private static double _readingTraceAt = -1;
        /// <summary>Local time when we detected the session login (fallback anchor if SetLoginVO is missing).</summary>
        private static double _sessionLoginAt = -1;

        // source / target acquisition
        private static int _readTries;
        private static int _snapStableReads;
        private static bool _continueLoginLogged;
        private static bool _continueCreditBumped;
        private static double _continueTapAt = -100;
        private static int _continueTapTries;
        private static bool _continueAcceptedLogged;
        private static int _snapLastCount = -1;
        private static double _continueSettleUntil = -1;
        private static double _lastReadTryAt = -100;
        private static double _musicSelectAt = -1;
        /// <summary>When the account data first looked ready (for the stable-read delay).</summary>
        private static double _readyAt = -1;
        private static int _lastReadyScoreCount;
        private static bool _emptyGraceRead;
        private static bool _emptyGraceWarned;
        private static bool _modeSelectSeen;

        // plan / batching
        private static List<List<Slot>> _batches = new List<List<Slot>>();
        private static int _batchIndex;
        private static List<Slot> _credit = new List<Slot>();
        private static int _slot;
        private static int _startedInCredit;
        private static int _abortSlot = -1;
        private static readonly List<TransferItem> _longSongItems = new List<TransferItem>();
        private static bool _autoConfirmRelogin;   // 续关不可用→登出刷卡后，重新规划不再要求人工确认
        private static readonly HashSet<string> _uploadedThisRun = new HashSet<string>();   // musicId-level，跨重登防重播
        private static bool _longFlushStarted;
        private static bool _longFlushDone;
        private static double _longFlushAt;
        private static int _longFlushBaseSave;
        private static bool _staleSessionAtStart;
        private static bool _staleLogoutSent;
        private static bool _stopping;
        private static double _lastTrackStartAt = -1;
        private static double _playEnteredAt = -1;
        private static double _playStuckLogAt = -100;
        private static double _playStuckSkipAt = -100;
        /// <summary>C mode: when GamePlay actually started (AFK until the chart ends).</summary>
        private static double _gameplayStartedAt = -1;
        private static double _playIdleLogAt = -100;
        private static double _playSkipTarget = -1;
        private static bool _playSkipLogged;
        private static bool _sawDataSave;
        private static double _dataSaveSeenAt = -1;
        /// <summary>本 credit 开始时的 `LoginHooks.DataSaveCount`（批次结束时的上传对账基准）。</summary>
        private static int _creditDataSaveBase;
        private static bool _trackSettled;
        private static bool _resumeAfterRelogin;
        private static bool _reloginSeen;
        /// <summary>True when the continue path already advanced _batchIndex (TickWaitLogout must not do it again).</summary>
        private static bool _batchAdvancedInContinue;
        /// <summary>When the previous credit's upload finished (for batch hand-over timing).</summary>
        private static double _lastUploadFinishedAt = -1;
        /// <summary>Between credits: true = use the game's CONTINUE screen (same session, no swipe).</summary>
        private static bool _reloginByContinue;
        /// <summary>续关后必须把 GameManager.MusicTrackNumber 重置回 1（continue 不会自己重置，实机 22:57/23:05 崩溃根因）。</summary>
        private static bool _continueTrackResetPending;

        // shutdown
        private static bool _shutdownActive;
        private static bool _shutdownFailed;
        private static bool _quitHandled;
        private static string _shutdownReason = "";
        private static FlowPhase _shutdownFinalPhase = FlowPhase.Finished;
        private static double _shutdownAt;
        private static bool _lockSyncWarned;

        private const double StableMusicSelectSec = 0.8;
        /// <summary>How long the account data must look "ready" before we read it.</summary>
        private const double StableReadSec = 0.8;

        // ---------------------------------------------------------------- lifecycle

        public static void Initialize()
        {
            _batches = new List<List<Slot>>();
            _credit = new List<Slot>();
            _shutdownActive = false;
            _quitHandled = false;
            _stopping = false;
            _batchIndex = 0;
            _longSongItems.Clear();
            _longFlushStarted = false;
            _longFlushDone = false;
            _autoConfirmRelogin = false;
            _uploadedThisRun.Clear();
            _missionsPlayed = 0;
            _slot = 0;
            _startedInCredit = 0;
            _sawDataSave = false;
            LastError = "";
            SetPhase(FlowPhase.Idle, "空闲");
            Log.Info("flow initialised");
        }

        /// <summary>Called from Main.OnApplicationQuit: last chance to release the cabinet.</summary>
        public static void Shutdown(string reason)
        {
            try
            {
                if (_quitHandled)
                {
                    // OnApplicationQuit and an in-flight Abort both ping us; do the (expensive)
                    // logout handshake exactly once.
                    Log.Info("flow: duplicate shutdown ignored (" + reason + ")");
                    MachineLock.Off();
                    return;
                }
                _quitHandled = true;
                Log.Warn("flow shutdown requested: " + reason + " (phase=" + Phase + ", loggedIn=" + LoginHooks.IsLoggedIn() + ")");
                if (_shutdownActive)
                {
                    // An abort is already running the logout handshake; injecting a second
                    // DataSave here is what made the game walk into OnApplicationQuit on the
                    // real machine.  Just let the in-flight shutdown finish and release the lock.
                    Log.Warn("flow: quit during an in-flight wrap-up - not forcing another logout");
                }
                else if (LoginHooks.IsLoggedIn())
                {
                    // Best effort only; the process is already going away.  We still try the normal
                    // save/logout so the server does not keep isLogin=True behind.
                    LoginHooks.BeginLogout("application quit");
                    if (LoginHooks.SessionHasPlaylog) LoginHooks.ForceDataSave("application quit");
                    else LoginHooks.ForceTimeUp();
                }
            }
            catch (Exception e)
            {
                Log.Error("flow shutdown logout attempt failed", e);
            }
            finally
            {
                _shutdownActive = false;
                LoginHooks.RestoreAccessCodeFiles();
                MachineLock.Off();
                SetPhase(FlowPhase.Idle, "已停止: " + reason);
            }
        }

        // ---------------------------------------------------------------- tick

        public static void Tick()
        {
            try
            {
                LoginHooks.Tick();
                CheckProcessPile();
                CheckAreaProgress();
                if (Navigation.IsTaskActive()) TickAreaSelection();
                if (Navigation.IsTaskActive()) Navigation.SkipPhotoShoot(_monitor >= 0 ? _monitor : 0);
                if (Navigation.IsTaskActive() && TickClosingPages())
                {
                    SyncMachineLock();
                    return;
                }

                if (_shutdownActive)
                {
                    TickShutdown();
                    SyncMachineLock();
                    return;
                }

                switch (Phase)
                {
                    case FlowPhase.Idle:
                    case FlowPhase.NeedSource:
                    case FlowPhase.SourceReady:
                    case FlowPhase.Finished:
                    case FlowPhase.Failed:
                        break;
                    case FlowPhase.SourceLoginPending:
                        TickLogin(true);
                        break;
                    case FlowPhase.SourceReading:
                        TickReading(true);
                        break;
                    case FlowPhase.TargetLoginPending:
                        TickLogin(false);
                        break;
                    case FlowPhase.TargetReading:
                        TickReading(false);
                        break;
                    case FlowPhase.Planning:
                        TickPlanning();
                        break;
                    case FlowPhase.AwaitingConfirm:
                        TickConfirm();
                        break;
                    case FlowPhase.Transferring:
                    case FlowPhase.Stopping:
                        TickTransfer();
                        break;
                }

                AutoSkipPhotoEdit();
                SyncMachineLock();
            }
            catch (Exception e)
            {
                Log.Error("flow tick failed", e);
                Fail("内部异常: " + e.Message);
            }
        }

        /// <summary>
        /// Diagnostics only: every 2s count the real screen processes in the live stack and warn when
        /// it piles up (>4).  Never touches the game - just a canary for the overlay/crash class of
        /// bugs ("stack=[Common > Continue > PleaseWait > MapResult > ...]").
        /// </summary>
        private static void CheckProcessPile()
        {
            try
            {
                double now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _pileCheckAt < 2.0) return;
                _pileCheckAt = now;
                string d = Navigation.Describe();
                if (string.IsNullOrEmpty(d)) return;
                int open = d.IndexOf("stack=[", StringComparison.Ordinal);
                if (open < 0) return;
                int start = open + "stack=[".Length;
                int close = d.IndexOf(']', start);
                string inner = close > start ? d.Substring(start, close - start) : d.Substring(start);
                if (inner.Length == 0) return;
                string[] parts = inner.Split(new string[] { " > " }, StringSplitOptions.RemoveEmptyEntries);
                int screens = 0;
                for (int i = 0; i < parts.Length; i++)
                    if (IsScreenProcess(parts[i].Trim())) screens++;
                if (screens > 4)
                {
                    Log.Warn("[flow] 进程堆积 warning=screenProcesses=" + screens + " stack=[" + inner + "]");
                }
            }
            catch (Exception) { }
        }
        private static double _pileCheckAt = -100;

        // ---- post-credit closing pages (friend ranking / collection / photo / new-item list) ----
        private static double _closingWaitStartedAt = -1;
        private static bool _closingForceSaveTried;
        private static bool _closingAbandoned;
        private static double _closingLogAt = -100;
        private static double _closingSkipAt = -100;
        private static readonly Dictionary<string, System.Reflection.MethodInfo> ClosingSkipMethods =
            new Dictionary<string, System.Reflection.MethodInfo>(StringComparer.Ordinal);
        private static readonly string[] ClosingProcessNames = new string[]
        {
            "CollectionGetProcess", "CollectionProcess", "FriendSeasonRankingProcess",
            "UserNewItemListDLProcess", "PhotoEditProcess", "GetPresentProcess", "UnlockMusicProcess"
        };
        private static readonly string[] ClosingTouchAreas = new string[] { "E5", "D5", "E4", "D4", "E6" };
        private static int _closingTouchIdx;
        private static double _closingTouchLogAt = -100;
        private static readonly string[] ClosingSkipMethodNames = new string[]
        {
            "PressCollectionGetSkip", "PressFriendRankingSkip", "PressPhotoEditSkip"
        };

        private static bool HasClosingProcess()
        {
            try
            {
                if (Navigation.IsTailScreen()) return true;
            }
            catch (Exception) { }
            try
            {
                if (NavProcess.HasName(ClosingProcessNames)) return true;
            }
            catch (Exception) { }
            try
            {
                string d = Navigation.Describe();
                if (string.IsNullOrEmpty(d)) return false;
                for (int i = 0; i < ClosingProcessNames.Length; i++)
                    if (d.IndexOf(ClosingProcessNames[i], StringComparison.Ordinal) >= 0) return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// After the credit's tracks the game shows a chain of full-screen pages (friend ranking,
        /// collection, photo, new item list).  They are not a failure: press their skip methods and
        /// wait.  We freeze the phase timers meanwhile so the 60/90s failure watches cannot fire.
        /// Returns true when a closing page is on screen (caller should skip this tick).
        /// </summary>
        private static bool TickClosingPages()
        {
            try
            {
                GameStage st = GameStagePump.Stage;
                bool stageOk = st == GameStage.Other || st == GameStage.Startup
                               || st == GameStage.Unknown || st == GameStage.PhotoEdit;
                if (!stageOk || !HasClosingProcess())
                {
                    if (_closingWaitStartedAt > 0)
                    {
                        Log.Info("[flow] 收尾页面结束，继续正常流程 (stage=" + st + ")");
                        _closingWaitStartedAt = -1;
                        _closingForceSaveTried = false;
                        _closingAbandoned = false;
                    }
                    return false;
                }

                double now = UnityEngine.Time.realtimeSinceStartup;
                if (_closingWaitStartedAt < 0)
                {
                    _closingWaitStartedAt = now;
                    Log.Info("[flow] 收尾页面模式: 等待全屏展示页自动跳过… (stage=" + st + ")");
                }
                // Hard cap: never hang in the closing pages forever.  The lead's rule is that after
                // 90s without a DataSave it is a genuine failure (then the shutdown path logs out
                // and unlocks the cabinet instead of leaving the game unattended).
                // Last resort: 45s in the tail pages and the game never started its own save (seen
                // with CollectionProcess stuck in SelectCollectionType/Start).  Ask the game once to
                // DataSave; the login-hook gates still refuse unsafe stages.
                if (now - _closingWaitStartedAt > 45.0 && !_closingForceSaveTried)
                {
                    _closingForceSaveTried = true;
                    Log.Warn("flow: 收尾页面 45s 未进 DataSave (" + ClosingNamesOnScreen() + ") - 尝试强制保存一次");
                    LoginHooks.ForceDataSave("closing-timeout");
                }

                if (now - _closingWaitStartedAt > 90.0)
                {
                    if (!_closingAbandoned)
                    {
                        _closingAbandoned = true;
                        Log.Warn("flow: 收尾页面 90s 未到 DataSave (" + ClosingNamesOnScreen() +
                                 ", nav=" + Navigation.Describe() + ") -> 放弃等待");
                        Fail("收尾页面超时 90s 未到 DataSave (" + ClosingNamesOnScreen() + ")");
                    }
                    return true;
                }
                if (now - _closingSkipAt >= 0.5)
                {
                    _closingSkipAt = now;
                    TryPressClosingSkips();
                }
                if (now - _closingLogAt >= 5.0)
                {
                    _closingLogAt = now;
                    Log.Info("[flow] 收尾页面: " + ClosingNamesOnScreen() + " (已等 " +
                             (now - _closingWaitStartedAt).ToString("0") + "s) 正在按跳过…");
                }
                // Freeze phase timers: this wait must not count toward any failure timeout.
                _stepAt = now;
                StatusText = "收尾页面: 等待展示页跳过… (" + st + ")";
                return true;
            }
            catch (Exception) { return false; }
        }

        private static string ClosingNamesOnScreen()
        {
            try
            {
                string d = Navigation.Describe();
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < ClosingProcessNames.Length; i++)
                {
                    if (d != null && d.IndexOf(ClosingProcessNames[i], StringComparison.Ordinal) >= 0)
                    {
                        if (sb.Length > 0) sb.Append('+');
                        sb.Append(ClosingProcessNames[i]);
                    }
                }
                return sb.Length > 0 ? sb.ToString() : "closing";
            }
            catch (Exception) { return "closing"; }
        }

        private static void TryPressClosingSkips()
        {
            // nav's unified entry first (covers PhotoEdit / CollectionGet / FriendSeasonRanking /
            // CollectionProcess and returns true when it issued an action or is waiting for the
            // skippable state).
            bool handled = false;
            try
            {
                handled = Navigation.PressAnyTailScreen(_monitor >= 0 ? _monitor : 0);
            }
            catch (Exception) { }

            // Generic dialogs nav does not know (present / unlock-music / item list): rotate OK-like
            // touches.  Harmless if there is no dialog - the game ignores touches outside a button.
            if (!handled)
            {
                try
                {
                    string area = ClosingTouchAreas[_closingTouchIdx % ClosingTouchAreas.Length];
                    _closingTouchIdx++;
                    Navigation.Touch(_monitor >= 0 ? _monitor : 0, area, 2);
                    double now = UnityEngine.Time.realtimeSinceStartup;
                    if (now - _closingTouchLogAt >= 10.0)
                    {
                        _closingTouchLogAt = now;
                        Log.Info("[flow] 收尾页面: 通用跳过触摸 area=" + area + " (" + ClosingNamesOnScreen() + ")");
                    }
                }
                catch (Exception) { }
            }

            // Fallbacks for builds/nav versions without the unified entry.
            for (int i = 0; i < ClosingSkipMethodNames.Length; i++)
            {
                string name = ClosingSkipMethodNames[i];
                try
                {
                    System.Reflection.MethodInfo mi;
                    if (!ClosingSkipMethods.TryGetValue(name, out mi))
                    {
                        mi = typeof(Navigation).GetMethod(name,
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        ClosingSkipMethods[name] = mi;
                        if (mi != null) Log.Info("flow: Navigation." + name + " found");
                    }
                    if (mi == null) continue;
                    var ps = mi.GetParameters();
                    object[] args = null;
                    if (ps != null && ps.Length > 0)
                    {
                        args = new object[ps.Length];
                        for (int k = 0; k < ps.Length; k++)
                            args[k] = ps[k].ParameterType == typeof(int)
                                ? (object)(_monitor >= 0 ? _monitor : 0) : null;
                    }
                    mi.Invoke(null, args);
                }
                catch (Exception e)
                {
                    if (!_closingSkipWarned)
                    {
                        _closingSkipWarned = true;
                        Log.Warn("flow: closing-page skip " + name + " failed: " + e.Message);
                    }
                }
            }
        }
        private static bool _closingSkipWarned;

        // ---- area selection ----
        private static double _areaSelectAt = -100;
        /// <summary>每 credit 第一轨等待课题曲扫描的起点。</summary>
        private static double _missionScanWaitAt = -1;
        /// <summary>本 credit 是否已经从区域表直接排过课题曲（不依赖选歌页懒加载）。</summary>
        private static bool _areaMissionQueued;
        /// <summary>本次 credit 是否已经重排放入过课题曲（每 credit 只放 1 首）。</summary>
        private static bool _missionQueuedThisCredit;
        private static readonly HashSet<int> _missionPlacedIds = new HashSet<int>();
        private static readonly HashSet<int> _missionDupLogged = new HashSet<int>();
        private static string _lastMissionSig = "";
        /// <summary>本次运行已经排入并打完的课题曲数量（用 MaxMissionCredits 上限，防止队列无限增长）。</summary>
        private static int _missionsPlayed;
        private static bool _areaSelectWarned;

        private static bool _areaConfirmed;
        private static double _areaWaitLogAt = -1000;
        /// <summary>
        /// On the area-select screen pick the next configured area (nav's MapHooks handles the
        /// cursor/confirm).  ConfigData.CrawlAreaIds empty = just take the first pending area;
        /// a manual cursor change by the operator wins for this screen.
        /// </summary>

        private static void TickAreaSelection()
        {
            if (!Navigation.IsTaskActive()) return;   // 用户自己玩时绝不接管选区
            try
            {
                GameStage s = GameStagePump.Stage;
                if (s != GameStage.MapSelect && !Navigation.IsAreaSelectScreen())
                {
                    _areaSelectAt = -100;
                    return;
                }
                double now = UnityEngine.Time.realtimeSinceStartup;

                // ManualAreaSelect：完全交给操作员（accel-crawl / 手动选区都走这条）
                if (ModConfig.Data.ManualAreaSelect)
                {
                    if (now - _areaWaitLogAt > 3.0)
                    {
                        _areaWaitLogAt = now;
                        var waitList = MapHooks.AvailableAreas;
                        Log.Warn("[flow] 选区页（ManualAreaSelect）: 共 " + (waitList != null ? waitList.Count : 0) +
                                 " 个区 —— 请手动操作，完成后可发 areaok 解除等待");
                    }
                    return;
                }

                // 操作员刚手动换过区：这一屏不抢
                if (MapHooks.AreaManuallyChanged) return;

                // 还没确认前，把"本 credit 想去哪个区"告诉 nav（配置区优先，其次第一个未完成区）。
                // nav 的 DriveRegionalSelect 会用真实按键把光标移过去再按決定。
                if (MapHooks.SelectedAreaId <= 0)
                {
                    int cursor = PickCrawlCursor();
                    if (cursor >= 0) Navigation.RequestSelectArea(cursor);
                }

                // 每帧都可调用（内部 0.25s 限速）：提示页/发现动画 → 4 号键推进；主页面 → 移动 + 決定
                int action = Navigation.DriveRegionalSelect(_monitor >= 0 ? _monitor : 0);
                if (action != 0) return;

                if (now - _areaSelectAt < 2.0) return;
                _areaSelectAt = now;
                Log.Info("[flow] 选区页: " + Navigation.Describe() +
                         " 进度=" + _crawlDone.Count + "/" +
                         (ModConfig.Data.CrawlAreaIds != null && ModConfig.Data.CrawlAreaIds.Count > 0
                             ? ModConfig.Data.CrawlAreaIds.Count.ToString() : "?") +
                         " 轮次=" + _crawlRound);
            }
            catch (Exception e)
            {
                if (!_areaSelectWarned)
                {
                    _areaSelectWarned = true;
                    Log.Warn("flow: area select failed: " + e.Message);
                }
            }
        }

        /// <summary>
        /// 本 credit 想去的光标：`CrawlAreaIds` 里还没打过的区优先；否则第一个"未锁/未完成/非无限"区。
        /// 返回 -1 = 没有可选项（让 nav 保持当前光标）。
        /// </summary>
        private static int PickCrawlCursor()
        {
            try
            {
                var areas = MapHooks.AvailableAreas;
                if (areas == null || areas.Count == 0) return -1;

                var wanted = ModConfig.Data.CrawlAreaIds;
                if (wanted != null && wanted.Count > 0)
                {
                    for (int w = 0; w < wanted.Count; w++)
                    {
                        int id = wanted[w];
                        if (_crawlDone.Contains(id) && !ModConfig.Data.CrawlAllAreasFallback) continue;
                        for (int i = 0; i < areas.Count; i++)
                        {
                            var a = areas[i];
                            if (a == null || a.Id != id || a.IsLock || a.IsInfinity) continue;
                            return a.Index;
                        }
                    }
                }

                var pending = MapHooks.PendingAreaIds;
                if (pending != null && pending.Count > 0)
                {
                    for (int i = 0; i < areas.Count; i++)
                        if (areas[i] != null && areas[i].Id == pending[0]) return areas[i].Index;
                }

                for (int i = 0; i < areas.Count; i++)
                {
                    var a = areas[i];
                    if (a == null || a.IsLock || a.IsInfinity || a.IsComplete) continue;
                    return a.Index;
                }
                return -1;
            }
            catch (Exception) { return -1; }
        }

        // ---- area crawl loop ----
        private static readonly HashSet<int> _crawlDone = new HashSet<int>();
        private static int _crawlRound = 1;
        private static bool _crawlFallbackDefault;
        private static int _crawlLastAreaId = -1;
        private static double _crawlCheckAt = -100;

        /// <summary>
        /// Track the area the game picked (nav auto-selects after AreaSelectTimeoutSec) and run the
        /// crawl loop: when every planned area is complete but the transfer is not, start over.
        /// Read-only diagnostics + bookkeeping - area selection itself is nav's MapHooks.
        /// </summary>
        private static void CheckAreaProgress()
        {
            try
            {
                double now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _crawlCheckAt < 3.0) return;
                _crawlCheckAt = now;

                int planned = ModConfig.Data.CrawlAreaIds != null ? ModConfig.Data.CrawlAreaIds.Count : 0;
                int areaId = MapHooks.SelectedAreaId;
                string areaName = MapHooks.SelectedAreaName ?? "";
                if (areaId > 0 && areaId != _crawlLastAreaId)
                {
                    _crawlLastAreaId = areaId;
                    _crawlDone.Add(areaId);
                    Log.Info("[flow] 选区: areaId=" + areaId + " name=" + areaName + " 进度=" +
                             _crawlDone.Count + (planned > 0 ? "/" + planned : "/?") +
                             " 剩余倒计时=" + ModConfig.Data.AreaSelectTimeoutSec.ToString("0.0") + "s" +
                             " 轮次=" + _crawlRound);
                }

                if (ModConfig.Data.CrawlAllAreasFallback && planned > 0 && Plan != null
                    && !_stopping && Plan.Done < Plan.Total)
                {
                    bool all = true;
                    foreach (var id in ModConfig.Data.CrawlAreaIds)
                        if (!_crawlDone.Contains(id)) { all = false; break; }
                    if (all)
                    {
                        _crawlFallbackDefault = true;
                        _crawlLastAreaId = -1;
                        Log.Info("[flow] 区域循环: 已爬完 " + planned + " 个区 → 之后固定使用默认区域（不重爬）");
                        Log.Info("[flow] 区域循环: 已爬完 " + planned + " 个区，从头再来（第 " + _crawlRound + " 轮）");
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Mission charts (課題曲) discovered for the current area are appended as extra credits so
        /// they get played even though the plan was built earlier.
        /// </summary>
        /// <summary>
        /// 把完整批次队列打到日志（`queue` 命令）。每一批一行，列出该批所有槽位，
        /// 课题曲用 ★ 标记 —— 这样插入位置是否生效一眼可见。
        /// </summary>
        public static void DumpQueue()
        {
            try
            {
                Log.Info("[queue] ==== 完整队列: " + _batches.Count + " 批, 当前 _batchIndex=" + _batchIndex +
                         " (credit " + (_batchIndex + 1) + ") ====");
                int missionCount = 0;
                for (int b = 0; b < _batches.Count; b++)
                {
                    var slots = _batches[b];
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[queue] ").Append(b == _batchIndex ? "▶" : " ")
                      .Append(" 批").Append(b + 1).Append("/").Append(_batches.Count)
                      .Append(" [").Append(slots == null ? 0 : slots.Count).Append(" 槽] ");
                    if (slots != null)
                    {
                        for (int s = 0; s < slots.Count; s++)
                        {
                            var sl = slots[s];
                            if (sl == null) { sb.Append(s + 1).Append(":<空> "); continue; }
                            bool isMission = sl.Kind == SlotKind.Mission;
                            if (isMission) missionCount++;
                            int qId = sl.Item != null ? sl.Item.MusicId : 0;
                            int qLv = sl.Item != null ? sl.Item.Level : 0;
                            sb.Append(s + 1).Append(":")
                              .Append(isMission ? "★" : "")
                              .Append(qId).Append("-").Append(qLv);
                            if (isMission) sb.Append("(课题)");
                            sb.Append(' ');
                        }
                    }
                    Log.Info(sb.ToString());
                }
                Log.Info("[queue] 队列里共 " + missionCount + " 个课题曲槽位");
            }
            catch (Exception e)
            {
                Log.Error("[queue] DumpQueue 失败", e);
            }
        }

        // ---------------------------------------------------------------- queue view (UI)

        /// <summary>One planned track inside a credit, as shown in the UI playlist.</summary>
        public sealed class QueueSlotView
        {
            public int Index;          // 0-based position inside the batch
            public int MusicId;
            public int Level;
            public int Kind;           // 0 = transfer, 1 = filler, 2 = mission (課題曲)
            /// <summary>Song title from the live DataManager (empty when unavailable).</summary>
            public string Name = "";
            public bool IsMission { get { return Kind == 2; } }
            public bool IsDone;
        }

        /// <summary>One credit (batch) of the plan.</summary>
        public sealed class QueueBatchView
        {
            public int Index;          // 0-based
            public QueueSlotView[] Slots;
            public int Count { get { return Slots != null ? Slots.Length : 0; } }
            public bool IsMission;     // the batch contains at least one mission chart
            public bool IsCurrent;
            public bool IsDone;
            public int CurrentSlot = -1;
            public int DoneCount;
        }

        /// <summary>Immutable snapshot of the whole queue.  Built on the game thread only.</summary>
        public sealed class QueueSnapshot
        {
            public QueueBatchView[] Batches = new QueueBatchView[0];
            public int CurrentIndex = -1;
            public int CurrentSlot = -1;
            public int TotalSlots;
            public int DoneSlots;
            public int MissionSlots;
            public int MissionBatches;
            public bool Running;
        }

        /// <summary>
        /// Read-only view of `_batches` for the window thread.  Called from `UIHost.Tick` (Unity main
        /// thread) while the window thread only ever sees the published immutable copy, so no locking
        /// is needed here.
        /// </summary>
        public static QueueSnapshot GetQueueSnapshot()
        {
            var view = new QueueSnapshot();
            try
            {
                bool running = Phase == FlowPhase.Transferring || Phase == FlowPhase.Stopping;
                bool finished = Phase == FlowPhase.Finished;
                view.Running = running;
                view.CurrentIndex = running ? _batchIndex : -1;
                view.CurrentSlot = running ? _slot : -1;

                int count = _batches != null ? _batches.Count : 0;
                var batches = new QueueBatchView[count];
                for (int b = 0; b < count; b++)
                {
                    var slots = _batches[b];
                    int n = slots != null ? slots.Count : 0;
                    var bv = new QueueBatchView { Index = b, Slots = new QueueSlotView[n] };
                    bool isCurrent = running && b == _batchIndex;
                    bool isDone = finished || (running && b < _batchIndex);
                    bv.IsCurrent = isCurrent;
                    bv.IsDone = isDone;
                    bv.CurrentSlot = isCurrent ? _slot : -1;
                    int done = 0;
                    for (int s = 0; s < n; s++)
                    {
                        var sl = slots[s];
                        var sv = new QueueSlotView { Index = s };
                        if (sl != null && sl.Item != null)
                        {
                            sv.MusicId = sl.Item.MusicId;
                            sv.Level = sl.Item.Level;
                        }
                        sv.Kind = sl == null ? 0 : (int)sl.Kind;
                        sv.Name = MusicNameOf(sv.MusicId);
                        if (sv.IsMission) { view.MissionSlots++; bv.IsMission = true; }
                        bool slotDone = isDone || (isCurrent && s < _slot);
                        sv.IsDone = slotDone;
                        if (slotDone) done++;
                        bv.Slots[s] = sv;
                    }
                    bv.DoneCount = done;
                    view.DoneSlots += done;
                    view.TotalSlots += n;
                    if (bv.IsMission) view.MissionBatches++;
                    batches[b] = bv;
                }
                view.Batches = batches;
            }
            catch (Exception e)
            {
                Log.Warn("flow: GetQueueSnapshot failed: " + e.Message);
            }
            return view;
        }

        private static readonly Dictionary<int, string> _musicNameCache = new Dictionary<int, string>();
        private static bool _musicNameWarned;

        /// <summary>
        /// 曲名（UI 待办列表用）。走游戏实时 `DataManager.GetMusic(id).name.str`，按 musicId 缓存；
        /// DataManager 还没加载/取不到时返回空串（UI 会退回显示 musicId-level）。
        /// 只在游戏主线程调用（UIHost.BuildSnapshot）。
        /// </summary>
        private static string MusicNameOf(int musicId)
        {
            if (musicId <= 0) return "";
            string cached;
            if (_musicNameCache.TryGetValue(musicId, out cached)) return cached;
            string name = "";
            try
            {
                var dm = MAI2.Util.Singleton<Manager.DataManager>.Instance;
                if (dm != null)
                {
                    var music = dm.GetMusic(musicId);
                    if (music != null && music.name != null) name = music.name.str ?? "";
                }
            }
            catch (Exception e)
            {
                if (!_musicNameWarned)
                {
                    _musicNameWarned = true;
                    Log.Warn("flow: 读取曲名失败（UI 退回 musicId-level）: " + e.Message);
                }
            }
            if (name == null) name = "";
            _musicNameCache[musicId] = name;
            return name;
        }

        /// <summary>
        /// 课题曲必须**在本 credit 内**打掉（占用一个轨道位置），不能追加成额外批次。
        /// 触发点：选歌页稳定后、本 credit 还没开始游玩（_slot==0 且 _startedInCredit==0）。
        /// 把本 credit 最后一个 transfer 槽换成课题曲槽；被挤掉的成绩项插到下一批开头，
        /// 下一批满了就新建一批（绝不丢成绩）。同一屏多余的课题曲留给后续 credit。
        ///
        /// 注意：**不能**生成只有 1 首轨道的 credit —— 实机 22:57 证明那会让 GameProcess.OnStart
        /// 读 `SelectMusicID[MusicTrackNumber-1]`（=0）得到 null 音乐而 NRE。
        /// </summary>
        /// <summary>
        /// 课题曲必须在**本 credit 内**打掉（占用一个轨道位置），但不能像以前那样"挤成绩 → 新建 filler 批"，
        /// 否则每多一首课题曲就多一批、每批还带 2 首性隐(filler)，越叠越多（历史 bug）。
        ///
        /// 现在的做法：**重排剩余队列**。
        ///   1. 把当前批（还没开始打）和后面所有批次里的未打成绩项按原顺序收集起来；
        ///   2. 从 `MapHooks` 取本次允许的新课题曲（受 `MaxMissionCredits` 限制）；
        ///   3. 重新分批：每批优先 [成绩, 成绩, 课题]，课题用完就 [成绩×3]；
        ///      正好 3 槽，不新增 filler，批次数 = ceil((成绩 + 课题) / 3)。
        /// 这样「课题曲每次结束后可能变多」时，列表只是重排，不会膨胀。
        /// </summary>
        /// <summary>
        /// 课题曲在**本 credit 内**占一个轨道位置。做法是"重排剩余队列"而不是"挤掉成绩再新建 filler 批"：
        /// 收集当前批+后续批里**未打**的成绩项，与 1 首新课题曲重新分批（2 成绩 + 1 课题，正好 3 槽）。
        /// 每个 credit 最多重排一次、最多放 1 首；不新增 filler，批次数保持 ceil(剩余/3)，不会膨胀。
        ///
        /// 另：选歌页扫描会把同一首课题曲反复重新塞进 PendingMissionCharts，所以这里用
        /// `_missionPlacedIds` 过滤已排过的曲目，避免"扫描→重排→再扫描"死循环（实机 17:48）。
        /// </summary>
        private static void ApplyMissionToCurrentCredit()
        {
            if (_credit != null && _credit.Count > 0 && _credit[0].Item != null && IsLongMusic(_credit[0].Item.MusicId)) return;   // long-song credit stays 2 tracks
            if (_missionQueuedThisCredit) return;
            if (!ModConfig.Data.AutoAddMissionSongs) return;
            if (_batches == null || _batchIndex < 0 || _batchIndex >= _batches.Count) return;
            if (_slot > 0 || _startedInCredit > 0) return;
            if (MapHooks.PendingMissionCharts == null || MapHooks.PendingMissionCharts.Count == 0) return;

            int budget = Math.Max(0, ModConfig.Data.MaxMissionCredits - _missionsPlayed);
            if (budget <= 0) return;

            try
            {
                var taken = MapHooks.TakeMissionCharts();
                if (taken == null || taken.Length == 0) return;

                MapHooks.MissionChart pick = null;
                for (int i = 0; i < taken.Length; i++)
                {
                    var m = taken[i];
                    if (m == null) continue;
                    if (_missionPlacedIds.Contains(m.MusicId)) continue;   // 扫描重复塞进来的
                    pick = m;
                    break;
                }
                if (pick == null)
                {
                    if (_missionDupLogged.Add(taken[0].MusicId))
                        Log.Info("[flow][mission] 忽略重复课题曲 " + taken[0].MusicId + "（已排过，防止扫描重排死循环）");
                    return;
                }

                // 1) 收集剩余未打成绩项（当前批 + 后续批）
                var transfers = new List<Slot>();
                for (int b = _batchIndex; b < _batches.Count; b++)
                {
                    var batch = _batches[b];
                    if (batch == null) continue;
                    for (int i = 0; i < batch.Count; i++)
                    {
                        var s = batch[i];
                        if (s != null && s.Kind == SlotKind.Transfer && s.Item != null) transfers.Add(s);
                    }
                }

                // 2) 重新分批：每批 [成绩, 成绩, 课题]（只剩课题时依次后排）
                var rebuilt = new List<List<Slot>>();
                int ti = 0;
                bool placed = false;
                while (ti < transfers.Count || !placed)
                {
                    var batch = new List<Slot>(3);
                    while (batch.Count < 2 && ti < transfers.Count) batch.Add(transfers[ti++]);
                    if (!placed) { batch.Add(BuildMissionSlot(pick)); placed = true; }
                    while (batch.Count < 3 && ti < transfers.Count) batch.Add(transfers[ti++]);
                    rebuilt.Add(batch);
                }

                if (rebuilt.Count == 0) return;
                // 服务端拒收 2-playlog 报文：重排后的最后一批若不足 3 槽，用 filler 补齐（只补这一次，不会累积）。
                if (CanUseFiller && ModConfig.Data.PadToFullCredit)
                {
                    var lastBatch = rebuilt[rebuilt.Count - 1];
                    if (lastBatch.Count < 3) PadWithFiller(lastBatch);
                }
                _batches.RemoveRange(_batchIndex, _batches.Count - _batchIndex);
                _batches.AddRange(rebuilt);
                _credit = _batches[_batchIndex];
                _missionQueuedThisCredit = true;
                _missionsPlayed++;
                _missionPlacedIds.Add(pick.MusicId);
                if (Plan != null) Plan.Credits = _batches.Count;
                Log.Info("[flow][mission] 课题曲占本批第 " + _credit.Count + " 轨: " + pick.MusicId + "-" +
                         pick.Level + " → 剩余重排 " + _batches.Count + " 批（无 filler 堆积）");
            }
            catch (Exception e)
            {
                Log.Warn("flow: 课题曲重排失败: " + e.Message);
            }
        }
        /// <summary>把 MapHooks 的课题曲描述转成一个 Mission 槽（80% 通关；挑战曲 100% 零 MISS）。</summary>
        private static Slot BuildMissionSlot(MapHooks.MissionChart mission)
        {
            var record = new ScoreRecord
            {
                MusicId = mission.MusicId,
                Level = mission.Level,
                PlayCount = 1,
                Achievement = ScoreHooks.MinClearAchievement,
                NoMiss = mission.StartLife > 0,
                AllowImperfect = mission.StartLife > 0 ? Math.Max(0, mission.StartLife - 1) : -1,
            };
            return new Slot
            {
                Kind = SlotKind.Mission,
                Item = new TransferItem { Desired = record, Index = -1, SkipReason = "mission" }
            };
        }

        private static void AppendMissionBatches()
        {
            if (_missionCreditsAdded >= ModConfig.Data.MaxMissionCredits) return;
            if (!MapHooks.MissionTaskActive && MapHooks.PendingMissionCharts.Count == 0) return;
            if (!ModConfig.Data.AutoAddMissionSongs) return;
            if (Plan != null && Plan.Done >= Plan.Total) return;
            try
            {
                var taken = MapHooks.TakeMissionCharts();
                if (taken == null || taken.Length == 0) return;
                var mission = new List<Slot>();
                for (int k = 0; k < taken.Length && mission.Count < 3; k++)
                {
                    var record = new ScoreRecord
                    {
                        MusicId = taken[k].MusicId,
                        Level = taken[k].Level,
                        PlayCount = 1,
                        // 挑战曲（startLife>0）限命：必须零 MISS → 用 100%；普通课题曲 80% 即可。
                        Achievement = ScoreHooks.MinClearAchievement,
                        // 扣命规则（maimai Wiki）: 每个非 Perfect 判定扣 1 命 → 允许 startLife-1 个
                        NoMiss = taken[k].StartLife > 0,
                        AllowImperfect = taken[k].StartLife > 0 ? Math.Max(0, taken[k].StartLife - 1) : -1,
                    };
                    mission.Add(new Slot
                    {
                        Kind = SlotKind.Mission,
                        Item = new TransferItem { Desired = record, Index = -1, SkipReason = "mission" }
                    });
                }
                if (mission.Count == 2 && CanUseFiller) PadWithFiller(mission);
                if (mission.Count == 2)
                {
                    _batches.Insert(Math.Min(_batches.Count, _batchIndex), new List<Slot> { mission[0] });
                    _batches.Insert(Math.Min(_batches.Count, _batchIndex), new List<Slot> { mission[1] });
                }
                else if (mission.Count > 0)
                {
                    // otherwise it waits behind all remaining transfer credits.
                    _batches.Insert(Math.Min(_batches.Count, _batchIndex), mission);
                }
                _missionCreditsAdded++;
                Log.Info("[flow] 课题曲批次 +1（已追加 " + _missionCreditsAdded + "/" + ModConfig.Data.MaxMissionCredits + "）");
                if (Plan != null) Plan.Credits = _batches.Count;
                Log.Info("[flow] 区域循环: 新增课题曲 " + mission.Count + " 首 → 共 " + _batches.Count + " 批");
            }
            catch (Exception e) { Log.Warn("flow: mission batch append failed: " + e.Message); }
        }

        private static bool IsScreenProcess(string name)
        {
            switch (name)
            {
                case "":
                case "CommonProcess":
                case "GenericProcess":
                case "FadeProcess":
                case "InputtableFadeProcess":
                case "SwitchProcess":
                case "BroadcastProcess":
                    return false;
                default:
                    return true;
            }
        }

        private static void TickShutdown()
        {
            try
            {
                if (!LoginHooks.IsLoggedIn(out int mon))
                {
                    CompleteShutdown();
                    return;
                }

                // 长版歌曲：不游玩，直接写 UserData + 报文，并等这次 DataSave 上传完成
                if (_longSongItems.Count > 0 && !_longFlushDone)
                {
                    double nowLong = UnityEngine.Time.realtimeSinceStartup;
                    if (!_longFlushStarted)
                    {
                        _longFlushStarted = true;
                        _longFlushAt = nowLong;
                        _longFlushBaseSave = LoginHooks.DataSaveCount;
                        int touched = 0;
                        try { FakerSwap.Network.PayloadRewriter.AppendMissingRows = true; } catch (Exception) { }
                        foreach (var item in _longSongItems)
                        {
                            try
                            {
                                if (UserDataPatcher.Commit(0, item.Desired)) touched++;
                                else Log.Warn("[flow] 长版直写失败: " + item.MusicId + "-" + item.Level);
                            }
                            catch (Exception e) { Log.Warn("[flow] 长版直写异常: " + item.MusicId + " " + e.Message); }
                        }
                        Log.Warn("[flow] 长版歌曲直写 " + touched + "/" + _longSongItems.Count + " 首，强制 DataSave 上传…");
                        LoginHooks.ForceDataSave("长版歌曲直写");
                    }
                    if (LoginHooks.DataSaveCount > _longFlushBaseSave || nowLong - _longFlushAt > 25.0)
                    {
                        _longFlushDone = true;
                        Log.Info("[flow] 长版歌曲上传完成 (DataSave #" + LoginHooks.DataSaveCount + ")");
                    }
                    else
                    {
                        StatusText = "上传长版歌曲…";
                        return;
                    }
                }

                GameStage stage = GameStagePump.Stage;
                if (IsInGameplay())
                {
                    // Never leave a chart running: skip it, the resulting DataSave will log us out.
                    Navigation.SkipCurrentChart();
                    StatusText = "结束当前曲…";
                }

                string detail;
                if (LoginHooks.PollLogout(out detail))
                {
                    if (LoginHooks.LogoutGaveUp)
                    {
                        _shutdownFailed = true;
                        LastError = "登出超时（未杀进程，请人工检查服务端 isLogin）";
                    }
                    CompleteShutdown();
                    return;
                }

                StatusText = "正在登出… " + detail;
                double elapsed = UnityEngine.Time.realtimeSinceStartup - _shutdownAt;
                if (elapsed > 4.0 * Math.Max(10.0, ModConfig.Data.LogoutTimeoutSec))
                {
                    _shutdownFailed = true;
                    LastError = "登出彻底超时";
                    CompleteShutdown();
                }
            }
            catch (Exception e)
            {
                Log.Error("flow shutdown tick failed", e);
                _shutdownFailed = true;
                LastError = e.Message;
                CompleteShutdown();
            }
        }

        /// <summary>Best-effort skip of the photo screen so a locked cabinet cannot get stuck there.</summary>
        /// <summary>
        /// PhotoEdit: the game finishes this by itself and walks on to DataSave.  We only log/wait -
        /// injecting a DataSaveProcess here is one of the ways the process stack used to pile up.
        /// </summary>
        private static void AutoSkipPhotoEdit()
        {
            if (!MachineLock.Active) return;
            if (GameStagePump.Stage != GameStage.PhotoEdit) return;
            if (GameStagePump.SecondsInStage < 1.5) return;
            if (!_photoEditWaitLogged)
            {
                _photoEditWaitLogged = true;
                Log.Info("flow: PhotoEdit 阶段 - 等待游戏自然进入 DataSave（不注入进程）");
            }
        }
        private static bool _photoEditWaitLogged;

        // ---------------------------------------------------------------- operator commands

        /// <summary>Use an existing backup / Astral file as the source data.</summary>
        public static bool UseSourceFile(string path, out string error)
        {
            SourceSnapshot snap;
            if (!SourceData.TryLoad(path, out snap, out error)) return false;
            Source = snap;
            _sourceUserId = snap.UserId;
            Plan = null;
            ModConfig.Data.LastSourceFile = path;
            ModConfig.Save();
            SetPhase(FlowPhase.SourceReady, "已载入源数据: " + snap.DisplayName);
            Log.Info("flow: source file loaded " + path + " -> " + snap.DisplayName);
            return true;
        }

        /// <summary>Drop the loaded source and go back to the file picker.</summary>
        public static void ClearSource()
        {
            if (IsBusy) return;
            Plan = null;
            Source = null;
            _sourceUserId = 0;
            _targetRecords = null;
            _targetRating = 0;
            SetPhase(FlowPhase.NeedSource, "请选择源数据文件或登录源账号");
        }

        /// <summary>Ask the operator to log in with the SOURCE account so we can fetch + back it up.</summary>
        public static void BeginSourceAcquire()
        {
            if (IsBusy) return;
            BeginSourceAcquire(null);
        }

        public static void BeginSourceAcquire(string accessCode)
        {
            if (IsBusy)
            {
                Log.Warn("flow: BeginSourceAcquire ignored, phase=" + Phase);
                return;
            }
            if (!string.IsNullOrEmpty(accessCode)) SourceAccessCode = accessCode;
            Plan = null;
            _targetRecords = null;
            _loginWarned = false;
            ResetSwipeBudget();
            SetPhase(FlowPhase.SourceLoginPending, "请刷入源账号（登录后自动读取并备份）");
            SetStep(Step.WaitLogin, "");
        }

        /// <summary>Second phase: log the target account in, read it, build the plan, wait for confirm.</summary>
        public static bool StartTransfer(out string error)
        {
            error = null;
            if (ModConfig.Data.SafeNoWrite)
            {
                error = "只读模式 (SafeNoWrite=true)：只允许获取源数据，写账号功能已锁定。确认流程无误后在 config.json 里把 SafeNoWrite 改成 false 并重启。";
                Log.Warn("flow: StartTransfer rejected - " + error);
                return false;
            }
            if (Phase != FlowPhase.SourceReady)
            {
                error = "当前阶段是 " + Phase + "，需要先准备源数据 (SourceReady)";
                return false;
            }
            if (Source == null || Source.Records == null || Source.Records.Count == 0)
            {
                error = "源数据为空";
                return false;
            }
            Plan = null;
            _loginWarned = false;
            _targetRecords = null;
            _targetRating = 0;
            _batchIndex = 0;
            _batchAdvancedInContinue = false;
            _failLogged = false;
            _failReason = "";
            _stopping = false;
            _resumeAfterRelogin = true;
            _staleSessionAtStart = LoginHooks.IsLoggedIn();
            _staleLogoutSent = false;
            ResetSwipeBudget();
            SetPhase(FlowPhase.TargetLoginPending, "请刷入目标账号");
            SetStep(Step.WaitLogin, "");
            Log.Info("flow: StartTransfer with source " + Source.DisplayName);
            return true;
        }

        /// <summary>Second confirmation: actually start pushing records.</summary>
        public static void ConfirmStart()
        {
            if (Phase != FlowPhase.AwaitingConfirm) return;
            Log.Info("flow: operator confirmed start");
            if (!LoginHooks.IsLoggedIn(out _monitor))
            {
                // The session died while the operator was reading the estimate: log in again and go
                // straight to the credit loop (the plan does not depend on the target account).
                SetPhase(FlowPhase.TargetLoginPending, "会话已超时，重新登录目标账号…");
                SetStep(Step.WaitLogin, "");
                _resumeAfterRelogin = true;
                ResetSwipeBudget();
                return;
            }
            StartCreditLoop();
        }

        public static void CancelStart()
        {
            if (Phase != FlowPhase.AwaitingConfirm) return;
            SetPhase(FlowPhase.SourceReady, "已取消（源数据仍保留）");
        }

        /// <summary>Abort the running task.  Semantics: no continue, finish the current credit with
        /// fillers (or log out right away when nothing was played yet), then log out.</summary>
        /// <summary>Release the ManualAreaSelect gate for the current area-select screen.</summary>
        public static void ConfirmAreaSelect() { _areaConfirmed = true; Log.Warn("flow: 选区已人工确认 → 进入区域"); }

        public static void Abort()
        {
            if (!IsBusy)
            {
                if (LoginHooks.IsLoggedIn())
                {
                    // Idle but a session is still open (e.g. a previous crash left the cabinet logged
                    // in).  Let the operator clean it up without killing the game: the wrap-up will
                    // send the direct UserLogout packet and upload nothing.
                    Log.Warn("flow: abort while idle - cleaning up a leftover logged-in session");
                    SetPhase(FlowPhase.Stopping, "清理残留登录会话…");
                    SetStep(Step.None, "");
                    StartShutdown(false, "人工清理残留会话", FlowPhase.Finished);
                }
                else
                {
                    Log.Warn("flow: Abort ignored, not busy (phase=" + Phase + ")");
                }
                return;
            }
            Log.Info("flow: abort requested in phase " + Phase + " (credit=" + CurrentCredit + " track=" + CurrentTrack + ")");
            _stopping = true;
            if (_abortSlot >= 0)
            {
                // 重复中止（界面连点 / 命令重放）：中止槽位已经处理过，再走一遍会覆盖成占位曲，
                // 把「已改写成绩的轨」也洗掉，并让 BatchPlay 推进错位。
                StatusText = "正在中止…（已处理）";
                return;
            }

            if (Phase == FlowPhase.Transferring || Phase == FlowPhase.Stopping)
            {
                if (_startedInCredit == 0 || _slot < 0)
                {
                    // Nothing was played in this credit yet: just end the session.
                    SetPhase(FlowPhase.Stopping, "已中止（未开始游玩），正在登出…");
                    StartShutdown(false, "用户中止", FlowPhase.Finished);
                    return;
                }
                _abortSlot = _slot;
                // 中止后剩余轨**保留占位**（游戏上传必须是 3 轨的成绩，1 轨会被服务端拒绝），
                // 但不再挂机播放：标 SkipPlay，选曲后立刻跳过（空成绩 0.0000%）。
                try
                {
                    int marked = 0;
                    for (int i = _abortSlot + 1; i < _credit.Count; i++)
                    {
                        if (_credit[i] == null) continue;
                        _credit[i].SkipPlay = true;
                        marked++;
                    }
                    Log.Warn("flow: 中止 → 当前轨(第 " + (_abortSlot + 1) + " 轨)跳过不改，剩余 " + marked +
                             " 轨保留占位并立刻跳过（空成绩，不挂机播放）");
                }
                catch (Exception ex) { Log.Warn("flow: 中止标记剩余轨失败: " + ex.Message); }
                SetPhase(FlowPhase.Stopping, "正在中止…");
                // immediately kill the chart that is currently playing
                GameStage stage = GameStagePump.Stage;
                if (IsInGameplay())
                {
                    ScoreHooks.BeginSkipUnmodified();
                    Navigation.SkipCurrentChart();
                }
                else if (stage == GameStage.Result || stage == GameStage.Continue)
                {
                    // this track's result is already in: do not modify it
                    if (_slot >= 0 && _slot < _credit.Count)
                    {
                        try { ScoreHooks.BeginSkipUnmodified(); } catch (Exception) { }
                    }
                }
                return;
            }

            // Not transferring yet: normal wrap-up (logout, then Finished).
            SetPhase(FlowPhase.Stopping, "已中止，正在登出…");
            StartShutdown(false, "用户中止", FlowPhase.Finished);
        }

        public static void SetSourceAccessCode(string code) { SourceAccessCode = LoginHooks.NormalizeCode(code) ?? ""; }
        public static void SetTargetAccessCode(string code) { TargetAccessCode = LoginHooks.NormalizeCode(code) ?? ""; }

        // ---------------------------------------------------------------- login steps

        private static void TickLogin(bool source)
        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            double age = now - _stepAt;

            int monitor;
            ulong userId;
            string userName;
            // A session may already be open when the task starts (operator played, or a previous run
            // left one).  Treating it as the target silently transferred into the source account, so
            // close it first and wait for a fresh swipe.
            if (!source && _staleSessionAtStart)
            {
                int staleMon; ulong staleUid; string staleName;
                bool logged = LoginHooks.IsLoggedIn(out staleMon, out staleUid, out staleName);
                // Only a session belonging to the SOURCE account is stale.  A session that is
                // already the target must simply be used (logging it out interrupted the operator).
                if (logged && _sourceUserId != 0 && (long)staleUid != _sourceUserId)
                {
                    _staleSessionAtStart = false;
                    _staleLogoutSent = false;
                    Log.Info("flow: 已登录账号非源账号(" + staleName + " uid=" + staleUid + ") → 直接作为目标账号使用");
                }
                else if (logged)
                {
                    if (!_staleLogoutSent)
                    {
                        _staleLogoutSent = true;
                        Log.Warn("flow: 启动前是源账号会话 → 先登出，再等目标账号刷卡");
                        try { LoginHooks.BeginLogout("清理源账号旧会话"); } catch (Exception) { }
                    }
                    StatusText = "正在清理源账号旧会话…";
                    if (age > 60) Fail("清理旧会话超时");
                    return;
                }
                else
                {
                    _staleSessionAtStart = false;
                    _staleLogoutSent = false;
                }
            }

            if (LoginHooks.IsLoggedIn(out monitor, out userId, out userName))
            {
                _monitor = monitor;
                _sessionLoginAt = UnityEngine.Time.realtimeSinceStartup;
                Log.Info("flow: " + (source ? "source" : "target") + " account logged in: " + userName +
                         " uid=" + userId + " monitor=" + monitor);

                if (!source)
                {
                    _targetName = userName;
                    if (_sourceUserId != 0 && (long)userId == _sourceUserId)
                    {
                        if (!ModConfig.Data.AllowSelfTransfer)
                        {
                            Fail("目标账号与源账号相同 (uid=" + userId + ")，拒绝自我转生（调试可开 ConfigData.AllowSelfTransfer）");
                            return;
                        }
                        Log.Warn("flow: self-transfer enabled (dev only) - target uid == source uid (" + userId + ")");
                    }
                    if (Plan != null && Plan.Items.Count > 0 && _resumeAfterRelogin)
                    {
                        // relogin after the confirm screen timed out: the plan is already built
                        StartCreditLoop();
                        return;
                    }
                    SetPhase(FlowPhase.TargetReading, "已登录 " + userName + "，读取目标账号并进入选歌页…");
                }
                else
                {
                    SetPhase(FlowPhase.SourceReading, "已登录 " + userName + "，进入选歌页读取数据…");
                }
                SetStep(Step.WaitMusicSelect, "");
                _musicSelectAt = -1;
                _readyAt = -1;
                _readingTraceAt = -1;
                return;
            }

            if (LoginHooks.LoginBlocked)
            {
                StatusText = "服务端 isLogin 未清 (returnCode=100)：请等待约15分钟或人工登出后再试";
                return;
            }

            // The Entry confirm page ("このAimeでプレイを開始しますか？") is driven by Navigation's
            // own EntryProcess.OnUpdate hook; here we only swipe + poll the login state.
            string code = source ? SourceAccessCode : TargetAccessCode;
            if (string.IsNullOrEmpty(code)) code = ModConfig.Data.AimeCode;
            AutoSwipe(code, who: source ? "源" : "目标");

            string who2 = source ? "源" : "目标";
            StatusText = (string.IsNullOrEmpty(code) ? "请刷入" + who2 + "账号卡（等待刷卡）" : "正在模拟刷卡登录" + who2 + "账号");
            if (_swipeAttempts >= MaxSwipeAttempts && !string.IsNullOrEmpty(code))
                StatusText = "自动刷卡已停（" + MaxSwipeAttempts + " 次），请人工刷卡或按中止  [" + who2 + "账号]";
            if (_loginTraceAt < 0) _loginTraceAt = now;
            if (now - _loginTraceAt >= 15.0)
            {
                // Actionable trace for the real-machine runs: which screen are we stuck on?
                _loginTraceAt = now;
                Log.Info("[flow] still waiting for " + who2 + " login (" + age.ToString("0") + "s) - " +
                         Navigation.Describe() + "  swipe=" + LoginHooks.SwipeCount +
                         " loginBlocked=" + LoginHooks.LoginBlocked);
            }
            if (age > ModConfig.Data.LoginWaitTimeoutSec && !_loginWarned)
            {
                _loginWarned = true;
                Log.Warn("flow: still waiting for the " + who2 + " card after " + age.ToString("0") +
                         "s - operator can still swipe manually or press abort");
            }
        }

        // ---------------------------------------------------------------- login helpers

        private const int MaxSwipeAttempts = 6;

        private static void AutoSwipe(string code, string who)
        {
            // 测试用自动刷卡默认关闭：只有 config 里 AutoSwipe=true 才模拟刷卡，否则停在登录页等人工刷卡。
            if (!ModConfig.Data.AutoSwipe) return;
            if (string.IsNullOrEmpty(code)) return;
            if (_swipeAttempts >= MaxSwipeAttempts)
            {
                if (!_swipeCapWarned)
                {
                    _swipeCapWarned = true;
                    Log.Warn("flow: auto-swipe capped at " + MaxSwipeAttempts + " for " + who +
                             " account - waiting for a manual swipe");
                }
                return;
            }
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (now - LoginHooks.LastSwipeAt < 4.0) return;
            if (LoginHooks.RequestLogin(code)) _swipeAttempts++;
        }

        private static void ResetSwipeBudget()
        {
            _swipeAttempts = 0;
            _swipeCapWarned = false;
            _loginTraceAt = -1;
        }

        // ---------------------------------------------------------------- reading steps

        private static void TickReading(bool source)
        {
            double now = UnityEngine.Time.realtimeSinceStartup;

            int monitor;
            ulong uid;
            string name;
            if (!LoginHooks.IsLoggedIn(out monitor, out uid, out name))
            {
                // session vanished while we were walking to the song select: restart the login
                Log.Warn("flow: session lost while reading " + (source ? "source" : "target"));
                if (source) { SetPhase(FlowPhase.SourceLoginPending, "会话丢失，请重新刷入源账号"); SetStep(Step.WaitLogin, ""); }
                else { SetPhase(FlowPhase.TargetLoginPending, "会话丢失，请重新刷入目标账号"); SetStep(Step.WaitLogin, ""); }
                _loginWarned = false;
                return;
            }
            if (_monitor != monitor) _monitor = monitor;

            if (_step != Step.WaitMusicSelect && _step != Step.ReadLive)
            {
                SetStep(Step.WaitMusicSelect, "");
                _readyAt = -1;
                _readingTraceAt = -1;
                _lastReadyScoreCount = 0;
                _emptyGraceRead = false;
                _emptyGraceWarned = false;
                _modeSelectSeen = false;
            }
            double stepAge = now - _stepAt;

            if (_step == Step.WaitMusicSelect)
            {
                // Reading happens on the MODE SELECT screen (verified by the user: RATING/成绩 are
                // only available there, the GetUserPreview zeros are a red herring).  So we DO drive
                // the reception screen -> MODE SELECT here; nav stops advancing once we arrive.
                Navigation.GoToMusicSelect();

                GameStage stage = GameStagePump.Stage;
                string nav = Navigation.Describe();
                bool atModeSelect = stage == GameStage.ModeSelect ||
                                    (nav != null && nav.IndexOf("ModeSelectProcess", StringComparison.Ordinal) >= 0);
                if (atModeSelect && !_modeSelectSeen)
                {
                    _modeSelectSeen = true;
                    Log.Info("[flow] 已到 MODE SELECT，等待数据… (nav=" + nav + ")");
                }

                int scoreCount = 0;
                try
                {
                    var scores = UserDataPatcher.ReadScores(_monitor);
                    if (scores != null) scoreCount = scores.Count;
                }
                catch (Exception) { }

                double sinceLogin = _sessionLoginAt > 0 ? now - _sessionLoginAt : stepAge;
                bool dataReady = scoreCount > 0;
                // Empty account: after the reception skip + transition + download 60s is realistic.
                bool graceExpired = sinceLogin >= 60.0;

                if (dataReady)
                {
                    if (scoreCount != _lastReadyScoreCount)
                    {
                        _lastReadyScoreCount = scoreCount;
                        _readyAt = now;
                    }
                    else if (_readyAt < 0)
                    {
                        _readyAt = now;
                    }
                    if (now - _readyAt >= StableReadSec)
                    {
                        _emptyGraceRead = false;
                        _readTries = 0;
                        _lastReadTryAt = -100;
                        SetStep(Step.ReadLive, "");
                    }
                }
                else if (graceExpired)
                {
                    if (!_emptyGraceWarned)
                    {
                        _emptyGraceWarned = true;
                        Log.Warn("flow: account data still empty after " + sinceLogin.ToString("0.0") +
                                 "s - accepting an empty account (stage=" + GameStagePump.Stage + ")");
                    }
                    _emptyGraceRead = true;
                    _readTries = 0;
                    _lastReadTryAt = -100;
                    SetStep(Step.ReadLive, "");
                }
                else
                {
                    _readyAt = -1;
                }

                if (now - _readingTraceAt >= 5.0)
                {
                    _readingTraceAt = now;
                    Log.Info("[flow] reading: waiting for account data… stage=" + stage +
                             " atModeSelect=" + atModeSelect + " scores=" + scoreCount +
                             " sinceLogin=" + sinceLogin.ToString("0.0") + "s nav=" + nav +
                             (source ? " (source)" : " (target)"));
                }
                StatusText = "等待 MODE SELECT 数据… (scores=" + scoreCount + ", stage=" + stage + ")";
                if (stepAge > 120) Fail("等待账号数据加载超时 (stage=" + stage + ", scores=" + scoreCount + ")");
                return;
            }

            // ---- ReadLive ----
            StatusText = "读取账号全量成绩…";
            if (now - _lastReadTryAt < 2.0) return;
            _lastReadTryAt = now;
            _readTries++;

            SourceSnapshot snap = UserDataPatcher.ReadLive(_monitor);
            if (snap == null || snap.Records == null)
            {
                Log.Warn("flow: ReadLive returned null (try " + _readTries + ") stage=" + GameStagePump.Stage);
                if (_readTries > 15) Fail("读取账号数据失败（UserDataPatcher.ReadLive 返回 null, stage=" +
                                          GameStagePump.Stage + "）");
                return;
            }
            // 登录后数据是异步分页下载的：第一次读到的可能是残缺快照（导致重复转生/降分）。
            // 要求「连续两次条数相同」且登录满 5 秒，再接受这份快照。
            double loginElapsed = _sessionLoginAt > 0 ? now - _sessionLoginAt : 0;
            if (snap.Records.Count == _snapLastCount) _snapStableReads++;
            else _snapStableReads = 0;
            _snapLastCount = snap.Records.Count;
            if (_snapStableReads < 1 || loginElapsed < 5.0)
            {
                StatusText = "等待账号数据完整… (" + snap.Records.Count + " 条, " + loginElapsed.ToString("0.0") + "s)";
                return;
            }

            if (snap.Records.Count == 0 && !_emptyGraceRead)
            {
                // Data looked ready (scoreCount>0) but the snapshot is empty: probably read while the
                // download was still being applied - retry instead of accepting an empty profile.
                Log.Warn("flow: ReadLive returned 0 records although data was ready (scoreCount=" +
                         _lastReadyScoreCount + ", try " + _readTries + ") - retrying");
                if (_readTries > 15) Fail("读取账号数据失败（数据已就绪但读出 0 条, stage=" +
                                          GameStagePump.Stage + "）");
                return;
            }
            if (snap.Records.Count == 0)
            {
                // Empty account after the grace period: accept it (log/UI show 0 条).
                Log.Warn("flow: ReadLive returned an EMPTY account (0 records) - accepting (stage=" +
                         GameStagePump.Stage + ")");
            }

            snap.UserName = string.IsNullOrEmpty(snap.UserName) ? name : snap.UserName;
            if (snap.UserId == 0) snap.UserId = (long)uid;

            if (source)
            {
                Source = snap;
                _sourceUserId = snap.UserId;
                Plan = null;
                try
                {
                    string file = BackupStore.Save(snap);
                    Log.Info("flow: source snapshot saved to " + file);
                }
                catch (Exception e)
                {
                    Log.Error("flow: backup write failed", e);
                }
                // A session is open: close it through the game's own save/logout chain.
                StartShutdown(false, "源数据已获取", FlowPhase.SourceReady);
            }
            else
            {
                _targetRating = UserDataPatcher.ReadRating(_monitor);
                _targetRecords = snap.Records;
                try                 {                     FakerSwap.Network.PayloadRewriter.SetKnownMusic(_targetRecords);
                         Log.Info("flow: isNew 修正基线 = 账号已有 " + _targetRecords.Count + " 条谱面");                 }                 catch (Exception e) { Log.Warn("flow: SetKnownMusic 失败: " + e.Message); }
                Log.Info("flow: target account read, rating=" + _targetRating + ", records=" + snap.Records.Count);
                SetPhase(FlowPhase.Planning, "生成转生计划…");
                SetStep(Step.Planning, "");
            }
        }

        // ---------------------------------------------------------------- planning / confirm

        private static void TickPlanning()
        {
            // DEV ONLY: normally "the target already has an equal/better score" charts are skipped.
            // For single-account testing the plan would always be empty, so ForceOverwriteExisting
            // (ConfigData, default false) disables that filter.
            bool skipAlreadyDone = !ModConfig.Data.ForceOverwriteExisting;
            if (!skipAlreadyDone)
                Log.Warn("flow: force overwrite existing enabled (dev only) - charts already present on the target will be replayed");

            PlanResult pr = PlanBuilder.Build(Source, ModConfig.Data.MinLevel, ModConfig.Data.MaxLevel,
                                              _targetRecords, skipAlreadyDone);

            // 重登后重新规划：把本 run 已经上传过的谱面剔除（否则会重播上一批，官方服判定重复 new 返回 1）
            try
            {
                int replayed = 0;
                for (int i = pr.Items.Count - 1; i >= 0; i--)
                {
                    var it = pr.Items[i];
                    if (_uploadedThisRun.Contains(it.MusicId + "-" + it.Level))
                    {
                        pr.Items.RemoveAt(i);
                        replayed++;
                    }
                }
                if (replayed > 0)
                    Log.Warn("flow: 跳过本 run 已上传的 " + replayed + " 条（防重登后重播/重复 new）");
            }
            catch (Exception e) { Log.Warn("flow: 已上传过滤失败: " + e.Message); }

            // B/C play-realism modes need a self-consistent judge breakdown attached to every
            // record, so plan / settlement / playlog all use the same distribution.
            try
            {
                int judges = PlanBuilder.EnsureJudges(pr);
                if (judges > 0)
                    Log.Info("flow: 判定表已生成 " + judges + " 条 (B/C 模式，与结算/playlog 同源)");
            }
            catch (Exception e)
            {
                Log.Warn("flow: EnsureJudges failed (nav 会用自己的分布兜底): " + e.Message);
            }

            // 長尺曲（长版歌曲）：正常游玩。游戏内它占 2 个轨道，所以单独成 credit：
            // [长曲, 1 首普通]（2 playlogs，长曲占 TRACK 1-2，普通在 TRACK 3）。
            string longMode = ModConfig.Data.LongSongMode ?? "PlayWithOne";
            var longItems = new List<TransferItem>();
            try
            {
                for (int i = pr.Items.Count - 1; i >= 0; i--)
                {
                    var item = pr.Items[i];
                    if (!IsLongMusic(item.MusicId)) continue;
                    pr.Items.RemoveAt(i);
                    if (string.Equals(longMode, "Skip", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Info("flow: 跳过長尺曲 " + item.MusicId + "-" + item.Level);
                    }
                    else if (string.Equals(longMode, "DirectWrite", StringComparison.OrdinalIgnoreCase))
                    {
                        _longSongItems.Add(item);
                    }
                    else
                    {
                        longItems.Add(item);
                    }
                }
                if (_longSongItems.Count > 0)
                    Log.Warn("flow: 長尺曲直写 " + _longSongItems.Count + " 首（不游玩，随 DataSave 上传）");
            }
            catch (Exception e) { Log.Warn("flow: 長尺曲处理失败: " + e.Message); }
            if (pr.Items.Count == 0)
            {
                Fail("计划为空：源数据里没有需要转生的红谱以上成绩（missing=" + pr.Missing.Count +
                     " outOfRange=" + pr.OutOfRange.Count + " alreadyDone=" + pr.AlreadyDone.Count + "）");
                return;
            }

            var plan = new TransferPlan();
            plan.Items = pr.Items;
            plan.AlreadyDone = pr.AlreadyDone.Count;
            foreach (var m in pr.Missing) plan.SkippedMusicIds.Add(m.MusicId);

            // Long songs get their own credit: [long, one normal] (long occupies TRACK 1-2, 2 playlogs)
            var longBatches = new List<List<Slot>>();
            foreach (var li in longItems)
            {
                var lb = new List<Slot> { MakeTransferSlot(li) };
                if (pr.Items.Count > 0)
                {
                    lb.Add(MakeTransferSlot(pr.Items[0]));
                    pr.Items.RemoveAt(0);
                }
                longBatches.Add(lb);
                Log.Info("flow: long-song credit: " + li.MusicId + "-" + li.Level + " + 1 normal (2 tracks)");
            }
            _batches.InsertRange(0, longBatches);
            _batches = BuildBatches(pr.Items, out int missionCount);
            plan.Missions = missionCount;
            plan.Credits = _batches.Count;

            // Estimate depends on the play-realism mode:
            //   C ("Duration"): every chart is played for its real length (+ gap), like a human.
            //   B ("Judge")   : charts are skipped immediately, only the 15s server gap remains.
            bool durationMode = IsDurationRealism;
            double perCredit = Math.Max(ModConfig.Data.BaseOverheadPerCreditSec,
                                        Navigation.EstimateCreditOverheadSec(!durationMode));
            double gapSec = Math.Max(0.0, ModConfig.Data.TrackGapSec);
            double totalSec;
            if (durationMode)
            {
                double sum = 0;
                string src = "";
                foreach (var it in plan.Items) sum += TrackLengthSec(it.MusicId, it.Level, out src);
                totalSec = sum + plan.Items.Count * gapSec + plan.Credits * perCredit;
                Log.Info("flow: C 模式预估 avg chart " +
                         (plan.Items.Count > 0 ? (sum / plan.Items.Count).ToString("0.0") : "?") + "s × " +
                         plan.Items.Count + " + gaps + " + plan.Credits + " credits overhead");
            }
            else
            {
                totalSec = plan.Items.Count * Math.Max(5.0, gapSec) + plan.Credits * perCredit;
                Log.Info("flow: B 模式预估 " + plan.Items.Count + " tracks × gap " + gapSec + "s + " +
                         plan.Credits + " credits overhead");
            }
            plan.Estimate = TimeSpan.FromSeconds(totalSec);
            plan.EstimateText = TimeEstimator.Describe(plan.Estimate) +
                                (durationMode ? "（C 模式·挂机）" : "（B 模式·秒跳）");

            Plan = plan;
            _batchIndex = 0;
            _resumeAfterRelogin = true;
            Log.Info("flow: plan " + plan.Total + " records in " + plan.Credits + " credits, estimate " +
                     plan.EstimateText + " (filler=" + ModConfig.Data.FillerMusicId +
                     ", batch=" + EffectiveBatchSize + ", alreadyDone=" + plan.AlreadyDone +
                     ", missing=" + pr.Missing.Count + ")");

            if (ModConfig.Data.ConfirmBeforeStart && !_autoConfirmRelogin)
            {
                SetPhase(FlowPhase.AwaitingConfirm,
                    "待确认: " + plan.Total + " 条 / " + plan.Credits + " 批 / 预计 " + plan.EstimateText);
                SetStep(Step.WaitConfirm, "");
            }
            else if (_autoConfirmRelogin)
            {
                Log.Warn("[flow] 续关不可用后重新登录 → 自动开始（沿用同一源，只排未完成成绩）");
                StartCreditLoop();
            }
            else
            {
                StartCreditLoop();
            }
        }

        private static void TickConfirm()
        {
            if (!LoginHooks.IsLoggedIn(out _monitor))
            {
                StatusText = "等待确认（注意：目标账号会话已超时，点开始后会重新登录）";
            }
            else if (GameStagePump.Stage != GameStage.MusicSelect)
            {
                Navigation.GoToMusicSelect();
                StatusText = "等待确认（游戏已回到 " + GameStagePump.Stage + "，会自动校正）";
            }
            else
            {
                StatusText = "待确认: " + (Plan != null ? Plan.Total + " 条 / " + Plan.Credits + " 批 / 预计 " + Plan.EstimateText : "");
            }
        }

        // ---------------------------------------------------------------- credit loop

        private static int EffectiveBatchSize
        {
            get
            {
                int bs = ModConfig.Data.BatchSize;
                if (bs >= 3) return 3;
                if (bs == 2)
                {
                    // The server rejects an upload with exactly two playlogs, so 2 is not a real
                    // option: fall back to 1 (safe) rather than inventing fillers for every credit.
                    if (!_twoWarned)
                    {
                        _twoWarned = true;
                        Log.Warn("flow: BatchSize=2 is rejected by the server; using 1 instead");
                    }
                    return 1;
                }
                return 1;
            }
        }
        private static bool _twoWarned;

        private static bool CanUseFiller { get { return ModConfig.Data.FillerMusicId > 0; } }

        /// <summary>DataManager 判定長尺曲（长版歌曲），取不到时按 false 处理。</summary>
        private static bool IsLongMusic(int musicId)
        {
            try
            {
                var dm = MAI2.Util.Singleton<Manager.DataManager>.Instance;
                if (dm == null) return false;
                var music = dm.GetMusic(musicId);
                if (music == null) return false;
                return dm.IsLong(music.longMusic);
            }
            catch (Exception) { return false; }
        }

        /// <summary>Normal transfer slot.</summary>
        private static Slot MakeTransferSlot(TransferItem item)
        {
            return new Slot { Kind = SlotKind.Transfer, Item = item };
        }

        private static List<List<Slot>> BuildBatches(List<TransferItem> items, out int missionCount)
        {
            missionCount = 0;
            var batches = new List<List<Slot>>();
            int bs = EffectiveBatchSize;
            int i = 0;
            while (i < items.Count)
            {
                int take = Math.Min(bs, items.Count - i);
                var batch = new List<Slot>();
                for (int k = 0; k < take; k++)
                    batch.Add(new Slot { Item = items[i + k], Kind = SlotKind.Transfer });
                i += take;

                if (bs == 3 && batch.Count < 3)
                {
                    if (CanUseFiller)
                    {
                        PadWithFiller(batch);
                    }
                    else
                    {
                        // no filler configured: never create a 2-playlog credit
                        foreach (var s in batch) batches.Add(new List<Slot> { s });
                        break;
                    }
                }
                batches.Add(batch);
            }

            try
            {
                if (ModConfig.Data.AutoAddMissionSongs)
                {
                    var taken = MapHooks.TakeMissionCharts();
                    if (taken != null && taken.Length > 0)
                    {
                        var mission = new List<Slot>();
                        for (int k = 0; k < taken.Length && mission.Count < 3; k++)
                        {
                            var record = new ScoreRecord
                            {
                                MusicId = taken[k].MusicId,
                                Level = taken[k].Level,
                                PlayCount = 1,
                                Achievement = ScoreHooks.MinClearAchievement
                            };
                            mission.Add(new Slot
                            {
                                Kind = SlotKind.Mission,
                                Item = new TransferItem { Desired = record, Index = -1, SkipReason = "mission" }
                            });
                        }
                        if (mission.Count == 2 && CanUseFiller) PadWithFiller(mission);
                        if (mission.Count == 2)
                        {
                            // no filler configured: two single-playlog credits are still valid
                            batches.Add(new List<Slot> { mission[0] });
                            batches.Add(new List<Slot> { mission[1] });
                        }
                        else if (mission.Count > 0)
                        {
                            batches.Add(mission);
                        }
                        missionCount = mission.Count;
                        Log.Info("flow: added " + mission.Count + " mission chart(s) to the plan");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("flow: mission chart integration failed: " + e.Message);
            }

            return batches;
        }

        private static void PadWithFiller(List<Slot> batch)
        {
            while (batch.Count < 3)
            {
                var record = new ScoreRecord
                {
                    MusicId = ModConfig.Data.FillerMusicId,
                    Level = ModConfig.Data.FillerLevel,
                    PlayCount = 1,
                    Achievement = 0
                };
                batch.Add(new Slot
                {
                    Kind = SlotKind.Filler,
                    Item = new TransferItem { Desired = record, Index = -1, SkipReason = "filler" }
                });
            }
        }

        private static void StartCreditLoop()
        {
            SetPhase(FlowPhase.Transferring, "开始转生");
            _batchIndex = 0;
            _stopping = false;
            BeginCredit();
        }

        /// <summary>
        /// Clear navigation's "logout mode" latch when a new credit starts (same-session continue).
        /// The previous credit's DeclineContinue/ForceTimeUp path otherwise makes the ~5-7s
        /// MODE SELECT window ignore OK and fall back to Title.
        /// </summary>
        private static void TryResetNavLogoutMode()
        {
            // One-shot: BeginWriteCredit resets nav's MODE SELECT key ladder to P0, so calling
            // it every frame froze the screen at MODE SELECT forever (observed 12:22).
            if (_navResetDone) return;
            _navResetDone = true;
            try { Navigation.BeginWriteCredit(_monitor >= 0 ? _monitor : 0); }
            catch (Exception e)
            {
                if (!_navResetWarned)
                {
                    _navResetWarned = true;
                    Log.Warn("flow: BeginWriteCredit failed: " + e.Message);
                }
            }
        }
        private static bool _navResetWarned;
        private static bool _navResetDone;
        private static int _missionCreditsAdded;
        /// <summary>When true the flow never presses CONTINUE YES; the operator does it.</summary>
        public static bool ManualContinueYes;
        private static bool _manualContinueLogged;
        /// <summary>
        /// Clear quick-retry / skip residue left by the previous credit.  GameProcess.OnStart takes a
        /// different path when GamePlayManager believes this is a quick retry, and after CONTINUE it
        /// crashed with a NullReferenceException in exactly that spot.
        /// </summary>
        private static void SetSkipFlagSafe(Manager.GamePlayManager mgr, int track)
        {
            try { mgr.SetTrackSkipFlag(0, track); } catch (Exception) { }
        }

        private static void ClearGamePlayManagerResidue()
        {
            try
            {
                var mgr = MAI2.Util.Singleton<Manager.GamePlayManager>.Instance;
                if (mgr == null) { Log.Warn("flow: GamePlayManager null"); return; }
                bool quick = false;
                try { quick = mgr.IsQuickRetry(); } catch (Exception) { }
                try { mgr.SetQuickRetryFrag(false); } catch (Exception) { }
                for (int t = 0; t < 4; t++)
                {
                    try { SetSkipFlagSafe(mgr, t); } catch (Exception) { }
                }
                Log.Info("flow: 已清理 GamePlayManager 残留 (quickRetry=" + quick +
                         ", scoreLists=" + mgr.GetScoreListCount() + ")");
            }
            catch (Exception e) { Log.Warn("flow: 清理 GamePlayManager 失败: " + e.Message); }
        }

        private static void BeginCredit()
        {
            _navResetDone = false;
            ScoreHooks.MinClearAchievement = ModConfig.Data.MissionAchievement;
            // 课题曲改为"占本 credit 一个轨道位置"（ApplyMissionToCurrentCredit），不再追加额外批次。
            TryResetNavLogoutMode();
            if (_lastUploadFinishedAt > 0)
            {
                double handover = UnityEngine.Time.realtimeSinceStartup - _lastUploadFinishedAt;
                Log.Info("[flow] 批次衔接: 上一批上传完成 → 本批开始 用时 " + handover.ToString("0.0") + "s");
            }
            if (_batchIndex < 0) _batchIndex = 0;
            if (_batchIndex >= _batches.Count)
            {
                StartShutdown(false, "全部完成", FlowPhase.Finished);
                return;
            }
            _credit = _batches[_batchIndex];
            _areaMissionQueued = false;   // 每个 credit 都要从当前区域表再排一首课题曲
            _missionQueuedThisCredit = false;
            // 选歌页扫描会把已打过的课题曲重新塞回 PendingMissionCharts：新 credit 开始时清掉，
            // 否则「区域表直接排入」会被 pending 非空挡住，下一首课题曲永远排不进来。
            try
            {
                if (MapHooks.PendingMissionCharts != null && MapHooks.PendingMissionCharts.Count > 0)
                    MapHooks.PendingMissionCharts.RemoveAll(delegate(MapHooks.MissionChart m)
                    {
                        return m != null && _missionPlacedIds.Contains(m.MusicId);
                    });
            }
            catch (Exception) { }
            _slot = 0;
            _startedInCredit = 0;
            _abortSlot = -1;
            _sawDataSave = false;
            _dataSaveSeenAt = -1;
            // 本 credit 的上传对账基准：开始时的 DataSave 计数。批结束时必须严格大于它，
            // 否则就是"没走 DataSave 就回到标题/刷卡页"——成绩根本没上传，绝不能记为完成。
            _creditDataSaveBase = LoginHooks.DataSaveCount;
            if (_continueTrackResetPending)
            {
                _continueTrackResetPending = false;
                try
                {
                    // 续关不会把 GameManager.MusicTrackNumber 重置回 1（实机 22:57/23:05 都是 3），
                    // GameProcess.OnStart 用 MusicTrackNumber-1=2 去取分数列表，得到 null → NRE，
                    // 随后 ResultProcess.OnStart 的 GamePlayManager.GetAchivement 再 NRE 整局崩。
                    Manager.GameManager.MusicTrackNumber = 1;
                    var ids = Manager.GameManager.SelectMusicID;
                    if (ids != null) for (int i = 0; i < ids.Length; i++) ids[i] = 0;
                    Log.Warn("[flow] 续关后重置游戏轨道计数: MusicTrackNumber=1, SelectMusicID 清零");
                }
                catch (Exception e) { Log.Warn("flow: 重置 MusicTrackNumber 失败: " + e.Message); }
            }
            try { FakerSwap.Network.PayloadRewriter.ClearPending(); }   // 上一批的残留改写不能漏到本批
            catch (Exception e) { Log.Warn("flow: ClearPending failed: " + e.Message); }
            _lastTrackStartAt = -1;
            _musicSelectAt = -1;
            CurrentCredit = _batchIndex + 1;
            CurrentTrack = 1;
            CurrentItem = null;
            _trackSettled = false;
            _uploadForceLogged = false;
            _photoEditWaitLogged = false;
                            ClearGamePlayManagerResidue();
            _reloginByContinue = false;
            _continueYesLogged = false;
            try { MapHooks.ResetForCredit(); }
            catch (Exception e) { Log.Warn("flow: MapHooks.ResetForCredit failed: " + e.Message); }
            try { ScoreHooks.ResetForCredit(); }
            catch (Exception e) { Log.Warn("flow: ScoreHooks.ResetForCredit failed: " + e.Message); }
            SetCStep(Step.BatchWaitMusicSelect,
                "第 " + CurrentCredit + "/" + _batches.Count + " 批：进入选歌页…");
            Log.Info("flow: credit " + CurrentCredit + "/" + _batches.Count + " with " + _credit.Count + " slot(s)");
        }

        // ---------------------------------------------------------------- transfer engine

        private static void TickTransfer()
        {
            if (!ProcessHooks.Ready)
            {
                StatusText = "等待游戏进程…";
                return;
            }

            int monitor;
            bool loggedOutOk = _step == Step.BatchWaitLogout || _step == Step.BatchWaitRelogin
                               || _step == Step.BatchWaitUpload || _step == Step.BatchAfterCredit;
            if (!LoginHooks.IsLoggedIn(out monitor) && !loggedOutOk)
            {
                if (_stopping)
                {
                    StartShutdown(false, "已中止", FlowPhase.Finished);
                }
                else
                {
                    Fail("会话意外结束（stage=" + GameStagePump.Stage + "），剩余 " +
                         (Plan != null ? Plan.Total - Plan.Done : 0) + " 条未完成");
                }
                return;
            }
            if (monitor >= 0) _monitor = monitor;

            switch (_step)
            {
                case Step.BatchWaitMusicSelect: TickWaitMusicSelect(); break;
                case Step.BatchWaitGap: TickWaitGap(); break;
                case Step.BatchSelect: TickSelect(); break;
                case Step.BatchStart: TickStart(); break;
                case Step.BatchPlay: TickPlay(); break;
                case Step.BatchResult: TickResult(); break;
                case Step.BatchDecide: TickDecide(); break;
                case Step.BatchWaitUpload: TickWaitUpload(); break;
                case Step.BatchAfterCredit: TickAfterCredit(); break;
                case Step.BatchWaitLogout: TickWaitLogout(); break;
                case Step.BatchWaitRelogin: TickWaitRelogin(); break;
                default:
                    BeginCredit();
                    break;
            }
        }

        private static void TickWaitMusicSelect()
        {
            // 课题曲来源二选一：优先用区域表（MapBonusMusic.xml）直接排，确定性最高，
            // 不依赖选歌页「ちほー課題曲」分类的懒加载；排不到再由 ScanMusicSelect 兜底。
            // 课题曲只打**游戏里已经出现**的（_combineMapTaskDataList 扫描）；不再按区域表提前把整张
            // BonusMusic 列表排进计划（用户反馈：那样会把没出现的课题曲也提前打了）。
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (GameStagePump.Stage == GameStage.MapSelect)
            {
                if (Navigation.IsTaskActive()) TickAreaSelection();
                if (Navigation.IsTaskActive()) Navigation.SkipPhotoShoot(_monitor >= 0 ? _monitor : 0);
                StatusText = "选区页: 正在选择区域…";
                return;
            }
            if (GameStagePump.Stage == GameStage.PhotoEdit)
            {
                TryPressPhotoEditSkip();
                StatusText = "写真页: 等待跳过…";
                return;
            }
            if (AtCleanMusicSelect())
            {
                ApplyMissionToCurrentCredit();   // 每帧尝试（课题曲扫描是懒加载的，可能晚几帧）
                if (_musicSelectAt < 0) _musicSelectAt = now;
                // 每个 credit 的第一轨：最多多等 2.5s，让 MapHooks 的课题曲列表（懒加载）扫描完成；
                // 扫到就由 ApplyMissionToCurrentCredit 占本批一个位置。
                if (_slot == 0 && MapHooks.MissionAreaReady && MapHooks.PendingMissionCharts.Count == 0)
                {
                    if (_missionScanWaitAt < 0) _missionScanWaitAt = now;
                    if (now - _missionScanWaitAt < 2.5)
                    {
                        ApplyMissionToCurrentCredit();
                        StatusText = "选歌页: 等待课题曲扫描…";
                        return;
                    }
                }
                else _missionScanWaitAt = -1;
                if (now - _musicSelectAt >= StableMusicSelectSec)
                {
                    SetCStep(Step.BatchWaitGap, "准备第 " + (_slot + 1) + " 轨…");
                    return;
                }
            }
            else
            {
                _musicSelectAt = -1;
                Navigation.GoToMusicSelect();
                StatusText = "第 " + CurrentCredit + "/" + _batches.Count + " 批：等待干净的选歌页… (" +
                             GameStagePump.Stage + ")";
            }
            if (StepAge > 75) Fail("进入选歌页超时 (stage=" + GameStagePump.Stage + ", nav=" + Navigation.Describe() + ")");
        }

        /// <summary>
        /// A song-select screen we can actually use.  Right after a relogin the previous credit's
        /// MapResult/Continue/Result screens are still in the process stack for a while; selecting a
        /// chart then makes the flow think the new track instantly "settled" (this caused a credit
        /// to be uploaded without being played).
        /// </summary>
        private static bool AtCleanMusicSelect()
        {
            GameStage s = GameStagePump.Stage;
            if (s == GameStage.MapResult || s == GameStage.Continue || s == GameStage.Result
                || s == GameStage.DataSave || s == GameStage.Entry || s == GameStage.Title
                || s == GameStage.Startup)
                return false;
            try
            {
                if (!Navigation.AtMusicSelect) return false;
            }
            catch (Exception) { return false; }
            return !HasStaleCreditScreen();
        }

        private static bool HasStaleCreditScreen()
        {
            // Structured check first (nav guarantees NavProcess.Any<T> + the Describe contract).
            try
            {
                if (NavProcess.Any<global::Process.ResultProcess>()) return true;
                if (NavProcess.Any<global::Process.ContinueProcess>()) return true;
                if (NavProcess.Any<global::Process.MapResultProcess>()) return true;
                if (NavProcess.Any<global::Process.GameOverProcess>()) return true;
                return false;
            }
            catch (Exception) { }
            // Fallback: parse the (stable) Describe() string.
            try
            {
                string d = Navigation.Describe();
                if (string.IsNullOrEmpty(d)) return false;
                return d.IndexOf("MapResultProcess", StringComparison.Ordinal) >= 0
                    || d.IndexOf("ContinueProcess", StringComparison.Ordinal) >= 0
                    || d.IndexOf("GameOverProcess", StringComparison.Ordinal) >= 0
                    || d.IndexOf("ResultProcess", StringComparison.Ordinal) >= 0;
            }
            catch (Exception) { return false; }
        }

        private static void TickWaitGap()
        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            double need = 0;

            // Anchor 1: the login that owns this session (SetLoginVO gives us the exact moment).
            double loginAt = LoginAnchor;
            if (loginAt > 0) need = loginAt + ModConfig.Data.TrackGapSec * (_slot + 1);

            // Anchor 2: at least TrackGapSec after the previous track started.
            if (_lastTrackStartAt > 0)
            {
                double gap = _lastTrackStartAt + ModConfig.Data.TrackGapSec;
                if (gap > need) need = gap;
            }

            // Anchor 3: the last track must start early enough for the upload to happen at least
            // UploadCooldownSec after the login (skip + result + save take roughly 9-12s).
            if (_slot == _credit.Count - 1 && loginAt > 0)
            {
                double uploadGate = loginAt + ModConfig.Data.UploadCooldownSec - 9.0;
                if (uploadGate > need) need = uploadGate;
            }

            if (now >= need)
            {
                SetCStep(Step.BatchSelect, "选择第 " + (_slot + 1) + " 轨…");
                return;
            }
            StatusText = "第 " + CurrentCredit + "/" + _batches.Count + " 批：等待曲间隔…";
        }

        private static void TickSelect()
        {
            Slot slot = _credit[_slot];
            CurrentItem = slot.Item;
            CurrentTrack = Math.Min(_slot + 1, 3);

            bool forcedSkip = _stopping && _abortSlot >= 0 && _slot >= _abortSlot;
            bool unmodified = forcedSkip && _slot == _abortSlot;

            try
            {
                if (slot.SkipPlay)
                {
                    ScoreHooks.BeginSkipUnmodified();
                }
                else if (slot.Kind == SlotKind.Filler || (forcedSkip && !unmodified))
                {
                    ScoreHooks.BeginFiller();
                }
                else if (unmodified)
                {
                    ScoreHooks.BeginSkipUnmodified();
                }
                else if (slot.Kind == SlotKind.Mission)
                {
                    ScoreHooks.BeginMission(slot.Item.Desired);
                    try { MapHooks.PlayedMissionMusicIds.Add(slot.Item.MusicId); } catch (Exception) { }
                }
                else
                {
                    ScoreHooks.BeginTransfer(slot.Item.Desired);
                }
            }
            catch (Exception e)
            {
                Log.Error("flow: ScoreHooks begin failed", e);
            }

            string tag = slot.Kind == SlotKind.Filler ? "filler"
                       : slot.Kind == SlotKind.Mission ? "mission"
                       : (unmodified ? "skip(no modify)" : "transfer");
            // 游戏可能已经自己开始（直接 GameStart 生效/上一帧过场）：别再选曲，直接进入游玩状态
            if (IsInGameplay() || GameStagePump.Stage == GameStage.TrackStart)
            {
                MarkTrackStarted();
                return;
            }
            if (Navigation.SelectChart(slot.Item.MusicId, slot.Item.Level))
            {
                SetCStep(Step.BatchStart, "进入 " + slot.Item.MusicId + "-" + slot.Item.Level + " [" + tag + "]");
                return;
            }
            StatusText = "选择 " + slot.Item.MusicId + "-" + slot.Item.Level + " [" + tag + "] …";
            if (StepAge > 25) Fail("选曲超时 " + slot.Item.MusicId + "-" + slot.Item.Level);
        }

        private static void TickStart()
        {
            GameStage stage = GameStagePump.Stage;
            bool started = stage == GameStage.TrackStart || stage == GameStage.GamePlay;
            // 注意：不能把 StartRequested 当成"已开始"（那只表示按过键/发过请求）
            if (started)
            {
                MarkTrackStarted();
                return;
            }
            if (Navigation.StartCurrentSelection())
            {
                MarkTrackStarted();
                return;
            }
            if (StepAge > 25) Fail("进入谱面超时" + DescribeSlot());
        }

        private static double LoginAnchor
        {
            get
            {
                double a = LoginHooks.LastLoginResponseAt;
                if (_sessionLoginAt > a) a = _sessionLoginAt;
                return a;
            }
        }

        private static void MarkTrackStarted()
        {
            _startedInCredit++;
            _lastTrackStartAt = UnityEngine.Time.realtimeSinceStartup;
            _playEnteredAt = _lastTrackStartAt;
            _playStuckLogAt = -100;
            _playStuckSkipAt = -100;
            _gameplayStartedAt = -1;
            _playIdleLogAt = -100;
            _playSkipTarget = -1;
            _playSkipLogged = false;
            _trackSettled = false;
            CurrentTrack = Math.Min(_slot + 1, 3);
            LoginHooks.NotePlaylog();
            SetCStep(Step.BatchPlay, "游玩中 " + DescribeSlot());
        }

        private static void TickPlay()
        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            GameStage stage = GameStagePump.Stage;

            if (IsInGameplay())
            {
                if (_slot >= 0 && _slot < _credit.Count && _credit[_slot].SkipPlay)
                {
                    // 中止后的占位轨：选曲占满 3 轨，但立刻跳过（空成绩），不挂机播放。
                    if (!_playSkipLogged)
                    {
                        _playSkipLogged = true;
                        Log.Info("[play] 占位轨立刻跳过（空成绩） " + DescribeSlot());
                    }
                    if (now - _playStuckSkipAt >= 2.0)
                    {
                        _playStuckSkipAt = now;
                        Navigation.SkipCurrentChart();
                    }
                    StatusText = "跳过占位轨 " + DescribeSlot();
                    return;
                }
                // 课题曲 MUST be played to the very end: skipping at 30% means the game never
                // records a clear, so the mission keeps reappearing (learned 18:4x).
                bool isMissionSlot = _slot >= 0 && _slot < _credit.Count && _credit[_slot].Kind == SlotKind.Mission;
                bool durationMode = IsDurationRealism || isMissionSlot;
                if (_gameplayStartedAt < 0) _gameplayStartedAt = now;
                double played = now - _gameplayStartedAt;

                if (durationMode)
                {
                    // ---- C mode: AFK - let the chart finish by itself.  The playlog duration is
                    // then exactly the real chart length, no estimation/skip needed at all.
                    if (now - _playIdleLogAt >= 10.0)
                    {
                        _playIdleLogAt = now;
                        Log.Info("[play] C 模式: 挂机等待谱面自然结束 (已等 " + played.ToString("0") +
                                 "s) music=" + CurrentMusicId);
                    }
                    StatusText = "挂机游玩中 " + DescribeSlot() + " (C 模式 已等 " + played.ToString("0") + "s)";
                }
                else
                {
                    // ---- B mode: look like a short human attempt, then skip.  NOT 1s anymore:
                    // ~30% of the chart length, clamped to 15..45s (user: "B是稍微过一会就跳过").
                    if (_playSkipTarget < 0)
                    {
                        string lenSrc;
                        double len = TrackLengthSec(CurrentMusicId, CurrentItem != null ? CurrentItem.Level : 3, out lenSrc);
                        _playSkipTarget = Math.Max(15.0, Math.Min(45.0, len * 0.30));
                        Log.Info("[play] B 模式: 跳过前等待 " + _playSkipTarget.ToString("0.0") + "s (~30% × " +
                                 len.ToString("0.0") + "s, src=" + lenSrc + ") music=" + CurrentMusicId);
                    }
                    if (played >= _playSkipTarget)
                    {
                        if (!_playSkipLogged)
                        {
                            _playSkipLogged = true;
                            Log.Info("[play] B 模式: 等待完成 " + played.ToString("0.0") +
                                     "s → 跳过 (判定明细自洽) music=" + CurrentMusicId);
                        }
                        Navigation.SkipCurrentChart();
                    }
                    StatusText = "游玩中 " + DescribeSlot() + " (B 模式 " +
                                 played.ToString("0") + "/" + _playSkipTarget.ToString("0") + "s)";
                }
            }
            else if (IsSettledStage(stage) && now - _lastTrackStartAt > 2.0)
            {
                // The photo-edit page (写真) is optional and must be skipped by nav; only wait here.
                if (stage == GameStage.PhotoEdit)
                {
                    TryPressPhotoEditSkip();
                    StatusText = "写真页: 等待跳过…";
                    return;
                }
                // Grace period: right after StartCurrentSelection the previous credit's
                // MapResult/Continue screen can still be on the stack.
                if (IsDurationRealism && _gameplayStartedAt > 0 && !_playSkipLogged)
                {
                    _playSkipLogged = true;
                    Log.Info("[play] C 模式: 谱面自然结束 用时 " + (now - _gameplayStartedAt).ToString("0.0") +
                             "s → 进入结算 music=" + CurrentMusicId);
                }
                SetCStep(Step.BatchResult, "结算 " + DescribeSlot());
                return;
            }

            // Stuck watchdog: C mode legitimately plays the whole chart (max ~3 min); B mode waits
            // ~30% of the chart then skips.
            double stuckLimit = IsDurationRealism
                ? 300.0
                : Math.Max(60.0, _playSkipTarget > 0 ? _playSkipTarget + 30.0 : 60.0);
            if (StepAge > stuckLimit)
            {
                if (now - _playStuckLogAt >= 5.0)
                {
                    _playStuckLogAt = now;
                    Log.Warn("flow: still in " + stage + " after " + StepAge.ToString("0") +
                             "s (limit " + stuckLimit.ToString("0") + "s, nav=" + Navigation.Describe() +
                             ") - forcing skip");
                }
                if (now - _playStuckSkipAt >= 1.0)
                {
                    _playStuckSkipAt = now;
                    Navigation.SkipCurrentChart();
                }
                if (StepAge > stuckLimit + 60.0)
                    Fail("游玩/跳曲超时 (stage=" + stage + ", nav=" + Navigation.Describe() + ")");
            }
        }

        /// <summary>Stages that mean "the track is over" (works even if the stage tracker lags).</summary>
        private static bool IsSettledStage(GameStage stage)
        {
            switch (stage)
            {
                case GameStage.Result:
                case GameStage.Continue:
                case GameStage.MapResult:
                case GameStage.DataSave:
                case GameStage.PhotoEdit:
                case GameStage.Entry:
                case GameStage.Title:
                    return true;
                default:
                    try { return Navigation.AtContinue; }
                    catch (Exception) { return false; }
            }
        }

        private static void TickResult()
        {
            // Photo-edit (写真) can appear after any track and blocks everything until skipped.
            // We never inject processes here - nav presses SKIP, we only wait.
            if (GameStagePump.Stage == GameStage.PhotoEdit)
            {
                TryPressPhotoEditSkip();
                StatusText = "写真页: 等待跳过… (" + DescribeSlot() + ")";
                return;
            }

            if (!_trackSettled)
            {
                _trackSettled = true;
                EndCurrentTrackHooks();
            }

            // The result screen needs "NEXT": nav drives ResultMonitor.SetNextWait/MoveNext and the
            // process chain (throttled internally, safe to call every frame).
            try { Navigation.PressResultNext(); }
            catch (Exception e) { Log.Warn("flow: PressResultNext failed: " + e.Message); }

            GameStage stage = GameStagePump.Stage;
            if (stage == GameStage.Continue) { SetCStep(Step.BatchDecide, "结算完成，选择是否续关…"); return; }
            if (stage == GameStage.Result) { StatusText = "结算画面…"; }
            else if (stage == GameStage.MusicSelect) { SetCStep(Step.BatchDecide, ""); return; }
            else if (stage == GameStage.MapResult || stage == GameStage.DataSave
                     || stage == GameStage.PhotoEdit || stage == GameStage.Entry || stage == GameStage.Title)
            {
                // MapResult also appears between tracks of the SAME credit (the game walks
                // Result -> MapResult -> NextTrack -> MusicSelect).  Only the last track may end in
                // the upload path; earlier tracks must continue the credit.
                if (_slot + 1 < _credit.Count && stage != GameStage.DataSave
                    && stage != GameStage.PhotoEdit && stage != GameStage.Entry && stage != GameStage.Title)
                {
                    SetCStep(Step.BatchDecide, "本轨结算完成，继续下一轨…");
                    return;
                }
                SetCStep(Step.BatchWaitUpload, "上传成绩…");
                return;
            }
            if (StepAge > 60) Fail("结算超时 (stage=" + stage + ")");
        }

        /// <summary>Clear the per-track score hook state and count this track as transferred.</summary>
        private static void EndCurrentTrackHooks()
        {
            try { ScoreHooks.EndTrack(); }
            catch (Exception e) { Log.Warn("flow: ScoreHooks.EndTrack failed: " + e.Message); }

            if (_slot < 0 || _slot >= _credit.Count) return;
            Slot s = _credit[_slot];
            if (s.Kind != SlotKind.Transfer || s.Item == null) return;
            if (s.SkipPlay) return;   // 中止占位轨不算转生完成
            // An aborted track was skipped without modification, it must not count as transferred.
            if (_stopping && _abortSlot >= 0 && _slot >= _abortSlot) return;
            s.Item.Done = true;
        }

        private static void TickDecide()
        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            bool moreTracks = _slot + 1 < _credit.Count;

            if (moreTracks)
            {
                // We still need to play the rest of this credit (transfer items or fillers).
                if (!Navigation.ContinueToNextTrack()) SetSelectContinue(true);
                GameStage stage = GameStagePump.Stage;
                if (stage == GameStage.MusicSelect)
                {
                    _slot++;
                    _musicSelectAt = -1;
                    SetCStep(Step.BatchWaitMusicSelect, "继续第 " + (_slot + 1) + " 轨…");
                    return;
                }
                if (stage == GameStage.TrackStart || stage == GameStage.GamePlay)
                {
                    // Still playing the current track: never advance the slot on a false "settled".
                    SetCStep(Step.BatchPlay, "仍在游玩 " + DescribeSlot());
                    return;
                }
                if (stage == GameStage.DataSave || stage == GameStage.Entry || stage == GameStage.Title)
                {
                    // The game ended the credit early; let the upload path handle it.
                    SetCStep(Step.BatchWaitUpload, "");
                    return;
                }
                StatusText = "请求续关… (" + stage + ")";
                if (now - _stepAt > 20) TrySyntheticButtonPress(Manager.InputManager.ButtonSetting.Button04);
                if (now - _stepAt > 45) Fail("无法续关（Continue 停在第 " + (_slot + 1) + " 轨）");
                return;
            }

            // Last track: with ContinueRelogin we MUST NOT decline - the game offers the CONTINUE
            // screen after DataSave and pressing YES starts the next credit in the same session.
            // (SDGB 1.55: declining here logged the session out and the following card re-login timed out.)
            if (ModConfig.Data.ContinueRelogin)
            {
                GameStage lc = GameStagePump.Stage;
                if (HasContinueProcess() || lc == GameStage.Continue)
                {
                    TryPressContinueYes();
                    StatusText = "续关: 按「是」进入下一批…";
                    return;
                }
                if (lc == GameStage.MapResult || lc == GameStage.DataSave)
                {
                    SetCStep(Step.BatchWaitUpload, "本局结束，等待上传…");
                    return;
                }
                // 其它阶段（Result/MusicSelect/PhotoEdit/Title…）继续等续关屏，绝不按「否」
                StatusText = "等待续关屏出现… (" + lc + ")";
                if (now - _stepAt > 30)
                {
                    LoginHooks.ForceDataSave("credit-end");
                    SetCStep(Step.BatchWaitUpload, "本局结束，等待上传…");
                }
                return;
            }
            // Non-continue mode: decline the continue so the credit ends and data gets saved.
            // Last track: decline the continue so the credit ends and data gets saved.
            if (!Navigation.DeclineContinue()) SetSelectContinue(false);
            GameStage last = GameStagePump.Stage;
            if (last == GameStage.MusicSelect)
            {
                // It went back to song select: decline by simply not selecting; wait for timeout.
                StatusText = "等待本局结束…";
            }
            else if (last == GameStage.MapResult || last == GameStage.DataSave || last == GameStage.PhotoEdit
                     || last == GameStage.Entry || last == GameStage.Title)
            {
                SetCStep(Step.BatchWaitUpload, "本局结束，等待上传…");
                return;
            }
            else
            {
                StatusText = "结束本局… (" + last + ")";
            }
            if (now - _stepAt > 30)
            {
                LoginHooks.ForceDataSave("credit-end");
                SetCStep(Step.BatchWaitUpload, "本局结束，等待上传…");
            }
        }

        private static void TickWaitUpload()
        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            GameStage stage = GameStagePump.Stage;

            // Mid-credit MapResult/Continue (NextTrackProcess) must NOT be treated as the batch end:
            // keep playing the remaining tracks of this credit.
            if (_slot + 1 < _credit.Count
                && (stage == GameStage.MapResult || stage == GameStage.Continue
                    || stage == GameStage.MusicSelect))
            {
                SetCStep(Step.BatchDecide, "本轨结算完成，继续下一轨… (" + stage + ")");
                return;
            }

            if (stage == GameStage.DataSave)
            {
                if (!_sawDataSave)
                {
                    _sawDataSave = true;
                    _dataSaveSeenAt = now;
                    Log.Info("flow: credit " + CurrentCredit + " upload started");
                }
                StatusText = "第 " + CurrentCredit + "/" + _batches.Count + " 批：上传成绩中…";
                return;
            }

            if (stage == GameStage.Continue && !_sawDataSave)
            {
                // The Continue before DataSave is the tail-chain "next track"/post-result page, not
                // the batch-end continue screen: the game still has to walk to DataSave by itself.
                StatusText = "等待本局结算保存… (" + stage + ")";
                return;
            }

            if (_sawDataSave)
            {
                if (LoginHooks.DataSaveCount <= _creditDataSaveBase)
                {
                    // 看到过 DataSave 屏但计数没涨 = 不是本 credit 的保存（例如上一批残留）。
                    FailCreditNoUpload("stage=" + stage + " DataSaveCount 未增加");
                    return;
                }
                Log.Info("flow: credit " + CurrentCredit + " upload finished (stage=" + stage +
                         ", DataSave#" + LoginHooks.DataSaveCount + ", pendingRewrite=" + FakerSwap.Network.PayloadRewriter.PendingCount + ")");
                _lastUploadFinishedAt = now;
                // 记录本 run 已上传的谱面，重登后重新规划时排除，避免重播同一批（官方服会因重复 new 返回 1）
                try
                {
                    if (_credit != null)
                    {
                        foreach (var s in _credit)
                        {
                            if (s == null || s.Item == null) continue;
                            if (s.Kind == SlotKind.Transfer || s.Kind == SlotKind.Mission)
                                _uploadedThisRun.Add(s.Item.MusicId + "-" + s.Item.Level);
                        }
                    }
                }
                catch (Exception) { }
                SetCStep(Step.BatchAfterCredit, "本批已上传");
                return;
            }

            if (stage == GameStage.Entry || stage == GameStage.Title)
            {
                if (LoginHooks.DataSaveCount <= _creditDataSaveBase)
                {
                    // 关键的数据一致性闸门：没有 DataSave 就回到登录界面 = 本批成绩压根没上传。
                    // 以前这里直接放行，于是「全部完成（N 条）」是假的。
                    FailCreditNoUpload("已回到 " + stage + " 但没有 DataSave");
                    return;
                }
                // already back at the reader: the game uploaded + logged out by itself
                SetCStep(Step.BatchAfterCredit, "已回到登录界面");
                return;
            }

            if (stage == GameStage.Continue)
            {
                if (ModConfig.Data.ContinueRelogin && !_stopping)
                {
                    SetSelectContinue(true);
                    SetCStep(Step.BatchWaitRelogin, "尝试续关登录下一批…");
                    _reloginDeadline = now + 25;
                    return;
                }
                SetSelectContinue(false);
            }

            // The game did not start a DataSave on its own: force it (this is also the logout).
            // Only force a DataSave when the credit is clearly stuck on the song-select screen
            // (i.e. the game did not start its own save).  NEVER on Result/MapResult/Continue/
            // PhotoEdit: those screens transition to DataSave by themselves and adding a process on
            // top piles up the stack (the "forced DataSave from Process.MapResultProcess" bug).
            if (StepAge > 20.0 && (stage == GameStage.MusicSelect || stage == GameStage.Other))
            {
                if (!_uploadForceLogged)
                {
                    _uploadForceLogged = true;
                    Log.Warn("flow: credit 结束但游戏未开始 DataSave (stage=" + stage + ") - 尝试强制一次");
                }
                LoginHooks.RetryForceDataSave();
            }
            StatusText = "等待上传/登出… (" + stage + ")";
            if (StepAge > ModConfig.Data.UploadCooldownSec + 90)
                Fail("上传超时 (stage=" + stage + ")，请检查网络与服务端日志");
        }
        /// <summary>
        /// 本批没有走 DataSave：成绩没有上传。把本批所有槽位的 Done 标记回滚，然后停止任务，
        /// 避免「全部完成（N 条）」把没上传的 credit 算进去（下次开始会重新把缺的成绩排进计划）。
        /// </summary>
        private static void FailCreditNoUpload(string why)
        {
            int rolledBack = 0;
            try
            {
                if (_credit != null)
                {
                    foreach (var slot in _credit)
                    {
                        if (slot == null || slot.Item == null) continue;
                        if (slot.Item.Done) rolledBack++;
                        slot.Item.Done = false;
                    }
                }
            }
            catch (Exception e) { Log.Warn("flow: 回滚 Done 标记失败: " + e.Message); }
            Log.Error("[flow] 第 " + CurrentCredit + "/" + (_batches != null ? _batches.Count : 0) +
                      " 批未上传（" + why + "）→ 已回滚 " + rolledBack + " 条的完成标记，任务停止");
            Fail("本批未上传（没有 DataSave），已停止以避免把未上传的 credit 记为完成");
        }

        private static bool _uploadForceLogged;

        private static double _reloginDeadline = -1;

        private static void TickAfterCredit()
        {
            if (_stopping)
            {
                StartShutdown(false, "已中止", FlowPhase.Finished);
                return;
            }
            if (_batchIndex + 1 >= _batches.Count)
            {
                StartShutdown(false, "全部完成（" + (Plan != null ? Plan.Done : 0) + " 条）", FlowPhase.Finished);
                return;
            }
            if (ModConfig.Data.ContinueRelogin)
            {
                // Same-session continue: press YES on the CONTINUE screen (no logout, no swipe).
                // Advance the credit cursor HERE (the logout path advances it in TickWaitLogout).
                _batchIndex++;
                _batchAdvancedInContinue = true;
                if (_batchIndex >= _batches.Count)
                {
                    StartShutdown(false, "全部完成（" + (Plan != null ? Plan.Done : 0) + " 条）", FlowPhase.Finished);
                    return;
                }
                _continueCreditBumped = false;
                _reloginByContinue = true;
                _continueSettleUntil = -1;
                _snapStableReads = 0;
                _snapLastCount = -1;
                _reloginSeen = false;
                _continueYesLogged = false;
                _continueSuccessLogged = false;
                _musicSelectAt = -1;
                ResetSwipeBudget();
                Log.Info("[flow] 续关模式: credit " + (_batchIndex + 1) + " 完成，等待 CONTINUE 屏按「是」开下一批");
                SetCStep(Step.BatchWaitRelogin, "续关：等待 CONTINUE 屏按「是」…");
                return;
            }
            _reloginByContinue = false;
            LoginHooks.BeginLogout("batch end");
            SetCStep(Step.BatchWaitLogout, "本批结束，登出中…");
        }

        private static void TickWaitLogout()
        {
            string detail;
            if (!LoginHooks.PollLogout(out detail))
            {
                StatusText = "第 " + CurrentCredit + "/" + _batches.Count + " 批：登出中… " + detail;
                return;
            }
            if (LoginHooks.LogoutGaveUp)
            {
                Fail("批次之间登出超时（未杀进程，请人工检查服务端 isLogin）");
                return;
            }
            if (_stopping)
            {
                StartShutdown(false, "已中止", FlowPhase.Finished);
                return;
            }
            if (!_batchAdvancedInContinue) _batchIndex++;
            else _batchAdvancedInContinue = false;
            _sawDataSave = false;
            _musicSelectAt = -1;
            _loginWarned = false;
            _reloginSeen = false;
            _reloginByContinue = false;
            ResetSwipeBudget();
            if (_batchIndex >= _batches.Count)
            {
                StartShutdown(false, "全部完成（" + (Plan != null ? Plan.Done : 0) + " 条）", FlowPhase.Finished);
                return;
            }
            SetCStep(Step.BatchWaitRelogin, "准备第 " + (_batchIndex + 1) + "/" + _batches.Count + " 批…");
        }

        private static void TickWaitRelogin()
        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (_stopping)
            {
                StartShutdown(false, "已中止", FlowPhase.Finished);
                return;
            }
            if (GameStagePump.Stage == GameStage.PhotoEdit)
            {
                TryPressPhotoEditSkip();
                StatusText = "写真页: 等待跳过… (重新登录/续关前)";
                return;
            }

            // ------------------------------------------------------------------ CONTINUE path
            // Preferred between credits when ContinueRelogin=true: the game itself offers the
            // "continue" screen after the credit's DataSave; pressing YES starts the next credit in
            // the SAME session - no logout, no card swipe, no process rebuild (the old
            // logout+swipe path crashed because the previous GameProcess was still in the stack).
            if (_reloginByContinue)
            {
                // The game continues BY ITSELF: after ContinueProcess appears it walks
                // Continue -> PlInformation -> ModeSelect within ~1-2s.  Success is therefore
                // "ContinueProcess gone AND the next-credit screen is up", not "we pressed YES".
                int contMon;
                bool loggedIn = LoginHooks.IsLoggedIn(out contMon);
                if (loggedIn && _monitor != contMon) _monitor = contMon;

                GameStage cs = GameStagePump.Stage;
                bool hasContinue = HasContinueProcess() || cs == GameStage.Continue;

                if (!hasContinue &&
                    (cs == GameStage.ModeSelect || cs == GameStage.MusicSelect
                     || cs == GameStage.TrackStart || cs == GameStage.GamePlay))
                {
                    // 续关按「是」后游戏要做 ~2s 的 credit 初始化（track 计数/脏数据清理 + isContinue 登录）。
                    if (_continueSettleUntil < 0) _continueSettleUntil = now + 3.0;
                    if (now < _continueSettleUntil)
                    {
                        StatusText = "续关初始化中… (" + (_continueSettleUntil - now).ToString("0.0") + "s)";
                        return;
                    }
                    // 真机续关会发 UserLoginApi(isContinue=true) 拿新 loginId；下一存 playlogId 必须与之一致。
                    if (Navigation.ContinueLoginBaseId == 0 || LoginHooks.LastLoginId == Navigation.ContinueLoginBaseId)
                    {
                        if (now - _continueSettleUntil < 10.0)
                        {
                            StatusText = "等待续关登录（新 loginId）…";
                            return;
                        }
                        NavReflect.WarnOnce("continue-login-missing", "[flow] 续关登录未完成（loginId 未变化），仍继续下一批");
                    }
                    else if (!_continueLoginLogged)
                    {
                        _continueLoginLogged = true;
                        Log.Info("[flow] 续关登录完成: 新 loginId=" + LoginHooks.LastLoginId + "（旧 " + Navigation.ContinueLoginBaseId + "）");
                    }
                    if (!_continueSuccessLogged)
                    {
                        _continueSuccessLogged = true;
                        Log.Info("[flow] 续关成功（游戏自动继续）→ 进入下一批 stage=" + cs);
                    }
                    // Clear navigation's logout-mode latch BEFORE driving MODE SELECT: the previous
                    // credit's ForceTimeUp path otherwise makes MODE SELECT ignore OK (~5-7s window).
                    _continueTrackResetPending = true;   // 新 credit：等下把 track 计数重置回 1
                    
                    if (Phase != FlowPhase.Transferring)
                        SetPhase(FlowPhase.Transferring, "续关成功，继续转生…");
                    TryResetNavLogoutMode();
                    if (AtCleanMusicSelect())
                    {
                        if (_musicSelectAt < 0) _musicSelectAt = now;
                        if (now - _musicSelectAt >= StableMusicSelectSec)
                        {
                            if (!WaitForGameProcessExit(60, "旧局 GameProcess 退出等待"))
                            {
                                StatusText = "续关成功，等待旧局 GameProcess 退出…";
                                return;
                            }
                            _reloginByContinue = false;
                            BeginCredit();
                            return;
                        }
                        StatusText = "续关成功，准备选歌…";
                    }
                    else
                    {
                        _musicSelectAt = -1;
                        Navigation.GoToMusicSelect();
                        StatusText = "续关成功，进入选歌页… (" + cs + ")";
                    }
                    return;
                }

                if (hasContinue)
                {
                    if (!_continueYesLogged)
                    {
                        _continueYesLogged = true;
                        Log.Info("[flow] 续关: 等待/按「是」开始 credit " + (_batchIndex + 1));
                    }
                    TryPressContinueYes();
                    StatusText = "续关: 等待游戏进入下一批… (" + cs + ")";
                    if (StepAge > 90.0)
                    {
                        Log.Warn("flow: 续关 90s 仍未进入下一批 - 回退到登出+刷卡");
                        _reloginByContinue = false;
                        ResetSwipeBudget();
                        SetCStep(Step.BatchWaitLogout, "续关超时，改走登出+刷卡");
                        LoginHooks.BeginLogout("continue-timeout");
                    }
                    return;
                }

                if (cs == GameStage.Entry || cs == GameStage.Title || cs == GameStage.Startup)
                {
                    if (StepAge > 8.0)
                    {
                        Log.Warn("flow: 续关不可用（Continue 消失后回到 " + cs + "）- 回退到登出+刷卡");
                        _reloginByContinue = false;
                        _reloginSeen = false;
                        ResetSwipeBudget();
                        SetCStep(Step.BatchWaitLogout, "续关不可用，改走登出+刷卡");
                        LoginHooks.BeginLogout("continue-unavailable");
                    }
                    else
                    {
                        StatusText = "续关: 等待画面… (" + cs + ")";
                    }
                    return;
                }
                if (cs == GameStage.MapResult || cs == GameStage.Result
                    || cs == GameStage.DataSave || cs == GameStage.PhotoEdit)
                {
                    StatusText = "续关: 等待 CONTINUE 屏… (" + cs + ")";
                    return;
                }
                Navigation.GoToMusicSelect();
                StatusText = "续关成功，进入选歌页… (" + cs + ")";
                return;
            }
            // ------------------------------------------------------------------ logout + swipe path
            int monitor;
            if (!LoginHooks.IsLoggedIn(out monitor))
            {
                _reloginSeen = false;
                if (LoginHooks.LoginBlocked)
                {
                    StatusText = "服务端 isLogin 未清 (returnCode=100)：请等待约15分钟或人工登出";
                    return;
                }
                // CRASH GUARD: the previous credit's GameProcess may still be alive; swiping a new
                // card while it runs OnUpdate on a torn-down state throws NullReferenceException.
                if (!WaitForGameProcessExit(60, "旧局 GameProcess 退出等待"))
                {
                    StatusText = "刷卡前等待上一局 GameProcess 退出… (" + GameStagePump.Stage + ")";
                    return;
                }
                string code = string.IsNullOrEmpty(TargetAccessCode) ? ModConfig.Data.AimeCode : TargetAccessCode;
                AutoSwipe(code, "目标");
                StatusText = (string.IsNullOrEmpty(code) ? "请刷入目标账号（等待刷卡）" : "重新登录目标账号（模拟刷卡）") + "…";
                if (_swipeAttempts >= MaxSwipeAttempts)
                    StatusText = "自动刷卡已停（" + MaxSwipeAttempts + " 次），请人工刷卡或按中止  [目标账号]";
                if (_reloginDeadline > 0 && now > _reloginDeadline)
                {
                    _reloginDeadline = -1;
                    Log.Warn("flow: relogin takes long (operator may swipe manually); still waiting");
                }
                if (StepAge > 4.0 * ModConfig.Data.LoginWaitTimeoutSec) Fail("重新登录超时");
                return;
            }
            if (_monitor != monitor) _monitor = monitor;
            ResetSwipeBudget();

            if (!_reloginSeen)
            {
                _reloginSeen = true;
                _sessionLoginAt = now;
                Log.Info("flow: relogin detected for credit " + (_batchIndex + 1) + " (anchor reset)");
            }

            if (AtCleanMusicSelect())
            {
                if (_musicSelectAt < 0) _musicSelectAt = now;
                if (now - _musicSelectAt >= StableMusicSelectSec)
                {
                    if (!WaitForGameProcessExit(60, "旧局 GameProcess 退出等待"))
                    {
                        StatusText = "重新登录成功，等待旧局 GameProcess 退出…";
                        return;
                    }
                    BeginCredit();
                    return;
                }
            }
            else
            {
                _musicSelectAt = -1;
                Navigation.GoToMusicSelect();
            }
            StatusText = "重新登录成功，进入选歌页… (" + GameStagePump.Stage + ")";
            // 登出→Title→AdvDemo→Entry→登录下载 这条链在本机实测 ~2 分钟（22:43:12 → 22:45:16），
            // 原来硬编码 90s 会在登录刚完成那一刻误判失败。改成 >=240s，且跟随 LoginWaitTimeoutSec。
            if (StepAge > Math.Max(240.0, ModConfig.Data.LoginWaitTimeoutSec * 2.0))
                Fail("重新登录后无法进入选歌页 (stage=" + GameStagePump.Stage + ")");
        }

        private static double _liveGameProcessWaitSince = -1;

        /// <summary>
        /// 等上一局 GameProcess 退出，最多 timeoutSec 秒；超时用 `Navigation.ReleaseGameProcesses`
        /// 走游戏自己的 ReleaseProcess 强制回收（绝不杀进程）。返回 true = 可以继续。
        /// </summary>
        private static bool WaitForGameProcessExit(int timeoutSec, string where)
        {
            if (!HasLiveGameProcess())
            {
                _liveGameProcessWaitSince = -1;
                return true;
            }
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (_liveGameProcessWaitSince < 0) _liveGameProcessWaitSince = now;
            if (now - _liveGameProcessWaitSince < timeoutSec) return false;
            Log.Warn("flow: 旧局 GameProcess 滞留超过 " + timeoutSec + "s (" + where + ") → 强制回收");
            int released = Navigation.ReleaseGameProcesses("flow wait timeout: " + where);
            Log.Warn("flow: 强制回收结果 released=" + released);
            _liveGameProcessWaitSince = -1;
            return !HasLiveGameProcess();
        }

        /// <summary>
        /// True when a chart process is still ALIVE (do not swipe a new card then).
        ///
        /// 关键：只把**未 Released** 的进程算活。续关后游戏常把上一局的 GameProcess /
        /// TrackStartProcess 留在栈上但状态已经是 Released（游戏自己的清理没收干净）——
        /// 旧实现用 `NavProcess.Any&lt;T&gt;` 把它们也算活，于是永远「等待旧局退出」卡死（实机 22:31）。
        /// </summary>
        private static bool HasLiveGameProcess()
        {
            try
            {
                var list = ProcessHooks.GetProcessList();
                if (list != null)
                {
                    foreach (var ctrl in list)
                    {
                        if (ctrl == null || ctrl.Process == null) continue;
                        string state;
                        try { state = ctrl.State.ToString(); } catch (Exception) { state = ""; }
                        if (state == "Released") continue;
                        if (ctrl.Process is global::Process.GameProcess) return true;
                        if (ctrl.Process is global::Process.TrackStartProcess) return true;
                        string name = ctrl.Process.GetType().Name;
                        if (name == "PhotoShootProcess" || name == "PleaseWaitUpdater") return true;
                    }
                    return false;
                }
            }
            catch (Exception) { }
            // 退化路径：拿不到进程列表时退回旧的类型扫描（宁可多等也不要误刷卡）。
            try
            {
                return NavProcess.Any<global::Process.GameProcess>()
                    || NavProcess.Any<global::Process.TrackStartProcess>()
                    || NavProcess.HasName("PhotoShootProcess", "PleaseWaitUpdater");
            }
            catch (Exception) { return false; }
        }

        private static bool HasContinueProcess()
        {
            try { return NavProcess.HasName("ContinueProcess"); }
            catch (Exception) { return false; }
        }

        /// <summary>After CONTINUE YES the next credit's screens replace ContinueProcess.</summary>
        private static bool HasReachedNextCreditScreen()
        {
            GameStage s = GameStagePump.Stage;
            return s == GameStage.ModeSelect || s == GameStage.MusicSelect || s == GameStage.TrackStart
                   || s == GameStage.GamePlay;
        }

        private static bool _continueYesLogged;
        private static bool _continueSuccessLogged;

        private static System.Reflection.MethodInfo _navPhotoSkip;
        private static bool _navPhotoSkipLooked;
        private static bool _photoSkipWarned;

        /// <summary>
        /// Ask navigation to skip the photo-edit page (nav exposes PressPhotoEditSkip; reflection
        /// keeps flow buildable before that lands).  Flow itself never injects a process here.
        /// </summary>
        private static void TryPressPhotoEditSkip()
        {
            try
            {
                if (!_navPhotoSkipLooked)
                {
                    _navPhotoSkipLooked = true;
                    _navPhotoSkip = typeof(Navigation).GetMethod("PressPhotoEditSkip",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (_navPhotoSkip != null) Log.Info("flow: Navigation.PressPhotoEditSkip found");
                }
                if (_navPhotoSkip == null) return;
                var ps = _navPhotoSkip.GetParameters();
                object[] args = ps != null && ps.Length > 0 ? new object[ps.Length] : null;
                if (args != null)
                    for (int i = 0; i < args.Length; i++)
                        args[i] = ps[i].ParameterType == typeof(int) ? (object)(_monitor >= 0 ? _monitor : 0) : null;
                _navPhotoSkip.Invoke(null, args);
            }
            catch (Exception e)
            {
                if (!_photoSkipWarned)
                {
                    _photoSkipWarned = true;
                    Log.Warn("flow: PressPhotoEditSkip failed: " + e.Message);
                }
            }
        }

        /// <summary>
        /// Press YES on the continue screen.  Navigation lives in the same assembly and guarantees
        /// the single signature <c>PressContinueYes(int monitor = 0)</c>, so call it directly; fall
        /// back to its ContinueToNextTrack / the game's IsSelectContinue flag when it refuses.
        /// </summary>

        private static bool TryPressContinueYes()
        {
            if (ManualContinueYes || ContinueDiag.InputSilenced)
            {
                if (!_manualContinueLogged)
                {
                    _manualContinueLogged = true;
                    Log.Warn("[flow] 续关: 已关闭自动按键（ManualContinueYes=" + ManualContinueYes +
                             ", InputSilenced=" + ContinueDiag.InputSilenced + "），等待操作员手动按「是」");
                }
                return false;
            }
            try
            {
                // 原版抓包（2026-11-21 01:52:36 手动）：state=2 → 点一下 4 号键 50ms → state=3
                // → 约 3 秒后游戏自己调用 ContinueProcess.ReLogin() 发 UserLoginApi(isContinue=true)。
                // 只点按一次并等待；不要长按/触摸刷屏/合成调用。
                var cont = NavProcess.Find<global::Process.ContinueProcess>();
                int contState = cont != null ? NavReflect.ToInt(NavReflect.Get(cont, "_state")) : -1;
                if (cont == null || contState < 2)
                {
                    _continueTapAt = -100;
                    _continueTapTries = 0;
                    _continueAcceptedLogged = false;
                    return false;
                }
                if (contState >= 4)
                {
                    ButtonHold.Cancel();
                    if (!_continueAcceptedLogged)
                    {
                        _continueAcceptedLogged = true;
                        Log.Info("[flow] 续关: 游戏已接受 YES（state=" + contState + "），等待 ReLogin/下一局…");
                    }
                    return true;
                }
                double now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _continueTapAt >= 4.0)
                {
                    _continueTapAt = now;
                    _continueTapTries++;
                    InputInject.Press(0, Manager.InputManager.ButtonSetting.Button04);
                    Log.Info("[flow] 续关: 点按 4 号键 YES (state=" + contState + ", 第 " + _continueTapTries +
                             " 次) → 等游戏自己 ReLogin");
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("flow: TryPressContinueYes failed: " + e.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------- helpers

        private static double StepAge { get { return UnityEngine.Time.realtimeSinceStartup - _stepAt; } }

        /// <summary>C mode ("Duration") vs B mode ("Judge").  C = wait the real chart length.</summary>
        private static bool IsDurationRealism
        {
            get { return string.Equals(ModConfig.Data.PlayRealism, "Duration", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// Chart length in seconds for C mode.  ChartCatalog's .ma2 stats first (once data-dev has a
        /// length reader), then a per-difficulty default.  MASTER/Re:MASTER 120s, EXPERT 110s, else 100s.
        /// </summary>
        private static double TrackLengthSec(int musicId, int level, out string source)
        {
            source = "default";
            try
            {
                double len = ChartCatalog.GetLengthSeconds(musicId);
                if (len > 0)
                {
                    source = "ChartCatalog";
                    return len;
                }
            }
            catch (Exception) { }
            switch (level)
            {
                case 3:
                case 4:
                    source = "default(MASTER/ReMASTER)";
                    return 120.0;
                case 2:
                    source = "default(EXPERT)";
                    return 110.0;
                default:
                    source = "default(ADVANCED/BASIC)";
                    return 100.0;
            }
        }

        /// <summary>
        /// "Are we in a chart right now" - prefer nav's own process-stack view (it works even when
        /// GameStageTracker is degraded), with the stage as a fallback.
        /// </summary>
        private static bool IsInGameplay()
        {
            try { if (Navigation.InGameplay) return true; }
            catch (Exception) { }
            GameStage s = GameStagePump.Stage;
            return s == GameStage.GamePlay || s == GameStage.TrackStart;
        }

        private static string DescribeSlot()
        {
            if (_slot < 0 || _slot >= _credit.Count) return "";
            Slot s = _credit[_slot];
            string kind = s.Kind == SlotKind.Filler ? "filler" : s.Kind == SlotKind.Mission ? "mission" : "transfer";
            return s.Item.MusicId + "-" + s.Item.Level + " [" + kind + "]";
        }

        private static void SetCStep(Step step, string status)
        {
            if (step != _step)
            {
                Log.Info("[flow] step " + _step + " -> " + step + "  slot=" + (_slot + 1) + "/" +
                         (_credit != null ? _credit.Count : 0) + " credit=" + CurrentCredit + "/" +
                         (_batches != null ? _batches.Count : 0) + " stage=" + GameStagePump.Stage +
                         " nav=" + Navigation.Describe());
            }
            _step = step;
            _stepAt = UnityEngine.Time.realtimeSinceStartup;
            if (!string.IsNullOrEmpty(status)) SetStatus(status);
        }

        private static void SetStep(Step step, string status)
        {
            _step = step;
            _stepAt = UnityEngine.Time.realtimeSinceStartup;
            if (!string.IsNullOrEmpty(status)) SetStatus(status);
        }

        private static void SetStatus(string status)
        {
            if (status == StatusText) return;
            StatusText = status ?? "";
            FireChanged();
        }

        private static void SetSelectContinue(bool want)
        {
            try
            {
                var arr = Manager.GameManager.IsSelectContinue;
                if (arr == null) return;
                if (arr.Length > 0) arr[0] = want;
                if (arr.Length > 1) arr[1] = false;
            }
            catch (Exception e)
            {
                Log.Warn("flow: setting IsSelectContinue failed: " + e.Message);
            }
        }

        private static void TrySyntheticButtonPress(Manager.InputManager.ButtonSetting button)
        {
            // Only used when the continue screen ignores IsSelectContinue.  InputLock must not eat it.
            try
            {
                InputLock.AllowSyntheticInput = true;
                Manager.InputManager.SetButtonDown(0, button);
            }
            catch (Exception e)
            {
                Log.Warn("flow: synthetic button press failed: " + e.Message);
            }
            finally
            {
                InputLock.AllowSyntheticInput = false;
            }
        }

        // ---------------------------------------------------------------- phase / shutdown

        internal static void SetPhase(FlowPhase phase, string status)
        {
            bool changed = phase != Phase || (status != null && status != StatusText);
            Phase = phase;
            _phaseAt = UnityEngine.Time.realtimeSinceStartup;
            if (status != null) StatusText = status;
            if (changed) FireChanged();
        }

        private static void FireChanged()
        {
            var h = Changed;
            if (h == null) return;
            try { h(); }
            catch (Exception e) { Log.Error("flow Changed handler failed", e); }
        }

        /// <summary>Enter a terminal/cleanup state, guaranteeing a logout attempt and lock release.</summary>
        internal static void Fail(string error)
        {
            LastError = error ?? "unknown";
            // One error line per reason: the closing-page watchdog could call this every tick and
            // the previous build spammed the same line 12 times.
            if (!_failLogged || _failReason != LastError)
            {
                _failLogged = true;
                _failReason = LastError;
                Log.Error("flow failed: " + LastError + "  (phase=" + Phase + ", credit=" + CurrentCredit +
                          ", track=" + CurrentTrack + ", stage=" + GameStagePump.Stage + "/" + GameStagePump.Raw +
                          ", nav=" + Navigation.Describe() + ")");
            }
            StartShutdown(true, LastError, FlowPhase.Failed);
        }
        private static bool _failLogged;
        private static string _failReason = "";

        internal static void Finish()
        {
            StartShutdown(false, "完成", FlowPhase.Finished);
        }

        private static void StartShutdown(bool failed, string reason, FlowPhase finalPhase)
        {
            if (_shutdownActive) return;
            _shutdownActive = true;
            _shutdownFailed = failed || _shutdownFailed;
            _shutdownReason = reason ?? "";
            _shutdownFinalPhase = finalPhase;
            _shutdownAt = UnityEngine.Time.realtimeSinceStartup;
            SetPhase(FlowPhase.Stopping, (failed ? "失败: " + LastError + " - " : "") + "正在结束会话…");
            Log.Info("flow: wrap-up begin (failed=" + failed + ", final=" + finalPhase + ", reason=" + reason +
                     ", loggedIn=" + LoginHooks.IsLoggedIn() + ")");

            if (!LoginHooks.IsLoggedIn(out int mon))
            {
                CompleteShutdown();
                return;
            }

            // NOTE: a plain timeout logout (ForceTimeUp) uploads nothing, so it must NOT require a
            // non-empty account.  Only the real DataSave path (inside LoginHooks.ForceDataSave) has
            // the "safe user data" gate.  Source acquire (even with 0 records) must end in
            // SourceReady, not Failed.
            LoginHooks.BeginLogout(reason);
            // Do NOT force a DataSave here: if a chart is still running the shutdown tick first
            // skips it, and PollLogout only forces the save once the stage allows it.
        }

        private static void CompleteShutdown()
        {
            if (!_shutdownActive && Phase != FlowPhase.Stopping) return;
            _shutdownActive = false;
            try
            {
                LoginHooks.RestoreAccessCodeFiles();
            }
            catch (Exception e) { Log.Warn("flow: restoring aime code failed: " + e.Message); }

            MachineLock.Off();

            if (_shutdownFailed)
            {
                SetPhase(FlowPhase.Failed, "失败: " + (string.IsNullOrEmpty(LastError) ? _shutdownReason : LastError));
            }
            else
            {
                FlowPhase target = _shutdownFinalPhase;
                string status;
                switch (target)
                {
                    case FlowPhase.SourceReady:
                        status = "源数据已就绪" + (Source != null ? ": " + Source.DisplayName : "");
                        break;
                    case FlowPhase.Finished:
                        status = string.IsNullOrEmpty(_shutdownReason) ? "完成" : _shutdownReason;
                        break;
                    default:
                        status = _shutdownReason;
                        break;
                }
                SetPhase(target, status);
            }
            _stopping = false;
            _reloginDeadline = -1;
            Log.Info("flow: wrap-up complete -> " + Phase + " (" + StatusText + ")");
        }

        private static void SyncMachineLock()
        {
            try
            {
                bool need = Phase == FlowPhase.SourceLoginPending || Phase == FlowPhase.SourceReading
                            || Phase == FlowPhase.TargetLoginPending || Phase == FlowPhase.TargetReading
                            || Phase == FlowPhase.Planning || Phase == FlowPhase.AwaitingConfirm
                            || Phase == FlowPhase.Transferring || Phase == FlowPhase.Stopping;
                if (need)
                {
                    string reason = (Phase == FlowPhase.SourceLoginPending || Phase == FlowPhase.SourceReading)
                        ? "获取源数据" : "转生";
                    if (!MachineLock.Active) MachineLock.On(reason, StatusText);
                    else MachineLock.Detail_(StatusText);
                }
                else if (MachineLock.Active)
                {
                    MachineLock.Off();
                }
            }
            catch (Exception e)
            {
                if (!_lockSyncWarned)
                {
                    _lockSyncWarned = true;
                    Log.Error("flow: machine lock sync failed", e);
                }
            }
        }
    }
}
