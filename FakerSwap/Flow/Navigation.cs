using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using FakerSwap.Core;
using MAI2.Util;
using Manager;
using Process;
using UnityEngine;

namespace FakerSwap.Flow
{
    /// <summary>
    /// 与游戏自身 UI 打交道的全部动作：跳过登录后的各个提示页、进选歌页、按 musicId+level 选中、
    /// 确认进入、立刻结算、继续/放弃续关。
    ///
    /// 设计原则（很重要，改之前先读）：
    ///  1. 幂等：所有方法都可以被 RebirthFlow.Tick() 每帧无脑调用；返回 false 表示“还没到/没做成，下帧再来”。
    ///  2. 不改成绩：除了 SkipCurrentChart 里显式消费 ScoreHooks 的期望成绩，本文件不碰任何分数。
    ///  3. 不猜签名：私有成员/版本间改名成员一律走 NavReflect，Install 时把可用性打进日志。
    ///  4. 不依赖 flow-dev 的 ProcessHooks：进程栈自己看，GameStageTracker 只作为附加信息。
    /// </summary>
    public static class Navigation
    {
        // ============================================================================================
        //  时间常量：TimeEstimator 用这些算“每次进选歌页的固定开销”
        // ============================================================================================

        /// <summary>进入一次选歌页的固定开销：fade + 列表构建 + BGM/资产加载（秒）。</summary>
        public const double MusicSelectFixedOverheadSec = 8.0;

        /// <summary>每次选曲 + 难度 + 确认（列表定位/Clamp/GameManager 写入）的开销（秒）。</summary>
        public const double ChartSelectOverheadSec = 2.0;

        /// <summary>选歌页 → TrackStart → GameProcess 就绪的过场时间（秒）。</summary>
        public const double StartSelectionOverheadSec = 3.5;

        /// <summary>跳过谱面 → 结算数据就绪的时间（秒）。</summary>
        public const double SkipToSettleSec = 2.5;

        /// <summary>结算 → 回到选歌页 / 进入 Continue 的时间（秒）。</summary>
        public const double ResultToSelectSec = 4.5;

        /// <summary>服务端要求同一 credit 内相邻 playlog 间隔 ≥ 15s（见 docs/upload-protocol.md）。</summary>
        public const double MinTrackGapSec = 15.0;

        /// <summary>
        /// 一个 credit（最多 3 轨）的固定开销，不含曲长。给 TimeEstimator.Estimate(..., creditOverheadSec, ...) 用。
        /// </summary>
        public static double EstimateCreditOverheadSec(bool skipCharts)
        {
            double perTrack = ChartSelectOverheadSec + StartSelectionOverheadSec + ResultToSelectSec;
            if (skipCharts) perTrack += SkipToSettleSec;
            // 每个选择界面会先停 UserHoldSec 秒给用户操作（MODE/区域/票券/伙伴/选歌 4 屏/续关 ≈ 8 次）
            double holdSec = 8.0 * Math.Max(0.0, ModConfig.Data.UserHoldSec);
            return MusicSelectFixedOverheadSec + 3.0 * perTrack + holdSec;
        }

        // ============================================================================================
        //  输入注入（只在 Entry 确认页这种"ボタンをおしてください"的界面用）
        // ============================================================================================

        /// <summary>Entry 确认页是否允许合成按键（默认开；合成按键比 DecideEntry 更贴近真实玩家操作）。</summary>
        public static bool UseSyntheticButtons = true;

        /// <summary>确认用按键：实机若 Button04 不对，运行时可改（Button01..Button08）。</summary>
        public static string ConfirmButtonName = "Button04";

        /// <summary>备用确认按键（每次尝试轮换用，避免某个版本只认某一个键）。</summary>
        public static string ConfirmButtonNameAlt = "Button05";

        // ============================================================================================
        //  可调开关 / 外部桥接
        // ============================================================================================

        /// <summary>登录委托（flow-dev 的 LoginHooks.RequestLogin）。为空时自动反射绑定。</summary>
        public static Func<string, bool> LoginHandler;

        /// <summary>登出委托（flow-dev 的 LoginHooks.RequestLogoutByTimeout）。为空时自动反射绑定。</summary>
        public static Func<bool> LogoutHandler;

        /// <summary>是否自动推进 Entry 之后的各个菜单页。</summary>
        public static bool AutoAdvance = true;

        /// <summary>是否自动在 Entry 确认页按“是”（刷卡后的「このAimeでプレイを開始しますか？」）。</summary>
        public static bool AutoAdvanceEntry = true;

        /// <summary>
        /// Entry 自动推进是否只在**插件任务运行中**启用（默认 true）。
        /// 用户自己玩游戏时（RebirthFlow 处于 Idle / 未锁机台）绝不允许碰 Entry 输入，
        /// 否则合成 YES 会打断读卡、把用户踢成游客流程（实机事故）。
        /// </summary>
        public static bool RequireTaskActiveForEntry = true;

        /// <summary>测试用：忽略任务状态强制允许 Entry 自动推进（默认关）。</summary>
        public static bool ForceEntryAdvanceWhenIdle = false;

        /// <summary>
        /// 是否允许对「ConfirmNewUser（新规 Aime 建号确认）」自动按「是」（默认 false）。
        ///
        /// 实机：换一张未注册的新 aime 时，服务端分配 uid 但 userName 为空/rating 0，
        /// 游戏停在「新規Aimeでプレイしますか？ [NO/YES]」；这里必须由用户明确授权建号才按。
        /// 每个 EntryProcess 实例**只按一次**，避免重复建号。
        /// config.json 键名：<c>AllowConfirmNewUser</c>（Lead 加了就自动读，不加也能用静态字段设置）。
        /// </summary>
        public static bool AllowConfirmNewUser = false;

        /// <summary>是否强制每轨之间至少间隔 MinTrackGapSec（服务端校验需要）。</summary>
        public static bool RespectTrackGap = true;

        /// <summary>filler/中止轨是否立刻走游戏原生 TrackSkip。false 则只标记不结算（一般不要关）。</summary>
        public static bool SettleFillerImmediately = true;

        // ============================================================================================
        //  状态
        // ============================================================================================

        private static bool _installed;
        private static HarmonyLib.Harmony _harmony;

        private static bool _atMusicSelect;
        private static double _musicSelectEnteredAt;

        // SelectChart
        private static uint _selProcessId;
        private static int _selMusicId = -1;
        private static int _selLevel = -1;
        private static int _selScoreKind;
        private static int _selCategory = -1;
        private static int _selIndex = -1;
        private static double _subPressAt = -100;
        private static string _musicSubState = "";
        private static double _musicSubStateAt = -1000;

        /// <summary>当前选歌子状态（Genre/Music/Difficulty/Menu…），供 flow 诊断卡屏。</summary>
        public static string MusicSubState { get { return _musicSubState; } }

        /// <summary>当前选歌子状态已经持续多少秒（&lt;0 = 未知）。</summary>
        public static double MusicSubStateAge
        {
            get { return _musicSubStateAt < -999 ? -1 : Time.realtimeSinceStartup - _musicSubStateAt; }
        }
        private static string _selectDiagKey;

        // StartCurrentSelection
        private static uint _startProcessId;
        private static bool _startFallbackSent;
        private static double _startAt;
        private static int _startAttempts;

        // SkipCurrentChart
        private static uint _skipProcessId;
        private static double _skipAt;
        private static double _lastTrackStartAt = -1000;
        private static double _lastTrackSettledAt = -1000;
        private static int _gapWarnedForMusicId = -1;

        // 菜单页限速
        private static uint _fadeFromId;
        private static double _fadeAt;
        private static double _infoSkipAt = -1000;
        private static double _logoutAt = -1000;

        // Entry
        private static double _entryConfirmAt = -1000;
        private static int _entryConfirmCount;
        private static int _entryStateSeen = -1;
        private static int _entryStateMode = -1;
        private static double _entryStateAt = -1000;
        private static double _autoDriveAt = -1000;
        private static bool _entryCandidatesLogged;
        private static double _lastStackLogAt = -1000;
        private static uint _entryProcessId;
        private static int _entryNudgeCount;
        private static bool _entryDone;
        private static int _entryStopReason = -1;   // 0=已登录, 1=尝试次数到顶, 2=状态/模式未知
        private static int _newUserConfirmCount;    // ConfirmNewUser 每个 EntryProcess 的上限计数
        private static int _newUserLastMode = -999; // ConfirmNewUser 最近一次已按的 ProcMode

        // 登录成功后的"受付 / 模式选择"屏推进
        private static int _postLoginAttempts;
        private static int _postLoginNextCount;
        private static double _postLoginAt = -1000;
        private static bool _postLoginLogged;
        private static int _postLoginRapidFrames;
        private static double _postLoginRapidAt = -1000;
        private static bool _postLoginRapidDone;
        private static bool _downloadProcessTried;
        private static uint _modeSelectProcessId;
        private static double _modeSelectAt = -1000;
        private static int _modeSelectAttempts;
        private static double _modeSelectForcedAt = -1000;
        private static bool _modeSelectGaveUp;
        private static double _modeSelectStuckSince = -1000;
        private static int _modeSelectHoldFrames;
        private static double _modeSelectHoldLastAt = -1000;
        private static int _modeSelectPhase;
        private static double _modeSelectPhaseAt = -1000;
        private static bool _modeSelectStateWritten;
        private static bool _modeSelectOkTried;
        private static bool _modeSelectReachedLogged;
        private static bool _modeSelectWaitLogged;
        private static bool _logoutModeSelectLogged;
        private static double _modeSelectOkAt = -1000;
        private static int _modeSelectOkTries;

        // 写入阶段「MODE SELECT 按 OK 开始 credit」的连点状态
        private static int _okPhase;
        private static double _okPhaseAt = -1000;
        private static int _okAnimFrames;
        private static int _okButtonIdx;
        private static bool _okHoldStarted;
        private static double _okWaitLogAt = -1000;
        private static double _okRegionalSince = -1000;
        private static double _areaManualLogAt = -1000;
        private static double _charaPageAt = -1000;
        private static double _charaPageLogAt = -1000;

        // 登录成功→到选歌页 的总时限（超时交回 flow，不再注入）
        private static double _loginSuccessAt = -1000;
        private static bool _postLoginGiveUp;
        private static bool _postLoginGiveUpLogged;

        // PlInformation（プレイ情報/隐私提示）跳过
        private static double _plInfoAt = -1000;
        private static int _plInfoAttempts;
        private static double _plInfoFadeAt = -1000;

        /// <summary>连点 timeskip 的总帧数（≈1~2s 连续快速按；用户实机确认这是正确按法）。</summary>
        public static int ModeSelectRapidPressFrames = 120;

        // 登出时"连点结束受付"状态
        private static bool _logoutRequested;
        private static double _logoutLastBurstAt = -1000;
        private static int _logoutBurstCount;

        // 登出时"强制倒计时归零"（让游戏自己超时 → Title → 游戏自己发 UserLogout）
        private static double _countdownZeroAt = -1000;
        private static int _countdownZeroAttempts;
        private static double _countdownZeroFirstAt = -1000;
        private static bool _countdownZeroStageLogged;
        private static bool _logoutDoneLogged;

        /// <summary>登录成功后多少秒仍未到选歌页就停止一切导航注入（交回 flow 的兜底 Fail+登出）。</summary>
        public static double PostLoginTimeoutSec = 25.0;

        /// <summary>写入阶段（Transferring）从登录到开始 credit 的时限：比读取阶段宽松（连点 OK 需要时间）。</summary>
        public static double WritePhaseModeSelectTimeoutSec = 90.0;

        private static FlowPhase _lastSeenPhase = FlowPhase.Idle;

        /// <summary>
        /// 阶段切换时重置"登录→到选歌页"的 25s 计时。
        /// 否则目标账号在 TargetReading 登录成功、经历 Planning/AwaitingConfirm（可能几分钟）后才进 Transferring，
        /// 25s 早就超时，会误伤写入阶段的 MODE SELECT OK 按不动。
        /// </summary>
        internal static void TrackPhaseChange()
        {
            try
            {
                var phase = RebirthFlow.Phase;
                if (phase == _lastSeenPhase) return;
                _lastSeenPhase = phase;
                if (phase == FlowPhase.Transferring)
                {
                    _loginSuccessAt = -1000;
                    _postLoginGiveUp = false;
                    _postLoginGiveUpLogged = false;
                    _postLoginLogged = false;
                    _postLoginRapidDone = false;
                    _postLoginRapidFrames = 0;
                }
            }
            catch (Exception) { }
        }

        /// <summary>每个 EntryProcess 实例最多尝试多少次按键（防止刷屏；到顶停手等人工/flow 超时）。</summary>
        public static int MaxEntryAttempts = 30;

        /// <summary>
        /// ConfirmPlay 卡在 SelectInWait 时，是否允许在多次失败后强推状态机（`_mode: SelectInWait→Select`）。
        /// 只在 `_isConfirmNewUser == false`（老卡）时生效；新卡分支永远不强推。
        /// </summary>
        public static bool AllowEntryModeForce = true;

        // ConfirmPlay 卡住诊断
        private static int _confirmPlayStuckTicks;
        private static int _confirmPlayLastMode = -1;
        private static bool _confirmPlayDiagLogged;
        private static bool _confirmPlayWaitZeroed;
        private static bool _confirmPlayModeForced;
        private static int _entryExecInjections;
        private static string _confirmPlayLastDiag = "";
        private static double _lastExecInjectAt = -1000;
        // 边沿触发：每个 ProcMode 只注入有限次数，避免每帧刷把过场动画重置
        private static int _confirmPlayInjectMode = -1;
        private static int _confirmPlayInjectAtTicks;
        private static int _confirmPlayInjectAttempts;

        // Continue
        private static double _continueAt = -1000;

        private static Func<string, bool> _autoLogin;
        private static Func<bool> _autoLogout;

        // ============================================================================================
        //  Install
        // ============================================================================================

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("fakerswap.flow.navigation");
                _harmony.PatchAll(typeof(UserDefinedPatches.ProcessContainerCtorPatch));
                _harmony.PatchAll(typeof(UserDefinedPatches.MusicSelectOnStartPatch));
                _harmony.PatchAll(typeof(UserDefinedPatches.EntryProcessOnUpdatePatch));
                _harmony.PatchAll(typeof(UserDefinedPatches.ConfirmPlayExecPatch));
                _harmony.PatchAll(typeof(UserDefinedPatches.ModeSelectUpdateInputPatch));
                _harmony.PatchAll(typeof(UserDefinedPatches.PlInformationSkipPatch));
                // dump 一次触摸映射（GamePanelDown 维度 / TouchAreaA..E），方便定位该按哪个区域
                DumpTouchMapOnce();
                _harmony.PatchAll(typeof(UserDefinedPatches.ForceTimeUpDetectPatch));
                _harmony.PatchAll(typeof(UserDefinedPatches.TouchPanelPatch));
                _harmony.PatchAll(typeof(UserDefinedPatches.GameProcessOnUpdateGuardPatch));
                NavProcess.MarkContainerPatchInstalled();
                Log.Info("[nav] Navigation 已安装 (harmony id=fakerswap.flow.navigation, Entry 自动推进=" +
                         AutoAdvanceEntry + ")");
                try
                {
                    var v = NavReflect.Get(ModConfig.Data, "AllowConfirmNewUser");
                    if (v != null) AllowConfirmNewUser = NavReflect.ToBool(v);
                    if (AllowConfirmNewUser)
                        Log.Info("[nav] AllowConfirmNewUser=true（config）→ ConfirmNewUser 会自动按一次「是」建号");
                }
                catch (Exception) { }
            }
            catch (Exception e)
            {
                Log.Error("[nav] Navigation 安装失败（导航仍可工作，只是少了容器捕获/进选歌页回调）", e);
            }
            Probe();
        }

        /// <summary>把关键游戏 API 的可用性打一份日志；实机出现“点不动”时先看这一行。</summary>
        public static void Probe()
        {
            int ok = 0, miss = 0;
            var missing = new StringBuilder();
            var good = new StringBuilder();

            Action<string, bool> check = (label, present) =>
            {
                if (present)
                {
                    ok++;
                    if (good.Length > 0) good.Append(", ");
                    good.Append(label);
                }
                else
                {
                    miss++;
                    if (missing.Length > 0) missing.Append(", ");
                    missing.Append(label);
                }
            };

            var msp = typeof(MusicSelectProcess);
            check("MusicSelect.GameStart", NavReflect.HasMethod(msp, "GameStart", 0));
            check("MusicSelect.CombineMusicDataList", NavReflect.FindProperty(msp, "CombineMusicDataList") != null);
            check("MusicSelect.CurrentCategorySelect", NavReflect.FindProperty(msp, "CurrentCategorySelect") != null);
            check("MusicSelect.CurrentMusicSelect", NavReflect.FindProperty(msp, "CurrentMusicSelect") != null);
            check("MusicSelect.DifficultySelectIndex", NavReflect.FindProperty(msp, "DifficultySelectIndex") != null);
            check("MusicSelect.CurrentDifficulty", NavReflect.FindProperty(msp, "CurrentDifficulty") != null);
            check("MusicSelect.SortDecidePlayer", NavReflect.FindProperty(msp, "SortDecidePlayer") != null);
            check("MusicSelect.ScoreType", NavReflect.FindProperty(msp, "ScoreType") != null);
            check("MusicSelect.ClampMusicIndex", NavReflect.HasMethod(msp, "ClampMusicIndex", 0));
            check("MusicSelect.GetLevelToListPositoin", NavReflect.HasMethod(msp, "GetLevelToListPositoin", 2));

            var entryData = typeof(MusicSelectProcess.CombineMusicSelectData);
            check("CombineMusicSelectData.GetID", NavReflect.HasMethod(entryData, "GetID", 1));
            check("CombineMusicSelectData.musicSelectData", NavReflect.FindField(entryData, "musicSelectData") != null);
            var msData = typeof(MusicSelectProcess.MusicSelectData);
            check("MusicSelectData.Difficulty", NavReflect.FindField(msData, "Difficulty") != null);
            check("MusicSelectData.isEnable", NavReflect.FindField(msData, "isEnable") != null);

            var gp = typeof(GameProcess);
            check("GameProcess._sequence", NavReflect.FindField(gp, "_sequence") != null);
            check("GameProcess._monitors", NavReflect.FindField(gp, "_monitors") != null);
            check("GameProcess._message", NavReflect.FindField(gp, "_message") != null);
            check("GameProcess.UpdateSubbMonitorData", NavReflect.HasMethod(gp, "UpdateSubbMonitorData", 1));
            check("GameProcess.SetRelease", NavReflect.HasMethod(gp, "SetRelease", 0));
            check("GameProcess.IsPartyPlay", NavReflect.HasMethod(gp, "IsPartyPlay", 0));

            check("FadeProcess(container,from,to)", NavReflect.HasCtor(typeof(FadeProcess), 3));
            check("TrackStartProcess(container)", NavReflect.HasCtor(typeof(TrackStartProcess), 1));
            check("MusicSelectProcess(container)", NavReflect.HasCtor(typeof(MusicSelectProcess), 1));

            var region = typeof(RegionalSelectProcess);
            check("Regional.TimeUp", NavReflect.HasMethod(region, "TimeUp", 0));
            check("Regional.SelectCursolIndex", NavReflect.HasMethod(region, "SelectCursolIndex", 1));
            check("Regional.GetUserMapDatas", NavReflect.HasMethod(region, "GetUserMapDatas", 1));
            check("Regional._userMapDataList", NavReflect.FindField(region, "_userMapDataList") != null);
            check("Regional._selectCursolIndex", NavReflect.FindField(region, "_selectCursolIndex") != null);
            check("Regional._subStates", NavReflect.FindField(region, "_subStates") != null);

            check("Information._state", NavReflect.FindField(typeof(Process.Information.InformationProcess), "_state") != null);
            check("Entry.GetCurrentState", NavReflect.HasMethod(typeof(EntryProcess), "GetCurrentState", 2));
            check("Entry.SetNextProcess", NavReflect.HasMethod(typeof(EntryProcess), "SetNextProcess", 0));
            check("Entry.OnUpdate", NavReflect.HasMethod(typeof(EntryProcess), "OnUpdate", 0));
            check("Entry.IsTimeCounting", NavReflect.HasMethod(typeof(EntryProcess), "IsTimeCounting", 1));
            check("Entry.IsFastSkip", NavReflect.HasMethod(typeof(EntryProcess), "IsFastSkip", 1));
            check("Entry.DecrementTimerSecond", NavReflect.HasMethod(typeof(EntryProcess), "DecrementTimerSecond", 1));
            check("ModeSelect.TimeUp", NavReflect.HasMethod(typeof(Process.ModeSelect.ModeSelectProcess), "TimeUp", 0));
            check("ModeSelect.TimeSkipStart", NavReflect.HasMethod(typeof(Process.ModeSelect.ModeSelectProcess), "TimeSkipStart", 0));
            check("ModeSelect.OKStart", NavReflect.HasMethod(typeof(Process.ModeSelect.ModeSelectProcess), "OKStart", 0));
            check("ModeSelect.isOKTimerZero", NavReflect.HasMethod(typeof(Process.ModeSelect.ModeSelectProcess), "isOKTimerZero", 0));
            check("ModeSelect._state", NavReflect.FindField(typeof(Process.ModeSelect.ModeSelectProcess), "_state") != null);
            check("ModeSelect.TimeSkipButtonAnim", NavReflect.HasMethod(typeof(Process.ModeSelect.ModeSelectProcess), "TimeSkipButtonAnim", 1));
            check("ModeSelect.UpdateInput", NavReflect.HasMethod(typeof(Process.ModeSelect.ModeSelectProcess), "UpdateInput", 1));
            check("PlInformation._state", NavReflect.FindField(typeof(Process.PlInformationProcess), "_state") != null);
            check("PlInformationMonitor.IsPlayPlInfoEnd", NavReflect.HasMethod(typeof(Monitor.PlInformationMonitor), "IsPlayPlInfoEnd", 0));
            check("EntryMonitor.DecideEntry", NavReflect.HasMethod(typeof(Monitor.Entry.EntryMonitor), "DecideEntry", 0));
            check("EntryMonitor.ResponseYes", NavReflect.HasMethod(typeof(Monitor.Entry.EntryMonitor), "ResponseYes", 0));
            check("EntryMonitor.ResponseNo", NavReflect.HasMethod(typeof(Monitor.Entry.EntryMonitor), "ResponseNo", 0));
            check("EntryMonitor.InputResponse", NavReflect.FindProperty(typeof(Monitor.Entry.EntryMonitor), "InputResponse") != null);
            check("ConfirmEntry.StateEntry", typeof(Process.Entry.State.ConfirmEntry) != null);
            check("ConfirmNewUser.StateEntry", typeof(Process.Entry.State.ConfirmNewUser) != null);
            check("StateEntry.StartYesNoResponse", NavReflect.HasMethod(typeof(Process.Entry.State.StateEntry), "StartYesNoResponse", 0));
            check("ConfirmPlay.Exec", NavReflect.HasMethod(typeof(Process.Entry.State.ConfirmPlay), "Exec", 1));
            check("Context.GetCurrentState", NavReflect.HasMethod(typeof(Process.Entry.State.Context), "GetCurrentState", 2));
            check("InputManager.SetButtonDown", NavReflect.HasMethod(typeof(Manager.InputManager), "SetButtonDown", 2));
            check("ResultProcess.OnStart", NavReflect.HasMethod(typeof(Process.ResultProcess), "OnStart", 0));

            var mon = typeof(Monitor.GameMonitor);
            check("GameMonitor.Seek", NavReflect.HasMethod(mon, "Seek", 1));
            check("GameMonitor.ForceAchivement", NavReflect.HasMethod(mon, "ForceAchivement", 2));

            var score = typeof(GameScoreList);
            check("GameScoreList.SetForceAchivement", NavReflect.HasMethod(score, "SetForceAchivement", 2));
            check("GameScoreList.SetTrackSkip", NavReflect.HasMethod(score, "SetTrackSkip", 0));
            check("GameScoreList.ScoreTotal", NavReflect.FindField(score, "ScoreTotal") != null);

            var play = typeof(GamePlayManager);
            check("GamePlayManager.Initialize", NavReflect.HasMethod(play, "Initialize", 1));
            check("GamePlayManager.SetSyncResult", NavReflect.HasMethod(play, "SetSyncResult", 2));
            check("GamePlayManager.GetGameScore", NavReflect.HasMethod(play, "GetGameScore", 2));
            check("GamePlayManager.GetAchivement", NavReflect.HasMethod(play, "GetAchivement", 2));
            check("GamePlayManager.SetTrackSkipFlag/Frag",
                NavReflect.HasMethodAny(play, new[] { "SetTrackSkipFlag", "SetTrackSkipFrag" }, 2));

            var notes = typeof(NotesManager);
            check("NotesManager.StartPlay", NavReflect.HasMethod(notes, "StartPlay", 1));
            check("NotesManager.Pause", NavReflect.HasMethod(notes, "Pause", 1));

            var gm = typeof(GameManager);
            check("GameManager.SelectMusicID", NavReflect.FindProperty(gm, "SelectMusicID") != null);
            check("GameManager.SelectDifficultyID", NavReflect.FindProperty(gm, "SelectDifficultyID") != null);
            check("GameManager.SelectGhostID", NavReflect.FindProperty(gm, "SelectGhostID") != null);
            check("GameManager.SetMaxTrack", NavReflect.HasMethod(gm, "SetMaxTrack", 0));
            check("GameManager.IsLongMusic(1.65)", NavReflect.FindProperty(gm, "IsLongMusic") != null);
            check("GameManager.IsSelectContinue", NavReflect.FindProperty(gm, "IsSelectContinue") != null);
            check("GameManager.MusicTrackNumber", NavReflect.FindField(gm, "MusicTrackNumber") != null);

            check("DataManager.GetMusic", NavReflect.HasMethod(typeof(DataManager), "GetMusic", 1));
            check("DataManager.IsLong", NavReflect.HasMethod(typeof(DataManager), "IsLong", 1));
            check("ContinueProcess._state", NavReflect.FindField(typeof(ContinueProcess), "_state") != null);
            check("ProcessManager.ForceTimeUp", NavReflect.HasMethod(typeof(ProcessManager), "ForceTimeUp", 0));
            check("GenericProcess._timerController", NavReflect.FindField(typeof(Process.GenericProcess), "_timerController") != null);
            check("TimerController.SetTimeZero", NavReflect.HasMethod(typeof(TimerController), "SetTimeZero", 0));
            check("TimerController.CountDownSecond", NavReflect.FindProperty(typeof(TimerController), "CountDownSecond") != null);
            check("TimerController.DecrementTime", NavReflect.HasMethod(typeof(TimerController), "DecrementTime", 1));

            Log.Info("[nav] API probe: ok=" + ok + " miss=" + miss);
            if (miss > 0) Log.Warn("[nav] API 缺失(" + miss + "): " + missing);
            try
            {
                if (ModConfig.Data.Verbose) Log.Info("[nav] API 可用: " + good);
            }
            catch (Exception) { }
            Log.Info("[nav] 游戏版本: " + MAI2System.ConstParameter.GameIDStr + " / " +
                     MAI2System.ConstParameter.NowGameVersion);
        }

        // ============================================================================================
        //  Harmony patch（都用 Navigation 自己的 Harmony id）
        // ============================================================================================

        private static class UserDefinedPatches
        {
            [HarmonyLib.HarmonyPatch(typeof(ProcessDataContainer), HarmonyLib.MethodType.Constructor)]
            internal static class ProcessContainerCtorPatch
            {
                [HarmonyLib.HarmonyPostfix]
                private static void Postfix(ProcessDataContainer __instance)
                {
                    NavProcess.SetContainer(__instance);
                }
            }

            [HarmonyLib.HarmonyPatch(typeof(MusicSelectProcess), "OnStart")]
            internal static class MusicSelectOnStartPatch
            {
                [HarmonyLib.HarmonyPostfix]
                private static void Postfix(MusicSelectProcess __instance)
                {
                    try { OnMusicSelectStarted(__instance); }
                    catch (Exception e) { Log.Error("[nav] 选歌页 OnStart 处理失败", e); }
                }
            }

            /// <summary>
            /// EntryProcess 每帧更新后驱动一次 Entry 确认页（0.5s 节流）。
            /// 这样即使 RebirthFlow 的 TickLogin 只轮询登录状态、忘了调导航，登录页也能自动过。
            /// GenericProcess 压在 EntryProcess 上面不影响：EntryProcess.OnUpdate 仍然每帧执行。
            /// </summary>
            [HarmonyLib.HarmonyPatch(typeof(EntryProcess), "OnUpdate")]
            internal static class EntryProcessOnUpdatePatch
            {
                [HarmonyLib.HarmonyPostfix]
                private static void Postfix()
                {
                    AutoDriveEntry();
                }
            }

            /// <summary>
            /// ConfirmPlay 状态机每帧 Exec 之前注入响应（只在该状态真的 WaitResponse(17) 时注入）。
            /// Prefix 时机在状态机读取 InputResponse 之前，避免"Postfix 设置 → 被 ResetResponse 清掉 → 从未被消费"。
            /// </summary>
            [HarmonyLib.HarmonyPatch(typeof(Process.Entry.State.ConfirmPlay), "Exec")]
            internal static class ConfirmPlayExecPatch
            {
                [HarmonyLib.HarmonyPrefix]
                private static void Prefix(Process.Entry.State.ConfirmPlay __instance)
                {
                    OnConfirmPlayExecPrefix(__instance);
                }
            }

            /// <summary>
            /// ModeSelect 的输入处理：任务中停在 ModeSelect ≥2s 后，连续 3 帧"模拟长按"
            /// （TimeSkipButtonAnim(Button05) + 私有 TimeUp），并 **跳过原 UpdateInput**（return false）——
            /// 这是 Sinmai-Assist InfinityTimer 里真实按键路径的行为；不跳过的话游戏自己每帧的
            /// UpdateInput 可能把结束标志/长按累积清掉，导致 TimeUp 永远无效。
            /// </summary>
            [HarmonyLib.HarmonyPatch(typeof(Process.ModeSelect.ModeSelectProcess), "UpdateInput")]
            internal static class ModeSelectUpdateInputPatch
            {
                [HarmonyLib.HarmonyPrefix]
                private static bool Prefix(Process.ModeSelect.ModeSelectProcess __instance)
                {
                    try { return !HandleModeSelect(__instance, true); }
                    catch (Exception e)
                    {
                        NavReflect.WarnOnce("modeselect-input", "ModeSelect.UpdateInput 注入失败: " + e.Message);
                        return true;
                    }
                }
            }

            /// <summary>
            /// PlInformation（プレイ情報/隐私提示）跳过：任务运行时让 `IsPlayPlInfoEnd` 直接返回 true
            /// （AquaMai / Sinmai-Assist 同款）。用户自己玩时不改行为。
            /// </summary>
            [HarmonyLib.HarmonyPatch(typeof(Monitor.PlInformationMonitor), "IsPlayPlInfoEnd")]
            internal static class PlInformationSkipPatch
            {
                [HarmonyLib.HarmonyPostfix]
                private static void Postfix(ref bool __result)
                {
                    // 只在"任务运行中 + 写入阶段 + 用户数据已下载完"时跳过；
                    // 读取阶段/下载窗口绝不跳（否则 GetUserMusicApi 不会发出，账号数据为空）。
                    if (!IsTaskActive()) return;
                    if (!IsModeSelectAutomationAllowed()) return;
                    if (!IsUserScoreDataLoaded()) return;
                    __result = true;
                }
            }

            /// <summary>
            /// 检测"登出被调用"：flow 的登出循环会反复调 `ProcessManager.ForceTimeUp()`，
            /// 我们借此把 `_logoutRequested` 打开，让 ModeSelect/EntryProcess 的每帧驱动开始连点 timeskip / 倒计时归零
            /// （实机：ForceTimeUp 对受付屏无效，60s 后 logout gave up）。
            /// 用户自己玩的超时登出不介入（IsTaskActive 为 false）。
            /// </summary>
            [HarmonyLib.HarmonyPatch(typeof(Manager.ProcessManager), "ForceTimeUp", new System.Type[0])]
            internal static class ForceTimeUpDetectPatch
            {
                [HarmonyLib.HarmonyPostfix]
                private static void Postfix()
                {
                    try { OnForceTimeUpCalled(); }
                    catch (Exception) { }
                }
            }

            /// <summary>
            /// 触摸注入：写 `InputManager.GamePanelDown/GamePanelPush` 后，同一帧游戏自己的 `UpdateTouchPanel`
            /// 会覆盖，所以在他的 Postfix 里重新补写（保持 `Navigation.Touch` 请求的帧数）。
            /// </summary>
            [HarmonyLib.HarmonyPatch(typeof(Manager.InputManager), "UpdateTouchPanel")]
            internal static class TouchPanelPatch
            {
                [HarmonyLib.HarmonyPostfix]
                private static void Postfix()
                {
                    try { PumpPendingTouches(); }
                    catch (Exception) { }
                }
            }

            /// <summary>
            /// `GameProcess.OnUpdate` 的防御性前缀：上一局的 GameProcess 残留、状态已拆时（monitor/message/
            /// container/GamePlayManager 任一为空），跳过原版避免 NRE 把游戏打崩。
            /// 实机 01:39：刷卡重登时旧 GameProcess 还在栈上跑 OnUpdate → NullReferenceException。
            /// 日志每 5s 最多一条。
            /// </summary>
            [HarmonyLib.HarmonyPatch(typeof(Process.GameProcess), "OnUpdate")]
            internal static class GameProcessOnUpdateGuardPatch
            {
                [HarmonyLib.HarmonyPrefix]
                private static bool Prefix(Process.GameProcess __instance)
                {
                    try
                    {
                        if (__instance == null) return true;
                        if (FakerSwap.Core.CrashFixes.IsGameProcessBroken(__instance))
                            return Skip("OnStart 曾失败（半初始化，OnUpdate 必 NRE）");
                        var monitors = NavReflect.Get(__instance, "_monitors") as Array;
                        if (monitors == null || monitors.Length == 0) return Skip("monitors 为空");
                        var messages = NavReflect.Get(__instance, "_message") as Array;
                        if (messages == null) return Skip("_message 为空");
                        if (NavProcess.ContainerOf(__instance) == null) return Skip("container 为空");
                        if (MAI2.Util.Singleton<GamePlayManager>.Instance == null) return Skip("GamePlayManager 为空");
                    }
                    catch (Exception e)
                    {
                        return Skip(e.GetType().Name + ": " + e.Message);
                    }
                    return true;
                }

                private static bool Skip(string reason)
                {
                    double now = Time.realtimeSinceStartup;
                    if (now - _lastGameProcessGuardLogAt > 5.0)
                    {
                        _lastGameProcessGuardLogAt = now;
                        Log.Warn("[nav] GameProcess.OnUpdate 防御跳过 (state 已拆): " + reason +
                                 "; stack=[" + NavProcess.StackNames() + "]");
                    }
                    return false;
                }
            }

            // 注意：NameEntryProcess（新账号名字确认页）**故意不做任何 patch/注入**。
            // 用户明确要求："先不要自动起名字，会出问题的，很复杂" —— 名字页交给人工。
        }

        /// <summary>
        /// 强制回收栈上的 `GameProcess`（OnStart 半初始化 / 卡住的残留）。
        /// 只走游戏自己的 `SetRelease` + `ProcessManager.ReleaseProcess`，绝不杀进程。
        /// 返回实际回收的数量。
        /// </summary>
        public static int ReleaseGameProcesses(string why)
        {
            int released = 0;
            try
            {
                var list = ProcessHooks.GetProcessList();
                var pm = ProcessHooks.Manager;
                if (list == null || pm == null) return 0;
                var targets = new System.Collections.Generic.List<ProcessBase>();
                foreach (var ctrl in list)
                {
                    if (ctrl == null || ctrl.Process == null) continue;
                    if (!(ctrl.Process is global::Process.GameProcess)) continue;
                    targets.Add(ctrl.Process);
                }
                for (int i = 0; i < targets.Count; i++)
                {
                    var process = targets[i];
                    try { NavReflect.Call(process, "SetRelease"); }
                    catch (Exception) { }
                    try
                    {
                        pm.ReleaseProcess(process);
                        released++;
                    }
                    catch (Exception e)
                    {
                        Log.Warn("[nav] 释放 GameProcess 失败: " + e.Message);
                    }
                }
                if (released > 0)
                    Log.Warn("[nav] 强制回收 GameProcess ×" + released + " (" + why + "); stack=[" +
                             NavProcess.StackNames() + "]");
            }
            catch (Exception e)
            {
                Log.Error("[nav] ReleaseGameProcesses 失败", e);
            }
            return released;
        }

        /// <summary>
        /// 写真拍摄流程（PhotoShootProcess）。
        ///
        /// 实机 22:35：登录后进入「写真を撮りますか？」确认页，nav 没处理它 → 确认窗口一直挂在
        /// 最上层，后面的选歌/结算画面叠在它上面（用户截图：是/否按钮和选歌页重叠）。
        /// 运行时 IL（`ildump PhotoShootProcess`）显示确认页 `InputPhotoCofirm`：
        ///   Button05 → 拍照；**Button04 → 不拍**：CloseWindow + ChangePhotographingSequence(Confirmation)
        ///   → PrepareTimer → GotoNextProcess。
        ///
        /// 这里走「不拍」这条路：直接关窗口 + 切到 Confirmation + `GotoNextProcess`，不再依赖输入层竞争。
        /// **只有栈上真的存在 PhotoShootProcess 才动作** —— 国服/未开启拍照的账号没有这个流程，
        /// 调用是彻底无副作用的。
        /// </summary>
        public static bool SkipPhotoShoot(int monitor = 0)
        {
            if (!IsTaskActive()) return false;   // 用户自己玩时绝不自动跳过写真确认
            try
            {
                var process = NavProcess.FindByName("PhotoShootProcess");
                if (process == null)
                {
                    _photoShootAt = -1000;
                    _photoShootSeqLogged = -1;
                    return false;
                }
                double now = Time.realtimeSinceStartup;
                if (now - _photoShootAt < 1.0) return false;
                _photoShootAt = now;

                int seq = -1;
                Array sequences = NavReflect.Get(process, "_photoSequences") as Array;
                if (sequences != null && monitor >= 0 && monitor < sequences.Length)
                    seq = NavReflect.ToInt(sequences.GetValue(monitor));

                if (seq != _photoShootSeqLogged)
                {
                    _photoShootSeqLogged = seq;
                    Log.Info("[nav] 写真流程: sequence=" + PhotoShootSequenceName(seq) +
                             "（确认页将自动选「不拍」）; stack=[" + NavProcess.StackNames() + "]");
                }

                // 0 = FirstInformation（说明页）: 按 4 号键跳过
                if (seq == 0)
                {
                    InputInject.Press(monitor, Manager.InputManager.ButtonSetting.Button04);
                    return true;
                }
                // 1 = PhotoCofirm（「写真を撮りますか？」）: 走游戏自己的「不拍」分支
                if (seq == 1)
                {
                    var pm = NavProcess.Manager;
                    if (pm != null)
                    {
                        try { pm.CloseWindow(monitor); }
                        catch (Exception e) { NavReflect.WarnOnce("photo-close", "关闭写真确认窗失败: " + e.Message); }
                    }
                    object nextEnum = sequences != null
                        ? Enum.ToObject(sequences.GetType().GetElementType(), 2)
                        : (object)2;
                    string err;
                    NavReflect.TryCall(process, "ChangePhotographingSequence", new object[] { nextEnum, monitor }, out err);
                    NavReflect.TryCall(process, "GotoNextProcess", new object[0], out err);
                    Log.Info("[nav] 写真确认页 → 已选「不拍」并关闭窗口" + (err != null ? " (warn: " + err + ")" : ""));
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("photo-shoot-skip", "写真流程跳过失败: " + e.Message);
                return false;
            }
        }

        private static double _photoShootAt = -1000;
        private static int _photoShootSeqLogged = -1;

        private static string PhotoShootSequenceName(int seq)
        {
            switch (seq)
            {
                case 0: return "FirstInformation(0)";
                case 1: return "PhotoCofirm(1)";
                case 2: return "Confirmation(2)";
                case 3: return "FrameSelect(3)";
                case 4: return "Preparation(4)";
                case 5: return "Photographing(5)";
                case 6: return "Adjustment(6)";
                case 7: return "Cleanup(7)";
                case 8: return "Cancel(8)";
                case 9: return "None(9)";
                default: return "?" + seq;
            }
        }

        /// <summary>只找**未 Released**的同名进程（游戏常把旧屏留在栈上，Released 的不算还在显示）。</summary>
        private static ProcessBase FindLiveByName(params string[] names)
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
                        string n = ctrl.Process.GetType().Name;
                        for (int k = 0; k < names.Length; k++)
                            if (n == names[k]) return ctrl.Process;
                    }
                    return null;
                }
            }
            catch (Exception) { }
            return NavProcess.FindByName(names);
        }

        internal static void OnMusicSelectStarted(MusicSelectProcess process)
        {
            int prevMusic = _selMusicId;
            int prevLevel = _selLevel;
            _atMusicSelect = true;
            _musicSelectEnteredAt = Time.realtimeSinceStartup;
            _selProcessId = 0;
            _selMusicId = -1;
            _selLevel = -1;
            _startProcessId = 0;
            _startAttempts = 0;
            _selectDiagKey = null;      // 每个 credit 允许再打一次定位失败诊断
            ResetPostLoginState();   // 已到选歌页：清掉 25s 计时/受付/ModeSelect 状态
            // 用户自己玩时不打日志（避免每次进选歌页一条），只在插件任务运行时输出
            if (IsTaskActive())
            {
                Log.Info("[nav] 进入选歌页 (player=" + SafePlayerIndex(process) + ")" +
                         (prevMusic >= 0 ? "; 上一首选中 " + prevMusic + "-" + prevLevel + " 已清空" : ""));
            }
            try { MapHooks.ScanMusicSelect(process); }
            catch (Exception e) { Log.Error("[nav] MapHooks.ScanMusicSelect 失败", e); }
            try
            {
                if (ModConfig.Data.Verbose) DumpMusicSelectSummary(process);
            }
            catch (Exception) { }
        }

        /// <summary>Verbose 模式下把选歌列表的结构打出来（实机排查“找不到曲”用）。</summary>
        private static void DumpMusicSelectSummary(MusicSelectProcess process)
        {
            var lists = process.CombineMusicDataList;
            if (lists == null) { Log.Info("[nav] 选歌列表: null"); return; }
            int entries = 0;
            for (int c = 0; c < lists.Count; c++)
                if (lists[c] != null) entries += lists[c].Count;
            Log.Info("[nav] 选歌列表: " + lists.Count + " 个分类 / " + entries + " 条；" +
                     "SortDecidePlayer=" + process.SortDecidePlayer + " ScoreType=" + process.ScoreType);

            int logged = 0;
            for (int c = 0; c < lists.Count && logged < 8; c++)
            {
                var column = lists[c];
                if (column == null || column.Count == 0) continue;
                var sb = new StringBuilder();
                for (int i = 0; i < column.Count && i < 3; i++)
                {
                    var entry = column[i];
                    if (entry == null) continue;
                    int id0 = -1, id1 = -1;
                    try { id0 = entry.GetID(MAI2System.ConstParameter.ScoreKind.Standard); } catch (Exception) { }
                    try { id1 = entry.GetID(MAI2System.ConstParameter.ScoreKind.Deluxe); } catch (Exception) { }
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(id0);
                    if (id1 != id0) sb.Append("/" ).Append(id1);
                }
                Log.Info("[nav]   分类[" + c + "] " + column.Count + " 条: " + sb);
                logged++;
            }
        }

        // ============================================================================================
        //  阶段判断
        // ============================================================================================

        public static bool AtTitle
        {
            get
            {
                return GameStageTracker.Current == GameStage.Title ||
                       NavProcess.HasName("TitleProcess", "StartupProcess", "PowerOnProcess");
            }
        }

        public static bool AtEntry
        {
            get { return NavProcess.Any<EntryProcess>() || GameStageTracker.Current == GameStage.Entry; }
        }

        public static bool AtMusicSelect
        {
            get { return NavProcess.Any<MusicSelectProcess>() || GameStageTracker.Current == GameStage.MusicSelect; }
        }

        public static bool InGameplay
        {
            get
            {
                return NavProcess.Any<GameProcess>() || NavProcess.Any<TrackStartProcess>() ||
                       GameStageTracker.Current == GameStage.GamePlay;
            }
        }

        public static bool AtAreaSelect
        {
            get { return NavProcess.Any<RegionalSelectProcess>(); }
        }

        public static bool AtContinue
        {
            get { return NavProcess.Any<ContinueProcess>(); }
        }

        /// <summary>已经请求过启动当前这轨、但游戏还没切走。</summary>
        public static bool StartRequested
        {
            get { return _startProcessId != 0 && _startAttempts > 0; }
        }

        /// <summary>距离上一轨开始已经过去多少秒。</summary>
        public static double SecondsSinceTrackStart
        {
            get { return Time.realtimeSinceStartup - _lastTrackStartAt; }
        }

        /// <summary>距离可以开始下一轨还差多少秒（服务端 15s 间隔要求）。</summary>
        public static double RemainingTrackGapSec
        {
            get
            {
                if (!RespectTrackGap) return 0;
                double left = MinTrackGapSec - (Time.realtimeSinceStartup - _lastTrackStartAt);
                return left > 0 ? left : 0;
            }
        }

        /// <summary>距离上一轨结算已经过去多少秒（flow 用来判断是否可以继续/上传）。</summary>
        public static double SecondsSinceTrackSettled
        {
            get { return Time.realtimeSinceStartup - _lastTrackSettledAt; }
        }

        /// <summary>进入选歌页后过了多少秒（不在选歌页时返回 -1）。</summary>
        public static double MusicSelectElapsed
        {
            get { return _atMusicSelect ? Time.realtimeSinceStartup - _musicSelectEnteredAt : -1; }
        }

        public static string Describe()
        {
            return "stage=" + GameStageTracker.Current + " stack=[" + NavProcess.StackNames() + "]";
        }

        // ============================================================================================
        //  通用"直调 / 直写"工具（任何界面卡住都能直接调用按钮对应函数 + 直接写终态字段）
        //  注意：net35 没有 System.ValueTuple，所以用 (bool 返回值 + out string error) 代替元组。
        // ============================================================================================

        /// <summary>反射调用实例方法（返回 true=调用成功）。args 为 null 表示无参。</summary>
        public static bool CallInstanceMethod(object instance, string method, object[] args, out string error)
        {
            error = null;
            if (instance == null) { error = "instance is null"; return false; }
            NavReflect.TryCall(instance, method, args, out error);
            return error == null;
        }

        /// <summary>反射写实例字段（int 写入 enum 字段时自动转枚举）。返回 true=写入成功。</summary>
        public static bool SetInstanceField(object instance, string field, object value, out string error)
        {
            error = null;
            if (instance == null) { error = "instance is null"; return false; }
            if (string.IsNullOrEmpty(field)) { error = "field is null"; return false; }
            var info = NavReflect.FindField(instance.GetType(), field);
            if (info == null)
            {
                error = "字段缺失 " + instance.GetType().Name + "." + field;
                return false;
            }
            try
            {
                object boxed = value;
                if (value is int && info.FieldType.IsEnum) boxed = Enum.ToObject(info.FieldType, value);
                info.SetValue(instance, boxed);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>读实例字段的整数值（枚举也还原成 int）；失败返回 fallback。</summary>
        public static int GetInstanceInt(object instance, string field, int fallback = -1)
        {
            try
            {
                var value = NavReflect.Get(instance, field);
                return value == null ? fallback : NavReflect.ToInt(value);
            }
            catch (Exception) { return fallback; }
        }

        // ============================================================================================
        //  触摸注入（MODE SELECT 的 OK / 结算页 NEXT / 选歌按钮 都是触摸区域，不是机台按键）
        //  做法：写 InputManager 私有静态 bool[,,] GamePanelDown / GamePanelPush，
        //  并在 InputManager.UpdateTouchPanel 的 Postfix 里每帧补写（否则同一帧被游戏更新覆盖）。
        // ============================================================================================

        private struct PendingTouch
        {
            public int Monitor;
            public int AreaIndex;
            public int Remaining;
        }

        private static readonly List<PendingTouch> PendingTouches = new List<PendingTouch>();
        private static double _lastTouchLogAt = -1000;

        /// <summary>
        /// 在指定 monitor 的指定触摸区域按下 <paramref name="frames"/> 帧（默认 2 帧）。
        /// 例：`Navigation.Touch(0, Manager.InputManager.TouchPanelArea.D4)`。
        /// </summary>
        public static bool Touch(int monitor, Manager.InputManager.TouchPanelArea area, int frames = 2)
        {
            try
            {
                int areaIndex = Convert.ToInt32(area);

                // 安全闸门（实机 03:51 教训）：在 MODE SELECT / 选歌页上，A/B 组（0..15）是屏幕顶部/上环，
                // MODE SELECT 左上角就是「設定」按钮 —— 误触会弹出設定菜单打乱状态机。菜单页一律禁止 A/B 组。
                bool menuPage = NavProcess.Any<Process.ModeSelect.ModeSelectProcess>() ||
                                NavProcess.Any<MusicSelectProcess>();
                if (menuPage && areaIndex >= 0 && areaIndex <= 15)
                {
                    NavReflect.WarnOnce("touch-menu-safe",
                        "[nav] Touch 拒绝：菜单页(MODE SELECT/选歌)禁止 A/B 组触摸区 area=" + area +
                        "(" + areaIndex + ")（防止误触左上角設定）; stack=[" + NavProcess.StackNames() + "]");
                    return false;
                }

                if (frames < 1) frames = 1;
                bool ok = InjectPanelTouch(monitor, areaIndex, true);
                PendingTouches.Add(new PendingTouch { Monitor = monitor, AreaIndex = areaIndex, Remaining = frames });
                Log.Info("[nav] touch inject area=" + area + "(" + areaIndex + ") monitor=" + monitor +
                         " frames=" + frames + " ok=" + ok + "; stack=[" + NavProcess.StackNames() + "]");
                return ok;
            }
            catch (Exception e)
            {
                Log.Error("[nav] Touch 注入失败", e);
                return false;
            }
        }

        /// <summary>按触摸区域名（如 "D4"/"A1"）注入，便于日志/配置里写字符串。</summary>
        public static bool Touch(int monitor, string areaName, int frames = 2)
        {
            try
            {
                var area = (Manager.InputManager.TouchPanelArea)Enum.Parse(
                    typeof(Manager.InputManager.TouchPanelArea), areaName, true);
                return Touch(monitor, area, frames);
            }
            catch (Exception e)
            {
                Log.Warn("[nav] Touch 无法解析触摸区域 '" + areaName + "': " + e.Message);
                return false;
            }
        }

        /// <summary>内部：写 GamePanelDown/GamePanelPush（三维数组，按 [monitor, area, …] 匹配维度）。</summary>
        internal static bool InjectPanelTouch(int monitor, int areaIndex, bool pressed)
        {
            bool any = false;
            any |= SetPanelValue(NavReflect.GetStaticValue(typeof(Manager.InputManager), "GamePanelDown"),
                monitor, areaIndex, pressed);
            any |= SetPanelValue(NavReflect.GetStaticValue(typeof(Manager.InputManager), "GamePanelPush"),
                monitor, areaIndex, pressed);
            return any;
        }

        private static bool SetPanelValue(object arrayObj, int monitor, int areaIndex, bool value)
        {
            var array = arrayObj as Array;
            if (array == null || array.Rank != 3) return false;
            int d0 = array.GetLength(0), d1 = array.GetLength(1), d2 = array.GetLength(2);

            // 1.65 实测：`GamePanelDown[2,2,35]` = [monitor, ?(写满), TouchPanelArea(0..34)]
            // area 索引与 TouchPanelArea 枚举顺序一致：A1..A8=0..7, B=8..15, C=16/17, D=18..25, E=26..33, 34=Blank
            if (d2 > areaIndex)
            {
                int mi = (monitor >= 0 && monitor < d0) ? monitor : 0;
                for (int y = 0; y < d1; y++) array.SetValue(value, mi, y, areaIndex);
                return true;
            }

            // 兜底：其它版本的 [monitor, area, …] 形状
            if (d1 > areaIndex)
            {
                int mi = d0 > monitor ? monitor : 0;
                for (int z = 0; z < d2; z++) array.SetValue(value, mi, areaIndex, z);
                return true;
            }
            return false;
        }

        /// <summary>InputManager.UpdateTouchPanel Postfix 调用：维持注入帧数，到期后清掉。</summary>
        internal static void PumpPendingTouches()
        {
            if (PendingTouches.Count == 0) return;
            for (int i = PendingTouches.Count - 1; i >= 0; i--)
            {
                var touch = PendingTouches[i];
                touch.Remaining--;
                if (touch.Remaining <= 0)
                {
                    InjectPanelTouch(touch.Monitor, touch.AreaIndex, false);
                    PendingTouches.RemoveAt(i);
                }
                else
                {
                    InjectPanelTouch(touch.Monitor, touch.AreaIndex, true);
                    PendingTouches[i] = touch;
                }
            }
        }

        /// <summary>
        /// MODE SELECT / 结算页「OK / NEXT」的候选触摸区域（屏幕底部中央；1.65 触摸区 E/D 环）：
        /// 实测 `GamePanelDown[2,2,35]`，area 0..34 = A1..A8/B1..B8/C1..C2/D1..D8/E1..E8/Blank。
        /// 会按顺序尝试，用 `[nav] touch inject area=…` 日志判断哪个生效。
        /// </summary>
        public static string[] OkButtonTouchAreas = { "E5", "E4", "E6", "D5", "D4" };


        /// <summary>依次尝试一组触摸区域（每个都 Touch 一次）。</summary>
        public static bool TouchAreas(int monitor, string[] areas, int frames = 2)
        {
            if (areas == null || areas.Length == 0) return false;
            bool any = false;
            for (int i = 0; i < areas.Length; i++) any |= Touch(monitor, areas[i], frames);
            return any;
        }
        /// <summary>清空所有待注入触摸（任务结束/中止时调用，避免卡住一个虚拟按下）。</summary>
        public static void ClearPendingTouches()
        {
            if (PendingTouches.Count == 0) return;
            for (int i = 0; i < PendingTouches.Count; i++)
                InjectPanelTouch(PendingTouches[i].Monitor, PendingTouches[i].AreaIndex, false);
            PendingTouches.Clear();
        }

        /// <summary>
        /// 按「按钮」注入触摸：从 `InputManager.TouchAreaA..E` 的映射数组里找出该 `ButtonSetting` 对应的触摸区域，
        /// 逐个 `Touch`（游戏内部就是 `GetTouchPanelAreaDown(player, button)` 这套）。找不到就返回 false。
        /// </summary>
        public static bool TouchButton(int monitor, Manager.InputManager.ButtonSetting button, int frames = 2)
        {
            try
            {
                var areas = ResolveButtonAreas(button);
                if (areas.Count == 0)
                {
                    NavReflect.WarnOnce("touch-no-area",
                        "[nav] TouchButton: ButtonSetting." + button + " 没有找到对应触摸区域（看 Install 日志的 TouchAreaA..E dump）");
                    return false;
                }
                bool any = false;
                for (int i = 0; i < areas.Count; i++)
                    any |= Touch(monitor, areas[i], frames);
                Log.Info("[nav] TouchButton ButtonSetting." + button + " → areas=[" +
                         string.Join(",", areas.ConvertAll(a => a.ToString()).ToArray()) + "] ok=" + any);
                return any;
            }
            catch (Exception e)
            {
                Log.Warn("[nav] TouchButton 失败: " + e.Message);
                return false;
            }
        }

        private static List<Manager.InputManager.TouchPanelArea> ResolveButtonAreas(
            Manager.InputManager.ButtonSetting button)
        {
            var result = new List<Manager.InputManager.TouchPanelArea>();
            foreach (var fieldName in new[] { "TouchAreaA", "TouchAreaB", "TouchAreaC", "TouchAreaD", "TouchAreaE" })
            {
                var array = NavReflect.GetStaticValue(typeof(Manager.InputManager), fieldName) as Array;
                if (array == null) continue;
                int index = Convert.ToInt32(button);
                if (index >= 0 && index < array.Length)
                {
                    var area = (Manager.InputManager.TouchPanelArea)array.GetValue(index);
                    if (!result.Contains(area)) result.Add(area);
                }
            }
            return result;
        }

        /// <summary>Install 时 dump 触摸映射与数组维度（只打一次，方便定位该按哪个区域）。</summary>
        internal static void DumpTouchMapOnce()
        {
            try
            {
                var down = NavReflect.GetStaticValue(typeof(Manager.InputManager), "GamePanelDown") as Array;
                var push = NavReflect.GetStaticValue(typeof(Manager.InputManager), "GamePanelPush") as Array;
                Log.Info("[nav] touch map: GamePanelDown dims=" +
                         (down == null ? "null" : down.GetLength(0) + "x" + down.GetLength(1) + "x" + down.GetLength(2)) +
                         ", GamePanelPush dims=" +
                         (push == null ? "null" : push.GetLength(0) + "x" + push.GetLength(1) + "x" + push.GetLength(2)));
                foreach (var fieldName in new[] { "TouchAreaA", "TouchAreaB", "TouchAreaC", "TouchAreaD", "TouchAreaE" })
                {
                    var array = NavReflect.GetStaticValue(typeof(Manager.InputManager), fieldName) as Array;
                    if (array == null) continue;
                    var parts = new List<string>();
                    for (int i = 0; i < array.Length; i++)
                        parts.Add(i + ":" + (int)(Manager.InputManager.TouchPanelArea)array.GetValue(i));
                    Log.Info("[nav] touch map " + fieldName + "[" + array.Length + "] = " + string.Join(",", parts.ToArray()));
                }
            }
            catch (Exception e)
            {
                Log.Warn("[nav] dump 触摸映射失败: " + e.Message);
            }
        }

        // ============================================================================================
        //  续关（CONTINUE 屏按「是」）
        //
        //  梯次（和 PressResultNext 同构，1.5s 一步、幂等）：
        //   1) ContinueMonitor.SelectContinue()   —— 选中「继续」
        //   2) ContinueMonitor.PlayContinue()     —— 播放确认动画（YES）
        //   3) ContinueProcess.DetailInputOnDisp(monitor) —— 让状态机消费输入
        //   4) ContinueProcess.NextContinueProcess() —— 兜底直接推进（私有）
        //   5) 触摸注入（ContinueButtonTouchAreas：右下角；逐个打日志）
        //  已在 Continue(4)/DownloadWait(5) 之后视为成功；`ContinueProcess` 消失后打一条"已进入下一局"。
        // ============================================================================================

        /// <summary>CONTINUE 屏「YES 是」的候选触摸区域（右下角；1.65 area 0..34 = A/B/C/D/E 环）。</summary>
        public static string[] ContinueButtonTouchAreas = { "E5", "E4", "E6", "D5", "D4" };

        private static int _continueStep;
        private static double _continueYesAt = -1000;
        private static double _continueTouchAt = -1000;
        private static ulong _continueLoginBase;
        private static double _continueLoginAt = -1000;
        /// <summary>本次续关开始前的 loginId（用于判断游戏是否真的做了 isContinue 登录）。</summary>
        public static ulong ContinueLoginBaseId { get { return _continueLoginBase; } }
        private static bool _continueStartedLogged;
        private static bool _continueDoneLogged;
        private static bool _continueWaitLogged;

        /// <summary>GameProcess.OnUpdate 防御日志限频（每 5s 最多一条）。</summary>
        private static double _lastGameProcessGuardLogAt = -1000;

        /// <summary>登录奖励页等待起点（它自己结束后会 Fade 到 RegionalSelectProcess 选区页）。</summary>
        private static double _loginBonusSince = -1;
        private static uint _loginBonusId;
        private static uint _loginBonusFadeId;
        private static uint _loginBonusTimeUpId;

        /// <summary>
        /// 续关：在当前 `ContinueProcess` 上按「YES 是」。幂等，flow 每 1.5s 调一次即可。
        /// 返回 true 表示已发出动作或已进入下一局。
        /// </summary>
        public static bool PressContinueYes(int monitor = 0)
        {
            // 原版流程：续关只由真实按键驱动（RebirthFlow 里按住 4 号键 ~2.2s）。
            // 不再直接调 SelectContinue/PlayContinue/DetailInputOnDisp/_isContinueReady/ReLogin ——
            // 那些合成调用会跳过游戏自己的继续登录与初始化，导致官方服第二存 playlogId/session 不对。
            return false;
        }

        /// <summary>写 bool[] 字段的某个下标（如 ContinueProcess._isContinueReady）。</summary>
        private static bool SetArrayBool(object instance, string field, int index, bool value)
        {
            try
            {
                var array = NavReflect.Get(instance, field) as Array;
                if (array == null || index < 0 || index >= array.Length) return false;
                array.SetValue(value, index);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static string ContinueStateName(int state)
        {            switch (state)
            {
                case 0: return "Init";
                case 1: return "Wait";
                case 2: return "Disp";
                case 3: return "DispEnd";
                case 4: return "Continue";
                case 5: return "DownloadWait";
                case 6: return "Error";
                case 7: return "Release";
                default: return "?" + state;
            }
        }

        /// <summary>
        // ============================================================================================
        //  写真页（PhotoEdit）SKIP
        //  一局 3 首结束后：Result → PhotoEdit(SKIP) → MapResult → DataSave → Continue
        //  PhotoEditProcess: _sequence Initialize0/Staging1/ReleaseWait2/Release3/End4,
        //  _subSequences Init0/FirstInfoInit1/FirstInformation2/Staging3/Update4/TimerWait5/Release6,
        //  _isShowSkipButton[]/_isUploadeds[]；私有 OnTimeUp()、OnTimerComplete(int)、CheckButton(int)。
        //  SKIP 本质是"让计时器到 0"（用户截图右下角绿色 SKIP），所以先 OnTimeUp，再触摸右下角兜底。
        // ============================================================================================

        public static string[] PhotoEditSkipTouchAreas = { "E8", "D8", "E7", "D7", "E6", "E5" };

        private static int _photoSkipStep;
        private static double _photoSkipAt = -1000;
        private static uint _photoSkipProcessId;
        private static double _photoSkipFirstAt = -1000;
        private static bool _photoSequenceWritten;

        /// <summary>写真编辑页按「SKIP 跳过」。幂等，flow 每 1.5s 调一次即可。返回 true = 已发出动作。</summary>
        public static bool PressPhotoEditSkip(int monitor = 0)
        {
            try
            {
                var process = NavProcess.Find<Process.PhotoEditProcess>();
                if (process == null) return false;

                double now = Time.realtimeSinceStartup;
                if (now - _photoSkipAt < 1.5) return false;
                _photoSkipAt = now;

                if (process.ProcessId != _photoSkipProcessId)
                {
                    _photoSkipProcessId = process.ProcessId;
                    _photoSkipStep = 0;
                    _photoSkipFirstAt = now;
                    _photoSequenceWritten = false;
                }
                if (_photoSkipFirstAt < 0) _photoSkipFirstAt = now;

                int sequence = GetInstanceInt(process, "_sequence");
                int sub = GetSubSequence(process, monitor);
                bool showSkip = GetArrayBool(process, "_isShowSkipButton", monitor);
                bool uploaded = GetArrayBool(process, "_isUploadeds", monitor);

                // 已经进入 ReleaseWait/Release/End：只等待
                if (sequence >= 2)
                    return false;

                // 实机 02:28：用户截图证明 SKIP 按钮一直可见可点，`_isShowSkipButton=False` 不可信。
                // 只有真正还在入场动画（sequence==Initialize 或 sub<=2）时才短暂等待，其余直接按。
                if (sequence == 0 || sub <= 2)
                {
                    if (_photoSkipStep == 0)
                        Log.Info("[nav] 写真页: 等待入场动画（state=" + PhotoEditSequenceName(sequence) +
                                 ", sub=" + sub + ", showSkip=" + showSkip + "）");
                    return false;
                }

                // 15s 无进展 → 直接写 _sequence = ReleaseWait(2) 兜底（提前结束写真页）
                if (!_photoSequenceWritten && now - _photoSkipFirstAt > 15.0)
                {
                    _photoSequenceWritten = true;
                    string err;
                    bool wrote = SetInstanceField(process, "_sequence", 2, out err);
                    Log.Warn("[nav] 写真页: 按 SKIP 15s 无效 → 直接写 _sequence=ReleaseWait(2) wrote=" + wrote +
                             (err != null ? " err=" + err : "") +
                             ", sub=" + sub + ", showSkip=" + showSkip);
                    return wrote;
                }

                _photoSkipStep++;
                string method;
                bool ok;
                if (_photoSkipStep == 1)
                {
                    method = "PhotoEditProcess.OnTimeUp";
                    ok = TryCallOk(process, "OnTimeUp");
                }
                else if (_photoSkipStep == 2)
                {
                    method = "PhotoEditProcess.OnTimerComplete";
                    ok = TryCallArgs(process, "OnTimerComplete", monitor);
                }
                else if (_photoSkipStep == 3)
                {
                    method = "PhotoEditProcess.CheckButton";
                    ok = TryCallArgs(process, "CheckButton", monitor);
                }
                else
                {
                    var monitors = NavReflect.Get(process, "_monitors") as Array;
                    object monitorObj = monitors != null && monitor < monitors.Length
                        ? monitors.GetValue(monitor) : null;
                    bool timerZero = monitorObj != null && TryCallOk(monitorObj, "isTimerZero");
                    TryCallArgs(monitorObj, "SetMonitorTimer", 0);
                    string area = PhotoEditSkipTouchAreas[(_photoSkipStep - 4) % PhotoEditSkipTouchAreas.Length];
                    method = "touch:" + area;
                    ok = Touch(monitor, area, 2) || timerZero || PressMenuButton(monitor);
                }

                Log.Info("[nav] 写真页: 按 SKIP step=" + _photoSkipStep + " method=" + method +
                         " ok=" + ok + ", state=" + PhotoEditSequenceName(sequence) + "(" + sequence + ")" +
                         ", sub=" + sub + ", showSkip=" + showSkip + ", uploaded=" + uploaded +
                         ", stack=[" + NavProcess.StackNames() + "]");
                return ok;
            }
            catch (Exception e)
            {
                Log.Error("[nav] PressPhotoEditSkip 失败", e);
                return false;
            }
        }
        private static string PhotoEditSequenceName(int sequence)
        {
            switch (sequence)
            {
                case 0: return "Initialize";
                case 1: return "Staging";
                case 2: return "ReleaseWait";
                case 3: return "Release";
                case 4: return "End";
                default: return "?" + sequence;
            }
        }

        // ============================================================================================
        //  收尾展示页（一局结束后"很多页"）：CollectionGet / FriendSeasonRanking
        //  CollectionGetProcess: _state Init0/GotoEnd1/StartAnim2/Wait3/Update4/Finish5/Released6，
        //    _isComplete[]/_isFinished；私有 ChangeFinishState()、GotoSkipNextProcess()、GotoNextProcess()。
        //  FriendSeasonRankingProcess: _state Init0/Wait1/PlaySubMode2/Release3/Released4，
        //    _subState[]、_timer[]、_isSkipSequence/_isSkipPlayer[]；
        //    FriendSeasonRankingMonitor 公开 SetButtonSkip()/Skip()/SetButtonOK()。
        // ============================================================================================

        public static string[] CollectionGetSkipTouchAreas = { "E7", "E8", "D7", "D8", "E6" };
        public static string[] FriendRankingSkipTouchAreas = { "E7", "E8", "D7", "D8", "E6" };

        private static int _collectionSkipStep;
        private static double _collectionSkipAt = -1000;
        private static double _collectionSkipFirstAt = -1000;
        private static bool _collectionForceWritten;
        private static int _friendSkipStep;
        private static double _friendSkipAt = -1000;
        private static double _friendSkipFirstAt = -1000;
        private static bool _friendForceWritten;

        /// <summary>收集品/称号获得页（CollectionGetProcess）按跳过。幂等，1.5s/步。</summary>
        public static bool PressCollectionGetSkip(int monitor = 0)
        {
            try
            {
                var process = NavProcess.Find<CollectionGetProcess>();
                if (process == null) return false;

                double now = Time.realtimeSinceStartup;
                if (now - _collectionSkipAt < 1.5) return false;
                _collectionSkipAt = now;

                int state = GetInstanceInt(process, "_state");
                if (state >= 5)
                {
                    NavReflect.WarnOnce("collection-finishing",
                        "[nav] 收集品页: state=" + CollectionStateName(state) + " → 等待释放");
                    return false;
                }

                if (_collectionSkipFirstAt < 0) _collectionSkipFirstAt = now;
                if (!_collectionForceWritten && now - _collectionSkipFirstAt > 15.0)
                {
                    _collectionForceWritten = true;
                    SetInstanceField(process, "_isFinished", true, out _);
                    bool changed = TryCallOk(process, "ChangeFinishState");
                    Log.Warn("[nav] 收集品页: 按跳过 15s 无效 → 直写 _isFinished=true + ChangeFinishState ok=" +
                             changed + ", state=" + CollectionStateName(state) + "(" + state + ")");
                    return changed;
                }

                _collectionSkipStep++;
                string method;
                bool ok;
                switch (_collectionSkipStep)
                {
                    case 1:
                        method = "CollectionGetProcess.ChangeFinishState";
                        SetArrayBool(process, "_isComplete", monitor, true);
                        ok = TryCallOk(process, "ChangeFinishState");
                        break;
                    case 2:
                        method = "CollectionGetProcess.GotoSkipNextProcess";
                        ok = TryCallOk(process, "GotoSkipNextProcess");
                        break;
                    case 3:
                        method = "CollectionGetProcess.GotoNextProcess";
                        ok = TryCallOk(process, "GotoNextProcess");
                        break;
                    case 4:
                    case 5:
                    case 6:
                    {
                        var monitors = NavReflect.Get(process, "_monitors") as Array;
                        object monitorObj = monitors != null && monitor < monitors.Length
                            ? monitors.GetValue(monitor) : null;
                        TryCallArgs(monitorObj, "SetActiveButton");
                        TryCallArgs(monitorObj, "SetButtonAnimationActive");
                        string area = CollectionGetSkipTouchAreas[(_collectionSkipStep - 4) % CollectionGetSkipTouchAreas.Length];
                        method = "touch:" + area;
                        ok = Touch(monitor, area, 2) || PressMenuButton(monitor);
                        break;
                    }
                    default:
                        method = "reset";
                        ok = false;
                        _collectionSkipStep = 0;
                        break;
                }

                Log.Info("[nav] 收集品页: 按跳过 step=" + _collectionSkipStep + " method=" + method +
                         " ok=" + ok + ", state=" + CollectionStateName(state) + "(" + state + ")" +
                         ", stack=[" + NavProcess.StackNames() + "]");
                return ok;
            }
            catch (Exception e)
            {
                Log.Error("[nav] PressCollectionGetSkip 失败", e);
                return false;
            }
        }

        // ---- 收藏品浏览页（CollectionProcess，全局命名空间；与上面的 CollectionGetProcess 是两个不同页面） ----
        // Sequence Init0/Start1/Update2/GuestWait3/Released4/EndWait5
        // SubSequence SelectCollectionType0/Title1/Icon2/NamePlate3/Frame4/Prtner5/Shop6/…/Exit12/ExitLeft13/Information14/AimeInfo15
        // 退出：SetSubSequence(monitor, Exit=12) / ExitLeft=13；兜底直写 _subSequence / _sequence=Released(4)

        public static string[] CollectionSkipTouchAreas = { "E5", "D5", "E4", "D4", "E6", "E3" };

        private static int _collection2SkipStep;
        private static double _collection2SkipAt = -1000;
        private static double _collection2SkipFirstAt = -1000;
        private static int _collection2SubBefore = -1;
        private static double _collection2FirstAt = -1000;

        /// <summary>收藏品浏览页（CollectionProcess）按退出。幂等，1.5s/步。</summary>
        public static bool PressCollectionSkip(int monitor = 0)
        {
            try
            {
                var process = NavProcess.Find<CollectionProcess>();
                if (process == null) return false;

                double now = Time.realtimeSinceStartup;
                if (now - _collection2SkipAt < 1.5) return false;
                _collection2SkipAt = now;
                if (_collection2SkipFirstAt < 0) _collection2SkipFirstAt = now;

                int sequence = GetInstanceInt(process, "_sequence");
                int sub = GetArrayInt(process, "_subSequence", monitor);
                if (sequence >= 4)
                {
                    NavReflect.WarnOnce("collection2-releasing",
                        "[nav] 收藏品页: sequence=" + CollectionSequenceName(sequence) + " → 等待释放");
                    return false;
                }

                string method;
                bool ok;

                // credit2 实机：`sequence=Start(1)` 时页面还没进入可操作状态，SetSubSequence 会被忽略，
                // 所以先把 sequence 推到 Update(2)，下一次调用再走 Exit 梯次。
                if (sequence == 1)
                {
                    ok = SetInstanceField(process, "_sequence", 2, out _);
                    Log.Info("[nav] 收藏品页: sequence=Start(1) 不可操作 → 写 _sequence=Update(2) ok=" + ok +
                             " sub=" + sub + "(" + CollectionSubName(sub) + ")");
                    _collection2SkipStep = 0;
                    _collection2SubBefore = sub;
                    return ok;
                }

                _collection2SkipStep++;
                if (_collection2SkipStep == 1)
                {
                    _collection2SubBefore = sub;
                    method = "CollectionProcess.SetSubSequence(Exit=12)";
                    ok = TryCallNestedEnum(process, "SetSubSequence", monitor, "SubSequence", 12);
                }
                else if (_collection2SkipStep == 2)
                {
                    method = "CollectionProcess.SetSubSequence(ExitLeft=13)";
                    ok = TryCallNestedEnum(process, "SetSubSequence", monitor, "SubSequence", 13);
                }
                else if (_collection2SkipStep == 3)
                {
                    if (sub == _collection2SubBefore)
                    {
                        // Exit/ExitLeft 都没被吃 → 先把 sequence 拉回 Update，再让下一轮重试
                        method = "write _sequence=Update(2) (Exit 未生效，重置可操作状态)";
                        ok = SetInstanceField(process, "_sequence", 2, out _);
                    }
                    else
                    {
                        method = "write _subSequence[monitor]=Exit(12)";
                        ok = WriteNestedEnumArray(process, "_subSequence", monitor, "SubSequence", 12);
                    }
                }
                else if (_collection2SkipStep == 4 || _collection2SkipStep == 5)
                {
                    method = "write _sequence=Released(4)";
                    ok = SetInstanceField(process, "_sequence", 4, out _);
                }
                else if (now - _collection2SkipFirstAt > 15.0)
                {
                    // 15s 还不动：强制 Released + 触摸（OK/决定区 + BACK 区都试）
                    SetInstanceField(process, "_sequence", 4, out _);
                    string area = CollectionSkipTouchAreas[(_collection2SkipStep - 6) % CollectionSkipTouchAreas.Length];
                    method = "forceReleased + touch:" + area;
                    ok = Touch(monitor, area, 2) || PressMenuButton(monitor);
                }
                else
                {
                    method = "write _sequence=Released(4) (retry)";
                    ok = SetInstanceField(process, "_sequence", 4, out _);
                }

                Log.Info("[nav] 收藏品页: 按退出 step=" + _collection2SkipStep + " method=" + method +
                         " sub=" + sub + "(" + CollectionSubName(sub) + ") sequence=" + CollectionSequenceName(sequence) +
                         "(" + sequence + ") ok=" + ok + ", stack=[" + NavProcess.StackNames() + "]");
                return ok;
            }
            catch (Exception e)
            {
                Log.Error("[nav] PressCollectionSkip 失败", e);
                return false;
            }
        }
        private static string CollectionSequenceName(int sequence)
        {
            switch (sequence)
            {
                case 0: return "Init";
                case 1: return "Start";
                case 2: return "Update";
                case 3: return "GuestWait";
                case 4: return "Released";
                case 5: return "EndWait";
                default: return "?" + sequence;
            }
        }

        private static string CollectionSubName(int sub)
        {
            switch (sub)
            {
                case 0: return "SelectCollectionType";
                case 1: return "Title";
                case 2: return "Icon";
                case 3: return "NamePlate";
                case 4: return "Frame";
                case 5: return "Prtner";
                case 6: return "Shop";
                case 7: return "ShopPartnerGetWindow";
                case 8: return "ShopPartnerSetDialog";
                case 9: return "PartnerPresent";
                case 10: return "PartnerPresentConfirm";
                case 11: return "PartnerPresentReward";
                case 12: return "Exit";
                case 13: return "ExitLeft";
                case 14: return "Information";
                case 15: return "AimeInfo";
                case 16: return "Max";
                default: return "?" + sub;
            }
        }

        /// <summary>调用带嵌套枚举参数的方法，如 `SetSubSequence(int, CollectionProcess.SubSequence)`。</summary>
        private static bool TryCallNestedEnum(object instance, string method, int monitor, string nestedEnumName, int value)
        {
            try
            {
                var type = instance.GetType();
                var enumType = type.GetNestedType(nestedEnumName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (enumType == null) return false;
                var enumValue = Enum.ToObject(enumType, value);
                var info = type.GetMethod(method,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, new System.Type[] { typeof(int), enumType }, null);
                if (info == null) return false;
                info.Invoke(instance, new object[] { monitor, enumValue });
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>把嵌套枚举值写进枚举数组字段，如 `_subSequence[monitor] = Exit`。</summary>
        private static bool WriteNestedEnumArray(object instance, string field, int index, string nestedEnumName, int value)
        {
            try
            {
                var array = NavReflect.Get(instance, field) as Array;
                if (array == null || index < 0 || index >= array.Length) return false;
                var elementType = array.GetType().GetElementType();
                if (elementType == null || !elementType.IsEnum) return false;
                array.SetValue(Enum.ToObject(elementType, value), index);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>好友赛季排名页（FriendSeasonRankingProcess）按跳过。幂等，1.5s/步。</summary>
        public static bool PressFriendRankingSkip(int monitor = 0)
        {
            try
            {
                var process = NavProcess.Find<Process.FriendSeasonRanking.FriendSeasonRankingProcess>();
                if (process == null) return false;

                double now = Time.realtimeSinceStartup;
                if (now - _friendSkipAt < 1.5) return false;
                _friendSkipAt = now;

                int state = GetInstanceInt(process, "_state");
                int subMode = GetArrayInt(process, "_subState", monitor);
                if (state >= 3)
                {
                    NavReflect.WarnOnce("friend-ranking-releasing",
                        "[nav] 好友赛季页: state=" + FriendStateName(state) + " → 等待释放");
                    return false;
                }

                if (_friendSkipFirstAt < 0) _friendSkipFirstAt = now;
                if (!_friendForceWritten && now - _friendSkipFirstAt > 15.0)
                {
                    _friendForceWritten = true;
                    bool wrote = SetInstanceField(process, "_state", 3, out _);
                    Log.Warn("[nav] 好友赛季页: 按跳过 15s 无效 → 直写 _state=Release(3) wrote=" + wrote +
                             ", state=" + FriendStateName(state) + "(" + state + "), subMode=" + subMode);
                    return wrote;
                }

                _friendSkipStep++;
                string method;
                bool ok;
                switch (_friendSkipStep)
                {
                    case 1:
                    {
                        method = "skipSequence+m_isSkipPlayer+SetButtonSkip";
                        SetInstanceField(process, "_isSkipSequence", true, out _);
                        SetArrayBool(process, "_isSkipPlayer", monitor, true);
                        var monitors = NavReflect.Get(process, "_monitors") as Array;
                        object monitorObj = monitors != null && monitor < monitors.Length
                            ? monitors.GetValue(monitor) : null;
                        ok = TryCallOk(monitorObj, "SetButtonSkip");
                        break;
                    }
                    case 2:
                    {
                        method = "FriendSeasonRankingMonitor.Skip";
                        var monitors = NavReflect.Get(process, "_monitors") as Array;
                        object monitorObj = monitors != null && monitor < monitors.Length
                            ? monitors.GetValue(monitor) : null;
                        ok = TryCallOk(monitorObj, "Skip");
                        break;
                    }
                    case 3:
                        method = "FriendSeasonRankingProcess.SubUpdate";
                        ok = TryCallOk(process, "SubUpdate");
                        break;
                    case 4:
                    case 5:
                    case 6:
                    {
                        var monitors = NavReflect.Get(process, "_monitors") as Array;
                        object monitorObj = monitors != null && monitor < monitors.Length
                            ? monitors.GetValue(monitor) : null;
                        TryCallOk(monitorObj, "SetButtonOK");
                        string area = FriendRankingSkipTouchAreas[(_friendSkipStep - 4) % FriendRankingSkipTouchAreas.Length];
                        method = "touch:" + area;
                        ok = Touch(monitor, area, 2) || PressMenuButton(monitor);
                        break;
                    }
                    default:
                        method = "reset";
                        ok = false;
                        _friendSkipStep = 0;
                        break;
                }

                Log.Info("[nav] 好友赛季页: 按跳过 step=" + _friendSkipStep + " method=" + method +
                         " ok=" + ok + ", state=" + FriendStateName(state) + "(" + state + ")" +
                         ", subMode=" + subMode + ", stack=[" + NavProcess.StackNames() + "]");
                return ok;
            }
            catch (Exception e)
            {
                Log.Error("[nav] PressFriendRankingSkip 失败", e);
                return false;
            }
        }

        // ---- 其它收尾展示页：GetPresent（礼物/解禁提示）与 UnlockMusic（解禁乐曲） ----
        // 两者都是 `_state: Wait0/GotoEnd1/Disp2/Released3`、没有公开 Skip 方法；
        // 做法：先试常见方法名 → 写 `_state=GotoEnd(1)` → 写 `_state=Released(3)` → 触摸兜底。
        public static string[] SimpleTailTouchAreas = { "E5", "D5", "E4", "D4", "E6" };

        private static int _getPresentStep;
        private static double _getPresentAt = -1000;
        private static double _getPresentFirstAt = -1000;
        private static int _unlockMusicStep;
        private static double _unlockMusicAt = -1000;
        private static double _unlockMusicFirstAt = -1000;

        /// <summary>礼物/解禁提示页（GetPresentProcess）按跳过。幂等，1.5s/步。</summary>
        public static bool PressGetPresentSkip(int monitor = 0)
        {
            var process = NavProcess.Find<Process.GetPresentProcess>();
            if (process == null) return false;
            return PressSimpleTailPage(process, "礼物页", monitor,
                ref _getPresentStep, ref _getPresentAt, ref _getPresentFirstAt, SimpleTailTouchAreas);
        }

        /// <summary>解禁乐曲页（UnlockMusicProcess）按跳过。幂等，1.5s/步。</summary>
        public static bool PressUnlockMusicSkip(int monitor = 0)
        {
            var process = NavProcess.Find<Process.UnlockMusicProcess>();
            if (process == null) return false;
            return PressSimpleTailPage(process, "解禁乐曲页", monitor,
                ref _unlockMusicStep, ref _unlockMusicAt, ref _unlockMusicFirstAt, SimpleTailTouchAreas);
        }

        /// <summary>
        /// 通用菜单按键注入：**只按 Button04（決定 / 是）**。
        ///
        /// 红线：**绝不注入 Button05**。续关屏的「否」就是 5 号键，历史实现里这里 04/05 交替，
        /// 而 `PressContinueYes` 恰好也会调到这里 —— 等于在「是/否」屏随机点「否」，轻则本 credit
        /// 提前结束、剩余 track 静默丢失，重则整屏状态错乱。其它收尾页的按钮一律先靠触摸兜底。
        ///
        /// **菜单页（MODE SELECT/选歌/续关/設定）禁止注入**：MODE SELECT 的決定由 InputInject 专门
        /// 处理，这里插手只会打乱它的状态机。
        /// </summary>
        public static bool PressMenuButton(int monitor = 0)
        {
            if (NavProcess.Any<Process.ModeSelect.ModeSelectProcess>() ||
                NavProcess.Any<MusicSelectProcess>() ||
                NavProcess.Any<Process.ContinueProcess>() ||
                NavProcess.Any<Process.SimpleSettingProcess>())
            {
                NavReflect.WarnOnce("menu-button-safe",
                    "[nav] PressMenuButton 拒绝：菜单页/选歌页/续关屏/設定页禁止合成按键（5 号键=否，红线）; stack=[" +
                    NavProcess.StackNames() + "]");
                return false;
            }
            bool ok = PressSyntheticButton(monitor, "Button04");
            double now = Time.realtimeSinceStartup;
            if (now - _lastMenuButtonLogAt > 2.0)
            {
                _lastMenuButtonLogAt = now;
                Log.Info("[nav] 通用按键注入: Button04 monitor=" + monitor + " ok=" + ok +
                         "; stack=[" + NavProcess.StackNames() + "]");
            }
            return ok;
        }

        private static double _lastMenuButtonLogAt = -1000;

        /// <summary>
        /// 误触「設定」页（`Process.SimpleSettingProcess`）时退出：写 `_sequence=Release(5)` →
        /// `GotoNextProcess()` → `_subSequence=None(7)` → `Button05`（戻る）。
        /// 实机 03:51：MODE SELECT 左上角「設定」被误触弹出，导致 MODE SELECT 状态乱掉。
        /// </summary>
        public static bool ExitSettings(int monitor = 0)
        {
            try
            {
                var process = NavProcess.Find<Process.SimpleSettingProcess>();
                if (process == null)
                {
                    _settingsExitStep = 0;
                    _settingsExitLogged = false;
                    return false;
                }

                double now = Time.realtimeSinceStartup;
                if (now - _settingsExitAt < 1.0) return false;
                _settingsExitAt = now;

                if (!_settingsExitLogged)
                {
                    _settingsExitLogged = true;
                    Log.Warn("[nav] 误触設定页 → 退出; stack=[" + NavProcess.StackNames() + "]");
                }

                _settingsExitStep++;
                int sequence = GetInstanceInt(process, "_sequence");
                int sub = GetArrayInt(process, "_subSequence", monitor);
                string method;
                bool ok;
                if (_settingsExitStep == 1)
                {
                    method = "write _sequence=Release(5)";
                    ok = SetInstanceField(process, "_sequence", 5, out _);
                }
                else if (_settingsExitStep == 2)
                {
                    method = "SimpleSettingProcess.GotoNextProcess";
                    ok = TryCallOk(process, "GotoNextProcess");
                }
                else if (_settingsExitStep == 3)
                {
                    method = "write _subSequence[monitor]=None(7)";
                    ok = WriteNestedEnumArray(process, "_subSequence", monitor, "SubSequence", 7);
                }
                else
                {
                    method = "Button05(BACK)";
                    ok = PressSyntheticButton(monitor, "Button05");
                }

                Log.Info("[nav] 設定页退出 step=" + _settingsExitStep + " method=" + method + " ok=" + ok +
                         ", sequence=" + sequence + ", sub=" + sub + ", stack=[" + NavProcess.StackNames() + "]");
                return ok;
            }
            catch (Exception e)
            {
                Log.Error("[nav] ExitSettings 失败", e);
                return false;
            }
        }

        private static int _settingsExitStep;
        private static double _settingsExitAt = -1000;
        private static bool _settingsExitLogged;

        /// <summary>
        /// 通用"简单展示页"跳过：常见方法名 → 写 `_state=GotoEnd(1)` → 写 `_state=Released(3)` → 触摸；
        /// 15s 后循环写 Released（幂等）。
        /// </summary>
        private static bool PressSimpleTailPage(object process, string label, int monitor,
            ref int step, ref double lastAt, ref double firstAt, string[] touchAreas)
        {
            try
            {
                double now = Time.realtimeSinceStartup;
                if (now - lastAt < 1.5) return false;
                lastAt = now;
                if (firstAt < 0) firstAt = now;

                int state = GetInstanceInt(process, "_state");
                if (state >= 3)
                    return false; // 已 Released，等它自己消失

                step++;
                string method;
                bool ok;
                if (step == 1)
                {
                    string used;
                    ok = NavReflect.TryCallAny(process, new[] { "GotoEnd", "OnTimeUp", "Skip", "ToNext", "GotoNextProcess" },
                        null, out used);
                    method = "try:" + used;
                }
                else if (step == 2)
                {
                    method = "write _state=GotoEnd(1)";
                    ok = SetInstanceField(process, "_state", 1, out _);
                }
                else if (step == 3)
                {
                    method = "write _state=Released(3)";
                    ok = SetInstanceField(process, "_state", 3, out _);
                }
                else if (now - firstAt > 15.0)
                {
                    string area = touchAreas[(step - 4) % touchAreas.Length];
                    method = "touch:" + area;
                    ok = Touch(monitor, area, 2) || PressMenuButton(monitor);
                }
                else
                {
                    method = "write _state=Released(3) (retry)";
                    ok = SetInstanceField(process, "_state", 3, out _);
                }

                Log.Info("[nav] " + label + ": 按跳过 step=" + step + " method=" + method +
                         " ok=" + ok + ", state=" + state + ", stack=[" + NavProcess.StackNames() + "]");
                return ok;
            }
            catch (Exception e)
            {
                Log.Error("[nav] " + label + " 跳过失败", e);
                return false;
            }
        }

        /// <summary>当前栈里是否有任一收尾展示页（写真/收集品获得/收藏品浏览/礼物/解禁乐曲/好友赛季）。</summary>
        public static bool IsTailScreen()
        {
            return NavProcess.Any<Process.PhotoEditProcess>() ||
                   NavProcess.Any<CollectionGetProcess>() ||
                   NavProcess.Any<CollectionProcess>() ||
                   NavProcess.Any<Process.GetPresentProcess>() ||
                   NavProcess.Any<Process.UnlockMusicProcess>() ||
                   NavProcess.Any<Process.FriendSeasonRanking.FriendSeasonRankingProcess>();
        }

        /// <summary>
        /// 收尾展示页统一入口（flow 在 stage==Other 且栈里有这些页时每帧调用，直到它们消失）：
        /// PhotoEdit → PressPhotoEditSkip；CollectionGet → PressCollectionGetSkip；
        /// FriendSeasonRanking → PressFriendRankingSkip。
        /// 返回 true = 已发出动作或正在等待这些页自己走（**flow 不应据此判失败**）。
        /// </summary>
        public static bool PressAnyTailScreen(int monitor = 0)
        {
            if (!IsTaskActive()) return false;

            // 誤触「設定」页最优先退出（否则 MENU 状态机会乱）
            if (NavProcess.Any<Process.SimpleSettingProcess>())
            {
                ExitSettings(monitor);
                return true;
            }

            // MODE SELECT / 选歌页上，收尾页可能在后面释放中 —— 绝不能在菜单页上触摸/按键（防误触設定）
            if (NavProcess.Any<Process.ModeSelect.ModeSelectProcess>() || NavProcess.Any<MusicSelectProcess>())
            {
                NavReflect.WarnOnce("tail-menu-safe",
                    "[nav] 收尾页与菜单页共存 → 暂不注入（防止误触）; stack=[" + NavProcess.StackNames() + "]");
                return true;
            }

            bool handled = false;
            if (NavProcess.Any<Process.PhotoEditProcess>())
            {
                PressPhotoEditSkip(monitor);
                handled = true;
            }
            if (NavProcess.Any<CollectionGetProcess>())
            {
                PressCollectionGetSkip(monitor);
                handled = true;
            }
            if (NavProcess.Any<CollectionProcess>())
            {
                PressCollectionSkip(monitor);
                handled = true;
            }
            if (NavProcess.Any<Process.GetPresentProcess>())
            {
                PressGetPresentSkip(monitor);
                handled = true;
            }
            if (NavProcess.Any<Process.UnlockMusicProcess>())
            {
                PressUnlockMusicSkip(monitor);
                handled = true;
            }
            if (NavProcess.Any<Process.FriendSeasonRanking.FriendSeasonRankingProcess>())
            {
                PressFriendRankingSkip(monitor);
                handled = true;
            }
            return handled;
        }

        private static string CollectionStateName(int state)
        {
            switch (state)
            {
                case 0: return "Init";
                case 1: return "GotoEnd";
                case 2: return "StartAnim";
                case 3: return "Wait";
                case 4: return "Update";
                case 5: return "Finish";
                case 6: return "Released";
                default: return "?" + state;
            }
        }

        private static string FriendStateName(int state)
        {
            switch (state)
            {
                case 0: return "Init";
                case 1: return "Wait";
                case 2: return "PlaySubMode";
                case 3: return "Release";
                case 4: return "Released";
                default: return "?" + state;
            }
        }

        private static int GetArrayInt(object process, string field, int monitor)
        {
            try
            {
                var array = NavReflect.Get(process, field) as Array;
                if (array != null && monitor >= 0 && monitor < array.Length)
                    return NavReflect.ToInt(array.GetValue(monitor));
            }
            catch (Exception) { }
            return -1;
        }

        private static int GetSubSequence(object process, int monitor)
        {
            try
            {
                var array = NavReflect.Get(process, "_subSequences") as Array;
                if (array != null && monitor >= 0 && monitor < array.Length)
                    return NavReflect.ToInt(array.GetValue(monitor));
            }
            catch (Exception) { }
            return -1;
        }

        private static bool GetArrayBool(object process, string field, int monitor)
        {
            try
            {
                var array = NavReflect.Get(process, field) as Array;
                if (array != null && monitor >= 0 && monitor < array.Length) return NavReflect.ToBool(array.GetValue(monitor));
            }
            catch (Exception) { }
            return false;
        }

        // ============================================================================================
        //  区域选择（RegionalSelectProcess）API + 自动爬区
        // ============================================================================================

        /// <summary>区域信息（给 UI / flow 用）。</summary>
        public sealed class AreaInfo
        {
            public int Index;
            public int Id;
            public string Name;
            public bool IsClear;
            public bool IsComplete;
            public bool IsInfinity;
            public bool IsEvent;
            public bool IsLock;
            public uint Distance;
            public bool IsCurrent;
        }

        /// <summary>当前是不是区域选择页。</summary>
        public static bool IsAreaSelectScreen()
        {
            return NavProcess.Any<Process.RegionalSelectProcess>();
        }

        /// <summary>列出当前区域选择页上的所有区域（id/名称/清关/完成/无限/活动/锁定/距离/是否光标）。</summary>
        private static int _areaListLoggedCount = -1;
        private static int _areaListLoggedCursor = -1;

        public static List<AreaInfo> ListAreas(int monitor = 0)
        {
            var result = new List<AreaInfo>();
            try
            {
                var process = NavProcess.Find<Process.RegionalSelectProcess>();
                if (process == null) return result;
                var list = process.GetUserMapDatas(monitor);
                if (list == null) return result;
                int cursor = process.SelectCursolIndex(monitor);
                for (int i = 0; i < list.Count; i++)
                {
                    var data = list[i];
                    if (data == null) continue;
                    var info = new AreaInfo();
                    info.Index = i;
                    info.Id = data.ID;
                    info.Name = data.Name;
                    info.IsClear = data.IsClear;
                    info.IsComplete = data.IsComplete;
                    info.IsInfinity = data.IsInfinity;
                    info.IsEvent = data.IsEvent;
                    info.IsLock = data.IsLock;
                    info.Distance = data.Distance;
                    info.IsCurrent = (i == cursor);
                    result.Add(info);
                }
                if (result.Count != _areaListLoggedCount || cursor != _areaListLoggedCursor)
                {
                    _areaListLoggedCount = result.Count;
                    _areaListLoggedCursor = cursor;
                    Log.Info("[nav] 区域选择页: 共 " + result.Count + " 个区, 当前光标=" + cursor);
                }
            }
            catch (Exception e)
            {
                Log.Error("[nav] ListAreas 失败", e);
            }
            return result;
        }

        /// <summary>选区页剩余倒计时秒数（无选区页时返回 -1）。</summary>
        public static double GetAreaSelectTimeout(int monitor = 0)
        {
            try
            {
                var process = NavProcess.Find<Process.RegionalSelectProcess>();
                if (process == null) return -1.0;
                var timers = NavReflect.Get(process, "_timer") as Array;
                if (timers == null || monitor < 0 || monitor >= timers.Length) return -1.0;
                return Convert.ToDouble(timers.GetValue(monitor));
            }
            catch (Exception) { return -1.0; }
        }

        /// <summary>选中指定 areaId（把光标移过去并确认）。返回 true = 已发出。</summary>
        public static bool SelectArea(int areaId, int monitor = 0)
        {
            try
            {
                var process = NavProcess.Find<Process.RegionalSelectProcess>();
                if (process == null) return false;
                var list = process.GetUserMapDatas(monitor);
                if (list == null) return false;

                int index = -1;
                string name = "";
                for (int i = 0; i < list.Count; i++)
                {
                    var data = list[i];
                    if (data == null || data.ID != areaId) continue;
                    index = i;
                    name = data.Name;
                    break;
                }
                if (index < 0)
                {
                    NavReflect.WarnOnce("area-notfound:" + areaId,
                        "[nav] 选区: areaId=" + areaId + " 不在当前列表里（可能未解锁/已过）");
                    return false;
                }

                SetArrayInt(process, "_selectCursolIndex", monitor, index);
                int cursorNow = process.SelectCursolIndex(monitor);
                bool decided = TryCallArgs(process, "RegionSelectDecide", monitor);
                Log.Info("[nav] 选区: areaId=" + areaId + " areaName=" + name +
                         " index=" + index + " cursor=" + cursorNow +
                         " 剩余倒计时=" + GetAreaSelectTimeout(monitor).ToString("0.0") + "s" +
                         " decide=" + decided);
                return decided || cursorNow == index;
            }
            catch (Exception e)
            {
                Log.Error("[nav] SelectArea 失败", e);
                return false;
            }
        }

        /// <summary>
        /// 自动爬区：选一个"未完成 / 未锁定 / 非无限"的区（优先非活动区），没有则返回 false（需要从头再来）。
        /// </summary>
        public static bool SelectNextIncompleteArea(int monitor = 0)
        {
            try
            {
                var areas = ListAreas(monitor);
                if (areas.Count == 0) return false;

                AreaInfo fallback = null;
                for (int i = 0; i < areas.Count; i++)
                {
                    var area = areas[i];
                    if (area.IsLock) continue;
                    if (area.IsComplete) continue;
                    if (area.IsInfinity) continue;
                    if (!area.IsEvent)
                    {
                        Log.Info("[nav] 自动爬区: 选择 areaId=" + area.Id + " areaName=" + area.Name +
                                 " (clear=" + area.IsClear + ", complete=" + area.IsComplete +
                                 ", 剩余倒计时=" + GetAreaSelectTimeout(monitor).ToString("0.0") + "s)");
                        return SelectArea(area.Id, monitor);
                    }
                    if (fallback == null) fallback = area;
                }
                if (fallback != null)
                {
                    Log.Info("[nav] 自动爬区: 只剩活动区可选 → areaId=" + fallback.Id + " areaName=" + fallback.Name);
                    return SelectArea(fallback.Id, monitor);
                }

                Log.Warn("[nav] 自动爬区: 当前列表没有未完成的区（全部完成/未解锁）→ 需要从头再来; areaCount=" +
                         areas.Count);
                return false;
            }
            catch (Exception e)
            {
                Log.Error("[nav] SelectNextIncompleteArea 失败", e);
                return false;
            }
        }

        // ---- 直调选区（Lead 2026-11-15 提供的 1.65 元数据路径）----
        // RegionSelectState { Initialize=0, Update=1, ToRelease=2, Release=3 }
        // RegionSelectIndividualState { Initialize=0, FirstInformation=1, FirstInfoWait=2, Discover=3,
        //   ToUpdate=4, ToUpdateWait=5, Update=6, Setting=7, Mission=8, FocusRegionList=9,
        //   ToDecide=10, RewardWait=11, InfinityInfoWait=12, Decide=13, ToWait=14, Wait=15, Release=16 }
        // 方法：SelectCursolIndex(int)、SetShowDecideButton(int)、RegionSelectDecide(int)、Decide(int)、TimeUp()

        /// <summary>flow 指定下一次要选的区光标（-1 = 自动选未完成区）。</summary>
        public static int PendingAreaCursor = -1;

        /// <summary>flow 指定目标区光标（等价于设置 PendingAreaCursor）。</summary>
        public static void RequestSelectArea(int cursorIndex)
        {
            if (PendingAreaCursor != cursorIndex) Log.Info("[nav] 选区请求: cursor=" + cursorIndex);
            PendingAreaCursor = cursorIndex;
        }

        /// <summary>按 areaId 直接选（不移动光标）：3 秒无人操作后由 flow 指定。</summary>
        public static int PendingAreaId = -1;
        public static void RequestSelectAreaId(int areaId) { PendingAreaId = areaId; }

        private static double _areaDirectAt = -1000;
        private static double _areaDirectFirstAt = -1000;
        private static bool _areaDirectTimedUp;
        private static bool _areaDirectWaitLogged;

        /// <summary>
        /// **直调选区**：SelectCursolIndex(cursor) → SetShowDecideButton(monitor) → RegionSelectDecide(monitor)（失败再 Decide）。
        /// 只在 `_state >= Update(1)` 且 subState 可操作时动作；FirstInformation/Discover 阶段先 TimeUp；
        /// 15s 无进展 → TimeUp() 兜底（会选默认区）。返回 true = 已发出动作。
        /// </summary>
        // ---------------------------------------------------------------- 区域选择（真实按键）

        private static double _regionDriveAt = -1000;
        private static int _regionDriveSubLogged = -1;
        private static int _regionDecidedFor = -1;

        /// <summary>
        /// 区域选择页的**真实按键**驱动（不再直调 SelectCursolIndex/RegionSelectDecide 跳过界面）。
        ///
        /// 运行时 IL（`ildump RegionalSelectProcess Input`）给出的真实输入链路：
        ///   - subState 2 FirstInfoWait / 3 Discover：`GetButtonDown(Button04)` 推进
        ///   - subState 6 Update：`GetInputDown(Button03)` = 光标右移，`GetInputDown(Button06)` = 左移，
        ///     `GetButtonDown(Button04)` = 決定（随后游戏自己走 ToDecide(10) → Decide(13) → Release(16)）
        ///   - subState 7 Setting / 8 Mission：4 号键关闭窗口
        /// 返回：0=等待/无动作，1=已注入一次推进，2=刚刚按下決定。
        /// </summary>
        public static int DriveRegionalSelect(int monitor = 0)
        {
            if (HoldForUser("area:" + monitor, ModConfig.Data.UserHoldSec)) return 0;
            try
            {
                var process = NavProcess.Find<Process.RegionalSelectProcess>();
                if (process == null)
                {
                    _regionDriveAt = -1000;
                    _regionDriveSubLogged = -1;
                    _regionDecidedFor = -1;
                    PendingAreaCursor = -1;   // 选区页已消失：清掉目标光标，下一轮重新指定
                    return 0;
                }
                double now = Time.realtimeSinceStartup;
                int state = GetInstanceInt(process, "_state");
                if (state <= 0) return 0;
                int sub = GetArrayInt(process, "_subStates", monitor);
                if (sub != _regionDriveSubLogged)
                {
                    _regionDriveSubLogged = sub;
                    int cursorNow = GetArrayInt(process, "_selectCursolIndex", monitor);
                    Log.Info("[nav] 选区: state=" + RegionStateName(state) + " sub=" + RegionSubStateName(sub) +
                             " cursor=" + cursorNow + "（真实按键驱动）");
                }
                if (now - _regionDriveAt < 0.25) return 0;

                // 说明/发现动画：4 号键推进
                if (sub == 2 || sub == 3)
                {
                    _regionDriveAt = now;
                    InputInject.Press(monitor, Manager.InputManager.ButtonSetting.Button04);
                    return 1;
                }

                // 主选择页：先移动光标到目标区，再按決定
                // 主选择页：**直接改光标数组**跳到目标区（正式机台只有 30s，逐步按 3/6 根本走不完），
                // 再按一次真实 4 号键走游戏自己的 CanVisitRegion→RegionSelectDecide 校验。
                // 用户要求：3s 不动就直接按 areaId 选，不再用按键移动光标。                 if (PendingAreaId > 0)                 {                     try                     {                         var list = ListAreas(monitor);                         int idx = -1;                         for (int i = 0; i < list.Count; i++)                         {                             var a = list[i];                             if (a == null || a.Id != PendingAreaId) continue;                             idx = a.Index;                             break;                         }                         if (idx >= 0)                         {                             SetArrayInt(process, "_selectCursolIndex", monitor, idx);                             string err = null;                             NavReflect.TryCall(process, "RegionSelectDecide", new object[] { monitor }, out err);                             Log.Info("[nav] 选区: areaId=" + PendingAreaId + " idx=" + idx + " → RegionSelectDecide err=" + (err ?? "none"));                             if (err != null) NavReflect.TryCall(process, "Decide", new object[] { monitor }, out err);                             PendingAreaId = -1;                             _regionDriveAt = now;                             return 2;                         }                         PendingAreaId = -1;                     }                     catch (Exception e) { Log.Warn("[nav] areaId 直选失败: " + e.Message); PendingAreaId = -1; }                 } 
                if (sub == 6)
                {
                    int target = ResolveAreaCursor();
                    int cursor = GetArrayInt(process, "_selectCursolIndex", monitor);
                    if (target >= 0 && cursor >= 0 && cursor != target)
                    {
                        SetArrayInt(process, "_selectCursolIndex", monitor, target);
                        try
                        {
                            var mons = NavReflect.Get(process, "_monitors") as Array;
                            var mon = mons != null && monitor >= 0 && monitor < mons.Length ? mons.GetValue(monitor) : null;
                            string lerr;
                            if (mon != null) NavReflect.TryCall(mon, "ListScroll", new object[] { target }, out lerr);
                        }
                        catch (Exception) { }
                        _regionDriveAt = now;
                        Log.Info("[nav] 选区: 直接跳光标 " + cursor + " → " + target + "（不再逐步移动）");
                        return 1;
                    }
                    _regionDriveAt = now;
                    _regionDecidedFor = cursor;
                    InputInject.Press(monitor, Manager.InputManager.ButtonSetting.Button04);
                    int areaId = -1;
                    try
                    {
                        var areas = MapHooks.AvailableAreas;
                        if (areas != null)
                            for (int i = 0; i < areas.Count; i++)
                                if (areas[i] != null && areas[i].Index == cursor) { areaId = areas[i].Id; break; }
                    }
                    catch (Exception) { }
                    if (areaId > 0) MapHooks.NotifyAreaSelected(areaId);
                    Log.Info("[nav] 选区: 光标=" + cursor + " 按 4 号键決定 (areaId=" + areaId + ")");
                    return 2;
                }

                // 首次进入无限区/任务区的说明窗：4 号键确认（不要 TimeUp，否则可能跳过确认）
                if (sub == 12)
                {
                    _regionDriveAt = now;
                    InputInject.Press(monitor, Manager.InputManager.ButtonSetting.Button04);
                    return 1;
                }

                // 設定/任务窗口：4 号键关闭，让游戏回到主选择页
                if (sub == 7 || sub == 8)
                {
                    _regionDriveAt = now;
                    InputInject.Press(monitor, Manager.InputManager.ButtonSetting.Button04);
                    return 1;
                }

                // 10 ToDecide / 13 Decide / 11 RewardWait / 16 Release：交给游戏自己播完
                return 0;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("region-drive", "选区真实按键驱动失败: " + e.Message);
                return 0;
            }
        }

        /// <summary>选区页正在被真实按键驱动 —— MapHooks 的 AreaSelectTimeoutSec 超时 TimeUp 此时不得抢跑。</summary>
        public static bool AreaSelectDriveActive
        {
            get { return Time.realtimeSinceStartup - _regionDriveAt < 8.0; }
        }

        /// <summary>
        /// 兼容旧调用点：返回 true 仅表示"这一次真的按下了決定"（光标移动/提示页推进返回 false）。
        /// 内部改成真实按键驱动，不再直调游戏内部方法。
        /// </summary>
        public static bool SelectAreaDirect(int cursorIndex, int monitor = 0)
        {
            PendingAreaCursor = cursorIndex;
            return DriveRegionalSelect(monitor) == 2;
        }

        // ---------------------------------------------------------------- 选票 / 角色选择（真实按键）

        private static double _ticketAt = -1000;
        private static int _ticketStateLogged = -1;

        /// <summary>
        /// 选票页（TicketSelectProcess）：真实 4 号键「決定」。
        /// 运行时 IL：`UpdateInput` 同样以 `GetButtonDown(monitorId, Button04)` 进入
        /// `TicketSelectMonitor.OKButtonAnim(Button04)`，没有任何"选票专用键"。
        /// 国服没有这个流程时根本不会进这里。
        /// </summary>
        /// <summary>
        /// 选票页（TicketSelectProcess）：**绝不按決定选票**（测试服会写异常数据）。
        /// 走游戏自己的 TimeUp 路径（= 用到时也不选票，直接进入下一屏）：
        /// 运行时 IL：TimeUp() 把 monitor 的 `_isTimeUp/_isDecidedOK/_isDecidedEntry` 置真、进程 `_isTimeUp=true`，
        /// 与"倒计时结束"完全同一条路径 —— 正常操作但不消耗任何票。
        /// 国服没有这个流程时根本不会进这里。
        /// </summary>
        /// <summary>
        /// 选票页（TicketSelectProcess）—— 默认**不选票**（测试服会写异常数据），每次走游戏自己的
        /// `TimeUp()`（= 倒计时结束、不消耗票进入下一屏）。
        ///
        /// 用户可在 UI 里：
        ///   - 「下次用票」：下一次选票页不自动跳过，由操作员手动选（选完自动记住票 ID）；
        ///   - 「保持」：每次都用上一次人工选择的那张票（自动把光标设到该票并按下決定）。
        /// </summary>
        /// <summary>
        /// 选票页（TicketSelectProcess）—— **直接篡改 monitor 状态**，不走 30 秒倒计时：
        ///   `TicketSelectMonitor.IsEnd()` 就是 `_state == 44`，而 `TicketSelectProcess.OnUpdate`
        ///   在两个 monitor 都 IsEnd 后才会 Released → Fade 到 GetPresentProcess → 选歌。
        ///   所以这里直接把两个 monitor 置成 [timeup+decided+state=44]（票 ID = 0 表示不用票），
        ///   下一帧进程自己就会走正常释放流程，全程 <0.5s，不需要按任何键。
        ///
        ///   UI 开「选票保持」且记录过票 ID 时，`_decidedSelectTicketID` 用那张票（用户明确要求）。
        /// </summary>
        private static bool HandleTicketSelect(int monitor)
        {
            if (HoldForUser("ticket:" + monitor, ModConfig.Data.UserHoldSec)) return false;
            try
            {
                var process = FindLiveByName("TicketSelectProcess");
                if (process == null)
                {
                    _ticketAt = -1000;
                    _ticketStateLogged = -1;
                    _ticketTimedUp = false;
                    _ticketForcedId = 0;
                    return false;
                }
                int state = GetInstanceInt(process, "_state");
                if (state == 2) return false;   // Released：等 Fade 结束
                // 「下次用票」：这一次不强制 End，等操作员手动选（选完记住票 ID）。
                if (ModConfig.Data.TicketUseNext)
                {
                    int manualId = GetInstanceInt(GetMonitorAt(process, monitor), "_decidedSelectTicketID");
                    if (manualId > 0)
                    {
                        ModConfig.Data.TicketLastManualId = manualId;
                        ModConfig.Data.TicketUseNext = false;
                        try { ModConfig.Save(); } catch (Exception) { }
                        Log.Warn("[nav] 选票页: 人工已选票 #" + manualId + " → 已记住，下次用票标志复位");
                    }
                    else
                    {
                        if (Time.realtimeSinceStartup - _ticketNextWarnAt > 5.0)
                        {
                            _ticketNextWarnAt = Time.realtimeSinceStartup;
                            Log.Warn("[nav] 选票页: 等待人工选票（TicketUseNext=true，C 键=決定；30s 倒计时结束前必须选完）");
                        }
                        return false;
                    }
                }

                if (_ticketForcedId != process.ProcessId)
                {
                    if (Time.realtimeSinceStartup - _ticketAt < 0.5) return false;
                    _ticketAt = Time.realtimeSinceStartup;
                    _ticketForcedId = process.ProcessId;
                    _ticketStateLogged = -1;
                    _ticketTimedUp = false;

                    bool useTicket = ModConfig.Data.TicketUseKeep && ModConfig.Data.TicketLastManualId > 0;
                    int targetId = useTicket ? ModConfig.Data.TicketLastManualId : 0;
                    var monitors = NavReflect.Get(process, "_monitors") as Array;
                    if (monitors != null)
                    {
                        for (int i = 0; i < monitors.Length; i++)
                        {
                            var mon = monitors.GetValue(i);
                            if (mon == null) continue;
                            NavReflect.Set(mon, "_isTimeUp", true);
                            NavReflect.Set(mon, "_isDecidedOK", true);
                            NavReflect.Set(mon, "_isDecidedEntry", true);
                            NavReflect.Set(mon, "_decidedSelectTicketID", targetId);
                            NavReflect.SetEnum(mon, "_state", 44);   // MonitorState.End
                        }
                    }
                    NavReflect.Set(process, "_isTimeUp", true);
                    Log.Warn("[nav] 选票页: 直接置 End(state=44) 跳过倒计时 → " +
                             (useTicket ? ("使用票 #" + targetId) : "不选票（票 ID=0）") +
                             " monitors=" + (monitors != null ? monitors.Length : 0));
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("ticket-select", "选票页操作失败: " + e.Message);
                return false;
            }
        }

        /// <summary>上一次直接置 End 的 TicketSelectProcess id（0 = 还没处理过任何一次）。</summary>
        private static uint _ticketForcedId;
        private static double _ticketNextWarnAt;

        /// <summary>取 process._monitors[monitor]（越界/反射失败返回 null）。</summary>
        private static object GetMonitorAt(object process, int monitor)
        {
            try
            {
                var array = NavReflect.Get(process, "_monitors") as Array;
                if (array != null && monitor >= 0 && monitor < array.Length) return array.GetValue(monitor);
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>取 `_monitors[index]`（越界/反射失败返回 null）。</summary>
        private static object MonitorAtIndex(object process, int index)
        {
            try
            {
                var array = NavReflect.Get(process, "_monitors") as Array;
                if (array == null || index < 0 || index >= array.Length) return null;
                return array.GetValue(index);
            }
            catch (Exception) { return null; }
        }

        /// <summary>在 `_ticketSelectTicketData` 里找 ticketID == id 的下标（找不到 -1）。</summary>
        private static int FindTicketRefIndex(object ticketMonitor, int ticketId)
        {
            try
            {
                var list = NavReflect.Get(ticketMonitor, "_ticketSelectTicketData") as System.Collections.IList;
                if (list == null) return -1;
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (item == null) continue;
                    if (NavReflect.ToInt(NavReflect.Get(item, "ticketID")) == ticketId) return i;
                }
            }
            catch (Exception) { }
            return -1;
        }

        private static bool _ticketTimedUp;

        private static double _charaAt = -1000;
        private static double _charaSince = -1;
        private static bool _charaPressed;
        private static int _charaStateLogged = -1;

        /// <summary>角色选择页：真实 4 号键「決定」（确认默认角色）。</summary>
        private static double _tutorialAt = -1000;
        private static int _tutorialStateLogged = -1;
        private static int _tutorialAttempts;
        private static double _tutorialWaitLogAt = -1000;

        /// <summary>
        /// 新手教程选择页（TutorialSelectProces）—— 按 4 号键 = 「不参加教程」。
        /// 游戏 IL（DetailInputOnDisp）：Button05→基础教程(1)、Button02→新教程(2)、Button04→不参加(3)，
        /// 随后 TutorialPlayed=0 并 Fade 到 MusicSelectInfoProcess，直接进选歌。
        /// </summary>
        /// <summary>
        /// 新手教程选择页（TutorialSelectProces）—— 按 **5 号键** = 屏幕底部的
        /// 「SKIP チュートリアルをスキップ」。游戏 IL（DetailInputOnDisp，注意参数是
        /// ButtonSetting 枚举：Button05=4、Button06=5）：
        ///   Button06(5) → entryStatus=1 → SelectBasicTutorialSelect（きほん）
        ///   Button03(2) → entryStatus=2 → SelectNewTutorialSelect（新要素）
        ///   Button05(4) → entryStatus=3 → SelectNotTutorialSelect → TutorialPlayed=0 → 直接进选歌
        /// 首次尝试真实 5 号键；若 0.8s 后仍停在 Disp(3)，直接写 entryStatus=3 兜底。
        /// </summary>
        private static bool HandleTutorialSelect(int monitor)
        {
            try
            {
                var process = FindLiveByName("TutorialSelectProces", "TutorialSelectProcess");
                if (process == null)
                {
                    _tutorialAt = -1000;
                    _tutorialStateLogged = -1;
                    _tutorialAttempts = 0;
                    return false;
                }
                int state = GetInstanceInt(process, "_state");
                if (state != _tutorialStateLogged)
                {
                    _tutorialStateLogged = state;
                    _tutorialAttempts = 0;
                    Log.Info("[nav] 新手教程选择页: state=" + state);
                }
                if (state != 3) return false;   // Init/Wait/DispWait/DispEnd：让游戏自己播
                double now = Time.realtimeSinceStartup;
                if (now - _tutorialAt < 0.8) return false;
                _tutorialAt = now;
                if (_tutorialAttempts == 0)
                {
                    InputInject.Press(monitor, Manager.InputManager.ButtonSetting.Button05);   // SKIP
                    Log.Warn("[nav] 新手教程选择页: 按 5 号键选择「不参加教程」（直接进选歌）");
                }
                else
                {
                    NavReflect.SetEnum(process, "_entryStatus", 3);   // 兜底：直接置「不参加教程」
                    Log.Warn("[nav] 新手教程选择页: 5 号键未推进 → 直接置 entryStatus=3");
                }
                _tutorialAttempts++;
                return true;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("tutorial-select", "教程选择页操作失败: " + e.Message);
                return false;
            }
        }


        // ============================================================================================
        //  用户接管：每个需要选择的界面先停 N 秒（默认 3s），期间释放机台锁；
        //  用户一旦操作就整屏让给用户（自动化不再注入），直到界面切换。
        // ============================================================================================
        private static string _holdKey = "";
        private static double _holdUntil = -1000;
        private static bool _holdUserTookOver;
        private static double _holdTookOverUntil = -1000;

        /// <summary>当前是否处在「让用户操作」窗口（机台锁应临时释放）。</summary>
        public static bool UserHoldActive
        {
            get { double n = Time.realtimeSinceStartup; return n < _holdUntil || (_holdUserTookOver && n < _holdTookOverUntil); }
        }

        public static void ResetUserHold()
        {
            _holdKey = "";
            _holdUntil = -1000;
            _holdUserTookOver = false;
        }

        /// <summary>是否检测到真人输入（按住/触摸）。我们的注入在 hold 期间不会发生，所以任何输入都算用户。*</summary>
        public static bool UserInputSeen()
        {
            try
            {
                for (int m = 0; m < 2; m++)
                {
                    if (Manager.InputManager.IsButtonPush(m)) return true;
                    if (Manager.InputManager.IsTouchPanelPush(m)) return true;
                    if (Manager.InputManager.IsTouchPanelDown(m)) return true;
                    if (Manager.InputManager.GetTouchPanelAreaAnyDown(m)) return true;
                    for (int b = 0; b <= 9; b++)
                    {
                        try { if (Manager.InputManager.GetButtonPush(m, (Manager.InputManager.ButtonSetting)b)) return true; }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return false;
        }


        /// <summary>用户在续关「是/否」屏按了「否」（5 号键）。我们从不注入 5 号键，所以一定是真人。*</summary>
        public static bool UserPressedNo()
        {
            try
            {
                if (Manager.InputManager.GetButtonDown(0, Manager.InputManager.ButtonSetting.Button05)) return true;
                if (Manager.InputManager.GetButtonDown(1, Manager.InputManager.ButtonSetting.Button05)) return true;
            }
            catch (Exception) { }
            return false;
        }
        /// <summary>
        /// 需要选择的界面调用：返回 true = 现在不要注入（等待 3s / 用户已接管）。
        /// key 用「界面名:monitor」，界面切换后重新计时。
        /// </summary>
        public static bool HoldForUser(string key, double seconds)
        {
            double now = Time.realtimeSinceStartup;
            if (key != _holdKey)
            {
                _holdKey = key;
                _holdUntil = now + (seconds > 0 ? seconds : 3.0);
                _holdUserTookOver = false;
                Log.Info("[hold] 界面 " + key + "：先停 " + (seconds > 0 ? seconds : 3.0).ToString("0.0") + "s 给用户操作（机台锁临时释放）");
            }
            if (UserInputSeen())
            {
                if (!_holdUserTookOver)
                {
                    _holdUserTookOver = true;
                    _holdTookOverUntil = now + 15.0;   // 用户接管最多 15s，超时恢复自动化，避免「不动」
                    Log.Info("[hold] 界面 " + key + "：检测到用户操作 → 本屏交给用户，自动化暂停");
                }
                return true;
            }
            if (_holdUserTookOver)
            {
                if (now < _holdTookOverUntil) return true;
                _holdUserTookOver = false;   // 用户 15s 没推进：恢复自动化
            }
            if (now < _holdUntil) return true;
            _holdUntil = -1000;
            return false;
        }

        private static bool HandleCharacterSelect(int monitor)
        {
            try
            {
                var process = NavProcess.FindByName("CharacterSelectProces", "CharacterSelectProcess");
                if (process == null) { _charaAt = -1000; _charaSince = -1; _charaPressed = false; _charaStateLogged = -1; return false; }
                if (_charaSince < 0)
                {
                    _charaSince = Time.realtimeSinceStartup;
                    _charaPressed = false;
                    Log.Info("[nav] 旅行伙伴选择页出现（不按键，交给游戏/玩家，最多等 10s）");
                }
                double now = Time.realtimeSinceStartup;
                // 原版这一屏有 FirstInformation/SlotView/CharacterSelect 等动画，至少展示 10 秒；
                // 期间不注入，让玩家自己选；10 秒后游戏仍未走再按一次決定。
                if (!_charaPressed && now - _charaSince >= 10.0)
                {
                    _charaPressed = true;
                    _charaAt = now;
                    InputInject.Press(monitor, Manager.InputManager.ButtonSetting.Button04);
                    Log.Info("[nav] 旅行伙伴选择页: 10s 未进入下一屏 → 按 4 号键決定");
                    return true;
                }
                if (_charaPressed && now - _charaAt >= 4.0)
                {
                    _charaAt = now;
                    InputInject.Press(monitor, Manager.InputManager.ButtonSetting.Button04);
                    Log.Info("[nav] 旅行伙伴选择页: 重按 4 号键");
                }
                return true;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("charaselect", "角色选择页操作失败: " + e.Message);
                return false;
            }
        }


        /// <summary>选区页上要选的光标：flow 指定的优先，否则第一个"未完成/未锁/非无限"的区。</summary>
        private static int ResolveAreaCursor()
        {
            if (PendingAreaCursor >= 0) return PendingAreaCursor;
            try
            {
                var areas = ListAreas(0);
                for (int i = 0; i < areas.Count; i++)
                {
                    var area = areas[i];
                    if (area == null || area.IsLock || area.IsComplete || area.IsInfinity) continue;
                    return area.Index;
                }
            }
            catch (Exception) { }
            return 0;
        }

        private static string RegionStateName(int state)
        {
            switch (state)
            {
                case 0: return "Initialize";
                case 1: return "Update";
                case 2: return "ToRelease";
                case 3: return "Release";
                default: return "?" + state;
            }
        }

        private static string RegionSubStateName(int sub)
        {
            switch (sub)
            {
                case 0: return "Initialize";
                case 1: return "FirstInformation";
                case 2: return "FirstInfoWait";
                case 3: return "Discover";
                case 4: return "ToUpdate";
                case 5: return "ToUpdateWait";
                case 6: return "Update";
                case 7: return "Setting";
                case 8: return "Mission";
                case 9: return "FocusRegionList";
                case 10: return "ToDecide";
                case 11: return "RewardWait";
                case 12: return "InfinityInfoWait";
                case 13: return "Decide";
                case 14: return "ToWait";
                case 15: return "Wait";
                case 16: return "Release";
                default: return "?" + sub;
            }
        }

        private static bool SetArrayInt(object instance, string field, int index, int value)
        {
            try
            {
                var array = NavReflect.Get(instance, field) as Array;
                if (array == null || index < 0 || index >= array.Length) return false;
                array.SetValue(value, index);
                return true;
            }
            catch (Exception) { return false; }
        }

        // ============================================================================================
        //  进歌前倒计时检查（用户踩过：选区倒计时在游玩中继续跑 → 触发选区超时 → 崩溃）
        // ============================================================================================

        private static double _trackTimerCheckAt = -1000;

        /// <summary>
        /// 每轨进歌前后检查游戏倒计时：如果 GenericProcess._timerController 仍在计时（选区/受付倒计时残留），
        /// 直接 `IsTimeCounting=false` 暂停它（**不归零**，归零会触发 TimeUp 崩溃），并打 Warn。
        /// 幂等，0.2s 限频。
        /// </summary>
        internal static void EnsureCountdownStoppedForGameplay()
        {
            if (!IsTaskActive()) return;
            double now = Time.realtimeSinceStartup;
            if (now - _trackTimerCheckAt < 0.2) return;
            _trackTimerCheckAt = now;
            try
            {
                var generic = NavProcess.FindByName("GenericProcess");
                var timers = NavReflect.Get(generic, "_timerController") as Array;
                if (timers == null) return;
                string desc = "";
                bool anyRunning = false;
                for (int i = 0; i < timers.Length; i++)
                {
                    var controller = timers.GetValue(i);
                    if (controller == null) continue;
                    bool counting = ReadTimerBool(controller, "IsTimeCounting");
                    uint countdown = ReadCountDown(controller);
                    if (i == 0) desc = "areaTimer=" + countdown + ", isCounting=" + counting;
                    if (counting)
                    {
                        anyRunning = true;
                        SetTimerBool(controller, "IsTimeCounting", false);
                    }
                }
                if (anyRunning)
                    Log.Warn("[nav] 进歌前倒计时检查: " + desc + ", 已停=True（检测到仍在跑 → 已暂停）; stack=[" +
                             NavProcess.StackNames() + "]");
                else
                    NavReflect.WarnOnce("track-timer-ok",
                        "[nav] 进歌前倒计时检查: " + desc + ", 已停=False（无倒计时在跑）");
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("track-timer-check", "[nav] 进歌前倒计时检查失败: " + e.Message);
            }
        }

        /// <summary>
        /// 结算页「NEXT」推进（flow 在 Result 阶段每帧调用即可）。
        /// 依次尝试：`ResultMonitor.SetNextWait()` → `ResultMonitor.MoveNext()` → `ResultProcess.ToNextCheck()` →
        /// `ResultProcess.ToNextProcess()`，每步 1.5s 间隔、最多 6 次，打 `[nav] Result NEXT`。
        /// </summary>
        public static bool PressResultNext()
        {
            try
            {
                var process = NavProcess.Find<Process.ResultProcess>();
                if (process == null) return false;
                double now = Time.realtimeSinceStartup;
                if (now - _resultNextAt < 1.5) return false;
                _resultNextAt = now;
                if (_resultNextStep >= 6) return false;

                _resultNextStep++;
                string step;
                bool ok = false;
                if (_resultNextStep == 1 || _resultNextStep == 2)
                {
                    step = "Monitor.SetNextWait";
                    var monitors = NavReflect.Get(process, "_monitors") as Array;
                    if (monitors != null)
                        for (int i = 0; i < monitors.Length && i < 2; i++)
                            ok |= TryCallOk(monitors.GetValue(i), "SetNextWait");
                }
                else if (_resultNextStep == 3 || _resultNextStep == 4)
                {
                    step = "Monitor.MoveNext";
                    var monitors = NavReflect.Get(process, "_monitors") as Array;
                    if (monitors != null)
                        for (int i = 0; i < monitors.Length && i < 2; i++)
                            ok |= TryCallOk(monitors.GetValue(i), "MoveNext");
                }
                else if (_resultNextStep == 5)
                {
                    step = "Process.ToNextCheck";
                    ok = TryCallOk(process, "ToNextCheck");
                }
                else
                {
                    step = "Process.ToNextProcess";
                    ok = TryCallOk(process, "ToNextProcess");
                }
                Log.Info("[nav] Result NEXT 推进: step=" + _resultNextStep + "/6, method=" + step +
                         ", ok=" + ok + ", stack=[" + NavProcess.StackNames() + "]");
                return ok;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("result-next", "[nav] Result NEXT 推进失败: " + e.Message);
                return false;
            }
        }

        private static double _resultNextAt = -1000;
        private static int _resultNextStep;

        /// <summary>反射调用无参方法，返回 true=调用成功（无异常）。</summary>
        private static bool TryCallOk(object target, string method)
        {
            if (target == null) return false;
            string error;
            NavReflect.TryCall(target, method, null, out error);
            return error == null;
        }

        // ============================================================================================
        //  1) Entry → 菜单 → 选歌页
        // ============================================================================================

        /// <summary>
        /// 把游戏从当前界面推到选歌页。幂等，可每帧调用。
        /// 返回 true 表示当前已经在选歌页（可以开始 SelectChart）。
        /// </summary>
        public static bool GoToMusicSelect()
        {
            if (!IsTaskActive()) return false;   // 任务没跑时不做任何自动推进
            TrackPhaseChange();
            ClearStaleLogoutLatch();
            // 误触「設定」页 → 先退出，绝不带病推进
            if (NavProcess.Any<Process.SimpleSettingProcess>())
            {
                ExitSettings(0);
                return false;
            }
            // 进歌前后确保倒计时已停（选区/受付倒计时残留会在游玩中触发超时 → 崩溃）
            if (NavProcess.Any<GameProcess>() || NavProcess.Any<TrackStartProcess>())
                EnsureCountdownStoppedForGameplay();

            // 选区页：**绝不 fade 跳过**。  ManualAreaSelect=true 时完全不动，留给人工确认
            // 角色选择：完整交给 HandleCharacterSelect（先展示 10 秒，玩家可自己选；不提前按）。
            if (NavProcess.HasName("Process.CharacterSelectProcess", "Process.CharacterSelectProces"))
            {
                HandleCharacterSelect(0);
                return false;
            }


            // 3d) 新手教程选择页：按 4 号键 = 「不参加教程」。
            //     游戏 IL：TutorialSelectProces.DetailInputOnDisp 里 Button05→基础教程(1)、Button02→新教程(2)、
            //     Button04→不参加(3)，随后 TutorialPlayed=0 并 Fade 到 MusicSelectInfoProcess。
            var tutSelect = FindLiveByName("TutorialSelectProces", "TutorialSelectProcess");
            if (tutSelect != null)
            {
                HandleTutorialSelect(0);
                return false;
            }

            // 3e) 已经进入教程游玩页：不强行跳过（会破坏教程完成标记），只等待它自己结束。
            var tutorialPlay = FindLiveByName("TutorialProcess");
            if (tutorialPlay != null)
            {
                if (Time.realtimeSinceStartup - _tutorialWaitLogAt > 5.0)
                {
                    _tutorialWaitLogAt = Time.realtimeSinceStartup;
                    Log.Warn("[nav] 新手教程游玩页出现：等待教程自己结束（不强行跳过）");
                }
                return false;
            }

            if (NavProcess.Any<Process.RegionalSelectProcess>())
            {
                if (ModConfig.Data.ManualAreaSelect)
                {
                    if (Time.realtimeSinceStartup - _areaManualLogAt > 3.0)
                    {
                        _areaManualLogAt = Time.realtimeSinceStartup;
                        Log.Warn("[nav] 选区页(ManualAreaSelect) 不做任何操作，等待人工确认; stack=[" + NavProcess.StackNames() + "]");
                    }
                    return false;
                }
                int cursor = ResolveAreaCursor();
                SelectAreaDirect(cursor, 0);
                return false;
            }

            if (NavProcess.Any<MusicSelectProcess>())
            {
                if (!_atMusicSelect)
                {
                    // OnStart patch 没赶上（比如 Install 晚于进程启动），这里补一次
                    var current = NavProcess.Find<MusicSelectProcess>();
                    if (current != null) OnMusicSelectStarted(current);
                }
                return true;
            }
            _atMusicSelect = false;

            if (!AutoAdvance) return false;
            if (CheckPostLoginTimeout()) return false;
            if (NavProcess.Any<GameProcess>() || NavProcess.Any<TrackStartProcess>()) return false;

            // 1) 活动/新曲提示页：直接置 GotoEnd（同 AquaMai SkipEventInfo 的做法）
            var info = NavProcess.Find<Process.Information.InformationProcess>();
            if (info != null)
            {
                TrySkipInformation(info);
                return false;
            }

            // 1b) PlInformation（プレイ情報/隐私提示）：置 DispEnd，卡住再 Fade 到选歌页
            var plInfo = NavProcess.Find<Process.PlInformationProcess>();
            if (plInfo != null)
            {
                TrySkipPlInformation(plInfo);
                return false;
            }

            // 2) 区域选择：**真实按键操作**（提示页/发现动画按 4 号，光标用 3/6 号移动，決定按 4 号）。
            //    走游戏自己的输入链路，内部 subState 才会正常到 ToDecide(10) —— MapHooks 也是靠
            //    subState>=10 才认为"选区已确认"，课题曲才会被扫描到。不再直调 SelectCursolIndex/Decide。
            var region = NavProcess.Find<RegionalSelectProcess>();
            if (region != null)
            {
                MapHooks.TickAreaSelect(region);
                if (IsTaskActive() && !ModConfig.Data.ManualAreaSelect)
                    DriveRegionalSelect(0);
                return false;
            }

            // 3) 选票页（TicketSelectProcess）：真实 4 号键按「決定」，不再强行 Fade 跳过。
            //    国服没有这个界面时这里完全不触发。
            var ticket = FindLiveByName("TicketSelectProcess");
            if (ticket != null)
            {
                HandleTicketSelect(0);
                return false;
            }

            // 3) 登录后的一串菜单：模式选择（长按 timeskip + TimeUp；3 帧无效强切 MusicSelect）
            var modeSelect = NavProcess.Find<Process.ModeSelect.ModeSelectProcess>();
            if (modeSelect != null)
            {
                HandleModeSelect(modeSelect, false);
                return false;
            }

            // 3b) 登录奖励页：**必须让游戏自己走完**！
            //     `LoginBonusProcess.OnUpdate` 在结束时按这个顺序选下一屏：
            //         IsGotoCodeRead → CodeReadProcess
            //         IsGotoPhotoShoot → PhotoShootProcess
            //         IsCourseMode || IsFreedomMode → GetPresentProcess
            //         否则 → **RegionalSelectProcess（区域选择）**
            //     以前这里直接 Fade 到 MusicSelectProcess，把整屏选区跳过了 ——
            //     这就是「MODE SELECT 过了、却从来没有课题曲」的根因。
            var loginBonus = FindLiveByName("LoginBonusProcess");
            if (loginBonus != null)
            {
                double now = Time.realtimeSinceStartup;
                if (_loginBonusSince < 0 || _loginBonusId != loginBonus.ProcessId)
                {
                    _loginBonusSince = now;
                    _loginBonusId = loginBonus.ProcessId;
                    Log.Info("[nav] 登录奖励页: 等游戏自己结束 → 选区页 RegionalSelect（不再强行跳选歌）");
                }
                // 5s 还没结束：调用游戏自己的 TimeUp()（monitor 置 _isTimeUp/_isDecidedOK → IsEnd →
                // 进程按 OnUpdate 的规则 Fade 到 RegionalSelectProcess）。注意**不能**直接 Fade 到选歌页，
                // 那会把选区整屏跳过（实机 17:31 就是这样丢了一次选区）。
                if (now - _loginBonusSince >= 5.0 && _loginBonusTimeUpId != loginBonus.ProcessId)
                {
                    _loginBonusTimeUpId = loginBonus.ProcessId;
                    string err;
                    NavReflect.TryCall(loginBonus, "TimeUp", new object[0], out err);
                    Log.Warn("[nav] 登录奖励页 5s 未结束 → 游戏 TimeUp() 跳过（会正常进入选区）err=" + (err ?? "none"));
                }
                if (now - _loginBonusSince < 30.0) return false;
                if (_loginBonusFadeId != loginBonus.ProcessId)
                {
                    _loginBonusFadeId = loginBonus.ProcessId;
                    Log.Error("[nav] 登录奖励页 30s 仍未结束 → 最后兜底 Fade 到选歌页（可能丢一次选区）");
                    FadeIntoMusicSelect(loginBonus);
                }
                return false;
            }

            // 3c) 角色选择（CharacterSelectProces）：真实 4 号键「決定」，不再 Fade 跳过。
            var chara = FindLiveByName("CharacterSelectProces", "CharacterSelectProcess");
            if (chara != null)
            {
                HandleCharacterSelect(0);
                return false;
            }

            // 4) Entry 确认页（刷卡后「このAimeでプレイを開始しますか？」）
            var entry = NavProcess.Find<EntryProcess>();
            if (entry != null)
            {
                if (IsTaskActive() && AutoAdvanceEntry) AdvanceEntryCore(entry);
                return false;
            }

            // 5) 没有匹配的处理器：加载中（PleaseWait/Fade）静默等待；其余每 10s 打一次现场
            double stackNow = Time.realtimeSinceStartup;
            if (stackNow - _lastStackLogAt > 10.0)
            {
                _lastStackLogAt = stackNow;
                var stack = NavProcess.StackNames();
                bool loading = stack.IndexOf("PleaseWaitProcess") >= 0 || stack.IndexOf("FadeProcess") >= 0;
                if (!string.IsNullOrEmpty(stack) && !loading)
                    Log.Warn("[nav] GoToMusicSelect 无匹配处理器: stack=[" + stack + "] stage=" +
                             GameStageTracker.Current + "（每 10s 一次）");
            }
            return false;
        }

        private static int _infoSkipAttempts;
        private static void TrySkipInformation(Process.Information.InformationProcess info)
        {
            double now = Time.realtimeSinceStartup;
            if (now - _infoSkipAt < 0.5) return;
            _infoSkipAt = now;
            int state = NavReflect.ToInt(NavReflect.Get(info, "_state"));
            if (state >= 3) { _infoSkipAttempts = 0; return; } // 已经是 GotoEnd / Released
            _infoSkipAttempts++;
            // 正常操作：像人工一样按 4 号键推进。连续 6 次（≈3s）不动才退回置 GotoEnd 兜底。
            InputInject.Press(0, Manager.InputManager.ButtonSetting.Button04);
            if (_infoSkipAttempts > 6 && NavReflect.SetEnum(info, "_state", 3))
                Log.Info("[nav] Information 提示页: 按 4 号键 6 次无效 → 置 GotoEnd 兜底 (state=" + state + ")");
        }

        /// <summary>
        /// PlInformation（プレイ情報/隐私提示：`PlInformationSequence Init0/Wait1/Disp2/DownloadWait3/DispEnd4/DispErrorWait5/Release6`）。
        /// 先置 `DispEnd(4)`（同 AquaMai/Sinmai-Assist 跳过 `IsPlayPlInfoEnd` 的思路）；
        /// 卡住 6s 再 Fade 到选歌页兜底。
        /// </summary>
        private static void TrySkipPlInformation(Process.PlInformationProcess plInfo)
        {
            double now = Time.realtimeSinceStartup;
            if (now - _plInfoAt < 1.0) return;
            _plInfoAt = now;
            _plInfoAttempts++;

            int state = NavReflect.ToInt(NavReflect.Get(plInfo, "_state"));
            if (state >= 4) return; // 已到 DispEnd/Release

            // 用户数据还没下载完（ScoreDic 为空）→ 这是真正的下载窗口，别跳
            if (!IsUserScoreDataLoaded())
            {
                NavReflect.WarnOnce("plinfo-wait-data",
                    "[nav] PlInformation 还在下载用户数据（ScoreDic 为空）→ 不跳过，等它自己完成");
                return;
            }

            if (_plInfoAttempts <= 3)
            {
                bool ok = NavReflect.SetEnum(plInfo, "_state", 4);
                Log.Info("[nav] PlInformation 提示页跳过 (state=" + state + " -> DispEnd) ok=" + ok);
                return;
            }

            // 6s 兜底：Fade 到选歌页（每 5s 最多一次）
            if (now - _plInfoFadeAt >= 5.0)
            {
                _plInfoFadeAt = now;
                Log.Warn("[nav] PlInformation 卡住 → Fade 到 MusicSelect 兜底");
                FadeIntoMusicSelect(plInfo);
            }
        }

        private static void FadeIntoMusicSelect(ProcessBase from)
        {
            double now = Time.realtimeSinceStartup;
            if (_fadeFromId == from.ProcessId && now - _fadeAt < 5.0) return;
            _fadeFromId = from.ProcessId;
            _fadeAt = now;

            try { Manager.GameManager.IsNormalMode = true; }
            catch (Exception e) { NavReflect.WarnOnce("isnormalmode", "写 IsNormalMode 失败: " + e.Message); }
            try { Manager.GameManager.SetMaxTrack(); }
            catch (Exception e) { NavReflect.WarnOnce("setmaxtrack", "SetMaxTrack 失败: " + e.Message); }

            var container = NavProcess.ContainerOf(from);
            if (container == null)
            {
                NavReflect.WarnOnce("fade-nocontainer", "拿不到 ProcessDataContainer，无法切到选歌页");
                return;
            }
            MusicSelectProcess next;
            try { next = new MusicSelectProcess(container); }
            catch (Exception e) { Log.Error("[nav] new MusicSelectProcess 失败", e); return; }
            NavProcess.Fade(from, next, "登录后菜单 -> 选歌页");
        }

        // ---- Entry 确认页 ----

        /// <summary>
        /// 每帧可调的 Entry 推进入口（幂等）。返回 true = 这一帧确实做了动作（按了是/否）。
        ///
        /// 实机教训：flow 的 TickLogin 只轮询登录状态，没人调用导航，所以这一页会一直卡住。
        /// 因此 <see cref="Install"/> 里还 patch 了 <c>EntryProcess.OnUpdate</c> 自动驱动（见 AutoDriveEntry），
        /// 即使调用方忘了调也能过。
        /// </summary>
        public static bool TryAdvanceEntry()
        {
            TrackPhaseChange();
            var entry = NavProcess.Find<EntryProcess>();
            if (entry == null) return false;
            return AdvanceEntryCore(entry);
        }

        /// <summary>
        /// 插件任务是否正在运行：`MachineLock.Active` 或 `RebirthFlow.Phase` 处于登录/转生执行阶段。
        /// 供 Entry 自动推进、MapHooks 区域接管等后台钩子共用；用户自己玩时一律 false。
        /// </summary>
        /// <summary>无论 flow 处于哪个 step，只要角色选择页在就按正常节奏处理（页面先展示，10s 后才按）。</summary>
        public static void HandlePassiveScreens()
        {
            try
            {
                if (!NavProcess.HasName("Process.CharacterSelectProcess", "Process.CharacterSelectProces")) return;
                if (!IsTaskActive()) return;
                HandleCharacterSelect(0);
            }
            catch (Exception) { }
        }

        public static bool IsTaskActive()
        {
            if (RebirthFlow.IsStoppingNow) return false;   // 中止中：不锁、不拦、不接管
            try
            {
                if (MachineLock.Active) return true;
                switch (RebirthFlow.Phase)
                {
                    case FlowPhase.SourceLoginPending:
                    case FlowPhase.SourceReading:
                    case FlowPhase.TargetLoginPending:
                    case FlowPhase.TargetReading:
                    case FlowPhase.Transferring:
                    case FlowPhase.Stopping:
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception)
            {
                return false; // 拿不到状态时保守
            }
        }

        /// <summary>
        /// Entry 自动推进的安全闸门：只有插件任务真正在跑时才允许碰 Entry 输入。
        /// 用户自己玩（Idle/Finished/Failed/Planning/AwaitingConfirm）时一律不碰。
        /// </summary>
        public static bool IsEntryAutomationAllowed()
        {
            if (ForceEntryAdvanceWhenIdle) return true;
            if (!RequireTaskActiveForEntry) return true;
            return IsTaskActive();
        }

        private static string SafePhaseName()
        {
            try { return RebirthFlow.Phase.ToString(); }
            catch (Exception) { return "?"; }
        }

        /// <summary>
        /// ModeSelect 自动化是否允许：**只有写入阶段（`Phase == Transferring`）**。
        ///
        /// 实机 + 用户确认（lead 转发 flow-dev 的安全结论）：
        ///  - MODE SELECT 屏上按「OK 好」是**开始一局游玩**，只读模式下会产生脏 playlog 甚至卡住；
        ///  - 用户实测的登出方式是：**在 MODE SELECT 什么都不按，约 30 秒后游戏自己超时 → UserLogout**；
        ///  - 所以读取阶段 / 登出阶段对 MODE SELECT **完全不注入**；读取阶段到达 MODE SELECT 靠的是
        ///    `HandlePostLoginEntry` 对 **EntryProcess 受付屏** 的连点，而不是对 ModeSelectProcess 注入。
        /// </summary>
        private static bool IsModeSelectAutomationAllowed()
        {
            if (ForceEntryAdvanceWhenIdle) return true;
            if (!RequireTaskActiveForEntry) return true;
            return IsWritePhase();
        }

        /// <summary>是否处于写入阶段（Transferring）。</summary>
        private static bool IsWritePhase()
        {
            try { return RebirthFlow.Phase == FlowPhase.Transferring; }
            catch (Exception) { return false; }
        }

        /// <summary>是否处于"只读取数据"的阶段（此时 MODE SELECT 完全静默）。</summary>
        private static bool IsReadPhase()
        {
            try
            {
                switch (RebirthFlow.Phase)
                {
                    case FlowPhase.SourceLoginPending:
                    case FlowPhase.SourceReading:
                    case FlowPhase.TargetLoginPending:
                    case FlowPhase.TargetReading:
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>是否已经到达真正的 MODE SELECT 屏（`ModeSelectProcess._state == Disp(1)`）。</summary>
        private static bool IsModeSelectScreenReached(int state)
        {
            if (state == 1) return true;
            if (state >= 0) return false;
            // 极端情况下 _state 读不到（-1）：退回 stage 判定 + 栈顶是 ModeSelectProcess
            try
            {
                return GameStageTracker.Current == GameStage.ModeSelect &&
                       NavProcess.Find<Process.ModeSelect.ModeSelectProcess>() != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>UserData 里是否已经下载到成绩数据（ScoreDic 非空）。用于防止跳过下载步骤。</summary>
        private static bool IsUserScoreDataLoaded()
        {
            try
            {
                var manager = Singleton<UserDataManager>.Instance;
                if (manager == null) return false;
                var userData = manager.GetUserData(0);
                if (userData == null) return false;
                if (userData.ScoreDic == null) return false;
                for (int i = 0; i < userData.ScoreDic.Length; i++)
                {
                    var dict = userData.ScoreDic[i];
                    if (dict != null && dict.Count > 0) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>记录"登录成功"时刻（用于 25s 总时限）。</summary>
        private static void NoteLoginSuccess()
        {
            if (_loginSuccessAt < 0) _loginSuccessAt = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// 登录成功 → 25s 仍未到选歌页：打一条现场并停止一切导航注入，交回 flow 的兜底（Fail+登出）。
        /// 返回 true 表示已经放弃，调用方应直接 return。
        /// </summary>
        private static bool CheckPostLoginTimeout()
        {
            if (!IsTaskActive()) return false;
            if (!IsWritePhase()) return false; // 读取/登出阶段不设总时限（flow 有自己的超时）
            if (_postLoginGiveUp) return true;
            if (_loginSuccessAt < 0) return false;
            double limit = WritePhaseModeSelectTimeoutSec > 0 ? WritePhaseModeSelectTimeoutSec : 90.0;
            if (Time.realtimeSinceStartup - _loginSuccessAt < limit) return false;

            _postLoginGiveUp = true;
            if (!_postLoginGiveUpLogged)
            {
                _postLoginGiveUpLogged = true;
                Log.Warn("[nav] " + limit.ToString("0") + "s 未到选歌页，交回 flow（停止一切导航注入）; stage=" +
                         GameStageTracker.Current + " stack=[" + NavProcess.StackNames() + "]");
            }
            return true;
        }

        private static void ResetPostLoginState()
        {
            _loginSuccessAt = -1000;
            _postLoginGiveUp = false;
            _postLoginGiveUpLogged = false;
            _postLoginAttempts = 0;
            _postLoginNextCount = 0;
            _postLoginAt = -1000;
            _postLoginLogged = false;
            _postLoginRapidFrames = 0;
            _postLoginRapidAt = -1000;
            _postLoginRapidDone = false;
            _downloadProcessTried = false;
            _modeSelectProcessId = 0;
            _modeSelectAttempts = 0;
            _modeSelectAt = -1000;
            _modeSelectForcedAt = -1000;
            _modeSelectGaveUp = false;
            _modeSelectStuckSince = -1000;
            _modeSelectHoldFrames = 0;
            _modeSelectHoldLastAt = -1000;
            _modeSelectPhase = 0;
            _modeSelectPhaseAt = -1000;
            _modeSelectStateWritten = false;
            _modeSelectOkTried = false;
            _modeSelectReachedLogged = false;
            _modeSelectWaitLogged = false;
            _loginBonusSince = -1;
            _loginBonusId = 0;
            _logoutModeSelectLogged = false;
            _modeSelectOkAt = -1000;
            _modeSelectOkTries = 0;
            _okPhase = 0;
            _okHoldStarted = false;
            _okPhaseAt = -1000;
            _okRegionalSince = -1000;
            _areaDirectAt = -1000;
            _areaDirectFirstAt = -1000;
            _areaDirectTimedUp = false;
            _areaDirectWaitLogged = false;
            _okAnimFrames = 0;
            _okButtonIdx = 0;
            _okWaitLogAt = -1000;
            ResetModeSelectPressState();
            _logoutRequested = false;
            _logoutBurstCount = 0;
            _logoutLastBurstAt = -1000;
            _lastForceTimeUpAt = -1000;
            _countdownZeroAt = -1000;
            _countdownZeroAttempts = 0;
            _countdownZeroFirstAt = -1000;
            _countdownZeroStageLogged = false;
            _logoutDoneLogged = false;
            _plInfoAt = -1000;
            _plInfoAttempts = 0;
            _plInfoFadeAt = -1000;
            // 新 credit 的结算页 NEXT 梯次重新计数（flow 每 credit 都会调 PressResultNext）
            _resultNextAt = -1000;
            _resultNextStep = 0;
            _continueStep = 0;
            _continueYesAt = -1000;
            _continueStartedLogged = false;
            _continueDoneLogged = false;
            _continueWaitLogged = false;
            ClearPendingTouches();
        }

        /// <summary>自动驱动：每 0.5s 最多尝试一次（由 EntryProcess.OnUpdate postfix 掉用）。</summary>
        internal static void AutoDriveEntry()
        {
            if (!AutoAdvanceEntry) return;
            if (!IsEntryAutomationAllowed()) return;
            try
            {
                double now = Time.realtimeSinceStartup;
                if (now - _autoDriveAt < 0.5) return;
                _autoDriveAt = now;
                TryAdvanceEntry();
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("autodrive", "Entry 自动推进异常: " + e.Message);
            }
        }

        internal static bool AdvanceEntryCore(EntryProcess entry)
        {
            double now = Time.realtimeSinceStartup;

            // 换了一个 EntryProcess 实例 = 新的一次登录流程 → 计数全部归零
            if (entry.ProcessId != _entryProcessId)
            {
                _entryProcessId = entry.ProcessId;
                _entryConfirmCount = 0;
                _entryNudgeCount = 0;
                _entryDone = false;
                _entryStopReason = -1;
                _entryStateSeen = -1;
                _entryStateMode = -1;
                _confirmPlayStuckTicks = 0;
                _confirmPlayLastMode = -1;
                _confirmPlayDiagLogged = false;
                _confirmPlayWaitZeroed = false;
                _confirmPlayModeForced = false;
                _entryExecInjections = 0;
                _newUserConfirmCount = 0;
                _newUserLastMode = -999;
                _postLoginAttempts = 0;
                _postLoginNextCount = 0;
                _postLoginAt = -1000;
                _postLoginLogged = false;
                _postLoginRapidFrames = 0;
                _postLoginRapidAt = -1000;
                _postLoginRapidDone = false;
                _downloadProcessTried = false;
                _logoutRequested = false;   // 新登录流程 → 退出"登出连点"模式
                _logoutBurstCount = 0;
                _logoutLastBurstAt = -1000;
                _countdownZeroAt = -1000;
                _countdownZeroAttempts = 0;
                _countdownZeroFirstAt = -1000;
                _countdownZeroStageLogged = false;
                _logoutDoneLogged = false;
            }

            if (now - _entryConfirmAt < 0.25) return false; // 按钮/响应每帧刷没意义，限到 4 次/秒
            _entryConfirmAt = now;

            if (CheckPostLoginTimeout()) return false;

            // 用户自己玩（任务未运行）时，绝不碰 Entry 输入
            if (!IsEntryAutomationAllowed())
            {
                NavReflect.WarnOnce("entry-idle-skip",
                    "Entry 自动推进已跳过：插件任务未运行（RebirthFlow=" + SafePhaseName() +
                    ", MachineLock=" + MachineLock.Active + "）");
                return false;
            }

            // ---- 登录成功之后：Entry 确认按键停手，但"受付/开始"这一屏还要继续推进 ----
            // 注意：新卡建号（ConfirmNewUser=3）时 UserData 里可能已有 uid，但账号还没建完，
            // 不能当成"已登录"直接去推进受付屏；只有 DoneEntry(10)/Nop(0) 才算登录流程完成。
            int preState = -1, preMode = -1;
            try
            {
                Process.Entry.State.StateType preType;
                entry.GetCurrentState(out preType, out preMode);
                preState = (int)preType;
            }
            catch (Exception) { }
            // 登录成功后的受付推进：**不再要求 state==DoneEntry**。
            //
            // 实机 01:14（flow-dev）：UserLogin returnCode=1 之后 EntryProcess 立刻变成
            // `ConfirmEntry(state=1, mode=2 Reading)` 并停住 —— 这其实是登录后的"受付/等待"阶段，
            // 真正的 `CreateDownloadProcess`（触发 GetUserMusicApi）要等受付计时结束才发生。
            // 旧逻辑要求 state∈{10,0,-1} 才进 HandlePostLoginEntry，导致受付永远没人推、数据读不到。
            // 现在：只要已登录且当前不是「新规建号确认(3)」，就交给 HandlePostLoginEntry（连点受付）。
            bool newUserConfirmPage = preState == 3;
            if (IsRealPlayerLoggedIn() && !newUserConfirmPage)
            {
                NoteLoginSuccess();
                if (!_entryDone)
                {
                    _entryDone = true;
                    _entryStopReason = 0;
                    Log.Info("[nav] 登录成功 → 停止 Entry 确认按键（共尝试 " + _entryConfirmCount +
                             " 次），继续推进受付/模式选择屏; state=" + preState + ", mode=" + preMode);
                }
                return HandlePostLoginEntry(entry, now);
            }

            // 其它停止原因（新卡不安全 / 次数到顶）
            if (_entryDone || _entryStopReason >= 0)
            {
                if (_entryNudgeCount >= 3) return false;
            }

            Process.Entry.State.StateType type;
            int mode;
            try { entry.GetCurrentState(out type, out mode); }
            catch (Exception e)
            {
                NavReflect.WarnOnce("entry-state", "GetCurrentState 失败: " + e.Message);
                return false;
            }

            int state = (int)type;

            if (state != _entryStateSeen || mode != _entryStateMode)
            {
                _entryStateSeen = state;
                _entryStateMode = mode;
                _entryStateAt = now;
                Log.Info("[nav] Entry 状态: " + type + " (state=" + state + ", mode=" + mode + "/" +
                         ModeName(state, mode) + ")");
                if (state == 1) DumpEntryCandidates();
            }

            if (state == 0) return false; // Nop
            if (state == 10)              // DoneEntry：登录流程已完成
            {
                _entryDone = true;
                // 只在写入阶段才催 SetNextProcess；读取阶段必须让游戏自己下载用户数据
                // （实机证据：提前 SetNextProcess 会让 GetUserMusicApi 完全不发出）
                if (IsModeSelectAutomationAllowed() &&
                    now - _entryStateAt > 2.0 && _entryNudgeCount < 3)
                {
                    _entryNudgeCount++;
                    Log.Info("[nav] Entry DoneEntry → SetNextProcess 第 " + _entryNudgeCount + " 次（写入阶段）");
                    NavReflect.Call(entry, "SetNextProcess");
                    _entryStateAt = now;
                    return true;
                }
                return false;
            }

            // ------------------------------------------------------------------
            // 安全白名单（flow-dev 要求在"新账号/继承/错误"相关页面上绝不自动按是，
            // 宁可什么都不按让流程超时登出，也不能给账号建新数据）：
            //
            //   ConfirmEntry(1) 的 mode 是 Process.Entry.State.ConfirmEntry.ProcMode：
            //     Setup=0, Request=1, Reading=2, Suspend=3,
            //     ResultNewAime=4, ResultNewFelica=5, NewFelicaPolling=6, NewFelicaWaitNotice=7,
            //     NewFelicaWaitConfirmSite=8, NewFelicaNoticeDataMigration=9, NewFelicaWaitDispQR=10,
            //     NewFelicaDone=11, NewAimeDispFelicaSiteAndQR=12, Error=13
            //   只在 1/2/3（请求/读卡/等待玩家决定）这三个"普通等待"阶段按是；
            //   4 及以上（新卡/新 Felica/迁移/二维码/错误）一律不动。
            //
            //   ConfirmEntry(1)      ：mode∈{1,2,3} 才按是
            //   ConfirmPlay(4)       ：开始游戏 → 是
            //   ConfirmContinue(8/9) ：继续上次会话 → 是
            //   ConfirmNewUser(3)    ：新规用户登录 → **不动**（危险）
            //   ConfirmGuest(2)      ：游客 → 否（不进游客模式）
            //   ConfirmFreedom(5/6)  ：自由模式 → 否
            //   ConfirmAccessCode(7) ：手动输码 → 不动
            // ------------------------------------------------------------------
            bool wantYes;
            switch (state)
            {
                case 1: // ConfirmEntry
                    if (!IsSafeConfirmEntryMode(mode))
                    {
                        NavReflect.WarnOnce("entry-confirm-mode:" + mode,
                            "ConfirmEntry 的 mode=" + mode + " " + ModeName(state, mode) +
                            "，属于新卡/继承/错误类子流程，为安全起见不自动按是；" +
                            "若这是标准「開始しますか？」页请把这条日志发给 nav-dev");
                        return false;
                    }
                    wantYes = true;
                    break;
                case 4:  // ConfirmPlay —— 状态机里另有 ProcMode，走专门推进（见 AdvanceConfirmPlay）
                    return AdvanceConfirmPlay(entry, mode);
                case 8:  // ConfirmContinue
                case 9:  // ConfirmContinueOne
                    wantYes = true;
                    break;
                case 2:  // ConfirmGuest
                case 5:  // ConfirmFreedom
                case 6:  // ConfirmFreedomOne
                    wantYes = false;
                    break;
                case 3: // ConfirmNewUser —— 未注册新 aime 的建号确认（默认不按，需 AllowConfirmNewUser=true）
                    if (!AllowConfirmNewUser)
                    {
                        NavReflect.WarnOnce("entry-newuser-blocked",
                            "Entry 状态 ConfirmNewUser(3) 是建号确认；默认不自动按是。" +
                            "确需建号请把 config.json 的 AllowConfirmNewUser 设为 true（或 Navigation.AllowConfirmNewUser=true）");
                        return false;
                    }
                    // 每个 ProcMode 只按一次（建号确认 → 同意条款等会有多次确认），最多 6 次，避免重复建号
                    if (_newUserConfirmCount >= 6)
                    {
                        NavReflect.WarnOnce("entry-newuser-limit",
                            "ConfirmNewUser 已按 6 次仍未完成建号 → 停手等人工; mode=" + mode);
                        return false;
                    }
                    if (_newUserConfirmCount > 0 && mode == _newUserLastMode)
                    {
                        NavReflect.WarnOnce("entry-newuser-wait",
                            "ConfirmNewUser 当前 mode=" + mode + " 已按过「是」→ 等状态机前进");
                        return false;
                    }
                    _newUserLastMode = mode;
                    _newUserConfirmCount++;
                    Log.Info("[nav] ConfirmNewUser → 已按「是」建号（AllowConfirmNewUser=true）; mode=" +
                             mode + ", try=" + _newUserConfirmCount + "/6, stack=[" +
                             NavProcess.StackNames() + "]");
                    return PerformEntryAnswer(entry, state, mode, true);
                default: // 7 ConfirmAccessCode / 其它 → 一律不动
                    NavReflect.WarnOnce("entry-unsafe-state:" + state,
                        "Entry 状态 " + state + " 属于「不自动确认」白名单（输码等），等人工或超时");
                    return false;
            }

            return PerformEntryAnswer(entry, state, mode, wantYes);
        }

        // ------------------------------------------------------------------ 登录后：受付 / 模式选择

        /// <summary>
        /// 登录成功之后：**什么都不做**，把受付屏完全交还给游戏。
        ///
        /// 实机对比（lead 23:30 vs 23:34，同账号/同一天）：
        ///  - 用户手动流程：Login → EntryProcess 停留 ~4s → Startup(PleaseWait) → GetUserRatingApi /
        ///    GetUserMusicApi(0/50/100) / GetUserPortrait… → 回 ModeSelectProcess；
        ///  - 我们的自动流程：登录后立刻 `IsTimeCounting(false)+IsFastSkip(true)+DecrementTimerSecond(30)`，
        ///    4.7s 又 `SetNextProcess()` → 整个会话 `GetUserMusicApi = 0`，账号数据根本没下载。
        ///
        /// 结论：登录后不要注入任何东西（不停表/不快进/不 SetNextProcess）。Entry 状态机自己会在 ~4s 内
        /// 下载用户数据并过渡到 Startup/ModeSelect。这里只打一行日志用于时间轴对照。
        /// </summary>
        /// <summary>
        /// 登录成功后的受付屏推进：**只对 EntryProcess 承载的受付屏连点 timeskip**。
        ///
        /// 用户实机链路（lead 转述）：
        ///   登录 → 受付屏连点跳过 → 「MODE SELECT（NORMAL/FREEDOM/段位認定）」停手
        ///   → flow 读数据 → 在 MODE SELECT 什么都不按、等 ~30s 游戏自己超时登出 → SourceReady。
        ///
        /// 安全边界：
        ///  - 只调 `DecrementTimerSecond(30)` 连点（=按 timeskip 键），**不做** `IsTimeCounting(false)` /
        ///    `IsFastSkip(true)` / `SetNextProcess()`（实机证据：SetNextProcess 会跳过用户数据下载）；
        ///  - 一旦栈里出现 `ModeSelectProcess`（MODE SELECT 屏）→ 立刻停手，什么都不注入。
        /// </summary>
        private static bool HandlePostLoginEntry(EntryProcess entry, double now)
        {
            if (_logoutRequested) return EndReceptionBurst(entry);

            // 到达 MODE SELECT → 立刻停止一切注入（这一屏按 OK = 开局；登出靠被动超时）
            if (NavProcess.Find<Process.ModeSelect.ModeSelectProcess>() != null)
            {
                if (!_postLoginRapidDone)
                {
                    _postLoginRapidDone = true;
                    Log.Info("[nav] 已到 MODE SELECT，停止一切注入（等待 flow 读取/被动登出）");
                }
                return false;
            }

            if (_postLoginRapidDone)
            {
                // 受付连点 120 次仍未离开受付 → 直调 CreateDownloadProcess() 触发数据下载
                // （实机 01:14：登录后 EntryProcess 停在 ConfirmEntry(Reading)，GetUserMusicApi 一次都不发）
                if (!_downloadProcessTried && IsReadPhase())
                {
                    _downloadProcessTried = true;
                    int st = -1, md = -1;
                    try
                    {
                        Process.Entry.State.StateType t;
                        entry.GetCurrentState(out t, out md);
                        st = (int)t;
                    }
                    catch (Exception) { }
                    bool ok = TryCallArgs(entry, "CreateDownloadProcess");
                    Log.Warn("[nav] 受付连点未离开 → 直调 EntryProcess.CreateDownloadProcess() 触发数据下载 ok=" + ok +
                             "; state=" + st + ", mode=" + md + ", stack=[" + NavProcess.StackNames() + "]");
                }
                return false;
            }
            if (now - _postLoginRapidAt < 0.02) return false; // ~50Hz 连点
            _postLoginRapidAt = now;

            int state = -1, mode = -1;
            try
            {
                Process.Entry.State.StateType type;
                entry.GetCurrentState(out type, out mode);
                state = (int)type;
            }
            catch (Exception) { }

            if (!_postLoginLogged)
            {
                _postLoginLogged = true;
                Log.Info("[nav] 登录成功 → 受付屏连点 timeskip（仅 EntryProcess 受付）; state=" + state +
                         ", mode=" + mode + ", stack=[" + NavProcess.StackNames() + "]");
            }

            int target = ModeSelectRapidPressFrames > 0 ? ModeSelectRapidPressFrames : 120;
            _postLoginRapidFrames++;
            for (int i = 0; i < 3; i++)
                TryCallArgs(entry, "DecrementTimerSecond", 30);
            if (_postLoginRapidFrames == 1 || _postLoginRapidFrames % 10 == 0)
                Log.Info("[nav] 受付连点 timeskip " + _postLoginRapidFrames + "/" + target +
                         ", state=" + state + ", mode=" + mode +
                         ", stack=[" + NavProcess.StackNames() + "]");

            if (_postLoginRapidFrames >= target)
            {
                _postLoginRapidDone = true;
                Log.Warn("[nav] 受付连点 " + target + " 次仍未离开受付 → 停手等游戏/flow; state=" + state);
            }
            return true;
        }

        /// <summary>
        /// 登出时把游戏自己的倒计时强制归零（用户的正确登出语义：倒计时归零 → 游戏弹超时 → 回 Title → 游戏自己发 UserLogout）。
        ///
        /// `Monitor.TimerController` 是普通类（非 MonoBehaviour），实例挂在
        /// `Process.GenericProcess._timerController`（`TimerController[]`）上。
        /// 公开成员：`CountDownSecond`(get)、`IsInfinity`(get/set)、`IsTimeCounting`(set)、
        /// `IsTimeUp`(get)、`SetTimeZero()`、`DecrementTime(int)`、`UpdateTimer(float,int)`、`PrepareTimer(int)`。
        ///
        /// `DisableTimeout` 这类 mod 会把 `IsInfinity=true` 或把秒数冻在 65535，所以这里同时：
        /// 关掉 Infinity → 打开计时 → `SetTimeZero()`（失败则直写 `_countDownSecond=0`）→ 再补 `DecrementTime(9999)`。
        /// 只在 `_logoutRequested` 时生效（用户自己玩不受影响）。
        /// </summary>
        internal static bool ForceCountdownToZero()
        {
            if (!_logoutRequested) return false;

            // 已经到 Title/Entry 说明游戏自己进超时流程了
            var stage = GameStageTracker.Current;
            if (stage == GameStage.Title || stage == GameStage.Entry || stage == GameStage.Startup)
            {
                if (!_logoutDoneLogged)
                {
                    _logoutDoneLogged = true;
                    Log.Info("[nav] 登出: 倒计时生效 → stage=" + stage + "，游戏将自行 UserLogout");
                }
                _logoutRequested = false;
                return true;
            }

            double now = Time.realtimeSinceStartup;
            if (now - _countdownZeroAt < 0.2) return false;
            _countdownZeroAt = now;
            _countdownZeroAttempts++;
            if (_countdownZeroFirstAt < 0) _countdownZeroFirstAt = now;
            if (_countdownZeroAttempts > 300) // 60s，flow 会兜底 PacketUserLogout
            {
                NavReflect.WarnOnce("countdown-zero-giveup",
                    "[nav] 登出: 强制倒计时归零 300 次仍没回 Title → 停手（flow 的 PacketUserLogout 兜底）");
                return false;
            }

            bool any = false;
            try
            {
                var generic = NavProcess.FindByName("GenericProcess");
                var timers = NavReflect.Get(generic, "_timerController") as Array;
                if (timers != null)
                {
                    for (int i = 0; i < timers.Length; i++)
                    {
                        var controller = timers.GetValue(i);
                        if (controller == null) continue;
                        any = true;

                        uint before = ReadCountDown(controller);
                        bool infBefore = ReadTimerBool(controller, "IsInfinity");

                        // DisableTimeout 之类会把无限/冻结算打开，必须先关掉
                        SetTimerBool(controller, "IsInfinity", false);
                        SetTimerBool(controller, "IsTimeCounting", true);
                        string zeroErr;
                        bool zeroOk = CallInstanceMethod(controller, "SetTimeZero", null, out zeroErr);
                        if (!zeroOk) SetInstanceField(controller, "_countDownSecond", 0, out zeroErr);
                        TryCallArgs(controller, "DecrementTime", 9999);

                        uint after = ReadCountDown(controller);
                        // 只对 monitor 0 打完整日志，避免双行
                        if (i == 0 || after != before)
                        {
                            Log.Info("[nav] 登出: 强制倒计时归零 attempt=" + _countdownZeroAttempts +
                                     ", controller=TimerController[" + i + "]" +
                                     ", before=" + before + ", after=" + after +
                                     ", isInfinity=" + infBefore + "->" + ReadTimerBool(controller, "IsInfinity") +
                                     ", isTimeUp=" + ReadTimerBool(controller, "IsTimeUp") +
                                     ", ok=" + zeroOk);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("countdown-zero", "强制倒计时归零失败: " + e.Message);
            }

            if (!any)
                NavReflect.WarnOnce("countdown-no-controller",
                    "[nav] 登出: 找不到 GenericProcess._timerController（倒计时无法强制归零）");

            if (!_countdownZeroStageLogged)
            {
                _countdownZeroStageLogged = true;
                Log.Info("[nav] 登出: 倒计时归零后 stage=" + stage + "（期待 -> Title）");
            }
            return any;
        }

        private static uint ReadCountDown(object controller)
        {
            try
            {
                var prop = NavReflect.FindProperty(controller.GetType(), "CountDownSecond");
                if (prop != null && prop.CanRead) return (uint)NavReflect.ToInt(prop.GetValue(controller, null));
            }
            catch (Exception) { }
            return (uint)NavReflect.ToInt(NavReflect.Get(controller, "_countDownSecond"));
        }

        private static bool ReadTimerBool(object controller, string property)
        {
            try
            {
                var prop = NavReflect.FindProperty(controller.GetType(), property);
                if (prop != null && prop.CanRead) return NavReflect.ToBool(prop.GetValue(controller, null));
            }
            catch (Exception) { }
            return NavReflect.ToBool(NavReflect.Get(controller, "_" + char.ToLowerInvariant(property[0]) + property.Substring(1)));
        }

        private static bool SetTimerBool(object controller, string property, bool value)
        {
            try
            {
                var prop = NavReflect.FindProperty(controller.GetType(), property);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(controller, value, null);
                    return true;
                }
            }
            catch (Exception) { }
            // 回退：写 backing field（setter 可能是 private）
            var name = "_" + char.ToLowerInvariant(property[0]) + property.Substring(1);
            string error;
            return SetInstanceField(controller, name, value, out error);
        }

        /// <summary>检测到 flow 调用了 ForceTimeUp（登出信号）→ 打开连点/倒计时归零模式。</summary>
        internal static void OnForceTimeUpCalled()
        {
            if (!IsTaskActive()) return; // 用户自己玩的超时登出不介入
            _lastForceTimeUpAt = Time.realtimeSinceStartup;
            if (_logoutRequested) return; // 不要每次 ForceTimeUp 都重置连点计数
            _logoutRequested = true;
            _logoutBurstCount = 0;
            _logoutLastBurstAt = -1000;
            Log.Info("[nav] 检测到登出(ForceTimeUp) → 若卡在受付屏将用 timeskip 连点结束");
        }

        private static double _lastForceTimeUpAt = -1000;

        /// <summary>
        /// 新写 credit 开始（flow 在 BeginCredit / 续关成功后调用）：清掉上一个 credit 残留的
        /// 登出闩锁、续关状态、MODE SELECT 梯次、設定退出状态、待注入触摸。
        /// 实机 04:04：上一 credit 收尾时的 ForceTimeUp 让 `_logoutRequested` 一直为 true，
        /// 续关后 MODE SELECT 被当成"登出中"不按 OK → 7s 窗口丢失回 Title。
        /// </summary>
        public static void BeginWriteCredit(int monitor = 0)
        {
            double now = Time.realtimeSinceStartup;
            bool hadLogoutLatch = _logoutRequested;
            _logoutRequested = false;
            _logoutBurstCount = 0;
            _logoutLastBurstAt = -1000;
            _logoutDoneLogged = false;
            _countdownZeroAt = -1000;
            _countdownZeroAttempts = 0;
            _countdownZeroFirstAt = -1000;
            _countdownZeroStageLogged = false;
            _lastForceTimeUpAt = -1000;

            _continueStep = 0;
            _continueYesAt = -1000;
            _continueStartedLogged = false;
            _continueDoneLogged = false;
            _continueWaitLogged = false;

            _okPhase = 0;
            _okHoldStarted = false;
            _okPhaseAt = now;
            _okAnimFrames = 0;
            _okButtonIdx = 0;
            _okWaitLogAt = -1000;
            ResetModeSelectPressState();
            _modeSelectProcessId = 0;
            _modeSelectGaveUp = false;
            _modeSelectForcedAt = -1000;
            _modeSelectStuckSince = now;

            _settingsExitStep = 0;
            _settingsExitAt = -1000;
            _settingsExitLogged = false;

            ClearPendingTouches();
            Log.Info("[nav] BeginWriteCredit: 清理登出/续关/ModeSelect 残留状态" +
                     (hadLogoutLatch ? "（清除残留 _logoutRequested=true）" : "") +
                     "; stack=[" + NavProcess.StackNames() + "]");
        }

        /// <summary>
        /// ForceTimeUp 之后太久（>5s）还挂着登出闩锁、而当前又在 MODE SELECT —— 视为上一 credit 的残留，
        /// 自动清除，避免续关后的 MODE SELECT 被当成"登出中"。
        /// </summary>
        private static void ClearStaleLogoutLatch()
        {
            if (!_logoutRequested) return;
            if (_lastForceTimeUpAt < 0) return;
            if (Time.realtimeSinceStartup - _lastForceTimeUpAt < 5.0) return;
            _logoutRequested = false;
            _logoutBurstCount = 0;
            _logoutLastBurstAt = -1000;
            Log.Warn("[nav] 清除残留登出闩锁（ForceTimeUp 已过 5s，回到正常流程）; stack=[" +
                     NavProcess.StackNames() + "]");
        }

        /// <summary>
        /// 登出时"结束受付屏"的连点驱动（不杀进程、不改账号数据）：
        ///  - **MODE SELECT 屏上完全不注入**（按 OK 会开局；用户实测就是等 ~30s 游戏自己超时登出）；
        ///  - 只有受付屏还在 EntryProcess 里时才 `IsTimeCounting(false)` + 连按 `DecrementTimerSecond(30)`。
        /// 10Hz 限频，返回 true 表示"还有屏在推进"。
        /// </summary>
        private static bool EndReceptionBurst(EntryProcess knownEntry = null)
        {
            // 登出第一优先：把游戏自己的倒计时强制归零（正确语义：归零 → 超时提示 → Title → 游戏自己 UserLogout）
            ForceCountdownToZero();

            // MODE SELECT 屏：不按键（按 OK 会开局），只靠倒计时归零让游戏自己超时登出
            var modeSelect = NavProcess.Find<Process.ModeSelect.ModeSelectProcess>();
            if (modeSelect != null)
            {
                if (!_logoutModeSelectLogged)
                {
                    _logoutModeSelectLogged = true;
                    Log.Info("[nav] 登出: 当前在 MODE SELECT(state=" +
                             ModeSelectStateName(GetInstanceInt(modeSelect, "_state")) +
                             ") → 不按键，只强制倒计时归零（等游戏自己超时登出）");
                }
                return false;
            }

            double now = Time.realtimeSinceStartup;
            if (now - _logoutLastBurstAt < 0.10) return true;
            _logoutLastBurstAt = now;
            _logoutBurstCount++;

            var entry = knownEntry != null ? knownEntry : NavProcess.Find<EntryProcess>();
            if (entry != null)
            {
                TryCallArgs(entry, "IsTimeCounting", false);
                for (int i = 0; i < 10; i++)
                    TryCallArgs(entry, "DecrementTimerSecond", 30);
                if (_logoutBurstCount <= 3 || _logoutBurstCount % 10 == 0)
                    Log.Info("[nav] 登出连点 timeskip: EntryProcess burst=" + _logoutBurstCount +
                             " (10 次/组)");
                return true;
            }

            // 已经不在受付屏（回 Entry/Title 或进别的进程）→ 登出驱动完成
            _logoutRequested = false;
            return false;
        }

        /// <summary>
        /// ModeSelect 屏推进（由两层驱动）：
        ///  1) `ModeSelectProcess.UpdateInput` Prefix（每帧，最接近真实按键）：任务中且停留 ≥2s 后，
        ///     连续 3 帧模拟长按 —— 每帧 `TimeSkipButtonAnim(Button05)` + 私有 `TimeUp()`，并 **return false 跳过原 UpdateInput**
        ///     （真实按键路径就是 return false；不跳过的话游戏自己的 UpdateInput 可能把结束标志/动画清回去）；
        ///  2) GoToMusicSelect 里的 `AdvanceModeSelect`（低频）只负责"3 帧后仍无效 → 强切 Fade 到 MusicSelect"，以及 5s 现场日志。
        /// 3 帧长按无效 → `FadeProcess(container, modeSelect, new MusicSelectProcess(container))`（失败再 AddProcess+Release）；
        /// 强切 5s 还没到选歌页 → 打一次进程栈并放弃（不再 loop）。
        /// </summary>
        private static bool HandleModeSelect(Process.ModeSelect.ModeSelectProcess modeSelect, bool fromUpdateInput)
        {
            if (HoldForUser("mode:0", ModConfig.Data.UserHoldSec)) return false;
            if (modeSelect == null) return false;
            if (!IsTaskActive()) return false;
            TrackPhaseChange();
            ClearStaleLogoutLatch();

            // 误触「設定」页：先退出，别在設定上按 OK
            if (NavProcess.Any<Process.SimpleSettingProcess>())
            {
                ExitSettings(0);
                return false;
            }

            // ★ 安全闸门（lead 转发 flow-dev 结论 + 用户实测）：
            //   MODE SELECT 屏上按「OK 好」= 开始一局游玩（只读模式会产生脏 playlog / 卡住），
            //   用户的登出方式是"在 MODE SELECT 什么都不按，约 30s 后游戏自己超时登出"。
            //   所以：到屏即静默；只有写入阶段才允许按一次 OK 开始 credit。
            int probeState = GetInstanceInt(modeSelect, "_state");
            if (IsModeSelectScreenReached(probeState))
            {
                if (!_modeSelectReachedLogged)
                {
                    _modeSelectReachedLogged = true;
                    Log.Info("[nav] 已到 MODE SELECT，停止一切注入（等待 flow 读取/被动登出）; state=" +
                             ModeSelectStateName(probeState) +
                             ", _isTimeSkip=" + ReadBool(modeSelect, "_isTimeSkip") +
                             ", _isTimeUp=" + ReadBool(modeSelect, "_isTimeUp"));
                }
                if (_logoutRequested)
                {
                    // 登出：MODE SELECT 上不按键（OK=开局），只把倒计时推到 0
                    ForceCountdownToZero();
                    return false;
                }
                if (IsWritePhase()) return TryPressOkForCredit(modeSelect, fromUpdateInput);
                return false;
            }
            if (_logoutRequested)
            {
                // 登出：MODE SELECT 上不按键（OK=开局），只把倒计时推到 0，让游戏自己超时 → Title
                ForceCountdownToZero();
                if (!_logoutModeSelectLogged)
                {
                    _logoutModeSelectLogged = true;
                    Log.Info("[nav] 登出中停在 MODE SELECT(state=" + ModeSelectStateName(probeState) +
                             ") → 不按键，只强制倒计时归零");
                }
                return false;
            }
            if (!IsWritePhase())
            {
                // 读/登录阶段在 ModeSelectProcess 上一律不注入；受付屏的连点由 HandlePostLoginEntry（EntryProcess）负责
                if (!_modeSelectWaitLogged)
                {
                    _modeSelectWaitLogged = true;
                    Log.Info("[nav] ModeSelect 等待进入 MODE SELECT(state=" + ModeSelectStateName(probeState) +
                             ")，不注入; stack=[" + NavProcess.StackNames() + "]");
                }
                return false;
            }

            if (CheckPostLoginTimeout()) return false;

            // 能走到这里只可能是 `_state == Wait(0)`：Disp(1) 已经在上面分流，Released(2) 在下面处理。
            // Wait 只存在一帧，进程自己会把它推进到 Disp —— 这里绝不注入：此刻 Monitor 的
            // `_isCheckOK` 还没就绪，按键会被 Monitor.OKButtonAnim 的前置条件吞掉，只会制造
            // "按了没反应" 的假象。真正的決定注入全部在 TryPressOkForCredit 里（Disp 分支）。
            if (!_modeSelectWaitLogged)
            {
                _modeSelectWaitLogged = true;
                Log.Info("[nav] ModeSelect 等待进入 MODE SELECT(state=" + ModeSelectStateName(probeState) +
                         ")，不注入; stack=[" + NavProcess.StackNames() + "]");
            }
            return false;
        }


        private static bool ReadBool(object instance, string field)
        {
            return NavReflect.ToBool(NavReflect.Get(instance, field));
        }

        /// <summary>When true the operator presses MODE SELECT himself; nav must not inject anything.</summary>
        public static bool ManualModeSelect;

        // ---- MODE SELECT 決定注入状态（每次 BeginWriteCredit 复位） --------------------
        /// <summary>第一次注入的时间（&lt; 0 = 尚未开始）。</summary>
        private static double _okPressStartedAt = -1;
        private static double _okPressNextAt = -1;
        private static int _okPressCount;
        private static int _okPressLastMonState = -2;
        private static bool _okPressDirectTried;
        private static bool _okPressGaveUp;

        private static void ResetModeSelectPressState()
        {
            _okPressStartedAt = -1;
            _okPressNextAt = -1;
            _okPressCount = 0;
            _okPressLastMonState = -2;
            _okPressDirectTried = false;
            _okPressGaveUp = false;
            InputInject.Clear();
        }

        /// <summary>取出 `_monitors[index]`（越界 / 反射失败返回 null）。</summary>
        private static object MonitorAt(Process.ModeSelect.ModeSelectProcess modeSelect, int index)
        {
            try
            {
                var array = NavReflect.Get(modeSelect, "_monitors") as Array;
                if (array == null || index < 0 || index >= array.Length) return null;
                return array.GetValue(index);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 写入阶段在 MODE SELECT（`_state == Disp`）按下「決定」开始本 credit。
        ///
        /// 【运行时 IL 实锤 · 见 `ildump ModeSelectProcess` / `ildump Monitor.ModeSelect.ModeSelectMonitor`】
        ///   1) `ModeSelectProcess.UpdateInput` 第一行就是
        ///        if (!InputManager.GetButtonDown(monitorId, ButtonSetting.Button04)) return;
        ///      → **決定键是 4 号键**（不是 `ButtonSetting.Select`），而且吃的是 `GetButtonDown` 边沿。
        ///   2) 之后调用 `_monitors[m].OKButtonAnim(Button04)`，把 **Monitor** 的 `_isOK` 置真。
        ///      进程自己那份 `_isOK` 只是 `SyncedParam` 的副本（只在 Common 模式回灌 Monitor），
        ///      所以以前直写 process._isOK/_isDecidedOK 对 Monitor 状态机毫无作用。
        ///   3) Monitor 状态机：8 SelectCardWait --(OK)--> 24/25 CreditJudge --(自动)--> 33/34/35
        ///      DecideCreditWait --(OK)--> `_isDecidedEntry = true` --(自动淡出)--> Finish -> End。
        ///      也就是屏幕上**要按两次**：一次在「決定ボタンを押してください」页，一次在 credit 确认页。
        ///   4) `InputManager.GetButtonDown` 还要求 `IsButtonDown(monitor)`，而 `ButtonHold` 只写
        ///      `PlayerButtonPush/Down`；`UpdateAmInput` 每帧重建 `AnyButtonDown`/`DownButtonList`，
        ///      所以那种写法永远拿不到 true —— 这正是"字段看起来都对、屏幕就是不动"的根因。
        ///
        /// 因此现在只做一件正确的事：通过 <see cref="InputInject"/> 向 `GetButtonDown` 注入真实边沿，
        /// 让游戏自己的 `UpdateInput` 完整走一遍（与人工按键完全同一条链路）。
        /// 旧的「直写 _isOK / 连点 Button05 / 扫 1~8 号键 / 直写 _state=Released」全部删除。
        /// </summary>
        private static bool TryPressOkForCredit(Process.ModeSelect.ModeSelectProcess modeSelect, bool fromUpdateInput)
        {
            if (modeSelect == null) return false;
            if (ManualModeSelect) return false;          // 操作员自己按，绝不抢
            if (_logoutRequested) return false;          // 登出中按 OK = 直接开局
            if (!IsWritePhase()) return false;           // 读取/准备阶段保持静默

            int state = GetInstanceInt(modeSelect, "_state");
            if (state != 1) return false;                // 只在 Disp(1) 上按

            double now = Time.realtimeSinceStartup;
            object monitor = MonitorAt(modeSelect, 0);
            int monState = monitor != null ? GetInstanceInt(monitor, "_state") : -1;
            bool monOk = monitor != null && ReadBool(monitor, "_isOK");
            bool decided = monitor != null && ReadBool(monitor, "_isDecidedEntry");
            bool canceled = monitor != null && ReadBool(monitor, "_isCanceledEntry");

            if (_okPressStartedAt < 0)
            {
                _okPressStartedAt = now;
                _okPressNextAt = now + 0.4;              // settle：等第一帧动画/调度稳定
                _okPressCount = 0;
                _okPressLastMonState = -2;
                _okPressDirectTried = false;
                _okPressGaveUp = false;
                Log.Info("[nav] MODE SELECT 決定准备: " + ModeSelectDiagnostics(modeSelect, monitor));
            }

            if (monState != _okPressLastMonState)
            {
                _okPressLastMonState = monState;
                Log.Info("[nav] MODE SELECT monitor0 状态 → " + MonitorStateName(monState) +
                         " (isOK=" + monOk + ", decidedEntry=" + decided + ", canceledEntry=" + canceled + ")");
            }

            // 已决定 / 已取消：剩下的交给 Monitor 自己淡出、进程自己换页，绝不再按。
            if (decided || canceled) return false;

            if (_okPressGaveUp) return false;
            if (_okPressCount >= 14 || now >= _okPressStartedAt + 15.0)
            {
                _okPressGaveUp = true;
                Log.Warn("[nav] MODE SELECT 注入 Button04 " + _okPressCount + " 次仍无进展 → 停止注入; " +
                         ModeSelectDiagnostics(modeSelect, monitor));
                return false;
            }

            if (now < _okPressNextAt) return false;

            // 真实边沿 5 次仍在原地 → 直接调 Monitor.OKButtonAnim（UpdateInput 内部同一入口），
            // 万一 GetButtonDown 被别的 Mod 覆盖/吞掉，也能把状态机推过去。
            if (_okPressCount >= 5 && !_okPressDirectTried)
            {
                _okPressDirectTried = true;
                string error;
                NavReflect.TryCall(monitor, "OKButtonAnim",
                    new object[] { Manager.InputManager.ButtonSetting.Button04 }, out error);
                Log.Warn("[nav] MODE SELECT 真实按键 5 次无效 → 直调 monitor.OKButtonAnim(Button04), err=" +
                         (error ?? "none") + "; " + ModeSelectDiagnostics(modeSelect, monitor));
            }

            _okPressCount++;
            _okPressNextAt = now + 0.7;
            InputInject.Press(0, Manager.InputManager.ButtonSetting.Button04);
            Log.Info("[nav] MODE SELECT 注入 Button04(決定) #" + _okPressCount +
                     " monitor0=" + MonitorStateName(monState) +
                     ", isOK=" + monOk + ", decidedEntry=" + decided);
            return false;   // 不跳过原 UpdateInput：让游戏自己读我们注入的边沿
        }

        /// <summary>现场诊断串：进程 / Monitor 两份状态机的关键字段。</summary>
        private static string ModeSelectDiagnostics(Process.ModeSelect.ModeSelectProcess modeSelect, object monitor)
        {
            try
            {
                var sb = new System.Text.StringBuilder(192);
                sb.Append("process{state=").Append(ModeSelectStateName(GetInstanceInt(modeSelect, "_state")))
                  .Append(", monState=").Append(GetInstanceInt(modeSelect, "_monitor_state"))
                  .Append(", activeMode=").Append(GetInstanceInt(modeSelect, "_active_mode"))
                  .Append(", callCreditMain=").Append(ReadBool(modeSelect, "_isCallCreditMain"))
                  .Append(", blockingCredit=").Append(ReadBool(modeSelect, "_isBlockingDecideCredit"))
                  .Append(", isEnableOK=").Append(ReadBool(modeSelect, "_isEnableOK"))
                  .Append(", isCheckOK=").Append(ReadBool(modeSelect, "_isCheckOK"))
                  .Append(", decidedMode=").Append(GetInstanceInt(modeSelect, "_decidedSelectModeType"))
                  .Append('}');
                if (monitor != null)
                {
                    sb.Append(" monitor0{state=").Append(MonitorStateName(GetInstanceInt(monitor, "_state")))
                      .Append(", isAwake=").Append(ReadBool(monitor, "_isAwake"))
                      .Append(", isEntry=").Append(ReadBool(monitor, "_isEntry"))
                      .Append(", enableOK=").Append(ReadBool(monitor, "_isEnableOK"))
                      .Append(", checkOK=").Append(ReadBool(monitor, "_isCheckOK"))
                      .Append(", isOK=").Append(ReadBool(monitor, "_isOK"))
                      .Append(", decidedOK=").Append(ReadBool(monitor, "_isDecidedOK"))
                      .Append(", decidedEntry=").Append(ReadBool(monitor, "_isDecidedEntry"))
                      .Append(", canceledEntry=").Append(ReadBool(monitor, "_isCanceledEntry"))
                      .Append(", grayOK=").Append(ReadBool(monitor, "_isGrayOKButton"))
                      .Append(", decidedMode=").Append(GetInstanceInt(monitor, "_decidedSelectModeType"))
                      .Append('}');
                }
                return sb.ToString();
            }
            catch (Exception e) { return "<diagnostics failed: " + e.Message + ">"; }
        }

        /// <summary>MonitorState 枚举名（0..58）。</summary>
        private static string MonitorStateName(int state)
        {
            switch (state)
            {
                case 0: return "None(0)";
                case 1: return "Init(1)";
                case 2: return "SelectCardFadeInJudge(2)";
                case 3: return "SelectCardFadeInJudgeWait(3)";
                case 4: return "SelectCardFadeInWait(4)";
                case 5: return "SelectCardFadeInSync(5)";
                case 6: return "SelectCardFadeInSyncWait(6)";
                case 7: return "SelectCardWaitInit(7)";
                case 8: return "SelectCardWait(8)";
                case 9: return "SelectCardWaitSyncBoth(9)";
                case 10: return "SelectCardMoveRight(10)";
                case 11: return "SelectCardMoveLeft(11)";
                case 12: return "SettingOpen_SyncAnimWait(12)";
                case 13: return "SettingOpen_SyncBothReached(13)";
                case 14: return "SettingWait(14)";
                case 15: return "SettingChange(15)";
                case 16: return "SettingChangeWait(16)";
                case 17: return "SettingClose_SyncAnimWait(17)";
                case 18: return "SettingClose_SyncBothReached(18)";
                case 19: return "MissionOpen_SyncAnimWait(19)";
                case 20: return "MissionOpen_SyncBothReached(20)";
                case 21: return "MissionWait(21)";
                case 22: return "MissionClose_SyncAnimWait(22)";
                case 23: return "MissionClose_SyncBothReached(23)";
                case 24: return "CreditJudgeInit(24)";
                case 25: return "CreditJudge(25)";
                case 26: return "EMoneyWait(26)";
                case 27: return "EMoneyExit(27)";
                case 28: return "InsertCoinFadeIn(28)";
                case 29: return "InsertCoinFadeInWait(29)";
                case 30: return "InsertCoinWait(30)";
                case 31: return "InsertCoinFadeOut(31)";
                case 32: return "InsertCoinFadeOutWait(32)";
                case 33: return "DecideCreditFadeIn(33)";
                case 34: return "DecideCreditFadeInWait(34)";
                case 35: return "DecideCreditWait(35)";
                case 36: return "DecideCreditFadeOut(36)";
                case 37: return "DecideCreditFadeOutWait(37)";
                case 38: return "CreditJudgeReset(38)";
                case 39: return "DecidedEntryFadeIn(39)";
                case 40: return "DecidedEntryFadeInWait(40)";
                case 41: return "DecidedEntryWait(41)";
                case 42: return "DecidedEntryFadeOut(42)";
                case 43: return "DecidedEntryFadeOutWait(43)";
                case 44: return "EntryResetInit(44)";
                case 45: return "EntryReset(45)";
                case 46: return "EntryTimeOutJudge(46)";
                case 47: return "EntryTimeOutReturnTitleFadeIn(47)";
                case 48: return "EntryTimeOutReturnTitleFadeInWait(48)";
                case 49: return "EntryTimeOutReturnTitleWait(49)";
                case 50: return "EntryTimeOutReturnTitleFadeOut(50)";
                case 51: return "EntryTimeOutReturnTitleFadeOutWait(51)";
                case 52: return "AddTrackInfoJudge(52)";
                case 53: return "AddTrackInfoJudgeWait(53)";
                case 54: return "AddTrackInfoFadeIn(54)";
                case 55: return "AddTrackInfoFadeInWait(55)";
                case 56: return "AddTrackInfoWait(56)";
                case 57: return "Finish(57)";
                case 58: return "End(58)";
                default: return "?" + state;
            }
        }

        private static string ModeSelectStateName(int state)
        {
            switch (state)
            {
                case 0: return "Wait(0)";
                case 1: return "Disp(1)";
                case 2: return "Released(2)";
                default: return "?" + state;
            }
        }

        /// <summary>
        /// 强切到选歌页（AquaMai OneKeyEntryEnd 同款）：Fade 到 MusicSelect；Fade 不可用就 Add+Release。
        /// 注意：`FadeProcess` 自己会在过场结束时释放 from，所以正常路径只 AddProcess(Fade)，避免重复 Release。
        /// </summary>
        private static bool ForceMusicSelect(ProcessBase from)
        {
            if (from == null) return false;
            var container = NavProcess.ContainerOf(from);
            if (container == null)
            {
                NavReflect.WarnOnce("force-ms-nocontainer", "强切 MusicSelect 失败：ProcessDataContainer 不可用");
                return false;
            }
            try
            {
                // Prefer forcing the AREA SELECT process instead of jumping straight to song
                // select: MODE SELECT never accepts our synthetic OK, but bypassing it to
                // RegionalSelect still gives us the area crawl / travel screen.
                // 用户实机反馈：这个「直接跳到选区」会破坏游戏自己的 ModeSelect→RegionalSelect 过渡，
                // 后续第一轨的 GameProcess.OnStart 会 NRE 崩溃，而且课题曲数据也不会被算出来。
                // 所以默认关闭，只有显式打开才用。
                if (ModConfig.Data.ForceJumpAreaSelect &&
                    (ModConfig.Data.CrawlAllAreasFallback || (ModConfig.Data.CrawlAreaIds != null && ModConfig.Data.CrawlAreaIds.Count > 0)))
                {
                    try
                    {
                        var area = new Process.RegionalSelectProcess(container);
                        if (NavProcess.Fade(from, area, "ModeSelect 强切区域选择"))
                        {
                            Log.Info("[nav] MODE SELECT 强切 → 区域选择页 (绕过 OK)");
                            return true;
                        }
                    }
                    catch (Exception e) { NavReflect.WarnOnce("force-area", "[nav] 强切区域选择失败: " + e.Message); }
                }
                var next = new MusicSelectProcess(container);
                if (NavProcess.Fade(from, next, "ModeSelect 强切")) return true;
                if (NavProcess.Add(next, 50))
                {
                    NavProcess.Release(from);
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.Error("[nav] 强切 MusicSelect 异常", e);
            }
            return false;
        }

        // ------------------------------------------------------------------ ConfirmPlay 状态机推进

        /// <summary>
        /// ConfirmPlay(刷卡后的「このAimeでプレイを開始しますか？」页) 的推进。
        ///
        /// 实机确认（lead 3 次复现 + 模式序列 9→10）：
        ///  - `SelectInWait(9)` 是列表滑入过场；**`Select(10)` 才是屏幕上等玩家点 YES/NO 的状态**；
        ///  - `WaitResponse(17)` 是玩家按完之后等服务器的状态；
        ///  - 在这个屏上每帧/每 0.5s 狂刷会重置过场 → 所以改成 **边沿触发**：每个 mode 只在刚进入时注入，
        ///    之后每 6 tick（约 1.5~3s）重试一次、最多 4 次；
        ///  - 优先用 `StateEntry` 官方 API：`StartYesNoResponse()` → `SetActiveYesNoButton(...)` →
        ///    `SetYesNoButton(...)` → `EntryMonitor.ResponseYes()`；再辅以 `InputResponse` / 合成按键；
        ///  - `_isConfirmNewUser == true`（服务端把卡当新卡）时**完全不动作**，只告警（避免建新号）。
        /// </summary>
        private static bool AdvanceConfirmPlay(EntryProcess entry, int reportedMode)
        {
            if (IsRealPlayerLoggedIn())
            {
                _entryDone = true;
                _entryStopReason = 0;
                Log.Info("[nav] 检测到已登录成功 → 停止自动按键");
                return false;
            }

            object state = GetCurrentEntryState(entry);
            int mode = reportedMode;
            if (state != null)
            {
                int realMode = GetProcMode(state);
                if (realMode >= 0) mode = realMode;
            }

            if (mode != _confirmPlayLastMode)
            {
                _confirmPlayLastMode = mode;
                _confirmPlayStuckTicks = 0;
                _confirmPlayInjectMode = -1;
                _confirmPlayInjectAttempts = 0;
                LogConfirmPlayDiag(state, mode, "mode 变化");
            }
            else
            {
                _confirmPlayStuckTicks++;
            }

            // 安全：新卡（服务端认为该 Aime 未注册）绝不自动确认，避免创建新账号数据
            bool isNewUser = state != null && NavReflect.ToBool(NavReflect.Get(state, "_isConfirmNewUser"));
            if (isNewUser)
            {
                NavReflect.WarnOnce("confirmplay-newuser",
                    "ConfirmPlay 报告 _isConfirmNewUser=true（服务端把该卡当新卡/或被判 BAN/警告）→ " +
                    "为安全起见不自动按是、不强推状态机；请先修账号数据。诊断: " + DescribeConfirmPlay(state, mode));
                _entryStopReason = 2;
                return false;
            }

            if (state == null)
            {
                // 状态机实例拿不到时退回旧的"按是"逻辑（至少能试）
                if (mode == 10 || mode == 17) return PerformEntryAnswer(entry, 4, mode, true);
                return false;
            }

            if (ShouldInjectConfirmPlay(mode))
            {
                return InjectConfirmPlayResponse(state, mode, "OnUpdate");
            }

            // 还没到等玩家 / 已注入待响应：不动动画，只做卡住救援
            HandleStuckConfirmPlay(state, mode);
            return false;
        }

        /// <summary>边沿触发判定：mode 10 刚进入/每 6 tick 重试最多 4 次；mode 17 只在久卡时兜底 1 次。</summary>
        private static bool ShouldInjectConfirmPlay(int mode)
        {
            if (mode == 10)
            {
                if (_confirmPlayInjectMode != 10) return true;                 // 刚进入 Select
                if (_confirmPlayInjectAttempts >= 4) return false;             // 已试 4 次
                return _confirmPlayStuckTicks - _confirmPlayInjectAtTicks >= 6; // 1.5~3s 后重试
            }
            if (mode == 17)
            {
                // 17 = 已按完、等服务端；只有久卡且从未注入过才兜底（>=25 tick ≈ 6~12s）
                return _confirmPlayInjectAttempts == 0 && _confirmPlayStuckTicks >= 25;
            }
            return false;
        }

        private static void HandleStuckConfirmPlay(object state, int mode)
        {
            if (state == null) return;

            if (_confirmPlayStuckTicks == 10 && !_confirmPlayWaitZeroed)
            {
                _confirmPlayWaitZeroed = true;
                LogConfirmPlayDiag(state, mode, "卡住 10 tick");
                bool ok = ZeroConfirmPlayWaitCounters(state);
                Log.Warn("[nav] ConfirmPlay 卡在 mode=" + mode + ConfirmPlayModeName(mode) +
                         " → 已清零等待计数 (_buttonAnimWait/_agreeStartWait/_warningInfoWait/_timer) ok=" + ok);
                return;
            }

            if (_confirmPlayStuckTicks == 20 && AllowEntryModeForce && !_confirmPlayModeForced)
            {
                _confirmPlayModeForced = true;
                LogConfirmPlayDiag(state, mode, "卡住 20 tick");
                if (mode == 9) // SelectInWait → Select
                {
                    bool ok = SetProcMode(state, 10);
                    Log.Warn("[nav] ConfirmPlay 卡在 SelectInWait(9) → 强制推进 _mode 9→10 (Select) ok=" + ok);
                }
                else
                {
                    Log.Warn("[nav] ConfirmPlay 卡在 mode=" + mode + ConfirmPlayModeName(mode) +
                             " → 不在强推白名单（只处理 9→10）");
                }
                return;
            }

            if (_confirmPlayStuckTicks >= 30 && _entryStopReason < 0)
            {
                _entryStopReason = 1;
                Log.Warn("[nav] ConfirmPlay 卡在 mode=" + mode + ConfirmPlayModeName(mode) +
                         " 已 30 tick → 停手；诊断: " + DescribeConfirmPlay(state, mode));
            }
        }

        /// <summary>
        /// 注入「是」：优先用 `StateEntry` 官方 API（YES/NO 按钮），再辅以 EntryMonitor 响应/InputResponse/合成按键。
        /// 边沿计数在这里更新，避免每帧刷。
        /// </summary>
        private static bool InjectConfirmPlayResponse(object state, int mode, string source)
        {
            if (IsRealPlayerLoggedIn()) return false;
            if (NavReflect.ToBool(NavReflect.Get(state, "_isConfirmNewUser"))) return false;
            if (_entryExecInjections >= MaxEntryAttempts)
            {
                if (_entryStopReason < 0)
                {
                    _entryStopReason = 1;
                    Log.Warn("[nav] ConfirmPlay 注入次数到顶 (" + _entryExecInjections + ") → 停手");
                }
                return false;
            }

            object monitor = NavReflect.Get(state, "_responseMonitor");
            if (monitor == null) monitor = FirstEntryMonitor();
            if (monitor == null) return false;

            bool isNewUser = false;
            int monitorIndex = MonitorIndexOf(monitor);

            // 官方 API 顺序（StateEntry）：StartYesNoResponse → SetActiveYesNoButton → SetYesNoButton
            bool startResponse = TryCallArgs(state, "StartYesNoResponse");
            bool setActive = TryCallArgs(state, "SetActiveYesNoButton", true, isNewUser);
            bool setButton = TryCallArgs(state, "SetYesNoButton", monitorIndex, isNewUser);

            // 再设置响应（不同版本可能用 DecideEntry / ResponseYes / InputResponse 之一）
            bool decide = CallOk(monitor, "DecideEntry", out _);
            bool responseYes = CallOk(monitor, "ResponseYes", out _);
            bool inputResponse = SetInputResponse(monitor, 1);

            bool synthetic = false;
            if (UseSyntheticButtons)
            {
                string button = (_confirmPlayInjectAttempts % 2 == 0) ? ConfirmButtonName : ConfirmButtonNameAlt;
                synthetic = PressSyntheticButton(monitorIndex, button);
            }

            _confirmPlayInjectMode = mode;
            _confirmPlayInjectAtTicks = _confirmPlayStuckTicks;
            _confirmPlayInjectAttempts++;
            _lastExecInjectAt = Time.realtimeSinceStartup;
            _entryExecInjections++;

            if (_entryExecInjections <= 8 || _confirmPlayInjectAttempts == 4)
            {
                Log.Info("[nav] ConfirmPlay 注入「是」(" + source + "): mode=" + mode + ConfirmPlayModeName(mode) +
                         " monitor=" + monitorIndex +
                         " official(start=" + startResponse + ",active=" + setActive + ",btn=" + setButton + ")" +
                         " decide=" + decide + " responseYes=" + responseYes + " inputResponse=" + inputResponse +
                         " synthetic=" + (synthetic ? "ok" : "fail") + " try=" + _confirmPlayInjectAttempts);
            }
            return startResponse || setActive || setButton || decide || responseYes || inputResponse || synthetic;
        }

        private static bool TryCallArgs(object target, string method, params object[] args)
        {
            string error;
            NavReflect.TryCall(target, method, args, out error);
            return error == null;
        }

        private static int MonitorIndexOf(object monitor)
        {
            try
            {
                var entry = NavProcess.Find<EntryProcess>();
                if (entry == null) return 0;
                var monitors = NavReflect.Get(entry, "_monitors") as IList;
                if (monitors == null) return 0;
                for (int i = 0; i < monitors.Count && i < 2; i++)
                    if (ReferenceEquals(monitors[i], monitor)) return i;
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>
        /// `ConfirmPlay.Exec` 的 Prefix：在状态机读响应**之前**注入（时机比 OnUpdate Postfix 更准）。
        /// 只在 mode 10(Select) / 17(WaitResponse 兜底) 且满足边沿触发条件时注入；过场 mode 一律不碰。
        /// </summary>
        internal static void OnConfirmPlayExecPrefix(Process.Entry.State.ConfirmPlay state)
        {
            try
            {
                if (!AutoAdvanceEntry || state == null) return;
                if (_entryStopReason >= 0) return;
                if (_postLoginGiveUp) return;
                if (_entryExecInjections >= MaxEntryAttempts) return;
                if (!IsEntryAutomationAllowed()) return;   // 用户自己玩时绝不注入

                int mode = GetProcMode(state);
                if (mode != 10 && mode != 17) return;
                if (IsRealPlayerLoggedIn()) return;
                if (NavReflect.ToBool(NavReflect.Get(state, "_isConfirmNewUser"))) return;
                if (!ShouldInjectConfirmPlay(mode)) return;
                if (Time.realtimeSinceStartup - _lastExecInjectAt < 0.2) return;

                InjectConfirmPlayResponse(state, mode, "Exec");
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("confirmplay-exec", "ConfirmPlay.Exec 注入失败: " + e.Message);
            }
        }

        private static object FirstEntryMonitor()
        {
            var entry = NavProcess.Find<EntryProcess>();
            if (entry == null) return null;
            var monitors = NavReflect.Get(entry, "_monitors") as IList;
            if (monitors == null) return null;
            for (int i = 0; i < monitors.Count && i < 2; i++)
                if (monitors[i] != null) return monitors[i];
            return null;
        }

        /// <summary>通过 Context 拿当前 Entry 状态机实例（ConfirmEntry/ConfirmPlay/…）。</summary>
        internal static object GetCurrentEntryState(EntryProcess entry)
        {
            if (entry == null) return null;
            try
            {
                var context = NavReflect.Get(entry, "_context");
                if (context == null) return null;

                // 首选：Context._current（私有字段，最稳；省掉 out 参数类型问题）
                var current = NavReflect.Get(context, "_current");
                if (current != null) return current;

                // 回退：Context.GetCurrentState(out StateType key, out State state)
                // 注意第一个 out 是枚举 StateType，**不是 int**；byref 值类型必须传对应类型的装箱值。
                var method = NavReflect.FindMethod(context.GetType(), "GetCurrentState", 2);
                if (method == null) return null;
                var parameters = method.GetParameters();
                var args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                    args[i] = BoxedDefault(parameters[i].ParameterType);
                method.Invoke(context, args);
                for (int i = 0; i < parameters.Length; i++)
                {
                    var t = parameters[i].ParameterType;
                    if (t.IsByRef) t = t.GetElementType();
                    if (t != null && typeof(Process.Entry.State.State).IsAssignableFrom(t)) return args[i];
                }
                return args.Length > 1 ? args[1] : null;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("entry-current-state", "读取当前 Entry 状态机失败: " + e.Message);
                return null;
            }
        }

        /// <summary>byref/out 参数的默认占位值：值类型给对应类型的装箱零值，引用类型给 null。</summary>
        private static object BoxedDefault(System.Type parameterType)
        {
            var t = parameterType != null && parameterType.IsByRef ? parameterType.GetElementType() : parameterType;
            if (t == null || !t.IsValueType) return null;
            try { return Activator.CreateInstance(t); }
            catch (Exception) { return null; }
        }

        /// <summary>读 `_mode`（ProcEnum&lt;T&gt;）里的 Mode（不同 StateType 是不同枚举，只当 int 用）。</summary>
        internal static int GetProcMode(object state)
        {
            var procEnum = NavReflect.Get(state, "_mode");
            if (procEnum == null) return -1;
            var prop = NavReflect.FindProperty(procEnum.GetType(), "Mode");
            if (prop == null || !prop.CanRead) return -1;
            try { return Convert.ToInt32(prop.GetValue(procEnum, null)); }
            catch (Exception) { return -1; }
        }

        /// <summary>写 `_mode.Mode`（ProcEnum&lt;T&gt; 的 setter 会触发状态机的 _onSet 回调）。</summary>
        internal static bool SetProcMode(object state, int mode)
        {
            var procEnum = NavReflect.Get(state, "_mode");
            if (procEnum == null) return false;
            var prop = NavReflect.FindProperty(procEnum.GetType(), "Mode");
            if (prop == null || !prop.CanWrite) return false;
            try
            {
                prop.SetValue(procEnum, Enum.ToObject(prop.PropertyType, mode), null);
                return true;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("set-procmode", "写 _mode 失败: " + e.Message);
                return false;
            }
        }

        private static bool ZeroConfirmPlayWaitCounters(object state)
        {
            bool ok = false;
            ok |= NavReflect.Set(state, "_buttonAnimWait", 0);
            ok |= NavReflect.Set(state, "_agreeStartWait", 0);
            ok |= NavReflect.Set(state, "_warningInfoWait", 0);
            ok |= NavReflect.Set(state, "_timer", 0);
            return ok;
        }

        private static void LogConfirmPlayDiag(object state, int mode, string why)
        {
            string diag = DescribeConfirmPlay(state, mode);
            if (diag == _confirmPlayLastDiag) return;
            _confirmPlayLastDiag = diag;
            Log.Info("[nav] ConfirmPlay 诊断(" + why + "): " + diag);
        }

        private static string DescribeConfirmPlay(object state, int mode)
        {
            var sb = new StringBuilder();
            sb.Append("mode=").Append(mode).Append(ConfirmPlayModeName(mode));
            if (state == null)
            {
                sb.Append(" state=null");
                return sb.ToString();
            }
            AppendFlag(sb, state, "_isConfirmNewUser");
            AppendFlag(sb, state, "_isBAN");
            AppendFlag(sb, state, "_isWarning");
            AppendFlag(sb, state, "_isWarningOrBAN");
            AppendFlag(sb, state, "_isEventModeInvalid");
            AppendFlag(sb, state, "_isVersionTooHigh");
            AppendFlag(sb, state, "_isInherit");
            AppendFlag(sb, state, "_isConvert");
            AppendInt(sb, state, "_buttonAnimWait");
            AppendInt(sb, state, "_agreeStartWait");
            AppendInt(sb, state, "_warningInfoWait");
            AppendInt(sb, state, "_timer");
            AppendInt(sb, state, "_aimeId");
            object monitor = NavReflect.Get(state, "_responseMonitor");
            sb.Append(" _responseMonitor=").Append(monitor == null ? "null" : NavReflect.Name(monitor));
            return sb.ToString();
        }

        private static void AppendFlag(StringBuilder sb, object target, string field)
        {
            sb.Append(' ').Append(field).Append('=')
              .Append(NavReflect.ToBool(NavReflect.Get(target, field)));
        }

        private static void AppendInt(StringBuilder sb, object target, string field)
        {
            sb.Append(' ').Append(field).Append('=')
              .Append(NavReflect.ToInt(NavReflect.Get(target, field)));
        }

        /// <summary>
        /// 对当前 Entry 确认页执行「是」/「否」。三条路径全部尝试，日志里逐项报告哪个生效：
        ///  1) EntryMonitor.DecideEntry()  —— 游戏自己的"决定"入口；
        ///  2) EntryMonitor.ResponseYes()/ResponseNo() —— 设置 InputResponse（YES/NO 触摸按钮的语义等价物）；
        ///  3) 直接写 InputResponse 属性/backing field + InputManager.SetButtonDown 合成按键。
        /// </summary>
        private static bool PerformEntryAnswer(EntryProcess entry, int state, int mode, bool wantYes)
        {
            if (_entryConfirmCount >= MaxEntryAttempts)
            {
                if (_entryStopReason < 0)
                {
                    _entryStopReason = 1;
                    Log.Warn("[nav] Entry 确认页已尝试 " + _entryConfirmCount +
                             " 次仍未前进 → 停手（state=" + state + ", mode=" + mode + "/" +
                             ModeName(state, mode) + "），等人工或 flow 超时处理");
                }
                return false;
            }
            var monitors = NavReflect.Get(entry, "_monitors") as IList;
            if (monitors == null)
            {
                NavReflect.WarnOnce("entry-monitors", "EntryProcess._monitors 不可用");
                return false;
            }

            bool anyDecide = false, anyResponse = false, anyInputResponse = false, anySynthetic = false;
            bool anyOfficial = false;
            string buttonName = (_entryConfirmCount % 2 == 0) ? ConfirmButtonName : ConfirmButtonNameAlt;

            // 官方 StateEntry YES/NO API（ConfirmNewUser / ConfirmPlay 这类 YES-NO 屏都继承自 StateEntry）：
            // StartYesNoResponse() 让状态机进入"等响应"，SetActiveYesNoButton/SetYesNoButton 绑定按钮。
            object stateObj = GetCurrentEntryState(entry);
            bool isNewAime = stateObj != null && NavReflect.ToBool(NavReflect.Get(stateObj, "_isConfirmNewUser"));
            if (stateObj != null)
            {
                if (TryCallArgs(stateObj, "StartYesNoResponse")) anyOfficial = true;
                if (TryCallArgs(stateObj, "SetActiveYesNoButton", true, isNewAime)) anyOfficial = true;
            }

            for (int i = 0; i < monitors.Count && i < 2; i++)
            {
                var monitor = monitors[i];
                if (monitor == null) continue;

                if (stateObj != null)
                {
                    if (TryCallArgs(stateObj, "SetYesNoButton", i, isNewAime)) anyOfficial = true;
                }

                string err;
                if (wantYes)
                {
                    if (CallOk(monitor, "DecideEntry", out err)) anyDecide = true;
                    if (CallOk(monitor, "ResponseYes", out err)) anyResponse = true;
                }
                else
                {
                    // 否：只设置响应，不调 DecideEntry（DecideEntry 语义是"是"）
                    if (CallOk(monitor, "ResponseNo", out err)) anyResponse = true;
                }

                if (SetInputResponse(monitor, wantYes ? 1 : 2)) anyInputResponse = true;

                if (UseSyntheticButtons && PressSyntheticButton(i, buttonName)) anySynthetic = true;
            }

            bool acted = anyDecide || anyResponse || anyInputResponse || anySynthetic || anyOfficial;
            if (!acted) return false;

            _entryConfirmCount++;
            // 日志节流：前 5 次 + 之后每 5 次打一条，避免刷屏（lead Bug C：之前 0.6s 刷 200+ 次）
            bool shouldLog = _entryConfirmCount <= 5 || (_entryConfirmCount % 5 == 0) ||
                             state == 3; // 建号页每按一次都要留痕
            if (!shouldLog) return true;

            if (wantYes)
            {
                Log.Info("[nav] Entry 确认页 -> 按「是」(decide=" + anyDecide + ", btn=" + buttonName +
                         ", synthetic=" + (anySynthetic ? "ok" : "fail") +
                         ", responseYes=" + anyResponse + ", inputResponse=" + anyInputResponse +
                         ", officialYesNo=" + anyOfficial + ", isNewAime=" + isNewAime +
                         ", state=" + state + ", mode=" + mode + ModeName(state, mode) +
                         ", try=" + _entryConfirmCount + ")");
            }
            else
            {
                Log.Info("[nav] Entry 确认页 -> 按「否」(responseNo=" + anyResponse +
                         ", inputResponse=" + anyInputResponse + ", officialYesNo=" + anyOfficial +
                         ", state=" + state + ", try=" + _entryConfirmCount + ")");
            }
            return true;
        }

        /// <summary>
        /// UserData 里是否已经登录了真实账号（非游客、UserID 非 0）。
        /// 用于 Entry 自动化"登录成功后立刻停手"（lead Bug C）。
        /// </summary>
        private static bool IsRealPlayerLoggedIn()
        {
            try
            {
                var manager = Singleton<UserDataManager>.Instance;
                if (manager == null) return false;
                for (int i = 0; i < 2; i++)
                {
                    var userData = manager.GetUserData(i);
                    if (userData == null) continue;
                    if (!userData.IsEntry) continue;
                    if (userData.IsGuest()) continue;
                    var detail = userData.Detail;
                    if (detail != null && detail.UserID != 0) return true;
                }
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("entry-loggedin", "检测登录状态失败: " + e.Message);
            }
            return false;
        }

        private static bool CallOk(object target, string method, out string error)
        {
            NavReflect.TryCall(target, method, null, out error);
            return error == null;
        }

        /// <summary>
        /// 直接设置 EntryMonitor.InputResponse（`Response.Yes=1 / No=2`）。
        /// 这是 YES/NO 触摸按钮在游戏内部的等价效果；属性 setter 是 private，所以走 backing field。
        /// </summary>
        private static bool SetInputResponse(object monitor, int value)
        {
            if (monitor == null) return false;
            var type = monitor.GetType();
            try
            {
                // 自动属性 backing field
                var backing = NavReflect.FindField(type, "<InputResponse>k__BackingField");
                if (backing != null)
                {
                    backing.SetValue(monitor, Enum.ToObject(backing.FieldType, value));
                    return true;
                }
                var prop = NavReflect.FindProperty(type, "InputResponse");
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(monitor, Enum.ToObject(prop.PropertyType, value), null);
                    return true;
                }
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("entry-inputresponse", "写 InputResponse 失败: " + e.Message);
            }
            return false;
        }

        /// <summary>一次性把 Entry 相关类型的候选方法名打进日志（实机定位第三条路径用）。</summary>
        private static void DumpEntryCandidates()
        {
            if (_entryCandidatesLogged) return;
            _entryCandidatesLogged = true;
            try
            {
                Log.Info("[nav] Entry 候选方法: EntryMonitor=[" +
                         MethodNames(typeof(Monitor.Entry.EntryMonitor), 50) + "]");
                Log.Info("[nav] Entry 候选方法: EntryProcess=[" + MethodNames(typeof(EntryProcess), 40) + "]");
                Log.Info("[nav] Entry 候选方法: ConfirmEntry=[" +
                         MethodNames(typeof(Process.Entry.State.ConfirmEntry), 40) + "]");
                Log.Info("[nav] Entry 候选方法: StateEntry=[" +
                         MethodNames(typeof(Process.Entry.State.StateEntry), 40) + "]");
                Log.Info("[nav] Entry 候选方法: ContextEntry=[" +
                         MethodNames(typeof(Process.Entry.State.ContextEntry), 20) + "]");
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("entry-candidates", "dump Entry 候选方法失败: " + e.Message);
            }
        }

        private static string MethodNames(System.Type type, int cap)
        {
            var sb = new StringBuilder();
            try
            {
                var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                              BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                int n = 0;
                for (int i = 0; i < methods.Length && n < cap; i++)
                {
                    var m = methods[i];
                    if (m.IsSpecialName) continue;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(m.Name).Append('/').Append(m.GetParameters().Length);
                    n++;
                }
            }
            catch (Exception) { }
            return sb.ToString();
        }

        /// <summary>
        /// 按 StateType 选对应的 mode 名字（不同 StateType 的 mode 是**不同枚举**，不能混用）：
        ///  - state==1 ConfirmEntry → ConfirmEntry.ProcMode
        ///  - state==4 ConfirmPlay  → ConfirmPlay.ProcMode
        ///  - 其它 → 不打名字（避免误导，例如 ConfirmPlay 的 mode=9 是 SelectInWait，不是 NewFelica…）
        /// </summary>
        private static string ModeName(int state, int mode)
        {
            string name = null;
            if (state == 1) name = ProcModeName(mode);
            else if (state == 4) name = ConfirmPlayModeName(mode);
            return string.IsNullOrEmpty(name) ? "" : "/" + name;
        }

        /// <summary>
        /// ConfirmEntry 阶段允许自动按「是」的 ProcMode：**只允许 Request(1)**。
        ///
        /// 实机事故（lead 复现，用户自己刷卡）：
        ///  - `Reading(2)` = 正在读卡/校验 → 这里按键会打断读卡，把用户踢进 ConfirmGuest(游客)流程；
        ///  - `Suspend(3)` = 挂起/等待中 → 也不该按；
        ///  - `ResultNewAime(4)` 及以上是 NewAime/NewFelica/迁移/二维码/Error → 更不能按。
        /// 只有 `Request(1)`（等玩家确认"用这张卡开始吗"）才是安全的。
        /// </summary>
        private static bool IsSafeConfirmEntryMode(int mode)
        {
            return mode == 1;
        }

        /// <summary>ConfirmPlay.ProcMode 名字（日志用）。</summary>
        private static string ConfirmPlayModeName(int mode)
        {
            switch (mode)
            {
                case 0: return "(Setup)";
                case 1: return "(GetUserPreview)";
                case 2: return "(Suspend)";
                case 3: return "(PreSelectInit)";
                case 4: return "(PreSelectIn)";
                case 5: return "(PreSelectInWait)";
                case 6: return "(PreSelectOut)";
                case 7: return "(PreSelectOutWait)";
                case 8: return "(SelectIn)";
                case 9: return "(SelectInWait)";
                case 10: return "(Select)";
                case 11: return "(SelectClose)";
                case 12: return "(SelectCloseWait)";
                case 13: return "(Agree01Open)";
                case 14: return "(Agree01OpenWait)";
                case 15: return "(Agree01)";
                case 16: return "(Error)";
                case 17: return "(WaitResponse)";
                default: return "(?)";
            }
        }

        /// <summary>ConfirmEntry.ProcMode 名字（日志用）。</summary>
        private static string ProcModeName(int mode)
        {
            switch (mode)
            {
                case 0: return "(Setup)";
                case 1: return "(Request)";
                case 2: return "(Reading)";
                case 3: return "(Suspend)";
                case 4: return "(ResultNewAime)";
                case 5: return "(ResultNewFelica)";
                case 6: return "(NewFelicaPolling)";
                case 7: return "(NewFelicaWaitNotice)";
                case 8: return "(NewFelicaWaitConfirmSite)";
                case 9: return "(NewFelicaNoticeDataMigration)";
                case 10: return "(NewFelicaWaitDispQR)";
                case 11: return "(NewFelicaDone)";
                case 12: return "(NewAimeDispFelicaSiteAndQR)";
                case 13: return "(Error)";
                default: return "(?)";
            }
        }

        /// <summary>
        /// 合成一次按键（InputManager.SetButtonDown）。只在 Entry 确认页这类
        /// 「ボタンをおしてください」界面用；若 InputLock 正在吞输入，会临时放行再恢复。
        /// </summary>
        internal static bool PressSyntheticButton(int monitor, string buttonName)
        {
            try
            {
                var button = ParseButton(buttonName, out bool parsed);
                if (!parsed)
                {
                    NavReflect.WarnOnce("button-name", "未知按键名: " + buttonName + "（用 Button01..Button08）");
                    return false;
                }

                bool restoreAllow = BeginSyntheticInputWindow();
                try
                {
                    Manager.InputManager.SetButtonDown(monitor, button);
                }
                finally
                {
                    if (restoreAllow) EndSyntheticInputWindow();
                }
                return true;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("synthetic-button", "合成按键失败: " + e.Message);
                return false;
            }
        }

        private static Manager.InputManager.ButtonSetting ParseButton(string name, out bool ok)
        {
            ok = true;
            switch ((name ?? "").Trim().ToUpperInvariant())
            {
                case "BUTTON01": case "1": return Manager.InputManager.ButtonSetting.Button01;
                case "BUTTON02": case "2": return Manager.InputManager.ButtonSetting.Button02;
                case "BUTTON03": case "3": return Manager.InputManager.ButtonSetting.Button03;
                case "BUTTON04": case "4": return Manager.InputManager.ButtonSetting.Button04;
                case "BUTTON05": case "5": return Manager.InputManager.ButtonSetting.Button05;
                case "BUTTON06": case "6": return Manager.InputManager.ButtonSetting.Button06;
                case "BUTTON07": case "7": return Manager.InputManager.ButtonSetting.Button07;
                case "BUTTON08": case "8": return Manager.InputManager.ButtonSetting.Button08;
            }
            ok = false;
            return Manager.InputManager.ButtonSetting.Button04;
        }

        /// <summary>让 InputLock 暂时不吞合成输入；返回 true 表示需要调用方恢复。</summary>
        private static bool BeginSyntheticInputWindow()
        {
            try
            {
                var field = typeof(InputLock).GetField("AllowSyntheticInput", BindingFlags.Public | BindingFlags.Static);
                if (field == null || field.FieldType != typeof(bool)) return false;
                bool previous = (bool)field.GetValue(null);
                field.SetValue(null, true);
                return !previous;
            }
            catch (Exception) { return false; }
        }

        private static void EndSyntheticInputWindow()
        {
            try
            {
                var field = typeof(InputLock).GetField("AllowSyntheticInput", BindingFlags.Public | BindingFlags.Static);
                if (field != null && field.FieldType == typeof(bool)) field.SetValue(null, false);
            }
            catch (Exception) { }
        }

        // ============================================================================================
        //  2) 选曲 + 选难度
        // ============================================================================================

        /// <summary>
        /// 在选歌页按 musicId+level 选中曲目。幂等；返回 true 表示已经选中（可以 StartCurrentSelection）。
        /// 注意：会同时把 GameManager.SelectMusicID/SelectDifficultyID 写好，因为游戏自己的
        /// MusicSelectProcess.GameStart 读的是 GameManager（见 AquaMai FixMissingDifficultyCrash）。
        /// </summary>
        public static bool SelectChart(int musicId, int level)
        {
            if (NavProcess.Any<TrackStartProcess>() || NavProcess.Any<GameProcess>()) return true;   // 已经在游玩/过场
            if (musicId <= 0) { NavReflect.WarnOnce("select-badid", "SelectChart: musicId=" + musicId + " 非法"); return false; }
            if (level < 0) level = 0;
            if (level > 4) level = 4;

            var msp = NavProcess.Find<MusicSelectProcess>();
            if (msp == null) return false;

            int player = SafePlayerIndex(msp);

            // 幂等：同一个进程实例上已经选过同一首
            if (_selProcessId == msp.ProcessId && _selMusicId == musicId && _selLevel == level)
                return true;

            // 难度：数组 + GameManager 两边都写（缺一个都可能让 GameStart 用错难度）
            SetPlayerDifficulty(msp, player, level);

            int scoreKind = musicId >= 10000 ? 1 : 0; // 10000+ = DX 谱
            int category = -1, index = -1;
            if (!LocateChart(msp, musicId, level, ref category, ref index, ref scoreKind))
            {
                LogSelectDiagnostics(msp, musicId, level);
                return false;
            }

            try
            {
                msp.CurrentCategorySelect = category;
                msp.CurrentMusicSelect = index;
                _selCategory = category;                 _selIndex = index;                 if (DriveMusicSubSequence(msp, player)) return false;   // 先进歌单/难度再交给 StartCurrentSelection
                msp.ScoreType = (MAI2System.ConstParameter.ScoreKind)scoreKind;
                msp.ClampMusicIndex();
            }
            catch (Exception e)
            {
                Log.Error("[nav] 写 CurrentMusicSelect 失败", e);
                return false;
            }

            ApplyGameManagerSelection(player, musicId, level, scoreKind);
                    // 分类排序（难度分类/版本分类）会让 CurrentCategorySelect 的下标含义变化；
                    // 用游戏自己的「按 musicId 定位」再跳一次，和分类排序无关。
                    try
                    {
                        string posErr;
                        object pos = NavReflect.TryCall(msp, "SetSortIndexToMaiList",
                            new object[] { musicId, level, false, true }, out posErr);
                        bool jumped = posErr == null && NavReflect.ToBool(pos);
                        Log.Info("[nav] 选歌定位 SetSortIndexToMaiList(" + musicId + "-" + level + ") ok=" + jumped +
                                 (posErr == null ? "" : " err=" + posErr));
                    }
                    catch (Exception e) { NavReflect.WarnOnce("setsortidx", "SetSortIndexToMaiList 失败: " + e.Message); }

            _selProcessId = msp.ProcessId;
            _subPressAt = -100;   // 新选歌页：重置子状态按键节流
            _selMusicId = musicId;
            _selLevel = level;
            _selScoreKind = scoreKind;
            Log.Info("[nav] 选中 " + musicId + "-" + level + " (cat=" + category + " idx=" + index + " kind=" + scoreKind + ")");
            return true;
        }

        /// <summary>
        /// 选歌的真实流程：分类 → 歌曲 → 难度 → 设置/最后确认 → 进入。
        /// 之前只处理了前三个，少了设置/确认页，所以直接 GameStart 会跳过界面甚至触发崩溃。
        /// 这里每个子状态都按一次真实 4 号键（決定），让游戏自己逐屏走完。
        /// </summary>
        private static bool DriveMusicSubSequence(MusicSelectProcess msp, int player)
        {
            try
            {
                var arr = NavReflect.Get(msp, "_currentPlayerSubSequence") as Array;
                if (arr == null || player < 0 || player >= arr.Length) return false;
                string sub = "";
                try { sub = arr.GetValue(player).ToString(); } catch (Exception) { return false; }

                bool advancable = sub == "Genre" || sub == "Music" || sub == "Difficulty" ||
                                  sub == "Menu" || sub == "Option" || sub == "Character" ||
                                  sub == "SortSetting";
                if (sub != _musicSubState)
                {
                    _musicSubState = sub;
                    _musicSubStateAt = Time.realtimeSinceStartup;
                }
                if (!advancable) return false;
                if (HoldForUser("music:" + player + ":" + sub, ModConfig.Data.UserHoldSec)) return true;

                // 各屏进入前把选择数据写好
                if (sub == "Genre")
                {
                    try { if (_selCategory >= 0) msp.CurrentCategorySelect = _selCategory; } catch (Exception) { }
                }
                else if (sub == "Music")
                {
                    try { if (_selIndex >= 0) msp.CurrentMusicSelect = _selIndex; } catch (Exception) { }
                }

                // 进选歌页后先等游戏把分类/标签数据初始化完（~2s），过早按 4 号键会在
                // MusicSelectMonitor.OnStartMusicSelect → getTabString 里 NRE 崩溃（2026-11-20 02:03 实机）。
                double __msElapsed = MusicSelectElapsed;
                if (__msElapsed >= 0 && __msElapsed < 2.0)
                {
                    NavReflect.WarnOnce("ms-init-wait", "选歌页初始化等待 " + __msElapsed.ToString("0.0") + "s（防止 getTabString NRE）");
                    return true;
                }
                if (Time.realtimeSinceStartup - _subPressAt >= 0.6)
                {
                    _subPressAt = Time.realtimeSinceStartup;
                    InputInject.Press(0, Manager.InputManager.ButtonSetting.Button04);
                    Log.Info("[nav] 选歌子状态: " + sub + " → 4 号键(決定)");
                }
                return true;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("subseq", "选歌子状态推进失败: " + e.Message);
                return false;
            }
        }


        private static bool CallSubNext(MusicSelectProcess msp, int player, System.Type subType, string name)
        {
            try
            {
                object val = Enum.Parse(subType, name);
                string err;
                object r1 = NavReflect.TryCall(msp, "Next", new object[] { player, val }, out err);
                if (err == null) return true;
                object r2 = NavReflect.TryCall(msp, "SyncNext", new object[] { val }, out err);
                if (err == null) return true;
            }
            catch (Exception e) { NavReflect.WarnOnce("subnext2:" + name, "[nav] 子状态推进异常: " + e.Message); }
            return false;
        }

        private static void LogSelectDiagnostics(MusicSelectProcess msp, int musicId, int level)
        {
            string key = msp.ProcessId + ":" + musicId + ":" + level;
            if (_selectDiagKey == key) return;
            _selectDiagKey = key;
            try
            {
                var lists = msp.CombineMusicDataList;
                int categories = lists == null ? -1 : lists.Count;
                int totalEntries = 0;
                int foundAnyLevel = -1;
                int foundCategory = -1, foundIndex = -1;
                if (lists != null)
                {
                    for (int c = 0; c < lists.Count; c++)
                    {
                        var column = lists[c];
                        if (column == null) continue;
                        for (int i = 0; i < column.Count; i++)
                        {
                            var entry = column[i];
                            if (entry == null) continue;
                            totalEntries++;
                            var data = entry.musicSelectData;
                            if (data == null) continue;
                            for (int sk = 0; sk < data.Count && sk < 2; sk++)
                            {
                                var msd = data[sk];
                                if (msd == null) continue;
                                int id;
                                try { id = entry.GetID((MAI2System.ConstParameter.ScoreKind)sk); }
                                catch (Exception) { continue; }
                                if (id != musicId) continue;
                                if (foundAnyLevel < 0)
                                {
                                    foundAnyLevel = (int)msd.Difficulty;
                                    foundCategory = c;
                                    foundIndex = i;
                                }
                            }
                        }
                    }
                }
                int curCat = -1, curMusic = -1;
                try { curCat = msp.CurrentCategorySelect; } catch (Exception) { }
                try { curMusic = msp.CurrentMusicSelect; } catch (Exception) { }
                Log.Warn("[nav] SelectChart 定位失败: music=" + musicId + "-" + level +
                         ", proc=" + msp.ProcessId + ", categories=" + categories +
                         ", totalEntries=" + totalEntries +
                         ", 该曲存在=" + (foundAnyLevel >= 0) +
                         (foundAnyLevel >= 0 ? " (难度=" + foundAnyLevel + ", cat=" + foundCategory + ", idx=" + foundIndex + ")" : "") +
                         ", CurrentCategorySelect=" + curCat + ", CurrentMusicSelect=" + curMusic +
                         ", stack=[" + NavProcess.StackNames() + "]");
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("select-diag", "[nav] SelectChart 诊断失败: " + e.Message);
            }
        }

        private static void SetPlayerDifficulty(MusicSelectProcess msp, int player, int level)
        {
            try
            {
                var diffIndex = msp.DifficultySelectIndex;
                if (diffIndex != null && player >= 0 && player < diffIndex.Length) diffIndex[player] = level;
            }
            catch (Exception e) { NavReflect.WarnOnce("diffindex", "DifficultySelectIndex 写入失败: " + e.Message); }
            try
            {
                var cur = msp.CurrentDifficulty;
                if (cur != null && player >= 0 && player < cur.Length) cur[player] = (MusicDifficultyID)level;
            }
            catch (Exception e) { NavReflect.WarnOnce("curdiff", "CurrentDifficulty 写入失败: " + e.Message); }
        }

        private static bool LocateChart(MusicSelectProcess msp, int musicId, int level,
            ref int category, ref int index, ref int scoreKind)
        {
            List<ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData>> lists;
            try { lists = msp.CombineMusicDataList; }
            catch (Exception e)
            {
                NavReflect.WarnOnce("combine-list", "CombineMusicDataList 读取失败: " + e.Message);
                return false;
            }
            if (lists == null) return false;

            int fallbackCategory = -1, fallbackIndex = -1, fallbackKind = -1;

            for (int c = 0; c < lists.Count; c++)
            {
                var column = lists[c];
                if (column == null) continue;
                for (int i = 0; i < column.Count; i++)
                {
                    var entry = column[i];
                    if (entry == null) continue;
                    var data = entry.musicSelectData;
                    if (data == null) continue;
                    for (int sk = 0; sk < data.Count && sk < 2; sk++)
                    {
                        var msd = data[sk];
                        if (msd == null) continue;
                        int id;
                        try { id = entry.GetID((MAI2System.ConstParameter.ScoreKind)sk); }
                        catch (Exception) { continue; }
                        if (id != musicId) continue;

                        if (msd.Difficulty == level)
                        {
                            category = c; index = i; scoreKind = sk;
                            return true;
                        }
                        if (fallbackIndex < 0)
                        {
                            fallbackCategory = c; fallbackIndex = i; fallbackKind = sk;
                        }
                    }
                }
            }

            if (fallbackIndex >= 0)
            {
                // 普通排序下条目不带难度，难度是独立选择的
                category = fallbackCategory; index = fallbackIndex; scoreKind = fallbackKind;
                return true;
            }
            return false;
        }

        private static void ApplyGameManagerSelection(int player, int musicId, int level, int scoreKind)
        {
            try
            {
                var ids = Manager.GameManager.SelectMusicID;
                if (ids != null && player >= 0 && player < ids.Length) ids[player] = musicId;

                var diffs = Manager.GameManager.SelectDifficultyID;
                if (diffs != null && player >= 0 && player < diffs.Length) diffs[player] = level;

                var ghosts = Manager.GameManager.SelectGhostID;
                if (ghosts != null && player >= 0 && player < ghosts.Length)
                    ghosts[player] = GhostManager.GhostTarget.End;

                Manager.GameManager.SelectedDeleteGhostID = GhostManager.GhostTarget.End;
                Manager.GameManager.SelectScoreType = scoreKind;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("gamemanager-select", "写 GameManager 选择状态失败: " + e.Message);
            }
        }

        private static int SafePlayerIndex(MusicSelectProcess msp)
        {
            int player = 0;
            try { player = msp.SortDecidePlayer; }
            catch (Exception) { }
            if (player < 0) player = 0;
            if (player > 1) player = 1;
            return player;
        }

        // ============================================================================================
        //  3) 确认进入当前选曲
        // ============================================================================================

        /// <summary>
        /// 逐屏推进选歌流程：分类 → 歌曲 → 难度 → 设置/最后确认 → 进入。
        /// 每屏用真实 4 号键（決定），游戏自己处理过场；只有所有界面都过了还进不去，才用 GameStart 兜底。
        /// 仍然只有真的到 TrackStart/GamePlay 才返回 true。
        /// </summary>
        public static bool StartCurrentSelection()
        {
            if (NavProcess.Any<TrackStartProcess>() || NavProcess.Any<GameProcess>()) return true;
            GameStage st = GameStagePump.Stage;
            if (st == GameStage.TrackStart || st == GameStage.GamePlay) return true;

            var msp = NavProcess.Find<MusicSelectProcess>();
            if (msp == null) return false;

            double now = Time.realtimeSinceStartup;
            if (_startProcessId != msp.ProcessId)
            {
                _startProcessId = msp.ProcessId;
                _startFallbackSent = false;
                _startAt = now;
                _startAttempts = 0;
            }

            // 还有可推进的界面（分类/歌曲/难度/设置/确认）就逐屏按 4 号键
            int player = SafePlayerIndex(msp);
            if (DriveMusicSubSequence(msp, player)) return false;

            // 界面都走完了：等游戏自己进；5s 还没进才直接 GameStart 兜底
            if (!_startFallbackSent && now - _startAt >= 5.0)
            {
                _startFallbackSent = true;
                PrepareGameManagerBeforeStart(msp);
                string error;
                NavReflect.TryCall(msp, "GameStart", null, out error);
                Log.Warn("[nav] 界面走完 5s 仍未进入 → 直接 GameStart 兜底 err=" + (error ?? "none"));
                return false;
            }

            if (now - _startAt >= 12.0)
            {
                NavReflect.WarnOnce("start-giveup:" + msp.ProcessId,
                    "[nav] 选歌流程 12s 仍未进入游玩，重置重试");
                _startProcessId = 0;
            }
            return false;
        }


        private static void PrepareGameManagerBeforeStart(MusicSelectProcess msp)
        {
            int player = SafePlayerIndex(msp);
            int musicId = _selMusicId;
            int level = _selLevel;

            if (musicId <= 0)
            {
                try
                {
                    var ids = Manager.GameManager.SelectMusicID;
                    if (ids != null) musicId = ids[player];
                    var diffs = Manager.GameManager.SelectDifficultyID;
                    if (diffs != null) level = diffs[player];
                }
                catch (Exception) { }
            }
            if (musicId <= 0)
            {
                NavReflect.WarnOnce("start-nomusic", "StartCurrentSelection: 还没有选中任何曲目");
                return;
            }
            if (level < 0) level = 0;

            int scoreKind = (_selMusicId == musicId) ? _selScoreKind : (musicId >= 10000 ? 1 : 0);
            ApplyGameManagerSelection(player, musicId, level, scoreKind);

            try { Manager.GameManager.IsNormalMode = true; } catch (Exception) { }
            try { Manager.GameManager.SetMaxTrack(); }
            catch (Exception e) { NavReflect.WarnOnce("setmaxtrack2", "SetMaxTrack 失败: " + e.Message); }

            ApplyLongMusicFlag(musicId);

            // 这些“准备完成”标志在直接调 GameStart 时可能被检查，先置位。
            try { msp.PrepareFinish = true; } catch (Exception) { }
            try
            {
                var prep = msp.IsPreparationCompletes;
                if (prep != null && player >= 0 && player < prep.Length) prep[player] = true;
            }
            catch (Exception) { }

            _selMusicId = musicId;
            _selLevel = level;
            _selScoreKind = scoreKind;
        }

        private static void ApplyLongMusicFlag(int musicId)
        {
            // 1.65 的 GameManager.IsLongMusic 是**静态属性**（不是字段）；只写字段会失败：
            // 实机日志 "静态字段缺失 GameManager.IsLongMusic"。
            try
            {
                var dataManager = Singleton<DataManager>.Instance;
                if (dataManager == null) return;
                var music = dataManager.GetMusic(musicId);
                if (music == null) return;
                bool isLong = dataManager.IsLong(music.longMusic);
                bool set = false;
                try
                {
                    var prop = typeof(Manager.GameManager).GetProperty("IsLongMusic",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null && prop.CanWrite)
                    {
                        prop.SetValue(null, isLong, null);
                        set = true;
                    }
                }
                catch (Exception) { }
                if (!set)
                    set = NavReflect.SetStatic(typeof(Manager.GameManager), "IsLongMusic", isLong);
                if (isLong)
                    Log.Info("[nav] 長尺曲判定: " + musicId + " longMusic=" + music.longMusic +
                             " → IsLongMusic=true (set=" + set + ")");
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("longmusic", "设置 IsLongMusic 失败: " + e.Message);
            }
        }

        // ============================================================================================
        //  4) 立刻结算当前谱面
        // ============================================================================================

        /// <summary>
        /// 立即结束当前谱面（跳过 note 直接到结算）。
        ///  - ScoreHooks 有本轨期望成绩 -> 用 FastSkip 同款路径强制成绩后结算；
        ///  - 否则（filler / 中止轨）-> 游戏原生 TrackSkip，不碰成绩。
        /// 幂等；返回 true 表示已经触发。
        /// 注意：为了服务端 15s 间隔校验，MinTrackGapSec 未到时会返回 false 并继续等。
        /// </summary>
        public static bool SkipCurrentChart()
        {
            var gp = NavProcess.Find<GameProcess>();
            if (gp == null) return false;

            int sequence = NavReflect.ToInt(NavReflect.Get(gp, "_sequence"));
            if (sequence < 4) return false;  // 还没进 Play
            if (sequence >= 9) return false; // 已经 Release

            double now = Time.realtimeSinceStartup;

            // 15s 间隔：同一个 credit 内相邻 playlog 至少差 15s，否则整包可能被判无效
            if (RespectTrackGap)
            {
                double elapsed = now - _lastTrackStartAt;
                if (elapsed < MinTrackGapSec)
                {
                    int musicId = _selMusicId >= 0 ? _selMusicId : -1;
                    if (_gapWarnedForMusicId != musicId)
                    {
                        _gapWarnedForMusicId = musicId;
                        Log.Info("[nav] 等 15s 轨间隔：还差 " + (MinTrackGapSec - elapsed).ToString("0.0") + "s");
                    }
                    return false;
                }
            }

            if (_skipProcessId == gp.ProcessId && now - _skipAt < 1.5) return true; // 已经在触发中
            _skipProcessId = gp.ProcessId;
            _skipAt = now;

            var monitors = NavReflect.Get(gp, "_monitors") as Array;
            var messages = NavReflect.Get(gp, "_message") as Array;
            var container = NavProcess.ContainerOf(gp);
            var playManager = Singleton<GamePlayManager>.Instance;
            if (monitors == null || playManager == null)
            {
                NavReflect.WarnOnce("skip-null", "SkipCurrentChart: monitors/GamePlayManager 不可用");
                return false;
            }

            bool party = NavReflect.ToBool(NavReflect.Call(gp, "IsPartyPlay"));
            int trackNo = Math.Max(0, (int)Manager.GameManager.MusicTrackNumber - 1);

            // 哪些 monitor 有玩家
            int[] active = new int[monitors.Length];
            int activeCount = 0;
            for (int i = 0; i < monitors.Length && i < 2; i++)
            {
                bool isEntry;
                try
                {
                    var userData = Singleton<UserDataManager>.Instance.GetUserData(i);
                    isEntry = userData != null && userData.IsEntry;
                }
                catch (Exception) { isEntry = i == 0; }
                if (isEntry && activeCount < active.Length) active[activeCount++] = i;
            }
            if (activeCount == 0) { active[0] = 0; activeCount = 1; }

            int desiredAchievement, desiredDx;
            bool applyScore = ScoreHooks.ConsumeTrackScore(out desiredAchievement, out desiredDx);
            // 单位换算（实机校准用；默认 1/10000 %，config 里可切 Percent100/Raw）
            int forceAchievement = ScoreHooks.ConvertForForce(desiredAchievement);

            try
            {
                if (applyScore)
                {
                    // Sinmai-Assist FastSkip 的 CustomSkip 路径：先把谱面状态推到可强制成绩的位置
                    for (int k = 0; k < activeCount; k++)
                        NavReflect.Call(monitors.GetValue(active[k]), "Seek", 0);
                    NavReflect.CallStatic(typeof(NotesManager), "StartPlay", 0f);
                    NavReflect.CallStatic(typeof(NotesManager), "Pause", true);
                    try { playManager.Initialize(party); }
                    catch (Exception e) { NavReflect.WarnOnce("gp-init", "GamePlayManager.Initialize 失败: " + e.Message); }
                }

                for (int k = 0; k < activeCount; k++)
                {
                    int i = active[k];
                    var monitor = monitors.GetValue(i);
                    if (monitor == null) continue;

                    if (applyScore)
                    {
                        NavReflect.Call(monitor, "ForceAchivement", forceAchievement, desiredDx);
                        VerifyForcedAchievement(playManager, i, trackNo, forceAchievement, desiredAchievement);
                    }
                    else if (SettleFillerImmediately)
                    {
                        // 游戏原生跳过：不改成绩
                        var score = SafeGetGameScore(playManager, i, trackNo);
                        if (score != null) NavReflect.Call(score, "SetTrackSkip");
                        string used;
                        NavReflect.TryCallAny(playManager, new[] { "SetTrackSkipFlag", "SetTrackSkipFrag" },
                            new object[] { i, trackNo }, out used);
                    }
                }

                // 结算：UpdateSubbMonitorData -> 发消息 -> 同步结果 -> SetRelease
                for (int k = 0; k < activeCount; k++)
                {
                    int i = active[k];
                    NavReflect.Call(gp, "UpdateSubbMonitorData", i);

                    if (container != null && messages != null && i < messages.Length)
                    {
                        var message = messages.GetValue(i) as Manager.Message;
                        if (message != null)
                        {
                            try { container.processManager.SendMessage(message); }
                            catch (Exception e) { NavReflect.WarnOnce("sendmsg", "SendMessage 失败: " + e.Message); }
                        }
                    }
                    NavReflect.Call(playManager, "SetSyncResult", i, trackNo);
                }

                NavReflect.Call(gp, "SetRelease");
            }
            catch (Exception e)
            {
                Log.Error("[nav] SkipCurrentChart 失败", e);
                return false;
            }

            _lastTrackSettledAt = Time.realtimeSinceStartup;
            Log.Info("[nav] 跳过第 " + Manager.GameManager.MusicTrackNumber + " 轨 " +
                     "(music=" + _selMusicId + "-" + _selLevel + ", score=" +
                     (applyScore ? ScoreHooks.AchievementText(desiredAchievement) +
                                   " 入参=" + forceAchievement + "[" + ScoreHooks.ForceUnit + "]" : "不改") + ")");
            ScoreHooks.EndTrack();
            return true;
        }

        private static GameScoreList SafeGetGameScore(GamePlayManager playManager, int monitor, int trackNo)
        {
            // trackNo 在 1.65 是 0-based（MusicTrackNumber-1），但不同入口用法不完全一致，两个都试。
            try
            {
                var score = playManager.GetGameScore(monitor, trackNo);
                if (score != null) return score;
            }
            catch (Exception) { }
            try
            {
                var list = playManager.GetGameScores(monitor);
                if (list != null && list.Length > 0)
                {
                    int index = trackNo;
                    if (index < 0) index = 0;
                    if (index >= list.Length) index = list.Length - 1;
                    return list[index];
                }
            }
            catch (Exception) { }
            return null;
        }

        private static void VerifyForcedAchievement(GamePlayManager playManager, int monitor, int trackNo,
            int forceArg, int expectedRaw)
        {
            try
            {
                uint actual = playManager.GetAchivement(monitor, trackNo);
                ScoreHooks.ExamineReadback("强制成绩校验", forceArg, expectedRaw, (int)actual, monitor, trackNo);
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("verify-score", "校验强制成绩失败: " + e.Message);
            }
        }

        // ============================================================================================
        //  5) Continue（续关）阶段
        // ============================================================================================

        /// <summary>
        /// 在 Continue 阶段选择“继续”（同一会话进下一个 credit）。
        /// 幂等；返回 true 表示已发出。注意：协议要求每个 credit 重新登录，正常情况下用不到本方法。
        /// </summary>
        public static bool ContinueToNextTrack()
        {
            var continueProcess = NavProcess.Find<ContinueProcess>();
            if (continueProcess == null) return false;

            double now = Time.realtimeSinceStartup;
            if (now - _continueAt < 1.0) return true;
            _continueAt = now;

            try
            {
                var flags = Manager.GameManager.IsSelectContinue;
                if (flags != null) for (int i = 0; i < flags.Length; i++) flags[i] = true;
            }
            catch (Exception e) { NavReflect.WarnOnce("continue-flags", "写 IsSelectContinue 失败: " + e.Message); }

            string used;
            bool ok = NavReflect.TryCallAny(continueProcess, new[] { "NextContinueProcess" }, null, out used);
            if (!ok)
            {
                // 退一步：把状态推到 Continue，剩下交给游戏
                NavReflect.SetEnum(continueProcess, "_state", 4);
                NavReflect.WarnOnce("continue-advance", "ContinueProcess 没有可用的推进方法，已设置 _state=Continue");
            }
            Log.Info("[nav] Continue -> 继续（1P/2P 已置位）");
            return true;
        }

        /// <summary>
        /// 在 Continue 阶段放弃续关：credit 结束 -> MapResult/DataSave（上传 + 登出）。
        /// 幂等；返回 true 表示已发出。
        /// </summary>
        public static bool DeclineContinue()
        {
            var continueProcess = NavProcess.Find<ContinueProcess>();
            if (continueProcess == null) return false;

            double now = Time.realtimeSinceStartup;
            if (now - _continueAt < 1.0) return true;
            _continueAt = now;

            try
            {
                var flags = Manager.GameManager.IsSelectContinue;
                if (flags != null) for (int i = 0; i < flags.Length; i++) flags[i] = false;
            }
            catch (Exception e) { NavReflect.WarnOnce("continue-flags2", "写 IsSelectContinue 失败: " + e.Message); }

            // 不按“继续”游戏也会超时结束，但太慢；直接掐掉本局计时
            var manager = NavProcess.Manager;
            if (manager != null)
            {
                try { manager.ForceTimeUp(); }
                catch (Exception e) { NavReflect.WarnOnce("decline-timeup", "ForceTimeUp 失败: " + e.Message); }
            }
            Log.Info("[nav] Continue -> 放弃续关（结束本局）");
            return true;
        }

        // ============================================================================================
        //  6) 登录 / 登出桥
        // ============================================================================================

        /// <summary>模拟刷卡登录。委托给 flow-dev 的 LoginHooks.RequestLogin（未设置时自动反射绑定）。</summary>
        public static bool RequestLogin(string accessCode)
        {
            var handler = LoginHandler ?? AutoBindLogin();
            if (handler == null)
            {
                NavReflect.WarnOnce("login-nohandler",
                    "RequestLogin: 没有 LoginHandler，也没找到 LoginHooks.SimulateSwipe/RequestLogin(string)");
                return false;
            }
            try { return handler(accessCode); }
            catch (Exception e) { Log.Error("[nav] RequestLogin 委托异常", e); return false; }
        }

        /// <summary>用“超时”的方式结束会话（我们统一的登出方式）。</summary>
        public static bool RequestLogoutByTimeout()
        {
            _logoutRequested = true;
            double now = Time.realtimeSinceStartup;

            bool handlerResult = false;
            var handler = LogoutHandler ?? AutoBindLogout();
            if (handler != null)
            {
                // 有 LoginHooks 实现时以它为准：它会在 GamePlay 时返回 false，表示"先跳曲再登出"。
                try { handlerResult = handler(); }
                catch (Exception e) { Log.Error("[nav] RequestLogoutByTimeout 委托异常", e); }
            }
            else
            {
                // 没有委托时的兜底：强制本局时间到
                var manager = NavProcess.Manager;
                if (manager != null && now - _logoutAt >= 1.5)
                {
                    _logoutAt = now;
                    try
                    {
                        manager.ForceTimeUp();
                        handlerResult = true;
                        Log.Info("[nav] 兜底登出: ProcessManager.ForceTimeUp()");
                    }
                    catch (Exception e) { Log.Error("[nav] ForceTimeUp 失败", e); }
                }
            }

            // 受付屏补连点 + 强制倒计时归零：实机验证 ForceTimeUp 对 ModeSelect 受付屏无效
            bool burst = EndReceptionBurst();
            ForceCountdownToZero();
            if (burst && !handlerResult)
                Log.Info("[nav] 登出: ForceTimeUp 未推进，改用 timeskip 连点/倒计时归零结束受付");
            return handlerResult || burst;
        }

        private static Func<string, bool> AutoBindLogin()
        {
            if (_autoLogin != null) return _autoLogin;
            try
            {
                var t = typeof(LoginHooks);
                string[] names = { "RequestLogin", "SimulateSwipe", "Login" };
                for (int i = 0; i < names.Length; i++)
                {
                    var m = t.GetMethod(names[i], BindingFlags.Public | BindingFlags.Static);
                    if (m == null) continue;
                    if (m.ReturnType != typeof(bool)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;
                    _autoLogin = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), m);
                    Log.Info("[nav] 自动绑定 LoginHooks." + names[i]);
                    return _autoLogin;
                }
            }
            catch (Exception e) { NavReflect.WarnOnce("autologin", "自动绑定 LoginHooks 失败: " + e.Message); }
            return null;
        }

        private static Func<bool> AutoBindLogout()
        {
            if (_autoLogout != null) return _autoLogout;
            try
            {
                var t = typeof(LoginHooks);
                string[] names = { "RequestLogoutByTimeout", "LogoutByTimeout", "RequestLogout" };
                for (int i = 0; i < names.Length; i++)
                {
                    var m = t.GetMethod(names[i], BindingFlags.Public | BindingFlags.Static);
                    if (m == null) continue;
                    if (m.ReturnType != typeof(bool)) continue;
                    if (m.GetParameters().Length != 0) continue;
                    _autoLogout = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), m);
                    Log.Info("[nav] 自动绑定 LoginHooks." + names[i]);
                    return _autoLogout;
                }
            }
            catch (Exception e) { NavReflect.WarnOnce("autologout", "自动绑定 Logout 失败: " + e.Message); }
            return null;
        }
    }
}
