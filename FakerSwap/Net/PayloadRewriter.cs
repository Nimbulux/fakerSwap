using System;
using System.Collections.Generic;
using System.Reflection;
using FakerSwap.Core;
using HarmonyLib;
using Net;
using Net.Packet;
using Net.VO.Mai2;

namespace FakerSwap.Network
{
    /// <summary>
    /// Optional second write path: rewrite the outgoing <c>UpsertUserAll</c> payload right before it is
    /// serialized, so the server receives our numbers even if the in-memory UserData path missed something.
    ///
    /// The client builds the packet as
    ///     NetQuery&lt;UserAllRequestVO, UpsertResponseVO&gt; { Request = { userId, ..., upsertUserAll = UserAll } }
    /// and serializes it in <c>Packet.ProcImpl</c>.  The request object is a plain managed object, so we
    /// simply mutate <c>Request.upsertUserAll.userMusicDetailList</c> from a postfix on
    /// <c>Packet.set_State</c> (state becomes <c>Ready</c>) — before the body is turned into JSON.
    /// </summary>
    public static class PayloadRewriter
    {
        private static readonly HashSet<string> _knownMusic = new HashSet<string>();
        private static readonly Dictionary<string, long> _lastSentSig = new Dictionary<string, long>();
        private static int _downgradeBlocked;
        private static int _sessionSaveIndex;
        private static int _sessionCredit = 1;

        /// <summary>本次登录会话内的第几个 credit（续关 +1；登录读取重置为 1）。</summary>
        public static void ResetSessionCredit() { _sessionCredit = 1; Log.Info("[net] session credit 重置为 1（新登录）"); }
        public static void BumpSessionCredit() { _sessionCredit++; Log.Info("[net] session credit → " + _sessionCredit + "（续关）"); }
        public static int SessionCredit { get { return _sessionCredit; } }

        /// <summary>登录时账号已有的谱面（musicId-level），用于修正 isNewMusicDetailList。</summary>
        public static void SetKnownMusic(System.Collections.Generic.IEnumerable<Data.ScoreRecord> recs)
        {
            _knownMusic.Clear();
            _lastSentSig.Clear();
            _sessionSaveIndex = 0;
            if (recs == null) return;
            foreach (var r in recs)
            {
                if (r == null) continue;
                _knownMusic.Add(r.MusicId + "-" + r.Level);
            }
        }
        public static bool Enabled;
        public static int PatchedUploads { get; private set; }
        public static int PatchedRows { get; private set; }
        public static int FailedUploads { get; private set; }
        /// <summary>Append rows the target account does not have yet (off by default: the transfer always plays the chart first).</summary>
        public static bool AppendMissingRows;

        /// <summary>Records to inject, keyed by "musicId:level".</summary>
        public static readonly Dictionary<string, Data.ScoreRecord> Pending = new Dictionary<string, Data.ScoreRecord>(StringComparer.Ordinal);

        private static bool _loggedShape;
        private static readonly object Sync = new object();

        public static int PendingCount { get { lock (Sync) return Pending.Count; } }

        public static void Install(HarmonyLib.Harmony harmony)
        {
            Enabled = false;
            try
            {
                if (harmony == null)
                {
                    Log.Warn("PayloadRewriter.Install: no Harmony instance");
                    return;
                }
                var setter = AccessTools.Method(typeof(Packet), "set_State");
                if (setter == null)
                {
                    Log.Error("PayloadRewriter.Install: Net.Packet.Packet.set_State not found");
                    return;
                }
                var postfix = AccessTools.Method(typeof(PayloadRewriter), "OnPacketState");
                harmony.Patch(setter, null, new HarmonyMethod(postfix));

                // Backup hook: if the tiny property setter ever gets inlined / skipped, ProcImpl is the
                // last moment before the request object is serialized and sent.
                var proc = AccessTools.Method(typeof(Packet), "ProcImpl");
                if (proc != null)
                {
                    var prefix = AccessTools.Method(typeof(PayloadRewriter), "OnProcPrefix");
                    harmony.Patch(proc, new HarmonyMethod(prefix), null);
                }

                Enabled = true;
                Log.Info("PayloadRewriter installed (Packet.set_State postfix + ProcImpl prefix)");
            }
            catch (Exception e)
            {
                Enabled = false;
                Log.Error("PayloadRewriter.Install failed (payload rewriting stays off)", e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        public static void OnPacketState(Packet __instance)
        {
            try
            {
                if (__instance == null) return;
                // 只在 ProcImpl prefix 改一次：set_State 后再改会让同一包的 strip 跑两遍（第二遍把本局行全删光）。
                return;
            }
            catch (Exception e)
            {
                Log.Error("PayloadRewriter.OnPacketState failed", e);
            }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]   // 必须早于 Sinmai-Assist 的 NetworkLogger 序列化，否则日志/报文仍是旧的
        public static void OnProcPrefix(Packet __instance)
        {
            try
            {
                if (__instance == null) return;
                if (__instance.State != PacketState.Ready) return;
                TryRewrite(__instance);
            }
            catch (Exception e)
            {
                Log.Error("PayloadRewriter.OnProcPrefix failed", e);
            }
        }

        /// <summary>Called for every packet transition; only UploadUserAll packets with pending rows are touched.</summary>
        public static bool TryRewrite(Packet packet)
        {
            if (!FakerSwap.Core.WriteGuard.Allow("PayloadRewriter.TryRewrite(UpsertUserAll)")) return false;
            try
            {
                if (!Enabled || packet == null) return false;
                int pending = PendingCount;

                var query = packet.Query;
                if (query == null) return false;
                string api = query.Api ?? "";
                if (api.IndexOf("UpsertUserAll", StringComparison.OrdinalIgnoreCase) < 0) return false;

                var reqProp = query.GetType().GetProperty("Request", BindingFlags.Instance | BindingFlags.Public);
                object request = reqProp != null ? reqProp.GetValue(query, null) : null;
                var vo = request as UserAllRequestVO;
                if (vo == null)
                {
                    if (!_loggedShape)
                    {
                        _loggedShape = true;
                        Log.Warn("PayloadRewriter: request object is " + (request == null ? "null" : request.GetType().FullName) +
                                 " (expected Net.VO.Mai2.UserAllRequestVO); payload rewriting will not touch this packet");
                    }
                    FailedUploads++;
                    return false;
                }
                if (vo.upsertUserAll == null)
                {
                    Log.Warn("PayloadRewriter: UpsertUserAll request without upsertUserAll body");
                    FailedUploads++;
                    return false;
                }


                // Write-before-backup: the upload will replace the account, so a full snapshot must
                // have been exported successfully first; otherwise refuse to rewrite anything.
                string backupError;
                if (!Data.UserDataPatcher.EnsurePreWriteBackup(out backupError))
                {
                    Log.Warn("PayloadRewriter: pre-write backup failed (" + backupError + ") -> UpsertUserAll rewrite REFUSED");
                    FailedUploads++;
                    return false;
                }

                var applied = new List<string>();
                int rows = pending > 0 ? RewriteDetailList(vo.upsertUserAll, applied) : 0;
                // 对照手动游玩（rc=1 成功）的两次存档：
                //   save1: 3 行, isNew="111"（全是新曲）
                //   save2: 3 行, isNew="001"（打过的标 0、新曲标 1）—— 不会重复带上一个 credit 的行
                // 我们的 flow 第二存会带 6 行（上一批 3 行原样重发）且 isNew="111111" → 官方服 rc=0（失败）。
                // 修复：① 去掉与上次发送完全相同的旧行；② isNew 只把账号里没有的谱面标 1。
                try
                {
                    var mlist = vo.upsertUserAll.userMusicDetailList;
                    if (mlist != null && mlist.Length > 0)
                    {
                        var keep = new List<UserMusicDetail>();
                        var keys = new List<string>();
                        int dropped = 0;
                        for (int i = 0; i < mlist.Length; i++)
                        {
                            string key = mlist[i].musicId + "-" + (int)mlist[i].level;
                            bool pendingRow; lock (Sync) { pendingRow = Pending.ContainsKey(key); }
                            if (pendingRow) { keep.Add(mlist[i]); keys.Add(key); continue; }   // 本 credit 的行绝不能删
                            long sig = ((long)mlist[i].achievement << 24) ^ ((long)mlist[i].playCount << 12) ^ (long)mlist[i].deluxscoreMax;
                            long prev;
                            if (_lastSentSig.TryGetValue(key, out prev) && prev == sig)
                            {
                                dropped++;
                                continue;
                            }
                            keep.Add(mlist[i]);
                            keys.Add(key);
                            _lastSentSig[key] = sig;
                        }
                        if (dropped > 0)
                        {
                            Log.Warn("[net] 去掉 " + dropped + " 行与上次相同、未变化的旧谱面（官方服累积重发会 rc=0）");
                            mlist = keep.ToArray();
                            vo.upsertUserAll.userMusicDetailList = mlist;
                        }
                        if (mlist.Length > 0)
                        {
                            var chars = new char[mlist.Length];
                            for (int i = 0; i < mlist.Length; i++)
                                chars[i] = _knownMusic.Contains(mlist[i].musicId + "-" + (int)mlist[i].level) ? '0' : '1';
                            string fixedNew = new string(chars);
                            if (vo.upsertUserAll.isNewMusicDetailList != fixedNew)
                            {
                                Log.Warn("[net] isNewMusicDetailList 修正: \"" + (vo.upsertUserAll.isNewMusicDetailList ?? "") +
                                         "\" → \"" + fixedNew + "\"（已有谱面标 0、只有新曲标 1）");
                                vo.upsertUserAll.isNewMusicDetailList = fixedNew;
                            }
                            for (int i = 0; i < mlist.Length; i++) _knownMusic.Add(mlist[i].musicId + "-" + (int)mlist[i].level);
                        }
                    }
                }
                catch (Exception e) { Log.Warn("[net] isNew/重复行 修正失败: " + e.Message); }

                // credit 序号：同一个 login session 里第二次存档应带 lastPlayCredit=2 / playCredit=2。
                // 我们的续关路径让游戏一直报 1（实机 02:25 rc=0），这里按 session 内第几次存档强制修正。
                try
                {
                    string apiName = query.Api ?? "";
                    if (apiName.IndexOf("UpsertUserAll", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _sessionSaveIndex++;   // 仅作诊断，不再作为 credit 序号
                        object ud0 = null;
                        var udField = vo.upsertUserAll.GetType().GetField("userData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        object udObj = udField != null ? udField.GetValue(vo.upsertUserAll) : null;
                        var udArr = udObj as Array;
                        if (udArr != null && udArr.Length > 0) ud0 = udArr.GetValue(0);
                        else if (udObj != null) ud0 = udObj;
                        if (ud0 != null)
                        {
                            var t = ud0.GetType();
                            var lpc = t.GetField("lastPlayCredit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (lpc != null && Convert.ToInt32(lpc.GetValue(ud0)) != _sessionCredit)
                            {
                                Log.Warn("[net] lastPlayCredit 修正: " + lpc.GetValue(ud0) + " → " + _sessionCredit + "（本次登录第 " + _sessionCredit + " 个 credit）");
                                lpc.SetValue(ud0, _sessionCredit);
                // Net.VO.Mai2.UserDetail 是 struct：GetValue 拿到的是装箱副本，必须写回数组
                if (ud0.GetType().IsValueType && udArr != null) udArr.SetValue(ud0, 0);
                            }
                        }
                        var gplField = vo.upsertUserAll.GetType().GetField("userGamePlaylogList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var gpl = gplField != null ? gplField.GetValue(vo.upsertUserAll) as Array : null;
                        if (gpl != null)
                        {
                            for (int i = 0; i < gpl.Length; i++)
                            {
                                object g = gpl.GetValue(i);
                                if (g == null) continue;
                                var pc = g.GetType().GetField("playCredit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (pc != null && Convert.ToInt32(pc.GetValue(g)) != _sessionCredit)
                                {
                                    pc.SetValue(g, _sessionCredit);
                                    if (g.GetType().IsValueType) gpl.SetValue(g, i);   // UserGamePlaylog 是 struct，需写回数组
                                }
                            }
                        }
                    }
                }
                catch (Exception e) { Log.Warn("[net] lastPlayCredit 修正失败: " + e.Message); }


                if (rows <= 0) return false;   // 无待写行：元数据/去重已应用，但没有需要落库的成绩

                lock (Sync)
                {
                    for (int i = 0; i < applied.Count; i++) Pending.Remove(applied[i]);
                }
                PatchedUploads++;
                PatchedRows += rows;
                Log.Info("PayloadRewriter: patched " + rows + " row(s) in " + api + " (uploads=" + PatchedUploads +
                         ", pendingLeft=" + PendingCount + ")");
                return true;
            }
            catch (Exception e)
            {
                FailedUploads++;
                Log.Error("PayloadRewriter.TryRewrite failed", e);
                return false;
            }
        }

        /// <summary>Mutates the payload list; returns how many rows were written.</summary>
        public static int RewriteDetailList(UserAll all, List<string> appliedKeys)
        {
            if (all == null) return 0;
            var list = all.userMusicDetailList;
            if (list == null)
            {
                list = new UserMusicDetail[0];
                all.userMusicDetailList = list;
            }

            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < list.Length; i++)
                index[Key(list[i].musicId, (int)list[i].level)] = i;

            List<string> keys;
            lock (Sync)
            {
                keys = new List<string>(Pending.Keys);
            }

            int rows = 0;
            for (int k = 0; k < keys.Count; k++)
            {
                Data.ScoreRecord rec;
                lock (Sync)
                {
                    if (!Pending.TryGetValue(keys[k], out rec)) continue;
                }
                if (rec == null) continue;

                int at;
                if (index.TryGetValue(keys[k], out at))
                {
                    WriteRow(ref list[at], rec);
                    rows++;
                    if (appliedKeys != null) appliedKeys.Add(keys[k]);
                    continue;
                }

                if (!AppendMissingRows) continue;

                // Row missing entirely: grow the array (kept opt-in because of isNewMusicDetailList bookkeeping).
                var grown = new UserMusicDetail[list.Length + 1];
                Array.Copy(list, grown, list.Length);
                WriteRow(ref grown[list.Length], rec);
                list = grown;
                all.userMusicDetailList = list;
                index[keys[k]] = list.Length - 1;
                rows++;
                if (appliedKeys != null) appliedKeys.Add(keys[k]);
            }
            return rows;
        }

        private static void WriteRow(ref UserMusicDetail d, Data.ScoreRecord r)
        {
            // 不降分：目标账号已有更高成绩时保留原值（登录数据未加载完时计划会误把已有曲当 missing）
            if (d.achievement > r.Achievement)
            {
                _downgradeBlocked++;
                if (_downgradeBlocked <= 8)
                    Log.Warn("[net] 跳过降分: " + r.MusicId + "-" + r.Level + " 现有 " + d.achievement + " > 目标 " + r.Achievement);
                return;
            }
            int achv = r.Achievement;
            if (achv < 0) achv = 0;
            if (achv > Data.ScoreMath.MaxAchievement) achv = Data.ScoreMath.MaxAchievement;
            int combo = r.ComboStatus;
            if (combo < 0 || combo > 4) combo = 0;
            int sync = r.SyncStatus;
            if (sync < 0 || sync > 5) sync = 0;
            int rank = r.ScoreRank;
            if (rank < 0 || rank >= Data.ScoreMath.RankCount) rank = Data.ScoreMath.RankOf(achv);
            int dx = r.DeluxscoreMax;
            if (dx < 0) dx = 0;
            int pc = r.PlayCount;
            if (pc <= 0) pc = 1;
            int ext = r.ExtNum1;
            if (ext < 0) ext = 0;

            d.musicId = r.MusicId;
            d.level = (Manager.MusicDifficultyID)r.Level;
            d.playCount = (uint)pc;
            d.achievement = (uint)achv;
            d.comboStatus = (DB.PlayComboflagID)combo;
            d.syncStatus = (DB.PlaySyncflagID)sync;
            d.deluxscoreMax = (uint)dx;
            d.scoreRank = (Manager.MusicClearrankID)rank;
            d.extNum1 = (uint)ext;
        }

        private static string Key(int musicId, int level)
        {
            return musicId + ":" + level;
        }

        // ------------------------------------------------------------------ queue helpers for the flow

        public static void AddPending(Data.ScoreRecord rec)
        {
            if (rec == null) return;
            lock (Sync) Pending[rec.Key] = rec;
        }

        public static void AddPending(IList<Data.ScoreRecord> records)
        {
            if (records == null) return;
            lock (Sync)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    var r = records[i];
                    if (r != null) Pending[r.Key] = r;
                }
            }
        }

        public static void RemovePending(Data.ScoreRecord rec)
        {
            if (rec == null) return;
            lock (Sync) Pending.Remove(rec.Key);
        }

        public static void ClearPending()
        {
            lock (Sync) Pending.Clear();
        }
    }
}
