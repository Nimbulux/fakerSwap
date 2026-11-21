using System;
using System.Collections.Generic;
using System.IO;
using FakerSwap.Core;

namespace FakerSwap.Data
{
    public sealed class PlanResult
    {
        public List<TransferItem> Items = new List<TransferItem>();
        /// <summary>Records dropped because this game build does not contain the chart.</summary>
        public List<ScoreRecord> Missing = new List<ScoreRecord>();
        /// <summary>Records dropped because the target account has the difficulty locked (event song etc.).</summary>
        public List<ScoreRecord> Locked = new List<ScoreRecord>();
        /// <summary>Records dropped because they were outside the difficulty window.</summary>
        public List<ScoreRecord> OutOfRange = new List<ScoreRecord>();
        /// <summary>Records already present with the same or better achievement.</summary>
        public List<ScoreRecord> AlreadyDone = new List<ScoreRecord>();
        /// <summary>源成绩低于 MinAchievement → 被抬到 80%+ 的记录数。</summary>
        public int RaisedLow;
        /// <summary>低分抬升的记录里，目标账号本来就已经超过下限 → 跳过不覆盖的数量。</summary>
        public int KeptAboveMin;
        public int SourceTotal;
        /// <summary>How many of the source records existed in the target account (diagnostics).</summary>
        public int ExistingTotal;

        public int Count => Items.Count;
        public bool IsEmpty => Items.Count == 0;

        public override string ToString()
        {
            return "items=" + Items.Count + " source=" + SourceTotal + " missing=" + Missing.Count +
                   " locked=" + Locked.Count + " outOfRange=" + OutOfRange.Count +
                   " alreadyDone=" + AlreadyDone.Count + " raised=" + RaisedLow +
                   " keptAboveMin=" + KeptAboveMin;
        }
    }

    /// <summary>
    /// Turns a source snapshot into the ordered list of charts that have to be replayed.
    ///
    /// Rules (from the user):
    ///   * only 红谱以上 (EXPERT and above) by default,
    ///   * for every song keep only the highest chart it has that exists in this build
    ///     (RE:MASTER &gt; MASTER &gt; EXPERT),
    ///   * drop songs that do not exist in this game build (delisted / never shipped),
    ///   * order = rating contribution first (highest internal level first) so an interrupted
    ///     run still leaves the account with the most valuable records,
    ///   * (optional) skip records whose target-account value is already as good or better.
    /// </summary>
    public static class PlanBuilder
    {
        /// <summary>When true, every planned item gets a <see cref="JudgeBreakdown"/> attached
        /// (needed by the B/C "realistic replay" modes).  Off by default: the A mode does not
        /// need it and generating it costs one .ma2 statistics parse per song.</summary>
        public static bool GenerateJudgeBreakdown;

        /// <summary>How many judges the last <see cref="EnsureJudges"/> call generated.</summary>
        public static int LastJudgeCount { get; private set; }

        /// <summary>Backward-compatible entry point: no target data, no "already done" filtering.</summary>
        public static PlanResult Build(SourceSnapshot source, int minLevel, int maxLevel)
        {
            return Build(source, minLevel, maxLevel, null, false);
        }

        /// <summary>Build a plan and skip charts the target account already has at the same/better value.</summary>
        public static PlanResult Build(SourceSnapshot source, int minLevel, int maxLevel, IList<ScoreRecord> existingTarget)
        {
            return Build(source, minLevel, maxLevel, existingTarget, true);
        }

        /// <summary>Full overload used by the flow (existingTarget may be null).</summary>
        public static PlanResult Build(SourceSnapshot source, int minLevel, int maxLevel,
                                       IList<ScoreRecord> existingTarget, bool skipAlreadyDone)
        {
            var res = new PlanResult();
            if (source == null || source.Records == null) return res;
            res.SourceTotal = source.Records.Count;

            if (minLevel < (int)Difficulty.Basic) minLevel = (int)Difficulty.Basic;
            if (maxLevel > (int)Difficulty.ReMaster) maxLevel = (int)Difficulty.ReMaster;
            if (maxLevel < minLevel) { int t = minLevel; minLevel = maxLevel; maxLevel = t; }

            var existing = BuildExistingMap(existingTarget);
            res.ExistingTotal = existing.Count;
            var autoSkip = LoadAutoSkip();

            // 1) window + existence check, 2) keep the highest existing difficulty per song.
            var bySong = new Dictionary<int, ScoreRecord>();
            foreach (var r in source.Records)
            {
                if (r == null) continue;
                if (r.Level < minLevel || r.Level > maxLevel) { res.OutOfRange.Add(r); continue; }
                if (!ChartCatalog.Exists(r.MusicId, r.Level)) { res.Missing.Add(r); continue; }
                if (autoSkip.Contains(r.MusicId) || IsMusicSkipped(r.MusicId)) { res.Locked.Add(r); continue; }
                // Re:MASTER（难度4）没解禁的谱面，游戏在难度页根本不接受「決定」，
                // 会卡到 25s 选曲超时把整轮任务判死。只放行「已有 Re:MASTER 成绩」或
                // 「服务端 kind7 解禁表里有它」的曲目。
                if (r.Level >= 4 && !IsReMasterPlayable(r.MusicId))
                {
                    res.Locked.Add(r);
                    continue;
                }
                // 难度/曲目对当前账号是锁的（活动曲等）：选歌页会卡在难度页直到 25s 超时，
                // 必须在计划阶段就剔除（下次跑区/转生才会跳过这些曲）。
                if (!ChartCatalog.IsDifficultyUnlocked(r.MusicId, r.Level) ||
                    !ChartCatalog.IsChartUnlocked(r.MusicId, r.Level))
                {
                    res.Locked.Add(r);
                    continue;
                }

                ScoreRecord prev;
                if (!bySong.TryGetValue(r.MusicId, out prev))
                {
                    bySong[r.MusicId] = r;
                    continue;
                }
                if (r.Level > prev.Level || (r.Level == prev.Level && r.Achievement > prev.Achievement))
                    bySong[r.MusicId] = r;
            }

            // 3) sort: internal level desc, difficulty desc, achievement desc, id asc.
            var list = new List<ScoreRecord>(bySong.Values);
            list.Sort(CompareForTransfer);

            // 4) optionally drop charts the target already has at the same/better achievement.
            int idx = 0;
            bool overwrite;
            try { overwrite = ModConfig.Data.OverwriteScores || ModConfig.Data.ForceOverwriteExisting; }
            catch (Exception) { overwrite = false; }
            foreach (var r in list)
            {
                int desiredAchv = overwrite ? MirrorAchievement(r.Achievement) : r.Achievement;
                bool raisedLow = false;
                if (ModConfig.Data.MinAchievement > 0 && desiredAchv < ModConfig.Data.MinAchievement)
                {
                    int raised = ModConfig.Data.MinAchievement + _dxRng.Next(0, 2001);
                    if (raised > 1010000) raised = 1010000;
                    if (raised > desiredAchv)
                    {
                        desiredAchv = raised;
                        raisedLow = true;
                        res.RaisedLow++;
                    }
                }
                if (existing.Count > 0)
                {
                    ScoreRecord old;
                    if (existing.TryGetValue(r.Key, out old) && old != null)
                    {
                        // 低分抬升的记录只升不降：目标账号已经超过 80%（或者已经高于抬升值）就跳过，
                        // 绝不用抬升值去覆盖一个本来更高的成绩。
                        if (raisedLow && old.Achievement > ModConfig.Data.MinAchievement)
                        {
                            res.AlreadyDone.Add(r);
                            res.KeptAboveMin++;
                            continue;
                        }
                        if (overwrite)
                        {
                            // 覆盖模式：源成绩（含镜像后的值）与目标完全一样才跳过；不同一律重传。
                            if (old.Achievement == desiredAchv)
                            {
                                res.AlreadyDone.Add(r);
                                continue;
                            }
                        }
                        else if (skipAlreadyDone && old.Achievement >= desiredAchv)
                        {
                            res.AlreadyDone.Add(r);
                            continue;
                        }
                    }
                }
                var item = new TransferItem { Desired = r.Clone(), Index = idx++ };
                if (item.Desired.Achievement != desiredAchv)
                {
                    if (raisedLow)
                        Log.Info("[plan] 低分抬到 80%+: " + r.MusicId + "-" + r.Level + "  " +
                                 (r.Achievement / 10000.0).ToString("0.0000") + "% → " +
                                 (desiredAchv / 10000.0).ToString("0.0000") + "%");
                    else
                        Log.Info("[plan] 达成率镜像上取整: " + r.MusicId + "-" + r.Level + "  " +
                                 (r.Achievement / 10000.0).ToString("0.0000") + "% → " +
                                 (desiredAchv / 10000.0).ToString("0.0000") + "%");
                    item.Desired.Achievement = desiredAchv;
                }
                if (raisedLow)
                {
                    // 抬高后 DX 由判定表按新成绩重新计算（源 DX 与新高分不匹配）。
                    item.Desired.DeluxscoreMax = 0;
                }
                else if (overwrite && ModConfig.Data.DxBonusMax > 0)
                {
                    int before = item.Desired.DeluxscoreMax;
                    int bumped = BumpDx(r.MusicId, r.Level, before);
                    if (bumped != before)
                    {
                        Log.Info("[plan] DX 加分: " + r.MusicId + "-" + r.Level + "  " + before + " → " + bumped);
                        item.Desired.DeluxscoreMax = bumped;
                    }
                }
                if (GenerateJudgeBreakdown) AttachJudge(item.Desired);
                res.Items.Add(item);
            }

            Log.Info("plan: " + res + (existing.Count > 0 ? " (target records=" + existing.Count + ")" : ""));
            return res;
        }

        /// <summary>
        /// 覆盖模式的达成率规则：小数部分 ≥ .9000 时「镜像上取整」——
        /// 整数位 +1，小数变成 1-小数。例：97.9191% → 98.0809%。
        /// 上限 101.0000%，超出则保持原值。
        /// </summary>
        private static int MirrorAchievement(int achv)
        {
            if (achv <= 0) return achv;
            bool on;
            try { on = ModConfig.Data.AchievementMirrorUp; }
            catch (Exception) { on = false; }
            if (!on) return achv;
            int frac = achv % 10000;
            if (frac < 9000) return achv;
            int mirrored = (achv / 10000 + 1) * 10000 + (10000 - frac);
            if (mirrored > 1010000) return achv;
            return mirrored;
        }

        /// <summary>
        /// 覆盖模式的 DX 加分：源 DX + rand(DxBonusMin..DxBonusMax)，夹到谱面理论最大值
        /// (maxNotes × 3) 以内，保证成绩是「打得出来」的。
        /// </summary>
        private static int BumpDx(int musicId, int level, int dx)
        {
            try
            {
                int lo = ModConfig.Data.DxBonusMin;
                int hi = ModConfig.Data.DxBonusMax;
                if (hi <= 0) return dx;
                if (lo < 0) lo = 0;
                if (hi < lo) hi = lo;
                int bonus = lo + _dxRng.Next(hi - lo + 1);
                int result = dx + bonus;
                int maxNotes = ChartCatalog.GetMaxNotes(musicId, level);
                if (maxNotes > 0)
                {
                    int theory = maxNotes * 3;
                    if (result > theory) result = theory;
                }
                if (result < 0) result = 0;
                return result;
            }
            catch (Exception) { return dx; }
        }

        private static readonly Random _dxRng = new Random(Environment.TickCount);

        /// <summary>True when the operator blacklisted this song in config (SkipMusicIds).</summary>
        private static bool IsMusicSkipped(int musicId)
        {
            try
            {
                var list = ModConfig.Data.SkipMusicIds;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] == musicId) return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Re:MASTER 是否可玩：服务端 kind7 解禁表里必须有它，或账号已经有这首的 Re:MASTER 成绩。
        /// （实机验证：没有这两者之一时，选歌页的难度页拒accept「決定」，任务会超时崩。）
        /// </summary>
        private static bool IsReMasterPlayable(int musicId)
        {
            try
            {
                var ud = UserDataPatcher.GetLive(0);
                if (ud == null) return false;   // 计划在登录后才构建；拿不到账号数据时保守跳过
                var unlockList = ud.MusicUnlockReMasterList;
                if (unlockList != null && unlockList.Contains(musicId)) return true;
                var scores = ud.ScoreDic;
                if (scores != null && scores.Length > 4 && scores[4] != null && scores[4].ContainsKey(musicId)) return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>FakerSwap/auto-skip.txt: 选曲超时后自动追加的 musicId（重启后自动跳过）。</summary>
        private static HashSet<int> LoadAutoSkip()
        {
            var set = new HashSet<int>();
            try
            {
                string path = Path.Combine(Fs.Root, "auto-skip.txt");
                if (!File.Exists(path)) return set;
                foreach (string line in File.ReadAllLines(path))
                {
                    int id;
                    if (int.TryParse(line.Trim(), out id) && id > 0) set.Add(id);
                }
            }
            catch (Exception) { }
            return set;
        }

        /// <summary>
        /// Fill <c>Desired.Judge</c> for every item that does not have one yet (B/C modes).
        /// Safe to call multiple times; already-generated judges are kept.
        /// </summary>
        public static int EnsureJudges(PlanResult plan)
        {
            LastJudgeCount = 0;
            if (plan == null || plan.Items == null) return 0;
            int n = 0;
            for (int i = 0; i < plan.Items.Count; i++)
            {
                var item = plan.Items[i];
                if (item == null || item.Desired == null) continue;
                if (AttachJudge(item.Desired)) n++;
            }
            LastJudgeCount = n;
            Log.Info("plan: judge breakdowns " + n + "/" + plan.Items.Count);
            return n;
        }

        private static bool AttachJudge(ScoreRecord rec)
        {
            if (rec == null) return false;
            if (rec.Judge != null) return true;
            try
            {
                ChartStats stats = ChartCatalog.GetStats(rec.MusicId, rec.Level);
                if (stats == null || !stats.IsValid) return false;
                rec.Judge = ScoreMath.BuildJudgeBreakdown(rec.Achievement, stats, rec.DeluxscoreMax);
                return rec.Judge != null;
            }
            catch (Exception e)
            {
                Log.Warn("PlanBuilder: judge breakdown failed for " + rec.Key + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Convenience wrapper around the config's difficulty window / batch settings.</summary>
        public static PlanResult BuildFromConfig(SourceSnapshot source, IList<ScoreRecord> existingTarget)
        {
            int min = 2, max = 4;
            try
            {
                min = ModConfig.Data.MinLevel;
                max = ModConfig.Data.MaxLevel;
            }
            catch (Exception) { }
            return Build(source, min, max, existingTarget, true);
        }

        private static Dictionary<string, ScoreRecord> BuildExistingMap(IList<ScoreRecord> existingTarget)
        {
            var map = new Dictionary<string, ScoreRecord>(StringComparer.Ordinal);
            if (existingTarget == null) return map;
            foreach (var r in existingTarget)
            {
                if (r == null) continue;
                ScoreRecord prev;
                if (!map.TryGetValue(r.Key, out prev) || prev.Achievement < r.Achievement) map[r.Key] = r;
            }
            return map;
        }

        private static int CompareForTransfer(ScoreRecord a, ScoreRecord b)
        {
            double la = ChartCatalog.InternalLevel(a.MusicId, a.Level);
            double lb = ChartCatalog.InternalLevel(b.MusicId, b.Level);
            int c = lb.CompareTo(la);
            if (c != 0) return c;
            c = b.Level.CompareTo(a.Level);
            if (c != 0) return c;
            c = b.Achievement.CompareTo(a.Achievement);
            if (c != 0) return c;
            return a.MusicId.CompareTo(b.MusicId);
        }
    }
}
