using System;
using System.Collections.Generic;

namespace FakerSwap.Data
{
    public enum Difficulty
    {
        Basic = 0,
        Advanced = 1,
        Expert = 2,     // 红谱
        Master = 3,     // 紫谱
        ReMaster = 4    // 白谱
    }

    /// <summary>One "userMusicDetailList" row, i.e. the best record of one chart.</summary>
    public sealed class ScoreRecord
    {
        public int MusicId;
        public int Level;
        public int PlayCount;
        /// <summary>Achievement in 1/10000 of a percent: 1010000 == 101.0000%.</summary>
        public int Achievement;
        public int ComboStatus;
        public int SyncStatus;
        public int DeluxscoreMax;
        public int ScoreRank;
        public int ExtNum1;
        /// <summary>Optional self-consistent judge distribution (B/C modes). Null = legacy behaviour.</summary>
        public JudgeBreakdown Judge;
        /// <summary>挑战曲限命：本轨必须零 MISS（其余判定自然分布）。</summary>
        /// <summary>挑战曲允许的非 Perfect 判定数（= 生命数-1）。-1 = 不限制。</summary>
        /// <summary>挑战曲限命：本轨必须零 MISS（其余判定自然分布）。</summary>
        public bool NoMiss;
        public int AllowImperfect = -1;

        public double Percent => Achievement / 10000.0;
        public Difficulty Diff => (Difficulty)Level;
        public string Key => MusicId + ":" + Level;

        public ScoreRecord Clone()
        {
            var c = (ScoreRecord)MemberwiseClone();
            if (Judge != null) c.Judge = Judge.Clone();
            return c;
        }

        public override string ToString() => MusicId + "-" + Level + " " + Percent.ToString("0.0000") + "%";
    }

    /// <summary>A complete "source account" snapshot: who it was and every chart record we care about.</summary>
    public sealed class SourceSnapshot
    {
        public string Path = "";
        public string Format = "";       // "astral" | "backup" | "live"
        public string UserName = "";
        public long UserId;
        public int Rating;
        public int HighestRating;
        public DateTime SavedAt = DateTime.Now;
        public List<ScoreRecord> Records = new List<ScoreRecord>();

        public SourceSnapshot Clone()
        {
            var c = (SourceSnapshot)MemberwiseClone();
            c.Records = new List<ScoreRecord>(Records.Count);
            foreach (var r in Records) c.Records.Add(r.Clone());
            return c;
        }

        public string DisplayName
        {
            get
            {
                var name = string.IsNullOrEmpty(UserName) ? ("uid" + UserId) : UserName;
                return name + "  R" + Rating + "  (" + Records.Count + "条)";
            }
        }
    }

    /// <summary>Facts about a chart that are needed to rebuild a self-consistent score.</summary>
    public sealed class ChartStats
    {
        public int MusicId;
        public int Level;
        /// <summary>Weight-1 notes (taps, touch taps and slide heads are all counted here).</summary>
        public int TapCount;
        /// <summary>Weight-2 notes (holds and touch holds).</summary>
        public int HoldCount;
        /// <summary>Weight-3 notes (slide segments).</summary>
        public int SlideCount;
        /// <summary>Kept at 0 by the catalog: touch notes are already folded into Tap/Hold (same weights).</summary>
        public int TouchCount;
        public int BreakCount;
        /// <summary>Diagnostics only: touch taps and touch holds (already included in Tap/Hold).</summary>
        public int TouchTapCount;
        public int TouchHoldCount;
        /// <summary>Total scoring notes (what the game calls maxNotes).</summary>
        public int NoteCount;
        public int TotalNotes => TapCount + HoldCount + SlideCount + TouchCount + BreakCount;
        /// <summary>Sum of every scoring note at Perfect (== the game's "all perfect score", TTM_SCR_SS).</summary>
        public double AllPerfectBaseScore;
        /// <summary>Extra bonus contributed by break notes at Critical (100 per break, TTM_SCR_ALL - TTM_SCR_SS).</summary>
        public double BreakBonusScore;
        public int TheoryDx;
        public bool IsValid => TotalNotes > 0 && AllPerfectBaseScore > 0;
        /// <summary>Sum of note weights (tap 1, hold 2, slide 3, break 5) - base score unit count.</summary>
        public double Units => TapCount + 2.0 * HoldCount + 3.0 * SlideCount + TouchCount + 5.0 * BreakCount;
    }

    /// <summary>One chart that has to be re-uploaded, plus the record we want the server to end up with.</summary>
    public sealed class TransferItem
    {
        public ScoreRecord Desired;
        public int Index;
        public bool Done;
        public string SkipReason;

        public int MusicId => Desired.MusicId;
        public int Level => Desired.Level;

        public override string ToString() => Desired.MusicId + "-" + Desired.Level;
    }
}
