using System;
using System.Collections.Generic;
using HarmonyLib;
using Manager.UserDatas;

namespace FakerSwap.Core
{
    /// <summary>
    /// Guards a vanilla crash that an "empty account" triggers:
    ///
    ///     Process.CharacterSelectProces.OnStart
    ///       -> UserDetail.AutoComposeCharacterSlot
    ///         -> UserDetail.InsertLockChara       ( List.First(...) )
    ///     InvalidOperationException: Sequence contains no matching element
    ///
    /// When the server hands back a profile with no collected characters (the wiped/dev account
    /// case) the list is empty, First() throws inside OnStart and the whole game dies right after
    /// region select.  We synthesise a minimal, valid slot array instead.
    /// </summary>
    public static class CrashFixes
    {
        private const int FallbackCharaId = 1;

        public static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(UserDetail), "AutoComposeCharacterSlot");
                if (target == null)
                {
                    Log.Warn("CrashFixes: UserDetail.AutoComposeCharacterSlot not found - empty-account guard not installed");
                    return;
                }
                var prefix = new HarmonyMethod(typeof(CrashFixes).GetMethod(nameof(AutoComposeCharacterSlotPrefix)));
                harmony.Patch(target, prefix: prefix);
                Log.Info("CrashFixes: AutoComposeCharacterSlot guarded (empty character list -> synthesized slots)");

                // Held-button injection (MODE SELECT 決定ボタン needs a long press, not an edge).
                harmony.PatchAll(typeof(FakerSwap.Flow.ButtonHold.Patch));
                harmony.PatchAll(typeof(FakerSwap.Flow.ModeDiag.Patch));
                harmony.PatchAll(typeof(FakerSwap.Core.GameProbe.OnStartProbe));
                harmony.PatchAll(typeof(GameProcessOnStartRecoveryPatch));
                harmony.PatchAll(typeof(GameProcessOnStartProbePatch));
                harmony.PatchAll(typeof(GameProcessIsTrackSkipGuard));
                // 选歌页倒计时会在等待时走到 0，触发 MusicSelect.TimerCountUp → OnGameStart →
                // PassTicketStatus.FadeOut() NRE（实机 CrashHandler）。任务期间禁用该倒计时。
                try
                {
                    if (AccessTools.Method(typeof(Process.MusicSelectProcess), "TimerCountUp") != null)
                    {
                        harmony.PatchAll(typeof(MusicSelectTimerCountUpGuard));
                        Log.Info("CrashFixes: MusicSelect.TimerCountUp 已在任务期间禁用（选歌倒计时不再触发崩溃）");
                    }
                    if (AccessTools.Method(typeof(Process.MusicSelectProcess), "OnGameStart") != null)
                    {
                        harmony.PatchAll(typeof(MusicSelectOnGameStartGuard));
                        Log.Info("CrashFixes: MusicSelect.OnGameStart NRE 已兜底");
                    }
                    if (AccessTools.Method(typeof(Monitor.MusicSelectMonitor), "OnStartMusicSelect") != null)
                    {
                        harmony.PatchAll(typeof(MusicSelectOnStartMusicSelectGuard));
                        Log.Info("CrashFixes: MusicSelectMonitor.OnStartMusicSelect NRE 已兜底");
                    }
                    if (AccessTools.Method(typeof(Process.ContinueProcess), "ReLogin") != null)
                    {
                        harmony.PatchAll(typeof(ContinueProcessReLoginTrace));
                        Log.Info("CrashFixes: ContinueProcess.ReLogin 轨迹捕获已安装");
                    }
                }
                catch (Exception e) { Log.Error("CrashFixes: MusicSelect 倒计时/OnGameStart 补丁安装失败", e); }
                Log.Info("CrashFixes: GameProcess.OnStart Finalizer installed (续关后 NRE 不再整局崩溃)");
                Log.Info("CrashFixes: GameProcess.IsTrackSkip 钉死 false (OnUpdate NRE 根因已掐断)");

                // 续关屏诊断：只观测、不注入（用户要求亲手按一次「是」来抓真实调用链）
                try
                {
                    harmony.PatchAll(typeof(FakerSwap.Flow.ContinueDiag.UpdateInputPatch));
                    harmony.PatchAll(typeof(FakerSwap.Flow.ContinueDiag.DetailInputOnDispPatch));
                    harmony.PatchAll(typeof(FakerSwap.Flow.ContinueDiag.ReLoginPatch));
                    harmony.PatchAll(typeof(FakerSwap.Flow.ContinueDiag.NextGameOverPatch));
                    Log.Info("CrashFixes: ContinueDiag 已安装（观测续关屏，不注入按键）");
                }
                catch (Exception e)
                {
                    Log.Error("CrashFixes: ContinueDiag 安装失败", e);
                }

                // Second empty-account crash: the map result screen indexes an empty userMapList.
                var mapTarget = AccessTools.Method(typeof(Monitor.MapResultMonitor), "Initialize");
                if (mapTarget != null)
                {
                    harmony.Patch(mapTarget, prefix: new HarmonyMethod(typeof(CrashFixes).GetMethod(nameof(MapResultInitializePrefix))));
                    Log.Info("CrashFixes: MapResultMonitor.Initialize guarded (empty map list)");
                }
                else Log.Warn("CrashFixes: MapResultMonitor.Initialize not found");

                // The map-result screen still throws even with map rows present (broken/incomplete
                // map master data).  Skip the whole screen and go straight to the data save so the
                // credit still uploads instead of taking the game down.
                var mapProcessStart = AccessTools.Method(typeof(Process.MapResultProcess), "OnStart");
                if (mapProcessStart != null)
                {
                    harmony.Patch(mapProcessStart, prefix: new HarmonyMethod(typeof(CrashFixes).GetMethod(nameof(MapResultProcessOnStartPrefix))));
                    Log.Info("CrashFixes: MapResultProcess.OnStart -> DataSave redirect installed");
                }
                else Log.Warn("CrashFixes: MapResultProcess.OnStart not found");

                // Skipping OnStart leaves the process without its monitors; its OnUpdate then throws
                // NullReferenceException and still takes the game down.  Suppress it as well.
                var mapProcessUpdate = AccessTools.Method(typeof(Process.MapResultProcess), "OnUpdate");
                if (mapProcessUpdate != null)
                {
                    harmony.Patch(mapProcessUpdate, prefix: new HarmonyMethod(typeof(CrashFixes).GetMethod(nameof(MapResultProcessOnUpdatePrefix))));
                    Log.Info("CrashFixes: MapResultProcess.OnUpdate suppressed while skipped");
                // GameProcess.OnStart is intentionally NOT guarded: the process allocates its monitors
                // inside OnStart, so skipping the vanilla body leaves them null and the game stops.
                }
                else Log.Warn("CrashFixes: MapResultProcess.OnUpdate not found");
            }
            catch (Exception e)
            {
                Log.Error("CrashFixes install failed", e);
            }
        }

        /// <summary>
        /// Replaces the map result screen (which crashes on this account) with a data save, so the
        /// credit is still uploaded and the cabinet returns to the song list instead of dying.
        /// </summary>
        public static bool MapResultProcessOnStartPrefix(Process.MapResultProcess __instance,
            Process.ProcessDataContainer ___container)
        {
            try
            {
                if (___container == null || ___container.processManager == null) return true;
                // Only bypass the screen when explicitly asked to: skipping it leaves a process on
                // the stack that never finishes its normal transition, which makes MapResult /
                // Continue / the next credit pile up on top of each other and eventually NRE in
                // GameProcess.OnUpdate.  A healthy account renders the screen fine, so by default
                // we let the vanilla path run.
                if (!ModConfig.Data.MapResultGuard) return true;
                // 旁路注入 DataSave 也必须过 WriteGuard + 真实非空账号 + 写前备份（和 ForceDataSave 同一套闸门）。
                if (!FakerSwap.Flow.LoginHooks.AllowInjectedDataSave("MapResultGuard"))
                {
                    Log.Warn("CrashFixes: MapResultGuard 注入被安全闸门拒绝 → 走原版流程");
                    _mapResultSkipped = false;
                    return true;
                }
                _mapResultSkipped = true;
                Log.Warn("CrashFixes: MapResultProcess skipped (map data unusable) -> DataSaveProcess");
                ___container.processManager.AddProcess(new Process.DataSaveProcess(___container), 50);
                ___container.processManager.ReleaseProcess(__instance);
                return false;
            }
            catch (Exception e)
            {
                _mapResultSkipped = false;
                Log.Error("CrashFixes map-result redirect failed (falling back to vanilla)", e);
                return true;
            }
        }

        /// <summary>
        /// The CONTINUE path keeps the same session, so the previous credit's GameProcess can still
        /// be started while its monitors/containers are already torn down - OnStart then throws a
        /// NullReferenceException and kills the game (seen right after a successful continue).
        /// Skip the vanilla body when its required state is missing.
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(Process.GameProcess), "OnStart")]
        internal static class GameProcessOnStartGuardPatch
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(Process.GameProcess __instance)
            {
                try
                {
                    if (__instance == null) return true;
                    var monitors = HarmonyLib.Traverse.Create(__instance).Field("_monitors").GetValue() as Array;
                    if (monitors == null || monitors.Length == 0) return Skip("monitors empty");
                    var messages = HarmonyLib.Traverse.Create(__instance).Field("_message").GetValue() as Array;
                    if (messages == null) return Skip("_message null");
                }
                catch (Exception e)
                {
                    return Skip(e.GetType().Name + ": " + e.Message);
                }
                return true;
            }

            private static bool Skip(string reason)
            {
                double tnow = (DateTime.UtcNow - _startedAt).TotalSeconds;
                if (tnow - _lastGameStartWarnAt > 5.0)
                {
                    _lastGameStartWarnAt = tnow;
                }
                return false;
            }

            private static double _lastGameStartWarnAt = -1000;
            private static readonly DateTime _startedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// 续关后进入下一批的第一首时，`GameProcess.OnStart` 会在 TrackStartProcess 仍处于
        /// Released（还没释放完）的瞬间抛 NullReferenceException，直接把整局游戏打死。
        /// 实机栈：ProcessManager.Update -> GameProcess.OnStart，且用户手按 CONTINUE YES 不崩。
        ///
        /// 这里用 Harmony <b>Finalizer</b> 把异常吞掉；同时把实例标记成"半初始化"，让
        /// Navigation 的 `GameProcessOnUpdateGuardPatch` 跳过它的 OnUpdate（OnStart 失败后字段
        /// 是 null，OnUpdate 必 NRE），最后延迟一帧用 `ProcessManager.ReleaseProcess` 回收，
        /// 让游戏自己回到上一屏，flow 的 `HasLiveGameProcess()` 也能继续往下走。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(Process.GameProcess), "OnStart")]
        internal static class GameProcessOnStartRecoveryPatch
        {
            /// <summary>最近一次 OnStart 是否异常失败（诊断用）。</summary>
            public static bool OnStartFailed;

            private static Process.GameProcess _broken;

            /// <summary>该实例的 OnStart 是否失败过（半初始化，OnUpdate 必须跳过）。</summary>
            public static bool IsBroken(object instance)
            {
                return instance != null && ReferenceEquals(instance, _broken);
            }

            public static void ClearBroken()
            {
                _broken = null;
                OnStartFailed = false;
            }

            [HarmonyLib.HarmonyFinalizer]
            private static Exception Finalizer(Process.GameProcess __instance, Exception __exception)
            {
                if (__exception == null)
                {
                    OnStartFailed = false;
                    return null;
                }
                // 只吞"续关后 GameProcess.OnStart 自己 NRE"这一种；其它 Mod 的异常必须原样抛回去，
                // 否则会把别人的崩溃伪装成"插件吞了"，现场全丢。
                bool known = __exception is NullReferenceException;
                if (known)
                {
                    try
                    {
                        string stack = __exception.StackTrace;
                        if (!string.IsNullOrEmpty(stack) &&
                            stack.IndexOf("GameProcess", StringComparison.Ordinal) < 0)
                            known = false;
                    }
                    catch (Exception) { }
                }
                if (!known)
                {
                    Log.Error("[crash] GameProcess.OnStart 抛出非已知异常 " + __exception.GetType().Name +
                              " → 不吞（交回原版处理）: " + __exception);
                    return __exception;
                }
                OnStartFailed = true;
                _broken = __instance;
                // 完整栈（含内层帧与 IL 偏移）是定位 OnStart 里哪一行 NRE 的唯一线索。
                Log.Error("[crash] GameProcess.OnStart 抛出 " + __exception.GetType().Name +
                          " → 吞掉并延迟回收; 完整栈: " + __exception);
                try
                {
                    MainThread.Post(delegate { ReleaseBroken(__instance); });
                }
                catch (Exception e) { Log.Warn("[crash] 排队回收 GameProcess 失败: " + e.Message); }
                return null;   // 吞掉异常
            }

            /// <summary>延迟到下一帧：从 OnStart 里直接释放自己不安全，而且 ProcessManager 正在遍历。</summary>
            private static void ReleaseBroken(Process.GameProcess instance)
            {
                try
                {
                    if (instance == null) return;
                    var container = FakerSwap.Flow.NavReflect.Get(instance, "container") as Process.ProcessDataContainer;
                    if (container == null || container.processManager == null)
                    {
                        Log.Warn("[crash] 回收半初始化 GameProcess: container/processManager 为空");
                        return;
                    }
                    try { FakerSwap.Flow.NavReflect.Call(instance, "SetRelease"); }
                    catch (Exception) { }
                    try
                    {
                        container.processManager.ReleaseProcess(instance);
                        Log.Warn("[crash] 半初始化的 GameProcess 已回收（OnStart 失败），等游戏自己回上一屏");
                    }
                    catch (Exception e)
                    {
                        Log.Error("[crash] ProcessManager.ReleaseProcess 失败（进程可能滞留）", e);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[crash] 回收 GameProcess 失败", e);
                }
            }
        }

        /// <summary>
        /// 续关后 `GameProcess.OnStart` 崩溃前的现场快照：OnStart 依赖的全局状态全部打一行，
        /// 配合 finalizer 的完整栈 IL 偏移，能直接定位是哪一条调用读到 null。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(Process.GameProcess), "OnStart")]
        internal static class GameProcessOnStartProbePatch
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(Process.GameProcess __instance)
            {
                try
                {
                    if (!FakerSwap.Flow.RebirthFlow.IsBusy) return;   // 用户自己玩时不刷日志
                    var sb = new System.Text.StringBuilder(200);
                    sb.Append("[crash] GameProcess.OnStart 现场: trackNo=")
                      .Append(Manager.GameManager.MusicTrackNumber)
                      .Append(", musicIds=");
                    var ids = Manager.GameManager.SelectMusicID;
                    if (ids == null) sb.Append("null");
                    else
                    {
                        sb.Append('[');
                        for (int i = 0; i < ids.Length && i < 4; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append(ids[i]);
                        }
                        sb.Append(']');
                    }
                    sb.Append(", modeId=").Append(Manager.GameManager.SelectedModeID)
                      .Append(", normal=").Append(Manager.GameManager.IsNormalMode)
                      .Append(", course=").Append(Manager.GameManager.IsCourseMode)
                      .Append(", freedom=").Append(Manager.GameManager.IsFreedomMode);
                    var mgr = MAI2.Util.Singleton<Manager.GamePlayManager>.Instance;
                    if (mgr == null) sb.Append(", gpm=null");
                    else
                    {
                        sb.Append(", gpm{quickRetry=").Append(SafeBool(delegate { return mgr.IsQuickRetry(); }))
                          .Append(", scoreLists=").Append(SafeInt(delegate { return mgr.GetScoreListCount(); }))
                          .Append('}');
                    }
                    sb.Append(", monitors=")
                      .Append(FakerSwap.Flow.NavReflect.Get(__instance, "_monitors") != null)
                      .Append(", userData0=")
                      .Append(SafeBool(delegate
                          {
                              var ud = MAI2.Util.Singleton<Manager.UserDataManager>.Instance;
                              return ud != null && ud.GetUserData(0) != null;
                          }));
                    Log.Info(sb.ToString());
                }
                catch (Exception e) { Log.Warn("[crash] OnStart 探针失败: " + e.Message); }
            }

            private static string SafeBool(Func<bool> f)
            {
                try { return f().ToString(); } catch (Exception) { return "?"; }
            }

            private static string SafeInt(Func<int> f)
            {
                try { return f().ToString(); } catch (Exception) { return "?"; }
            }
        }


        /// <summary>
        /// 【崩溃根因 · Crash Handler 完整栈证实】
        ///   NullReferenceException at Process.GameProcess.IsTrackSkip () [0x00023]
        ///     &lt;- IsTrackSkipWaitEnd() &lt;- DMD&lt;GameProcess::OnUpdate&gt; &lt;- ProcessManager.Update
        ///
        /// IsTrackSkip 读的 GamePlayManager 跳轨数据在新 credit 里是 null，于是 OnUpdate 每帧都 NRE。
        /// 我们**完全不需要跳轨功能**（所有 SetTrackSkipFlag 调用已移除），
        /// 所以直接把 IsTrackSkip 钉死返回 false、不执行原方法体 —— NRE 从源头消失，
        /// IsTrackSkipWaitEnd() 恒为 false，游戏走正常流程。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(Process.GameProcess), "IsTrackSkip")]
        internal static class GameProcessIsTrackSkipGuard
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(ref bool __result)
            {
                try
                {
                    if (FakerSwap.Flow.Navigation.IsTaskActive() && !FakerSwap.Flow.RebirthFlow.IsStoppingNow)
                    {
                        __result = false;
                        return false;   // 任务中钉死 false，防 OnUpdate NRE
                    }
                }
                catch (Exception) { }
                return true;        // 空闲/中止中：放过原值，用户可手动跳过
            }
        }

        /// <summary>GameProcess.OnStart 失败过的实例（半初始化）—— 供 Navigation 的 OnUpdate 守卫查询。</summary>
        public static bool IsGameProcessBroken(object instance)
        {
            return GameProcessOnStartRecoveryPatch.IsBroken(instance);
        }

        /// <summary>True while the map-result screen is bypassed; its OnUpdate would NRE on the
        /// monitors that OnStart never created.</summary>
        private static bool _mapResultSkipped;

        public static bool MapResultProcessOnUpdatePrefix()
        {
            // Suppress only while the screen is actually bypassed: in that mode OnStart never ran,
            // so every OnUpdate would NRE on the monitors that were never created.  The flag stays
            // set for the whole lifetime of the process (clearing it after one frame let the
            // vanilla body run on the next frame and killed the game).
            return !_mapResultSkipped;
        }

        /// <summary>
        /// MapResultMonitor reads userData.MapList[SelectMapID]; when the account has no map rows at
        /// all (wiped profile) it throws ArgumentOutOfRangeException inside Process.MapResultProcess.OnStart
        /// and kills the game.  Skipping initialization lets the process time out and move on.
        /// </summary>
        public static bool MapResultInitializePrefix(int monIndex)
        {
            try
            {
                var userData = MAI2.Util.Singleton<Manager.UserDataManager>.Instance.GetUserData(monIndex);
                var maps = userData?.MapList;
                if (maps != null && maps.Count > 0) return true;

                Log.Warn("CrashFixes: account has no map rows - skipping MapResultMonitor.Initialize(mon=" +
                         monIndex + ") to avoid ArgumentOutOfRangeException");
                return false;
            }
            catch (Exception e)
            {
                Log.Error("CrashFixes map prefix failed (falling back to vanilla)", e);
                return true;
            }
        }

        /// <summary>
        /// Replaces the original when the account has no usable character, so the vanilla
        /// InsertLockChara().First() cannot throw.
        /// </summary>
        public static bool AutoComposeCharacterSlotPrefix(List<UserChara> removeList, int leaderID,
            ref UserChara[] __result)
        {
            bool usable = false;
            try { usable = HasUsableChara(removeList); }
            catch (Exception e)
            {
                Log.Warn("CrashFixes: character list probe failed (" + e.GetType().Name +
                         ") - treating the account as having no characters");
            }
            // 用 CharaList 装满 removeList，保证原版 AutoComposeCharacterSlot 能算出 5 个槽             if (usable && (removeList == null || removeList.Count == 0))             {                 try                 {                     var manager2 = MAI2.Util.Singleton<Manager.UserDataManager>.Instance;                     var ud2 = manager2 != null ? manager2.GetUserData(0) : null;                     var cl2 = ud2 != null ? ud2.CharaList : null;                     if (cl2 != null && removeList != null)                     {                         for (int i = 0; i < cl2.Count && i < 200; i++)                         {                             var c2 = cl2[i];                             if (c2 != null && c2.ID > 0) removeList.Add(c2);                         }                     }                 }                 catch (Exception) { }             }
            if (usable) return true;

            // Never fall back to the vanilla method here: with an unusable list it throws
            // "Sequence contains no matching element" and kills the game.
            try
            {
                int id = leaderID > 0 ? leaderID : FallbackCharaId;
                var slots = new UserChara[5];
                for (int i = 0; i < slots.Length; i++)
                {
                    var chara = new UserChara();
                    chara.ID = id;
                    slots[i] = chara;
                }
                __result = slots;
                Log.Warn("CrashFixes: account has no characters - synthesized 5 slots with charaId=" + id +
                         " (character select would have crashed otherwise)");
                return false;
            }
            catch (Exception e)
            {
                Log.Error("CrashFixes: could not synthesize character slots - returning an empty array", e);
                __result = new UserChara[0];
                return false;
            }
        }

        private static bool HasUsableChara(List<UserChara> list)
        {
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c != null && c.ID > 0) return true;
                }
            }
            // removeList 可能只是「本次可移除列表」，账号真实角色在 UserData.CharaList；
            // 国服 1.55 实测 removeList 为空，但账号明明有旅行伙伴 → 不能用它判断「没有角色」。
            try
            {
                var manager = MAI2.Util.Singleton<Manager.UserDataManager>.Instance;
                var userData = manager != null ? manager.GetUserData(0) : null;
                var charaList = userData != null ? userData.CharaList : null;
                if (charaList != null)
                {
                    for (int i = 0; i < charaList.Count; i++)
                    {
                        var c = charaList[i];
                        if (c != null && c.ID > 0) return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// 选歌页倒计时在等待选曲时会走到 0，触发 MusicSelectProcess.TimerCountUp → OnGameStart →
        /// PassTicketStatus.FadeOut() 的 NullReferenceException（实机 Crash Handler 栈）。
        /// 任务运行期间直接跳过 TimerCountUp：倒计时不再自动开局，也不会走那条 NRE 路径。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(Process.MusicSelectProcess), "TimerCountUp")]
        internal static class MusicSelectTimerCountUpGuard
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix()
            {
                try
                {
                    if (FakerSwap.Flow.Navigation.IsTaskActive()) return false;
                }
                catch (Exception) { }
                return true;
            }
        }

        /// <summary>安全网：即使 OnGameStart 仍被调用，也不让票券淡出 NRE 把游戏带崩。</summary>
        [HarmonyLib.HarmonyPatch(typeof(Process.MusicSelectProcess), "OnGameStart")]
        internal static class MusicSelectOnGameStartGuard
        {
            [HarmonyLib.HarmonyFinalizer]
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception is NullReferenceException)
                {
                    Log.Warn("[crash] MusicSelect.OnGameStart 吞掉 NRE（选歌倒计时/票券状态）: " + __exception.Message);
                    return null;
                }
                return __exception;
            }
        }

        /// <summary>实机 2026-11-20 02:03：进选歌页时 GenreSelectData 未就绪，
        /// MusicSelectMonitor.OnStartMusicSelect → getTabString 抛 NRE 直接崩游戏。吞掉，界面后补。</summary>
        [HarmonyLib.HarmonyPatch(typeof(Monitor.MusicSelectMonitor), "OnStartMusicSelect")]
        internal static class MusicSelectOnStartMusicSelectGuard
        {
            [HarmonyLib.HarmonyFinalizer]
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception is NullReferenceException)
                {
                    Log.Warn("[crash] MusicSelectMonitor.OnStartMusicSelect 吞掉 NRE（分类数据未就绪）: " + __exception.Message);
                    return null;
                }
                return __exception;
            }
        }

        /// <summary>只观测：原生续关是否真的调用 ContinueProcess.ReLogin（对照手动游玩抓包用）。</summary>
        [HarmonyLib.HarmonyPatch(typeof(Process.ContinueProcess), "ReLogin")]
        internal static class ContinueProcessReLoginTrace
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(int id)
            {
                Log.Info("[trace] ContinueProcess.ReLogin(id=" + id + ") 被调用（原生续关登录）");
            }
        }
    }
}
