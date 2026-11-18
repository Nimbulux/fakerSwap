using System;

namespace FakerSwap.UI.NativeWindow
{
    /// <summary>One backup / source file as shown in the source page.</summary>
    internal sealed class SourceEntry
    {
        public string Path = "";
        public string Name = "";
        public string FileName = "";
        public string Format = "";
        public int Rating;
        public int Count;
        public string SavedAt = "";

        public string Title
        {
            get
            {
                string name = string.IsNullOrEmpty(Name) ? "(未命名)" : Name;
                return name + "   R" + Rating;
            }
        }
    }

    /// <summary>One map area row for the crawl panel.</summary>
    internal sealed class AreaEntry
    {
        public int Index;
        public int Id = -1;
        public string Name = "";
        public bool IsClear;
        public bool IsComplete;
        public bool IsLock;
        public bool IsEvent;
        public bool IsInfinity;
        public bool IsCurrent;
        public uint Distance;
        public int Progress;
        public int Total;

        /// <summary>Right-hand progress text: "3 / 5", "进度 12345" or "—".</summary>
        public string ProgressText
        {
            get
            {
                if (Total > 0) return Progress + " / " + Total;
                if (Distance > 0) return "进度 " + Distance;
                return IsComplete ? "已完成" : "—";
            }
        }

        /// <summary>Short state tag(s) shown after the name.</summary>
        public string StateText
        {
            get
            {
                if (IsLock) return "锁定";
                if (IsInfinity) return "无限";
                if (IsEvent) return "活动";
                if (IsComplete) return "已完成";
                if (IsClear) return "已通关";
                if (IsCurrent) return "当前";
                return "";
            }
        }
    }

    /// <summary>One planned track inside a playlist batch (the 待办曲目 panel).</summary>
    internal sealed class QueueEntry
    {
        public int Index;
        public int MusicId;
        public int Level;
        public int Kind;        // 0 = transfer, 1 = filler, 2 = mission (課題曲)
        /// <summary>Song title from the live DataManager (may be empty).</summary>
        public string Name = "";
        public bool IsMission;
        public bool IsDone;

        /// <summary>
        /// "11813-3" for a planned transfer / filler chart, "★11976-2" for a 課題曲.
        /// Prefers the real song title (e.g. "★QuQi") so the operator can tell songs apart.
        /// </summary>
        public string Label
        {
            get
            {
                string head = IsMission ? "★" : "";
                if (!string.IsNullOrEmpty(Name)) return head + Name;
                return head + MusicId + "-" + Level;
            }
        }
    }

    /// <summary>One credit (batch) of the playlist as shown in the 待办曲目 panel.</summary>
    internal sealed class QueueBatch
    {
        public int Index;
        public QueueEntry[] Slots = EmptySlots;
        public int Count { get { return Slots != null ? Slots.Length : 0; } }
        public bool IsMission;
        public bool IsCurrent;
        public bool IsDone;
        public int CurrentSlot = -1;
        public int DoneCount;

        private static readonly QueueEntry[] EmptySlots = new QueueEntry[0];
    }

    /// <summary>
    /// Immutable game-thread → window-thread state.  The game thread builds a fresh instance and
    /// publishes it through <see cref="WindowState.Current"/>; the window thread only ever reads
    /// this object, so it can never touch Unity / game objects.
    /// </summary>
    internal sealed class WindowSnapshot
    {
        // ---- flow phase -----------------------------------------------------------------
        public int Phase;
        public string PhaseName = "";
        public string Status = "";
        public string LastError = "";
        public bool Busy;
        public string GameStage = "";

        // ---- cabinet lock ---------------------------------------------------------------
        public bool LockActive;
        public string LockNotice = "";
        public double LockSeconds;

        // ---- source selection -----------------------------------------------------------
        public SourceEntry[] Sources = EmptySources;
        public int SourceVersion;
        public string SourcesError = "";
        public string SourcePath = "";
        public string SourceFileName = "";
        public string SourceDisplay = "";
        public string SourceFormat = "";
        public int SourceRecords;
        public int SourceRating;
        public string SourceSavedAt = "";

        // ---- plan / progress ------------------------------------------------------------
        public bool HasPlan;
        public int PlanTotal;
        public int PlanDone;
        public int PlanCredits;
        public int BatchSize = 3;
        public string EstimateText = "";
        public double EstimateSeconds;
        public int CurrentCredit;
        public int CurrentTrack;
        public string CurrentSong = "";
        public string CurrentDetail = "";
        public double Progress;
        public double ElapsedSeconds;
        public double RemainingSeconds;
        public string ElapsedText = "";
        public string RemainingText = "";

        // ---- settings shown in the UI ---------------------------------------------------
        public int MinLevel = 2;
        public int MaxLevel = 4;
        public string WriteMode = "";
        public string LongSongMode = "PlayWithOne";
        public string PlayMode = "";
        public bool TopMost = true;
        public bool ShowBanner = true;
        public bool LockInput = true;
        public bool SafeNoWrite;
        public int FillerMusicId;
        public int FillerLevel;
        public double UploadCooldownSec;
        public double BaseOverheadSec;

        // ---- editable settings (Settings section) -----------------------------------------
        public string PlayRealism = "Duration";
        public bool ConfirmBeforeStart = true;
        public bool ContinueRelogin;
        public bool AllowConfirmNewUser;
        public bool ForceOverwriteExisting;
        public bool MapResultGuard;
        public double AreaSelectTimeoutSec = 3.0;

        // ---- build info / capabilities ---------------------------------------------------
        public string VersionText = "";
        public bool WindowVisible = true;

        // ---- area crawl panel -------------------------------------------------------------
        public AreaEntry[] Areas = EmptyAreas;
        public int AreaVersion;
        public string AreaError = "";
        public bool AreaApiAvailable;
        public bool CrawlConfigAvailable;
        public int[] SelectedAreaIds = EmptyInts;
        public bool CrawlFallback;
        // ---- ticket select（选票页）----
        public bool TicketUseNext;
        public bool TicketUseKeep;
        public int TicketLastManualId;
        public string CrawlCurrentArea = "";
        public bool AtAreaSelect;
        public int CrawlDoneThisRound;
        public int CrawlSelectedCount;
        public int CrawlCompletedCount;
        public int CrawlProgressDone;
        public int CrawlProgressTotal;

        // ---- pending playlist (待办曲目 panel, copied from RebirthFlow.GetQueueSnapshot) -------
        public QueueBatch[] Queue = EmptyQueue;
        public int QueueCurrentIndex = -1;
        public int QueueCurrentSlot = -1;
        public int QueueTotalSlots;
        public int QueueDoneSlots;
        public int QueueMissionSlots;
        public int QueueMissionBatches;
        public bool QueueRunning;

        private static readonly SourceEntry[] EmptySources = new SourceEntry[0];
        private static readonly AreaEntry[] EmptyAreas = new AreaEntry[0];
        private static readonly int[] EmptyInts = new int[0];
        private static readonly QueueBatch[] EmptyQueue = new QueueBatch[0];
    }

    /// <summary>Latest published snapshot.  Reference assignment is atomic; only the game thread writes.</summary>
    internal static class WindowState
    {
        private static volatile WindowSnapshot _current;

        public static WindowSnapshot Current
        {
            get { return _current; }
            set { _current = value; }
        }
    }
}
