using System;
using HarmonyLib;
using FakerSwap.Core;

namespace FakerSwap.Flow
{
    /// <summary>
    /// Injects a REAL cabinet button press into the game's input layer.
    ///
    /// Why this exists (verified against the *loaded* IL of SDEZ 1.65):
    ///
    ///   ModeSelectProcess.UpdateInput(monitorId):
    ///       if (!InputManager.GetButtonDown(monitorId, ButtonSetting.Button04)) return;   // 決定 = 4号键
    ///       ... -> _monitors[monitorId].OKButtonAnim(Button04) -> monitor._isOK = true
    ///
    ///   InputManager.GetButtonDown(m, b) = IsButtonDown(m) &amp;&amp; PlayerButtonDown[m, b]
    ///
    /// Writing `PlayerButtonPush`/`PlayerButtonDown` (the old ButtonHold approach) is not enough:
    /// `UpdateAmInput()` recomputes `AnyButtonDown` / `DownButtonList` from the JVS raw state every
    /// frame, so `IsButtonDown(monitor)` stays false and the game never sees the edge.  Also the
    /// decision button is Button04 - NOT `ButtonSetting.Select` (that is the round cabinet button
    /// used by other screens).
    ///
    /// Patching `GetButtonDown` itself is the narrowest point: the patch only fires for the exact
    /// (monitor, button) pair that was armed, returns true exactly ONCE (a real edge), and expires
    /// on its own if nothing consumes it - so it can never keep a button stuck down.
    /// </summary>
    public static class InputInject
    {
        private static int _monitor;
        private static int _button = -1;
        private static int _armedFrame = -1;
        private static int _expireFrame = -1;

        /// <summary>True while a press is armed (diagnostics only).</summary>
        public static bool Armed { get { return _armedFrame >= 0; } }

        public static int ArmedButton { get { return _button; } }

        public static void Install(HarmonyLib.Harmony harmony)
        {
            harmony.PatchAll(typeof(GetButtonDownPatch));
            harmony.PatchAll(typeof(GetInputDownPatch));
            harmony.PatchAll(typeof(GetInputLongPushPatch));
            Log.Info("InputInject: GetButtonDown / GetInputDown / GetInputLongPush 注入已安装（真实按键边沿）");
        }

        /// <summary>Arm one button-down edge on `monitor`, valid for the next few frames.</summary>
        public static void Press(int monitor, Manager.InputManager.ButtonSetting button)
        {
            _monitor = monitor;
            _button = (int)button;
            _armedFrame = UnityEngine.Time.frameCount;
            _expireFrame = _armedFrame + 3;
        }

        public static void Clear()
        {
            _armedFrame = -1;
            _expireFrame = -1;
            _button = -1;
        }

        [HarmonyPatch(typeof(Manager.InputManager), "GetButtonDown")]
        internal static class GetButtonDownPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ref bool __result, int monitorId, Manager.InputManager.ButtonSetting button)
            {
                try
                {
                    if (_armedFrame < 0) return;
                    int frame = UnityEngine.Time.frameCount;
                    if (frame > _expireFrame) { Clear(); return; }
                    if (__result) return;
                    if (monitorId != _monitor || (int)button != _button) return;

                    // Consume: a real GetButtonDown is an EDGE - return true for a single read only.
                    __result = true;
                    Clear();
                    Log.Info("[input] 注入按下 " + button + " monitor=" + monitorId + " frame=" + frame);
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// 区域选择页的左右移动用的是 `GetInputDown(monitorId, ButtonSetting, TouchPanelArea)`
        /// （InputUpdateState 里 Button03/A3 右、Button06/A6 左），不是 GetButtonDown ——
        /// 也要一起注入口，否则"移动到目标区"永远动不了，只能靠直调方法跳过界面。
        /// </summary>
        [HarmonyPatch(typeof(Manager.InputManager), "GetInputDown", new Type[] { typeof(int), typeof(Manager.InputManager.ButtonSetting), typeof(Manager.InputManager.TouchPanelArea) })]
        internal static class GetInputDownPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ref bool __result, int monitorId, Manager.InputManager.ButtonSetting button)
            {
                try
                {
                    if (_armedFrame < 0) return;
                    int frame = UnityEngine.Time.frameCount;
                    if (frame > _expireFrame) { Clear(); return; }
                    if (__result) return;
                    if (monitorId != _monitor || (int)button != _button) return;
                    __result = true;
                    Clear();
                    Log.Info("[input] 注入 GetInputDown " + button + " monitor=" + monitorId + " frame=" + frame);
                }
                catch (Exception) { }
            }
        }

        [HarmonyPatch(typeof(Manager.InputManager), "GetInputLongPush", new Type[] { typeof(int), typeof(Manager.InputManager.ButtonSetting), typeof(Manager.InputManager.TouchPanelArea), typeof(long) })]
        internal static class GetInputLongPushPatch
        {
            [HarmonyPostfix]
            // 注意：游戏源码里第一个参数拼错成 monitoId（少了 r），Harmony 按参数名绑定，必须同名。
            private static void Postfix(ref bool __result, int monitoId, Manager.InputManager.ButtonSetting button)
            {
                try
                {
                    if (_armedFrame < 0) return;
                    int frame = UnityEngine.Time.frameCount;
                    if (frame > _expireFrame) { Clear(); return; }
                    if (__result) return;
                    if (monitoId != _monitor || (int)button != _button) return;
                    __result = true;
                    Clear();
                    Log.Info("[input] 注入 GetInputLongPush " + button + " monitor=" + monitoId + " frame=" + frame);
                }
                catch (Exception) { }
            }
        }
    }
}
