using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FakerSwap.Core;
using Manager;
using Process;
using UnityEngine;

namespace FakerSwap.Flow
{
    /// <summary>
    /// 当前进程栈 / ProcessDataContainer 的访问层。
    ///
    /// 为什么不用 flow-dev 的 ProcessHooks：导航层要能在 RebirthFlow.Tick 之外被调用（例如
    /// 启动自检/调试），而且进程识别是"当前在哪个界面"最硬的证据。这里自己维护一份快照，
    /// 与 GameStageTracker 互不依赖，任何一边坏了另一边仍能工作。
    /// </summary>
    internal static class NavProcess
    {
        private static ProcessDataContainer _container;
        private static ProcessManager _manager;
        private static double _nextResolveAttempt;
        private static bool _patchedContainer;

        public static void SetContainer(ProcessDataContainer container)
        {
            if (container == null) return;
            _container = container;
            try { _manager = container.processManager; } catch (Exception) { }
        }

        public static void MarkContainerPatchInstalled() { _patchedContainer = true; }

        public static ProcessDataContainer Container
        {
            get
            {
                if (_container != null) return _container;
                ResolveFallback();
                return _container;
            }
        }

        public static ProcessManager Manager
        {
            get
            {
                if (_manager != null) return _manager;
                var c = Container;
                if (c != null) { try { _manager = c.processManager; } catch (Exception) { } }
                return _manager;
            }
        }

        /// <summary>Bootstrap without any patch: GameMainObject.main._container.</summary>
        private static void ResolveFallback()
        {
            double now = Time.realtimeSinceStartup;
            if (now < _nextResolveAttempt) return;
            _nextResolveAttempt = now + 2.0;
            try
            {
                var gmo = UnityEngine.Object.FindObjectOfType<Main.GameMainObject>();
                if (gmo == null) return;
                var main = NavReflect.Get(gmo, "main");
                if (main == null) return;
                var c = NavReflect.Get(main, "_container") as ProcessDataContainer;
                var m = NavReflect.Get(main, "_processManager") as ProcessManager;
                if (c != null) { _container = c; _manager = m ?? c.processManager; }
                else if (m != null) _manager = m;
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("container-fallback", "解析 ProcessDataContainer 失败: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ process list

        /// <summary>
        /// Snapshot of the active process stack, top-most last.  Never throws.
        /// 每次都新建 List（不共用静态缓冲），避免调用方在遍历时又嵌套调用 Find/Any 把缓冲清掉。
        /// </summary>
        public static List<ProcessBase> Snapshot()
        {
            var result = new List<ProcessBase>();
            try
            {
                var manager = Manager;
                if (manager == null) return result;
                var list = NavReflect.Get(manager, "_processList") as IEnumerable;
                if (list == null) return result;
                foreach (var control in list)
                {
                    if (control == null) continue;
                    var process = NavReflect.Get(control, "Process") as ProcessBase;
                    if (process != null) result.Add(process);
                }
            }
            catch (Exception e)
            {
                NavReflect.WarnOnce("snapshot", "枚举进程栈失败: " + e.Message);
            }
            return result;
        }

        public static T Find<T>() where T : ProcessBase
        {
            var list = Snapshot();
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i] as T;
                if (t != null) return t;
            }
            return null;
        }

        public static bool Any<T>() where T : ProcessBase { return Find<T>() != null; }

        /// <summary>Find the first process whose concrete type name matches one of <paramref name="typeNames"/>.</summary>
        public static ProcessBase FindByName(params string[] typeNames)
        {
            var list = Snapshot();
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                var n = p.GetType().Name;
                for (int k = 0; k < typeNames.Length; k++)
                    if (n == typeNames[k]) return p;
            }
            return null;
        }

        public static bool HasName(params string[] typeNames) { return FindByName(typeNames) != null; }

        /// <summary>Names of the current stack, for <see cref="Navigation.Describe"/> / logs.</summary>
        public static string StackNames()
        {
            var list = Snapshot();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null) continue;
                if (sb.Length > 0) sb.Append(" > ");
                sb.Append(list[i].GetType().Name);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ manipulation

        public static ProcessDataContainer ContainerOf(ProcessBase process)
        {
            if (process == null) return null;
            var c = NavReflect.Get(process, "container") as ProcessDataContainer;
            if (c != null) { SetContainer(c); return c; }
            return Container;
        }

        public static bool Add(ProcessBase process, byte priority)
        {
            if (process == null) return false;
            var manager = Manager;
            if (manager == null) { NavReflect.WarnOnce("add-nomanager", "ProcessManager 不可用，无法 AddProcess"); return false; }
            try
            {
                uint id = manager.AddProcess(process, priority);
                Log.Info("[nav] AddProcess " + process.GetType().Name + " prio=" + priority + " id=" + id);
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[nav] AddProcess " + process.GetType().Name + " 失败", e);
                return false;
            }
        }

        public static bool Release(ProcessBase process)
        {
            if (process == null) return false;
            var manager = Manager;
            if (manager == null) return false;
            try
            {
                manager.ReleaseProcess(process);
                Log.Info("[nav] ReleaseProcess " + process.GetType().Name);
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[nav] ReleaseProcess " + process.GetType().Name + " 失败", e);
                return false;
            }
        }

        /// <summary>Fade from one process into another (the way the game itself transitions screens).</summary>
        public static bool Fade(ProcessBase from, ProcessBase to, string reason)
        {
            if (to == null) return false;
            var container = ContainerOf(from) ?? Container;
            if (container == null) { NavReflect.WarnOnce("fade-nocontainer", "ProcessDataContainer 不可用，无法 Fade"); return false; }
            try
            {
                var fade = new FadeProcess(container, from, to);
                container.processManager.AddProcess(fade, 50);
                Log.Info("[nav] Fade " + (from == null ? "(null)" : from.GetType().Name) + " -> " +
                         to.GetType().Name + " (" + reason + ")");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[nav] Fade -> " + to.GetType().Name + " 失败 (" + reason + ")", e);
                return false;
            }
        }

        /// <summary>Force the currently running process (with <paramref name="processType"/>) to end.</summary>
        public static int IndexOf(ProcessBase process)
        {
            var list = Snapshot();
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], process)) return i;
            return -1;
        }
    }
}
