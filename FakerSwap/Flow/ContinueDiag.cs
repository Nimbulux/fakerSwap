using System;
using System.Text;
using FakerSwap.Core;
using HarmonyLib;

namespace FakerSwap.Flow
{
    /// <summary>
    /// 续关屏诊断：**只观测，绝不注入**。
    ///
    /// 目的：操作员亲手按一次「是(4号)」，抓出游戏到底是从哪个方法、哪个输入位接受续关的。
    /// 之前一直靠猜（先 Button01 再 Button04），这次直接看真实调用链：
    ///   - UpdateInput / DetailInputOnDisp  每帧状态 + 输入快照
    ///   - ReLogin              ← 「是」被接受
    ///   - NextGameOverProcess  ← 「否」被接受
    ///
    /// 连续按 5 号键 = 否，所以本模块**不注入任何按键**，只是被动记录。
    /// </summary>
    public static class ContinueDiag
    {
        /// <summary>只打日志，默认开着。</summary>
        public static bool Enabled = true;

        private static double _lastDumpAt = -1000;
        private static string _lastSig = "";

        public static void Toggle()
        {
            Enabled = !Enabled;
            Log.Warn("[cdiag] ContinueDiag = " + Enabled + "（只观测，不注入按键）");
        }

        /// <summary>诊断静默期开关：操作员手动测试时用它把我们的注入全部关掉。</summary>
        public static bool InputSilenced;

        [HarmonyLib.HarmonyPatch(typeof(global::Process.ContinueProcess), "UpdateInput")]
        internal static class UpdateInputPatch
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(global::Process.ContinueProcess __instance)
            {
                if (!Enabled) return;
                try { Dump(__instance, "UpdateInput"); }
                catch (Exception) { }
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(global::Process.ContinueProcess), "DetailInputOnDisp")]
        internal static class DetailInputOnDispPatch
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(global::Process.ContinueProcess __instance)
            {
                if (!Enabled) return;
                try { Dump(__instance, "DetailInputOnDisp"); }
                catch (Exception) { }
            }
        }

        /// <summary>「是」路径 —— 真正走通的标志。</summary>
        [HarmonyLib.HarmonyPatch(typeof(global::Process.ContinueProcess), "ReLogin")]
        internal static class ReLoginPatch
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix()
            {
                Log.Warn("[cdiag] ★★★ 「是」被接受 → ContinueProcess.ReLogin() 调用");
            }
        }

        /// <summary>「否」路径 —— 5 号键会走到这里。</summary>
        [HarmonyLib.HarmonyPatch(typeof(global::Process.ContinueProcess), "NextGameOverProcess")]
        internal static class NextGameOverPatch
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix()
            {
                Log.Warn("[cdiag] 「否」被接受 → ContinueProcess.NextGameOverProcess() 调用");
            }
        }

        private static void Dump(global::Process.ContinueProcess p, string where)
        {
            string state, ready, counter, select;
            try { state = NavReflect.ToInt(NavReflect.Get(p, "_state")).ToString(); }
            catch (Exception) { state = "?"; }
            try { ready = NavReflect.ToBool(NavReflect.Get(p, "_isContinueReady")).ToString(); }
            catch (Exception) { ready = "?"; }
            try { counter = NavReflect.ToInt(NavReflect.Get(p, "_timeCounter")).ToString(); }
            catch (Exception) { counter = "?"; }
            try { select = NavReflect.ToInt(NavReflect.Get(p, "_selectIndex")).ToString(); }
            catch (Exception) { select = "-"; }

            string inputs = DescribeInputs();
            string sig = where + "|" + state + "|" + ready + "|" + counter + "|" + inputs;
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (sig == _lastSig) return;                       // 完全没变化就不刷屏
            if (now - _lastDumpAt < 0.2) return;               // 最多 5 条/秒
            _lastSig = sig;
            _lastDumpAt = now;
            Log.Info("[cdiag] " + where + " state=" + state + " ready=" + ready +
                     " counter=" + counter + " select=" + select + " " + inputs);
        }

        private static string DescribeInputs()
        {
            var sb = new StringBuilder();
            try
            {
                var t = typeof(Manager.InputManager);
                sb.Append("Push=");
                AppendTrue(sb, AccessTools.Field(t, "PlayerButtonPush")?.GetValue(null) as Array);
                sb.Append(" Down=");
                AppendTrue(sb, AccessTools.Field(t, "PlayerButtonDown")?.GetValue(null) as Array);
                sb.Append(" Time=");
                AppendTrue(sb, AccessTools.Field(t, "PlayerButtonPushTime")?.GetValue(null) as Array);
                sb.Append(" GDown=");
                AppendTrue(sb, AccessTools.Field(t, "GameButtonDown")?.GetValue(null) as Array);
            }
            catch (Exception e) { sb.Append("err:" + e.Message); }
            return sb.ToString();
        }

        private static void AppendTrue(StringBuilder sb, Array a)
        {
            if (a == null) { sb.Append("null"); return; }
            sb.Append('[');
            if (a is bool[,] m2)
            {
                for (int i = 0; i < m2.GetLength(0); i++)
                    for (int j = 0; j < m2.GetLength(1); j++)
                        if (m2[i, j]) sb.Append(i + ":" + (j + 1) + " ");   // 按钮下标从 1 开始，方便对照
                sb.Append(']');
                return;
            }
            if (a is bool[,,] m3)
            {
                for (int i = 0; i < m3.GetLength(0); i++)
                    for (int j = 0; j < m3.GetLength(1); j++)
                        for (int k = 0; k < m3.GetLength(2); k++)
                            if (m3[i, j, k]) sb.Append(i + "/" + j + ":" + (k + 1) + " ");
                sb.Append(']');
                return;
            }
            if (a is long[,] l2)
            {
                for (int i = 0; i < l2.GetLength(0); i++)
                    for (int j = 0; j < l2.GetLength(1); j++)
                        if (l2[i, j] > 0) sb.Append(i + ":" + (j + 1) + "(" + l2[i, j] + ") ");
                sb.Append(']');
                return;
            }
            sb.Append("type?" + a.GetType().Name + "]");
        }
    }
}
