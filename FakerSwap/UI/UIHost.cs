using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using FakerSwap.Core;
using FakerSwap.Data;
using FakerSwap.Flow;
using FakerSwap.UI.NativeWindow;

namespace FakerSwap.UI
{
    /// <summary>
    /// Owns the standalone Win32 control window (separate thread) plus the always-on in-game
    /// banner that says the cabinet is occupied.
    ///
    /// Everything here runs on the Unity main thread: it mirrors the flow / lock state into an
    /// immutable <see cref="WindowSnapshot"/> that the window thread paints, and marshals window
    /// clicks back through <see cref="MainThread"/>.  The window thread never touches Unity.
    /// </summary>
    public static class UIHost
    {
        private const double SnapshotInterval = 0.25;
        private const double SourceRefreshInterval = 15.0;
        private const double AreaRefreshInterval = 12.0;

        private static bool _started;
        private static double _nextSnapshot;
        private static double _nextSourceRefresh;
        private static double _nextAreaRefresh;

        private static readonly List<SourceSnapshot> _sources = new List<SourceSnapshot>();
        private static int _sourceVersion;
        private static bool _sourcesLoaded;
        private static string _sourceError = "";
        private static int _lastLoggedCount = -1;

        private static readonly List<AreaInfoDto> _areas = new List<AreaInfoDto>();
        private static int _areaVersion;
        private static string _areaError = "";
        private static int _lastLoggedAreaCount = -1;
        private static int _lastAreaRefreshId = -2;

        private static double _transferStartedAt = -1;
        private static double _lastElapsed;
        private static FlowPhase _lastPhase = FlowPhase.Idle;
        private static bool _windowFailureLogged;
        private static int _lastCrawlAreaId = -1;
        private static int _crawlDoneRound;

        // In-game banner (IMGUI, main thread only).
        private static GUIStyle _bannerStyle;
        private static GUIStyle _hintStyle;
        private static Texture2D _bannerBg;
        private static Texture2D _hintBg;

        // ================================================================ lifecycle

        public static void Initialize()
        {
            if (_started) return;
            _started = true;
            _windowFailureLogged = false;
            _lastPhase = RebirthFlow.Phase;
            _transferStartedAt = -1;
            _lastElapsed = 0;
            _sources.Clear();
            _sourcesLoaded = false;
            _sourceVersion++;
            _sourceError = "";
            _lastLoggedCount = -1;
            _nextSnapshot = 0;
            _nextSourceRefresh = 0;
            _areas.Clear();
            _areaVersion++;
            _areaError = "";
            _lastLoggedAreaCount = -1;
            _lastAreaRefreshId = -2;
            _nextAreaRefresh = 0;
            _lastCrawlAreaId = -1;
            _crawlDoneRound = 0;

            try
            {
                var cfg = ModConfig.Data;
                Publish(Now());
                NativeModWindow.Start(cfg.WindowTopMost, true, cfg.WindowX, cfg.WindowY, cfg.WindowW, cfg.WindowH);
                Log.Info("UIHost: standalone window requested (TopMost=" + cfg.WindowTopMost + ")");
            }
            catch (Exception e)
            {
                Log.Error("UIHost: window start failed", e);
            }

            WindowRequests.RequestSourcesRefresh();
            WindowRequests.RequestAreasRefresh();
        }

        public static void Tick()
        {
            if (!_started) return;
            double now = Now();

            try
            {
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    NativeModWindow.ToggleVisible();
                    Log.Info("UIHost: F8 -> window " + (NativeModWindow.IsVisible ? "shown" : "hidden"));
                }
            }
            catch (Exception) { }

            if (NativeModWindow.Failed && !_windowFailureLogged)
            {
                _windowFailureLogged = true;
                Log.Warn("UIHost: standalone window failed - using the in-game fallback panel (F8 still tries to show it)");
            }

            bool force = WindowRequests.ConsumeSourcesRefresh();
            bool auto = now >= _nextSourceRefresh && SourcesMayChange();
            if (force || !_sourcesLoaded || auto)
            {
                LoadSources();
                _nextSourceRefresh = now + SourceRefreshInterval;
            }

            bool forceAreas = WindowRequests.ConsumeAreasRefresh();
            int currentAreaId = -1;
            try { currentAreaId = AreaBridge.SelectedAreaId; }
            catch (Exception) { }
            bool areaChanged = currentAreaId >= 0 && currentAreaId != _lastAreaRefreshId;
            bool areaDue = now >= _nextAreaRefresh &&
                           (areaChanged || _areas.Count == 0 || RebirthFlow.IsBusy);
            if (forceAreas || areaChanged || areaDue)
            {
                LoadAreas();
                _lastAreaRefreshId = currentAreaId;
                _nextAreaRefresh = now + AreaRefreshInterval;
            }

            if (_nextSnapshot <= 0 || now >= _nextSnapshot)
            {
                _nextSnapshot = now + SnapshotInterval;
                Publish(now);
            }
        }

        public static void Shutdown()
        {
            if (!_started) return;
            _started = false;
            try
            {
                NativeModWindow.Shutdown();
            }
            catch (Exception e)
            {
                Log.Error("UIHost: window shutdown failed", e);
            }
            WindowState.Current = null;
            _sources.Clear();
            _sourcesLoaded = false;
            _sourceVersion++;
            _sourceError = "";
            _lastLoggedCount = -1;
            _lastPhase = FlowPhase.Idle;
            _transferStartedAt = -1;
            _lastElapsed = 0;
            _nextSnapshot = 0;
            _nextSourceRefresh = 0;
            _windowFailureLogged = false;
            _bannerStyle = null;
            _hintStyle = null;
            _bannerBg = null;
            _hintBg = null;
            Log.Info("UIHost shutdown");
        }

        // ================================================================ state publishing

        private static void Publish(double now)
        {
            var phase = RebirthFlow.Phase;
            bool running = phase == FlowPhase.Transferring || phase == FlowPhase.Stopping;
            bool wasRunning = _lastPhase == FlowPhase.Transferring || _lastPhase == FlowPhase.Stopping;

            if (phase == FlowPhase.Transferring && _lastPhase != FlowPhase.Transferring)
            {
                _transferStartedAt = now;
                _lastElapsed = 0;
            }
            else if (!running && wasRunning && _transferStartedAt >= 0)
            {
                _lastElapsed = Math.Max(0, now - _transferStartedAt);
                _transferStartedAt = -1;
            }
            _lastPhase = phase;

            bool busy = RebirthFlow.IsBusy;
            int areaId = -1;
            try { areaId = AreaBridge.SelectedAreaId; }
            catch (Exception) { }
            if (busy)
            {
                if (areaId >= 0 && areaId != _lastCrawlAreaId)
                {
                    _lastCrawlAreaId = areaId;
                    _crawlDoneRound++;
                }
            }
            else if (_crawlDoneRound != 0 || _lastCrawlAreaId != -1)
            {
                _crawlDoneRound = 0;
                _lastCrawlAreaId = -1;
            }

            try
            {
                WindowState.Current = BuildSnapshot(now);
            }
            catch (Exception e)
            {
                Log.Error("UIHost: snapshot build failed", e);
            }
        }

        private static WindowSnapshot BuildSnapshot(double now)
        {
            var cfg = ModConfig.Data;
            var snap = new WindowSnapshot();

            var phase = RebirthFlow.Phase;
            snap.Phase = (int)phase;
            snap.PhaseName = PhaseName(phase);
            snap.Status = RebirthFlow.StatusText ?? "";
            snap.LastError = RebirthFlow.LastError ?? "";
            snap.Busy = RebirthFlow.IsBusy;
            snap.GameStage = StageText();

            snap.LockActive = MachineLock.Active;
            snap.LockNotice = MachineLock.NoticeText;
            if (snap.LockActive)
            {
                double since = MachineLock.Since;
                snap.LockSeconds = since > 0 ? Math.Max(0, now - since) : 0;
            }

            snap.Sources = BuildSourceEntries();
            snap.SourceVersion = _sourceVersion;
            snap.SourcesError = _sourceError ?? "";

            var source = RebirthFlow.Source;
            if (source != null)
            {
                snap.SourcePath = source.Path ?? "";
                snap.SourceFileName = FileNameOf(snap.SourcePath);
                snap.SourceDisplay = source.DisplayName;
                snap.SourceFormat = source.Format ?? "";
                snap.SourceRecords = source.Records != null ? source.Records.Count : 0;
                snap.SourceRating = source.Rating;
                try { snap.SourceSavedAt = source.SavedAt.ToString("yyyy-MM-dd HH:mm:ss"); }
                catch (Exception) { snap.SourceSavedAt = source.SavedAt.ToString(); }
            }

            snap.BatchSize = Math.Max(1, cfg.BatchSize);
            snap.MinLevel = cfg.MinLevel;
            snap.MaxLevel = cfg.MaxLevel;
            snap.WriteMode = cfg.WriteMode ?? "";
            snap.PlayMode = cfg.PlayMode ?? "";
            snap.TopMost = cfg.WindowTopMost;
            snap.ShowBanner = cfg.ShowOverlayBanner;
            snap.LockInput = cfg.LockInput;
            snap.SafeNoWrite = cfg.SafeNoWrite;

            // ---- area crawl panel --------------------------------------------------------
            snap.Areas = BuildAreaEntries();
            snap.AreaVersion = _areaVersion;
            snap.AreaError = _areaError ?? "";
            snap.AreaApiAvailable = AreaBridge.NavigationAvailable;
            snap.CrawlConfigAvailable = AreaBridge.ConfigAvailable;
            var selectedAreas = new List<int>();
            bool crawlFallback = false;
            AreaBridge.ReadSelection(selectedAreas, out crawlFallback);
            snap.SelectedAreaIds = selectedAreas.ToArray();
            snap.CrawlFallback = crawlFallback;
            snap.TicketUseNext = cfg.TicketUseNext;
            snap.TicketUseKeep = cfg.TicketUseKeep;
            snap.TicketLastManualId = cfg.TicketLastManualId;
            try
            {
                int currentAreaId = AreaBridge.SelectedAreaId;
                string currentAreaName = AreaBridge.SelectedAreaName;
                snap.CrawlCurrentArea = currentAreaId < 0
                    ? ""
                    : (string.IsNullOrEmpty(currentAreaName) ? "#" + currentAreaId : currentAreaName);
                snap.AtAreaSelect = Navigation.AtAreaSelect;
            }
            catch (Exception)
            {
                snap.CrawlCurrentArea = "";
                snap.AtAreaSelect = false;
            }
            snap.CrawlDoneThisRound = _crawlDoneRound;
            snap.CrawlSelectedCount = selectedAreas.Count;
            int completedAreas = 0;
            int progressDone = 0;
            int progressTotal = 0;
            foreach (var area in _areas)
            {
                if (area == null || !selectedAreas.Contains(area.Id)) continue;
                if (area.IsComplete) completedAreas++;
                if (area.Progress > 0) progressDone += area.Progress;
                if (area.Total > 0) progressTotal += area.Total;
            }
            snap.CrawlCompletedCount = completedAreas;
            snap.CrawlProgressDone = progressDone;
            snap.CrawlProgressTotal = progressTotal;
            snap.FillerMusicId = cfg.FillerMusicId;
            snap.FillerLevel = cfg.FillerLevel;
            snap.UploadCooldownSec = cfg.UploadCooldownSec;
            snap.BaseOverheadSec = cfg.BaseOverheadPerCreditSec;
            snap.PlayRealism = cfg.PlayRealism ?? "Duration";
            snap.LongSongMode = cfg.LongSongMode ?? "PlayWithOne";
            snap.ConfirmBeforeStart = cfg.ConfirmBeforeStart;
            snap.ContinueRelogin = cfg.ContinueRelogin;
            snap.AllowConfirmNewUser = cfg.AllowConfirmNewUser;
            snap.ForceOverwriteExisting = cfg.ForceOverwriteExisting;
            snap.MapResultGuard = cfg.MapResultGuard;
            snap.AreaSelectTimeoutSec = cfg.AreaSelectTimeoutSec;
            snap.VersionText = BuildInfo.Version;
            snap.WindowVisible = NativeModWindow.IsVisible;

            ReadPlan(snap, cfg);
            ReadQueue(snap);

            double elapsed = CurrentElapsed(now);
            snap.ElapsedSeconds = elapsed;
            snap.ElapsedText = elapsed > 0 ? FormatDuration(elapsed) : "—";

            if (snap.PlanTotal > 0)
            {
                double done = snap.PlanDone;
                if (phase == FlowPhase.Transferring && snap.CurrentTrack >= 1 && snap.CurrentTrack <= 3 &&
                    snap.PlanDone < snap.PlanTotal)
                    done += (snap.CurrentTrack - 1) / 3.0;
                snap.Progress = Clamp(done / snap.PlanTotal);
            }
            else if (phase == FlowPhase.Finished)
            {
                snap.Progress = 1.0;
            }

            if (snap.EstimateSeconds > 0)
            {
                double remaining = snap.EstimateSeconds * (1.0 - snap.Progress);
                if (remaining < 0) remaining = 0;
                snap.RemainingSeconds = remaining;
                snap.RemainingText = elapsed > snap.EstimateSeconds + 30 && snap.Progress < 1.0
                    ? "已超过预估"
                    : FormatDuration(remaining);
            }
            else
            {
                snap.RemainingText = "—";
            }

            return snap;
        }

        private static void ReadPlan(WindowSnapshot snap, ConfigData cfg)
        {
            var plan = RebirthFlow.Plan;
            if (plan == null) return;
            try
            {
                snap.HasPlan = true;
                snap.PlanTotal = plan.Total;
                snap.PlanDone = plan.Done;
                snap.PlanCredits = plan.Credits > 0
                    ? plan.Credits
                    : (plan.Total + snap.BatchSize - 1) / Math.Max(1, snap.BatchSize);
                snap.EstimateSeconds = plan.Estimate.TotalSeconds;
                snap.EstimateText = string.IsNullOrEmpty(plan.EstimateText)
                    ? TimeEstimator.Describe(plan.Estimate)
                    : plan.EstimateText;
            }
            catch (Exception e)
            {
                Log.Warn("UIHost: reading plan failed: " + e.Message);
            }

            snap.CurrentCredit = RebirthFlow.CurrentCredit;
            snap.CurrentTrack = RebirthFlow.CurrentTrack;

            try
            {
                // Prefer the flow's current slot (it may be a mission / filler chart), then fall
                // back to the next not-yet-done plan item.
                TransferItem item = RebirthFlow.CurrentItem;
                if (item == null)
                {
                    int index = snap.PlanDone;
                    if (index >= 0 && index < plan.Items.Count) item = plan.Items[index];
                }
                if (item != null && item.Desired != null)
                {
                    int musicId = item.Desired.MusicId;
                    int level = item.Desired.Level;
                    string title = null;
                    try { title = ChartCatalog.GetTitle(musicId); }
                    catch (Exception) { }
                    if (string.IsNullOrEmpty(title)) title = "#" + musicId;
                    snap.CurrentSong = "『" + title + "』 " + LevelName(level) + "   " +
                                      (item.Desired.Achievement / 10000.0).ToString("0.0000", CultureInfo.InvariantCulture) + "%";
                    snap.CurrentDetail = "musicId " + musicId + " · level " + level;
                }
            }
            catch (Exception e)
            {
                Log.Warn("UIHost: reading current item failed: " + e.Message);
            }
        }

        /// <summary>
        /// Copies the flow's queue view into the immutable window snapshot (the 待办曲目 playlist).
        /// Runs on the Unity main thread only: <see cref="RebirthFlow.GetQueueSnapshot"/> reads the
        /// flow's mutable batch lists, so the window thread must never see them - it only ever gets
        /// the plain <see cref="QueueBatch"/> / <see cref="QueueEntry"/> copies built here.
        /// </summary>
        private static void ReadQueue(WindowSnapshot snap)
        {
            try
            {
                var queue = RebirthFlow.GetQueueSnapshot();
                if (queue == null) return;

                snap.QueueRunning = queue.Running;
                snap.QueueCurrentIndex = queue.CurrentIndex;
                snap.QueueCurrentSlot = queue.CurrentSlot;
                snap.QueueTotalSlots = queue.TotalSlots;
                snap.QueueDoneSlots = queue.DoneSlots;
                snap.QueueMissionSlots = queue.MissionSlots;
                snap.QueueMissionBatches = queue.MissionBatches;

                var batches = queue.Batches;
                if (batches == null) return;
                var copy = new QueueBatch[batches.Length];
                for (int b = 0; b < batches.Length; b++)
                {
                    var source = batches[b];
                    if (source == null) continue;

                    var batch = new QueueBatch();
                    batch.Index = source.Index;
                    batch.IsMission = source.IsMission;
                    batch.IsCurrent = source.IsCurrent;
                    batch.IsDone = source.IsDone;
                    batch.CurrentSlot = source.CurrentSlot;
                    batch.DoneCount = source.DoneCount;

                    var slots = source.Slots;
                    if (slots == null)
                    {
                        batch.Slots = new QueueEntry[0];
                    }
                    else
                    {
                        batch.Slots = new QueueEntry[slots.Length];
                        for (int i = 0; i < slots.Length; i++)
                        {
                            var slot = slots[i];
                            if (slot == null) continue;
                            var entry = new QueueEntry();
                            entry.Index = slot.Index;
                            entry.MusicId = slot.MusicId;
                            entry.Level = slot.Level;
                            entry.Kind = slot.Kind;
                            entry.Name = slot.Name ?? "";
                            entry.IsMission = slot.IsMission;
                            entry.IsDone = slot.IsDone;
                            batch.Slots[i] = entry;
                        }
                    }
                    copy[b] = batch;
                }
                snap.Queue = copy;
            }
            catch (Exception e)
            {
                Log.Warn("UIHost: reading queue snapshot failed: " + e.Message);
            }
        }

        private static SourceEntry[] BuildSourceEntries()
        {
            var arr = new SourceEntry[_sources.Count];
            for (int i = 0; i < _sources.Count; i++)
            {
                var source = _sources[i];
                if (source == null) continue;
                var entry = new SourceEntry();
                entry.Path = source.Path ?? "";
                entry.Name = string.IsNullOrEmpty(source.UserName) ? "(未命名)" : source.UserName;
                entry.FileName = FileNameOf(entry.Path);
                entry.Format = source.Format ?? "";
                entry.Rating = source.Rating;
                entry.Count = source.Records != null ? source.Records.Count : 0;
                try { entry.SavedAt = source.SavedAt.ToString("MM-dd HH:mm"); }
                catch (Exception) { entry.SavedAt = ""; }
                arr[i] = entry;
            }
            return arr;
        }

        private static void LoadSources()
        {
            try
            {
                var list = BackupStore.List();
                _sources.Clear();
                _sources.AddRange(list);
                _sourcesLoaded = true;
                _sourceError = "";
                _sourceVersion++;
                if (_lastLoggedCount != _sources.Count)
                {
                    _lastLoggedCount = _sources.Count;
                    Log.Info("UIHost: source list refreshed, " + _sources.Count + " file(s)");
                }
            }
            catch (Exception e)
            {
                _sourceError = e.Message;
                Log.Warn("UIHost: source list failed: " + e.Message);
            }
        }

        private static AreaEntry[] BuildAreaEntries()
        {
            var entries = new AreaEntry[_areas.Count];
            for (int i = 0; i < _areas.Count; i++)
            {
                var source = _areas[i];
                if (source == null) continue;
                var entry = new AreaEntry();
                entry.Index = source.Index;
                entry.Id = source.Id;
                entry.Name = source.Name;
                entry.IsClear = source.IsClear;
                entry.IsComplete = source.IsComplete;
                entry.IsLock = source.IsLock;
                entry.IsEvent = source.IsEvent;
                entry.IsInfinity = source.IsInfinity;
                entry.IsCurrent = source.IsCurrent;
                entry.Distance = source.Distance;
                entry.Progress = source.Progress;
                entry.Total = source.Total;
                entries[i] = entry;
            }
            return entries;
        }

        private static void LoadAreas()
        {
            try
            {
                var list = AreaBridge.ReadAreas();
                if (list.Count == 0 && _areas.Count > 0)
                {
                    // Outside the area-select screen ListAreas returns nothing: keep the last
                    // known list so the operator can still tick areas before a run.
                    _areaError = AreaBridge.LastError;
                    _areaVersion++;
                    return;
                }
                _areas.Clear();
                _areas.AddRange(list);
                _areaError = AreaBridge.LastError ?? "";
                _areaVersion++;
                if (_lastLoggedAreaCount != _areas.Count)
                {
                    _lastLoggedAreaCount = _areas.Count;
                    Log.Info("UIHost: area list refreshed, " + _areas.Count + " area(s)");
                }
            }
            catch (Exception e)
            {
                _areaError = e.Message;
                Log.Warn("UIHost: area list failed: " + e.Message);
            }
        }

        private static bool SourcesMayChange()
        {
            if (!_sourcesLoaded) return true;
            var phase = RebirthFlow.Phase;
            return phase == FlowPhase.Idle || phase == FlowPhase.NeedSource ||
                   phase == FlowPhase.SourceReady || phase == FlowPhase.Finished ||
                   phase == FlowPhase.Failed || phase == FlowPhase.SourceLoginPending ||
                   phase == FlowPhase.SourceReading;
        }

        // ================================================================ in-game banner

        public static void OnGUI()
        {
            try
            {
                if (MachineLock.Active && ModConfig.Data.ShowOverlayBanner) DrawBanner();
                else if (!NativeModWindow.IsVisible && ModConfig.Data.ShowOverlayBanner) DrawWindowHint();

                if (NativeModWindow.Failed) GameFallbackUi.Draw();
            }
            catch (Exception e)
            {
                Log.Error("UIHost: OnGUI failed", e);
            }
        }

        private static void DrawBanner()
        {
            EnsureBannerStyles();
            float w = Mathf.Min(Screen.width * 0.92f, 1200f);
            var rect = new Rect((Screen.width - w) / 2f, 8f, w, 42f);
            GUI.DrawTexture(rect, _bannerBg);
            GUI.Label(rect, MachineLock.NoticeText, _bannerStyle);
        }

        private static void DrawWindowHint()
        {
            EnsureBannerStyles();
            var rect = new Rect(10f, Screen.height - 34f, 330f, 24f);
            GUI.DrawTexture(rect, _hintBg);
            GUI.Label(new Rect(rect.x + 8f, rect.y, rect.width - 16f, rect.height),
                "FakerSwap：F8 打开控制窗口", _hintStyle);
        }

        private static void EnsureBannerStyles()
        {
            if (_bannerBg == null)
            {
                _bannerBg = new Texture2D(1, 1);
                _bannerBg.SetPixel(0, 0, new Color(0.55f, 0.06f, 0.06f, 0.86f));
                _bannerBg.Apply();
            }
            if (_hintBg == null)
            {
                _hintBg = new Texture2D(1, 1);
                _hintBg.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.09f, 0.78f));
                _hintBg.Apply();
            }
            if (_bannerStyle == null)
            {
                _bannerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 18,
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
                _bannerStyle.normal.textColor = Color.white;
            }
            if (_hintStyle == null)
            {
                _hintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    alignment = TextAnchor.MiddleLeft
                };
                _hintStyle.normal.textColor = new Color(0.85f, 0.86f, 0.88f, 1f);
            }
        }

        // ================================================================ helpers

        private static double Now()
        {
            try { return Time.realtimeSinceStartup; }
            catch (Exception) { return Environment.TickCount / 1000.0; }
        }

        private static double CurrentElapsed(double now)
        {
            if (_transferStartedAt >= 0) return Math.Max(0, now - _transferStartedAt);
            return Math.Max(0, _lastElapsed);
        }

        private static double Clamp(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private static string StageText()
        {
            try
            {
                var stage = GameStageTracker.Current;
                string raw = GameStageTracker.RawProcess;
                return string.IsNullOrEmpty(raw) ? stage.ToString() : stage + " / " + raw;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string PhaseName(FlowPhase phase)
        {
            switch (phase)
            {
                case FlowPhase.Idle: return "空闲";
                case FlowPhase.NeedSource: return "选择源数据";
                case FlowPhase.SourceLoginPending: return "等待源账号刷卡";
                case FlowPhase.SourceReading: return "读取源数据";
                case FlowPhase.SourceReady: return "源数据就绪";
                case FlowPhase.TargetLoginPending: return "等待目标账号刷卡";
                case FlowPhase.TargetReading: return "读取目标账号";
                case FlowPhase.Planning: return "生成计划";
                case FlowPhase.AwaitingConfirm: return "等待二次确认";
                case FlowPhase.Transferring: return "转生中";
                case FlowPhase.Stopping: return "正在中止";
                case FlowPhase.Finished: return "已完成";
                case FlowPhase.Failed: return "失败";
                default: return phase.ToString();
            }
        }

        private static string LevelName(int level)
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

        private static string FormatDuration(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int total = (int)Math.Round(seconds);
            if (total < 60) return total + " 秒";
            if (total < 3600) return (total / 60) + " 分 " + (total % 60) + " 秒";
            return (total / 3600) + " 小时 " + (total % 3600 / 60) + " 分";
        }

        private static string FileNameOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return System.IO.Path.GetFileName(path); }
            catch (Exception) { return path; }
        }
    }
}
