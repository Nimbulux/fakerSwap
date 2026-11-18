using System;
using System.Reflection;
using FakerSwap.Core;
using HarmonyLib;
using Manager;

namespace FakerSwap.Flow
{
    /// <summary>
    /// While <see cref="MachineLock"/> is active the cabinet belongs to the task, so every menu
    /// button / touch-panel read is swallowed.  The card swipe is NOT affected: the daemon reads
    /// <c>DEVICE/aime.txt</c> through its own keyboard handling, not through Manager.InputManager.
    ///
    /// Modules that need to inject synthetic input (navigation does not, but keep the hatch) set
    /// <see cref="AllowSyntheticInput"/> around the injection.
    /// </summary>
    public static class InputLock
    {
        /// <summary>When true the lock is bypassed so the flow can fake a button press.</summary>
        public static bool AllowSyntheticInput;

        /// <summary>Scope helper for navigation's touch injection (same as setting the bool).</summary>
        public static void BeginSyntheticInput() { AllowSyntheticInput = true; }
        public static void EndSyntheticInput() { AllowSyntheticInput = false; }

        /// <summary>
        /// Login / source-reading phases still need the game's own confirm dialogs (and the operator
        /// may have to press a button); only the actual transfer is locked down completely.
        /// </summary>
        public static bool ShouldSwallow
        {
            get
            {
                if (!ModConfig.Data.LockInput || !MachineLock.Active || AllowSyntheticInput) return false;
                // The CONTINUE prompt only reacts to a real cabinet button - touch injection is
                // ignored there.  Swallowing the button reads would make the prompt time out and
                // drop the session back to the title screen, so never lock input while it is up.
                if (NavProcess.Any<Process.ContinueProcess>()) return false;
                // MODE SELECT waits for a real 決定ボタン press; swallowing the button reads there
                // means the credit can never start (nav then has to force-fade, skipping the area
                // select screen entirely).
                if (NavProcess.Any<Process.ModeSelect.ModeSelectProcess>()) return false;
                switch (RebirthFlow.Phase)
                {
                    case FlowPhase.SourceLoginPending:
                    case FlowPhase.SourceReading:
                    case FlowPhase.TargetLoginPending:
                    case FlowPhase.TargetReading:
                        return false;
                    default:
                        return true;
                }
            }
        }

        private static MethodInfo _swallowMethod;
        private static int _patched;
        private static int _missing;

        public static void Install(HarmonyLib.Harmony harmony)
        {
            _swallowMethod = AccessTools.Method(typeof(InputLock), nameof(Swallow));
            if (_swallowMethod == null)
            {
                Log.Error("InputLock: cannot find Swallow method");
                return;
            }

            var button = typeof(Manager.InputManager.ButtonSetting);
            var area = typeof(Manager.InputManager.TouchPanelArea);
            var sys = typeof(Manager.InputManager.SystemButtonSetting);
            var i = typeof(int);
            var l = typeof(long);

            // ---- "is anything pressed" helpers ----
            Patch(harmony, "IsButtonDown", System.Type.EmptyTypes);
            Patch(harmony, "IsButtonDown", new[] { i });
            Patch(harmony, "IsButtonPush", System.Type.EmptyTypes);
            Patch(harmony, "IsButtonPush", new[] { i });

            // NOTE: touch-panel reads are intentionally NOT swallowed any more.  The cabinet UI
            // (OK / NEXT / song list) is touch driven; swallowing it made the machine unusable for
            // the player and blocked our own touch injection.  Button reads stay locked.

            // ---- per player buttons ----
            Patch(harmony, "GetButtonDown", new[] { i, button });
            Patch(harmony, "GetButtonPush", new[] { i, button });
            Patch(harmony, "GetButtonLongPush", new[] { i, button, l });

            // ---- "any monitor" buttons ----
            Patch(harmony, "GetMonitorButtonDown", new[] { button });
            Patch(harmony, "GetMonitorButtonPush", new[] { button });
            Patch(harmony, "GetMonitorButtonLongPush", new[] { button, l });
            Patch(harmony, "GetMonitorButtonRepeat", new[] { button });
            Patch(harmony, "GetMonitorButtonDown", new[] { i.MakeByRefType(), button });
            Patch(harmony, "GetMonitorButtonPush", new[] { i.MakeByRefType(), button });

            // ---- slide gestures (menus) ----
            Patch(harmony, "SlideAreaLr", System.Type.EmptyTypes);
            Patch(harmony, "SlideAreaLr", new[] { i });
            Patch(harmony, "SlideAreaRl", System.Type.EmptyTypes);
            Patch(harmony, "SlideAreaRl", new[] { i });

            // ---- hardware service / test buttons ----
            Patch(harmony, "GetSystemInputDown", new[] { sys });
            Patch(harmony, "GetSystemInputPush", new[] { sys });

            // ---- in-game button reads (touch stays free for the player / injection) ----
            Patch(harmony, "InGameButtonDown", new[] { i, button });
            Patch(harmony, "InGameButtonPush", new[] { i, button });

            // ---- in-game TOUCH reads: swallowed ONLY during gameplay (C mode AFK must not get
            //      random note hits from someone touching the panel; menus keep their touch input
            //      so the cabinet stays usable for the operator/navigation). ----
            PatchInGameTouch(harmony, "InGameTouchPanelAreaDown", new[] { i, area });
            PatchInGameTouch(harmony, "InGameTouchPanelAreaPush", new[] { i, area });

            Log.Info("InputLock installed: patched=" + _patched + " missing=" + _missing);
        }

        private static void Patch(HarmonyLib.Harmony harmony, string name, System.Type[] args)
        {
            try
            {
                var m = AccessTools.Method(typeof(Manager.InputManager), name, args);
                if (m == null)
                {
                    _missing++;
                    Log.Warn("InputLock: Manager.InputManager." + name + args.Length + " args not found");
                    return;
                }
                harmony.Patch(m, prefix: new HarmonyMethod(_swallowMethod));
                _patched++;
            }
            catch (Exception e)
            {
                _missing++;
                Log.Warn("InputLock: patch " + name + " failed: " + e.Message);
            }
        }

        /// <summary>Prefix for every boolean input getter.</summary>
        private static bool Swallow(ref bool __result)
        {
            if (!ShouldSwallow) return true;
            __result = false;
            return false;
        }

        private static MethodInfo _swallowInGameTouchMethod;

        private static void PatchInGameTouch(HarmonyLib.Harmony harmony, string name, System.Type[] args)
        {
            try
            {
                if (_swallowInGameTouchMethod == null)
                    _swallowInGameTouchMethod = AccessTools.Method(typeof(InputLock), nameof(SwallowInGameTouch));
                var m = AccessTools.Method(typeof(Manager.InputManager), name, args);
                if (m == null)
                {
                    Log.Warn("InputLock: Manager.InputManager." + name + " not found (in-game touch)");
                    return;
                }
                harmony.Patch(m, prefix: new HarmonyMethod(_swallowInGameTouchMethod));
                _patched++;
            }
            catch (Exception e)
            {
                Log.Warn("InputLock: patch " + name + " failed: " + e.Message);
            }
        }

        /// <summary>Swallow in-game touch only while a chart is running (C-mode AFK safety).</summary>
        private static bool SwallowInGameTouch(ref bool __result)
        {
            if (!ModConfig.Data.LockInput || !MachineLock.Active || AllowSyntheticInput) return true;
            GameStage s = GameStagePump.Stage;
            if (s != GameStage.GamePlay && s != GameStage.TrackStart) return true;
            __result = false;
            return false;
        }
    }
}

