using System;
using System.Collections.Generic;
using FakerSwap.Core;
using Manager;
using Process;

namespace FakerSwap.Flow
{
    /// <summary>
    /// Mirrors the game process stack onto <see cref="GameStageTracker"/>.
    ///
    /// The process stack is the authoritative "where are we" source: every maimai screen is a
    /// <see cref="ProcessBase"/> in <c>ProcessManager._processList</c>.  We walk the list from the
    /// newest entry backwards, skip pure transition processes (fade/common) and map the first real
    /// process to a <see cref="GameStage"/>.
    /// </summary>
    public static class GameStagePump
    {
        private static readonly Dictionary<string, GameStage> StageByType =
            new Dictionary<string, GameStage>(StringComparer.Ordinal);

        private static GameStage _stage = GameStage.Unknown;
        private static string _raw = "";
        private static double _stageSince;
        private static double _lastWarnAt = -100;
        private static bool _warnedNoProcess;

        /// <summary>Stage as last published to <see cref="GameStageTracker"/>.</summary>
        public static GameStage Stage { get { return _stage; } }
        public static string Raw { get { return _raw; } }
        public static double SecondsInStage { get { return UnityEngine.Time.realtimeSinceStartup - _stageSince; } }

        static GameStagePump()
        {
            // ---- screens that have a dedicated stage ----
            Map("Process.StartupProcess", GameStage.Startup);
            Map("Process.PowerOnProcess", GameStage.Startup);
            Map("Process.WarningProcess", GameStage.Startup);
            Map("Process.MovieCheckProcess", GameStage.Startup);
            Map("Process.PleaseWaitProcess", GameStage.Startup);
            Map("Process.AdvertiseProcess", GameStage.Title);          // title / attract / logo
            Map("Process.EntryProcess", GameStage.Entry);
            Map("Process.EntryAgingProcess", GameStage.Entry);         // attract loop on the entry screen
            Map("Process.ModeSelect.ModeSelectProcess", GameStage.ModeSelect);
            Map("Process.Information.InformationProcess", GameStage.Information);
            Map("Process.RegionalSelectProcess", GameStage.MapSelect); // 区域选择
            Map("Process.MusicSelectProcess", GameStage.MusicSelect);
            Map("Process.TrackStartProcess", GameStage.TrackStart);
            Map("Process.PlInformationProcess", GameStage.TrackStart);
            Map("Process.GameProcess", GameStage.GamePlay);
            Map("Process.ResultProcess", GameStage.Result);
            Map("Process.GameOverProcess", GameStage.Result);
            Map("Process.ContinueProcess", GameStage.Continue);
            Map("Process.NextTrackProcess", GameStage.Continue);
            Map("Process.PhotoEditProcess", GameStage.PhotoEdit);
            Map("Process.PhotoShootProcess", GameStage.PhotoEdit);
            Map("Process.DataSaveProcess", GameStage.DataSave);
            Map("Process.UserDataULProcess", GameStage.DataSave);
            Map("Process.MapResultProcess", GameStage.MapResult);

            // ---- screens we do not care about but that must not hide the real stage ----
            Map("Process.LoginBonus.LoginBonusProcess", GameStage.Other);
            Map("Process.CharacterSelectProces", GameStage.Other);
            Map("Process.CharacterSelectProcess", GameStage.Other);
            Map("Process.TicketSelect.TicketSelectProcess", GameStage.Other);
            Map("Process.UnlockProcess", GameStage.Other);
            Map("Process.UnlockMusicProcess", GameStage.Other);
            Map("Process.KaleidxScopeProcess", GameStage.Other);
            Map("Process.KaleidxScopeFadeProcess", GameStage.Other);
            Map("Process.FriendBattleResultProcess", GameStage.Other);
            Map("Process.PhotoAgreeProcess", GameStage.Other);
            Map("Process.CollectionProcess", GameStage.Other);
            Map("Process.TutorialProcess", GameStage.Other);
        }

        private static void Map(string fullTypeName, GameStage stage)
        {
            StageByType[fullTypeName] = stage;
        }

        /// <summary>Pure transitions: they never define the stage by themselves.</summary>
        internal static bool IsTransitional(string fullTypeName)
        {
            switch (fullTypeName)
            {
                case "Process.FadeProcess":
                case "Process.InputtableFadeProcess":
                case "Process.SwitchProcess":
                case "Process.CommonProcess":
                case "Process.BroadcastProcess":
                // GenericProcess is a wrapper that sits on top of the real screen process
                // (observed: CommonProcess > EntryProcess > GenericProcess) - it must never hide
                // the stage and it must never be used as a FadeProcess "from" argument.
                case "Process.GenericProcess":
                    return true;
                default:
                    return false;
            }
        }

        public static void Tick()
        {
            if (!ProcessHooks.Ready) return;

            LinkedList<global::Manager.ProcessManager.ProcessControle> list = ProcessHooks.GetProcessList();
            if (list == null || list.Count == 0)
            {
                // Degraded mode: fall back to the last process pushed by ProcessManager.AddProcess.
                if (list == null)
                {
                    string degradedName = ProcessHooks.LastAddedProcessName;
                    if (degradedName.Length > 0)
                    {
                        GameStage degradedStage;
                        if (!StageByType.TryGetValue(degradedName, out degradedStage)) degradedStage = GameStage.Other;
                        if (degradedStage != _stage || degradedName != _raw)
                        {
                            _stage = degradedStage;
                            _raw = degradedName;
                            _stageSince = UnityEngine.Time.realtimeSinceStartup;
                            GameStageTracker.Set(degradedStage, degradedName);
                            Log.Warn("[stage] degraded (no processManager): " + degradedStage + " (" + ShortType(degradedName) + ")");
                        }
                    }
                    else if (!_warnedNoProcess)
                    {
                        _warnedNoProcess = true;
                        WarnThrottled("stage pump: process manager/list not available yet");
                    }
                }
                return;
            }
            _warnedNoProcess = false;

            ProcessBase best = null;
            string bestName = "";
            GameStage bestStage = GameStage.Unknown;

            // Newest process hierarchy wins.  Two tiers:
            //   1. the newest process with a *meaningful* stage (Entry/ModeSelect/MusicSelect/...)
            //   2. if none, the newest non-transitional process mapped to Other/Unknown
            // This keeps overlay/helper processes (e.g. Process.GenericProcess which sits on top of
            // EntryProcess during the card confirm screen) from hiding the real stage.
            ProcessBase fallback = null;
            string fallbackName = "";
            GameStage fallbackStage = GameStage.Other;
            // PleaseWaitProcess is an overlay ("please wait" popup) that can sit on top of the real
            // screen (observed: it hid the Result screen).  Only use it when nothing else exists.
            ProcessBase lowPriority = null;
            string lowPriorityName = "";
            string newestName = "";

            for (var node = list.Last; node != null; node = node.Previous)
            {
                var ctrl = node.Value;
                if (ctrl == null || ctrl.Process == null) continue;
                if (ctrl.State.ToString() == "Released") continue;

                string name = ctrl.Process.GetType().FullName ?? ctrl.Process.GetType().Name;
                if (newestName.Length == 0) newestName = name;
                if (IsTransitional(name)) continue;

                if (name == "Process.PleaseWaitProcess")
                {
                    if (lowPriority == null) { lowPriority = ctrl.Process; lowPriorityName = name; }
                    continue;
                }

                GameStage stage;
                bool mapped = StageByType.TryGetValue(name, out stage);
                if (mapped && stage != GameStage.Other && stage != GameStage.Unknown)
                {
                    best = ctrl.Process;
                    bestName = name;
                    bestStage = stage;
                    break;
                }
                if (fallback == null)
                {
                    fallback = ctrl.Process;
                    fallbackName = name;
                    fallbackStage = mapped ? stage : GameStage.Other;
                }
            }

            if (best == null && fallback != null)
            {
                best = fallback;
                bestName = fallbackName;
                bestStage = fallbackStage;
            }

            // Result / Continue screens stay in the process list but their ProcessControle.State is
            // already "Released" (OnRelease ran) while the UI is still up - observed on the real
            // machine as "ResultProcess Released" + PleaseWait on top.  Trust their presence instead
            // of the state-derived stage.
            if (best == null || bestStage == GameStage.Startup || bestStage == GameStage.Other
                || bestStage == GameStage.Unknown)
            {
                ProcessBase screenProc;
                string screenName;
                GameStage screenStage;
                if (FindPresenceScreen(list, out screenProc, out screenName, out screenStage))
                {
                    best = screenProc;
                    bestName = screenName;
                    bestStage = screenStage;
                }
            }

            if (best == null && lowPriority != null)
            {
                best = lowPriority;
                bestName = lowPriorityName;
                bestStage = GameStage.Startup;
            }

            if (best == null)
            {
                // Only transition processes on screen (e.g. a fade in progress): keep the last stage
                // but refresh the raw name so the UI/log can see what is happening.
                if (newestName.Length > 0 && newestName != _raw)
                {
                    _raw = newestName;
                    GameStageTracker.Set(_stage, _raw);
                }
                return;
            }

            ProcessHooks.SetCurrent(best, bestName);

            GameStage stage2 = bestStage;

            // This build shows the "1P/2P + FREE PLAY + タイムスキップで受付終了" reception screen INSIDE
            // EntryProcess (there is no ModeSelectProcess on the stack - verified on the real
            // machine).  Once a real player is logged in that screen is the mode-select screen
            // semantically, so report ModeSelect; everything else keeps Entry.
            if (stage2 == GameStage.Entry && bestName == "Process.EntryProcess" && IsLoggedInCached())
            {
                stage2 = GameStage.ModeSelect;
            }

            if (stage2 == _stage && bestName == _raw) return;

            GameStage prev = _stage;
            _stage = stage2;
            _raw = bestName;
            _stageSince = UnityEngine.Time.realtimeSinceStartup;
            GameStageTracker.Set(stage2, bestName);

            if (prev != stage2 || ModConfig.Data.Verbose)
            {
                Log.Info("[stage] " + prev + " -> " + stage2 + "   (" + ShortType(bestName) + ")");
            }
        }

        private static bool _loggedInCache;
        private static double _loggedInCacheAt = -100;

        /// <summary>Login state cached for 0.5s (the pump may call this every frame).</summary>
        private static bool IsLoggedInCached()
        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _loggedInCacheAt < 0.5) return _loggedInCache;
            _loggedInCacheAt = now;
            try { _loggedInCache = LoginHooks.IsLoggedIn(); }
            catch (Exception) { _loggedInCache = false; }
            return _loggedInCache;
        }

        /// <summary>
        /// Find a screen process that is still displayed even though its controle state is Released.
        /// Continue wins over Result (Continue is shown after the result screen).
        /// </summary>
        private static bool FindPresenceScreen(LinkedList<Manager.ProcessManager.ProcessControle> list,
                                               out ProcessBase process, out string name, out GameStage stage)
        {
            process = null;
            name = "";
            stage = GameStage.Unknown;

            string[] candidates = { "Process.ContinueProcess", "Process.ResultProcess", "Process.MapResultProcess" };
            GameStage[] stages = { GameStage.Continue, GameStage.Result, GameStage.MapResult };
            for (int c = 0; c < candidates.Length; c++)
            {
                for (var node = list.Last; node != null; node = node.Previous)
                {
                    var ctrl = node.Value;
                    if (ctrl == null || ctrl.Process == null) continue;
                    if ((ctrl.Process.GetType().FullName ?? "") != candidates[c]) continue;
                    process = ctrl.Process;
                    name = candidates[c];
                    stage = stages[c];
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when the game is on one of the "in a credit" screens.</summary>
        public static bool IsInCreditFlow
        {
            get
            {
                GameStage s = _stage;
                return s == GameStage.MusicSelect || s == GameStage.TrackStart || s == GameStage.GamePlay
                       || s == GameStage.Result || s == GameStage.Continue || s == GameStage.PhotoEdit
                       || s == GameStage.DataSave || s == GameStage.MapSelect || s == GameStage.MapResult;
            }
        }

        public static bool IsSettled(GameStage stage, double minSeconds)
        {
            return _stage == stage && SecondsInStage >= minSeconds;
        }

        internal static void Set(GameStage stage, string raw)
        {
            _stage = stage;
            _raw = raw ?? "";
            _stageSince = UnityEngine.Time.realtimeSinceStartup;
            GameStageTracker.Set(stage, raw);
        }

        private static string ShortType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "";
            int i = fullName.LastIndexOf('.');
            return i >= 0 && i + 1 < fullName.Length ? fullName.Substring(i + 1) : fullName;
        }

        private static void WarnThrottled(string msg)
        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastWarnAt < 10) return;
            _lastWarnAt = now;
            Log.Warn(msg);
        }
    }
}
