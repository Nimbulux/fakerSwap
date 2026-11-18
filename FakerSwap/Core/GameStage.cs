using System;
using UnityEngine;

namespace FakerSwap.Core
{
    public enum GameStage
    {
        Unknown = 0,
        Startup,
        Title,
        Entry,          // waiting for aime card / scanning
        ModeSelect,
        Information,    // event/character/area info after login
        MusicSelect,
        TrackStart,     // difficulty / option selection
        GamePlay,
        Result,
        Continue,
        PhotoEdit,
        DataSave,       // uploading to the server
        MapSelect,
        MapResult,
        Other
    }

    /// <summary>
    /// Coarse mirror of what the game is currently doing.  Flow and UI both consume it; the
    /// Harmony side (Flow/Harmony) is what actually writes it.
    /// </summary>
    public static class GameStageTracker
    {
        private static readonly object Sync = new object();
        private static GameStage _current = GameStage.Unknown;
        private static double _enteredAt;
        private static string _raw = "";

        public static event Action<GameStage, GameStage> Changed;

        public static GameStage Current { get { lock (Sync) return _current; } }
        public static string RawProcess { get { lock (Sync) return _raw; } }
        public static double SecondsInStage => Time.realtimeSinceStartup - _enteredAt;

        public static void Set(GameStage stage, string rawProcess = null)
        {
            GameStage prev;
            lock (Sync)
            {
                if (_current == stage && (rawProcess == null || rawProcess == _raw)) return;
                prev = _current;
                _current = stage;
                _raw = rawProcess ?? "";
                _enteredAt = Time.realtimeSinceStartup;
            }
            var h = Changed;
            if (h != null)
            {
                try { h(prev, stage); }
                catch (Exception e) { Log.Error("GameStage.Changed handler failed", e); }
            }
        }

        public static bool IsInGameplay => Current == GameStage.GamePlay || Current == GameStage.TrackStart || Current == GameStage.Result || Current == GameStage.Continue;

        public static string Name(GameStage s) => s.ToString();
    }
}
