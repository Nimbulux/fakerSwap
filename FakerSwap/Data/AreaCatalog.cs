using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using FakerSwap.Core;
using Manager;
using Manager.UserDatas;
using MAI2.Util;

namespace FakerSwap.Data
{
    /// <summary>
    /// Complete area list that works BEFORE the area-select screen exists.
    ///
    /// Sources, in priority order:
    ///   1. <c>UserData.MapList</c>          - logged-in account: name + progress + lock (the authoritative list,
    ///                                          available as soon as login finished, no RegionalSelectProcess needed),
    ///   2. <c>DataManager.GetMapDatas()</c> - master data: name / bonus-music group / collabo / infinity / open event,
    ///   3. <c>FakerSwap/areas.json</c>      - cache of the last successful master scan (works at Title / before login),
    ///   4. StreamingAssets XML scan         - last resort (A*\map\Map{id}\Map.xml).
    ///
    /// The cache is invalidated by the pack set / map dir count / newest map dir timestamp, so a game
    /// update rebuilds it automatically.  Main-thread only; the window thread reads a volatile array.
    /// </summary>
    public static class AreaCatalog
    {
        private const int Schema = 1;

        public sealed class AreaRow
        {
            public int Id;
            public string Name = "";
            public int BonusMusicId;
            public int OpenEventId;
            public bool IsCollabo;
            public bool IsInfinity;
            public string Pack = "";
            public bool HasUserState;
            public bool IsLock;
            public bool IsClear;
            public bool IsComplete;
            public uint Distance;
        }

        private static readonly AreaRow[] Empty = new AreaRow[0];
        private static volatile AreaRow[] _areas = Empty;
        private static volatile string _error = "";
        private static volatile int _version;
        private static volatile bool _stale = true;
        private static volatile bool _busy;
        private static volatile string _source = "";
        private static string _savedFingerprint = "";

        public static AreaRow[] Areas { get { return _areas; } }
        public static string Error { get { return _error ?? ""; } }
        public static int Version { get { return _version; } }
        public static string Source { get { return _source ?? ""; } }
        public static int Count { get { return _areas.Length; } }
        public static bool HasData { get { return _areas.Length > 0; } }

        public static void MarkStale()
        {
            _stale = true;
        }

        public static void RefreshIfStale()
        {
            if (_stale && !_busy) Refresh();
        }

        /// <summary>Rebuild the whole catalog.  Unity main thread only.</summary>
        public static void Refresh()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var map = new Dictionary<int, AreaRow>();
                string source = "";

                // 1) live account
                var ud = UserDataPatcher.GetLive(0);
                bool loggedIn = ud != null && ud.Detail != null && ud.IsEntry && !ud.IsGuest();
                if (loggedIn)
                {
                    try
                    {
                        var userMaps = ud.MapList;
                        if (userMaps != null)
                        {
                            for (int i = 0; i < userMaps.Count; i++)
                            {
                                var um = userMaps[i];
                                if (um == null || um.ID <= 0) continue;
                                var row = GetOrAdd(map, um.ID);
                                row.HasUserState = true;
                                if (!string.IsNullOrEmpty(um.Name)) row.Name = um.Name;
                                row.IsLock = um.IsLock;
                                row.IsClear = um.IsClear;
                                row.IsComplete = um.IsComplete;
                                row.IsInfinity = um.IsInfinity;
                                row.Distance = um.Distance;
                            }
                            source = "UserData.MapList";
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warn("AreaCatalog: UserData.MapList failed: " + e.Message);
                    }
                }

                // 2) master data
                try
                {
                    var dm = GetDataManager();
                    bool loaded = false;
                    if (dm != null) { try { loaded = dm.IsLoaded(); } catch (Exception) { } }
                    if (loaded)
                    {
                        var maps = dm.GetMapDatas();
                        if (maps != null)
                        {
                            foreach (var pair in maps)
                            {
                                var md = pair.Value;
                                if (md == null || pair.Key <= 0) continue;
                                var row = GetOrAdd(map, pair.Key);
                                try { if (md.name != null && !string.IsNullOrEmpty(md.name.str)) row.Name = md.name.str; } catch (Exception) { }
                                try { row.BonusMusicId = md.BonusMusicId != null ? md.BonusMusicId.id : 0; } catch (Exception) { }
                                try { row.OpenEventId = md.OpenEventId != null ? md.OpenEventId.id : 0; } catch (Exception) { }
                                try { row.IsCollabo = md.IsCollabo; } catch (Exception) { }
                                try { if (md.IsInfinity) row.IsInfinity = true; } catch (Exception) { }
                                if (source.Length == 0) source = "DataManager";
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Warn("AreaCatalog: DataManager map list failed: " + e.Message);
                }

                // 3) cache
                bool cacheUsed = false;
                if (map.Count == 0)
                {
                    cacheUsed = TryLoadCache(map);
                    if (cacheUsed) source = "areas.json 缓存";
                }

                // 4) XML scan (no master data, no cache)
                if (map.Count == 0)
                {
                    int scan = ScanXml(map);
                    if (scan > 0) source = "StreamingAssets XML";
                }

                // save the cache whenever we got data from the game (so the next boot is instant)
                if (map.Count > 0 && !cacheUsed)
                {
                    TrySaveCache(map);
                }

                var list = new List<AreaRow>(map.Values);
                list.Sort((a, b) => a.Id.CompareTo(b.Id));
                _areas = list.ToArray();
                _source = source;
                _error = _areas.Length == 0 ? "区域列表为空（主数据未加载且没有缓存）" : "";
                _stale = false;
                _version++;
                Log.Info("AreaCatalog: " + _areas.Length + " area(s) from " + source +
                         (loggedIn ? " (logged in)" : " (not logged in)"));
            }
            catch (Exception e)
            {
                _error = e.Message;
                Log.Error("AreaCatalog.Refresh failed", e);
            }
            finally
            {
                _busy = false;
            }
        }

        // ================================================================ cache

        private static string CachePath
        {
            get { return Path.Combine(Fs.Root, "areas.json"); }
        }

        private static bool TryLoadCache(Dictionary<int, AreaRow> map)
        {
            try
            {
                if (!File.Exists(CachePath)) return false;
                JsonValue root;
                string error;
                if (!JsonValue.TryParse(File.ReadAllText(CachePath), out root, out error)) return false;
                if (root["schema"].AsInt(0) != Schema) return false;
                string cachedPrint = root["fingerprint"].AsString("");
                if (!string.IsNullOrEmpty(cachedPrint) && cachedPrint != Fingerprint()) return false;
                var areas = root["areas"];
                if (areas.Kind != JsonKind.Array) return false;
                for (int i = 0; i < areas.Count; i++)
                {
                    var node = areas[i];
                    int id = node["id"].AsInt(0);
                    if (id <= 0) continue;
                    var row = GetOrAdd(map, id);
                    row.Name = node["name"].AsString("");
                    row.BonusMusicId = node["bonus"].AsInt(0);
                    row.OpenEventId = node["event"].AsInt(0);
                    row.IsCollabo = node["collabo"].AsBool(false);
                    row.IsInfinity = node["infinity"].AsBool(false);
                    row.Pack = node["pack"].AsString("");
                }
                return map.Count > 0;
            }
            catch (Exception e)
            {
                Log.Warn("AreaCatalog: cache load failed: " + e.Message);
                return false;
            }
        }

        private static void TrySaveCache(Dictionary<int, AreaRow> map)
        {
            try
            {
                Fs.EnsureDirs();
                string fingerprint = Fingerprint();
                if (File.Exists(CachePath) && fingerprint.Length > 0 && fingerprint == _savedFingerprint) return;
                var root = JsonValue.NewObject();
                root["schema"] = JsonValue.Of(Schema);
                root["fingerprint"] = JsonValue.Of(fingerprint);
                var areas = JsonValue.NewArray();
                var sorted = new List<AreaRow>(map.Values);
                sorted.Sort((a, b) => a.Id.CompareTo(b.Id));
                for (int i = 0; i < sorted.Count; i++)
                {
                    var row = sorted[i];
                    var node = JsonValue.NewObject();
                    node["id"] = JsonValue.Of(row.Id);
                    node["name"] = JsonValue.Of(row.Name ?? "");
                    node["bonus"] = JsonValue.Of(row.BonusMusicId);
                    node["event"] = JsonValue.Of(row.OpenEventId);
                    node["collabo"] = JsonValue.Of(row.IsCollabo);
                    node["infinity"] = JsonValue.Of(row.IsInfinity);
                    node["pack"] = JsonValue.Of(row.Pack ?? "");
                    areas.Add(node);
                }
                root["areas"] = areas;
                File.WriteAllText(CachePath, root.ToJson(true));
                _savedFingerprint = fingerprint;
                Log.Info("AreaCatalog: cache saved (" + sorted.Count + " areas) -> " + CachePath);
            }
            catch (Exception e)
            {
                Log.Warn("AreaCatalog: cache save failed: " + e.Message);
            }
        }

        /// <summary>Pack set + map dir count + newest map dir timestamp; changes when the game updates.</summary>
        private static string Fingerprint()
        {
            try
            {
                string sa = StreamingAssets();
                if (string.IsNullOrEmpty(sa)) return "";
                var dirs = Directory.GetDirectories(sa);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                int mapDirs = 0;
                long newest = 0;
                var names = new List<string>();
                for (int i = 0; i < dirs.Length; i++)
                {
                    string pack = Path.GetFileName(dirs[i]);
                    string mapDir = Path.Combine(dirs[i], "map");
                    if (!Directory.Exists(mapDir)) continue;
                    names.Add(pack);
                    mapDirs += Directory.GetDirectories(mapDir).Length;
                    DateTime stamp = Directory.GetLastWriteTimeUtc(mapDir);
                    if (stamp.Ticks > newest) newest = stamp.Ticks;
                }
                return string.Join(",", names.ToArray()) + "|" + mapDirs + "|" + newest;
            }
            catch (Exception) { return ""; }
        }

        // ================================================================ XML scan

        private static int ScanXml(Dictionary<int, AreaRow> map)
        {
            int added = 0;
            try
            {
                string sa = StreamingAssets();
                if (string.IsNullOrEmpty(sa)) return 0;
                var packs = Directory.GetDirectories(sa);
                Array.Sort(packs, StringComparer.OrdinalIgnoreCase);
                for (int p = 0; p < packs.Length; p++)
                {
                    string pack = Path.GetFileName(packs[p]);
                    string mapDir = Path.Combine(packs[p], "map");
                    if (!Directory.Exists(mapDir)) continue;
                    var dirs = Directory.GetDirectories(mapDir);
                    for (int d = 0; d < dirs.Length; d++)
                    {
                        string name = Path.GetFileName(dirs[d]);
                        int id;
                        if (name.Length < 7 || !name.StartsWith("Map", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!int.TryParse(name.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id <= 0) continue;
                        string xmlPath = Path.Combine(dirs[d], "Map.xml");
                        if (!File.Exists(xmlPath)) continue;
                        try
                        {
                            var doc = new XmlDocument();
                            doc.Load(xmlPath);
                            var row = GetOrAdd(map, id);
                            string areaName = Text(doc, "MapData/name/str");
                            if (!string.IsNullOrEmpty(areaName)) row.Name = areaName;
                            row.BonusMusicId = Int(doc, "MapData/BonusMusicId/id");
                            row.OpenEventId = Int(doc, "MapData/OpenEventId/id");
                            row.IsCollabo = Bool(doc, "MapData/IsCollabo");
                            row.IsInfinity = Bool(doc, "MapData/IsInfinity");
                            if (row.Pack.Length == 0) row.Pack = pack;
                            // A newer pack can carry an event-gated copy of the same area: prefer it.
                            if (row.OpenEventId > 1) row.Pack = pack;
                            added++;
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("AreaCatalog: XML scan failed: " + e.Message);
            }
            return added;
        }

        private static string Text(XmlDocument doc, string path)
        {
            try
            {
                var node = doc.SelectSingleNode(path);
                return node != null ? (node.InnerText ?? "").Trim() : "";
            }
            catch (Exception) { return ""; }
        }

        private static int Int(XmlDocument doc, string path)
        {
            int value;
            return int.TryParse(Text(doc, path), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static bool Bool(XmlDocument doc, string path)
        {
            string text = Text(doc, path);
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }

        // ================================================================ helpers

        private static AreaRow GetOrAdd(Dictionary<int, AreaRow> map, int id)
        {
            AreaRow row;
            if (map.TryGetValue(id, out row)) return row;
            row = new AreaRow();
            row.Id = id;
            map[id] = row;
            return row;
        }

        private static string StreamingAssets()
        {
            try
            {
                string sa = UnityEngine.Application.streamingAssetsPath;
                if (!string.IsNullOrEmpty(sa) && Directory.Exists(sa)) return sa;
            }
            catch (Exception) { }
            try
            {
                string sa = Path.Combine(Environment.CurrentDirectory, "Sinmai_Data");
                sa = Path.Combine(sa, "StreamingAssets");
                if (Directory.Exists(sa)) return sa;
            }
            catch (Exception) { }
            try
            {
                string sa = Path.Combine(Environment.CurrentDirectory, "StreamingAssets");
                if (Directory.Exists(sa)) return sa;
            }
            catch (Exception) { }
            return null;
        }

        private static DataManager GetDataManager()
        {
            try { return Singleton<DataManager>.Instance; }
            catch (Exception) { return null; }
        }
    }
}
