using System;
using System.IO;
using System.Text;
using MelonLoader;

namespace FakerSwap.Core
{
    /// <summary>Thin logger that mirrors to the MelonLoader console and a rotating text file.</summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static string _path;

        public static void Init()
        {
            try
            {
                Fs.EnsureDirs();
                _path = Path.Combine(Fs.LogDir, DateTime.Now.ToString("yyyyMMdd") + ".log");
                Write("==== FakerSwap " + BuildInfo.Version + " session start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
            }
            catch (Exception)
            {
                _path = null;
            }
        }

        public static void Info(string msg) => Write("[INFO] " + msg);
        public static void Warn(string msg) => Write("[WARN] " + msg);
        public static void Error(string msg) => Write("[ERR ] " + msg);

        public static void Error(string msg, Exception e) => Write("[ERR ] " + msg + Environment.NewLine + e);

        private static void Write(string line)
        {
            try { MelonLogger.Msg(line); } catch (Exception) { }
            if (_path == null) return;
            try
            {
                lock (Sync) File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception) { }
        }
    }
}
