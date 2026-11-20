using System;
using System.Collections.Generic;
using FakerSwap.Core;
using Manager;
using Manager.UserDatas;
using MAI2.Util;

namespace FakerSwap.Data
{
    /// <summary>
    /// Plain, window-thread-safe copy of the game's master data + the logged-in account's unlock
    /// lists.  Built on the Unity main thread from <c>Singleton&lt;DataManager&gt;</c> (music list,
    /// collection tables) and <c>Singleton&lt;UserDataManager&gt;</c> (unlock lists), then published
    /// through a volatile array reference so the window thread can page through it without ever
    /// touching a Unity object.
    /// </summary>
    public static class GameCatalog
    {
        public sealed class SongRow
        {
            public int Id;
            public string Title = "";
            public string Artist = "";
            public int Bpm;
            public int Version;
            public readonly int[] Level = new int[5];
            public readonly int[] Decimal = new int[5];
            public readonly int[] Notes = new int[5];
            public readonly bool[] Has = new bool[5];
            public bool BaseInList;
            public bool MasterInList;
            public bool ReMasterInList;

            public string LevelText(int level)
            {
                if (level < 0 || level > 4 || !Has[level]) return "—";
                return Decimal[level] > 0 ? Level[level] + "+" : Level[level].ToString();
            }
        }

        public sealed class CollectionInfo
        {
            public string Name = "";
            public int Owned;
            public int Total;
            public bool Available { get { return Total > 0; } }
        }

        private static readonly SongRow[] EmptySongs = new SongRow[0];
        private static readonly CollectionInfo[] EmptyCollections = new CollectionInfo[0];

        private static volatile SongRow[] _songs = EmptySongs;
        private static volatile CollectionInfo[] _collections = EmptyCollections;
        private static volatile string _error = "";
        private static volatile int _version;
        private static volatile bool _stale = true;
        private static volatile bool _busy;
        private static long _builtForUserId = -1;

        public static SongRow[] Songs { get { return _songs; } }
        public static CollectionInfo[] Collections { get { return _collections; } }
        public static string Error { get { return _error ?? ""; } }
        public static int Version { get { return _version; } }
        public static bool HasData { get { return _songs.Length > 0; } }
        public static bool Stale { get { return _stale; } }
        public static bool Busy { get { return _busy; } }

        /// <summary>Mark the copy outdated (after an unlock write / account switch).</summary>
        public static void MarkStale()
        {
            _stale = true;
        }

        /// <summary>Re-read the game data if it was invalidated.  Unity main thread only.</summary>
        public static void RefreshIfStale()
        {
            if (_stale && !_busy) Refresh();
        }

        /// <summary>Re-read everything from the game.  Unity main thread only.</summary>
        public static void Refresh()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var dm = GetDataManager();
                if (dm == null)
                {
                    _error = "游戏主数据(DataManager)还没创建";
                    return;
                }
                bool loaded = false;
                try { loaded = dm.IsLoaded(); } catch (Exception) { }
                if (!loaded)
                {
                    _error = "游戏主数据仍在加载，稍后再刷新";
                    return;
                }

                var ud = UserDataPatcher.GetLive(0);
                long userId = -1;
                var baseSet = new HashSet<int>();
                var masterSet = new HashSet<int>();
                var remasterSet = new HashSet<int>();
                if (ud != null && ud.Detail != null)
                {
                    try { userId = (long)ud.Detail.UserID; } catch (Exception) { }
                    FillSet(ud.MusicUnlockList, baseSet);
                    FillSet(ud.MusicUnlockMasterList, masterSet);
                    FillSet(ud.MusicUnlockReMasterList, remasterSet);
                }
                _builtForUserId = userId;

                // ---- songs -------------------------------------------------------------
                var songs = new List<SongRow>(1800);
                try
                {
                    var musics = dm.GetMusics();
                    if (musics != null)
                    {
                        foreach (var pair in musics)
                        {
                            var music = pair.Value;
                            if (music == null) continue;
                            var row = new SongRow();
                            row.Id = pair.Key;
                            try { row.Title = music.name != null ? (music.name.str ?? "") : ""; } catch (Exception) { }
                            try { row.Artist = music.artistName != null ? (music.artistName.str ?? "") : ""; } catch (Exception) { }
                            try { row.Bpm = music.bpm; } catch (Exception) { }
                            try { row.Version = music.version; } catch (Exception) { }
                            FillNotes(row, music);
                            row.BaseInList = baseSet.Contains(row.Id);
                            row.MasterInList = masterSet.Contains(row.Id);
                            row.ReMasterInList = remasterSet.Contains(row.Id);
                            songs.Add(row);
                        }
                    }
                }
                catch (Exception e)
                {
                    _error = "读取曲库失败：" + e.Message;
                    Log.Warn("GameCatalog: music list failed: " + e.Message);
                }
                songs.Sort((a, b) => a.Id.CompareTo(b.Id));

                // ---- collections -------------------------------------------------------
                var collections = new List<CollectionInfo>(8);
                AddCollection(collections, dm, ud, "称号", 0);
                AddCollection(collections, dm, ud, "头像", 1);
                AddCollection(collections, dm, ud, "姓名框", 2);
                AddCollection(collections, dm, ud, "背景", 3);
                AddCollection(collections, dm, ud, "伙伴", 4);

                _songs = songs.ToArray();
                _collections = collections.ToArray();
                if (songs.Count > 0) _error = "";
                _stale = false;
                _version++;
                Log.Info("GameCatalog: " + songs.Count + " song(s), " + collections.Count + " collection kind(s), uid=" + userId);
            }
            catch (Exception e)
            {
                _error = e.Message;
                Log.Error("GameCatalog.Refresh failed", e);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>True when the catalog was built for a different account than the live one.</summary>
        public static bool AccountChanged()
        {
            try
            {
                var ud = UserDataPatcher.GetLive(0);
                if (ud == null || ud.Detail == null) return false;
                return (long)ud.Detail.UserID != _builtForUserId;
            }
            catch (Exception) { return false; }
        }

        private static void FillNotes(SongRow row, Manager.MaiStudio.MusicData music)
        {
            try
            {
                var notes = music.notesData;
                if (notes == null) return;
                int count = Math.Min(5, notes.Count);
                for (int level = 0; level < count; level++)
                {
                    var note = notes[level];
                    if (note == null) continue;
                    row.Level[level] = note.level;
                    row.Decimal[level] = note.levelDecimal;
                    row.Notes[level] = note.maxNotes;
                    row.Has[level] = note.isEnable && note.maxNotes > 0;
                }
            }
            catch (Exception) { }
        }

        private static void AddCollection(List<CollectionInfo> into, DataManager dm, UserData ud, string name, int kind)
        {
            var info = new CollectionInfo();
            info.Name = name;
            try
            {
                List<int> master = MasterIds(dm, kind);
                info.Total = master.Count;
                List<UserItem> owned = OwnedList(ud, kind);
                var have = new HashSet<int>();
                if (owned != null)
                {
                    for (int i = 0; i < owned.Count; i++)
                    {
                        var item = owned[i];
                        if (item != null && item.stock > 0) have.Add(item.itemId);
                    }
                }
                int count = 0;
                for (int i = 0; i < master.Count; i++)
                    if (have.Contains(master[i])) count++;
                info.Owned = count;
            }
            catch (Exception e)
            {
                Log.Warn("GameCatalog: collection " + name + " failed: " + e.Message);
            }
            into.Add(info);
        }

        private static List<int> MasterIds(DataManager dm, int kind)
        {
            var ids = new List<int>();
            switch (kind)
            {
                case 0:
                    foreach (var pair in dm.GetTitles()) ids.Add(pair.Key);
                    break;
                case 1:
                    foreach (var pair in dm.GetIcons()) ids.Add(pair.Key);
                    break;
                case 2:
                    foreach (var pair in dm.GetPlates()) ids.Add(pair.Key);
                    break;
                case 3:
                    foreach (var pair in dm.GetFrames()) ids.Add(pair.Key);
                    break;
                case 4:
                    foreach (var pair in dm.GetPartners()) ids.Add(pair.Key);
                    break;
            }
            ids.Sort();
            return ids;
        }

        private static List<UserItem> OwnedList(UserData ud, int kind)
        {
            if (ud == null) return null;
            // Field-backed read: collection getters may be Harmony-patched to return everything.
            return UserDataAccess.GetCollectionList(ud, kind);
        }

        private static void FillSet(List<int> source, HashSet<int> target)
        {
            if (source == null) return;
            for (int i = 0; i < source.Count; i++) target.Add(source[i]);
        }

        private static DataManager GetDataManager()
        {
            try { return Singleton<DataManager>.Instance; }
            catch (Exception) { return null; }
        }
    }
}
