using System;
using System.Collections.Generic;
using FakerSwap.Core;

namespace FakerSwap.Data
{
    /// <summary>
    /// Turns a desired achievement into a self-consistent set of counters (rank / DX score /
    /// combo / sync) so the uploaded record does not look syntactically impossible.
    ///
    /// maimai DX scoring (verified against the chart data shipped with SDEZ 1.65):
    ///   base score (100%) : tap/touch-tap/slide-head = 1, hold = 2, slide = 3, break = 5 units;
    ///                       perfect and critical both give full value; great = 0.8, good = 0.5,
    ///                       miss = 0 (break great has 0.8/0.6/0.5 windows and break good = 0.4).
    ///   break bonus (1%)  : per break 1.0 (critical) / 0.75 (high perfect) / 0.5 (low perfect) /
    ///                       0.4 (great) / 0.3 (good) / 0 (miss), averaged over the break count.
    ///   total achievement = base% + break%  ->  max 101% (1010000).
    ///   DX score          = 3*critical + 2*perfect + 1*great (theory = 3 * total notes).
    ///   rank thresholds are taken from the game's own MusicClearRank.xml table.
    /// </summary>
    public static class ScoreMath
    {
        // Manager.MusicClearrankID (the game's own enum order).
        public const int RankD = 0, RankC = 1, RankB = 2, RankBB = 3, RankBBB = 4,
                         RankA = 5, RankAA = 6, RankAAA = 7, RankS = 8, RankSP = 9,
                         RankSS = 10, RankSSP = 11, RankSSS = 12, RankSSSp = 13;
        public const int RankCount = 14;

        /// <summary>Maximum possible achievement (101.0000%).</summary>
        public const int MaxAchievement = 1010000;

        /// <summary>When true the source values are overwritten with the ones derived from the achievement.</summary>
        public static bool RecalcFromAchievement;

        // Thresholds straight out of StreamingAssets/&lt;pack&gt;/musicClearRank/MusicClearRank*.xml
        private static readonly int[] RankThresholds =
        {
            0,        // Rank_D
            500000,   // Rank_C     50%
            600000,   // Rank_B     60%
            700000,   // Rank_BB    70%
            750000,   // Rank_BBB   75%
            800000,   // Rank_A     80%
            900000,   // Rank_AA    90%
            940000,   // Rank_AAA   94%
            970000,   // Rank_S     97%
            980000,   // Rank_S+    98%
            990000,   // Rank_SS    99%
            995000,   // Rank_SS+   99.5%
            1000000,  // Rank_SSS   100%
            1005000,  // Rank_SSS+  100.5%
        };

        public static int RankOf(int achievement)
        {
            for (int i = RankThresholds.Length - 1; i >= 0; i--)
                if (achievement >= RankThresholds[i]) return i;
            return RankD;
        }

        public static int ThresholdOf(int rank)
        {
            if (rank < 0) rank = 0;
            if (rank >= RankThresholds.Length) rank = RankThresholds.Length - 1;
            return RankThresholds[rank];
        }

        public static string RankName(int rank)
        {
            switch (rank)
            {
                case RankD: return "D";
                case RankC: return "C";
                case RankB: return "B";
                case RankBB: return "BB";
                case RankBBB: return "BBB";
                case RankA: return "A";
                case RankAA: return "AA";
                case RankAAA: return "AAA";
                case RankS: return "S";
                case RankSP: return "S+";
                case RankSS: return "SS";
                case RankSSP: return "SS+";
                case RankSSS: return "SSS";
                case RankSSSp: return "SSS+";
                default: return "?";
            }
        }

        /// <summary>Combo flag implied by the achievement alone: 0 none / 1 FC / 2 FC+ / 3 AP / 4 AP+.</summary>
        public static int ComboOf(int achievement)
        {
            if (achievement >= 1010000) return 4;
            if (achievement >= 1000000) return 3;
            return 0;
        }

        /// <summary>Combo flag implied by a concrete judge distribution.</summary>
        public static int ComboOf(JudgeCounts j)
        {
            if (j == null || j.Total <= 0) return 0;
            bool hasMiss = j.Miss > 0;
            bool hasGood = j.Good > 0;
            bool hasGreat = j.Great > 0;
            if (!hasMiss && !hasGood && !hasGreat) return j.Critical >= j.Total ? 4 : 3;
            if (!hasMiss && !hasGood) return 2; // FC+ : only greats (or better)
            if (!hasMiss) return 1;             // FC  : no miss
            return 0;
        }

        /// <summary>Derive the rank that goes with an achievement.</summary>
        public static int RankFor(JudgeCounts j, int achievement) => RankOf(achievement);

        /// <summary>Fill in every field of <paramref name="rec"/> that can be derived from its achievement.
        /// Default policy: keep the source values (a rebirth is a copy), only repair invalid/absent ones.</summary>
        public static ScoreRecord Normalize(ScoreRecord rec, ChartStats stats)
        {
            return Normalize(rec, stats, RecalcFromAchievement);
        }

        /// <summary>Same, with an explicit recompute switch.</summary>
        public static ScoreRecord Normalize(ScoreRecord rec, ChartStats stats, bool recalc)
        {
            if (rec == null) return null;
            if (rec.Achievement < 0) rec.Achievement = 0;
            if (rec.Achievement > MaxAchievement) rec.Achievement = MaxAchievement;

            JudgeCounts j = SolveJudgeCounts(stats, rec.Achievement);

            if (recalc || rec.ScoreRank < 0 || rec.ScoreRank >= RankCount)
                rec.ScoreRank = RankOf(rec.Achievement);
            if (recalc || rec.ComboStatus < 0 || rec.ComboStatus > 4)
                rec.ComboStatus = (j != null && j.Total > 0) ? ComboOf(j) : ComboOf(rec.Achievement);
            if (rec.SyncStatus < 0 || rec.SyncStatus > 5) rec.SyncStatus = 0;

            int theory = stats != null && stats.IsValid
                ? (stats.TheoryDx > 0 ? stats.TheoryDx : stats.TotalNotes * 3)
                : 0;
            if (recalc && theory > 0)
                rec.DeluxscoreMax = DxScore(j);
            else if (rec.DeluxscoreMax <= 0)
                rec.DeluxscoreMax = DxScore(j);
            if (theory > 0 && rec.DeluxscoreMax > theory) rec.DeluxscoreMax = theory;
            if (rec.DeluxscoreMax < 0) rec.DeluxscoreMax = 0;
            if (rec.PlayCount <= 0) rec.PlayCount = 1;
            if (rec.ExtNum1 < 0) rec.ExtNum1 = 0;
            return rec;
        }

        /// <summary>DX score for a judge distribution: 3*critical + 2*perfect + great, capped at 3*notes.</summary>
        public static int DxScore(JudgeCounts j)
        {
            if (j == null) return 0;
            long dx = 3L * j.Critical + 2L * j.Perfect + j.Great;
            long max = 3L * j.Total;
            if (dx > max) dx = max;
            if (dx < 0) dx = 0;
            return (int)dx;
        }

        // ------------------------------------------------------------------ judge breakdown (B/C modes)

        // Base-score judge values (fraction of a note's full value).  Verified against the arcade
        // scoring rules and the reference implementation (MajdataPlay): great = 0.8, good = 0.5,
        // perfect/critical = 1.0, miss = 0.  (Break notes use the same base values plus the break pool.)
        public static double GreatCoefficient = 0.8;
        public static double GoodCoefficient = 0.5;
        public const double BreakBaseGreatCoefficient = 0.8; // break great base = 4/5 of 5 units
        public const double BreakBaseGoodCoefficient = 0.4;  // break good  base = 2/5 of 5 units

        // Break bonus pool (1% total, averaged per break note).
        public const double BreakBonusCritical = 1.0;
        public const double BreakBonusPerfect = 0.5; // low perfect (conservative)
        public const double BreakBonusGreat = 0.4;
        public const double BreakBonusGood = 0.3;
        public const double BreakBonusMiss = 0.0;

        public const int SyncNone = 0, SyncFull = 1, SyncFullPlus = 2;

        // DX (deluxe score) star bands: 0★ >=0, 1★ >=85, 2★ >=90, 3★ >=93, 4★ >=95, 5★ >=97.
        public static readonly double[] DxStarThresholds = { 0.0, 85.0, 90.0, 93.0, 95.0, 97.0 };

        /// <summary>DX star of a DX percentage (0..5).</summary>
        public static int DxStarOf(double dxPercent)
        {
            int star = 0;
            for (int i = 0; i < DxStarThresholds.Length; i++)
                if (dxPercent >= DxStarThresholds[i]) star = i;
            return star;
        }

        /// <summary>Star band [low, high) that a source DX percentage belongs to.</summary>
        public static void DxBandOf(double sourcePercent, out double low, out double high)
        {
            int star = DxStarOf(sourcePercent);
            low = DxStarThresholds[star];
            high = star < DxStarThresholds.Length - 1 ? DxStarThresholds[star + 1] - 0.0001 : 100.0;
            // stay close to the source value as well (lead: intersect with source±1)
            double nearLow = sourcePercent - 1.0;
            double nearHigh = sourcePercent + 1.0;
            if (nearLow > low) low = nearLow;
            if (nearHigh < high) high = nearHigh;
            if (high < low) { low = DxStarThresholds[star]; high = low + 0.0001; }
        }

        /// <summary>Build a self-consistent judge distribution for a target achievement.
        /// AP is assumed when the target is high enough to be an all-perfect run.</summary>
        public static JudgeBreakdown BuildJudgeBreakdown(int achievement, ChartStats stats)
        {
            return BuildJudgeBreakdownCore(achievement, stats, IsAllPerfectCapable(achievement, stats), -1, null);
        }

        public static JudgeBreakdown BuildJudgeBreakdown(int achievement, ChartStats stats, bool allPerfectCapable)
        {
            return BuildJudgeBreakdownCore(achievement, stats, allPerfectCapable, -1, null);
        }

        /// <summary>Match the source record's deluxe score (DX) at the same time as the achievement.</summary>
        public static JudgeBreakdown BuildJudgeBreakdown(int achievement, ChartStats stats, int sourceDeluxscoreMax)
        {
            return BuildJudgeBreakdownCore(achievement, stats, IsAllPerfectCapable(achievement, stats), sourceDeluxscoreMax, null);
        }

        public static JudgeBreakdown BuildJudgeBreakdown(int achievement, ChartStats stats, bool allPerfectCapable, int? seed)
        {
            return BuildJudgeBreakdownCore(achievement, stats, allPerfectCapable, -1, seed);
        }

        /// <summary>Full overload: <paramref name="sourceDeluxscoreMax"/> &lt;= 0 means "no DX constraint".</summary>
        public static JudgeBreakdown BuildJudgeBreakdown(int achievement, ChartStats stats, bool allPerfectCapable,
                                                         int sourceDeluxscoreMax, int? seed)
        {
            return BuildJudgeBreakdownCore(achievement, stats, allPerfectCapable, sourceDeluxscoreMax, seed);
        }

        private static JudgeBreakdown BuildJudgeBreakdownCore(int achievement, ChartStats stats, bool allPerfectCapable,
                                                              int sourceDeluxscoreMax, int? seed)
        {
            var result = new JudgeBreakdown();
            if (stats == null || !stats.IsValid) return result;

            int achv = achievement;
            if (achv < 0) achv = 0;
            if (achv > MaxAchievement) achv = MaxAchievement;

            int nBreak = Math.Max(0, stats.BreakCount);
            int nTap = Math.Max(0, stats.TapCount);
            int nHold = Math.Max(0, stats.HoldCount);
            int nSlide = Math.Max(0, stats.SlideCount);
            int total = nTap + nHold + nSlide + nBreak;
            if (total <= 0) return result;
            result.TotalNotes = total;

            var rng = seed.HasValue ? new Random(seed.Value) : new Random(unchecked(Environment.TickCount * 31 + total * 17 + achv));

            try
            {
                if (allPerfectCapable && TryBuildAllPerfect(achv, nTap, nHold, nSlide, nBreak, sourceDeluxscoreMax, result, rng))
                    return result;

                BuildRealistic(achv, nTap, nHold, nSlide, nBreak, sourceDeluxscoreMax, result, rng);
                return result;
            }
            catch (Exception)
            {
                // Never throw into the game loop; fall back to a plain all-perfect table.
                result.CriticalPerfect = total;
                result.MaxCombo = total;
                result.SyncStatus = SyncFullPlus;
                result.EstimatedAchievement = 1010000;
                result.DeluxscoreMax = total * 3;
                result.DxPercent = 100.0;
                result.DxStar = 5;
                return result;
            }
        }

        /// <summary>True when the target can be represented as an all-perfect run (no great/good/miss).</summary>
        public static bool IsAllPerfectCapable(int achievement, ChartStats stats)
        {
            if (stats == null || !stats.IsValid) return false;
            if (achievement >= MaxAchievement) return true;
            if (stats.BreakCount <= 0) return achievement >= 1000000;
            return achievement >= 1005000; // 100% base + at least 0.5% break bonus
        }

        private static bool TryBuildAllPerfect(int achv, int nTap, int nHold, int nSlide, int nBreak,
                                               int sourceDeluxscoreMax, JudgeBreakdown result, Random rng)
        {
            int total = nTap + nHold + nSlide + nBreak;
            int nonBreak = nTap + nHold + nSlide;
            if (nBreak <= 0)
            {
                if (achv < 1000000) return false;
                // all perfect for the achievement; DX can be tuned with CP -> Perfect conversions
                int cp = nonBreak;
                if (sourceDeluxscoreMax > 0)
                {
                    int targetDx = Math.Max(0, Math.Min(total * 3, sourceDeluxscoreMax));
                    int needLower = total * 3 - targetDx;   // each CP->Perfect costs 1 DX point
                    if (needLower < 0) needLower = 0;
                    if (needLower > cp) needLower = cp;
                    cp -= needLower;
                }
                result.CriticalPerfect = cp;
                result.Perfect = nonBreak - cp;
                result.MaxCombo = total;
                result.SyncStatus = result.Perfect == 0 ? SyncFullPlus : SyncFull;
                result.EstimatedAchievement = 1000000;
                result.ErrorPercent = Math.Abs(1000000 - achv) / 10000.0;
                int cpTap, cpHold, cpSlide;
                Distribute(cp, nTap, nHold, nSlide, out cpTap, out cpHold, out cpSlide);
                result.TapCounts[0] = cpTap; result.TapCounts[1] = nTap - cpTap;
                result.HoldCounts[0] = cpHold; result.HoldCounts[1] = nHold - cpHold;
                result.SlideCounts[0] = cpSlide; result.SlideCounts[1] = nSlide - cpSlide;
                FinalizeDx(result, nBreak, sourceDeluxscoreMax);
                return true;
            }
            if (achv < 1005000) return false;

            // base = 100%, break bonus = (0.5 * B + 0.5 * criticalBreaks) / B  -> 0.5%..1%
            double targetSum = (achv - 1000000) / 10000.0 * nBreak; // in "break coefficient" units
            int crit = (int)Math.Round((targetSum - 0.5 * nBreak) / 0.5);
            if (crit < 0) crit = 0;
            if (crit > nBreak) crit = nBreak;

            int perfectBreaks = nBreak - crit;
            // Non-break notes all critical: AP+ when every break is critical too, otherwise AP.
            int cpNonBreak = nonBreak;
            if (sourceDeluxscoreMax > 0)
            {
                int targetDx = Math.Max(0, Math.Min(total * 3, sourceDeluxscoreMax));
                int dxNow = 3 * crit + 2 * perfectBreaks + 3 * nonBreak;
                int needLower = dxNow - targetDx;          // each CP->Perfect costs 1 DX point
                if (needLower < 0) needLower = 0;
                if (needLower > cpNonBreak) needLower = cpNonBreak;
                cpNonBreak -= needLower;
            }
            result.CriticalPerfect = cpNonBreak + crit;
            result.Perfect = perfectBreaks + (nonBreak - cpNonBreak);
            result.MaxCombo = total;
            result.SyncStatus = result.Perfect == 0 ? SyncFullPlus : SyncFull;
            int cpTap2, cpHold2, cpSlide2;
            Distribute(cpNonBreak, nTap, nHold, nSlide, out cpTap2, out cpHold2, out cpSlide2);
            result.TapCounts[0] = cpTap2; result.TapCounts[1] = nTap - cpTap2;
            result.HoldCounts[0] = cpHold2; result.HoldCounts[1] = nHold - cpHold2;
            result.SlideCounts[0] = cpSlide2; result.SlideCounts[1] = nSlide - cpSlide2;
            result.BreakCounts[0] = crit; result.BreakCounts[1] = perfectBreaks;

            double breakSum = crit * BreakBonusCritical + perfectBreaks * BreakBonusPerfect;
            double actual = 1000000.0 + 10000.0 * breakSum / nBreak;
            result.EstimatedAchievement = (int)Math.Round(actual);
            result.ErrorPercent = Math.Abs(actual - achv) / 10000.0;
            result.DxSourcePercent = sourceDeluxscoreMax > 0 ? sourceDeluxscoreMax * 100.0 / (3.0 * total) : -1;
            FinalizeDx(result, nBreak, sourceDeluxscoreMax);
            return true;
        }

        private static void BuildRealistic(int achv, int nTap, int nHold, int nSlide, int nBreak,
                                           int sourceDeluxscoreMax, JudgeBreakdown result, Random rng)
        {
            int total = nTap + nHold + nSlide + nBreak;
            int nonBreak = nTap + nHold + nSlide;
            double units = nTap * TapWeight + nHold * HoldWeight + nSlide * SlideWeight + 5.0 * nBreak;
            double nonBreakUnits = nTap * TapWeight + nHold * HoldWeight + nSlide * SlideWeight;
            if (total <= 0 || units <= 0) return;

            // note slots (kept in chart-type order; max combo is simulated on a shuffled copy)
            var slots = new Slot[total];
            int si = 0;
            for (int i = 0; i < nTap; i++) slots[si++] = new Slot { Weight = TapWeight, Judge = JudgeFull };
            for (int i = 0; i < nHold; i++) slots[si++] = new Slot { Weight = HoldWeight, Judge = JudgeFull };
            for (int i = 0; i < nSlide; i++) slots[si++] = new Slot { Weight = SlideWeight, Judge = JudgeFull };
            for (int i = 0; i < nBreak; i++) slots[si++] = new Slot { Weight = 5.0, Break = true, Judge = JudgeFull };

            // break notes are critical: 1% break bonus in the pool, no base loss from them
            for (int i = total - nBreak; i < total; i++) slots[i].Judge = JudgeCritical;
            double breakBonus = nBreak > 0 ? 10000.0 : 0.0;

            double baseTarget = achv - breakBonus;
            if (baseTarget < 0) baseTarget = 0;
            if (baseTarget > 1000000) baseTarget = 1000000;
            double targetLossUnits = (1.0 - baseTarget / 1000000.0) * units;

            // 1) place misses on the heaviest non-break notes first: fewer affected notes means a
            //    higher achievable DX (each note can lose at most 3 DX points regardless of type).
            var heavy = new List<int>(nonBreak);
            for (int i = 0; i < nonBreak; i++) heavy.Add(i);
            Shuffle(heavy, rng);
            heavy.Sort(delegate (int x, int y)
            {
                int c = slots[y].Weight.CompareTo(slots[x].Weight);
                return c != 0 ? c : x.CompareTo(y);
            });

            int missTotal = PickMissCount(achv, total, nonBreak, rng);
            if (missTotal > heavy.Count) missTotal = heavy.Count;
            double missLoss = 0;
            int missCount = 0;
            for (int i = 0; i < missTotal; i++)
            {
                int idx = heavy[i];
                slots[idx].Judge = JudgeMiss;
                missLoss += slots[idx].Weight;
                missCount++;
            }
            // misses alone must not overshoot the target (drop the lightest of the chosen ones)
            while (missLoss > targetLossUnits + 0.001 && missCount > 0)
            {
                missCount--;
                int idx = heavy[missCount];
                slots[idx].Judge = JudgeFull;
                missLoss -= slots[idx].Weight;
            }

            // 2) spend the rest of the loss on the heaviest remaining notes.  Good eats the most
            //    accuracy per lost DX point, so it goes first, then greats; polish fixes the tail.
            int cursor = missCount;
            double remainingLoss = targetLossUnits - missLoss;
            if (remainingLoss < 0) remainingLoss = 0;
            double remUnits = 0;
            for (int i = cursor; i < heavy.Count; i++) remUnits += slots[heavy[i]].Weight;
            int maxGood = Math.Max(0, (int)Math.Round(heavy.Count * 0.15));
            int goodCount = 0, greatCount = 0;
            int guard = 0;
            while (cursor < heavy.Count && remainingLoss > 0.0001 && guard++ < 8000)
            {
                int idx = heavy[cursor];
                double w = slots[idx].Weight;
                if (remainingLoss >= 0.5 * w && goodCount < maxGood)
                {
                    slots[idx].Judge = JudgeGood;
                    remainingLoss -= 0.5 * w;
                    goodCount++;
                    cursor++;
                    continue;
                }
                if (remainingLoss >= 0.2 * w)
                {
                    slots[idx].Judge = JudgeGreat;
                    remainingLoss -= 0.2 * w;
                    greatCount++;
                    cursor++;
                    continue;
                }
                break;
            }
            // very low achievements need even more loss: promote some goods to misses
            guard = 0;
            while (remainingLoss > 0.0001 && guard++ < 8000)
            {
                int found = -1;
                for (int i = missCount; i < cursor; i++)
                {
                    if (slots[heavy[i]].Judge == JudgeGood) { found = i; break; }
                }
                if (found < 0) break;
                int idx = heavy[found];
                double w = slots[idx].Weight;
                slots[idx].Judge = JudgeMiss;
                remainingLoss -= 0.5 * w;
                missCount++;
            }
            if (remainingLoss < 0) remainingLoss = 0;

            // 2b) fine-tune the last fraction of the loss on the lightest untouched notes: the heavy
            //     notes above are great for DX, but light notes give the fine granularity the
            //     achievement target needs (0.2 accuracy unit per tap great).
            if (remainingLoss > 0.0001)
            {
                var light = new List<int>();
                for (int i = cursor; i < heavy.Count; i++)
                {
                    int idx = heavy[i];
                    if (slots[idx].Judge == JudgeFull) light.Add(idx);
                }
                light.Sort(delegate (int x, int y) { return slots[x].Weight.CompareTo(slots[y].Weight); });
                for (int i = 0; i < light.Count && remainingLoss > 0.0001; i++)
                {
                    int idx = light[i];
                    double w = slots[idx].Weight;
                    double greatLoss = 0.2 * w;
                    double goodLoss = 0.5 * w;
                    bool useGood = remainingLoss >= goodLoss &&
                                   Math.Abs(remainingLoss - goodLoss) <= Math.Abs(remainingLoss - greatLoss);
                    if (useGood) { slots[idx].Judge = JudgeGood; remainingLoss -= goodLoss; }
                    else if (remainingLoss >= greatLoss) { slots[idx].Judge = JudgeGreat; remainingLoss -= greatLoss; }
                    else { slots[idx].Judge = JudgeGreat; remainingLoss = 0; } // tiny overshoot, polish fixes it
                }
            }

            var pool = new List<int>(heavy.Count);
            for (int i = 0; i < heavy.Count; i++)
            {
                pool.Add(heavy[i]);
            }

            // 3) polish with single-note tweaks until the formula lands on the target
            Polish(slots, pool, achv, breakBonus, units, nBreak, rng, 120);

            // 4) CP vs Perfect split.  Perfect costs one DX point but no achievement, so this is the
            //    knob that pins the deluxe score: with a source DX we convert exactly enough CPs into
            //    Perfects to land on the source band; otherwise use the plausible CP fraction.
            int fullNonBreak = 0;
            for (int i = 0; i < slots.Length; i++)
                if (!slots[i].Break && slots[i].Judge == JudgeFull) fullNonBreak++;
            int greatNow = 0, goodNow = 0, missNow = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Judge == JudgeGreat) greatNow++;
                else if (slots[i].Judge == JudgeGood) goodNow++;
                else if (slots[i].Judge == JudgeMiss) missNow++;
            }

            double sourcePct = sourceDeluxscoreMax > 0 ? sourceDeluxscoreMax * 100.0 / (3.0 * total) : -1.0;
            if (sourcePct >= 0)
            {
                double lo, hi;
                DxBandOf(sourcePct, out lo, out hi);
                double targetPct = Math.Max(lo, Math.Min(hi, sourcePct));
                double targetPoints = targetPct / 100.0 * 3.0 * total;
                // breaks are all critical here: 3 points each; full non-break notes cost 2 + (CP?1:0)
                double dxWithoutCp = 3.0 * nBreak + 2.0 * fullNonBreak + greatNow;
                int cpReq = (int)Math.Round(targetPoints - dxWithoutCp);
                if (cpReq < 0) cpReq = 0;
                if (cpReq > fullNonBreak) cpReq = fullNonBreak;

                var fullIdx = new List<int>(fullNonBreak);
                for (int i = 0; i < slots.Length; i++)
                    if (!slots[i].Break && slots[i].Judge == JudgeFull) fullIdx.Add(i);
                Shuffle(fullIdx, rng);
                for (int i = 0; i < fullIdx.Count; i++)
                    slots[fullIdx[i]].Judge = i < cpReq ? JudgeCritical : JudgePerfect;
                result.DxTargetPercent = targetPct;
                result.DxSourcePercent = sourcePct;
            }
            else
            {
                double cpFrac = CriticalFractionForBreakdown(achv, rng);
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i].Judge != JudgeFull) continue;
                    slots[i].Judge = rng.NextDouble() < cpFrac ? JudgeCritical : JudgePerfect;
                }
            }

            int cp = 0, perfect = 0, great = 0, good = 0, missOut = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                int[] arr = s.Break ? result.BreakCounts
                    : (s.Weight >= 3.0 ? result.SlideCounts
                    : (s.Weight >= 2.0 ? result.HoldCounts : result.TapCounts));
                switch (s.Judge)
                {
                    case JudgeCritical: cp++; arr[0]++; break;
                    case JudgePerfect: perfect++; arr[1]++; break;
                    case JudgeGreat: great++; arr[2]++; break;
                    case JudgeGood: good++; arr[3]++; break;
                    default: missOut++; arr[4]++; break;
                }
            }
            result.CriticalPerfect = cp;
            result.Perfect = perfect;
            result.Great = great;
            result.Good = good;
            result.Miss = missOut;
            result.MaxCombo = SimulateMaxCombo(slots, rng);
            result.SyncStatus = missOut == 0
                ? (great == 0 && good == 0 && perfect == 0 ? SyncFullPlus : SyncFull)
                : SyncNone;

            double actualAchv = AchievementOf(slots, units, nBreak, breakBonus);
            result.EstimatedAchievement = (int)Math.Round(actualAchv);
            result.ErrorPercent = Math.Abs(actualAchv - achv) / 10000.0;
            FinalizeDx(result, nBreak, sourceDeluxscoreMax);
        }

        /// <summary>
        /// Achievement (in percent, e.g. 96.1658) that a judge table produces under the game's real
        /// formula:
        ///   base%  = (maxBaseScore - lostBaseScore) / maxBaseScore * 100
        ///   bonus% = breakExtra / maxBreakExtra * 1%          (0.5/0.75/1.0/0.4/0.3 per break judge)
        /// Note the break bonus is an ADDITIVE 0..1%, not a multiplicative factor: dividing by
        /// (maxBase + maxBreakExtra) yields exactly 1% less and is a common mistake (e.g. it turns
        /// 96.17% into ~95.21% on a chart with breaks).
        /// </summary>
        public static double ExpectedAchievementPercent(JudgeBreakdown b, ChartStats s)
        {
            if (b == null || s == null || !s.IsValid) return 0;
            double units = s.TapCount * TapWeight + s.HoldCount * HoldWeight + s.SlideCount * SlideWeight + 5.0 * s.BreakCount;
            if (units <= 0) return 0;
            int[] t = b.TapCounts, h = b.HoldCounts, sl = b.SlideCounts, br = b.BreakCounts;
            double loss = 0;
            loss += t[4] * TapWeight + h[4] * HoldWeight + sl[4] * SlideWeight + br[4] * 5.0;
            loss += (t[2] * TapWeight + h[2] * HoldWeight + sl[2] * SlideWeight) * (1.0 - GreatCoefficient);
            loss += (t[3] * TapWeight + h[3] * HoldWeight + sl[3] * SlideWeight) * (1.0 - GoodCoefficient);
            loss += br[2] * 5.0 * (1.0 - BreakBaseGreatCoefficient);
            loss += br[3] * 5.0 * (1.0 - BreakBaseGoodCoefficient);
            double baseRatio = 1.0 - loss / units;
            if (baseRatio < 0) baseRatio = 0;
            double bonus = 0;
            int breaks = s.BreakCount;
            if (breaks > 0)
            {
                bonus = (br[0] * BreakBonusCritical + br[1] * BreakBonusPerfect + br[2] * BreakBonusGreat + br[3] * BreakBonusGood)
                        / (double)breaks; // 0..1 fraction of the 1% pool
            }
            return (baseRatio + 0.01 * bonus) * 100.0;
        }
        /// <summary>Fill DeluxscoreMax / DxPercent / DxStar from the final judge counts.</summary>
        private static void FinalizeDx(JudgeBreakdown r, int nBreak, int sourceDeluxscoreMax)
        {
            if (r == null || r.TotalNotes <= 0) return;
            long dx = 3L * r.CriticalPerfect + 2L * r.Perfect + r.Great;
            long max = 3L * r.TotalNotes;
            if (dx > max) dx = max;
            if (dx < 0) dx = 0;
            r.DeluxscoreMax = (int)dx;
            r.DxPercent = dx * 100.0 / max;
            r.DxStar = DxStarOf(r.DxPercent);
            r.DxSourcePercent = sourceDeluxscoreMax > 0
                ? sourceDeluxscoreMax * 100.0 / max
                : -1.0;
        }

        private static void ClampPair(ref int good, ref int great, int available)
        {
            if (good < 0) good = 0;
            if (great < 0) great = 0;
            if (good > available) good = available;
            if (great > available - good) great = available - good;
        }

        /// <summary>Split <paramref name="amount"/> over three buckets proportionally to their sizes.</summary>
        private static void Distribute(int amount, int a, int b, int c, out int x, out int y, out int z)
        {
            x = y = z = 0;
            int sum = a + b + c;
            if (amount <= 0 || sum <= 0) return;
            if (amount >= sum) { x = a; y = b; z = c; return; }
            int total = 0;
            x = (int)Math.Round(amount * (double)a / sum);
            y = (int)Math.Round(amount * (double)b / sum);
            z = amount - x - y;
            total = x + y + z;
            // repair rounding drift without exceeding the bucket sizes
            int drift = amount - total;
            while (drift > 0)
            {
                if (x < a) { x++; drift--; }
                else if (y < b) { y++; drift--; }
                else if (z < c) { z++; drift--; }
                else break;
            }
            while (drift < 0)
            {
                if (z > 0) { z--; drift++; }
                else if (y > 0) { y--; drift++; }
                else if (x > 0) { x--; drift++; }
                else break;
            }
            if (x > a) x = a;
            if (y > b) y = b;
            if (z > c) z = c;
        }

        private static List<int> MakeIndexList(int from, int to, int skipFirst, Random rng)
        {
            var list = new List<int>(Math.Max(0, to - from - skipFirst));
            for (int i = from + skipFirst; i < to; i++) list.Add(i);
            Shuffle(list, rng);
            return list;
        }

        private static void Assign(List<int> indices, int goodCount, int greatCount, Slot[] slots, int goodJudge, int greatJudge)
        {
            int gd = Math.Min(goodCount, indices.Count);
            int gr = Math.Min(greatCount, indices.Count - gd);
            for (int i = 0; i < gd; i++) slots[indices[i]].Judge = goodJudge;
            for (int i = gd; i < gd + gr; i++) slots[indices[i]].Judge = greatJudge;
        }

        private static void Polish(Slot[] slots, List<int> pool, int achv, double breakBonus,
                                   double units, int nBreak, Random rng, int maxIterations)
        {
            for (int iter = 0; iter < maxIterations; iter++)
            {
                double actual = AchievementOf(slots, units, nBreak, breakBonus);
                double error = actual - achv;
                if (Math.Abs(error) <= 1.0) return;

                int bestIdx = -1;
                int bestJudge = -1;
                double bestGain = Math.Abs(error);
                for (int i = 0; i < pool.Count; i++)
                {
                    int idx = pool[i];
                    int judge = slots[idx].Judge;
                    double w = slots[idx].Weight;
                    // candidate single-note changes
                    ConsiderPolish(judge, JudgeGreat, idx, w, error, units, ref bestIdx, ref bestJudge, ref bestGain);
                    ConsiderPolish(judge, JudgeGood, idx, w, error, units, ref bestIdx, ref bestJudge, ref bestGain);
                    ConsiderPolish(judge, JudgeFull, idx, w, error, units, ref bestIdx, ref bestJudge, ref bestGain);
                }
                if (bestIdx >= 0)
                {
                    slots[bestIdx].Judge = bestJudge;
                    continue;
                }
                // single moves are stuck: try a pair of moves (the loss grid is 0.1 units, pairs can
                // reach it even when no single step improves)
                if (!TryPairPolish(slots, pool, error, units, rng)) return;
            }
        }

        private static bool TryPairPolish(Slot[] slots, List<int> pool, double error, double units, Random rng)
        {
            if (pool.Count < 2) return false;
            var sample = new List<int>(pool);
            for (int i = sample.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                int t = sample[i]; sample[i] = sample[j]; sample[j] = t;
            }
            int n = Math.Min(80, sample.Count);
            var deltas = new List<double>(n * 3);
            var idxs = new List<int>(n * 3);
            var judges = new List<int>(n * 3);
            for (int i = 0; i < n; i++)
            {
                int idx = sample[i];
                int from = slots[idx].Judge;
                double w = slots[idx].Weight;
                AddMove(deltas, idxs, judges, idx, from, JudgeGreat, w, units);
                AddMove(deltas, idxs, judges, idx, from, JudgeGood, w, units);
                AddMove(deltas, idxs, judges, idx, from, JudgeFull, w, units);
            }
            double bestGain = Math.Abs(error);
            int bestA = -1, bestB = -1, bestJa = -1, bestJb = -1;
            for (int i = 0; i < deltas.Count; i++)
            {
                for (int j = i + 1; j < deltas.Count; j++)
                {
                    if (idxs[i] == idxs[j]) continue;
                    double gain = Math.Abs(error + deltas[i] + deltas[j]);
                    if (gain < bestGain - 1e-9)
                    {
                        bestGain = gain;
                        bestA = idxs[i]; bestJa = judges[i];
                        bestB = idxs[j]; bestJb = judges[j];
                    }
                }
            }
            if (bestA < 0 || bestB < 0)
            {
                // triples: loss steps of 0.2/0.3/0.5/0.6 units combine into a 0.1 grid only with
                // combinations of three moves on some charts; sample randomly instead of O(n^3)
                int bestTa = -1, bestTb = -1, bestTc = -1, bestTja = -1, bestTjb = -1, bestTjc = -1;
                int tries = deltas.Count > 2 ? 30000 : 0;
                for (int t = 0; t < tries; t++)
                {
                    int a = rng.Next(deltas.Count);
                    int b = rng.Next(deltas.Count);
                    int c = rng.Next(deltas.Count);
                    if (idxs[a] == idxs[b] || idxs[a] == idxs[c] || idxs[b] == idxs[c]) continue;
                    double gain = Math.Abs(error + deltas[a] + deltas[b] + deltas[c]);
                    if (gain < bestGain - 1e-9)
                    {
                        bestGain = gain;
                        bestTa = idxs[a]; bestTja = judges[a];
                        bestTb = idxs[b]; bestTjb = judges[b];
                        bestTc = idxs[c]; bestTjc = judges[c];
                    }
                }
                if (bestTa < 0) return false;
                slots[bestTa].Judge = bestTja;
                slots[bestTb].Judge = bestTjb;
                slots[bestTc].Judge = bestTjc;
                return true;
            }
            slots[bestA].Judge = bestJa;
            slots[bestB].Judge = bestJb;
            return true;
        }

        private static void AddMove(List<double> deltas, List<int> idxs, List<int> judges,
                                    int idx, int from, int to, double weight, double units)
        {
            if (from == to) return;
            double lossDelta = JudgeLossDelta(from, to, weight);
            deltas.Add(-lossDelta / units * 1000000.0);
            idxs.Add(idx);
            judges.Add(to);
        }

        private static void ConsiderPolish(int from, int to, int idx, double w, double error, double units,
                                           ref int bestIdx, ref int bestJudge, ref double bestGain)
        {
            if (from == to) return;
            double delta = JudgeLossDelta(from, to, w) / units * 1000000.0; // achievement change = -loss change
            double gain = Math.Abs(error - delta);
            if (gain < bestGain - 1e-9)
            {
                bestGain = gain;
                bestIdx = idx;
                bestJudge = to;
            }
        }

        private static double JudgeLossDelta(int from, int to, double weight)
        {
            return LossOfJudge(to, weight) - LossOfJudge(from, weight);
        }

        private static double LossOfJudge(int judge, double weight)
        {
            switch (judge)
            {
                case JudgeGreat: return (1.0 - GreatCoefficient) * weight;
                case JudgeGood: return (1.0 - GoodCoefficient) * weight;
                case JudgeMiss: return weight;
                default: return 0; // full value (perfect / critical)
            }
        }

        private static double AchievementOf(Slot[] slots, double units, int nBreak, double breakBonusHint)
        {
            if (units <= 0) return 0;
            double loss = 0;
            double breakSum = 0;
            int breaks = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                loss += LossOfJudge(s.Judge, s.Weight);
                if (s.Break)
                {
                    breaks++;
                    switch (s.Judge)
                    {
                        case JudgeCritical: breakSum += BreakBonusCritical; break;
                        case JudgePerfect: breakSum += BreakBonusPerfect; break;
                        case JudgeGreat: breakSum += BreakBonusGreat; break;
                        case JudgeGood: breakSum += BreakBonusGood; break;
                        default: breakSum += BreakBonusMiss; break;
                    }
                }
            }
            double base10k = (1.0 - loss / units) * 1000000.0;
            if (base10k < 0) base10k = 0;
            double bonus10k = breaks > 0 ? 10000.0 * breakSum / breaks : 0;
            double achv = base10k + bonus10k;
            if (achv < 0) achv = 0;
            if (achv > MaxAchievement) achv = MaxAchievement;
            return achv;
        }

        private static int SimulateMaxCombo(Slot[] slots, Random rng)
        {
            int n = slots.Length;
            if (n <= 0) return 0;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Shuffle(order, rng);
            int best = 0, run = 0;
            for (int i = 0; i < n; i++)
            {
                if (slots[order[i]].Judge == JudgeMiss)
                {
                    run = 0;
                    continue;
                }
                run++;
                if (run > best) best = run;
            }
            return best;
        }

        private static int PickMissCount(int achv, int total, int nonBreak, Random rng)
        {
            int miss;
            if (achv >= 995000) miss = 0;
            else if (achv >= 980000) miss = NextInclusive(rng, 0, 1);
            else if (achv >= 960000) miss = NextInclusive(rng, 1, 2);
            else if (achv >= 930000) miss = NextInclusive(rng, 2, 4);
            else if (achv >= 900000) miss = NextInclusive(rng, 4, 8);
            else
            {
                double deficit = (900000.0 - achv) / 1000000.0;
                int baseMiss = (int)Math.Round(deficit * total * 0.6);
                double jitter = 0.8 + rng.NextDouble() * 0.4;
                miss = (int)Math.Round(baseMiss * jitter);
                if (miss < 9) miss = 9;
            }
            if (miss > nonBreak) miss = nonBreak;
            if (miss < 0) miss = 0;
            return miss;
        }

        private static double CriticalFractionForBreakdown(int achv, Random rng)
        {
            if (achv >= 1000000) return 1.0;
            double frac = 0.60 + (achv - 900000) / 100000.0 * 0.28;
            frac += (rng.NextDouble() - 0.5) * 0.06;
            if (frac < 0.55) frac = 0.55;
            if (frac > 0.95) frac = 0.95;
            return frac;
        }

        private static int NextInclusive(Random rng, int min, int max)
        {
            if (max < min) max = min;
            return rng.Next(min, max + 1);
        }

        private static double AverageWeight(Slot[] slots, List<int> pool)
        {
            if (pool.Count == 0) return 1.0;
            double sum = 0;
            for (int i = 0; i < pool.Count; i++) sum += slots[pool[i]].Weight;
            return sum / pool.Count;
        }

        private static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                T t = list[i];
                list[i] = list[j];
                list[j] = t;
            }
        }

        private const int JudgeMiss = 0, JudgeGood = 1, JudgeGreat = 2, JudgePerfect = 3, JudgeCritical = 4, JudgeFull = 5;

        private sealed class Slot
        {
            public double Weight;
            public bool Break;
            public int Judge;
        }

        // ------------------------------------------------------------------ solver

        private const double TapWeight = 1.0;
        private const double HoldWeight = 2.0;
        private const double SlideWeight = 3.0;

        /// <summary>
        /// Given the target achievement and the chart note counts, reverse-engineer a self-consistent
        /// judge distribution.  The result is within roughly one note's worth of score of the target
        /// (much closer for normal charts), and keeps AP/FC flags consistent with the distribution.
        /// </summary>
        public static JudgeCounts SolveJudgeCounts(ChartStats stats, int achievement)
        {
            var result = new JudgeCounts();
            if (stats == null || !stats.IsValid) return result;
            int achv = achievement;
            if (achv < 0) achv = 0;
            if (achv > MaxAchievement) achv = MaxAchievement;

            int brk = Math.Max(0, stats.BreakCount);
            int tap = Math.Max(0, stats.TapCount);
            int hold = Math.Max(0, stats.HoldCount);
            int slide = Math.Max(0, stats.SlideCount);
            int total = tap + hold + slide + brk;
            if (total <= 0) return result;
            result.Total = total;

            var nbCounts = new[] { tap, hold, slide };
            var nbWeights = new[] { TapWeight, HoldWeight, SlideWeight };
            double nbUnits = tap * TapWeight + hold * HoldWeight + slide * SlideWeight;
            double totalUnits = nbUnits + 5.0 * brk;
            if (totalUnits <= 0) return result;

            var candidates = BreakCandidates(brk, achv);
            double bestScore = double.MaxValue;
            double bestActual = 0;
            int[] bestDist = null;
            int[] bestGreat = null, bestGood = null, bestMiss = null;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                var dist = candidates[ci];
                double bonus10k = BreakBonus10k(dist, brk);
                double breakLoss = dist[3] * 1.0 + dist[4] * 2.0 + dist[5] * 5.0;

                double baseTarget10k = achv - bonus10k;
                if (baseTarget10k < 0) baseTarget10k = 0;
                if (baseTarget10k > 1000000) baseTarget10k = 1000000;

                double targetLoss = (1000000.0 - baseTarget10k) / 1000000.0 * totalUnits;
                double nbTargetLoss = targetLoss - breakLoss;
                if (nbTargetLoss < 0) nbTargetLoss = 0;

                int[] great = new int[3], good = new int[3], miss = new int[3];
                SolveBaseCounts(nbCounts, nbWeights, nbTargetLoss, great, good, miss);
                double nbLoss = LossUnits(nbWeights, great, good, miss);

                double actualLoss = breakLoss + nbLoss;
                double actualBase10k = (1.0 - actualLoss / totalUnits) * 1000000.0;
                double actualAchv = actualBase10k + bonus10k;
                double error = Math.Abs(actualAchv - achv);

                // Plausibility: a missed/great break is rare, so only choose it when it fixes a real error.
                double penalty = dist[5] * 60.0 + dist[4] * 25.0 + dist[3] * 8.0;
                if (achv >= 1000000)
                {
                    if (nbLoss > 0.05) penalty += 400.0;
                    if (dist[5] > 0) penalty += 300.0;
                    if (dist[4] > 0) penalty += 150.0;
                }
                double score = error + penalty;
                if (score < bestScore - 1e-9)
                {
                    bestScore = score;
                    bestActual = actualAchv;
                    bestDist = dist;
                    bestGreat = great;
                    bestGood = good;
                    bestMiss = miss;
                }
            }

            if (bestDist == null) return result;

            int nbGreat = bestGreat[0] + bestGreat[1] + bestGreat[2];
            int nbGood = bestGood[0] + bestGood[1] + bestGood[2];
            int nbMiss = bestMiss[0] + bestMiss[1] + bestMiss[2];
            int nbFull = tap + hold + slide - nbGreat - nbGood - nbMiss;
            if (nbFull < 0) nbFull = 0;

            int critical = 0, perfect = 0;
            if (nbFull > 0)
            {
                if (achv >= 1000000 && nbGreat == 0 && nbGood == 0 && nbMiss == 0)
                {
                    critical = nbFull;
                }
                else
                {
                    double q = CriticalFraction(achv);
                    critical = (int)Math.Round(nbFull * q);
                    if (critical < 0) critical = 0;
                    if (critical > nbFull) critical = nbFull;
                    perfect = nbFull - critical;
                }
            }

            result.Critical = critical + bestDist[0];
            result.Perfect = perfect + bestDist[1] + bestDist[2];
            result.Great = nbGreat + bestDist[3];
            result.Good = nbGood + bestDist[4];
            result.Miss = nbMiss + bestDist[5];
            result.EstimatedAchievement = (int)Math.Round(bestActual);
            return result;
        }

        /// <summary>How much of a chart's full-value notes are assumed to be critical (DX score plausibility).</summary>
        private static double CriticalFraction(int achievement)
        {
            if (achievement >= 1000000) return 1.0;
            double q = (achievement - 900000) / 100000.0;
            if (q < 0) q = 0;
            if (q > 0.95) q = 0.95;
            return q;
        }

        /// <summary>Achievement implied by a judge distribution (used for logging / self-checks).
        /// The exact per-type split is not kept in <see cref="JudgeCounts"/>, so this is approximate:
        /// it spreads great/good/miss over the average non-break note weight.</summary>
        public static int EstimatedAchievement(JudgeCounts j, ChartStats stats)
        {
            if (j == null || stats == null || !stats.IsValid) return 0;
            double nbUnits = stats.TapCount * TapWeight + stats.HoldCount * HoldWeight + stats.SlideCount * SlideWeight;
            int brk = Math.Max(0, stats.BreakCount);
            double totalUnits = nbUnits + 5.0 * brk;
            if (totalUnits <= 0) return 0;

            int nbNotes = Math.Max(1, stats.TotalNotes - brk);
            double avgWeight = nbUnits / nbNotes;
            double loss = (j.Great * 0.2 + j.Good * 0.5 + j.Miss * 1.0) * avgWeight;
            double base10k = (1.0 - loss / totalUnits) * 1000000.0;
            if (base10k < 0) base10k = 0;
            if (base10k > 1000000) base10k = 1000000;

            double bonus10k = 0;
            if (brk > 0 && j.Miss == 0)
            {
                // Without the break split, assume the best case that still matches the combo flag.
                bonus10k = j.Good == 0 && j.Great == 0 ? 10000.0 : 5000.0;
            }
            double achv = base10k + bonus10k;
            if (achv < 0) achv = 0;
            if (achv > MaxAchievement) achv = MaxAchievement;
            return (int)Math.Round(achv);
        }

        // ------------------------------------------------------------------ internals

        private static List<int[]> BreakCandidates(int brk, int achv)
        {
            var list = new List<int[]>();
            if (brk <= 0)
            {
                list.Add(new int[6]);
                return list;
            }

            if (achv < 1000000)
            {
                // Base score can absorb any break bonus, so just offer the plausible extremes.
                list.Add(new[] { brk, 0, 0, 0, 0, 0 }); // all critical
                list.Add(new[] { 0, brk, 0, 0, 0, 0 }); // all high perfect
                list.Add(new[] { 0, 0, brk, 0, 0, 0 }); // all low perfect
                list.Add(new[] { 0, 0, 0, 0, 0, 0 });   // handled below as all-miss replacement
                list[3] = new[] { 0, 0, 0, 0, 0, brk };  // all miss
                return list;
            }

            if (brk <= 8)
            {
                // Full enumeration of (critical, high perfect, low perfect, great, good, miss).
                for (int c = 0; c <= brk; c++)
                    for (int h = 0; h <= brk - c; h++)
                        for (int l = 0; l <= brk - c - h; l++)
                            for (int g = 0; g <= brk - c - h - l; g++)
                                for (int gd = 0; gd <= brk - c - h - l - g; gd++)
                                {
                                    int m = brk - c - h - l - g - gd;
                                    list.Add(new[] { c, h, l, g, gd, m });
                                }
                return list;
            }

            // Many break notes: a compact family is enough (granularity is fine at this size).
            for (int c = 0; c <= brk; c++) list.Add(new[] { c, 0, brk - c, 0, 0, 0 });
            for (int h = 1; h <= brk; h++) list.Add(new[] { 0, h, brk - h, 0, 0, 0 });
            list.Add(new[] { 0, 0, 0, brk, 0, 0 });
            list.Add(new[] { 0, 0, 0, 0, brk, 0 });
            list.Add(new[] { 0, 0, 0, 0, 0, brk });
            int fine = Math.Min(brk, 24);
            for (int j = 1; j <= fine; j++)
            {
                list.Add(new[] { 0, 0, brk - j, j, 0, 0 }); // some great breaks
                list.Add(new[] { 0, 0, brk - j, 0, j, 0 }); // some good breaks
            }
            return list;
        }

        private static double BreakBonus10k(int[] dist, int brk)
        {
            if (brk <= 0) return 0;
            double sum = dist[0] * 1.0 + dist[1] * 0.75 + dist[2] * 0.5 + dist[3] * 0.4 + dist[4] * 0.3;
            return 10000.0 * sum / brk;
        }

        private static double LossUnits(double[] weights, int[] great, int[] good, int[] miss)
        {
            double loss = 0;
            for (int i = 0; i < weights.Length; i++)
                loss += (great[i] * 0.2 + good[i] * 0.5 + miss[i] * 1.0) * weights[i];
            return loss;
        }

        /// <summary>
        /// Distribute a base-score loss (in units) over the non-break notes, preferring greats for small
        /// losses and adding goods/misses as the loss grows, then polish with local single-note moves.
        /// </summary>
        private static void SolveBaseCounts(int[] counts, double[] weights, double targetLoss,
                                            int[] great, int[] good, int[] miss)
        {
            for (int i = 0; i < counts.Length; i++) { great[i] = 0; good[i] = 0; miss[i] = 0; }

            double units = 0;
            for (int i = 0; i < counts.Length; i++) units += counts[i] * weights[i];
            if (units <= 0) return;

            double f = targetLoss / units;
            if (f < 0) f = 0;
            if (f > 1) f = 1;

            double qGreat, qGood, qMiss;
            if (f <= 0.2) { qGreat = f / 0.2; qGood = 0; qMiss = 0; }
            else if (f <= 0.5) { qGood = (f - 0.2) / 0.3; qGreat = 1 - qGood; qMiss = 0; }
            else { qMiss = (f - 0.5) / 0.5; qGood = 1 - qMiss; qGreat = 0; }

            for (int i = 0; i < counts.Length; i++)
            {
                int n = counts[i];
                int m = (int)Math.Round(qMiss * n);
                if (m > n) m = n;
                if (m < 0) m = 0;
                int gd = (int)Math.Round(qGood * (n - m));
                if (gd > n - m) gd = n - m;
                if (gd < 0) gd = 0;
                int gr = (int)Math.Round(qGreat * (n - m - gd));
                if (gr > n - m - gd) gr = n - m - gd;
                if (gr < 0) gr = 0;
                miss[i] = m;
                good[i] = gd;
                great[i] = gr;
            }

            for (int iter = 0; iter < 400; iter++)
            {
                double loss = LossUnits(weights, great, good, miss);
                double diff = targetLoss - loss;
                if (Math.Abs(diff) < 0.05) break;

                double bestAbs = Math.Abs(diff);
                int bestType = -1, bestMove = 0;
                for (int i = 0; i < counts.Length; i++)
                {
                    int n = counts[i];
                    double w = weights[i];
                    if (diff > 0)
                    {
                        // increase loss
                        if (great[i] + good[i] + miss[i] < n)
                            ConsiderMove(diff, 0.2 * w, i, 1, ref bestAbs, ref bestType, ref bestMove);
                        if (great[i] > 0)
                            ConsiderMove(diff, 0.3 * w, i, 2, ref bestAbs, ref bestType, ref bestMove);
                        if (good[i] > 0)
                            ConsiderMove(diff, 0.5 * w, i, 3, ref bestAbs, ref bestType, ref bestMove);
                    }
                    else
                    {
                        // decrease loss
                        if (great[i] > 0)
                            ConsiderMove(diff, -0.2 * w, i, 4, ref bestAbs, ref bestType, ref bestMove);
                        if (good[i] > 0)
                            ConsiderMove(diff, -0.3 * w, i, 5, ref bestAbs, ref bestType, ref bestMove);
                        if (miss[i] > 0)
                            ConsiderMove(diff, -0.5 * w, i, 6, ref bestAbs, ref bestType, ref bestMove);
                    }
                }
                if (bestType < 0) break;
                switch (bestMove)
                {
                    case 1: great[bestType]++; break;
                    case 2: great[bestType]--; good[bestType]++; break;
                    case 3: good[bestType]--; miss[bestType]++; break;
                    case 4: great[bestType]--; break;
                    case 5: good[bestType]--; great[bestType]++; break;
                    case 6: miss[bestType]--; good[bestType]++; break;
                }
            }
        }

        private static void ConsiderMove(double diff, double delta, int type, int move,
                                         ref double bestAbs, ref int bestType, ref int bestMove)
        {
            double candidate = Math.Abs(diff - delta);
            if (candidate < bestAbs - 1e-9)
            {
                bestAbs = candidate;
                bestType = type;
                bestMove = move;
            }
        }
    }

    public sealed class JudgeCounts
    {
        public int Total;
        public int Critical;
        public int Perfect;
        public int Great;
        public int Good;
        public int Miss;
        /// <summary>Set by <see cref="ScoreMath.SolveJudgeCounts"/>: the achievement the counts actually produce.</summary>
        public int EstimatedAchievement;

        public int Sum => Critical + Perfect + Great + Good + Miss;

        public override string ToString()
        {
            return "C" + Critical + " P" + Perfect + " G" + Great + " Gd" + Good + " M" + Miss +
                   " /" + Total + " (~" + (EstimatedAchievement / 10000.0).ToString("0.0000") + "%)";
        }
    }

    /// <summary>
    /// A concrete, self-consistent judge table for one chart (B/C modes rebuild the result screen and
    /// the playlog from this).  <see cref="ScoreMath.SyncStatus"/> uses the simplified mapping
    /// 0 = none / 1 = full sync / 2 = full sync plus (see <see cref="ScoreMath.SyncNone"/> etc.).
    /// </summary>
    public sealed class JudgeBreakdown
    {
        public int CriticalPerfect;
        public int Perfect;
        public int Great;
        public int Good;
        public int Miss;
        public int MaxCombo;
        public int SyncStatus;
        public int TotalNotes;

        /// <summary>Diagnostics: DX points the generated table produces (0..3*TotalNotes).</summary>
        public int DeluxscoreMax;
        /// <summary>Diagnostics: DX percentage (0..100).</summary>
        public double DxPercent;
        /// <summary>Diagnostics: DX star band (0..5).</summary>
        public int DxStar;
        /// <summary>Diagnostics: source DX percentage the generator tried to match (-1 = no DX constraint).</summary>
        public double DxSourcePercent = -1;
        /// <summary>Diagnostics: DX percentage actually targeted inside the star band (-1 = no constraint).</summary>
        public double DxTargetPercent = -1;

        /// <summary>Diagnostics: achievement the generated table actually produces (1/10000%).</summary>
        public int EstimatedAchievement;
        /// <summary>Diagnostics: |actual - requested| in percent points (0.01 == one hundredth of a percent).</summary>
        public double ErrorPercent;

        public int Sum => CriticalPerfect + Perfect + Great + Good + Miss;
        public bool IsAllPerfect => Miss == 0 && Good == 0 && Great == 0;

        // Per-note-type counts, index 0=CP 1=Perfect 2=Great 3=Good 4=Miss.
        // The totals above are the sums of these; the per-type split matters because tap/hold/slide/break
        // have different score weights, so a renderer that wants to reproduce the achievement exactly
        // has to put the counted judges on the matching note types.
        public int[] TapCounts = new int[5];
        public int[] HoldCounts = new int[5];
        public int[] SlideCounts = new int[5];
        public int[] BreakCounts = new int[5];

        public int DxScore
        {
            get
            {
                long dx = 3L * CriticalPerfect + 2L * Perfect + Great;
                long max = 3L * TotalNotes;
                if (dx > max) dx = max;
                if (dx < 0) dx = 0;
                return (int)dx;
            }
        }

        public JudgeBreakdown Clone()
        {
            var c = (JudgeBreakdown)MemberwiseClone();
            c.TapCounts = (int[])TapCounts.Clone();
            c.HoldCounts = (int[])HoldCounts.Clone();
            c.SlideCounts = (int[])SlideCounts.Clone();
            c.BreakCounts = (int[])BreakCounts.Clone();
            return c;
        }

        public override string ToString()
        {
            return "CP" + CriticalPerfect + " P" + Perfect + " G" + Great + " Gd" + Good + " M" + Miss +
                   " combo" + MaxCombo + " sync" + SyncStatus + " /" + TotalNotes +
                   " (~" + (EstimatedAchievement / 10000.0).ToString("0.0000") + "%, err " +
                   ErrorPercent.ToString("0.0000") + "%)";
        }
    }
}
