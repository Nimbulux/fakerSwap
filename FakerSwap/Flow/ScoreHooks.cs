using System;
using System.Collections.Generic;
using FakerSwap.Core;
using FakerSwap.Data;
using HarmonyLib;
using Manager;

namespace FakerSwap.Flow
{
    /// <summary>
    /// 结算钩子：把"本轨期望成绩"应用到正在结算的这一轨，并区分 filler / 中止轨（不改成绩）。
    ///
    /// 游戏侧真正的入口是 <c>GameMonitor.ForceAchivement</c> / <c>GameScoreList.SetForceAchivement</c>，
    /// 它们把一串 note 判定生成出来、再据此算出 achievement 与 DX 分。本类做两件事：
    ///  1. <see cref="Navigation.SkipCurrentChart"/> 触发结算前，用 <see cref="ConsumeTrackScore"/>
    ///     取走期望成绩并调用游戏的 ForceAchivement（单次、可控）；
    ///  2. 打两个 Harmony Prefix 兜底：只要本轨有期望成绩，任何路径进 SetForceAchivement 都会被改写成期望值，
    ///     避免 README 里说的"换个入口就漏改"。
    ///
    /// 契约（flow-dev 用）：
    ///  - 每轨选曲前设置 <see cref="PendingForThisTrack"/> 与 <see cref="SkipCurrentTrack"/>；
    ///  - 或者调 <see cref="BeginTransfer"/> / <see cref="BeginFiller"/> / <see cref="BeginSkipUnmodified"/>；
    ///  - 本轨结束后 <see cref="EndTrack"/>（<see cref="Navigation.SkipCurrentChart"/> 会自动清一次）。
    /// </summary>
    public static class ScoreHooks
    {
        /// <summary>Records that must be applied when the current track settles (set by the flow).</summary>
        public static ScoreRecord PendingForThisTrack;

        /// <summary>When true the current track is a filler (性隐 BASIC) and must NOT be modified.</summary>
        public static bool SkipCurrentTrack;
        /// <summary>本轨强制全 MISS（filler/中止轨上传空成绩；PickMissCount 对 0% 只能降到 ~50%）。</summary>
        private static bool ForceAllMiss;

        /// <summary>
        /// 课题曲"以能通过的最低条件通过"用的成绩：80.0000%（maimai 的通关线）。
        /// 单位与 <see cref="ScoreRecord.Achievement"/> 一致（1/10000 %）。
        /// </summary>
        public static int MinClearAchievement = 800000;

        /// <summary>最近一次真正结算时应用的记录（null = filler/未改）。</summary>
        public static ScoreRecord LastSettledRecord;

        /// <summary>
        /// 是否在每轨结算后把期望成绩写回 <c>UserData</c>（data-dev 的 per-track `Commit`）。
        /// 与 data-dev / flow-dev 约定：**per-track 写**，不用 CommitBatch、不等 credit 结束批量写。
        /// </summary>
        public static bool AutoCommitToUserData = true;

        /// <summary>本轨最后一次 Commit 的返回值（true = 内存或报文至少一路接受，见 data-dev 说明）。</summary>
        public static bool LastCommitOk;

        /// <summary>本轨最后一次 Commit 真正写进内存的行数（0 且 LastCommitQueuedPayload=true 表示只走了报文路径）。</summary>
        public static int LastCommitMemoryTouched = -1;

        /// <summary>本轨最后一次 Commit 是否已排队 PayloadRewriter 兜底。</summary>
        public static bool LastCommitQueuedPayload;

        /// <summary>本轨 Commit 是否已经做过（防止 OnStart + 重试重复写）。</summary>
        private static bool _committedThisTrack;

        /// <summary>本轨 Commit 遇到"任何写路径都不接受"后停止重试（如只读闸门/写前备份失败/游客账号）。</summary>
        private static bool _commitAborted;

        private static bool _forceDisableScoreWrites;
        private static bool _readOnlyScoreLogged;

        /// <summary>
        /// 总开关：为 true 时**连游戏内强制成绩（ForceAchivement）都不写**，本 credit 完全不改任何成绩。
        ///
        /// 默认绑定只读闸门：`Core.WriteGuard.WritesAllowed == false`（`Config.SafeNoWrite=true`）时自动为 true，
        /// 因为 ForceAchivement 改的是游戏内存里的结算成绩，最终会经游戏原生 DataSave 上传。
        /// 手动 set true 会强制关掉（即使闸门允许写）；set false 不会绕过只读闸门。
        /// </summary>
        public static bool DisableAllScoreWrites
        {
            get { return _forceDisableScoreWrites || !FakerSwap.Core.WriteGuard.WritesAllowed; }
            set { _forceDisableScoreWrites = value; }
        }

        /// <summary>只读/停写模式：每 credit 只打一条 INFO。</summary>
        public static void NoteReadOnlyOnce()
        {
            if (_readOnlyScoreLogged) return;
            _readOnlyScoreLogged = true;
            Log.Info("[score] 只读模式 (WriteGuard.SafeNoWrite=true / DisableAllScoreWrites) → " +
                     "本 credit 不改任何成绩（不 ForceAchivement、不入 UserData）");
        }

        /// <summary>本轨要提交的记录快照（提前抓，避免 flow 的 EndTrack 先清掉 PendingForThisTrack）。</summary>
        private static ScoreRecord _commitCandidate;

        /// <summary>本轨结算时实际读回的 achievement（1/10000 %），用于校验/日志。</summary>
        public static int LastSettledAchievement = -1;

        /// <summary>本轨是不是 filler / 不改成绩的轨。</summary>
        public static bool LastTrackWasUntouched;

        /// <summary>
        /// ForceAchivement 入参的 achievement 单位。实机校准用（改 config 即可，不用改代码）：
        ///  - TenThousandth：1/10000 %（1005000 = 100.5%），与 UserMusicDetail.achievement 一致（默认）
        ///  - Percent100   ：整数百分比（100 = 100%，101 = 101%）
        ///  - Raw          ：ScoreRecord.Achievement 原样传入（不换算）
        /// config.json 里的键名：<c>ForceAchievementUnit</c>（加了就自动读，没加也不影响）。
        /// </summary>
        public static AchievementUnit ForceUnit = AchievementUnit.TenThousandth;

        public enum AchievementUnit
        {
            TenThousandth = 0,
            Percent100 = 1,
            Raw = 2
        }

        /// <summary>把 1/10000 % 的目标值换算成 ForceAchivement 的入参。</summary>
        public static int ConvertForForce(int achievementTenThousandth)
        {
            switch (ForceUnit)
            {
                case AchievementUnit.Percent100:
                    return achievementTenThousandth / 10000;
                case AchievementUnit.Raw:
                default:
                    return achievementTenThousandth;
            }
        }

        /// <summary>日志格式：raw 原值 + 按 1/10000 换算的百分比。</summary>
        public static string AchievementText(int raw)
        {
            return "raw=" + raw + " (" + (raw / 10000.0).ToString("0.0000") + "%)";
        }

        /// <summary>从 config 里读 ForceAchievementUnit（Core 是 Lead 的文件，用反射读，没加字段就跳过）。</summary>
        public static void LoadFromConfig()
        {
            try
            {
                var value = NavReflect.Get(ModConfig.Data, "ForceAchievementUnit") as string;
                if (string.IsNullOrEmpty(value)) return;
                SetForceUnit(value);
            }
            catch (Exception) { }
        }

        /// <summary>按名字设置单位（容错：大小写/常见写法都认）。</summary>
        public static bool SetForceUnit(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.Trim().ToLowerInvariant();
            if (n == "percent100" || n == "percent" || n == "100" || n == "percent_int")
            {
                ForceUnit = AchievementUnit.Percent100;
                Log.Info("[score] ForceAchivement 单位 = Percent100（整数百分比）");
                return true;
            }
            if (n == "raw" || n == "asis" || n == "as-is")
            {
                ForceUnit = AchievementUnit.Raw;
                Log.Info("[score] ForceAchivement 单位 = Raw（原始值）");
                return true;
            }
            if (n == "tenthousandth" || n == "10000" || n == "unit" || n == "default")
            {
                ForceUnit = AchievementUnit.TenThousandth;
                Log.Info("[score] ForceAchivement 单位 = TenThousandth（1/10000 %）");
                return true;
            }
            Log.Warn("[score] 未知的 ForceAchievementUnit: " + name + "（可选 TenThousandth / Percent100 / Raw）");
            return false;
        }

        /// <summary>调试：把每次 SetForceAchivement 的入口参数打出来（实机校准单位时打开）。</summary>
        public static bool TraceForceAchievement;

        private static bool _installed;
        private static HarmonyLib.Harmony _harmony;
        private static bool _consumed;

        /// <summary>日志用：两个 ForceAchivement 入口被调用的次数（确认 patch 是否真的挂上）。</summary>
        private static int _forceCalledCount;
        private static int _setForceCalledCount;

        /// <summary>本轨是否已经做过"回读不一致 → 就地重建判定表"的修复（每轨一次）。</summary>
        private static bool _repairAttemptedThisTrack;

        /// <summary>回读日志去重键（`monitor:track` / `filler:monitor:track`），避免同一帧刷上百条。</summary>
        private static string _lastSettleKey;

        public static bool HasTrackScore
        {
            get { return !DisableAllScoreWrites && PendingForThisTrack != null && !SkipCurrentTrack; }
        }

        // ---------------------------------------------------------------- flow 用的设置接口

        /// <summary>本轨要应用一份期望成绩（来自 data-dev 的 ScoreRecord）。</summary>
        public static void BeginTransfer(ScoreRecord record)
        {
            PendingForThisTrack = record;
            SkipCurrentTrack = false;
            _consumed = false;
            _committedThisTrack = false;
            _commitAborted = false;
            _commitCandidate = record;
            LastCommitOk = false;
            LastSettledRecord = null;
            LastTrackWasUntouched = false;
            LastSettledAchievement = -1;
            _forceCalledCount = 0;
            _setForceCalledCount = 0;
            _repairAttemptedThisTrack = false;
            _lastSettleKey = null;
        }

        /// <summary>本轨是 filler（性隐 BASIC）：跳过且绝对不改成绩。</summary>
        public static void BeginFiller()
        {
            PendingForThisTrack = null;
            SkipCurrentTrack = true;
            _consumed = false;
            _committedThisTrack = false;
            _commitAborted = false;
            _commitCandidate = null;
            LastSettledRecord = null;
            LastSettledAchievement = -1;
        }

        /// <summary>本轨中止：跳过且不改成绩。</summary>
        public static void BeginSkipUnmodified()
        {
            PendingForThisTrack = null;
            SkipCurrentTrack = true;
            _consumed = false;
            _committedThisTrack = false;
            _commitAborted = false;
            _commitCandidate = null;
            LastSettledRecord = null;
            LastSettledAchievement = -1;
        }

        /// <summary>课题曲：用"能通过的最低条件"（<see cref="MinClearAchievement"/>）通过。</summary>
        public static void BeginMission(ScoreRecord record)
        {
            if (record == null)
            {
                BeginFiller();
                return;
            }
            var mission = record.Clone();
            if (mission.Achievement < MinClearAchievement) mission.Achievement = MinClearAchievement;
            // 只要求“通过”，combo/sync 状态交给游戏按这个 achievement 自己算，不要伪造 FC/AP。
            BeginTransfer(mission);
        }

        /// <summary>本轨结束，清状态（提交候选保留到 Result 钩子写完 UserData）。</summary>
        public static void EndTrack()
        {
            if (PendingForThisTrack != null) LastSettledRecord = PendingForThisTrack;
            LastTrackWasUntouched = SkipCurrentTrack || PendingForThisTrack == null;
            PendingForThisTrack = null;
            SkipCurrentTrack = false;
            _consumed = false;
        }

        /// <summary>新 credit 开始 / 中止收尾时清掉所有累积状态（flow 每批开始时调一次）。</summary>
        public static void ResetForCredit()
        {
            PendingForThisTrack = null;
            SkipCurrentTrack = false;
            _consumed = false;
            _committedThisTrack = false;
            _commitAborted = false;
            _commitCandidate = null;
            _readOnlyScoreLogged = false;   // 新 credit 允许再打一条只读模式日志
            LastCommitOk = false;
            LastSettledRecord = null;
            LastSettledAchievement = -1;
            LastTrackWasUntouched = false;
            _forceCalledCount = 0;
            _setForceCalledCount = 0;
            _repairAttemptedThisTrack = false;
            _lastSettleKey = null;
        }

        /// <summary>
        /// 取走本轨期望成绩（单次）。返回 true 时 <paramref name="achievement"/> 是 1/10000 % 单位。
        /// 取走后再调不会重复返回。
        /// </summary>
        public static bool ConsumeTrackScore(out int achievement, out int dxScore)
        {
            achievement = 0;
            dxScore = 0;
            if (DisableAllScoreWrites)
            {
                NoteReadOnlyOnce();
                return false;
            }
            if (_consumed) return false;
            if (PendingForThisTrack == null || SkipCurrentTrack) return false;
            _consumed = true;
            achievement = PendingForThisTrack.Achievement;
            dxScore = PendingForThisTrack.DeluxscoreMax;
            if (achievement < 0) achievement = 0;
            if (achievement > 1010000) achievement = 1010000;
            if (dxScore < 0) dxScore = 0;

            // 抓一份候选：即使 flow 在 Result 阶段先 EndTrack 清空了 PendingForThisTrack，
            // 我们的 Result 钩子仍然拿得到要提交的记录。
            _commitCandidate = PendingForThisTrack;
            return true;
        }

        // ---------------------------------------------------------------- 写回 UserData（per-track）

        /// <summary>
        /// 在 Result 阶段把本轨期望成绩写回 UserData。由 <c>Process.ResultProcess.OnStart</c> 触发，
        /// 延迟 0.25s 并在 0.8s / 1.8s 重试，确保落在"游戏写完 ScoreDic 之后、上传之前"。
        /// filler / 中止轨（<see cref="SkipCurrentTrack"/> 或没有候选）直接跳过。
        /// </summary>
        internal static void OnResultProcessStarted()
        {
            var candidate = _commitCandidate;
            if (candidate == null || SkipCurrentTrack) return;
            if (!AutoCommitToUserData)
            {
                NavReflect.WarnOnce("commit-disabled", "AutoCommitToUserData=false，跳过写回 UserData（仅依赖报文改写）");
                return;
            }
            // 只读闸门（Lead 的 Core.WriteGuard，SafeNoWrite=true 时默认拒绝一切账号写入）：
            // 只打一条日志、不排队重试，免得每轨刷 3 条 Commit 失败警告。
            if (DisableAllScoreWrites)
            {
                NoteReadOnlyOnce();
                return;
            }
            // 结果页刚出现时游戏可能还在写 UserData，延迟一点更稳；多次重试是幂等的（_committedThisTrack）。
            // 另外先做一次"结算补写"：C 模式（不调 SkipCurrentChart、挂机自然结算）没有 ForceAchivement 入口，
            // AFK 打出来的真实判定会是全 MISS(0%) —— 这里在 Result 阶段把判定表补写成目标值，
            // 保证结算页/playlog 读到的不是 0%。
            ForceApplyToCurrentScores(candidate);
            Scheduler.After(0.25, () => CommitTrackScore(candidate, "Result+0.25s"), "fakerswap.score.commit");
            Scheduler.After(0.8, () => CommitTrackScore(candidate, "Result+0.8s"), "fakerswap.score.commit");
            Scheduler.After(1.8, () => CommitTrackScore(candidate, "Result+1.8s"), "fakerswap.score.commit");
        }

        /// <summary>
        /// C 模式兜底：Result 阶段如果当前轨的 GameScoreList.Achivement 与目标不符（AFK 自然结算 = 全 MISS），
        /// 就地把它重建为目标判定表（幂等；失败只 warn，不影响后续 Commit）。
        /// </summary>
        private static void ForceApplyToCurrentScores(ScoreRecord candidate)
        {
            if (candidate == null) return;
            if (DisableAllScoreWrites) { NoteReadOnlyOnce(); return; }
            try
            {
                var playManager = MAI2.Util.Singleton<GamePlayManager>.Instance;
                if (playManager == null) return;
                int trackNo = Math.Max(0, (int)GameManager.MusicTrackNumber - 1);
                for (int i = 0; i < 2; i++)
                {
                    var score = TryGetGameScore(i, trackNo);
                    if (score == null || !score.IsEnable) continue;
                    int actual = (int)playManager.GetAchivement(i, trackNo);
                    int expected = candidate.Achievement;
                    int diff = actual - expected;
                    if (diff < 0) diff = -diff;
                    if (diff <= 100) continue;

                    bool ok = RewriteSetForceAchivement(score, candidate);
                    int after = (int)playManager.GetAchivement(i, trackNo);
                    Log.Warn("[score] Result 阶段补写判定表: monitor=" + i + " ok=" + ok +
                             " before=" + AchievementText(actual) + " after=" + AchievementText(after) +
                             " (C 模式自然结算兜底)");
                }
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("result-force-apply", "[score] Result 阶段补写判定表失败: " + e.Message);
            }
        }

        /// <summary>把一条期望成绩写回当前登录玩家的 UserData（per-track，data-dev 的 Commit）。</summary>
        public static bool CommitTrackScore(ScoreRecord record, string reason)
        {
            if (record == null) return false;
            if (_committedThisTrack) return true;
            if (_commitAborted) return false;
            if (SkipCurrentTrack) return false;

            // 只读模式：不写、不重试（Lead 的 Core.WriteGuard 闸门 / DisableAllScoreWrites）
            if (DisableAllScoreWrites)
            {
                NoteReadOnlyOnce();
                LastCommitOk = false;
                LastCommitMemoryTouched = -1;
                LastCommitQueuedPayload = false;
                _committedThisTrack = true; // 本轨不再重试
                return false;
            }

            int monitor = PickPlayerMonitor();
            if (monitor < 0)
            {
                NavReflect.WarnOnce("commit-nomonitor", "提交成绩时没有已登录玩家，跳过内存写入（报文兜底仍生效）");
                return false;
            }
            try
            {
                bool ok = UserDataPatcher.Commit(monitor, record);
                LastCommitOk = ok;
                // data-dev 的两个诊断字段（static 属性；版本不匹配时读不到就是 -1/false，不影响功能）
                var memValue = NavReflect.GetStaticValue(typeof(UserDataPatcher), "LastCommitMemoryTouched");
                var payloadValue = NavReflect.GetStaticValue(typeof(UserDataPatcher), "LastCommitQueuedPayload");
                LastCommitMemoryTouched = memValue == null ? -1 : NavReflect.ToInt(memValue);
                LastCommitQueuedPayload = payloadValue != null && NavReflect.ToBool(payloadValue);

                if (ok)
                {
                    _committedThisTrack = true;
                    Log.Info("[score] 成绩写回 UserData(" + reason + "): monitor=" + monitor +
                             " 内存touched=" + LastCommitMemoryTouched +
                             " 报文排队=" + LastCommitQueuedPayload + " " + record);
                }
                else
                {
                    Log.Warn("[score] UserDataPatcher.Commit 返回 false (" + reason + "): monitor=" + monitor +
                             " 内存touched=" + LastCommitMemoryTouched +
                             " 报文排队=" + LastCommitQueuedPayload + " " + record + "（下一轮重试）");

                    // 内存/报文都没接受 → 多半是只读闸门/写前备份失败/游客/越界，重试无意义（data-dev 要求不要重试）
                    if (LastCommitMemoryTouched == 0 && !LastCommitQueuedPayload)
                    {
                        _commitAborted = true;
                        Log.Warn("[score] Commit 未接受任何写路径（内存=0 且报文未排队）→ 停止本轨重试；" +
                                 "若是 PRE-WRITE BACKUP FAILED/只读模式，等 Lead 处理后再跑");
                    }
                }
                return ok;
            }
            catch (Exception e)
            {
                Log.Error("[score] UserDataPatcher.Commit 异常 (" + reason + ")", e);
                return false;
            }
        }

        private static int PickPlayerMonitor()
        {
            try
            {
                var manager = MAI2.Util.Singleton<UserDataManager>.Instance;
                if (manager == null) return -1;
                for (int i = 0; i < 2; i++)
                {
                    var userData = manager.GetUserData(i);
                    if (userData != null && userData.IsEntry) return i;
                }
            }
            catch (Exception) { }
            return -1;
        }

        // ---------------------------------------------------------------- 判定表重建（B 级：像真人打的）

        /// <summary>
        /// 按目标成绩重建整轨判定表 —— **"像真人打的"分布版**（lead B 级要求）：
        ///  - 由目标 achievement 先定 miss 数（97%+ → 0~1，95%~97% → 1~3，90%~95% → 3~8 …），绝不出现"9 miss 还 97%"；
        ///  - miss 均匀散布在整轨（不是全塞末尾），maxCombo 由实际最长连续非 miss 段算出；
        ///  - 其余 note 先全 CP，再按 round-robin 把 loss 均摊成 Perfect/Great/Good，分布自然、不会大量 TooFast；
        ///  - sync/combo：无 miss 且全 CP → SyncPlay(=FullSyncPlus) / AllPerfectPlus；无 miss → SyncHi(=FullSync) / Gold；有 miss → None；
        ///  - 最后把"目标 achievement"直接写进 `GameScoreList.<Achivement>k__BackingField`（Decimal 百分比），
        ///    并回写 CP/Perfect/Great/Good/Miss/MaxCombo 计数，保证结算回读 == 目标（±0.0001%）。
        /// 返回 true = 已重建，调用方跳过原版。
        /// </summary>
        private static bool RewriteSetForceAchivement(GameScoreList score, ScoreRecord pending)
        {
            if (score == null || pending == null) return false;
            int monitorIndex = NavReflect.ToInt(NavReflect.Get(score, "_monitorIndex"));
            if (monitorIndex < 0 || monitorIndex > 1) return false;
            if (!score.IsEnable) return false;

            var notesManager = NotesManager.Instance(monitorIndex);
            if (notesManager == null) return false;
            var reader = notesManager.getReader();
            if (reader == null) return false;
            var noteList = reader.GetNoteList();
            if (noteList == null) return false;
            var enumerable = noteList as System.Collections.IEnumerable;
            if (enumerable == null) return false;

            var total = score.ScoreTotal;
            if (total == null) return false;

            // ---- 1) 收集单位（跳过 connect slide；End 单独记，参与 SetResult 但不参与计分/combo） ----
            var unitIndex = new List<int>();
            var unitType = new List<NoteScore.EScoreType>();
            var unitMax = new List<int>();
            var unitTouchHold = new List<bool>();
            var unitDef = new List<Manager.NotesTypeID.Def>();
            var endIndex = new List<int>();
            var judged = new HashSet<string>();

            foreach (object obj in enumerable)
            {
                var note = obj as NoteData;
                if (note == null) continue;
                Manager.NotesTypeID.Def def;
                try { def = note.type.getEnum(); }
                catch (Exception) { continue; }
                if (Manager.NotesTypeID_Extension.isConnectSlide(def))
                {
                    note.isJudged = true;
                    continue;
                }
                if (note.indexNote < 0) continue;
                var scoreType = GamePlayManager.NoteType2ScoreType(note.type);
                if (!judged.Add(note.indexNote + "_" + (int)scoreType)) continue;
                if (scoreType == NoteScore.EScoreType.End)
                {
                    endIndex.Add(note.indexNote);
                    continue;
                }
                int max = (int)NoteScore.GetJudgeScore(NoteJudge.ETiming.Critical, scoreType);
                if (scoreType == NoteScore.EScoreType.Break)
                    max += (int)NoteScore.GetJudgeScore(NoteJudge.ETiming.Critical, NoteScore.EScoreType.BreakBonus);
                unitIndex.Add(note.indexNote);
                unitType.Add(scoreType);
                unitMax.Add(max);
                // Touch 有两种（TouchTap/TouchHold），游戏都映射成 EScoreType.Touch —— 单独记一份，
                // 分配时 TouchHold 并入 Hold 池、TouchTap 并入 Tap 池（与 data-dev 的 .ma2 权重一致）。
                unitTouchHold.Add(def == Manager.NotesTypeID.Def.TouchHold);
                unitDef.Add(def);
            }
            int n = unitIndex.Count;
            if (n == 0) return false;

            long maxSum = 0;
            for (int i = 0; i < n; i++) maxSum += unitMax[i];
            if (maxSum <= 0) return false;

            // ---- 2) 目标 & 随机源（固定 seed，同一 track 结果可复现） ----
            decimal targetPercent = pending.Achievement / 10000m;
            if (targetPercent < 0m) targetPercent = 0m;
            if (targetPercent > 101m) targetPercent = 101m;
            var rng = new Random(unchecked(pending.MusicId * 397 ^ pending.Level * 131 ^ n * 17 ^ monitorIndex * 7));

            int missCount;

            // ---- 源账号的 FC/AP 标记（DB.PlayComboflagID: 0=None 1=Silver(FC) 2=Gold(FC+) 3=AP 4=AP+）----
            // 判定必须跟源一致：源里没 FC 的歌，这里就必须真的产生 MISS，
            // 否则游戏的连击/徽章会把它们算成 FC（用户反馈的就是这个问题）。
            int srcCombo = pending.ComboStatus;
            if (srcCombo < 0 || srcCombo > 4) srcCombo = -1;          // 非法 → 由判定自行推算
            bool forceMiss = srcCombo == 0                              // 源明确「没有 FC」
                             && pending.Achievement < 1000000            // 100%+ 必然是 AP，不可能有 MISS
                             && pending.AllowImperfect != 0;             // 挑战曲命数不够时不硬塞 MISS

            var judge = pending.Judge;
            if (judge != null && judge.TotalNotes > 0)
            {
                // 用 data-dev 的 `ScoreMath.BuildJudgeBreakdown` 分布（playlog/PlanBuilder 同一份数据），
                // 按本轨实际 note 数等比缩放；CP/Perfect/Great/Good 的比例仍由下面的 loss 分配自然产生。
                double scale = (double)n / judge.TotalNotes;
                missCount = (int)Math.Round(judge.Miss * scale);
                if (missCount < 0) missCount = 0;
                int cap = Math.Max(1, n / 4);
                if (missCount > cap) missCount = cap;
                if (!forceMiss && (targetPercent >= 100m || pending.NoMiss)) missCount = 0;   // 挑战曲限命：零 MISS
                Log.Info("[score] 采用 data-dev 判定分布: total=" + judge.TotalNotes +
                         " cp=" + judge.CriticalPerfect + " perfect=" + judge.Perfect +
                         " great=" + judge.Great + " good=" + judge.Good + " miss=" + judge.Miss +
                         " maxCombo=" + judge.MaxCombo + " sync=" + judge.SyncStatus +
                         " → 本轨 notes=" + n + " miss=" + missCount);
            }
            else
            {
                missCount = PickMissCount(targetPercent, n, rng);
                if (!forceMiss && (targetPercent >= 100m || pending.NoMiss)) missCount = 0;
            }

            // 源没 FC → 至少 1 个 MISS，保证结算的 Silver/Gold 徽章与源一致
            if (ForceAllMiss) missCount = n;   // filler/中止：全 MISS
            if (forceMiss && missCount < 1) missCount = 1;
            if (forceMiss && missCount > 0)
                Log.Info("[score] 源标记=未FC(comboStatus=" + pending.ComboStatus + ") → 强制 " + missCount + " 个 MISS");
            else if (srcCombo >= 1)
                Log.Info("[score] 源标记=comboStatus=" + pending.ComboStatus + "（已 FC/AP）→ MISS=" + missCount);

            // ---- 3) miss 均匀散布 ----
            var isMiss = new bool[n];
            if (missCount > 0)
            {
                double stride = (double)n / (missCount + 1);
                for (int k = 1; k <= missCount; k++)
                {
                    int pos = (int)(stride * k);
                    if (pos >= n) pos = n - 1;
                    int guard = 0;
                    while (isMiss[pos] && guard++ < n) pos = (pos + 1) % n;
                    isMiss[pos] = true;
                }
            }

            // ---- 4) 判定等级分配 ----
            // 优先：data-dev 求解器的 per-type 计数（Tap/Hold/Slide/Break 各 5 档，精确满足成就 + DX）；
            // 兜底：全 CP 起步，按 loss 需求 round-robin 降级（0=CP, 1=Perfect, 2=Great, 3=Good, -1=miss）。
            var level = new int[n];
            bool perTypeAssigned = !ModConfig.Data.RealisticJudgeSpread && judge != null && TryAssignPerTypeCounts(judge, unitType, unitTouchHold, level, n);
            long cpSum = 0;
            if (!perTypeAssigned)
            {
                for (int i = 0; i < n; i++)
                {
                    if (isMiss[i]) { level[i] = -1; continue; }
                    level[i] = 0;
                    cpSum += unitMax[i];
                }
            }
            long targetSum = (long)((decimal)maxSum * targetPercent / 100m);
            long lossNeeded = perTypeAssigned ? 0 : (cpSum - targetSum);
            if (lossNeeded < 0) lossNeeded = 0;

            if (lossNeeded > 0)
            {
                int[] order = new int[n];
                for (int i = 0; i < n; i++) order[i] = i;
                for (int i = n - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    int tmp = order[i]; order[i] = order[j]; order[j] = tmp;
                }
                bool changed = true;
                int rounds = 0;
                while (lossNeeded > 0 && changed && rounds++ < 16)
                {
                    changed = false;
                    for (int oi = 0; oi < n && lossNeeded > 0; oi++)
                    {
                        int i = order[oi];
                        if (level[i] < 0 || level[i] >= 3) continue;
                        int loss = StepLoss(unitType[i], level[i]);
                        if (loss <= 0) continue;
                        if (loss <= lossNeeded)
                        {
                            level[i]++;
                            lossNeeded -= loss;
                            changed = true;
                        }
                    }
                }
            }

            // ---- 4.2) 音符类型 × 判定表约束（用户实测矩阵 docs/judge-matrix.md）----
            // Ex* = 「保护」音符，只能 大P 或 MISS；星星身子不会出 小P；星星头与普通 tap 同组。
            // 关键前提：CP 与小P 分数完全相同（ScoreMath: critical/perfect 同为满值），
            // 所以「小P → 大P」是零代价修复，不影响成就。
            var unitCls = new int[n];
            for (int i = 0; i < n; i++) unitCls[i] = JudgeClassOf(unitDef[i]);
            {
                int cTap = 0, cHold = 0, cProTap = 0, cProHold = 0, cStar = 0, cProStar = 0, cSlideB = 0, cTT = 0, cTH = 0, cBrk = 0;
                for (int i = 0; i < n; i++)
                {
                    switch (unitCls[i])
                    {
                        case JC_Tap: cTap++; break;
                        case JC_Hold: cHold++; break;
                        case JC_ProtectTap: cProTap++; break;
                        case JC_ProtectHold: cProHold++; break;
                        case JC_StarHead: cStar++; break;
                        case JC_ProtectStarHead: cProStar++; break;
                        case JC_SlideBody: cSlideB++; break;
                        case JC_TouchTap: cTT++; break;
                        case JC_TouchHold: cTH++; break;
                        default: cBrk++; break;
                    }
                }
                Log.Info("[score] 音符类型: tap=" + cTap + " hold=" + cHold + " 保tap=" + cProTap + " 保hold=" + cProHold +
                         " 星星头=" + cStar + " 保星头=" + cProStar + " 星星身子=" + cSlideB +
                         " touch=" + cTT + " touchHold=" + cTH + " 绝赞=" + cBrk);
            }

            int repaired = 0;
            for (int i = 0; i < n; i++)
            {
                if (level[i] < 0) continue;
                int mask = AllowedMask(unitCls[i]);
                if ((mask & (1 << level[i])) != 0) continue;
                level[i] = NearestAllowedLevel(mask, level[i]);
                repaired++;
            }
            if (repaired > 0)
                Log.Info("[score] 判定表约束修复: " + repaired + " 个音符按类型改判（保护/星星身子/touch）");

            // ---- 4.3) CP : 小P ≈ 1:1（用户要求；两者同分，互换零代价）----
            // touch 与 保护tap/保护星星头/星星身子 不参与小P（真实 play 里这些音符几乎不出小P）。
            var rebalEligible = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (level[i] != 0 && level[i] != 1) continue;
                if (unitCls[i] == JC_TouchTap || unitCls[i] == JC_TouchHold) continue;
                int mask = AllowedMask(unitCls[i]);
                if ((mask & 0b0011) != 0b0011) continue;   // 必须同时允许 大P 和 小P
                rebalEligible.Add(i);
            }
            if (rebalEligible.Count >= 4)
            {
                int targetPerfect = rebalEligible.Count / 2;
                int curPerfect = 0;
                for (int k = 0; k < rebalEligible.Count; k++) if (level[rebalEligible[k]] == 1) curPerfect++;
                int moved = 0;
                if (curPerfect < targetPerfect)
                {
                    int need = targetPerfect - curPerfect;
                    for (int k = 0; k < rebalEligible.Count && need > 0; k++)
                    {
                        int i = rebalEligible[k];
                        if (level[i] != 0) continue;
                        level[i] = 1; need--; moved++;
                    }
                }
                else if (curPerfect > targetPerfect)
                {
                    int need = curPerfect - targetPerfect;
                    for (int k = 0; k < rebalEligible.Count && need > 0; k++)
                    {
                        int i = rebalEligible[k];
                        if (level[i] != 1) continue;
                        level[i] = 0; need--; moved++;
                    }
                }
                Log.Info("[score] CP:小P 再平衡: 可参与=" + rebalEligible.Count + " 目标小P=" + targetPerfect +
                         " 原小P=" + curPerfect + " 调整=" + moved);
            }

            // ---- 4.4) 约束修复后的残差补偿（只在允许的档位里动，保证成就仍贴合目标）----
            {
                long curSum = 0;
                for (int i = 0; i < n; i++)
                {
                    if (level[i] < 0) continue;
                    curSum += UnitValue(unitType[i], TimingForLevel(unitType[i], level[i], i));
                }
                for (int iter = 0; iter < 30; iter++)
                {
                    decimal fitted = (decimal)curSum * 100m / maxSum;
                    decimal err = fitted - targetPercent;
                    if (Math.Abs(err) <= 0.00005m) break;
                    bool wantBetter = err < 0m;
                    int bestUnit = -1, bestNewLevel = 0;
                    long bestDelta = 0;
                    double bestErr = double.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        // 源未 FC：绝不允许把强制塞进去的 MISS 又抬回去（否则徽章会变回 FC）
                        if (level[i] < 0 && forceMiss) continue;
                        int mask = AllowedMask(unitCls[i]);
                        int nl = level[i] < 0 ? (wantBetter ? 3 : -1) : level[i] + (wantBetter ? -1 : 1);
                        if (nl < -1 || nl > 3) continue;
                        if (nl >= 0 && (mask & (1 << nl)) == 0) continue;
                        if (nl == level[i]) continue;
                        long cur = level[i] < 0 ? 0L : UnitValue(unitType[i], TimingForLevel(unitType[i], level[i], i));
                        long nxt = nl < 0 ? 0L : UnitValue(unitType[i], TimingForLevel(unitType[i], nl, i));
                        long delta = nxt - cur;
                        double newErr = Math.Abs((double)((decimal)(curSum + delta) * 100m / maxSum - targetPercent));
                        if (newErr < bestErr) { bestErr = newErr; bestUnit = i; bestNewLevel = nl; bestDelta = delta; }
                    }
                    if (bestUnit < 0) break;
                    level[bestUnit] = bestNewLevel;
                    curSum += bestDelta;
                }
            }

            // ---- 4.5) 挑战曲限命：非 Perfect 判定数必须 <= 生命数-1 ----
            // 扣命规则（maimai Wiki / Perfect Challenge）: 每个非 Perfect（Great/Good/Miss）扣 1 命，
            // 生命归零立刻结束。  CP 和 Perfect 都不扣，所以这里只把超出的判定提升成 Perfect。
            if (pending.AllowImperfect >= 0)
            {
                int allowed = pending.AllowImperfect;
                int imperfect = 0;
                for (int i = 0; i < n; i++) if (level[i] < 0 || level[i] >= 2) imperfect++;
                int promoted = 0;
                for (int i = 0; i < n && imperfect > allowed; i++)
                {
                    if (level[i] == 0 || level[i] == 1) continue;
                    // 提升成「允许的最优档位」：优先 小P，其次 大P（保护类只允许大P）
                    int m = AllowedMask(unitCls[i]);
                    if ((m & 0b0010) != 0) level[i] = 1;
                    else if ((m & 0b0001) != 0) level[i] = 0;
                    else continue;
                    imperfect--;
                    promoted++;
                }
                if (promoted > 0)
                    Log.Info("[score] 挑战曲限命: 生命=" + (allowed + 1) + " → 允许非Perfect=" + allowed +
                             ", 提升 " + promoted + " 个判定, 剩余非Perfect=" + imperfect);
            }

            // ---- 4.6) 源未 FC 时补回 MISS（4.5 的限命提升可能把唯一的 MISS 改成 Perfect）----
            if (forceMiss && n > 0)
            {
                bool hasMiss = false;
                for (int i = 0; i < n; i++) if (level[i] < 0) { hasMiss = true; break; }
                if (!hasMiss)
                {
                    int pick = rng.Next(n);
                    level[pick] = -1;
                    Log.Info("[score] 源未 FC：限命提升吃掉了 MISS，已在 #" + pick + " 重新置 MISS");
                }
            }

            // ---- 5) 分配 timing / 统计 / 记分 ----
            var unitTiming = new NoteJudge.ETiming[n];
            int cp = 0, perfect = 0, great = 0, good = 0, miss = 0;
            long scoreSum = 0;
            for (int i = 0; i < n; i++)
            {
                if (level[i] < 0) { unitTiming[i] = NoteJudge.ETiming.TooFast; miss++; continue; }
                var timing = TimingForLevel(unitType[i], level[i], i);
                unitTiming[i] = timing;
                scoreSum += UnitValue(unitType[i], timing);
                switch (level[i])
                {
                    case 0: cp++; break;
                    case 1: perfect++; break;
                    case 2: great++; break;
                    default: good++; break;
                }
            }
            Log.Info("[score] 判定分布: CP=" + cp + " 小P=" + perfect + " GREAT=" + great + " GOOD=" + good +
                     " MISS=" + miss + " total=" + n + " 拟合=" + ((decimal)scoreSum * 100m / maxSum).ToString("0.0000") +
                     "% 目标=" + targetPercent.ToString("0.0000") + "% DX=" + DxPercent(cp, perfect, great, n).ToString("0.00") + "%");

            // ---- 6) 残差微调（最多 20 次，把拟合误差压到 0.0001%）----
            // per-type 计数来自 data-dev 求解器（已精确满足成就 + DX），不再微调以免破坏 DX。
            if (!perTypeAssigned)
            for (int iter = 0; iter < 20; iter++)
            {
                decimal fitted = (decimal)scoreSum * 100m / maxSum;
                decimal err = fitted - targetPercent;
                if (Math.Abs(err) <= 0.00005m) break;

                bool wantBetter = err < 0m;
                int bestUnit = -1;
                double bestErr = double.MaxValue;
                long bestDelta = 0;
                for (int i = 0; i < n; i++)
                {
                    if (level[i] < 0) continue;
                    if (wantBetter && level[i] == 0) continue;
                    if (!wantBetter && level[i] >= 3) continue;
                    // 判定表约束：只能在允许的档位里微调（保护/星星身子 不能随便降级）
                    if ((AllowedMask(unitCls[i]) & (1 << (level[i] + (wantBetter ? -1 : 1)))) == 0) continue;
                    var st = unitType[i];
                    var curTiming = unitTiming[i];
                    var altTiming = TimingForLevel(st, wantBetter ? level[i] - 1 : level[i] + 1, i);
                    long delta = (long)UnitValue(st, altTiming) - UnitValue(st, curTiming);
                    double newErr = Math.Abs((double)((decimal)(scoreSum + delta) * 100m / maxSum - targetPercent));
                    if (newErr < bestErr) { bestErr = newErr; bestUnit = i; bestDelta = delta; }
                }
                if (bestUnit < 0) break;

                int newLevel = level[bestUnit] + (wantBetter ? -1 : 1);
                if (newLevel < 0 || newLevel > 3) break;

                switch (level[bestUnit])
                {
                    case 0: cp--; break;
                    case 1: perfect--; break;
                    case 2: great--; break;
                    default: good--; break;
                }
                switch (newLevel)
                {
                    case 0: cp++; break;
                    case 1: perfect++; break;
                    case 2: great++; break;
                    default: good++; break;
                }
                level[bestUnit] = newLevel;
                unitTiming[bestUnit] = TimingForLevel(unitType[bestUnit], newLevel, bestUnit);
                scoreSum += bestDelta;
            }

            // ---- 6.5) DX 约束（用户: 不要刷出 4 星；DX = 3*CP + 2*Perfect + Great，max = 3*notes） ----
            // data-dev 的求解器已把源 deluxscoreMax 纳入约束：优先用 Judge 的 DxTargetPercent/DeluxscoreMax。
            double targetDxPercent = -1.0;
            string dxSourceTag = "none";
            if (judge != null && judge.DxTargetPercent > 0.0)
            {
                targetDxPercent = judge.DxTargetPercent;
                dxSourceTag = "judge.DxTargetPercent";
            }
            else if (pending.DeluxscoreMax > 0)
            {
                targetDxPercent = 100.0 * pending.DeluxscoreMax / (3.0 * n);
                dxSourceTag = "pending.DeluxscoreMax";
            }
            else if (judge != null && judge.DxScore > 0 && judge.TotalNotes > 0)
            {
                targetDxPercent = 100.0 * judge.DxScore / (3.0 * judge.TotalNotes);
                dxSourceTag = "judge.DxScore";
            }

            bool dxFromSource = targetDxPercent > 0.0;
            if (!perTypeAssigned && targetDxPercent > 0.0)
            {
                for (int iter = 0; iter < 10; iter++)
                {
                    double dxPercent = DxPercent(cp, perfect, great, n);
                    double dxErr = dxPercent - targetDxPercent;
                    if (Math.Abs(dxErr) < 0.5) break;

                    bool needLower = dxErr > 0.0; // DX 太高 → 往下换（CP→Perfect→Great→Good）
                    int bestUnit = -1, bestNewLevel = -1;
                    long bestDelta = 0;
                    double bestCost = double.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        if (level[i] < 0) continue;
                        int newLevel = level[i] + (needLower ? 1 : -1);
                        if (newLevel < 0 || newLevel > 3) continue;
                        // 判定表约束
                        if ((AllowedMask(unitCls[i]) & (1 << newLevel)) == 0) continue;
                        var st = unitType[i];
                        long deltaScore = (long)UnitValue(st, TimingForLevel(st, newLevel, i)) - UnitValue(st, unitTiming[i]);
                        double achErr = Math.Abs((double)((decimal)(scoreSum + deltaScore) * 100m / maxSum - targetPercent));
                        double newDx = dxPercent + (needLower ? -1.0 : 1.0) * 100.0 / (3.0 * n);
                        double cost = achErr * 100.0 + Math.Abs(newDx - targetDxPercent);
                        if (cost < bestCost) { bestCost = cost; bestUnit = i; bestNewLevel = newLevel; bestDelta = deltaScore; }
                    }
                    if (bestUnit < 0) break;

                    switch (level[bestUnit])
                    {
                        case 0: cp--; break;
                        case 1: perfect--; break;
                        case 2: great--; break;
                        default: good--; break;
                    }
                    switch (bestNewLevel)
                    {
                        case 0: cp++; break;
                        case 1: perfect++; break;
                        case 2: great++; break;
                        default: good++; break;
                    }
                    level[bestUnit] = bestNewLevel;
                    unitTiming[bestUnit] = TimingForLevel(unitType[bestUnit], bestNewLevel, bestUnit);
                    scoreSum += bestDelta;
                }
            }

            // ---- 7) maxCombo：最长连续非 miss 段 ----
            int maxCombo = 0, run = 0;
            for (int i = 0; i < n; i++)
            {
                if (level[i] < 0) { run = 0; continue; }
                run++;
                if (run > maxCombo) maxCombo = run;
            }

            // ---- 8) 落地：SetResult + 写回 GameScoreList 的缓存字段 ----
            for (int i = 0; i < n; i++)
            {
                score.SetResult(unitIndex[i], unitType[i], unitTiming[i]);
                if (unitType[i] == NoteScore.EScoreType.Break && level[i] >= 0)
                    score.SetResult(unitIndex[i], NoteScore.EScoreType.BreakBonus, unitTiming[i]);
            }
            for (int i = 0; i < endIndex.Count; i++)
                score.SetResult(endIndex[i], NoteScore.EScoreType.End, NoteJudge.ETiming.Critical);

            // 直接写 Achivement/计数，保证结算回读等于目标（SetResult 的缓存不一定会更新 GetAchivement）
            string fieldError;
            Navigation.SetInstanceField(score, "<Achivement>k__BackingField", targetPercent, out fieldError);
            Navigation.SetInstanceField(score, "<CriticalNum>k__BackingField", (uint)cp, out fieldError);
            Navigation.SetInstanceField(score, "<PerfectNum>k__BackingField", (uint)perfect, out fieldError);
            Navigation.SetInstanceField(score, "<GreatNum>k__BackingField", (uint)great, out fieldError);
            Navigation.SetInstanceField(score, "<GoodNum>k__BackingField", (uint)good, out fieldError);
            Navigation.SetInstanceField(score, "<MissNum>k__BackingField", (uint)miss, out fieldError);
            Navigation.SetInstanceField(score, "<MaxCombo>k__BackingField", (uint)maxCombo, out fieldError);
            Navigation.SetInstanceField(score, "<Combo>k__BackingField", (uint)maxCombo, out fieldError);
            Navigation.SetInstanceField(score, "<TheoryCombo>k__BackingField", (uint)n, out fieldError);

            // DB.PlayComboflagID: None=0 Silver=1(FC) Gold=2(FC+) AllPerfect=3 AllPerfectPlus=4
            // DB.PlaySyncflagID : None=0 ChainLow=1 ChainHi=2 SyncLow=3 SyncHi=4 SyncPlay=5
            // 先按本轨真实判定算出「实际可达」的最高徽章，再用源标记封顶 ——
            // 这样既不会把没 FC 的歌刷成 FC，也不会凭空造出源里没有的 AP。
            int achievableCombo;
            if (miss > 0) achievableCombo = 0;
            else if (good > 0) achievableCombo = 1;          // Silver = FULL COMBO（有 GOOD）
            else if (great > 0) achievableCombo = 2;         // Gold   = FULL COMBO+（无 GOOD）
            else achievableCombo = (perfect > 0) ? 3 : 4;    // AP / AP+
            int comboValue = srcCombo >= 0 ? Math.Min(srcCombo, achievableCombo) : achievableCombo;
            // 源说不是 FC，但本轨居然没 MISS → 以判定为准会矛盾，强制降到 0
            if (forceMiss && miss == 0) comboValue = 0;

            int syncValue;
            if (miss > 0) syncValue = 0;                                             // None
            else if (perfect == 0 && great == 0 && good == 0) syncValue = 5;          // SyncPlay
            else syncValue = 4;                                                      // SyncHi
            Navigation.SetInstanceField(score, "<SyncType>k__BackingField", syncValue, out fieldError);
            Navigation.SetInstanceField(score, "<ComboType>k__BackingField", comboValue, out fieldError);
            Log.Info("[score] 徽章: combo=" + comboValue + " sync=" + syncValue +
                     " (源comboStatus=" + (srcCombo >= 0 ? srcCombo.ToString() : "未知") +
                     ", 本轨可达=" + achievableCombo + ")");

            // 拟合值必须按游戏真实公式算（data-dev 校准）：
            //   achievement% = baseRatio*100 + 1% * breakBonusRatio
            //   baseRatio  = (maxBaseScore - baseLost) / maxBaseScore   （分母不含 break bonus）
            //   bonusRatio = breakExtra / maxBreakExtra
            // 之前用 (base+bonus)/(maxBase+maxBonus) 的乘法归一化，会系统性低估 ~1%（break 越多越明显）。
            long baseSum = 0, maxBaseSum = 0, breakBonusSum = 0, maxBreakBonus = 0;
            for (int i = 0; i < n; i++)
            {
                var unitScoreType = unitType[i];
                baseSum += UnitBaseValue(unitScoreType, unitTiming[i]);
                maxBaseSum += UnitBaseValue(unitScoreType, NoteJudge.ETiming.Critical);
                if (unitScoreType == NoteScore.EScoreType.Break)
                {
                    breakBonusSum += UnitBonusValue(unitTiming[i]);
                    maxBreakBonus += UnitBonusValue(NoteJudge.ETiming.Critical);
                }
            }
            double baseRatio = maxBaseSum > 0 ? (double)baseSum / maxBaseSum : 0.0;
            double bonusRatio = maxBreakBonus > 0 ? (double)breakBonusSum / maxBreakBonus : 0.0;
            decimal finalFitted = (decimal)((baseRatio + 0.01 * bonusRatio) * 100.0);
            double finalDxPercent = DxPercent(cp, perfect, great, n);
            string syncName = syncValue == 5 ? "FullSyncPlus" : (syncValue == 4 ? "FullSync" : "None");
            Log.Info("[score] 判定表重建: 目标=" + targetPercent.ToString("0.0000") + "% notes=" + n +
                     " cp=" + cp + " perfect=" + perfect + " great=" + great + " good=" + good + " miss=" + miss +
                     " maxCombo=" + maxCombo + " sync=" + syncName +
                     " 拟合=" + finalFitted.ToString("0.0000") + "%" +
                     " dx=" + finalDxPercent.ToString("0.00") + "%" +
                     " perType=" + (perTypeAssigned ? "judge" : "fallback"));

            if (targetDxPercent > 0.0)
            {
                int actualStars = StarsOf(finalDxPercent);
                int targetStars = StarsOf(targetDxPercent);
                double dxError = finalDxPercent - targetDxPercent;
                string dxLevelTag = Math.Abs(dxError) >= 1.0 ? "WARN" : "OK";
                long dxMax = 3L * n;
                double sourceDxPercent = pending.DeluxscoreMax > 0 ? 100.0 * pending.DeluxscoreMax / dxMax : -1.0;
                string judgeTag = judge == null ? "" :
                    " judge: dx=" + judge.DxScore + "(" + judge.DxPercent.ToString("0.00") + "%," +
                    judge.DxStar + "★) src=" + judge.DxSourcePercent.ToString("0.00") +
                    "% target=" + judge.DxTargetPercent.ToString("0.00") + "%";
                Log.Info("[score] DX 校验(" + dxLevelTag + "): 源DX=" + pending.DeluxscoreMax + "/" + dxMax + "=" +
                         (sourceDxPercent < 0 ? "?" : sourceDxPercent.ToString("0.0000") + "%") +
                         "(star=" + (sourceDxPercent < 0 ? "?" : StarsOf(sourceDxPercent).ToString()) + ")" +
                         " src=" + dxSourceTag +
                         " → 目标DX=" + targetDxPercent.ToString("0.00") + "%(" + targetStars + "★)" +
                         " 实际DX=" + finalDxPercent.ToString("0.00") + "%(" + actualStars + "★)" +
                         " 成就=" + targetPercent.ToString("0.0000") + "% 拟合误差=" +
                         Math.Abs((double)(finalFitted - targetPercent)).ToString("0.0000") + "%" +
                         judgeTag +
                         (dxFromSource ? "" : "（无源 DX 信息 → 未做 DX 压制）"));
            }
            return cp + perfect + great + good + miss > 0;
        }

        /// <summary>单个判定的 base 分（**不含** break bonus）。</summary>
        private static int UnitBaseValue(NoteScore.EScoreType scoreType, NoteJudge.ETiming timing)
        {
            return (int)NoteScore.GetJudgeScore(timing, scoreType);
        }

        /// <summary>Break note 的 bonus 分（非 break 返回 0）。</summary>
        private static int UnitBonusValue(NoteJudge.ETiming timing)
        {
            return (int)NoteScore.GetJudgeScore(timing, NoteScore.EScoreType.BreakBonus);
        }

        /// <summary>DX 百分比 = (3*CP + 2*Perfect + Great) / (3*notes) * 100。</summary>
        private static double DxPercent(int cp, int perfect, int great, int total)
        {
            if (total <= 0) return 0.0;
            long dx = 3L * cp + 2L * perfect + great;
            return 100.0 * dx / (3.0 * total);
        }

        /// <summary>星数（maimai DX 星级阈值 0/85/90/93/95/97）。</summary>
        private static int StarsOf(double dxPercent)
        {
            if (dxPercent >= 97.0) return 5;
            if (dxPercent >= 95.0) return 4;
            if (dxPercent >= 93.0) return 3;
            if (dxPercent >= 90.0) return 2;
            if (dxPercent >= 85.0) return 1;
            return 0;
        }

        /// <summary>按目标成绩挑一个"像真人"的 miss 数。</summary>
        private static int PickMissCount(decimal percent, int total, Random rng)
        {
            int m;
            if (percent >= 100m) m = 0;
            else if (percent >= 99.5m) m = rng.Next(0, 2);
            else if (percent >= 97m) m = rng.Next(0, 2);
            else if (percent >= 95m) m = rng.Next(1, 4);
            else if (percent >= 90m) m = rng.Next(3, 9);
            else if (percent >= 85m) m = rng.Next(5, 13);
            else if (percent >= 80m) m = rng.Next(8, 25);
            else m = (int)Math.Max(0m, (80m - percent) / 80m * total * 0.1m);
            int cap = Math.Max(1, total / 4);
            if (m > cap) m = cap;
            if (m < 0) m = 0;
            return m;
        }

        private static readonly NoteJudge.ETiming[] GreatTimings = new NoteJudge.ETiming[]
        {
            NoteJudge.ETiming.FastGreat, NoteJudge.ETiming.LateGreat,
            NoteJudge.ETiming.FastGreat2nd, NoteJudge.ETiming.LateGreat2nd,
            NoteJudge.ETiming.FastGreat3rd, NoteJudge.ETiming.LateGreat3rd
        };

        private static NoteJudge.ETiming TimingForLevel(NoteScore.EScoreType scoreType, int level, int ordinal)
        {
            switch (level)
            {
                case 0: return NoteJudge.ETiming.Critical;
                case 1: return (ordinal % 2 == 0) ? NoteJudge.ETiming.FastPerfect : NoteJudge.ETiming.LatePerfect;
                case 2: return GreatTimings[ordinal % GreatTimings.Length];
                default: return (ordinal % 2 == 0) ? NoteJudge.ETiming.FastGood : NoteJudge.ETiming.LateGood;
            }
        }

        /// <summary>单个判定实际计入 achievement 的分数（Break 含 BreakBonus）。</summary>
        private static int UnitValue(NoteScore.EScoreType scoreType, NoteJudge.ETiming timing)
        {
            int value = (int)NoteScore.GetJudgeScore(timing, scoreType);
            if (scoreType == NoteScore.EScoreType.Break)
                value += (int)NoteScore.GetJudgeScore(timing, NoteScore.EScoreType.BreakBonus);
            return value;
        }

        /// <summary>从 level 降一级带来的分数损失（估商用代表性 timing）。</summary>
        private static int StepLoss(NoteScore.EScoreType scoreType, int level)
        {
            if (level < 0 || level >= 3) return 0;
            int current = UnitValue(scoreType, TimingForLevel(scoreType, level, 0));
            int next = UnitValue(scoreType, TimingForLevel(scoreType, level + 1, 0));
            return current - next;
        }

        // ================= 音符类型 × 判定表（docs/judge-matrix.md） =================
        // NotesTypeID.Def 完整列出了音符变体；其中 Ex* 系列就是游戏内「保护」音符。
        //   Tap=0 Break=1 ExTap=2(保护tap) Hold=3 ExHold=4(保护hold) Slide=5(星星身子)
        //   Star=6(星星头) BreakStar=7 ExStar=8(保护星星头) TouchTap=9 TouchHold=10
        //   ExBreakTap=11 BreakHold=12 ExBreakHold=13 BreakSlide=14 ExSlide=15
        //   ExBreakSlide=16 ExBreakStar=17(保护绝赞星) ConnectSlide=18
        // 判定档位：0=大P(CP) 1=小P 2=GREAT 3=GOOD -1=MISS
        private const int JC_Tap = 0;
        private const int JC_Hold = 1;
        private const int JC_ProtectTap = 2;
        private const int JC_ProtectHold = 3;
        private const int JC_StarHead = 4;
        private const int JC_ProtectStarHead = 5;
        private const int JC_SlideBody = 6;
        private const int JC_TouchTap = 7;
        private const int JC_TouchHold = 8;
        private const int JC_Break = 9;

        private static int JudgeClassOf(Manager.NotesTypeID.Def def)
        {
            switch (def)
            {
                case Manager.NotesTypeID.Def.ExTap: return JC_ProtectTap;
                case Manager.NotesTypeID.Def.ExBreakTap: return JC_ProtectTap;
                case Manager.NotesTypeID.Def.ExHold: return JC_ProtectHold;
                case Manager.NotesTypeID.Def.ExBreakHold: return JC_ProtectHold;
                case Manager.NotesTypeID.Def.ExStar: return JC_ProtectStarHead;
                case Manager.NotesTypeID.Def.ExBreakStar: return JC_ProtectStarHead;
                case Manager.NotesTypeID.Def.Star: return JC_StarHead;
                case Manager.NotesTypeID.Def.BreakStar: return JC_StarHead;
                case Manager.NotesTypeID.Def.Slide:
                case Manager.NotesTypeID.Def.BreakSlide:
                case Manager.NotesTypeID.Def.ExSlide:
                case Manager.NotesTypeID.Def.ExBreakSlide:
                case Manager.NotesTypeID.Def.ConnectSlide:
                    return JC_SlideBody;
                case Manager.NotesTypeID.Def.TouchTap: return JC_TouchTap;
                case Manager.NotesTypeID.Def.TouchHold: return JC_TouchHold;
                case Manager.NotesTypeID.Def.Break: return JC_Break;
                case Manager.NotesTypeID.Def.BreakHold:
                case Manager.NotesTypeID.Def.Hold: return JC_Hold;
                default: return JC_Tap;
            }
        }

        /// <summary>
        /// 该音符类型允许出现的判定档位（位 0=大P 1=小P 2=GREAT 3=GOOD）。MISS 一律允许。
        /// 依据 docs/judge-matrix.md：
        ///  - 保护 tap / 保护星星头 / 保护绝赞：只能 大P 或 MISS
        ///  - 保护 hold：大P 或 小P 或 MISS（无 GREAT/GOOD）
        ///  - 星星身子：无 小P，可以有 GREAT/GOOD
        ///  - 星星头与普通 tap 同组（全档位）
        ///  - touch：全档位，但实际很少出小P（由 4.3 再平衡时把 touch 排除在 小P 之外）
        /// </summary>
        private static int AllowedMask(int cls)
        {
            switch (cls)
            {
                case JC_ProtectTap: return 0b0001;
                case JC_ProtectStarHead: return 0b0001;
                case JC_ProtectHold: return 0b0011;
                case JC_SlideBody: return 0b1101;
                default: return 0b1111;
            }
        }

        /// <summary>把不允许的档位改到最接近的允许档位；小P→大P 是同分免费互换，优先。</summary>
        private static int NearestAllowedLevel(int mask, int desired)
        {
            if (desired == 1 && (mask & 0b0001) != 0) return 0;
            int best = -1;
            int bestCost = int.MaxValue;
            for (int l = 0; l <= 3; l++)
            {
                if ((mask & (1 << l)) == 0) continue;
                int cost = Math.Abs(l - desired);
                if (cost < bestCost) { bestCost = cost; best = l; }
            }
            return best < 0 ? 0 : best;
        }

        /// <summary>
        /// 用 data-dev 求解器的 per-type 计数给每个 unit 定级（严格按类型填，不跨类型借）。
        /// `TapCounts/HoldCounts/SlideCounts/BreakCounts` 各 5 档：0=CP,1=Perfect,2=Great,3=Good,4=Miss。
        /// Touch 类型没有独立数组，按全局 CP/Perfect/Great/Good/Miss 比例缩放到 touch 数。
        /// 返回 true = 所有 unit 都分配成功（此时 DX 应精确等于求解器目标，无需再做 DX 压制）。
        /// </summary>
        private static bool TryAssignPerTypeCounts(JudgeBreakdown judge, List<NoteScore.EScoreType> unitType,
            List<bool> unitTouchHold, int[] level, int n)
        {
            if (judge == null) return false;
            if (judge.TapCounts == null || judge.TapCounts.Length < 5) return false;
            if (judge.HoldCounts == null || judge.HoldCounts.Length < 5) return false;
            if (judge.SlideCounts == null || judge.SlideCounts.Length < 5) return false;
            if (judge.BreakCounts == null || judge.BreakCounts.Length < 5) return false;

            int[] typeCount = new int[7];
            int touchTapCount = 0, touchHoldCount = 0;
            for (int i = 0; i < n; i++)
            {
                var type = unitType[i];
                typeCount[(int)type]++;
                if (type == NoteScore.EScoreType.Touch)
                {
                    if (unitTouchHold != null && i < unitTouchHold.Count && unitTouchHold[i]) touchHoldCount++;
                    else touchTapCount++;
                }
            }

            int[][] cumulative = new int[7][];
            int[] tmpCum;

            // data-dev 的 .ma2 统计：touch tap 计入 Tap（权重 1）、touch hold 计入 Hold（权重 2）。
            // 游戏里两者都表现为 EScoreType.Touch，所以这里按 Def 区分后分别并入 Tap / Hold 池共用游标。
            int tapPool = typeCount[(int)NoteScore.EScoreType.Tap] + touchTapCount;
            int holdPool = typeCount[(int)NoteScore.EScoreType.Hold] + touchHoldCount;

            if (!BuildPerTypeCumulative(judge.TapCounts, tapPool, out tmpCum)) return false;
            cumulative[(int)NoteScore.EScoreType.Tap] = tmpCum;
            if (!BuildPerTypeCumulative(judge.HoldCounts, holdPool, out tmpCum)) return false;
            cumulative[(int)NoteScore.EScoreType.Hold] = tmpCum;
            if (!BuildPerTypeCumulative(judge.SlideCounts, typeCount[(int)NoteScore.EScoreType.Slide], out tmpCum)) return false;
            cumulative[(int)NoteScore.EScoreType.Slide] = tmpCum;
            if (!BuildPerTypeCumulative(judge.BreakCounts, typeCount[(int)NoteScore.EScoreType.Break], out tmpCum)) return false;
            cumulative[(int)NoteScore.EScoreType.Break] = tmpCum;

            int[] cursor = new int[7];
            for (int i = 0; i < n; i++)
            {
                var type = unitType[i];
                int pool;
                if (type == NoteScore.EScoreType.Touch)
                    pool = (unitTouchHold != null && i < unitTouchHold.Count && unitTouchHold[i])
                        ? (int)NoteScore.EScoreType.Hold : (int)NoteScore.EScoreType.Tap;
                else
                    pool = (int)type;
                if (cumulative[pool] == null) return false;
                int ordinal = cursor[pool]++;
                level[i] = LevelFromCumulative(cumulative[pool], ordinal);
            }
            return true;
        }

        /// <summary>把 5 档计数缩放到 total 个 unit，并转成前缀和；余数补给 CP。</summary>
        private static bool BuildPerTypeCumulative(int[] source, int total, out int[] cumulative)
        {
            cumulative = null;
            if (total <= 0)
            {
                cumulative = new int[5];
                return true;
            }
            int srcSum = 0;
            for (int k = 0; k < 5; k++) srcSum += source[k];
            if (srcSum <= 0) return false;

            int[] counts = new int[5];
            int assigned = 0;
            for (int k = 0; k < 5; k++)
            {
                counts[k] = (int)Math.Round((double)source[k] * total / srcSum);
                if (counts[k] < 0) counts[k] = 0;
                assigned += counts[k];
            }
            int diff = total - assigned;
            counts[0] += diff;
            if (counts[0] < 0)
            {
                int deficit = -counts[0];
                counts[0] = 0;
                for (int k = 1; k < 5 && deficit > 0; k++)
                {
                    int take = Math.Min(deficit, counts[k]);
                    counts[k] -= take;
                    deficit -= take;
                }
            }

            cumulative = new int[5];
            int run = 0;
            for (int k = 0; k < 5; k++)
            {
                run += counts[k];
                cumulative[k] = run;
            }
            return true;
        }

        private static int LevelFromCumulative(int[] cumulative, int ordinal)
        {
            if (cumulative == null) return 0;
            for (int k = 0; k < 4; k++)
                if (ordinal < cumulative[k]) return k;   // 0=CP, 1=Perfect, 2=Great, 3=Good
            return -1;                                   // Miss
        }

        private static GameScoreList TryGetGameScore(int monitor, int trackNo)
        {
            try
            {
                var playManager = MAI2.Util.Singleton<GamePlayManager>.Instance;
                if (playManager == null) return null;
                var score = playManager.GetGameScore(monitor, trackNo);
                if (score != null) return score;
                var list = playManager.GetGameScores(monitor);
                if (list != null && list.Length > 0)
                {
                    int index = trackNo < 0 ? 0 : (trackNo >= list.Length ? list.Length - 1 : trackNo);
                    return list[index];
                }
            }
            catch (Exception) { }
            return null;
        }

        // ---------------------------------------------------------------- install

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            LoadFromConfig();
            try
            {
                // 先确认 patch 目标在 1.65 上真实存在（签名不符时 Harmony 会静默/抛异常）
                var targetLog = "[score] patch targets: ";
                targetLog += "GameMonitor.ForceAchivement=" +
                             (AccessTools.Method(typeof(Monitor.GameMonitor), "ForceAchivement",
                                 new[] { typeof(int), typeof(int) }) != null ? "ok" : "MISS");
                targetLog += ", GameScoreList.SetForceAchivement=" +
                             (AccessTools.Method(typeof(GameScoreList), "SetForceAchivement",
                                 new[] { typeof(int), typeof(int) }) != null ? "ok" : "MISS");
                targetLog += ", GameScoreList.SetResult=" +
                             (AccessTools.Method(typeof(GameScoreList), "SetResult",
                                 new[] { typeof(int), typeof(NoteScore.EScoreType), typeof(NoteJudge.ETiming) }) != null ? "ok" : "MISS");
                targetLog += ", GameProcess.UpdateSubbMonitorData=" +
                             (AccessTools.Method(typeof(Process.GameProcess), "UpdateSubbMonitorData",
                                 new[] { typeof(int) }) != null ? "ok" : "MISS");
                targetLog += ", ResultProcess.OnStart=" +
                             (AccessTools.Method(typeof(Process.ResultProcess), "OnStart") != null ? "ok" : "MISS");
                Log.Info(targetLog);

                _harmony = new HarmonyLib.Harmony("fakerswap.flow.score");
                _harmony.PatchAll(typeof(Patches.MonitorForcePatch));
                _harmony.PatchAll(typeof(Patches.ScoreListForcePatch));
                _harmony.PatchAll(typeof(Patches.GameProcessSettlePatch));
                _harmony.PatchAll(typeof(Patches.ResultProcessCommitPatch));
                Log.Info("[score] ScoreHooks 已安装 (最小通关=" + MinClearAchievement +
                         ", 单位=" + ForceUnit + ", 每轨写回 UserData=" + AutoCommitToUserData + ")");
            }
            catch (Exception e)
            {
                Log.Error("[score] ScoreHooks 安装失败（Navigation.SkipCurrentChart 仍会直接调 ForceAchivement）", e);
            }
        }

        // ---------------------------------------------------------------- patches

        private static class Patches
        {
            /// <summary>GameMonitor.ForceAchivement(int achivement, int dxscore) —— 强制成绩的第一入口。</summary>
            [HarmonyPatch(typeof(Monitor.GameMonitor), "ForceAchivement")]
            internal static class MonitorForcePatch
            {
                [HarmonyPrefix]
                private static void Prefix(ref int achivement, ref int dxscore)
                {
                    // 无条件确认调用链（lead/flow-dev 要求：先确认有没有被调用）
                    if (_forceCalledCount < 3 || TraceForceAchievement)
                        Log.Info("[score] GameMonitor.ForceAchivement called ach=" + achivement +
                                 ", dx=" + dxscore + ", pending=" +
                                 (PendingForThisTrack == null ? "null" : AchievementText(PendingForThisTrack.Achievement)) +
                                 ", skipTrack=" + SkipCurrentTrack + ", disabled=" + DisableAllScoreWrites);
                    _forceCalledCount++;
                    OverrideArguments(ref achivement, ref dxscore, "GameMonitor.ForceAchivement");
                }
            }

            /// <summary>
            /// GameScoreList.SetForceAchivement —— **重写实现**（Prefix 返回 false 跳过原版）。
            ///
            /// 实机 01:03：只改写入参时结算页仍是 0.0000% / 全 MISS —— 原版在"跳过谱面后所有 note 未判定"的状态下
            /// 产不出判定表。这里按目标成绩自己生成一整套"像真人打的"判定（见 RewriteSetForceAchivement），
            /// 并直接写 Achivement 缓存，保证结算回读 == 目标。
            /// </summary>
            [HarmonyPatch(typeof(GameScoreList), "SetForceAchivement")]
            internal static class ScoreListForcePatch
            {
                [HarmonyPrefix]
                private static bool Prefix(int achivement, int dxscore, GameScoreList __instance)
                {
                    try
                    {
                        if (_setForceCalledCount < 3 || TraceForceAchievement)
                            Log.Info("[score] GameScoreList.SetForceAchivement called ach=" + achivement +
                                     ", dx=" + dxscore + ", monitor=" +
                                     (__instance == null ? -1 : NavReflect.ToInt(NavReflect.Get(__instance, "_monitorIndex"))) +
                                     ", pending=" + (PendingForThisTrack == null ? "null" :
                                         AchievementText(PendingForThisTrack.Achievement)));
                        _setForceCalledCount++;

                        if (__instance == null) return true;          // 原版兜底
                        if (SkipCurrentTrack)
                        {
                            // filler/中止轨：上传必须是空成绩（0.0000% 全 MISS）。以前 return true 保持原生，
                            // 实机在 Sinmai-Assist 自动打歌开启时把 100% 的 autoplay 成绩传上去了。
                            try
                            {
                                var zero = new ScoreRecord { MusicId = 0, Level = 0, PlayCount = 1, Achievement = 0 };
                                ForceAllMiss = true;
                                bool zeroed = RewriteSetForceAchivement(__instance, zero);
                                ForceAllMiss = false;
                                if (zeroed) return false;
                            }
                            catch (Exception e)
                            {
                                ForceAllMiss = false;
                                Log.Warn("[score] 强制空成绩失败: " + e.Message);
                            }
                            return true;
                        }
                        if (DisableAllScoreWrites) return true;       // 只读模式绝不改
                        var pending = PendingForThisTrack;
                        if (pending == null) return true;

                        bool rewritten = RewriteSetForceAchivement(__instance, pending);
                        return !rewritten; // rewritten=true 时跳过原版
                    }
                    catch (Exception e)
                    {
                        Log.Error("[score] SetForceAchivement 重写失败，回退原版", e);
                        return true;
                    }
                }
            }

            /// <summary>
            /// ResultProcess.OnStart：本轨结算界面的起点。在这里延迟调用 <see cref="CommitTrackScore"/>，
            /// 把期望成绩写回 UserData（per-track，和 data-dev / flow-dev 约好的方案 A）。
            /// 不用 DataSave 批量写，避免与上传时序耦合。
            /// </summary>
            [HarmonyPatch(typeof(Process.ResultProcess), "OnStart")]
            internal static class ResultProcessCommitPatch
            {
                [HarmonyPostfix]
                private static void Postfix()
                {
                    try { OnResultProcessStarted(); }
                    catch (Exception e) { Log.Error("[score] Result 钩子写回 UserData 失败", e); }
                }
            }

            /// <summary>结算数据生成后回读一次 achievement，校验是否真的写进去了（每轨每 monitor 只打一条）。</summary>
            [HarmonyPatch(typeof(Process.GameProcess), "UpdateSubbMonitorData")]
            internal static class GameProcessSettlePatch
            {
                [HarmonyPostfix]
                private static void Postfix(int i)
                {
                    try
                    {
                        // 用户自己玩时完全静默（不读分、不打日志），只在插件任务运行时校验
                        if (!Navigation.IsTaskActive()) return;
                        var playManager = MAI2.Util.Singleton<GamePlayManager>.Instance;
                        if (playManager == null) return;
                        int trackNo = Math.Max(0, (int)GameManager.MusicTrackNumber - 1);
                        uint actual = playManager.GetAchivement(i, trackNo);
                        LastSettledAchievement = (int)actual;

                        // UpdateSubbMonitorData 会被高频调用（每帧/每轨多次），日志必须按 track 去重
                        string settleKey = i + ":" + trackNo;
                        var pending = PendingForThisTrack ?? _commitCandidate;
                        if (pending != null && !SkipCurrentTrack)
                        {
                            int expectedRaw = pending.Achievement;
                            int forceArg = ConvertForForce(expectedRaw);
                            bool consistent = Math.Abs((int)actual - forceArg) <= 100;
                            if (!consistent && !_repairAttemptedThisTrack)
                            {
                                // 兜底：SetForceAchivement 的重写没生效（或没被调用）→ 在这里就地重建判定表
                                _repairAttemptedThisTrack = true;
                                uint beforeRepair = actual;
                                var score = TryGetGameScore(i, trackNo);
                                bool repaired = score != null && RewriteSetForceAchivement(score, pending);
                                if (repaired) actual = playManager.GetAchivement(i, trackNo);
                                Log.Warn("[score] 结算回读不一致 → 就地重建判定表 repair=" + repaired +
                                         " 回读before=" + AchievementText((int)beforeRepair) +
                                         " 回读after=" + AchievementText((int)actual));
                                _lastSettleKey = settleKey;   // 修复后再打一条仲裁日志
                            }
                            if (_lastSettleKey != settleKey)
                            {
                                _lastSettleKey = settleKey;
                                ExamineReadback("结算回读", forceArg, expectedRaw, (int)actual, i, trackNo);
                            }
                        }
                        else if (_lastSettleKey != "filler:" + settleKey)
                        {
                            _lastSettleKey = "filler:" + settleKey;
                            Log.Info("[score] 本轨未改成绩（filler/中止），结算回读 " +
                                     AchievementText((int)actual) + " (monitor=" + i + ", track=" + trackNo + ")");
                        }
                    }
                    catch (Exception e)
                    {
                        NavReflect.WarnOnce("settle-readback", "结算回读失败: " + e.Message);
                    }
                }
            }
        }

        /// <summary>
        /// 对比"我们传进去的值"和"结算回读值"，并把可能单位不匹配的判断直接写进日志。
        /// 这是实机校准 ForceUnit 的主要依据。
        /// </summary>
        internal static void ExamineReadback(string where, int forceArg, int expectedRaw, int actualRaw, int monitor, int trackNo)
        {
            int diff = actualRaw - forceArg;
            if (diff < 0) diff = -diff;
            string tail = " (monitor=" + monitor + ", track=" + trackNo + ", unit=" + ForceUnit + ")";

            if (diff <= 100)
            {
                Log.Info("[score] " + where + " OK: 入参=" + forceArg + " 回读 " + AchievementText(actualRaw) +
                         " 期望 " + AchievementText(expectedRaw) + tail);
                return;
            }

            Log.Warn("[score] " + where + " 不一致: 入参=" + forceArg + " [" + ForceUnit + "] 回读 " +
                     AchievementText(actualRaw) + " 期望 " + AchievementText(expectedRaw) + tail);

            // 常见"单位用错"的两种特征，直接把建议写出来
            if (actualRaw >= 1000000 && forceArg >= 1000000 && actualRaw <= 1010000 && forceArg > 1010000)
                Log.Warn("[score] 建议: 入参被游戏当成百分比整数后封顶 → 把 ForceAchievementUnit 改成 Percent100");
            else if (forceArg <= 101 && actualRaw >= 100000)
                Log.Warn("[score] 建议: 入参按整数百分比被解释成 1/10000 了 → 目标应再 ×10000；" +
                         "当前单位 " + ForceUnit + "，请确认是否应为 TenThousandth");
            else if (actualRaw >= 100000 && forceArg * 10000 < 200000 && forceArg > 0)
                Log.Warn("[score] 建议: 回读≈入参×10000 → 把 ForceAchievementUnit 改成 TenThousandth");
        }

        /// <summary>把任何 ForceAchivement 入口的参数改写成期望成绩；无期望成绩时原样放行。</summary>
        private static void OverrideArguments(ref int achievement, ref int dxScore, string source)
        {
            try
            {
                if (SkipCurrentTrack) return;
                if (DisableAllScoreWrites) return;   // 双保险：只读模式绝不改游戏内结算成绩
                var pending = PendingForThisTrack;
                if (pending == null) return;

                int forceArg = ConvertForForce(pending.Achievement);
                if (TraceForceAchievement)
                    Log.Info("[score] " + source + " 入口: ach=" + achievement + " dx=" + dxScore +
                             " -> 期望 " + AchievementText(pending.Achievement) +
                             " 入参=" + forceArg + " [" + ForceUnit + "] dx=" + pending.DeluxscoreMax);

                achievement = forceArg;
                if (pending.DeluxscoreMax > 0) dxScore = pending.DeluxscoreMax;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("force-override", "改写 ForceAchivement 参数失败: " + e.Message);
            }
        }
    }
}
