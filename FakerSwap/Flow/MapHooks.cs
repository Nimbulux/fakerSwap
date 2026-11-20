using System;
using System.Collections;
using System.Collections.Generic;
using FakerSwap.Core;
using FakerSwap.Data;
using HarmonyLib;
using Manager;
using Process;
using UnityEngine;

namespace FakerSwap.Flow
{
    /// <summary>
    /// 地图 / 区域选择 / 课题曲。
    ///
    /// 需求（见 docs/architecture.md §7）：
    ///  1. 区域选择界面出现时给 <c>Config.AreaSelectTimeoutSec</c>（默认 3s）人工选择时间，没人选就走默认项；
    ///  2. 检测列表里是否有"课题区/课题曲"选项，有就动态把课题曲加进下一批游玩列表（以能通过的最低条件通过）；
    ///  3. 如果检测到人工换了另一个区域，就取消课题曲任务。
    ///
    /// 实机行为要点：
    ///  - 区域选择的"默认项"由游戏自己的 <see cref="RegionalSelectProcess.TimeUp"/> 决定（等于倒计时归零）；
    ///  - 课题曲在选歌页表现为 MapTask / CircleTask（1.65 新增 circle）分类，这里直接从
    ///    MusicSelectProcess 的内部分类表里扫 musicId+difficulty，不依赖 UI 文案；
    ///  - 1.65 的 DLL 方法体是加密的，所有判定都只依赖"成员存在 + 运行时可读"，读不到就降级并打日志。
    /// </summary>
    public static class MapHooks
    {
        /// <summary>一条课题曲（要加进下一批游玩列表的条目）。</summary>
        public sealed class MissionChart
        {
            public int MusicId;
            public int Level;
            /// <summary>目标成绩（1/10000 %），默认 80.0000% 最低通关。</summary>
            public int Achievement;
            /// <summary>来源分类（MapTask / CircleTask / Challenge）。</summary>
            public string Source = "";
            /// <summary>挑战曲生命数（>0 = 限命挑战，MISS 会扣命）。</summary>
            public int StartLife;
            /// <summary>挑战曲解锁难度限制。</summary>
            public int ChallengeUnlockDiff;

            public override string ToString()
            {
                return MusicId + "-" + Level + "@" + (Achievement / 10000.0).ToString("0.0000") + "%(" + Source + ")";
            }
        }

        /// <summary>本 credit 检测到的课题曲；flow 组下一批游玩列表时消费。</summary>
        public static readonly List<MissionChart> PendingMissionCharts = new List<MissionChart>();

        /// <summary>当前是否处于课题曲任务（检测到课题曲 → true；人工换区/消费后 → false）。</summary>
        public static bool MissionTaskActive { get; private set; }

        /// <summary>课题曲来源（最近一次扫描结果）。</summary>
        public static string MissionSource { get; private set; } = "";

        /// <summary>区域选择是否已经超时（我方调过 TimeUp）。</summary>
        public static bool AreaSelectTimedOut { get; private set; }

        /// <summary>操作员是否手动动过区域光标。</summary>
        public static bool AreaManuallyChanged { get; private set; }

        /// <summary>最终选中的区域 id / 名字（读到才有）。</summary>
        public static int SelectedAreaId { get; private set; } = -1;
        public static string SelectedAreaName { get; private set; } = "";

        // ---------------------------------------------------------------- 区域自动化（flow 爬区用）

        /// <summary>当前区域选择页上可选的区（含 id/name/isComplete/isLock/isInfinity/isEvent/倒计时）。</summary>
        public static List<Navigation.AreaInfo> AvailableAreas
        {
            get { return Navigation.ListAreas(0); }
        }

        /// <summary>可爬的区 id（未完成 + 未锁定 + 非无限，优先非活动区）。</summary>
        public static List<int> PendingAreaIds
        {
            get
            {
                var ids = new List<int>();
                var areas = Navigation.ListAreas(0);
                for (int i = 0; i < areas.Count; i++)
                {
                    var area = areas[i];
                    if (area == null || area.IsLock || area.IsComplete || area.IsInfinity) continue;
                    ids.Add(area.Id);
                }
                return ids;
            }
        }

        /// <summary>在选区页选中指定区（把光标移过去并确认）。</summary>
        public static bool SelectArea(int areaId)
        {
            bool ok = Navigation.SelectArea(areaId, 0);
            if (ok)
            {
                SelectedAreaId = areaId;
                SelectedAreaName = "";
                try
                {
                    var areas = Navigation.ListAreas(0);
                    for (int i = 0; i < areas.Count; i++)
                    {
                        if (areas[i] != null && areas[i].Id == areaId) { SelectedAreaName = areas[i].Name; break; }
                    }
                }
                catch (Exception) { }
                Log.Info("[map] SelectArea: areaId=" + areaId + " name=" + SelectedAreaName +
                         " 剩余倒计时=" + Navigation.GetAreaSelectTimeout(0).ToString("0.0") + "s");
            }
            return ok;
        }

        /// <summary>选区页剩余倒计时（秒；不在选区页返回 -1）。</summary>
        public static double AreaSelectRemainingSec
        {
            get { return Navigation.GetAreaSelectTimeout(0); }
        }

        /// <summary>自动选一个未爬完的区（没有则 false）。</summary>
        public static bool SelectNextIncompleteArea()
        {
            return Navigation.SelectNextIncompleteArea(0);
        }

        /// <summary>超时秒数：来自 Config，最小 0.5s（0 表示"立刻走默认项"）。</summary>
        public static double AreaSelectTimeoutSec
        {
            get
            {
                double v = 3.0;
                try { v = ModConfig.Data.AreaSelectTimeoutSec; } catch (Exception) { }
                if (v < 0.5) v = 0.5;
                if (v > 60) v = 60;
                return v;
            }
        }

        // ---------------------------------------------------------------- state

        private static bool _installed;
        private static HarmonyLib.Harmony _harmony;

        private static RegionalSelectProcess _process;
        private static uint _processId;
        private static double _armedAt;
        private static bool _sampled;
        private static int _cursorAtArm = -1;
        private static bool _decided;

        /// <summary>每次区域选择界面出现 +1；人工换区取消后，本次区域选择内的课题曲都不再自动加。</summary>
        private static int _areaGeneration;
        private static int _cancelledGeneration = -1;

        /// <summary>最近一次选歌页进程实例（供 missiondump 诊断命令使用）。</summary>
        public static MusicSelectProcess LastMusicSelect;
        /// <summary>
        /// 课题曲列表要等「选区确认」之后游戏才会算出来（用户实机确认）。
        /// 没选框区就读选歌页分类，拿到的永远是空/过期数据，所以用它做门控。
        /// </summary>
        public static bool MissionAreaReady;
        /// <summary>OnUpdate 里节流扫描课题曲，避免每帧都做反射。</summary>
        private static int _lastScanFrame = -1000;

        // ---------------------------------------------------------------- install

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("fakerswap.flow.map");
                _harmony.PatchAll(typeof(Patches.RegionalSelectOnStartPatch));
                _harmony.PatchAll(typeof(Patches.RegionalSelectOnUpdatePatch));
                _harmony.PatchAll(typeof(Patches.MusicSelectOnStartPatch));
                _harmony.PatchAll(typeof(Patches.MusicSelectOnUpdatePatch));
                Log.Info("[map] MapHooks 已安装 (区域超时=" + AreaSelectTimeoutSec + "s)");
            }
            catch (Exception e)
            {
                Log.Error("[map] MapHooks 安装失败（Navigation 仍会主动调 TickAreaSelect）", e);
            }
        }

        private static class Patches
        {
            [HarmonyPatch(typeof(RegionalSelectProcess), "OnStart")]
            internal static class RegionalSelectOnStartPatch
            {
                [HarmonyPostfix]
                private static void Postfix(RegionalSelectProcess __instance)
                {
                    Arm(__instance);
                }
            }

            [HarmonyPatch(typeof(RegionalSelectProcess), "OnUpdate")]
            internal static class RegionalSelectOnUpdatePatch
            {
                [HarmonyPostfix]
                private static void Postfix(RegionalSelectProcess __instance)
                {
                    try { TickAreaSelect(__instance); }
                    catch (Exception e) { NavReflect.WarnOnce("map-update", "区域选择处理失败: " + e.Message); }
                }
            }

            [HarmonyPatch(typeof(MusicSelectProcess), "OnStart")]
            internal static class MusicSelectOnStartPatch
            {
                [HarmonyPostfix]
                private static void Postfix(MusicSelectProcess __instance)
                {
                    LastMusicSelect = __instance;
                    _lastScanFrame = UnityEngine.Time.frameCount;
                    try { ScanMusicSelect(__instance); }
                    catch (Exception e) { NavReflect.WarnOnce("map-scan", "课题曲扫描失败: " + e.Message); }
                }
            }

            // 选歌页的分类列表是懒加载的：OnStart 那一刻 _combineMapTaskDataList 通常还是 null，
            // 必须等用户/插件切到「ちほー課題曲」分类后才有数据。所以这里在 OnUpdate 里节流重扫，
            // 否则课题曲明明存在却永远检测不到（用户反馈）。
            [HarmonyPatch(typeof(MusicSelectProcess), "OnUpdate")]
            internal static class MusicSelectOnUpdatePatch
            {
                [HarmonyPostfix]
                private static void Postfix(MusicSelectProcess __instance)
                {
                    LastMusicSelect = __instance;
                    int frame = UnityEngine.Time.frameCount;
                    if (frame - _lastScanFrame < 20) return;
                    _lastScanFrame = frame;
                    try { ScanMusicSelect(__instance); }
                    catch (Exception e) { NavReflect.WarnOnce("map-scan-update", "课题曲扫描失败: " + e.Message); }
                }
            }
        }

        // ---------------------------------------------------------------- 区域选择

        private static void Arm(RegionalSelectProcess process)
        {
            if (process == null) return;
            // 用户自己玩游戏时（任务未运行）完全不接管区域选择
            if (!Navigation.IsTaskActive()) return;
            _process = process;
            _processId = process.ProcessId;
            MissionAreaReady = false;   // 新一轮选区开始：课题曲数据要等这次确认后才有
            _armedAt = Time.realtimeSinceStartup;
            _decided = false;
            _sampled = false;
            _areaGeneration++;
            AreaSelectTimedOut = false;
            AreaManuallyChanged = false;
            SelectedAreaId = -1;
            SelectedAreaName = "";
            _cursorAtArm = SafeCursor(process, 0);
            Log.Info("[map] 区域选择界面出现，等待操作员 " + AreaSelectTimeoutSec.ToString("0.0") +
                     "s（当前光标=" + _cursorAtArm + "）");
        }

        /// <summary>每次 Update 调；Navigation.GoToMusicSelect 也会调（幂等）。</summary>
        public static void TickAreaSelect(RegionalSelectProcess process)
        {
            if (process == null) return;

            // 任务闸门：用户自己玩时【不接管、不采样、不打日志、不 TimeUp】，避免打断玩家
            if (!Navigation.IsTaskActive())
            {
                _process = null;
                _processId = 0;
                return;
            }

            if (!ReferenceEquals(process, _process) || process.ProcessId != _processId) Arm(process);
            if (!ReferenceEquals(process, _process)) return; // Arm 被闸门拒绝

            double now = Time.realtimeSinceStartup;

            // 每个区域选择界面只采样/打印一次（实机事故：之前每 0.5s 打 217 条）
            if (!_sampled)
            {
                _sampled = true;
                LogAreaCandidatesOnce(process);
            }

            // 1) 人工动过光标 → 换区了 → 取消课题曲任务
            int cursor = SafeCursor(process, 0);
            if (_cursorAtArm < 0 && cursor >= 0)
            {
                _cursorAtArm = cursor; // 第一次读到光标，作为基准
            }
            // 导航正在用真实按键驱动光标（DriveRegionalSelect）时，光标变化是我们自己造成的，
            // 不能当成"操作员手动换区" —— 否则会 CancelMission 把刚排进来的课题曲清掉（实机 17:40 pending 丢失）。
            if (Navigation.AreaSelectDriveActive)
            {
                _cursorAtArm = cursor;
            }
            else if (!AreaManuallyChanged && cursor >= 0 && _cursorAtArm >= 0 && cursor != _cursorAtArm)
            {
                AreaManuallyChanged = true;
                Log.Info("[map] 操作员手动换区: 光标 " + _cursorAtArm + " -> " + cursor);
                CancelMission("操作员手动换区");
            }

            // 2) 选择已确认（游戏内部状态到了 ToDecide 及之后）
            int subState = SafeSubState(process, 0);
            if (!_decided && subState >= 10) // RegionSelectIndividualState.ToDecide = 10
            {
                _decided = true;
                SelectedAreaId = SafeAreaId(process, cursor);
                SelectedAreaName = SafeAreaName(process, cursor);
                MissionAreaReady = true;
                Log.Info("[map] 区域已确认: id=" + SelectedAreaId + " name=\"" + SelectedAreaName + "\"");
            }

            // 3) 超时 → 走游戏自己的默认项
            if (!AreaSelectTimedOut && SelectedAreaId <= 0 && !Navigation.AreaSelectDriveActive && now - _armedAt >= AreaSelectTimeoutSec)
            {
                AreaSelectTimedOut = true;
                try
                {
                    process.TimeUp();
                    Log.Info("[map] 区域选择超时 " + AreaSelectTimeoutSec.ToString("0.0") +
                             "s → 走默认项 (TimeUp)");
                }
                catch (Exception e)
                {
                    Log.Error("[map] RegionalSelectProcess.TimeUp 调用失败", e);
                }
            }
        }

        private static int SafeCursor(RegionalSelectProcess process, int monitor)
        {
            try { return process.SelectCursolIndex(monitor); }
            catch (Exception) { return -1; }
        }

        private static int SafeSubState(RegionalSelectProcess process, int monitor)
        {
            try
            {
                var states = NavReflect.Get(process, "_subStates") as Array;
                if (states != null && monitor < states.Length) return NavReflect.ToInt(states.GetValue(monitor));
            }
            catch (Exception) { }
            return -1;
        }

        private static int SafeAreaId(RegionalSelectProcess process, int cursor)
        {
            try
            {
                var list = process.GetUserMapDatas(0);
                if (list != null && cursor >= 0 && cursor < list.Count && list[cursor] != null) return list[cursor].ID;
            }
            catch (Exception) { }
            return -1;
        }

        private static string SafeAreaName(RegionalSelectProcess process, int cursor)
        {
            try
            {
                var list = process.GetUserMapDatas(0);
                if (list != null && cursor >= 0 && cursor < list.Count && list[cursor] != null) return list[cursor].Name ?? "";
            }
            catch (Exception) { }
            return "";
        }

        /// <summary>
        /// 每个区域选择界面只调用一次：打一行摘要；完整候选列表仅在 <c>Config.Verbose=true</c> 时输出。
        /// （实机事故：之前每 0.5s 全量打印 217 条，几分钟几 MB 日志 + 反射遍历拖慢游戏。）
        /// </summary>
        private static void LogAreaCandidatesOnce(RegionalSelectProcess process)
        {
            try
            {
                var list = process.GetUserMapDatas(0);
                if (list == null)
                {
                    NavReflect.WarnOnce("map-sample-null", "区域列表 GetUserMapDatas(0) 返回 null");
                    return;
                }
                int cursor = SafeCursor(process, 0);
                bool verbose = false;
                try { verbose = ModConfig.Data.Verbose; } catch (Exception) { }

                bool missionLike = false;
                string missionSample = "";
                for (int i = 0; i < list.Count; i++)
                {
                    var area = list[i];
                    if (area == null) continue;
                    bool like = IsMissionLike(area);
                    if (like)
                    {
                        missionLike = true;
                        if (missionSample.Length == 0)
                            missionSample = " id=" + area.ID + " name=\"" + area.Name + "\"";
                    }
                    if (!verbose) continue;
                    Log.Info("[map] 区域候选[" + i + "] id=" + area.ID + " name=\"" + area.Name + "\"" +
                             " event=" + area.IsEvent + " infinity=" + area.IsInfinity +
                             " clear=" + area.IsClear + " complete=" + area.IsComplete +
                             " lock=" + area.IsLock +
                             " releaseIds=" + (area.ReleaseIds != null ? area.ReleaseIds.Count : 0));
                }

                Log.Info("[map] 区域候选 " + list.Count + " 个 (当前光标=" + cursor +
                         ", 课题区=" + (missionLike ? "有" : "无") + ")");
                if (missionLike && !verbose)
                    Log.Info("[map] 课题区候选:" + missionSample + "（要看完整列表请把 config.Verbose 设为 true）");
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("map-sample", "区域列表采样失败: " + e.Message);
            }
        }

        /// <summary>粗略判断"课题区/事件区"：事件/无限区，或名字像课题/任务/活动。</summary>
        private static bool IsMissionLike(UserMapData area)
        {
            if (area == null) return false;
            if (area.IsEvent || area.IsInfinity) return true;
            var name = area.Name ?? "";
            if (name.Length == 0) return false;
            if (name.Contains("課題") || name.Contains("任务") || name.Contains("任務")) return true;
            if (name.IndexOf("mission", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.Contains("イベント") || name.Contains("活动") || name.Contains("活動")) return true;
            return false;
        }

        // ---------------------------------------------------------------- 课题曲

        /// <summary>清掉本次 credit 的课题曲状态（flow 开始新 credit 时调）。</summary>
        public static void ResetForCredit()
        {
            PendingMissionCharts.Clear();
            MissionTaskActive = false;
            MissionSource = "";
            AreaManuallyChanged = false;
            AreaSelectTimedOut = false;
            _decided = false;
            _cancelledGeneration = -1;
            _areaGeneration++;
            _process = null;
            _processId = 0;
        }

        /// <summary>取消课题曲任务（人工换区 / 超时选到别处 / flow 主动取消都走这里）。</summary>
        public static void CancelMission(string reason)
        {
            // 记下"本次区域选择内不要再自动加课题曲"；下一次区域选择界面出现会自增 generation 解除。
            _cancelledGeneration = _areaGeneration;
            if (!MissionTaskActive && PendingMissionCharts.Count == 0)
            {
                Log.Info("[map] 课题曲任务已取消（本次区域选择不再添加）: " + reason);
                return;
            }
            Log.Info("[map] 取消课题曲任务: " + reason +
                     " (清掉 " + PendingMissionCharts.Count + " 首)");
            PendingMissionCharts.Clear();
            MissionTaskActive = false;
            MissionSource = "";
        }

        /// <summary>取走课题曲列表（flow 组下一批时用；取走后任务状态清零）。</summary>
        // ---------------------------------------------------------------- 区域课题曲（真实数据）
        // Area bonus songs live in the master data, not on the server:
        //   map/Map{areaId}/Map.xml            -> <BonusMusicId><id>X</id>
        //   mapBonusMusic/MapBonusMusic{X}/MapBonusMusic.xml -> <MusicIds><list><StringID><id>…
        private static readonly Dictionary<int, int> _areaBonusIdCache = new Dictionary<int, int>();
        private static readonly Dictionary<int, int[]> _bonusMusicCache = new Dictionary<int, int[]>();
        private static string _streamingAssets;

        private static string StreamingAssetsRoot()
        {
            if (!string.IsNullOrEmpty(_streamingAssets)) return _streamingAssets;
            try
            {
                string sa = Application.streamingAssetsPath;
                if (!string.IsNullOrEmpty(sa) && !System.IO.Directory.Exists(sa) &&
                    System.IO.Directory.Exists(System.IO.Path.Combine(sa, "StreamingAssets")))
                    sa = System.IO.Path.Combine(sa, "StreamingAssets");
                if (System.IO.Directory.Exists(sa)) _streamingAssets = sa;
            }
            catch (Exception) { }
            return _streamingAssets ?? "";
        }

        /// <summary>Area id of the currently selected region, or 0.</summary>
        public static int CurrentAreaId;

        /// <summary>Music ids already used as 课题 once this session - never queue them twice.</summary>
        public static readonly HashSet<int> PlayedMissionMusicIds = new HashSet<int>();

        /// <summary>Read the area's bonus-music group id from the Map master data (0 when unknown).</summary>
        public static int GetAreaBonusId(int areaId)
        {
            if (areaId <= 0) return 0;
            lock (_areaBonusIdCache)
            {
                int cached;
                if (_areaBonusIdCache.TryGetValue(areaId, out cached)) return cached;
            }
            int bonusId = 0;
            try
            {
                string root = StreamingAssetsRoot();
                if (root.Length > 0)
                {
                    foreach (string pack in System.IO.Directory.GetDirectories(root))
                    {
                        string dir = System.IO.Path.Combine(pack, "map", "Map" + areaId.ToString("000000"));
                        if (!System.IO.Directory.Exists(dir)) continue;
                        string xml = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "Map.xml"));
                        var m = System.Text.RegularExpressions.Regex.Match(xml, "<BonusMusicId>\\s*<id>(\\d+)</id>");
                        if (m.Success) bonusId = int.Parse(m.Groups[1].Value);
                        break;
                    }
                }
            }
            catch (Exception e) { Log.Warn("[map] 读取区域课题曲组失败 area=" + areaId + ": " + e.Message); }
            lock (_areaBonusIdCache) { _areaBonusIdCache[areaId] = bonusId; }
            return bonusId;
        }

        /// <summary>All bonus (课题) music ids of an area, read from mapBonusMusic/MapBonusMusic{groupId}.</summary>
        public static int[] GetAreaBonusMusicIds(int areaId)
        {
            int bonusId = GetAreaBonusId(areaId);
            if (bonusId <= 0) return new int[0];
            lock (_bonusMusicCache)
            {
                int[] cached;
                if (_bonusMusicCache.TryGetValue(bonusId, out cached)) return cached;
            }
            var ids = new List<int>();
            try
            {
                string root = StreamingAssetsRoot();
                if (root.Length > 0)
                {
                    foreach (string pack in System.IO.Directory.GetDirectories(root))
                    {
                        string dir = System.IO.Path.Combine(pack, "mapBonusMusic", "MapBonusMusic" + bonusId.ToString("000000"));
                        if (!System.IO.Directory.Exists(dir)) continue;
                        string xml = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "MapBonusMusic.xml"));
                        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml, "<StringID>\\s*<id>(\\d+)</id>"))
                        {
                            int id = int.Parse(m.Groups[1].Value);
                            if (!ids.Contains(id)) ids.Add(id);
                        }
                        break;
                    }
                }
            }
            catch (Exception e) { Log.Warn("[map] 读取课题曲表失败 group=" + bonusId + ": " + e.Message); }
            int[] result = ids.ToArray();
            lock (_bonusMusicCache) { _bonusMusicCache[bonusId] = result; }
            return result;
        }

        /// <summary>Queue up to <paramref name="max"/> of the current area's bonus songs as mission slots.</summary>
        public static int QueueAreaBonusMissions(int areaId, int max)
        {
            if (areaId <= 0 || max <= 0) return 0;
            CurrentAreaId = areaId;
            int[] ids = GetAreaBonusMusicIds(areaId);
            if (ids.Length == 0) return 0;
            int preferred = Math.Max(0, Math.Min(4, ModConfig.Data.MinLevel));
            int added = 0;
            for (int i = 0; i < ids.Length && added < max; i++)
            {
                int musicId = ids[i];
                // 普通课题曲不限难度：配置难度不存在时退到这首曲子实际存在的难度。
                int level = PickMissionLevel(musicId, preferred);
                if (level < 0) continue;
                bool dup = false;
                for (int k = 0; k < PendingMissionCharts.Count; k++)
                    if (PendingMissionCharts[k].MusicId == musicId) { dup = true; break; }
                if (dup) continue;
                if (PlayedMissionMusicIds.Contains(musicId)) continue;
                PendingMissionCharts.Add(new MissionChart
                {
                    MusicId = musicId,
                    Level = level,
                    Achievement = ScoreHooks.MinClearAchievement,
                    Source = "AreaBonus(" + areaId + ")"
                });
                added++;
                PlayedMissionMusicIds.Add(musicId);   // never queue the same 课题 twice
            }
            if (added > 0)
            {
                MissionTaskActive = true;
                MissionSource = "AreaBonus(" + areaId + ") x" + added;
                Log.Info("[map] 区域课题曲: area=" + areaId + " 组=" + GetAreaBonusId(areaId) +
                         " 表中 " + ids.Length + " 首 → 加入 " + added + " 首 (level=" + preferred + " 起)");
            }
            return added;
        }

        /// <summary>难度选择：优先配置的难度，不存在则退到这首曲子实际带的最低难度；都没有返回 -1。</summary>
        private static int PickMissionLevel(int musicId, int preferred)
        {
            try
            {
                if (ChartCatalog.Exists(musicId, preferred)) return preferred;
                for (int level = 0; level <= 4; level++)
                    if (ChartCatalog.Exists(musicId, level)) return level;
            }
            catch (Exception) { }
            return -1;
        }

        public static MissionChart[] TakeMissionCharts()
        {
            if (PendingMissionCharts.Count == 0) return new MissionChart[0];
            // Dedupe by music id: the master-data queue and the music-select MapTask scan can
            // both contribute, and playing the same chart twice in one credit is useless.
            var unique = new List<MissionChart>();
            for (int i = 0; i < PendingMissionCharts.Count; i++)
            {
                int musicId = PendingMissionCharts[i].MusicId;
                bool dup = false;
                for (int k = 0; k < unique.Count; k++)
                    if (unique[k].MusicId == musicId) { dup = true; break; }
                if (!dup) unique.Add(PendingMissionCharts[i]);
            }
            var result = unique.ToArray();
            PendingMissionCharts.Clear();   // MUST clear: without this the queue grows forever
            MissionTaskActive = false;
            Log.Info("[map] 交出课题曲 " + result.Length + " 首给 flow");
            return result;
        }

        /// <summary>扫描选歌页里的课题曲分类（MapTask / CircleTask / Challenge）。</summary>
        public static void ScanMusicSelect(MusicSelectProcess process)
        {
            if (process == null) return;
            // 用户自己玩时不做扫描/不打日志（只有插件任务运行才需要课题曲信息）
            if (!Navigation.IsTaskActive()) return;
            if (!ModConfig.Data.AutoAddMissionSongs)
            {
                if (MissionTaskActive) CancelMission("配置 AutoAddMissionSongs=false");
                return;
            }

            // 课题曲数据是「选区确认之后」游戏才算出来的（用户实机确认）。
            // 但强切路径下 ContinueProcess/RegionalSelect 的内部 subState 到不了 ToDecide(10)，
            // 只靠 MissionAreaReady 会把识别彻底锁死（实测整轮 0 次扫描）。
            // 所以放宽：只要已经拿到过 SelectedAreaId，就认为选区已发生，允许扫描。
            if (!MissionAreaReady && SelectedAreaId <= 0)
            {
                NavReflect.WarnOnce("mission-no-area",
                    "[map] 还没选区 → 先不读课题曲（等区域确认后自动重扫）");
                return;
            }

            // 本次区域选择里操作员手动换过区 → 不加课题曲（直到下一次区域选择界面出现）
            if (_cancelledGeneration == _areaGeneration && _areaGeneration > 0)
            {
                if (MissionTaskActive || PendingMissionCharts.Count > 0)
                    CancelMission("本次区域选择已被判定为人工换区");
                else
                    NavReflect.WarnOnce("mission-cancelled-gen" + _areaGeneration,
                        "本次区域选择人工换过区，跳过课题曲检测");
                return;
            }

            var found = new List<MissionChart>();
            var sources = new List<string>();
            ScanField(process, "_combineMapTaskDataList", "MapTask", found, sources);
            ScanField(process, "_combineCircleDataList", "CircleTask", found, sources);
            ScanField(process, "_combineChallengeDataList", "Challenge", found, sources);

            bool hasCircleFlag = NavReflect.ToBool(NavReflect.Get(process, "hasCircleTaskMusic"));

            if (found.Count == 0)
            {
                if (MissionTaskActive)
                {
                    Log.Info("[map] 选歌页没有课题曲分类 → 课题任务结束");
                    MissionTaskActive = false;
                    MissionSource = "";
                }
                if (hasCircleFlag)
                    Log.Info("[map] hasCircleTaskMusic=true 但没读到 circle 曲目列表（可能需要登录后才有数据）");
                return;
            }

            var source = string.Join("+", sources.ToArray());
            if (found.Count == PendingMissionCharts.Count && source == MissionSource &&
                MissionTaskActive && SameCharts(found))
                return; // 已知，不重复刷日志

            // Merge instead of replacing: the master-data path (QueueAreaBonusMissions) already
            // queued the area's full 课题 list, and clearing it here left only the one song the
            // music-select screen happened to expose (level 0, no control).
            for (int i = 0; i < found.Count; i++)
            {
                if (PlayedMissionMusicIds.Contains(found[i].MusicId)) continue;   // 已打过的不要再排
                int musicId = found[i].MusicId;
                bool dup = false;
                for (int k = 0; k < PendingMissionCharts.Count; k++)
                    if (PendingMissionCharts[k].MusicId == musicId) { dup = true; break; }
                if (!dup) PendingMissionCharts.Add(found[i]);
            }
            MissionTaskActive = true;
            MissionSource = source;
            Log.Info("[map] 检测到课题曲 " + found.Count + " 首 (" + source + ")，将加入下一批游玩列表（最低通关 " +
                     (ScoreHooks.MinClearAchievement / 10000.0).ToString("0.0000") + "%）");
            for (int i = 0; i < found.Count && i < 10; i++) Log.Info("[map]   课题曲: " + found[i]);
            for (int i = 0; i < found.Count && i < 10; i++)
                if (found[i].StartLife > 0)
                    Log.Warn("[map]   ★挑战曲: " + found[i].MusicId + "-" + found[i].Level +
                             " 生命=" + found[i].StartLife + " 解锁难度=" + found[i].ChallengeUnlockDiff);
        }

        private static bool SameCharts(List<MissionChart> fresh)
        {
            if (fresh.Count != PendingMissionCharts.Count) return false;
            for (int i = 0; i < fresh.Count; i++)
            {
                var a = fresh[i];
                var b = PendingMissionCharts[i];
                if (a.MusicId != b.MusicId || a.Level != b.Level || a.Achievement != b.Achievement) return false;
            }
            return true;
        }

        /// <summary>
        /// 由 flow 在「选区成功」时显式调用（强切路径下 RegionalSelectProcess 的
        /// subState 到不了 ToDecide，内部那段赋值不会执行，课题曲扫描就永远被门控挡死）。
        /// </summary>
        public static void NotifyAreaSelected(int areaId)
        {
            SelectedAreaId = areaId;
            if (string.IsNullOrEmpty(SelectedAreaName)) SelectedAreaName = "area" + areaId;
            MissionAreaReady = true;
            Log.Info("[map] 选区已生效(flow 通知): areaId=" + areaId + " → 允许扫描课题曲");
        }

        /// <summary>
        /// 读取 CombineMusicSelectData.isCircleTaskScore。
        /// 它是 auto-property（&lt;isCircleTaskScore&gt;k__BackingField），只查字段会漏，必须走属性。
        /// 读不到时默认 true（宁可多打，也不要把真课题曲漏掉）。
        /// </summary>
        private static bool ReadCircleTaskFlag(MusicSelectProcess.CombineMusicSelectData entry)
        {
            if (entry == null) return false;
            try
            {
                var prop = entry.GetType().GetProperty("isCircleTaskScore",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (prop != null)
                {
                    object v = prop.GetValue(entry, null);
                    if (v is bool) return (bool)v;
                }
            }
            catch (Exception) { }
            try
            {
                object v = NavReflect.Get(entry, "<isCircleTaskScore>k__BackingField");
                if (v is bool) return (bool)v;
            }
            catch (Exception) { }
            return true;
        }

        /// <summary>
        /// 诊断命令 missiondump：把选歌页每个分类列表里的曲目与标记全部打出来。
        /// 用来确认「ちほー課題曲」分类到底落在哪个 _combine* 字段、isCircleTaskScore 真值如何。
        /// </summary>
        public static void DumpCategories()
        {
            var process = LastMusicSelect;
            if (process == null)
            {
                Log.Warn("[map] missiondump: 当前不在选歌页（没有 MusicSelectProcess 实例）");
                return;
            }
            Log.Info("[map] ==== 选歌页分类诊断 ====");
            Log.Info("[map] hasCircleTaskMusic=" + NavReflect.ToBool(NavReflect.Get(process, "hasCircleTaskMusic")));
            string[] fields =
            {
                "_combineMapTaskDataList", "_combineChallengeDataList", "_combineCircleDataList",
                "_combineBonusDataList", "_combineMusicDataList"
            };
            for (int f = 0; f < fields.Length; f++)
            {
                object raw = null;
                try { raw = NavReflect.Get(process, fields[f]); } catch (Exception) { }
                var list = raw as System.Collections.IEnumerable;
                if (list == null)
                {
                    Log.Info("[map] " + fields[f] + " = null");
                    continue;
                }
                int idx = 0, taskTrue = 0;
                foreach (var item in list)
                {
                    var entry = item as MusicSelectProcess.CombineMusicSelectData;
                    if (entry == null) { idx++; continue; }
                    bool isTask = ReadCircleTaskFlag(entry);
                    if (isTask) taskTrue++;
                    if (idx < 40)
                    {
                        var d = entry.msDetailData;
                        Log.Info("[map]   " + fields[f] + "[" + idx + "] musicId=" + (d != null ? d.musicId : -1) +
                                 " diff=" + (d != null ? d.difficultyId : -1) +
                                 " startLife=" + (d != null ? d.startLife : 0) +
                                 " unlockDiff=" + (d != null ? d.challengeUnlockDiff : 0) +
                                 " isCircleTaskScore=" + isTask);
                    }
                    idx++;
                }
                Log.Info("[map] " + fields[f] + ": 共 " + idx + " 条, isCircleTaskScore=true 的 " + taskTrue + " 条");
            }
            Log.Info("[map] 待加入队列: " + PendingMissionCharts.Count + " 首, MissionTaskActive=" + MissionTaskActive +
                     ", source=" + MissionSource);
        }

        private static void ScanField(MusicSelectProcess process, string field, string source,
            List<MissionChart> found, List<string> sources)
        {
            object raw;
            try { raw = NavReflect.Get(process, field); }
            catch (Exception) { return; }
            if (raw == null) return;

            int before = found.Count;
            if (raw is Array)
            {
                var array = (Array)raw;
                for (int i = 0; i < array.Length; i++) ScanCollection(array.GetValue(i) as IEnumerable, source, found);
            }
            else
            {
                ScanCollection(raw as IEnumerable, source, found);
            }
            if (found.Count > before) sources.Add(source);
        }

        private static void ScanCollection(IEnumerable collection, string source, List<MissionChart> found)
        {
            if (collection == null) return;
            foreach (var item in collection)
            {
                var entry = item as MusicSelectProcess.CombineMusicSelectData;
                // Only real 課題曲 carry isCircleTaskScore; the rest of the list is the area's
                // ちほーボーナス曲 (bonus songs) which we must NOT treat as missions.
                // isCircleTaskScore is an auto-property (<isCircleTaskScore>k__BackingField),
                // so a field-only lookup misses it - read the property explicitly.
                bool isTask = ReadCircleTaskFlag(entry);
                if (ModConfig.Data.Verbose)
                    Log.Info("[map] 候选曲 isCircleTaskScore=" + isTask);
                // The game already filters _combineMapTaskDataList to task songs, and isCircleTaskScore only covers circle tasks - accept either.
                if (!isTask && source != "MapTask")
                {
                    if (ModConfig.Data.Verbose)
                    {
                        int dbgId = 0;
                        try { dbgId = entry.GetID((MAI2System.ConstParameter.ScoreKind)0); } catch (Exception) { }
                        Log.Info("[map] 跳过非课题曲(区域奖励曲): " + dbgId);
                    }
                    continue;
                }
                if (entry == null) continue;
                var data = entry.musicSelectData;
                if (data == null) continue;
                for (int sk = 0; sk < data.Count && sk < 2; sk++)
                {
                    var msd = data[sk];
                    if (msd == null) continue;
                    int musicId;
                    try { musicId = entry.GetID((MAI2System.ConstParameter.ScoreKind)sk); }
                    catch (Exception) { continue; }
                    if (musicId <= 0) continue;

                    int startLife = 0;
                    int challengeDiff = 0;
                    int missionDiff = -1;
                    try
                    {
                        var detail = NavReflect.Get(entry, "msDetailData");
                        if (detail != null)
                        {
                            startLife = NavReflect.ToInt(NavReflect.Get(detail, "startLife"));
                            challengeDiff = NavReflect.ToInt(NavReflect.Get(detail, "challengeUnlockDiff"));
                            // 注意：NavReflect.ToInt(null) 返回 0，取不到时会被误当成「难度 0」，
                            // 之前打出 11975-0（绿谱）就是这个坑。必须先判 null。
                            object dvRaw = NavReflect.Get(detail, "difficultyId");
                            if (dvRaw != null)
                            {
                                int d = NavReflect.ToInt(dvRaw);
                                if (d >= 0 && d <= 6) missionDiff = d;
                            }
                            Log.Info("[map] 课题曲条目详情: difficultyId=" +
                                     (dvRaw == null ? "<取不到>" : dvRaw.ToString()) +
                                     " startLife=" + startLife + " challengeUnlockDiff=" + challengeDiff +
                                     " → missionDiff=" + missionDiff);
                        }
                    }
                    catch (Exception) { }
                    // 课题曲条目自带难度（msDetailData.difficultyId），比「猜最低可用难度」准得多。
                    // 之前打出 11975-0 这种绿谱，就是因为猜到了最低难度。
                    // 课题曲条目里的 difficultyId 不可靠（实机读到 0/缺字段）。统一用难度下限（默认 EXPERT），
                    // 挑战曲用 challengeUnlockDiff；这样不会再出现「11975-0」绿谱。
                    int level = challengeDiff >= 0 ? Math.Max(0, Math.Min(4, challengeDiff)) : Math.Max(0, Math.Min(4, ModConfig.Data.MinLevel));
                    if (level < 0) continue;
                    if (Contains(found, musicId, level)) continue;

                    found.Add(new MissionChart
                    {
                        MusicId = musicId,
                        Level = level,
                        Achievement = ScoreHooks.MinClearAchievement,
                        StartLife = startLife,
                        ChallengeUnlockDiff = challengeDiff,
                        Source = source
                    });
                }
            }
        }

        /// <summary>凑一个"能通过的最低条件"：优先最低的可用难度。</summary>
        private static int ChooseLowestEnabledLevel(MusicSelectProcess.MusicSelectData msd)
        {
            try
            {
                var enable = msd.isEnable as IList;
                if (enable != null && enable.Count > 0)
                {
                    for (int d = 0; d < enable.Count; d++)
                        if (enable[d] is bool && (bool)enable[d]) return d;
                    return -1;
                }
            }
            catch (Exception) { }
            int diff = msd.Difficulty;
            if (diff < 0) diff = 0;
            if (diff > 4) diff = 4;
            return diff;
        }

        private static bool Contains(List<MissionChart> list, int musicId, int level)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].MusicId == musicId && list[i].Level == level) return true;
            return false;
        }
    }
}
