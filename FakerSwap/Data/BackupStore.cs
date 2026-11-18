using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FakerSwap.Core;

namespace FakerSwap.Data
{
    /// <summary>
    /// Backup files:  &lt;userName&gt;-&lt;yyyyMMdd-HHmmss&gt;-R&lt;rating&gt;.json
    /// Pre-write safety snapshots: pre-write-&lt;yyyyMMdd-HHmmss&gt;[-n].json
    /// (both are the same JSON shape, so SourceData.TryLoad can restore either one.)
    /// </summary>
    public static class BackupStore
    {
        public const string PreWritePrefix = "pre-write-";
        public const string FormatBackup = "fakerswap-backup/1";
        public const string FormatPreWrite = "fakerswap-pre-write/1";

        public static string BuildFileName(string userName, int rating, DateTime when)
        {
            var name = Fs.Safe(userName, "unknown");
            return name + "-" + when.ToString("yyyyMMdd-HHmmss") + "-R" + rating + ".json";
        }

        /// <summary>Persist a snapshot (used right after a source account has been fetched).</summary>
        public static string Save(SourceSnapshot snap, DateTime? when = null)
        {
            if (snap == null) return null;
            Fs.EnsureDirs();
            var stamp = when ?? DateTime.Now;
            var file = Path.Combine(Fs.BackupDir, BuildFileName(snap.UserName, snap.Rating, stamp));
            return WriteSnapshot(snap, file, FormatBackup, true);
        }

        /// <summary>
        /// Safety snapshot taken right before anything mutates the account.  Returns the file path,
        /// or null when the snapshot could not be written (the caller must then refuse to write).
        /// </summary>
        public static string SavePreWrite(SourceSnapshot snap, DateTime? when = null)
        {
            if (snap == null) return null;
            Fs.EnsureDirs();
            var stamp = when ?? DateTime.Now;
            string file = UniquePreWritePath(stamp);
            string written = WriteSnapshot(snap, file, FormatPreWrite, false);

            // A pre-write snapshot counts only when it is really on disk and non-empty.
            try
            {
                var info = new FileInfo(written);
                if (!info.Exists || info.Length <= 0)
                {
                    Log.Error("pre-write backup is empty: " + written);
                    return null;
                }
            }
            catch (Exception e)
            {
                Log.Error("pre-write backup verification failed: " + written, e);
                return null;
            }
            Log.Info("pre-write backup written: " + written + " (" + (snap.Records == null ? 0 : snap.Records.Count) + " records)");
            PrunePreWrite(10);
            return written;
        }

        /// <summary>
        /// Keep only the newest <paramref name="keep"/> pre-write snapshots.  Every credit writes one
        /// (they are deliberately per-credit, not per-write), so an unbounded directory would grow to
        /// dozens of files during a full transfer run.
        /// </summary>
        public static void PrunePreWrite(int keep)
        {
            try
            {
                if (!Directory.Exists(Fs.BackupDir)) return;
                var files = new List<string>(Directory.GetFiles(Fs.BackupDir, PreWritePrefix + "*.json"));
                if (files.Count <= keep) return;
                files.Sort(delegate (string a, string b)
                {
                    return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a));
                });
                int removed = 0;
                for (int i = keep; i < files.Count; i++)
                {
                    try { File.Delete(files[i]); removed++; }
                    catch (Exception) { }
                }
                if (removed > 0)
                    Log.Info("BackupStore: cleaned " + removed + " old pre-write backup(s), kept " + keep);
            }
            catch (Exception e) { Log.Warn("BackupStore: pre-write prune failed: " + e.Message); }
        }

        private static string UniquePreWritePath(DateTime stamp)
        {
            string baseName = PreWritePrefix + stamp.ToString("yyyyMMdd-HHmmss");
            string file = Path.Combine(Fs.BackupDir, baseName + ".json");
            int n = 2;
            while (File.Exists(file) && n < 1000)
            {
                file = Path.Combine(Fs.BackupDir, baseName + "-" + n + ".json");
                n++;
            }
            return file;
        }

        private static string WriteSnapshot(SourceSnapshot snap, string file, string format, bool log)
        {
            var root = JsonValue.NewObject();
            root["format"] = JsonValue.Of(format);
            root["userName"] = JsonValue.Of(snap.UserName);
            root["userId"] = JsonValue.Of(snap.UserId);
            root["rating"] = JsonValue.Of(snap.Rating);
            root["highestRating"] = JsonValue.Of(snap.HighestRating);
            root["savedAt"] = JsonValue.Of(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            var arr = JsonValue.NewArray();
            var records = snap.Records ?? new List<ScoreRecord>();
            foreach (var r in records)
            {
                if (r == null) continue;
                var o = JsonValue.NewObject();
                o["musicId"] = JsonValue.Of(r.MusicId);
                o["level"] = JsonValue.Of(r.Level);
                o["playCount"] = JsonValue.Of(r.PlayCount);
                o["achievement"] = JsonValue.Of(r.Achievement);
                o["comboStatus"] = JsonValue.Of(r.ComboStatus);
                o["syncStatus"] = JsonValue.Of(r.SyncStatus);
                o["deluxscoreMax"] = JsonValue.Of(r.DeluxscoreMax);
                o["scoreRank"] = JsonValue.Of(r.ScoreRank);
                o["extNum1"] = JsonValue.Of(r.ExtNum1);
                arr.Add(o);
            }
            root["records"] = arr;
            File.WriteAllText(file, root.ToJson(true));
            if (log) Log.Info("backup written: " + file);
            return file;
        }

        /// <summary>Newest pre-write snapshot, or null.</summary>
        public static string LatestPreWrite()
        {
            try
            {
                Fs.EnsureDirs();
                var files = Directory.GetFiles(Fs.BackupDir, PreWritePrefix + "*.json");
                if (files == null || files.Length == 0) return null;
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (var f in files)
                {
                    var t = File.GetLastWriteTime(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
                return best;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Every loadable file in the Backup/Source directories, newest first.</summary>
        public static List<SourceSnapshot> List()
        {
            var result = new List<SourceSnapshot>();
            Fs.EnsureDirs();
            foreach (var dir in new[] { Fs.BackupDir, Fs.SourceDir })
            {
                foreach (var file in Directory.GetFiles(dir, "*.json").OrderByDescending(File.GetLastWriteTime))
                {
                    SourceSnapshot snap;
                    string err;
                    if (SourceData.TryLoad(file, out snap, out err)) result.Add(snap);
                    else Log.Warn("skip " + file + ": " + err);
                }
            }
            return result;
        }
    }
}
