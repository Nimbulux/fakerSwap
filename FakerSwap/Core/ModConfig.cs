using System;
using System.Collections.Generic;
using System.IO;

namespace FakerSwap.Core
{
    /// <summary>Persisted settings.  Stored as a single readable JSON object in FakerSwap/config.json.</summary>
    public sealed class ConfigData
    {
        // ---- login ----
        /// <summary>Access code used for the simulated card swipe. Empty = read DEVICE/aime.txt.</summary>
        public string AimeCode = "";
        /// <summary>Preferred source user id (also used to pick a file in the backup list).</summary>
        public long SourceUserId = 0;

        // ---- source data ----
        /// <summary>Last used source file, remembered so the UI can pre-select it.</summary>
        public string LastSourceFile = "";

        // ---- transfer plan ----
        /// <summary>Difficulty floor: 0=BASIC 1=ADVANCED 2=EXPERT 3=MASTER 4=Re:MASTER. 2 == "red and above".</summary>
        public int MinLevel = 2;
        public int MaxLevel = 4;
        /// <summary>Drop records whose chart does not exist in this game build.</summary>
        public bool SkipMissingSongs = true;
        /// <summary>Records pushed per credit / per UpsertUserAll. Must be 1 or 3 (2 is rejected by the server).</summary>
        public int BatchSize = 3;

        // ---- write / play strategy ----
        /// <summary>Memory | Payload | Both</summary>
        public string WriteMode = "Both";
        /// <summary>Skip | Auto   (Skip = 璺宠繃璋遍潰鐩存帴缁撶畻, Auto = 鑷姩鎵撴瓕鍚庢敼鎴愮哗)</summary>
        public string PlayMode = "Skip";
        /// <summary>Server rejects uploads earlier than 50s after login.</summary>
        public double UploadCooldownSec = 50;
        /// <summary>Minimum gap between two tracks of the same credit (server validates userPlayDate spacing).</summary>
        public double TrackGapSec = 15;
        /// <summary>Watchdog for logout: if the cabinet does not return to Entry within this many seconds, escalate.</summary>
        public double LogoutTimeoutSec = 30;
        /// <summary>true = continue-relogin between batches, false = timeout logout back to entry and swipe again (safer).</summary>
        public bool ContinueRelogin = false;
        /// <summary>How long we wait for the operator to swipe a card before only escalating the hint (never fails).</summary>
        public double LoginWaitTimeoutSec = 120;
        public double BaseOverheadPerCreditSec = 75;
        public bool ConfirmBeforeStart = true;
        /// <summary>Copy the source record verbatim.  When false, dx/rank/combo are regenerated from the achievement.</summary>
        public bool CopyFieldsVerbatim = true;
        /// <summary>Unit expected by GameMonitor.ForceAchivement: TenThousandth | Percent100 | Raw.</summary>
        public string ForceAchievementUnit = "TenThousandth";

        // ---- filler ----
        /// <summary>骞搁殣 = 骞搞仜銇仾銈屻倠闅犮仐銈炽優銉炽儔銇屻亗銈嬨倝銇椼亜, musicId 432 (verified from Music.xml), BASIC chart.</summary>
        public int FillerMusicId = 432;
        public int FillerLevel = 0;
        /// <summary>Extra filler tracks so that a credit is never left with exactly 2 playlogs.</summary>
        public bool PadToFullCredit = true;
        /// <summary>跳过長尺曲（长版歌曲会改变 credit 轨道数，导致 flow 与游戏轨道计数错位）。</summary>
        public bool SkipLongSongs = true;
        /// <summary>长版歌曲改为「不游玩、直接写 UserData + 报文」上传（额外逻辑；默认关）。</summary>
        public bool LongSongDirectWrite = false;
        /// <summary>判定表跨音符类型随机散布（更像真人；关掉则用 data-dev per-type 计数）。</summary>
        public bool RealisticJudgeSpread = true;
        /// <summary>每个需要选择的界面先停几秒交给用户操作（期间释放机台锁）。0=关闭。</summary>
        public double UserHoldSec = 3.0;
        /// <summary>长版歌曲模式：PlayWithOne=正常游玩（长曲占2轨+1首普通，2 playlogs）/ Skip / DirectWrite。</summary>
        public string LongSongMode = "PlayWithOne";

        // ---- map / area ----
        public double AreaSelectTimeoutSec = 8.0;
        public bool AutoAddMissionSongs = true;

        /// <summary>一次任务里最多追加多少个课题曲批次（防止每批都追加导致永远跑不完）。</summary>
        public int MaxMissionCredits = 3;

        /// <summary>普通课题曲的目标成绩（1/10000 %）。默认 80%（通关线）。挑战曲会自动用 100%。</summary>
        public int MissionAchievement = 800000;

        /// <summary>填充轨（性隐 BASIC）的目标成绩；绿谱没人看，拉满拿 AP / 跑图加成。</summary>
        public int FillerAchievement = 1005000;

        /// <summary>填充轨是否真的写成绩（false = 老行为：跳过且不改成绩）。</summary>
        public bool FillerScoreEnable = true;

        /// <summary>解锁写入每批最多多少条 userItem（防止一次上传太大被服务端拒）。</summary>
        public int UnlockBatchRows = 200;

        /// <summary>专用跑区模式最多跑多少个 credit（1 credit = 3 轨）后自动停。</summary>
        public int CrawlCreditLimit = 999;

        /// <summary>这些 musicId 直接跳过不传（临时规避：某些谱面在选歌页选不出来，会拖死任务）。</summary>
        public List<int> SkipMusicIds = new List<int>();

        /// <summary>强制覆盖：目标已有成绩与源不同就按源成绩重写（允许降分）；完全相同则跳过。</summary>
        public bool OverwriteScores = true;

        /// <summary>游玩次数覆盖为 源次数 × 该百分比（向下取整，至少 1）。0=不改，100=保持源次数。</summary>
        public int PlayCountPercent = 50;

        /// <summary>覆盖模式下，达成率小数部分 ≥ .9000 的做「镜像上取整」：
        /// 97.9191% → 98.0809%（整数位+1，1-小数作为新小数）。</summary>
        public bool AchievementMirrorUp = true;

        /// <summary>覆盖模式下 DX 分数在源值上加 [DxBonusMin, DxBonusMax]（0/0=不加），
        /// 并夹到谱面理论最大值（note 数 × 3）以内，保证是打得出来的。</summary>
        public int DxBonusMin = 5;
        public int DxBonusMax = 10;

        /// <summary>源成绩低于该值时自动抬到该值以上再传（1/10000 %，默认 80%）。0=关闭。
        /// FC/AP 等徽章保持源标记不变；DX 由判定表按新成绩重算。</summary>
        public int MinAchievement = 800000;

        /// <summary>自动刷卡（写 aime.txt + 回车）。默认 false：只停在登录页，等人工刷卡。</summary>
        public bool AutoSwipe = false;
        /// <summary>Areas the operator wants to crawl (empty = all / whatever the game offers).</summary>
        public List<int> CrawlAreaIds = new List<int>();
        /// <summary>Fallback: when every planned area is complete but the transfer is not, start over.</summary>
        public bool CrawlAllAreasFallback = true;

        /// <summary>选区页出现后停下来等人工确认（command: areaok）再进入区域，便于观察/调试。</summary>
        public bool ManualAreaSelect = false;

        // ---- ticket select（选票页）----
        /// <summary>默认不选票（测试服会写异常数据）：在选票页调用游戏 TimeUp。true = 下一次不自动跳过，交给人工选。</summary>
        public bool TicketUseNext = false;
        /// <summary>保持：每次选票页都自动使用上一次人工选择的票（TicketLastManualId）。</summary>
        public bool TicketUseKeep = false;
        /// <summary>上一次人工选择的票 ID（0 = 没选过/不用票）。</summary>
        public int TicketLastManualId = 0;
        /// <summary>
        /// 允许「MODE SELECT 强切 → 直接 new RegionalSelectProcess」。
        /// 默认 false：实机确认这条会破坏游戏自己的 ModeSelect→RegionalSelect 过渡，
        /// 导致下一轨 GameProcess.OnStart 崩溃、而且课题曲数据也不会被算出来。
        /// </summary>
        public bool ForceJumpAreaSelect = false;

        // ---- window ----
        public bool WindowTopMost = true;
        public bool ShowOverlayBanner = true;
        public int WindowX = 60;
        public int WindowY = 60;
        public int WindowW = 1000;
        public int WindowH = 780;

        // ---- safety ----
        /// <summary>While true the mod refuses every account-mutating operation (read-only mode).</summary>
        public bool SafeNoWrite = true;
        /// <summary>Swallow all cabinet/menu input while a task runs.</summary>
        public bool LockInput = false;   // 默认不锁输入：等待时可自由玩，插件仍用注入按键
        public bool Verbose = false;

        // ---- DEV / TEST ONLY (keep both false for real use) ----
        /// <summary>
        /// DEV ONLY - keep false in production.
        /// Allows using the SAME account as source and target (needed to validate the whole
        /// pipeline on a single test account).  When false the flow refuses to run.
        /// </summary>
        public bool AllowSelfTransfer = false;
        /// <summary>
        /// DEV ONLY - keep false in production.
        /// Do not skip charts the target account already has at the same/better achievement.
        /// Without this a self-transfer plan is always empty.
        /// </summary>
        public bool ForceOverwriteExisting = false;
        /// <summary>DEV ONLY - auto press YES on the "new Aime" registration screen (creates the account).</summary>
        public bool AllowConfirmNewUser = false;
        /// <summary>
        /// C "Duration" (default): really wait out the chart length before skipping, so the play log
        /// time matches the song.  B "Judge": skip immediately but still rebuild a self-consistent
        /// judge table (fast, for testing).
        /// </summary>
        public string PlayRealism = "Duration";
        /// <summary>
        /// DEV ONLY.  Bypass the map-result screen (only needed for accounts whose map data is
        /// broken - bypassing it on a healthy account makes the screen stack pile up and crash).
        /// </summary>
        public bool MapResultGuard = false;
    }

    public static class ModConfig
    {
        public static ConfigData Data = new ConfigData();
        public static string LastError { get; private set; }

        public static void Load()
        {
            LastError = null;
            try
            {
                if (!File.Exists(Fs.ConfigPath))
                {
                    Save();
                    return;
                }
                var text = File.ReadAllText(Fs.ConfigPath);
                JsonValue root;
                string err;
                if (!JsonValue.TryParse(text, out root, out err))
                {
                    LastError = "config parse failed: " + err;
                    Log.Error(LastError);
                    return;
                }
                if (root.Kind != JsonKind.Object) return;
                var d = new ConfigData();
                d.AimeCode = root["AimeCode"].AsString(d.AimeCode);
                d.SourceUserId = root["SourceUserId"].AsLong(d.SourceUserId);
                d.LastSourceFile = root["LastSourceFile"].AsString(d.LastSourceFile);
                d.MinLevel = root["MinLevel"].AsInt(d.MinLevel);
                d.MaxLevel = root["MaxLevel"].AsInt(d.MaxLevel);
                d.SkipMissingSongs = root["SkipMissingSongs"].AsBool(d.SkipMissingSongs);
                d.BatchSize = root["BatchSize"].AsInt(d.BatchSize);
                d.WriteMode = root["WriteMode"].AsString(d.WriteMode);
                d.PlayMode = root["PlayMode"].AsString(d.PlayMode);
                d.UploadCooldownSec = root["UploadCooldownSec"].AsDouble(d.UploadCooldownSec);
                d.TrackGapSec = root["TrackGapSec"].AsDouble(d.TrackGapSec);
                d.LogoutTimeoutSec = root["LogoutTimeoutSec"].AsDouble(d.LogoutTimeoutSec);
                d.ContinueRelogin = root["ContinueRelogin"].AsBool(d.ContinueRelogin);
                d.LoginWaitTimeoutSec = root["LoginWaitTimeoutSec"].AsDouble(d.LoginWaitTimeoutSec);
                d.BaseOverheadPerCreditSec = root["BaseOverheadPerCreditSec"].AsDouble(d.BaseOverheadPerCreditSec);
                d.ConfirmBeforeStart = root["ConfirmBeforeStart"].AsBool(d.ConfirmBeforeStart);
                d.CopyFieldsVerbatim = root["CopyFieldsVerbatim"].AsBool(d.CopyFieldsVerbatim);
                d.ForceAchievementUnit = root["ForceAchievementUnit"].AsString(d.ForceAchievementUnit);
                d.FillerMusicId = root["FillerMusicId"].AsInt(d.FillerMusicId);
                d.FillerLevel = root["FillerLevel"].AsInt(d.FillerLevel);
                d.PadToFullCredit = root["PadToFullCredit"].AsBool(d.PadToFullCredit);
                d.SkipLongSongs = root["SkipLongSongs"].AsBool(d.SkipLongSongs);
                d.LongSongDirectWrite = root["LongSongDirectWrite"].AsBool(d.LongSongDirectWrite);
                d.RealisticJudgeSpread = root["RealisticJudgeSpread"].AsBool(d.RealisticJudgeSpread);
                d.UserHoldSec = root["UserHoldSec"].AsDouble(d.UserHoldSec);
                d.LongSongMode = root["LongSongMode"].AsString(d.LongSongMode);
                d.AreaSelectTimeoutSec = root["AreaSelectTimeoutSec"].AsDouble(d.AreaSelectTimeoutSec);
                d.AutoAddMissionSongs = root["AutoAddMissionSongs"].AsBool(d.AutoAddMissionSongs);
                d.AutoSwipe = root["AutoSwipe"].AsBool(d.AutoSwipe);
                d.MaxMissionCredits = root["MaxMissionCredits"].AsInt(d.MaxMissionCredits);
                d.MissionAchievement = root["MissionAchievement"].AsInt(d.MissionAchievement);
                d.FillerAchievement = root["FillerAchievement"].AsInt(d.FillerAchievement);
                d.FillerScoreEnable = root["FillerScoreEnable"].AsBool(d.FillerScoreEnable);
                d.UnlockBatchRows = root["UnlockBatchRows"].AsInt(d.UnlockBatchRows);
                if (d.UnlockBatchRows < 10) d.UnlockBatchRows = 10;
                if (d.UnlockBatchRows > 1000) d.UnlockBatchRows = 1000;
                d.CrawlCreditLimit = root["CrawlCreditLimit"].AsInt(d.CrawlCreditLimit);
                if (d.CrawlCreditLimit < 1) d.CrawlCreditLimit = 1;
                d.OverwriteScores = root["OverwriteScores"].AsBool(d.OverwriteScores);
                d.PlayCountPercent = root["PlayCountPercent"].AsInt(d.PlayCountPercent);
                if (d.PlayCountPercent < 0) d.PlayCountPercent = 0;
                if (d.PlayCountPercent > 100) d.PlayCountPercent = 100;
                d.AchievementMirrorUp = root["AchievementMirrorUp"].AsBool(d.AchievementMirrorUp);
                d.DxBonusMin = root["DxBonusMin"].AsInt(d.DxBonusMin);
                d.DxBonusMax = root["DxBonusMax"].AsInt(d.DxBonusMax);
                if (d.DxBonusMin < 0) d.DxBonusMin = 0;
                if (d.DxBonusMax < 0) d.DxBonusMax = 0;
                if (d.DxBonusMax < d.DxBonusMin) d.DxBonusMax = d.DxBonusMin;
                d.MinAchievement = root["MinAchievement"].AsInt(d.MinAchievement);
                if (d.MinAchievement < 0) d.MinAchievement = 0;
                if (d.MinAchievement > 1010000) d.MinAchievement = 1010000;
                d.SkipMusicIds = new List<int>();
                var skipArr = root["SkipMusicIds"];
                if (skipArr.Kind == JsonKind.Array)
                {
                    for (int si = 0; si < skipArr.Count; si++)
                    {
                        int skipId = skipArr[si].AsInt(-1);
                        if (skipId > 0) d.SkipMusicIds.Add(skipId);
                    }
                }
                d.ManualAreaSelect = root["ManualAreaSelect"].AsBool(d.ManualAreaSelect);
                d.TicketUseNext = root["TicketUseNext"].AsBool(d.TicketUseNext);
                d.TicketUseKeep = root["TicketUseKeep"].AsBool(d.TicketUseKeep);
                d.TicketLastManualId = root["TicketLastManualId"].AsInt(d.TicketLastManualId);
                d.CrawlAllAreasFallback = root["CrawlAllAreasFallback"].AsBool(d.CrawlAllAreasFallback);
                d.CrawlAreaIds = new List<int>();
                var crawlArr = root["CrawlAreaIds"];
                if (crawlArr.Kind == JsonKind.Array)
                {
                    for (int ci = 0; ci < crawlArr.Count; ci++)
                    {
                        int areaId = crawlArr[ci].AsInt(-1);
                        if (areaId >= 0) d.CrawlAreaIds.Add(areaId);
                    }
                }
                d.WindowTopMost = root["WindowTopMost"].AsBool(d.WindowTopMost);
                d.ShowOverlayBanner = root["ShowOverlayBanner"].AsBool(d.ShowOverlayBanner);
                d.WindowX = root["WindowX"].AsInt(d.WindowX);
                d.WindowY = root["WindowY"].AsInt(d.WindowY);
                d.WindowW = root["WindowW"].AsInt(d.WindowW);
                d.WindowH = root["WindowH"].AsInt(d.WindowH);
                d.SafeNoWrite = root["SafeNoWrite"].AsBool(d.SafeNoWrite);
                d.LockInput = root["LockInput"].AsBool(d.LockInput);
                d.Verbose = root["Verbose"].AsBool(d.Verbose);
                // DEV ONLY: keep false for real use.
                d.AllowSelfTransfer = root["AllowSelfTransfer"].AsBool(d.AllowSelfTransfer);
                d.ForceOverwriteExisting = root["ForceOverwriteExisting"].AsBool(d.ForceOverwriteExisting);
                d.AllowConfirmNewUser = root["AllowConfirmNewUser"].AsBool(d.AllowConfirmNewUser);
                d.PlayRealism = root["PlayRealism"].AsString(d.PlayRealism);
                d.MapResultGuard = root["MapResultGuard"].AsBool(d.MapResultGuard);
                if (d.BatchSize < 1) d.BatchSize = 1;
                if (d.BatchSize > 3) d.BatchSize = 3;
                if (d.BatchSize == 2) d.BatchSize = 3;   // 2-playlog packets are rejected by the server
                Data = d;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log.Error("config load failed", e);
            }
        }

        public static void Save()
        {
            try
            {
                Fs.EnsureDirs();
                var d = Data;
                var o = JsonValue.NewObject();
                o["_comment"] = JsonValue.Of("FakerSwap config. Edit while the game is closed; most values are read on the fly.");
                o["AimeCode"] = JsonValue.Of(d.AimeCode);
                o["SourceUserId"] = JsonValue.Of(d.SourceUserId);
                o["LastSourceFile"] = JsonValue.Of(d.LastSourceFile);
                o["MinLevel"] = JsonValue.Of(d.MinLevel);
                o["MaxLevel"] = JsonValue.Of(d.MaxLevel);
                o["SkipMissingSongs"] = JsonValue.Of(d.SkipMissingSongs);
                o["BatchSize"] = JsonValue.Of(d.BatchSize);
                o["WriteMode"] = JsonValue.Of(d.WriteMode);
                o["PlayMode"] = JsonValue.Of(d.PlayMode);
                o["UploadCooldownSec"] = JsonValue.Of(d.UploadCooldownSec);
                o["TrackGapSec"] = JsonValue.Of(d.TrackGapSec);
                o["LogoutTimeoutSec"] = JsonValue.Of(d.LogoutTimeoutSec);
                o["ContinueRelogin"] = JsonValue.Of(d.ContinueRelogin);
                o["LoginWaitTimeoutSec"] = JsonValue.Of(d.LoginWaitTimeoutSec);
                o["BaseOverheadPerCreditSec"] = JsonValue.Of(d.BaseOverheadPerCreditSec);
                o["ConfirmBeforeStart"] = JsonValue.Of(d.ConfirmBeforeStart);
                o["CopyFieldsVerbatim"] = JsonValue.Of(d.CopyFieldsVerbatim);
                o["ForceAchievementUnit"] = JsonValue.Of(d.ForceAchievementUnit);
                o["FillerMusicId"] = JsonValue.Of(d.FillerMusicId);
                o["FillerLevel"] = JsonValue.Of(d.FillerLevel);
                o["PadToFullCredit"] = JsonValue.Of(d.PadToFullCredit);
                o["SkipLongSongs"] = JsonValue.Of(d.SkipLongSongs);
                o["LongSongDirectWrite"] = JsonValue.Of(d.LongSongDirectWrite);
                o["RealisticJudgeSpread"] = JsonValue.Of(d.RealisticJudgeSpread);
                o["UserHoldSec"] = JsonValue.Of(d.UserHoldSec);
                o["LongSongMode"] = JsonValue.Of(d.LongSongMode);
                o["AreaSelectTimeoutSec"] = JsonValue.Of(d.AreaSelectTimeoutSec);
                o["MaxMissionCredits"] = JsonValue.Of(d.MaxMissionCredits);
                o["MissionAchievement"] = JsonValue.Of(d.MissionAchievement);
                o["FillerAchievement"] = JsonValue.Of(d.FillerAchievement);
                o["FillerScoreEnable"] = JsonValue.Of(d.FillerScoreEnable);
                o["UnlockBatchRows"] = JsonValue.Of(d.UnlockBatchRows);
                o["CrawlCreditLimit"] = JsonValue.Of(d.CrawlCreditLimit);
                o["OverwriteScores"] = JsonValue.Of(d.OverwriteScores);
                o["PlayCountPercent"] = JsonValue.Of(d.PlayCountPercent);
                o["AchievementMirrorUp"] = JsonValue.Of(d.AchievementMirrorUp);
                o["DxBonusMin"] = JsonValue.Of(d.DxBonusMin);
                o["DxBonusMax"] = JsonValue.Of(d.DxBonusMax);
                o["MinAchievement"] = JsonValue.Of(d.MinAchievement);
                var skipSave = JsonValue.NewArray();
                if (d.SkipMusicIds != null)
                    foreach (var skipId in d.SkipMusicIds) skipSave.Add(JsonValue.Of(skipId));
                o["SkipMusicIds"] = skipSave;
                o["ManualAreaSelect"] = JsonValue.Of(d.ManualAreaSelect);
                o["TicketUseNext"] = JsonValue.Of(d.TicketUseNext);
                o["TicketUseKeep"] = JsonValue.Of(d.TicketUseKeep);
                o["TicketLastManualId"] = JsonValue.Of(d.TicketLastManualId);
                o["AutoAddMissionSongs"] = JsonValue.Of(d.AutoAddMissionSongs);
                o["AutoSwipe"] = JsonValue.Of(d.AutoSwipe);
                var crawlSave = JsonValue.NewArray();
                if (d.CrawlAreaIds != null)
                    foreach (var areaId in d.CrawlAreaIds) crawlSave.Add(JsonValue.Of(areaId));
                o["CrawlAreaIds"] = crawlSave;
                o["CrawlAllAreasFallback"] = JsonValue.Of(d.CrawlAllAreasFallback);
                o["WindowTopMost"] = JsonValue.Of(d.WindowTopMost);
                o["ShowOverlayBanner"] = JsonValue.Of(d.ShowOverlayBanner);
                o["WindowX"] = JsonValue.Of(d.WindowX);
                o["WindowY"] = JsonValue.Of(d.WindowY);
                o["WindowW"] = JsonValue.Of(d.WindowW);
                o["WindowH"] = JsonValue.Of(d.WindowH);
                o["SafeNoWrite"] = JsonValue.Of(d.SafeNoWrite);
                o["LockInput"] = JsonValue.Of(d.LockInput);
                o["Verbose"] = JsonValue.Of(d.Verbose);
                // DEV ONLY: keep false for real use.
                o["AllowSelfTransfer"] = JsonValue.Of(d.AllowSelfTransfer);
                o["ForceOverwriteExisting"] = JsonValue.Of(d.ForceOverwriteExisting);
                o["AllowConfirmNewUser"] = JsonValue.Of(d.AllowConfirmNewUser);
                o["PlayRealism"] = JsonValue.Of(d.PlayRealism);
                o["MapResultGuard"] = JsonValue.Of(d.MapResultGuard);
                File.WriteAllText(Fs.ConfigPath, o.ToJson(true));
            }
            catch (Exception e)
            {
                Log.Error("config save failed", e);
            }
        }
    }
}
