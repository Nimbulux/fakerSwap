using System;

namespace FakerSwap.Core
{
    /// <summary>
    /// The "this cabinet is occupied" flag.  While a task owns the cabinet every menu input is
    /// swallowed and the overlay/banner stays on screen.
    /// </summary>
    public static class MachineLock
    {
        private static readonly object Sync = new object();
        private static bool _active;
        private static string _reason = "";
        private static string _detail = "";
        private static double _since;

        public static event Action Changed;

        public static bool Active { get { lock (Sync) return _active; } }
        public static string Reason { get { lock (Sync) return _reason; } }
        public static string Detail { get { lock (Sync) return _detail; } }
        public static double Since { get { lock (Sync) return _since; } }

        /// <summary>One-line text meant for the in-game banner.</summary>
        public static string NoticeText
        {
            get
            {
                lock (Sync)
                {
                    if (!_active) return "";
                    var s = string.IsNullOrEmpty(_detail) ? _reason : _reason + " - " + _detail;
                    return "FakerSwap 占用中  |  " + s;
                }
            }
        }

        public static void On(string reason, string detail = "")
        {
            bool changed;
            lock (Sync)
            {
                changed = !_active || _reason != reason || _detail != detail;
                _active = true;
                _reason = reason ?? "";
                _detail = detail ?? "";
                _since = UnityEngine.Time.realtimeSinceStartup;
            }
            if (changed) Fire();
            Log.Info("machine lock ON: " + reason + (string.IsNullOrEmpty(detail) ? "" : " / " + detail));
        }

        public static void Detail_(string detail)
        {
            bool changed;
            lock (Sync)
            {
                changed = _active && _detail != detail;
                _detail = detail ?? "";
            }
            if (changed) Fire();
        }

        public static void Off()
        {
            bool was;
            lock (Sync)
            {
                was = _active;
                _active = false;
                _reason = "";
                _detail = "";
            }
            if (was)
            {
                Log.Info("machine lock OFF");
                Fire();
            }
        }

        private static void Fire()
        {
            var h = Changed;
            if (h == null) return;
            try { h(); }
            catch (Exception e) { Log.Error("MachineLock.Changed handler failed", e); }
        }
    }
}
