using System;
using System.Collections.Generic;
using FakerSwap.Core;
using Manager;
using Manager.UserDatas;
using MAI2.Util;
using Net.VO.Mai2;

namespace FakerSwap.Data
{
    /// <summary>
    /// Reads / writes the live <c>Manager.UserData</c> of a logged-in player.
    ///
    /// The game uploads the whole account through <c>Net.Packet.Mai2.PacketUpsertUserAll</c>, which builds
    /// its payload from this very object (VOExtensions.ExportUserAll -> ExportScoreDetailList -> ScoreDic),
    /// so patching <c>ScoreDic</c> before that packet is created is enough for both the upload and the
    /// on-screen "best" values.
    ///
    /// Writes stay defensive: every game type may be null before login / during logout, and any exception
    /// inside these helpers must never bubble up into the main-loop Tick.
    /// </summary>
    public static class UserDataPatcher
    {
        /// <summary>Add written scores to <c>UserData.Update.UpdateScoreList</c> (the game's per-credit dirty list).
        /// Keep on: it is harmless for the full-account upload and drives the playlog bookkeeping.</summary>
        public static bool MarkDirtyOnApply = true;

        /// <summary>Recompute the displayed/uploaded rating after a memory write. Disable if it ever misbehaves.</summary>
        public static bool RefreshRatingOnApply = true;

        /// <summary>Diagnostics: rows touched by the last <see cref="Commit"/> call.</summary>
        public static int LastCommitMemoryTouched { get; private set; }
        /// <summary>Diagnostics: whether the last <see cref="Commit"/> call queued a payload rewrite.</summary>
        public static bool LastCommitQueuedPayload { get; private set; }

        /// <summary>True when a real (non-guest) UserData is available for this monitor.</summary>
        public static bool IsReady(int monitorIndex)
        {
            try
            {
                var ud = GetLive(monitorIndex);
                return ud != null && ud.Detail != null && !ud.IsGuest();
            }
            catch (Exception) { return false; }
        }

        /// <summary>The live UserData object (null before login / for guests).</summary>
        public static UserData GetLive(int monitorIndex)
        {
            try
            {
                var mgr = Singleton<UserDataManager>.Instance;
                if (mgr == null) return null;
                return mgr.GetUserData(monitorIndex);
            }
            catch (Exception e)
            {
                Log.Warn("UserDataPatcher.GetLive(" + monitorIndex + "): " + e.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ read

        /// <summary>Dump the currently logged-in account of a monitor into a snapshot (source acquisition).</summary>
        public static SourceSnapshot ReadLive(int monitorIndex)
        {
            try
            {
                var ud = GetLive(monitorIndex);
                if (ud == null || ud.Detail == null)
                {
                    Log.Warn("UserDataPatcher.ReadLive: no user data for monitor " + monitorIndex + " (not logged in yet?)");
                    return null;
                }
                if (ud.IsGuest())
                {
                    Log.Warn("UserDataPatcher.ReadLive: monitor " + monitorIndex + " is a guest account, nothing to read");
                    return null;
                }

                var snap = new SourceSnapshot
                {
                    Path = "",
                    Format = "live",
                    SavedAt = DateTime.Now
                };
                try
                {
                    snap.UserName = ud.Detail.UserName ?? "";
                    snap.UserId = (long)ud.Detail.UserID;
                    snap.Rating = (int)ud.Detail.Rating;
                    snap.HighestRating = (int)ud.Detail.HighestRating;
                }
                catch (Exception e)
                {
                    Log.Warn("UserDataPatcher.ReadLive: cannot read user detail: " + e.Message);
                }
                if (string.IsNullOrEmpty(snap.UserName)) snap.UserName = "uid" + snap.UserId;

                // Preferred path: the game's own converter (same data the upload would carry).
                try
                {
                    var details = ud.ExportScoreDetailList();
                    if (details != null)
                    {
                        for (int i = 0; i < details.Length; i++)
                        {
                            var r = FromDetail(details[i]);
                            if (r != null) snap.Records.Add(r);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Warn("UserDataPatcher.ReadLive: ExportScoreDetailList failed, using ScoreDic: " + e.Message);
                }

                if (snap.Records.Count == 0) ReadScoreDic(ud, snap.Records);

                Log.Info("UserDataPatcher.ReadLive: " + snap.DisplayName);
                return snap;
            }
            catch (Exception e)
            {
                Log.Error("UserDataPatcher.ReadLive(" + monitorIndex + ") failed", e);
                return null;
            }
        }

        /// <summary>All chart records of the live account (handy for "target already has a better score" checks).</summary>
        public static List<ScoreRecord> ReadScores(int monitorIndex)
        {
            var list = new List<ScoreRecord>();
            try
            {
                var ud = GetLive(monitorIndex);
                if (ud == null) return list;
                ReadScoreDic(ud, list);
            }
            catch (Exception e)
            {
                Log.Warn("UserDataPatcher.ReadScores failed: " + e.Message);
            }
            return list;
        }

        /// <summary>One live record, or null when the target account has not played that chart.</summary>
        public static ScoreRecord GetScore(int monitorIndex, int musicId, int level)
        {
            try
            {
                var ud = GetLive(monitorIndex);
                if (ud == null || ud.ScoreDic == null) return null;
                if (level < 0 || level >= ud.ScoreDic.Length || level > 4) return null;
                var dict = ud.ScoreDic[level];
                if (dict == null) return null;
                UserScore score;
                if (!dict.TryGetValue(musicId, out score) || score == null) return null;
                return FromScore(score, level);
            }
            catch (Exception) { return null; }
        }

        /// <summary>Current rating of the logged-in account (for the backup file name / estimate).</summary>
        public static int ReadRating(int monitorIndex)
        {
            try
            {
                var ud = GetLive(monitorIndex);
                if (ud == null || ud.Detail == null) return 0;
                return (int)ud.Detail.Rating;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>Player name of the logged-in account, or null.</summary>
        public static string ReadUserName(int monitorIndex)
        {
            try
            {
                var ud = GetLive(monitorIndex);
                if (ud == null || ud.Detail == null) return null;
                return ud.Detail.UserName;
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------ pre-write safety backup

        /// <summary>Minimum age of the last pre-write snapshot before another one is written.</summary>
        public static int PreWriteBackupReuseSec = 120;
        public static string LastPreWriteBackupPath { get; private set; }
        public static DateTime LastPreWriteBackupAt { get; private set; }
        private static readonly HashSet<string> PreWriteBackupWarned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Mandatory safety net: before anything mutates the account, make sure a complete snapshot of
        /// every logged-in (non-guest) user data exists in <c>FakerSwap/Backup/pre-write-*.json</c>.
        /// Returns false when no snapshot could be produced - the caller MUST then refuse to write.
        /// A snapshot younger than <see cref="PreWriteBackupReuseSec"/> is reused to avoid one file per track.
        /// </summary>
        public static bool EnsurePreWriteBackup(out string error)
        {
            return EnsurePreWriteBackup(false, out error);
        }

        /// <summary>Same, but always writes a fresh snapshot (use before a forced DataSave).</summary>
        public static bool ForcePreWriteBackup(out string error)
        {
            return EnsurePreWriteBackup(true, out error);
        }

        public static bool EnsurePreWriteBackup(bool force, out string error)
        {
            error = null;
            try
            {
                if (!force && !string.IsNullOrEmpty(LastPreWriteBackupPath) &&
                    (DateTime.Now - LastPreWriteBackupAt).TotalSeconds <= PreWriteBackupReuseSec &&
                    System.IO.File.Exists(LastPreWriteBackupPath))
                {
                    error = null;
                    return true;
                }

                int saved = 0;
                string firstPath = null;
                string lastError = null;
                for (int mon = 0; mon < 2; mon++)
                {
                    var ud = GetLive(mon);
                    if (ud == null || ud.Detail == null || ud.IsGuest()) continue;
                    var snap = ReadLive(mon); // logs "ReadLive: <name> R.. (N条)"
                    if (snap == null || snap.Records == null || snap.Records.Count == 0)
                    {
                        lastError = "monitor " + mon + " snapshot empty";
                        continue;
                    }
                    string file = BackupStore.SavePreWrite(snap);
                    if (string.IsNullOrEmpty(file))
                    {
                        lastError = "monitor " + mon + " backup write failed";
                        continue;
                    }
                    if (firstPath == null) firstPath = file;
                    saved++;
                }

                if (saved > 0)
                {
                    LastPreWriteBackupPath = firstPath;
                    LastPreWriteBackupAt = DateTime.Now;
                    return true;
                }

                error = lastError ?? "no logged-in (non-guest) user data";
                if (PreWriteBackupWarned.Add(error))
                    Log.Warn("PRE-WRITE BACKUP FAILED (" + error + ") -> account write REFUSED");
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                if (PreWriteBackupWarned.Add("ex:" + e.Message))
                    Log.Error("PRE-WRITE BACKUP FAILED -> account write REFUSED", e);
                return false;
            }
        }

        // ------------------------------------------------------------------ write

        /// <summary>Apply the desired records to the live UserData.  Returns how many rows were touched.</summary>
        public static int Apply(int monitorIndex, IList<ScoreRecord> desired)
        {
            if (desired == null || desired.Count == 0) return 0;
            if (!WriteGuard.Allow("UserDataPatcher.Apply")) return 0;
            string backupError;
            if (!EnsurePreWriteBackup(out backupError))
            {
                Log.Warn("UserDataPatcher.Apply: pre-write backup failed (" + backupError + ") -> write REFUSED");
                return 0;
            }
            try
            {
                var ud = GetLive(monitorIndex);
                if (ud == null || ud.ScoreDic == null)
                {
                    Log.Warn("UserDataPatcher.Apply: no user data for monitor " + monitorIndex);
                    return 0;
                }
                if (ud.IsGuest())
                {
                    Log.Warn("UserDataPatcher.Apply: refusing to patch a guest account");
                    return 0;
                }

                int touched = 0, added = 0;
                for (int i = 0; i < desired.Count; i++)
                {
                    var rec = desired[i];
                    if (rec == null) continue;
                    if (rec.Level < 0 || rec.Level > 4 || rec.Level >= ud.ScoreDic.Length) continue;
                    var dict = ud.ScoreDic[rec.Level];
                    if (dict == null) continue;

                    UserScore score;
                    if (!dict.TryGetValue(rec.MusicId, out score) || score == null)
                    {
                        score = new UserScore(rec.MusicId);
                        dict[rec.MusicId] = score;
                        added++;
                    }
                    ApplyToScore(score, rec);
                    MarkDirty(ud, rec.Level, score);
                    touched++;
                }

                if (touched > 0 && RefreshRatingOnApply)
                {
                    // Refresh the rating so the uploaded userRatingList / detail matches the new scores.
                    try { ud.UpdateUserRate(); }
                    catch (Exception e) { Log.Warn("UserDataPatcher.Apply: UpdateUserRate failed: " + e.Message); }
                }

                Log.Info("UserDataPatcher.Apply: monitor=" + monitorIndex + " touched=" + touched + " added=" + added +
                         " rating=" + ReadRating(monitorIndex));
                return touched;
            }
            catch (Exception e)
            {
                Log.Error("UserDataPatcher.Apply(" + monitorIndex + ") failed", e);
                return 0;
            }
        }

        /// <summary>Apply exactly one record (used by the result-screen hook).</summary>
        public static bool ApplyOne(int monitorIndex, ScoreRecord rec)
        {
            if (rec == null) return false;
            var list = new List<ScoreRecord>(1);
            list.Add(rec);
            return Apply(monitorIndex, list) > 0;
        }

        // ------------------------------------------------------------------ one-call commit for the flow

        /// <summary>
        /// Normalize + write one finished track.  Honours <c>ConfigData.WriteMode</c>:
        ///   Memory = only touch the live UserData (the game's own UpsertUserAll carries it),
        ///   Payload = only queue it for <see cref="Network.PayloadRewriter"/>, Both = do both.
        /// Call this from the track-end hook for non-filler / non-skipped tracks.
        /// </summary>
        public static bool Commit(int monitorIndex, ScoreRecord rec, ChartStats stats)
        {
            LastCommitMemoryTouched = 0;
            LastCommitQueuedPayload = false;
            if (rec == null) return false;
            if (!FakerSwap.Core.WriteGuard.Allow("UserDataPatcher.Commit")) return false;

            // Write-before-backup: no mutation may happen unless a full snapshot was exported first.
            string backupError;
            if (!EnsurePreWriteBackup(out backupError))
            {
                Log.Warn("UserDataPatcher.Commit: pre-write backup failed (" + backupError + ") -> write REFUSED");
                return false;
            }
            try
            {
                ScoreMath.Normalize(rec, stats);
                bool memory, payload;
                SplitWriteMode(out memory, out payload);

                int touched = 0;
                if (memory)
                {
                    var one = new List<ScoreRecord>(1);
                    one.Add(rec);
                    touched = Apply(monitorIndex, one);
                }
                LastCommitMemoryTouched = touched;
                LastCommitQueuedPayload = payload;
                if (payload) Network.PayloadRewriter.AddPending(rec);

                // true = at least one write path accepted the record (memory rows touched or payload queued).
                return touched > 0 || payload;
            }
            catch (Exception e)
            {
                LastCommitMemoryTouched = 0;
                LastCommitQueuedPayload = false;
                Log.Error("UserDataPatcher.Commit(" + monitorIndex + ") failed", e);
                return false;
            }
        }

        /// <summary>Commit one track, fetching its chart stats from the catalog.</summary>
        public static bool Commit(int monitorIndex, ScoreRecord rec)
        {
            ChartStats stats = null;
            if (rec != null)
            {
                try { stats = ChartCatalog.GetStats(rec.MusicId, rec.Level); }
                catch (Exception) { }
            }
            return Commit(monitorIndex, rec, stats);
        }

        /// <summary>Commit a whole credit at once (memory write + payload queue with one config read).
        /// Not the main path: the flow uses per-track <see cref="Commit(int, ScoreRecord)"/>.</summary>
        public static int CommitBatch(int monitorIndex, IList<ScoreRecord> records)
        {
            if (records == null || records.Count == 0) return 0;
            if (!WriteGuard.Allow("UserDataPatcher.CommitBatch")) return 0;
            string backupError;
            if (!EnsurePreWriteBackup(out backupError))
            {
                Log.Warn("UserDataPatcher.CommitBatch: pre-write backup failed (" + backupError + ") -> write REFUSED");
                return 0;
            }
            try
            {
                bool memory, payload;
                SplitWriteMode(out memory, out payload);

                int touched = 0;
                if (memory) touched = Apply(monitorIndex, records);
                if (payload)
                {
                    for (int i = 0; i < records.Count; i++)
                    {
                        var r = records[i];
                        if (r == null) continue;
                        try { ScoreMath.Normalize(r, ChartCatalog.GetStats(r.MusicId, r.Level)); }
                        catch (Exception) { ScoreMath.Normalize(r, null); }
                        Network.PayloadRewriter.AddPending(r);
                    }
                    if (touched == 0) touched = records.Count;
                }
                return touched;
            }
            catch (Exception e)
            {
                Log.Error("UserDataPatcher.CommitBatch(" + monitorIndex + ") failed", e);
                return 0;
            }
        }

        private static void SplitWriteMode(out bool memory, out bool payload)
        {
            string mode = "Both";
            try
            {
                if (!string.IsNullOrEmpty(ModConfig.Data.WriteMode)) mode = ModConfig.Data.WriteMode.Trim();
            }
            catch (Exception) { }
            memory = !string.Equals(mode, "Payload", StringComparison.OrdinalIgnoreCase);
            payload = !string.Equals(mode, "Memory", StringComparison.OrdinalIgnoreCase);
            if (!memory && !payload) { memory = true; payload = true; } // unknown value -> safe default
        }

        // ------------------------------------------------------------------ helpers

        private static void ApplyToScore(UserScore score, ScoreRecord rec)
        {
            int achv = rec.Achievement;
            if (achv < 0) achv = 0;
            if (achv > ScoreMath.MaxAchievement) achv = ScoreMath.MaxAchievement;

            int combo = rec.ComboStatus;
            if (combo < 0 || combo > 4) combo = 0;
            int sync = rec.SyncStatus;
            if (sync < 0 || sync > 5) sync = 0;
            int rank = rec.ScoreRank;
            if (rank < 0 || rank >= ScoreMath.RankCount) rank = ScoreMath.RankOf(achv);
            int dx = rec.DeluxscoreMax;
            if (dx < 0) dx = 0;
            int pc = rec.PlayCount;
            if (pc <= 0) pc = 1;
            int ext = rec.ExtNum1;
            if (ext < 0) ext = 0;

            score.id = rec.MusicId;
            score.playcount = (uint)pc;
            score.achivement = (uint)achv;
            score.combo = (DB.PlayComboflagID)combo;
            score.sync = (DB.PlaySyncflagID)sync;
            score.deluxscore = (uint)dx;
            score.scoreRank = (MusicClearrankID)rank;
            score.extNum1 = (uint)ext;
        }

        /// <summary>
        /// The game keeps a per-difficulty list of the scores touched in the current credit
        /// (<c>UserData.Update.UpdateScoreList</c>); it drives the playlog the client uploads, so we add our
        /// rows there as well.  The upload payload itself is the full ScoreDic.
        /// </summary>
        private static void MarkDirty(UserData ud, int difficulty, UserScore score)
        {
            if (!MarkDirtyOnApply) return;
            try
            {
                var upd = ud.Update;
                if (upd == null || upd.UpdateScoreList == null) return;
                if (difficulty < 0 || difficulty >= upd.UpdateScoreList.Length) return;
                var list = upd.UpdateScoreList[difficulty];
                if (list == null) return;
                if (!list.Contains(score)) list.Add(score);
            }
            catch (Exception) { }
        }

        private static void ReadScoreDic(UserData ud, List<ScoreRecord> into)
        {
            try
            {
                var dic = ud.ScoreDic;
                if (dic == null) return;
                int levels = Math.Min(5, dic.Length);
                for (int lv = 0; lv < levels; lv++)
                {
                    var dict = dic[lv];
                    if (dict == null) continue;
                    foreach (var kv in dict)
                    {
                        var score = kv.Value;
                        if (score == null) continue;
                        var r = FromScore(score, lv);
                        if (r != null) into.Add(r);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("UserDataPatcher.ReadScoreDic failed: " + e.Message);
            }
        }

        private static ScoreRecord FromScore(UserScore src, int level)
        {
            if (src == null) return null;
            return new ScoreRecord
            {
                MusicId = src.id,
                Level = level,
                PlayCount = (int)src.playcount,
                Achievement = (int)src.achivement,
                ComboStatus = (int)src.combo,
                SyncStatus = (int)src.sync,
                DeluxscoreMax = (int)src.deluxscore,
                ScoreRank = (int)src.scoreRank,
                ExtNum1 = (int)src.extNum1
            };
        }

        private static ScoreRecord FromDetail(global::Net.VO.Mai2.UserMusicDetail d)
        {
            if (d.musicId <= 0) return null;
            return new ScoreRecord
            {
                MusicId = d.musicId,
                Level = (int)d.level,
                PlayCount = (int)d.playCount,
                Achievement = (int)d.achievement,
                ComboStatus = (int)d.comboStatus,
                SyncStatus = (int)d.syncStatus,
                DeluxscoreMax = (int)d.deluxscoreMax,
                ScoreRank = (int)d.scoreRank,
                ExtNum1 = (int)d.extNum1
            };
        }
    }
}
