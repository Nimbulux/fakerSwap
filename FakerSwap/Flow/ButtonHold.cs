using System;
using System.Reflection;
using FakerSwap.Core;
using HarmonyLib;
using Manager;

namespace FakerSwap.Flow
{
    /// <summary>
    /// Simulates a HELD cabinet button.
    ///
    /// `InputManager.SetButtonDown` only raises a one-frame edge, but several screens - notably
    /// MODE SELECT's 決定ボタン prompt - only commit after `GetButtonLongPush()`, i.e. after the
    /// button has stayed down for a while.  `InputManager` recomputes its (private, static) button
    /// arrays inside `UpdateAmInput()`, so the write has to happen in a postfix of that method,
    /// otherwise the very next frame wipes it.
    /// </summary>
    public static class ButtonHold
    {
        public static bool Active;
        public static int Monitor;
        public static int Button = -1;
        public static int Frames;
        public static int FramesDone;

        private static int _startTick;
        /// <summary>最近一次 Press() 的挂钟时间：调用一旦停止超过 <see cref="StaleMs"/> 就自动松开。</summary>
        private static int _lastCallTick;
        private const int StaleMs = 300;
        private static bool _looked;
        private static FieldInfo _pushField;
        private static FieldInfo _downField;
        private static FieldInfo _timeField;
        private static FieldInfo _gamePushField;
        private static FieldInfo _gameDownField;
        private static FieldInfo _anyPushField;

        private static void EnsureFields()
        {
            if (_looked) return;
            _looked = true;
            var t = typeof(Manager.InputManager);
            _pushField = AccessTools.Field(t, "PlayerButtonPush");
            _downField = AccessTools.Field(t, "PlayerButtonDown");
            _timeField = AccessTools.Field(t, "PlayerButtonPushTime");
            _gamePushField = AccessTools.Field(t, "GameButtonPush");
            _gameDownField = AccessTools.Field(t, "GameButtonDown");
            _anyPushField = AccessTools.Field(t, "AnyButtonPush");
            try
            {
                var gp = _gamePushField != null ? _gamePushField.GetValue(null) as Array : null;
                var pp = _pushField != null ? _pushField.GetValue(null) as Array : null;
                Log.Info("[hold] 数组维度: PlayerButtonPush=" + (pp == null ? "null" : pp.GetLength(0) + "x" + pp.GetLength(1)) +
                         " GameButtonPush=" + (gp == null ? "null" : gp.GetLength(0) + "x" + gp.GetLength(1) + "x" + gp.GetLength(2)));
            }
            catch (Exception) { }
            if (_pushField == null)
                Log.Warn("[hold] InputManager.PlayerButtonPush 找不到，长按注入不可用");
        }

        /// <summary>Hold `button` on `monitor` for `frames` frames (≈ frames / 60 seconds).</summary>
        public static void Press(int monitor, Manager.InputManager.ButtonSetting button, int frames = 90)
        {
            EnsureFields();
            Monitor = monitor;
            Button = (int)button;
            Frames = Math.Max(2, frames);
            FramesDone = 0;
            _startTick = Environment.TickCount;
            _lastCallTick = _startTick;
            Active = true;
            Log.Info("[hold] 开始长按 " + button + " monitor=" + monitor + " frames=" + Frames);
        }

        public static void Cancel()
        {
            if (!Active) return;
            Active = false;
            try
            {
                var push = _pushField != null ? _pushField.GetValue(null) as bool[,] : null;
                var down = _downField != null ? _downField.GetValue(null) as bool[,] : null;
                if (push != null && Monitor >= 0 && Monitor < push.GetLength(0) &&
                    Button >= 0 && Button < push.GetLength(1)) push[Monitor, Button] = false;
                if (down != null && Monitor >= 0 && Monitor < down.GetLength(0) &&
                    Button >= 0 && Button < down.GetLength(1)) down[Monitor, Button] = false;
            }
            catch (Exception) { }
            Log.Info("[hold] 结束长按，共 " + FramesDone + " 帧");
        }

        [HarmonyPatch(typeof(Manager.InputManager), "UpdateAmInput")]
        internal static class Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                try
                {
                    if (!Active) return;
                    // 兜底自动松开：调用方停止调用 Press() 超过 300ms 就立刻释放。
                    // 之前续关屏每帧重置帧数 → 离开续关屏后 4 号键还被按住 ~3.3s，
                    // 正好落在 MODE SELECT 上，会把「決定」误按/长按，状态机直接错乱。
                    if (Environment.TickCount - _lastCallTick > StaleMs)
                    {
                        Log.Warn("[hold] 调用方已停止注入 " + (Environment.TickCount - _lastCallTick) +
                                 "ms → 自动松开 " + (Manager.InputManager.ButtonSetting)Button);
                        Cancel();
                        return;
                    }
                    EnsureFields();
                    var push = _pushField != null ? _pushField.GetValue(null) as bool[,] : null;
                    if (push == null) { Active = false; return; }
                    int m = Monitor, b = Button;
                    if (m < 0 || m >= push.GetLength(0) || b < 0 || b >= push.GetLength(1))
                    {
                        Active = false;
                        return;
                    }
                    push[m, b] = true;
                    var down = _downField != null ? _downField.GetValue(null) as bool[,] : null;
                    if (down != null && m < down.GetLength(0) && b < down.GetLength(1))
                        down[m, b] = FramesDone < 2;   // edge only on the first frames
                    var time = _timeField != null ? _timeField.GetValue(null) as long[,] : null;
                    if (time != null && m < time.GetLength(0) && b < time.GetLength(1))
                        time[m, b] = Environment.TickCount - _startTick;
                    // The game may read GameButtonPush/Down rather than the raw player arrays.
                    var gpush = _gamePushField != null ? _gamePushField.GetValue(null) as bool[,,] : null;
                    if (gpush != null && m < gpush.GetLength(0) && b < gpush.GetLength(2))
                        for (int c = 0; c < gpush.GetLength(1); c++) gpush[m, c, b] = true;
                    var gdown = _gameDownField != null ? _gameDownField.GetValue(null) as bool[,,] : null;
                    if (gdown != null && m < gdown.GetLength(0) && b < gdown.GetLength(2))
                        for (int c = 0; c < gdown.GetLength(1); c++) gdown[m, c, b] = FramesDone < 2;
                    var any = _anyPushField != null ? _anyPushField.GetValue(null) as bool[] : null;
                    if (any != null && m < any.Length) any[m] = true;
                    FramesDone++;
                    if (FramesDone >= Frames) Cancel();
                }
                catch (Exception) { }
            }
        }
    }
}
