using System;
using System.Collections.Generic;

namespace FakerSwap.Core
{
    /// <summary>
    /// Global kill-switch for everything that mutates the account.
    ///
    /// While <see cref="ConfigData.SafeNoWrite"/> is true the mod may only read (navigate, dump
    /// user data, build plans) - it will refuse to patch UserData, rewrite an UpsertUserAll payload
    /// or force a DataSave.  This exists because a forced DataSave from an unsafe game state
    /// overwrote a real profile once; the write path stays off until it has been validated end to
    /// end in read-only mode.
    /// </summary>
    public static class WriteGuard
    {
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        public static bool WritesAllowed => !ModConfig.Data.SafeNoWrite;

        /// <summary>Returns true when a write may proceed.  Logs once per call-site when blocked.</summary>
        public static bool Allow(string what)
        {
            if (!ModConfig.Data.SafeNoWrite) return true;
            if (Warned.Add(what ?? "?"))
                Log.Warn("WRITE BLOCKED (SafeNoWrite=true): " + what + "  -> 只读模式，未做任何修改");
            return false;
        }

        /// <summary>Reset the "already warned" set (e.g. after the operator flips the switch).</summary>
        public static void ResetWarnings() => Warned.Clear();
    }
}
