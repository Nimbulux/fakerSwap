using System;
using System.IO;

namespace FakerSwap.Core
{
    /// <summary>All on-disk locations used by the mod. Everything lives next to Sinmai.exe.</summary>
    public static class Fs
    {
        public const string RootName = "FakerSwap";

        public static string Root => Path.Combine(Environment.CurrentDirectory, RootName);
        public static string LogDir => Path.Combine(Root, "Logs");
        public static string BackupDir => Path.Combine(Root, "Backup");
        public static string SourceDir => Path.Combine(Root, "Source");
        public static string ConfigPath => Path.Combine(Root, "config.json");

        public static void EnsureDirs()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(BackupDir);
            Directory.CreateDirectory(SourceDir);
        }

        /// <summary>Make a filesystem-safe variant of a free-form string (player name etc.).</summary>
        public static string Safe(string name, string fallback = "unknown")
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0 || c == ' ' || c == '\\' || c == '/') sb.Append('_');
                else sb.Append(c);
            }
            var s = sb.ToString().Trim();
            if (s.Length > 80) s = s.Substring(0, 80);
            return s.Length == 0 ? fallback : s;
        }
    }
}
