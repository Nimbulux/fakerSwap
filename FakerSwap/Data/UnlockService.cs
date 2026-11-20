using System;
using System.Collections.Generic;
using FakerSwap.Core;
using Manager;
using Manager.UserDatas;
using MAI2.Util;

namespace FakerSwap.Data
{
    /// <summary>
    /// Unlock songs / difficulties / collections by writing the live <c>UserData</c> lists.
    ///
    /// We never build an UpsertUserAll packet ourselves: the game's own export
    /// (<c>VOExtensions.ExportUserAll</c>) turns
    ///   - <c>MusicUnlockList</c>        -> userItemList itemKind 5,
    ///   - <c>MusicUnlockMasterList</c>  -> itemKind 6,
    ///   - <c>MusicUnlockReMasterList</c>-> itemKind 7,
    ///   - Title/Icon/Plate/Frame/Partner lists -> collection rows (itemKind 2),
    /// and only sends the rows that differ from the server snapshot (verified in the real network
    /// log: a play that unlocked two collection items produced 2 userItemList rows + "11" bits).
    ///
    /// Main-thread only.  Callers must go through <see cref="FakerSwap.UI.UiActions"/>.
    /// </summary>
    public static class UnlockService
    {
        /// <summary>Standard ids below this value have Master / Re:Master unlocked by design.</summary>
        public const int MasterUnlockMinMusicId = 10000;

        public sealed class Result
        {
            public int Candidates;
            public int Added;
            public int Skipped;
            public int Remaining;
            public string Error = "";
            public string Detail = "";

            public bool Ok { get { return string.IsNullOrEmpty(Error); } }

            public override string ToString()
            {
                if (!Ok) return "失败：" + Error;
                string text = "新增 " + Added + " 条";
                if (Skipped > 0) text += " · 跳过 " + Skipped;
                if (Remaining > 0) text += " · 剩余 " + Remaining + "（再点一次继续）";
                if (!string.IsNullOrEmpty(Detail)) text += " · " + Detail;
                return text;
            }
        }

        // ================================================================ songs

        /// <summary>Unlock an explicit list of songs (base / Master / Re:Master).</summary>
        public static Result UnlockSongs(int monitor, IList<int> ids, bool baseUnlock, bool master, bool remaster, int maxRows)
        {
            var result = new Result();
            try
            {
                string error;
                if (!CanWrite(out error)) { result.Error = error; return result; }
                var ud = UserDataPatcher.GetLive(monitor);
                if (!IsWritableUser(ud, out error)) { result.Error = error; return result; }
                var dm = GetDataManager();
                if (dm == null) { result.Error = "游戏主数据(DataManager)尚未加载"; return result; }
                if (maxRows <= 0) maxRows = int.MaxValue;

                var seen = new HashSet<int>();
                if (ids != null)
                {
                    for (int i = 0; i < ids.Count; i++)
                    {
                        if (ids[i] > 0) seen.Add(ids[i]);
                    }
                }

                foreach (int id in seen)
                {
                    result.Candidates++;
                    if (result.Added >= maxRows) { result.Remaining++; continue; }
                    if (AddUnlockFor(ud, dm, id, baseUnlock, master, remaster)) result.Added++;
                    else result.Skipped++;
                }
                Log.Info("UnlockService.UnlockSongs: candidates=" + result.Candidates + " added=" + result.Added +
                         " skipped=" + result.Skipped + " remaining=" + result.Remaining);
                InvalidateCatalog();
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                Log.Error("UnlockService.UnlockSongs failed", e);
            }
            return result;
        }

        /// <summary>Unlock every song this build actually ships, at most <paramref name="maxRows"/> new rows per call.</summary>
        public static Result UnlockAllSongs(int monitor, bool baseUnlock, bool master, bool remaster, int maxRows)
        {
            var result = new Result();
            try
            {
                string error;
                if (!CanWrite(out error)) { result.Error = error; return result; }
                var ud = UserDataPatcher.GetLive(monitor);
                if (!IsWritableUser(ud, out error)) { result.Error = error; return result; }
                var dm = GetDataManager();
                if (dm == null) { result.Error = "游戏主数据(DataManager)尚未加载"; return result; }
                if (maxRows <= 0) maxRows = 200;

                List<int> ids = AllMusicIds(dm);
                for (int i = 0; i < ids.Count; i++)
                {
                    int id = ids[i];
                    result.Candidates++;
                    if (result.Added >= maxRows) { result.Remaining++; continue; }
                    if (AddUnlockFor(ud, dm, id, baseUnlock, master, remaster)) result.Added++;
                    else result.Skipped++;
                }
                Log.Info("UnlockService.UnlockAllSongs: candidates=" + result.Candidates + " added=" + result.Added +
                         " skipped=" + result.Skipped + " remaining=" + result.Remaining);
                InvalidateCatalog();
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                Log.Error("UnlockService.UnlockAllSongs failed", e);
            }
            return result;
        }

        /// <summary>All music ids this build ships, sorted (skips disabled / chart-less entries).</summary>
        public static List<int> AllMusicIds(DataManager dm)
        {
            var ids = new List<int>();
            try
            {
                var musics = dm.GetMusics();
                if (musics == null) return ids;
                foreach (var pair in musics)
                {
                    if (pair.Key > 0) ids.Add(pair.Key);
                }
                ids.Sort();
            }
            catch (Exception e)
            {
                Log.Warn("UnlockService.AllMusicIds failed: " + e.Message);
            }
            return ids;
        }

        private static bool AddUnlockFor(UserData ud, DataManager dm, int musicId,
            bool baseUnlock, bool master, bool remaster)
        {
            Manager.MaiStudio.MusicData music = GetMusic(dm, musicId);
            if (music == null) return false;
            try { if (music.disable) return false; }
            catch (Exception) { }

            bool added = false;
            if (baseUnlock && !Contains(ud.MusicUnlockList, musicId))
            {
                ud.AddUnlockMusic(UserData.MusicUnlock.Base, musicId);
                added = true;
            }
            if (musicId >= MasterUnlockMinMusicId)
            {
                if (master && ChartCatalog.Exists(musicId, 3) && !Contains(ud.MusicUnlockMasterList, musicId))
                {
                    ud.AddUnlockMusic(UserData.MusicUnlock.Master, musicId);
                    added = true;
                }
                if (remaster && ChartCatalog.Exists(musicId, 4) && !Contains(ud.MusicUnlockReMasterList, musicId))
                {
                    ud.AddUnlockMusic(UserData.MusicUnlock.ReMaster, musicId);
                    added = true;
                }
            }
            return added;
        }

        private static bool Contains(List<int> list, int id)
        {
            return list != null && list.Contains(id);
        }

        // ================================================================ collections

        /// <summary>Add every collection item of the requested kinds to the live UserData lists.</summary>
        public static Result UnlockCollections(int monitor, bool titles, bool icons, bool plates,
            bool frames, bool partners)
        {
            var result = new Result();
            try
            {
                string error;
                if (!CanWrite(out error)) { result.Error = error; return result; }
                var ud = UserDataPatcher.GetLive(monitor);
                if (!IsWritableUser(ud, out error)) { result.Error = error; return result; }
                var dm = GetDataManager();
                if (dm == null) { result.Error = "游戏主数据(DataManager)尚未加载"; return result; }

                int total = 0;
                if (titles) result.Added += FillCollection(ref result, dm, ud, "称号", CollectionKind.Title, ref total);
                if (icons) result.Added += FillCollection(ref result, dm, ud, "头像", CollectionKind.Icon, ref total);
                if (plates) result.Added += FillCollection(ref result, dm, ud, "姓名框", CollectionKind.Plate, ref total);
                if (frames) result.Added += FillCollection(ref result, dm, ud, "背景", CollectionKind.Frame, ref total);
                if (partners) result.Added += FillCollection(ref result, dm, ud, "伙伴", CollectionKind.Partner, ref total);
                result.Candidates = total;

                Log.Info("UnlockService.UnlockCollections: added=" + result.Added + " total=" + total);
                InvalidateCatalog();
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                Log.Error("UnlockService.UnlockCollections failed", e);
            }
            return result;
        }

        private enum CollectionKind { Title, Icon, Plate, Frame, Partner }

        private static int FillCollection(ref Result result, DataManager dm, UserData ud, string label,
            CollectionKind kind, ref int total)
        {
            try
            {
                List<UserItem> list = GetCollectionList(ud, kind);
                if (list == null) { result.Detail += label + "列表不可用；"; return 0; }
                var have = new HashSet<int>();
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (item != null && item.stock > 0) have.Add(item.itemId);
                }
                int added = 0, seen = 0;
                foreach (int id in GetMasterCollectionIds(dm, kind))
                {
                    seen++;
                    if (have.Contains(id)) continue;
                    var item = new UserItem(id);
                    item.stock = 1;
                    item.isValid = true;
                    list.Add(item);
                    added++;
                }
                total += seen;
                // Some builds hand back a copy from the getter: write the list back through the setter.
                SetCollectionList(ud, kind, list);
                Log.Info("[unlock] 收藏品 " + label + ": 持有 " + have.Count + " / 共 " + seen + " → 新增 " + added);
                return added;
            }
            catch (Exception e)
            {
                result.Detail += label + "失败(" + e.Message + ")；";
                Log.Warn("[unlock] 收藏品 " + label + " 失败: " + e.Message);
                return 0;
            }
        }

        private static List<UserItem> GetCollectionList(UserData ud, CollectionKind kind)
        {
            // Field-backed read: other mods patch the UserData getters to return every master item.
            var list = UserDataAccess.GetCollectionList(ud, (int)kind);
            if (list == null)
            {
                Log.Warn("[unlock] 无法读取真实的 " + kind + " 列表（字段缺失？）");
            }
            return list;
        }

        private static void SetCollectionList(UserData ud, CollectionKind kind, List<UserItem> list)
        {
            UserDataAccess.SetCollectionList(ud, (int)kind, list);
        }

        private static List<int> GetMasterCollectionIds(DataManager dm, CollectionKind kind)
        {
            var ids = new List<int>();
            try
            {
                switch (kind)
                {
                    case CollectionKind.Title:
                        foreach (var pair in dm.GetTitles()) ids.Add(pair.Key);
                        break;
                    case CollectionKind.Icon:
                        foreach (var pair in dm.GetIcons()) ids.Add(pair.Key);
                        break;
                    case CollectionKind.Plate:
                        foreach (var pair in dm.GetPlates()) ids.Add(pair.Key);
                        break;
                    case CollectionKind.Frame:
                        foreach (var pair in dm.GetFrames()) ids.Add(pair.Key);
                        break;
                    case CollectionKind.Partner:
                        foreach (var pair in dm.GetPartners()) ids.Add(pair.Key);
                        break;
                }
                ids.Sort();
            }
            catch (Exception e)
            {
                Log.Warn("[unlock] 读取 " + kind + " 主数据失败: " + e.Message);
            }
            return ids;
        }

        // ================================================================ flush

        /// <summary>
        /// Ask the game to run its own DataSave (upload) chain.  Works only from a safe stage
        /// (song select / mode select etc.); returns false with a log line otherwise.
        /// </summary>
        public static bool Flush(string why)
        {
            try
            {
                if (!CanWrite(out string error))
                {
                    Log.Warn("[unlock] Flush rejected: " + error);
                    return false;
                }
                bool ok = Flow.LoginHooks.ForceDataSave("unlock:" + why);
                Log.Info("[unlock] Flush(" + why + ") = " + ok);
                return ok;
            }
            catch (Exception e)
            {
                Log.Error("[unlock] Flush failed", e);
                return false;
            }
        }

        // ================================================================ helpers

        private static bool CanWrite(out string error)
        {
            error = null;
            try
            {
                if (ModConfig.Data.SafeNoWrite)
                {
                    error = "当前是只读模式（SafeNoWrite=true），先解除只读";
                    return false;
                }
            }
            catch (Exception) { }
            if (!WriteGuard.Allow("UnlockService"))
            {
                error = "写入守卫拒绝了本次操作";
                return false;
            }
            return true;
        }

        private static bool IsWritableUser(UserData ud, out string error)
        {
            error = null;
            if (ud == null || ud.Detail == null) { error = "当前没有登录账号"; return false; }
            if (!ud.IsEntry) { error = "账号尚未登录完成（IsEntry=false）"; return false; }
            if (ud.IsGuest()) { error = "游客账号不能写入"; return false; }
            return true;
        }

        private static DataManager GetDataManager()
        {
            try { return Singleton<DataManager>.Instance; }
            catch (Exception) { return null; }
        }

        private static Manager.MaiStudio.MusicData GetMusic(DataManager dm, int musicId)
        {
            try { return dm.GetMusic(musicId); }
            catch (Exception) { return null; }
        }

        private static void InvalidateCatalog()
        {
            try { GameCatalog.MarkStale(); }
            catch (Exception) { }
        }
    }
}
