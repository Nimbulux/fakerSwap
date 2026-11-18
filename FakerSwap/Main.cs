using System;
using MelonLoader;
using FakerSwap.Core;
using FakerSwap.Data;
using FakerSwap.Flow;
using FakerSwap.Network;
using FakerSwap.UI;

namespace FakerSwap
{
    public class FakerSwapMod : MelonMod
    {
        public static FakerSwapMod Instance { get; private set; }
        public static HarmonyLib.Harmony Patcher { get; private set; }

        public override void OnInitializeMelon()
        {
            Instance = this;
            Patcher = HarmonyInstance;

            Log.Init();
            Log.Info("FakerSwap " + BuildInfo.Version + " | MelonLoader " +
                     typeof(MelonMod).Assembly.GetName().Version + " | game dir " + Environment.CurrentDirectory);

            Fs.EnsureDirs();
            ModConfig.Load();
            Log.Info("config: WriteMode=" + ModConfig.Data.WriteMode + " PlayMode=" + ModConfig.Data.PlayMode +
                     " batch=" + ModConfig.Data.BatchSize + " levels=" + ModConfig.Data.MinLevel + ".." + ModConfig.Data.MaxLevel);

            try { CrashFixes.Install(Patcher); }
            catch (Exception e) { Log.Error("CrashFixes install failed", e); }
            try { HarmonyHooks.Install(Patcher); }
            catch (Exception e) { Log.Error("Hook install failed", e); }

            try { UIHost.Initialize(); }
            catch (Exception e) { Log.Error("UI init failed", e); }

            try { RebirthFlow.Initialize(); }
            catch (Exception e) { Log.Error("Flow init failed", e); }

            Log.Info("FakerSwap initialised.");
            Log.Info("紧急脱出: 按住 " + EmergencyExit.EscapeKey + " 3 秒可强制中止任务并解锁机台。窗口热键 F8 显示/隐藏。");
        }

        public override void OnUpdate()
        {
            try
            {
                MainThread.Drain(128);
                Scheduler.Tick();
                EmergencyExit.Tick();
                CommandFile.Tick();
                GameStagePump.Tick();
                RebirthFlow.Tick();
                UIHost.Tick();
            }
            catch (Exception e)
            {
                Log.Error("OnUpdate failed", e);
            }
        }

        public override void OnGUI()
        {
            try { UIHost.OnGUI(); }
            catch (Exception e) { Log.Error("OnGUI failed", e); }
        }

        public override void OnApplicationQuit()
        {
            try { RebirthFlow.Shutdown("application quit"); }
            catch (Exception e) { Log.Error("flow shutdown failed", e); }
            try { UIHost.Shutdown(); }
            catch (Exception e) { Log.Error("ui shutdown failed", e); }
            MachineLock.Off();
            Log.Info("FakerSwap quit.");
        }
    }
}
