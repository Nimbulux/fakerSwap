using System;
using System.Collections;
using System.Reflection;
using System.Text;
using FakerSwap.Core;
using HarmonyLib;

namespace FakerSwap.Core
{
    /// <summary>
    /// Pure diagnostic for the "continue" crash.
    ///
    /// Right after a successful CONTINUE the next credit's first track dies inside
    /// `GameProcess.OnStart` with a NullReferenceException.  The assembly's method bodies are
    /// encrypted (`Invalid MethodBodyBlock`), so the only way to find the missing dependency is to
    /// dump the process and the GamePlayManager right before the vanilla body runs and compare a
    /// healthy start with the crashing one.
    /// </summary>
    public static class GameProbe
    {
        /// <summary>Set to false to stop the per-track dump.</summary>
        public static bool Enabled = false;   // OFF by default: the per-track reflection dump is heavy

        private static int _count;

        [HarmonyPatch(typeof(Process.GameProcess), "OnStart")]
        internal static class OnStartProbe
        {
            [HarmonyPrefix]
            private static void Prefix(Process.GameProcess __instance)
            {
                if (!Enabled) return;
                try
                {
                    if (__instance == null) { Log.Warn("[gprobe] instance null"); return; }
                    if (_count >= 400) return;
                    _count++;
                    if (_count == 400) Log.Warn("[gprobe] 已达 400 条，后续不再打印");

                    var sb = new StringBuilder(256);
                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    foreach (var fi in typeof(Process.GameProcess).GetFields(flags))
                        sb.Append(' ').Append(fi.Name).Append('=').Append(Describe(fi.GetValue(__instance)));
                    Log.Info("[gprobe] GameProcess.OnStart:" + sb);

                    ProbeGamePlayManager();
                }
                catch (Exception e)
                {
                    Log.Warn("[gprobe] 探测失败: " + e.Message);
                }
            }
        }

        private static string Describe(object v)
        {
            try
            {
                if (v == null) return "null";
                if (v is Array arr) return v.GetType().Name + "[" + arr.Length + "]";
                return v.GetType().Name;
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>
        /// GamePlayManager owns the score lists the process reads on start; a continue may leave
        /// them empty, which is the most likely source of the NullReferenceException.
        /// </summary>
        private static void ProbeGamePlayManager()
        {
            try
            {
                var mgrType = Type.GetType("Manager.GamePlayManager, Assembly-CSharp");
                if (mgrType == null) { Log.Warn("[gprobe] GamePlayManager 类型未找到"); return; }
                object inst = null;
                try
                {
                    var closed = typeof(MAI2.Util.Singleton<>).MakeGenericType(mgrType);
                    var prop = closed.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (prop != null) inst = prop.GetValue(null);
                }
                catch (Exception) { }
                if (inst == null) { Log.Warn("[gprobe] GamePlayManager.Instance = null"); return; }

                var sb = new StringBuilder(200);
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var fi in mgrType.GetFields(flags))
                {
                    string val;
                    try
                    {
                        object v = fi.GetValue(inst);
                        if (v == null) val = "null";
                        else if (v is string s) val = s;
                        else if (v is IEnumerable en)
                        {
                            int n = 0;
                            foreach (var unused in en) { n++; if (n > 99) break; }
                            val = v.GetType().Name + "(" + n + ")";
                        }
                        else val = v.ToString();
                    }
                    catch (Exception) { val = "?"; }
                    sb.Append(' ').Append(fi.Name).Append('=').Append(val);
                }
                Log.Info("[gprobe] GamePlayManager:" + sb);
            }
            catch (Exception e)
            {
                Log.Warn("[gprobe] GamePlayManager 探测失败: " + e.Message);
            }
        }
    }
}
