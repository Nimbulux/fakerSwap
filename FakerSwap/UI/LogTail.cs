using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FakerSwap.Core;

namespace FakerSwap.UI
{
    /// <summary>
    /// Tails the newest FakerSwap log file.  Pure file IO (no Unity, no game objects), so the
    /// window thread may call it directly while painting.  Results are cached for a fraction of a
    /// second; the newest *.log in FakerSwap/Logs is re-detected periodically so a session that
    /// crosses midnight keeps working.
    /// </summary>
    internal static class LogTail
    {
        private const int CacheMs = 700;
        private const int MaxReadBytes = 48 * 1024;
        private const int PathCacheMs = 10 * 1000;

        private static readonly object Sync = new object();
        private static string[] _lines = new string[0];
        private static int _lastRead = int.MinValue;
        private static int _cachedMax;
        private static string _error;
        private static string _path;
        private static int _pathCheckedAt = int.MinValue;

        public static string[] Get(int maxLines)
        {
            if (maxLines < 1) maxLines = 1;
            lock (Sync)
            {
                int now = Environment.TickCount;
                if (_lines.Length == 0 || maxLines > _cachedMax || unchecked(now - _lastRead) >= CacheMs)
                {
                    try
                    {
                        Read(maxLines, now);
                        _error = null;
                    }
                    catch (Exception e)
                    {
                        _error = e.Message;
                    }
                    _lastRead = now;
                    _cachedMax = maxLines;
                }
                if (_error != null) return new[] { "（日志读取失败：" + _error + "）" };
                return _lines;
            }
        }

        private static void Read(int maxLines, int now)
        {
            string path = ResolvePath(now);
            if (!File.Exists(path))
            {
                _lines = new[] { "（还没有日志文件：" + path + "）" };
                return;
            }

            byte[] buffer;
            int read;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None))
            {
                long length = stream.Length;
                int take = (int)Math.Min(length, MaxReadBytes);
                if (take <= 0)
                {
                    _lines = new string[0];
                    return;
                }
                stream.Seek(-take, SeekOrigin.End);
                buffer = new byte[take];
                read = 0;
                while (read < take)
                {
                    int n = stream.Read(buffer, read, take - read);
                    if (n <= 0) break;
                    read += n;
                }
            }

            string text = Encoding.UTF8.GetString(buffer, 0, read).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] raw = text.Split('\n');
            int start = raw.Length > 0 ? 1 : 0; // drop the (probably partial) first line
            var all = new List<string>(raw.Length);
            for (int i = start; i < raw.Length; i++)
            {
                if (i == raw.Length - 1 && raw[i].Length == 0) continue;
                all.Add(raw[i]);
            }

            int count = Math.Min(maxLines, all.Count);
            var lines = new string[count];
            for (int i = 0; i < count; i++) lines[i] = all[all.Count - count + i];
            _lines = lines;
        }

        /// <summary>Newest *.log in FakerSwap/Logs (falls back to today's file).</summary>
        private static string ResolvePath(int now)
        {
            if (_path != null && unchecked(now - _pathCheckedAt) < PathCacheMs && File.Exists(_path)) return _path;

            string fallback = Path.Combine(Fs.LogDir, DateTime.Now.ToString("yyyyMMdd") + ".log");
            try
            {
                string dir = Fs.LogDir;
                if (Directory.Exists(dir))
                {
                    string newest = null;
                    DateTime newestTime = DateTime.MinValue;
                    foreach (string file in Directory.GetFiles(dir, "*.log"))
                    {
                        DateTime stamp = File.GetLastWriteTime(file);
                        if (stamp > newestTime)
                        {
                            newestTime = stamp;
                            newest = file;
                        }
                    }
                    if (newest != null) fallback = newest;
                }
            }
            catch (Exception) { }

            _path = fallback;
            _pathCheckedAt = now;
            return _path;
        }
    }
}
