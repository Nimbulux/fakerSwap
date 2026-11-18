using System;
using System.Collections.Generic;
using System.Reflection;
using FakerSwap.Core;
using HarmonyLib;
using Manager;
using Process;

namespace FakerSwap.Flow
{
    /// <summary>
    /// Process-level plumbing shared by the whole flow layer.
    ///
    /// Responsibilities:
    ///   * capture the one and only <see cref="ProcessDataContainer"/> (patched ctor) so any
    ///     module can add/release processes without having to be inside a process itself;
    ///   * expose the live process list (<c>ProcessManager._processList</c>) and the current
    ///     top-most process;
    ///   * probe the reflection targets we rely on and log what is missing instead of crashing.
    ///
    /// Nothing in here changes game behaviour - the stage mapping lives in <see cref="GameStagePump"/>.
    /// </summary>
    public static class ProcessHooks
    {
        // ---------------------------------------------------------------- captured state

        public static ProcessDataContainer Container { get; private set; }
        private static global::Manager.ProcessManager _manager;
        private static bool _managerWarned;
        private static bool _listWarned;

        /// <summary>
        /// The live ProcessManager.  <c>ProcessDataContainer.processManager</c> is assigned *after*
        /// the container constructor returns, so this is resolved lazily on first use.
        /// </summary>
        public static global::Manager.ProcessManager Manager
        {
            get
            {
                if (_manager != null) return _manager;
                var c = Container;
                if (c == null) return null;
                try
                {
                    _manager = c.processManager;
                }
                catch (Exception e)
                {
                    if (!_managerWarned)
                    {
                        _managerWarned = true;
                        Log.Warn("ProcessHooks: container.processManager read failed: " + e.Message);
                    }
                }
                if (_manager == null)
                {
                    try
                    {
                        _manager = Traverse.Create(c).Field("processManager").GetValue<global::Manager.ProcessManager>();
                    }
                    catch (Exception) { }
                }
                if (_manager != null) Log.Info("ProcessHooks: processManager resolved lazily");
                return _manager;
            }
        }

        public static ProcessBase CurrentProcess { get; private set; }
        public static string CurrentProcessName { get; private set; } = "";
        public static string PreviousProcessName { get; private set; } = "";

        /// <summary>Degraded-mode fallback: last process pushed onto the stack.</summary>
        public static ProcessBase LastAddedProcess { get; private set; }
        public static string LastAddedProcessName { get; private set; } = "";

        public static bool Ready => Container != null && Manager != null;

        private static HarmonyLib.Harmony _harmony;
        private static FieldInfo _processListField;
        private static bool _missingListLogged;
        private static double _lastCaptureLogAt = -100;
        private static int _addCount;
        private static int _releaseCount;

        // ---------------------------------------------------------------- install

        public static void Install(HarmonyLib.Harmony harmony)
        {
            _harmony = harmony;

            // The container is created exactly once, very early.  Patching the ctor is how
            // AquaMai (SharedInstances.ProcessDataContainer) gets at it and it works on 1.65.
            var ctor = AccessTools.Constructor(typeof(ProcessDataContainer), System.Type.EmptyTypes);
            if (ctor != null)
            {
                HarmonyMethod post = new HarmonyMethod(AccessTools.Method(typeof(ProcessHooks), nameof(PostContainerCtor)));
                try
                {
                    harmony.Patch(ctor, postfix: post);
                    Log.Info("ProcessHooks: patched ProcessDataContainer..ctor");
                }
                catch (Exception e) { Log.Error("ProcessHooks: container ctor patch failed", e); }
            }
            else
            {
                Log.Warn("ProcessHooks: ProcessDataContainer..ctor not found");
            }

            // Fallback: every ProcessBase ctor receives the container too.
            var baseCtor = AccessTools.Constructor(typeof(ProcessBase), new[] { typeof(ProcessDataContainer) });
            if (baseCtor != null)
            {
                try
                {
                    harmony.Patch(baseCtor, postfix: new HarmonyMethod(AccessTools.Method(typeof(ProcessHooks), nameof(PostProcessBaseCtor))));
                    Log.Info("ProcessHooks: patched ProcessBase..ctor");
                }
                catch (Exception e) { Log.Warn("ProcessHooks: ProcessBase ctor patch failed: " + e.Message); }
            }

            // Observability: log every process push/pop when Verbose is on.  Also keeps a recent
            // list so the stage pump can fall back if the linked list layout ever changes.
            PatchManager("AddProcess", nameof(PostAddProcess));
            PatchManager("ReleaseProcess", nameof(PostReleaseProcess));
            PatchManager("Update", nameof(PostManagerUpdate));

            // Reflection probe: make "what is available in this build" explicit in the log.
            Probe();
        }

        private static void PatchManager(string methodName, string postfixName)
        {
            if (_harmony == null) return;
            var m = AccessTools.Method(typeof(global::Manager.ProcessManager), methodName);
            if (m == null) { Log.Warn("ProcessHooks: ProcessManager." + methodName + " not found"); return; }
            try
            {
                _harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(ProcessHooks), postfixName)));
            }
            catch (Exception e)
            {
                Log.Warn("ProcessHooks: patch ProcessManager." + methodName + " failed: " + e.Message);
            }
        }

        private static void Probe()
        {
            ProbeField(typeof(ProcessDataContainer), "processManager");
            ProbeField(typeof(global::Manager.ProcessManager), "_processList");
            ProbeMethod(typeof(global::Manager.ProcessManager), "ForceTimeUp");
            ProbeMethod(typeof(global::Manager.ProcessManager), "PrepareTimer");
            ProbeMethod(typeof(global::Manager.NetDataManager), "SetLoginVO");
            ProbeMethod(typeof(Process.DataSaveProcess), "OnStart");
            ProbeMethod(typeof(Process.EntryProcess), "DecrementTimerSecond");
            ProbeMethod(typeof(Process.ModeSelect.ModeSelectProcess), "TimeUp");
            ProbeMethod(typeof(Process.RegionalSelectProcess), "TimeUp");
            ProbeMethod(typeof(Process.ContinueProcess), "ReLogin");
            ProbeMethod(typeof(Process.GameProcess), "SetRelease");
            ProbeMethod(typeof(Process.GameProcess), "UpdateSubbMonitorData");
            ProbeMethod(typeof(Process.MusicSelectProcess), "GameStart");
        }

        private static void ProbeField(System.Type t, string name)
        {
            var f = AccessTools.Field(t, name);
            if (f == null) Log.Warn("probe: field " + t.Name + "." + name + " MISSING");
            else Log.Info("probe: field " + t.Name + "." + name + " ok (" + f.FieldType.Name + ")");
        }

        private static void ProbeMethod(System.Type t, string name)
        {
            var m = AccessTools.Method(t, name);
            if (m == null) Log.Warn("probe: method " + t.Name + "." + name + " MISSING");
            else Log.Info("probe: method " + t.Name + "." + name + " ok");
        }

        // ---------------------------------------------------------------- patches

        private static void PostContainerCtor(ProcessDataContainer __instance)
        {
            try
            {
                Capture(__instance);
            }
            catch (Exception e) { Log.Error("ProcessHooks: container capture failed", e); }
        }

        private static void PostProcessBaseCtor(ProcessDataContainer dataContainer)
        {
            try
            {
                if (Container == null) Capture(dataContainer);
            }
            catch (Exception e) { Log.Error("ProcessHooks: base ctor capture failed", e); }
        }

        private static void PostAddProcess(ProcessBase process)
        {
            try
            {
                _addCount++;
                if (process != null)
                {
                    LastAddedProcess = process;
                    LastAddedProcessName = process.GetType().FullName ?? "";
                }
                if (Container == null && process != null)
                {
                    var c = Traverse.Create(process).Field("container").GetValue<ProcessDataContainer>();
                    Capture(c);
                }
                if (ModConfig.Data.Verbose && process != null)
                    Log.Info("[proc+] " + process.GetType().FullName + " (adds=" + _addCount + ")");
            }
            catch (Exception e) { Log.Error("ProcessHooks: AddProcess postfix failed", e); }
        }

        /// <summary>Fallback manager capture: ProcessManager.Update is called every frame.</summary>
        private static void PostManagerUpdate(global::Manager.ProcessManager __instance)
        {
            try
            {
                if (__instance == null || ReferenceEquals(_manager, __instance)) return;
                _manager = __instance;
                Log.Info("ProcessHooks: processManager captured from ProcessManager.Update");
            }
            catch (Exception e) { Log.Error("ProcessHooks: manager capture failed", e); }
        }

        private static void PostReleaseProcess(ProcessBase process)
        {
            try
            {
                _releaseCount++;
                if (ModConfig.Data.Verbose && process != null)
                    Log.Info("[proc-] " + process.GetType().FullName);
            }
            catch (Exception e) { Log.Error("ProcessHooks: ReleaseProcess postfix failed", e); }
        }

        private static void Capture(ProcessDataContainer container)
        {
            if (container == null) return;
            Container = container;
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastCaptureLogAt > 1)
            {
                _lastCaptureLogAt = now;
                Log.Info("ProcessHooks: container captured (processManager=" + (container.processManager != null) + ")");
            }
        }

        // ---------------------------------------------------------------- process list

        /// <summary>Live process list (newest entries last), or null when unavailable.</summary>
        public static LinkedList<global::Manager.ProcessManager.ProcessControle> GetProcessList()
        {
            if (Manager == null)
            {
                if (!_listWarned)
                {
                    _listWarned = true;
                    Log.Warn("ProcessHooks: processManager not available yet - stage tracking waits for ProcessManager.Update");
                }
                return null;
            }
            if (_processListField == null)
            {
                _processListField = AccessTools.Field(typeof(global::Manager.ProcessManager), "_processList");
                if (_processListField == null && !_missingListLogged)
                {
                    _missingListLogged = true;
                    Log.Error("ProcessHooks: ProcessManager._processList not found - stage tracking disabled");
                }
            }
            if (_processListField == null) return null;
            try
            {
                return _processListField.GetValue(Manager) as LinkedList<global::Manager.ProcessManager.ProcessControle>;
            }
            catch (Exception e)
            {
                Log.Error("ProcessHooks: reading _processList failed", e);
                return null;
            }
        }

        /// <summary>Called by the stage pump every frame once it picked the active process.</summary>
        internal static void SetCurrent(ProcessBase process, string typeName)
        {
            if (ReferenceEquals(process, CurrentProcess) && typeName == CurrentProcessName) return;
            if (CurrentProcessName != typeName)
            {
                PreviousProcessName = CurrentProcessName;
                CurrentProcessName = typeName ?? "";
            }
            CurrentProcess = process;
        }

        /// <summary>Find a live process of the given full type name (or a subclass of it).</summary>
        public static ProcessBase FindProcess(string fullTypeName)        {
            var list = GetProcessList();
            if (list == null || fullTypeName == null) return null;
            for (var node = list.Last; node != null; node = node.Previous)
            {
                var ctrl = node.Value;
                if (ctrl == null || ctrl.Process == null) continue;
                if (ctrl.Process.GetType().FullName == fullTypeName) return ctrl.Process;
            }
            return null;
        }

        public static bool IsStageProcess(GameStage stage)
        {
            return GameStagePump.Stage == stage;
        }

        /// <summary>Newest still-alive, non-transitional process.  Used when a cached reference may be stale.</summary>
        public static ProcessBase NewestLiveProcess()
        {
            var list = GetProcessList();
            if (list == null) return null;
            for (var node = list.Last; node != null; node = node.Previous)
            {
                var ctrl = node.Value;
                if (ctrl == null || ctrl.Process == null) continue;
                if (ctrl.State.ToString() == "Released") continue;
                string name = ctrl.Process.GetType().FullName ?? "";
                if (GameStagePump.IsTransitional(name)) continue;
                return ctrl.Process;
            }
            return null;
        }
    }
}

