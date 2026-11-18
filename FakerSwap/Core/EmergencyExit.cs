using System;
using UnityEngine;

namespace FakerSwap.Core
{
    /// <summary>
    /// Last-resort escape hatch.  If the flow ever wedges while the cabinet is locked, the operator
    /// can hold <see cref="EscapeKey"/> for a few seconds to abort the task and release the lock.
    /// Runs on the Unity main thread and bypasses the mod's own InputLock (which only patches
    /// Manager.InputManager, not UnityEngine.Input).
    /// </summary>
    public static class EmergencyExit
    {
        public const KeyCode EscapeKey = KeyCode.End;
        public const float HoldSeconds = 3f;

        private static float _held;

        public static void Tick()
        {
            try
            {
                if (!Input.GetKey(EscapeKey))
                {
                    _held = 0f;
                    return;
                }
                _held += Time.unscaledDeltaTime;
                if (_held < HoldSeconds) return;
                _held = 0f;

                Log.Warn("EMERGENCY: operator held " + EscapeKey + " for " + HoldSeconds +
                         "s - releasing cabinet lock and aborting the task");
                var wasLocked = MachineLock.Active;
                var wasBusy = FakerSwap.Flow.RebirthFlow.IsBusy;
                try { MachineLock.Off(); } catch (Exception) { }
                if (wasBusy)
                {
                    try { FakerSwap.Flow.RebirthFlow.Abort(); }
                    catch (Exception e) { Log.Error("EMERGENCY: abort failed", e); }
                }
                Log.Warn("EMERGENCY: done (wasLocked=" + wasLocked + ", wasBusy=" + wasBusy + ")");
            }
            catch (Exception e)
            {
                Log.Error("EmergencyExit.Tick failed", e);
            }
        }
    }
}
