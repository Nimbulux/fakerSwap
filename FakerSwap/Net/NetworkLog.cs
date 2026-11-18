using System;
using System.Collections.Generic;
using System.IO;
using FakerSwap.Core;

namespace FakerSwap.Network
{
    /// <summary>
    /// Captures every HTTP request/response of the game.  Used both for debugging and to learn
    /// the exact UpsertUserAll shape.  Writes FakerSwap/Logs/net-yyyyMMdd.log.
    /// </summary>
    public static class NetworkLog
    {
        public static bool Enabled = true;
        public static bool PrintToConsole = false;
        private static readonly object Sync = new object();

        public static void Line(string direction, string api, string body)
        {
            if (!Enabled) return;
            try
            {
                var line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + direction + "] " + api + " -> " + body;
                if (PrintToConsole) Log.Info("[net] " + line);
                lock (Sync)
                {
                    Fs.EnsureDirs();
                    File.AppendAllText(Path.Combine(Fs.LogDir, "net-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), line + Environment.NewLine);
                }
            }
            catch (Exception) { }
        }
    }
}
