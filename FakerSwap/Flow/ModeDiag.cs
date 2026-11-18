using System;
using System.Text;
using FakerSwap.Core;
using HarmonyLib;
using Manager;

namespace FakerSwap.Flow
{
    /// <summary>
    /// Black-box capture for MODE SELECT.
    ///
    /// The game's method bodies are encrypted (`Invalid MethodBodyBlock`), so the only way to learn
    /// what the 決定ボタン prompt actually waits for is to watch the public flags while a human
    /// presses it.  Enable with the `modediag` command, walk to MODE SELECT manually and press the
    /// button; every change of the state snapshot is logged.
    /// </summary>
    public static class ModeDiag
    {
        public static bool Active;

        private static string _last;
        private static double _lastWarnAt = -1000;

        public static void Toggle()
        {
            Active = !Active;
            _last = null;
            Log.Info("[modediag] " + (Active
                ? "开始捕获 MODE SELECT 状态 —— 请手动走到 MODE SELECT 并按一次「決定」按钮"
                : "停止捕获"));
        }

        private static bool B(object instance, string field)
        {
            try
            {
                object v = Traverse.Create(instance).Field(field).GetValue();
                return v is bool b && b;
            }
            catch (Exception) { return false; }
        }

        private static int I(object instance, string field)
        {
            try
            {
                object v = Traverse.Create(instance).Field(field).GetValue();
                if (v == null) return -999;
                return Convert.ToInt32(v);
            }
            catch (Exception) { return -999; }
        }

        public static void Tick(object modeSelect)
        {
            if (!Active || modeSelect == null) return;
            try
            {
                var sb = new StringBuilder(256);
                sb.Append("t=").Append(UnityEngine.Time.realtimeSinceStartup.ToString("0.00"));
                sb.Append(" isOK=").Append(B(modeSelect, "_isOK"));
                sb.Append(" checkOK=").Append(B(modeSelect, "_isCheckOK"));
                sb.Append(" enableOK=").Append(B(modeSelect, "_isEnableOK"));
                sb.Append(" decidedOK=").Append(B(modeSelect, "_isDecidedOK"));
                sb.Append(" checkLR=").Append(B(modeSelect, "_isCheckLR"));
                sb.Append(" state=").Append(I(modeSelect, "_state"));
                sb.Append(" monState=").Append(I(modeSelect, "_monitor_state"));
                sb.Append(" monTimer=").Append(I(modeSelect, "_monitor_timer"));
                sb.Append(" entryNum=").Append(I(modeSelect, "_isEntryNum"));
                sb.Append(" decidedEntryNum=").Append(I(modeSelect, "_isDecidedEntryNum"));
                sb.Append(" activeMode=").Append(I(modeSelect, "_active_mode"));
                sb.Append(" modeType=").Append(I(modeSelect, "_decidedSelectModeType"));
                sb.Append(" creditCur=").Append(I(modeSelect, "_creditCurrent"));
                sb.Append(" creditCost=").Append(I(modeSelect, "_creditCost"));
                sb.Append(" down=");
                for (int b = 0; b <= 9; b++)
                    sb.Append(Manager.InputManager.GetButtonDown(0, (InputManager.ButtonSetting)b) ? '1' : '0');
                sb.Append(" push=");
                for (int b = 0; b <= 9; b++)
                    sb.Append(Manager.InputManager.GetButtonPush(0, (InputManager.ButtonSetting)b) ? '1' : '0');
                string line = sb.ToString();
                if (line != _last)
                {
                    _last = line;
                    Log.Info("[modediag] " + line);
                }
            }
            catch (Exception e)
            {
                double now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _lastWarnAt > 5.0)
                {
                    _lastWarnAt = now;
                    Log.Warn("[modediag] 采集失败: " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(Process.ModeSelect.ModeSelectProcess), "OnUpdate")]
        internal static class Patch
        {
            [HarmonyPostfix]
            private static void Postfix(Process.ModeSelect.ModeSelectProcess __instance)
            {
                Tick(__instance);
            }
        }
    }
}
