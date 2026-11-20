using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;
using FakerSwap.Core;

namespace FakerSwap.Data
{
    /// <summary>
    /// Read access to the game's own music database.
    ///
    /// The music data ships as plain-text XML on disk (StreamingAssets/&lt;pack&gt;/music/musicXXXXXX/Music.xml),
    /// so we read it straight from the file system instead of going through DB/Manager.DataManager:
    /// that works before login, never touches obfuscated method bodies, and gives us the
    /// <c>disable</c> / <c>isEnable</c> flags we need to filter charts that this build does not have.
    /// (The chart files themselves — *.ma2 — carry a statistics footer with the exact per-type note
    /// counts, the all-perfect base score and the break-bonus score, which is what <see cref="GetStats"/> uses.)
    /// </summary>
    public static class ChartCatalog
    {
        // ------------------------------------------------------------------ model

        public sealed class NoteInfo
        {
            public int Level;          // displayed level, 0 when the data only carries musicLevelID
            public int LevelDecimal;   // internal-level decimal (tenths: 13.7 -> Level=13, LevelDecimal=7)
            public int MusicLevelId;   // index into the "musicLevel" table (used by custom packs)
            public int MaxNotes;
            public bool Enable;
            public string ChartFile;   // "000008_03.ma2"
        }

        public sealed class MusicInfo
        {
            public int Id;
            public string Title = "";
            public string Artist = "";
            public int Bpm;
            public int Version;
            public bool Disable;
            public string DirPath = "";
            public string XmlPath = "";
            public string PackName = "";
            public NoteInfo[] Notes;   // always 6 slots (0..4 = BASIC..RE:MASTER, 5 = STRONG/utage)
        }

        // ------------------------------------------------------------------ state

        private static readonly object Sync = new object();
        private static readonly Dictionary<int, MusicInfo> MusicCache = new Dictionary<int, MusicInfo>();
        private static readonly Dictionary<int, Manager.MaiStudio.MusicData> RuntimeMusicCache =
            new Dictionary<int, Manager.MaiStudio.MusicData>();
        private static readonly Dictionary<int, double> LengthCache = new Dictionary<int, double>();
        private static readonly Dictionary<int, double> SongLengthCache = new Dictionary<int, double>();
        private static readonly Dictionary<int, string> DirIndex = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> PackByMusicId = new Dictionary<int, string>();
        private static readonly Dictionary<int, int> LevelTable = new Dictionary<int, int>();
        private static readonly HashSet<int> WarnedSongs = new HashSet<int>();

        private static bool _indexed;
        private static bool _levelTableLoaded;
        private static string _scanError = "";
        private static List<string> _roots = new List<string>();
        private static bool _runtimeWarned;

        /// <summary>How many songs the scanner found in this build's data packs (0 until the first query).</summary>
        public static int KnownMusicCount { get { lock (Sync) { EnsureIndexLocked(); return DirIndex.Count; } } }

        public static string ScanError { get { lock (Sync) { EnsureIndexLocked(); return _scanError; } } }

        /// <summary>Forget everything (call again after AMDaemon/option packs are mounted).</summary>
        public static void ResetCache()
        {
            lock (Sync)
            {
                MusicCache.Clear();
                RuntimeMusicCache.Clear();
                LengthCache.Clear();
                SongLengthCache.Clear();
                DirIndex.Clear();
                PackByMusicId.Clear();
                LevelTable.Clear();
                WarnedSongs.Clear();
                _indexed = false;
                _levelTableLoaded = false;
                _runtimeWarned = false;
                _scanError = "";
                _roots = new List<string>();
            }
        }

        // ------------------------------------------------------------------ public API (contract)

        /// <summary>True when this game build actually ships the chart (delisted / missing songs must be skipped).</summary>
        public static bool Exists(int musicId, int level)
        {
            try
            {
                if (level < 0 || level > 4) return false;

                // Online view first: DB/Manager.DataManager is what the game actually loaded
                // (it applies option packs, disables and version rules).  Falls back to the XML
                // index before login / before the data tables finished loading.
                var dm = GetDataManager();
                if (dm != null && IsDataManagerLoaded(dm))
                {
                    var mv = GetRuntimeMusic(dm, musicId);
                    if (mv == null) return false;
                    if (mv.disable) return false;
                    var notes = mv.notesData;
                    if (notes == null || level >= notes.Count) return false;
                    var rn = notes[level];
                    return rn != null && rn.isEnable && rn.maxNotes > 0;
                }

                var mi = GetMusic(musicId);
                if (mi == null || mi.Disable || mi.Notes == null) return false;
                if (level >= mi.Notes.Length) return false;
                var n = mi.Notes[level];
                return n != null && n.Enable && n.MaxNotes > 0;
            }
            catch (Exception e)
            {
                Log.Error("ChartCatalog.Exists(" + musicId + "," + level + ") failed", e);
                return false;
            }
        }

        /// <summary>
        /// The game's own "can this account play this difficulty" check (NotesListManager).
        /// MASTER/Re:MASTER of event-locked songs return false here even though the chart exists;
        /// selecting such a chart in the song list just does nothing and the flow would spin on the
        /// difficulty screen until the 25s selection timeout.
        ///
        /// NOTE: the running game build differs from Libs/Assembly-CSharp.dll, so the method must be
        /// found and invoked **via reflection** (a direct call threw MissingMethodException).
        /// </summary>
        public static bool IsDifficultyUnlocked(int musicId, int level)
        {
            if (level <= 2) return true;   // BASIC/ADVANCED/EXPERT are always available
            try
            {
                var nlm = MAI2.Util.Singleton<Manager.NotesListManager>.Instance;
                if (nlm == null) return true;
                string name = level == 4 ? "IsUnlockReMaster" : "IsUnlockMaster";
                var method = FindInstanceMethod(nlm.GetType(), name);
                if (method == null) return true;   // build without that method -> do not filter
                var ps = method.GetParameters();
                if (ps.Length != 2) return true;
                object arg2 = ps[1].ParameterType.IsEnum
                    ? Enum.ToObject(ps[1].ParameterType, 0)
                    : (object)0;
                object result = method.Invoke(method.IsStatic ? null : (object)nlm, new object[] { musicId, arg2 });
                return result is bool ? (bool)result : true;
            }
            catch (Exception e)
            {
                if (WarnedSongs.Add(-musicId))
                    Log.Warn("ChartCatalog.IsDifficultyUnlocked(" + musicId + "," + level + ") failed: " +
                             e.GetType().Name + ": " + e.Message);
            }
            return true;
        }

        private static MethodInfo FindInstanceMethod(System.Type type, string name)
        {
            try
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Instance | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                    if (methods[i].Name == name) return methods[i];
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Song-level lock check via the game's NotesWrapper (IsNeedUnlock / IsUnlock), read through
        /// reflection only (no compile-time NotesWrapper reference) so a build without those members
        /// simply skips the filter.  Returns true when unsure - we never drop a playable chart.
        /// </summary>
        public static bool IsChartUnlocked(int musicId, int level)
        {
            try
            {
                var nlm = MAI2.Util.Singleton<Manager.NotesListManager>.Instance;
                if (nlm == null) return true;
                var listMethod = FindInstanceMethod(nlm.GetType(), "GetNotesList");
                if (listMethod == null || listMethod.GetParameters().Length != 0) return true;
                object list = listMethod.Invoke(listMethod.IsStatic ? null : (object)nlm, null);
                var dict = list as System.Collections.IDictionary;
                if (dict == null) return true;
                object wrapper = dict[musicId];
                if (wrapper == null) return true;

                var type = wrapper.GetType();
                bool need = false;
                var needProp = type.GetProperty("IsNeedUnlock");
                if (needProp != null)
                {
                    var value = needProp.GetValue(wrapper, null);
                    if (value is bool) need = (bool)value;
                }
                var unlockProp = type.GetProperty("IsUnlock");
                if (unlockProp == null) return true;
                var unlock = unlockProp.GetValue(wrapper, null);
                if (unlock == null) return true;
                var perLevel = unlock as bool[];
                if (perLevel != null)
                {
                    if (level >= 0 && level < perLevel.Length) return perLevel[level];
                    return true;
                }
                if (unlock is bool)
                {
                    bool value = (bool)unlock;
                    return !need || value;   // 只有「需要解禁且未解禁」才判为不可玩
                }
            }
            catch (Exception) { }
            return true;
        }

        /// <summary>Note statistics for one chart, or null when the chart does not exist.</summary>
        public static ChartStats GetStats(int musicId, int level)
        {
            try
            {
                if (!Exists(musicId, level)) return null;
                var mi = GetMusic(musicId);
                var stats = mi != null ? ReadChartStats(mi, mi.Notes[level]) : null;
                if (stats != null && stats.IsValid) return stats;

                // Fall back to a coarse-but-usable model: treat every note as a tap.
                int maxNotes = GetMaxNotes(musicId, level);
                if (maxNotes <= 0) return null;
                if (WarnedSongs.Add(musicId * 8 + level))
                    Log.Warn("ChartCatalog: no .ma2 statistics for " + musicId + "-" + level + " (falling back to maxNotes-only stats)");
                return new ChartStats
                {
                    MusicId = musicId,
                    Level = level,
                    TapCount = maxNotes,
                    HoldCount = 0,
                    SlideCount = 0,
                    TouchCount = 0,
                    BreakCount = 0,
                    AllPerfectBaseScore = maxNotes * 500.0,
                    BreakBonusScore = 0,
                    TheoryDx = maxNotes * 3,
                    NoteCount = maxNotes
                };
            }
            catch (Exception e)
            {
                Log.Error("ChartCatalog.GetStats(" + musicId + "," + level + ") failed", e);
                return null;
            }
        }

        public static string GetTitle(int musicId)
        {
            try
            {
                var dm = GetDataManager();
                if (dm != null)
                {
                    var mv = GetRuntimeMusic(dm, musicId);
                    if (mv != null && mv.name != null && !string.IsNullOrEmpty(mv.name.str)) return mv.name.str;
                }
                var mi = GetMusic(musicId);
                if (mi != null && !string.IsNullOrEmpty(mi.Title)) return mi.Title;
            }
            catch (Exception) { }
            return "#" + musicId;
        }

        public static string GetArtist(int musicId)
        {
            try
            {
                var dm = GetDataManager();
                if (dm != null)
                {
                    var mv = GetRuntimeMusic(dm, musicId);
                    if (mv != null && mv.artistName != null) return mv.artistName.str;
                }
                var mi = GetMusic(musicId);
                if (mi != null) return mi.Artist;
            }
            catch (Exception) { }
            return "";
        }

        /// <summary>Extra seconds added after the last note to approximate the tail/outro of the audio.
        /// Calibrated against known song lengths: True Love Song (96s) charts end at 89.6s.</summary>
        public static double LengthTailSeconds = 5.0;

        /// <summary>Optional runtime override (returns seconds, &lt;= 0 = unknown). The flow/nav layer can point
        /// this at a game API that knows the real chart/audio length; it wins over the .ma2 estimate.</summary>
        public static Func<int, double> RuntimeLengthProvider;

        /// <summary>Song length in seconds; -1 when unknown. Uses the highest existing difficulty.</summary>
        public static double GetLengthSeconds(int musicId)
        {
            if (musicId <= 0) return -1;
            lock (Sync)
            {
                double cached;
                if (SongLengthCache.TryGetValue(musicId, out cached)) return cached;
            }
            double result = -1;
            try
            {
                if (RuntimeLengthProvider != null)
                {
                    try
                    {
                        double runtime = RuntimeLengthProvider(musicId);
                        if (runtime > 5 && runtime < 900) result = runtime;
                    }
                    catch (Exception) { }
                }
                if (result <= 0)
                {
                    for (int lv = (int)Difficulty.ReMaster; lv >= (int)Difficulty.Basic; lv--)
                    {
                        if (!Exists(musicId, lv)) continue;
                        double s = GetLengthSeconds(musicId, lv);
                        if (s > 0) { result = s; break; }
                    }
                }
                if (result <= 0) result = DefaultLengthFor(musicId);
            }
            catch (Exception e)
            {
                Log.Warn("ChartCatalog.GetLengthSeconds(" + musicId + ") failed: " + e.Message);
            }
            lock (Sync) SongLengthCache[musicId] = result;
            return result;
        }

        /// <summary>Length of one specific chart, in seconds (last note + tail); -1 when the chart is missing.</summary>
        public static double GetLengthSeconds(int musicId, int level)
        {
            if (musicId <= 0 || level < 0 || level > 4) return -1;
            int key = musicId * 8 + level;
            lock (Sync)
            {
                double cached;
                if (LengthCache.TryGetValue(key, out cached)) return cached;
            }
            double result = -1;
            try
            {
                if (Exists(musicId, level))
                {
                    var mi = GetMusic(musicId);
                    if (mi != null && mi.Notes != null && level < mi.Notes.Length)
                    {
                        double chartSeconds = ComputeChartSeconds(mi, mi.Notes[level]);
                        if (chartSeconds > 0)
                            result = ClampLength(chartSeconds + Math.Max(0, LengthTailSeconds));
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("ChartCatalog.GetLengthSeconds(" + musicId + "," + level + ") failed: " + e.Message);
            }
            lock (Sync) LengthCache[key] = result;
            return result;
        }

        private static double DefaultLengthFor(int musicId)
        {
            int lv = HighestLevel(musicId);
            if (lv >= (int)Difficulty.Master) return 120.0;
            if (lv == (int)Difficulty.Expert) return 110.0;
            if (lv >= 0) return 100.0;
            return 120.0;
        }

        private static double ClampLength(double seconds)
        {
            if (seconds < 20) return 20;
            if (seconds > 600) return 600;
            return seconds;
        }

        /// <summary>
        /// Time of the last note (in seconds) from a plain-text .ma2 chart, honouring the BPM/MET events.
        ///
        /// .ma2 layout used here: "BPM bar tick bpm" / "MET bar tick num den" set the tempo and the
        /// beats-per-bar for everything that follows; notes are "TOKEN bar tick ..." with tick in
        /// 0..RESOLUTION-1 of that bar (RESOLUTION is 384 in every chart shipped with SDEZ 1.65).
        /// </summary>
        private static double ComputeChartSeconds(MusicInfo mi, NoteInfo n)
        {
            string file = ResolveChartPath(mi, n);
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return -1;

            double bpm = 120.0;
            int beatsNum = 4, beatsDen = 4, resolution = 384;
            var events = new List<TimeEvent>();
            var notes = new List<NotePos>();
            try
            {
                using (var sr = new StreamReader(file, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length < 3 || line[0] == '#') continue;
                        var parts = line.Split('\t');
                        if (parts.Length < 2) continue;
                        string token = parts[0];
                        if (token == "BPM_DEF")
                        {
                            double v;
                            if (parts.Length > 1 && TryParseDouble(parts[1], out v) && v > 10) bpm = v;
                            continue;
                        }
                        if (token == "MET_DEF")
                        {
                            int a, b;
                            if (parts.Length > 2 && int.TryParse(parts[1], out a) && int.TryParse(parts[2], out b) && a > 0 && b > 0)
                            { beatsNum = a; beatsDen = b; }
                            continue;
                        }
                        if (token == "RESOLUTION")
                        {
                            int r;
                            if (parts.Length > 1 && int.TryParse(parts[1], out r) && r > 0) resolution = r;
                            continue;
                        }
                        if (token == "BPM")
                        {
                            int bar, tick; double v;
                            if (parts.Length > 3 && int.TryParse(parts[1], out bar) && int.TryParse(parts[2], out tick) && TryParseDouble(parts[3], out v))
                                events.Add(new TimeEvent { Bar = bar, Tick = tick, Kind = 0, A = v });
                            continue;
                        }
                        if (token == "MET")
                        {
                            int bar, tick, a, b;
                            if (parts.Length > 4 && int.TryParse(parts[1], out bar) && int.TryParse(parts[2], out tick) &&
                                int.TryParse(parts[3], out a) && int.TryParse(parts[4], out b))
                                events.Add(new TimeEvent { Bar = bar, Tick = tick, Kind = 1, A = a, B = b });
                            continue;
                        }
                        if (token.StartsWith("T_", StringComparison.Ordinal) || token.StartsWith("TTM_", StringComparison.Ordinal)) continue;
                        if (token == "VERSION" || token == "FES_MODE" || token == "CLK_DEF" || token == "COMPATIBLE_CODE") continue;

                        int nbar, ntick;
                        if (!int.TryParse(parts[1], out nbar) || !int.TryParse(parts[2], out ntick)) continue;
                        if (nbar < 0 || nbar > 2000 || ntick < 0 || ntick > resolution) continue;
                        var np = new NotePos { Bar = nbar, Tick = ntick };
                        // HLD lines carry the hold length in the 4th field (in ticks).
                        if (parts.Length > 3 && (token == "HLD" || token == "NMHLD" || token == "EXHLD" || token == "BRHLD" || token == "BXHLD" || token == "BRSCL"))
                        {
                            int len;
                            if (int.TryParse(parts[3], out len) && len > 0) np.DurationTicks = len;
                        }
                        notes.Add(np);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("ChartCatalog: cannot parse chart timing " + file + ": " + e.Message);
                return -1;
            }
            if (notes.Count == 0) return -1;

            events.Sort(CompareTimeEvent);
            notes.Sort(CompareNotePos);

            double beats = beatsNum * 4.0 / beatsDen;
            double time = 0;
            int curBar = 0, curTick = 0;
            double maxTime = 0;
            int ei = 0, ni = 0;
            while (ei < events.Count || ni < notes.Count)
            {
                bool takeEvent;
                if (ei < events.Count && ni < notes.Count)
                {
                    var ev0 = events[ei];
                    var no0 = notes[ni];
                    takeEvent = ev0.Bar < no0.Bar || (ev0.Bar == no0.Bar && ev0.Tick <= no0.Tick);
                }
                else takeEvent = ei < events.Count;

                if (takeEvent)
                {
                    var ev = events[ei];
                    Advance(ref time, ref curBar, ref curTick, ev.Bar, Math.Min(ev.Tick, resolution), beats, bpm, resolution);
                    if (ev.Kind == 0) bpm = ev.A > 10 ? ev.A : bpm;
                    else if (ev.A > 0 && ev.B > 0) beats = ev.A * 4.0 / ev.B;
                    ei++;
                }
                else
                {
                    var np = notes[ni];
                    Advance(ref time, ref curBar, ref curTick, np.Bar, Math.Min(np.Tick, resolution), beats, bpm, resolution);
                    double end = time;
                    if (np.DurationTicks > 0)
                        end += np.DurationTicks / (double)resolution * beats * 60.0 / bpm;
                    if (end > maxTime) maxTime = end;
                    ni++;
                }
            }
            return maxTime > 0 ? maxTime : -1;
        }

        private static void Advance(ref double time, ref int curBar, ref int curTick, int bar, int tick,
                                    double beats, double bpm, int resolution)
        {
            if (bar < curBar || (bar == curBar && tick < curTick)) return;
            double secPerBar = beats * 60.0 / Math.Max(20.0, bpm);
            if (bar == curBar)
            {
                time += (tick - curTick) / (double)resolution * secPerBar;
            }
            else
            {
                time += (resolution - curTick) / (double)resolution * secPerBar;
                time += (bar - curBar - 1) * secPerBar;
                time += tick / (double)resolution * secPerBar;
            }
            curBar = bar;
            curTick = tick;
        }

        private static int CompareTimeEvent(TimeEvent a, TimeEvent b)
        {
            int c = a.Bar.CompareTo(b.Bar);
            if (c != 0) return c;
            return a.Tick.CompareTo(b.Tick);
        }

        private static int CompareNotePos(NotePos a, NotePos b)
        {
            int c = a.Bar.CompareTo(b.Bar);
            if (c != 0) return c;
            return a.Tick.CompareTo(b.Tick);
        }

        private static bool TryParseDouble(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private sealed class TimeEvent
        {
            public int Bar;
            public int Tick;
            public int Kind; // 0 = BPM, 1 = MET
            public double A;
            public double B;
        }

        private sealed class NotePos
        {
            public int Bar;
            public int Tick;
            public int DurationTicks;
        }

        /// <summary>Highest difficulty that exists for this song, or -1.</summary>
        public static int HighestLevel(int musicId)
        {
            for (int lv = (int)Difficulty.ReMaster; lv >= (int)Difficulty.Basic; lv--)
                if (Exists(musicId, lv)) return lv;
            return -1;
        }

        // ------------------------------------------------------------------ extras

        /// <summary>Internal (rating) level: 13 + levelDecimal 7 -> 13.7. Falls back to level / musicLevel table.</summary>
        public static double InternalLevel(int musicId, int level)
        {
            try
            {
                var dm = GetDataManager();
                if (dm != null)
                {
                    var mv = GetRuntimeMusic(dm, musicId);
                    if (mv != null && mv.notesData != null && level >= 0 && level < mv.notesData.Count)
                    {
                        var rn = mv.notesData[level];
                        if (rn != null)
                        {
                            if (rn.level > 0) return rn.level + Math.Max(0, Math.Min(9, rn.levelDecimal)) / 10.0;
                            int t = MusicLevelToNumber(rn.musicLevelID);
                            if (t > 0) return t;
                        }
                    }
                }

                var mi = GetMusic(musicId);
                if (mi != null && mi.Notes != null && level >= 0 && level < mi.Notes.Length)
                {
                    var n = mi.Notes[level];
                    if (n != null)
                    {
                        if (n.Level > 0) return n.Level + Math.Max(0, Math.Min(9, n.LevelDecimal)) / 10.0;
                        int fromTable = MusicLevelToNumber(n.MusicLevelId);
                        if (fromTable > 0) return fromTable;
                        return n.Level;
                    }
                }
            }
            catch (Exception) { }
            return level;
        }

        /// <summary>Version tag of the song (Music.xml &lt;version&gt;), used only for logging.</summary>
        public static int Version(int musicId)
        {
            try
            {
                var dm = GetDataManager();
                if (dm != null)
                {
                    var mv = GetRuntimeMusic(dm, musicId);
                    if (mv != null) return mv.version;
                }
                var mi = GetMusic(musicId);
                if (mi != null) return mi.Version;
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>BPM of the song, 0 when unknown.</summary>
        public static int Bpm(int musicId)
        {
            try
            {
                var dm = GetDataManager();
                if (dm != null)
                {
                    var mv = GetRuntimeMusic(dm, musicId);
                    if (mv != null) return mv.bpm;
                }
                var mi = GetMusic(musicId);
                if (mi != null) return mi.Bpm;
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>maxNotes of one chart from the live DataManager, falling back to the XML.</summary>
        public static int GetMaxNotes(int musicId, int level)
        {
            try
            {
                var dm = GetDataManager();
                if (dm != null)
                {
                    var mv = GetRuntimeMusic(dm, musicId);
                    if (mv != null && mv.notesData != null && level >= 0 && level < mv.notesData.Count)
                    {
                        var rn = mv.notesData[level];
                        if (rn != null && rn.maxNotes > 0) return rn.maxNotes;
                    }
                }
                var mi = GetMusic(musicId);
                if (mi != null && mi.Notes != null && level >= 0 && level < mi.Notes.Length)
                {
                    var n = mi.Notes[level];
                    if (n != null) return n.MaxNotes;
                }
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>True when the game's DataManager is actually available (used by the UI/flow logs).</summary>
        public static bool RuntimeDataAvailable
        {
            get
            {
                try
                {
                    var dm = GetDataManager();
                    return dm != null && IsDataManagerLoaded(dm);
                }
                catch (Exception) { return false; }
            }
        }

        // ------------------------------------------------------------------ runtime (DataManager) access

        private static Manager.DataManager GetDataManager()
        {
            try
            {
                return MAI2.Util.Singleton<Manager.DataManager>.Instance;
            }
            catch (Exception e)
            {
                if (!_runtimeWarned)
                {
                    _runtimeWarned = true;
                    Log.Warn("ChartCatalog: DataManager unavailable, using XML data only: " + e.Message);
                }
                return null;
            }
        }

        private static bool IsDataManagerLoaded(Manager.DataManager dm)
        {
            try { return dm != null && dm.IsLoaded(); }
            catch (Exception) { return false; }
        }

        private static Manager.MaiStudio.MusicData GetRuntimeMusic(Manager.DataManager dm, int musicId)
        {
            if (dm == null) return null;
            lock (Sync)
            {
                Manager.MaiStudio.MusicData mv;
                if (RuntimeMusicCache.TryGetValue(musicId, out mv)) return mv;
                try
                {
                    mv = dm.GetMusic(musicId);
                }
                catch (Exception)
                {
                    mv = null;
                }
                RuntimeMusicCache[musicId] = mv;
                return mv;
            }
        }

        /// <summary>Which data pack the song came from ("A000", "A600", ...); empty when unknown.</summary>
        public static string PackOf(int musicId)
        {
            try
            {
                var mi = GetMusic(musicId);
                if (mi != null) return mi.PackName;
            }
            catch (Exception) { }
            return "";
        }

        /// <summary>Number of enabled charts of one song (0..5), for diagnostics.</summary>
        public static int ChartCount(int musicId)
        {
            int c = 0;
            for (int i = 0; i <= 4; i++)
                if (Exists(musicId, i)) c++;
            return c;
        }

        /// <summary>All rock-solid public data roots that were scanned (for the log/UI).</summary>
        public static List<string> ScannedRoots()
        {
            lock (Sync)
            {
                EnsureIndexLocked();
                return new List<string>(_roots);
            }
        }

        // ------------------------------------------------------------------ index

        private static string GetStreamingAssets()
        {
            try
            {
                var sa = UnityEngine.Application.streamingAssetsPath;
                if (!string.IsNullOrEmpty(sa) && Directory.Exists(sa)) return sa;
            }
            catch (Exception) { }
            try
            {
                var sa = Path.Combine(Environment.CurrentDirectory, "Sinmai_Data");
                sa = Path.Combine(sa, "StreamingAssets");
                if (Directory.Exists(sa)) return sa;
            }
            catch (Exception) { }
            try
            {
                var sa = Path.Combine(Environment.CurrentDirectory, "StreamingAssets");
                if (Directory.Exists(sa)) return sa;
            }
            catch (Exception) { }
            return null;
        }

        private static void EnsureIndexLocked()
        {
            if (_indexed) return;
            _indexed = true;
            try
            {
                var roots = new List<string>();
                string sa = GetStreamingAssets();
                if (!string.IsNullOrEmpty(sa))
                {
                    roots.Add(sa);
                    try
                    {
                        foreach (var d in Directory.GetDirectories(sa)) roots.Add(d);
                    }
                    catch (Exception) { }
                }

                // Option packs mounted by AMDaemon (extra songs may live there). Best effort only.
                try
                {
                    var dm = MAI2.Util.Singleton<Manager.DataManager>.Instance;
                    if (dm != null && !string.IsNullOrEmpty(sa))
                    {
                        var dirs = new List<string>();
                        dm.GetDirs(sa, AMDaemon.AppImage.OptionMountRootPath, ref dirs);
                        if (dirs != null)
                        {
                            foreach (var d in dirs)
                            {
                                if (string.IsNullOrEmpty(d)) continue;
                                if (!roots.Contains(d)) roots.Add(d);
                            }
                        }
                    }
                }
                catch (Exception) { }

                // Collect (pack, musicDir) candidates; A000 wins duplicate ids over older packs.
                var candidates = new List<KeyValuePair<string, string>>();
                foreach (var root in roots)
                {
                    string musicDir = Path.Combine(root, "music");
                    if (!Directory.Exists(musicDir))
                    {
                        // The root itself may already be a pack directory.
                        if (string.Equals(Path.GetFileName(root), "music", StringComparison.OrdinalIgnoreCase))
                            musicDir = root;
                        else
                            continue;
                    }
                    string pack = Path.GetFileName(root);
                    if (string.IsNullOrEmpty(pack)) pack = root;
                    try
                    {
                        foreach (var d in Directory.GetDirectories(musicDir))
                        {
                            string name = Path.GetFileName(d);
                            if (name == null || !name.StartsWith("music", StringComparison.OrdinalIgnoreCase)) continue;
                            candidates.Add(new KeyValuePair<string, string>(d, pack));
                        }
                    }
                    catch (Exception e)
                    {
                        if (_scanError.Length == 0) _scanError = e.Message;
                    }
                    if (!_roots.Contains(root)) _roots.Add(root);
                }

                candidates.Sort(ComparePacks);
                foreach (var c in candidates)
                {
                    string name = Path.GetFileName(c.Key);
                    int id;
                    if (!int.TryParse(name.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
                    if (DirIndex.ContainsKey(id)) continue;
                    DirIndex[id] = c.Key;
                    PackByMusicId[id] = c.Value;
                }

                Log.Info("ChartCatalog: indexed " + DirIndex.Count + " songs from " + _roots.Count + " data roots" +
                         (_scanError.Length > 0 ? " (scan error: " + _scanError + ")" : ""));
            }
            catch (Exception e)
            {
                _scanError = e.Message;
                Log.Error("ChartCatalog: data scan failed", e);
            }
        }

        private static int ComparePacks(KeyValuePair<string, string> a, KeyValuePair<string, string> b)
        {
            int ra = PackRank(a.Value);
            int rb = PackRank(b.Value);
            if (ra != rb) return ra.CompareTo(rb);
            int c = string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
        }

        private static int PackRank(string pack)
        {
            // Prefer the current data pack ("A000") when several packs carry the same music id.
            if (string.Equals(pack, "A000", StringComparison.OrdinalIgnoreCase)) return 0;
            return 1;
        }

        // ------------------------------------------------------------------ xml

        private static MusicInfo GetMusic(int musicId)
        {
            if (musicId < 0) return null;
            lock (Sync)
            {
                EnsureIndexLocked();
                MusicInfo mi;
                if (MusicCache.TryGetValue(musicId, out mi)) return mi;

                string dir;
                if (!DirIndex.TryGetValue(musicId, out dir))
                {
                    MusicCache[musicId] = null;
                    return null;
                }
                mi = LoadMusicXml(musicId, dir);
                MusicCache[musicId] = mi;
                return mi;
            }
        }

        private static MusicInfo LoadMusicXml(int musicId, string dir)
        {
            var mi = new MusicInfo { Id = musicId, DirPath = dir };
            string pack;
            if (PackByMusicId.TryGetValue(musicId, out pack)) mi.PackName = pack;
            mi.XmlPath = Path.Combine(dir, "Music.xml");
            try
            {
                if (!File.Exists(mi.XmlPath)) return null;
                var doc = new XmlDocument();
                var settings = new XmlReaderSettings();
                settings.DtdProcessing = DtdProcessing.Ignore;
                settings.XmlResolver = null;
                settings.IgnoreComments = true;
                settings.IgnoreWhitespace = false;
                using (var reader = XmlReader.Create(mi.XmlPath, settings))
                {
                    doc.Load(reader);
                }
                var root = doc.DocumentElement;
                if (root == null) return null;
                mi.Disable = ReadBool(root, "disable");
                mi.Title = ReadStringId(root, "name");
                mi.Artist = ReadStringId(root, "artistName");
                mi.Bpm = ReadInt(root, "bpm");
                mi.Version = ReadInt(root, "version");

                mi.Notes = new NoteInfo[6];
                var notes = root.SelectNodes("notesData/Notes");
                if (notes != null)
                {
                    for (int i = 0; i < notes.Count && i < mi.Notes.Length; i++)
                    {
                        var el = notes[i] as XmlElement;
                        if (el == null) continue;
                        var n = new NoteInfo();
                        n.Level = ReadInt(el, "level");
                        n.LevelDecimal = ReadInt(el, "levelDecimal");
                        n.MusicLevelId = ReadInt(el, "musicLevelID");
                        n.MaxNotes = ReadInt(el, "maxNotes");
                        n.Enable = ReadBool(el, "isEnable");
                        n.ChartFile = ReadText(el, "file/path");
                        mi.Notes[i] = n;
                    }
                }
                for (int i = 0; i < mi.Notes.Length; i++) if (mi.Notes[i] == null) mi.Notes[i] = new NoteInfo();
                return mi;
            }
            catch (Exception e)
            {
                Log.Warn("ChartCatalog: failed to read " + mi.XmlPath + ": " + e.Message);
                return null;
            }
        }

        private static string ReadText(XmlNode node, string path)
        {
            try
            {
                if (node == null) return "";
                var n = node.SelectSingleNode(path);
                return n == null ? "" : (n.InnerText ?? "").Trim();
            }
            catch (Exception) { return ""; }
        }

        private static int ReadInt(XmlNode node, string path)
        {
            string s = ReadText(node, path);
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        private static bool ReadBool(XmlNode node, string path)
        {
            string s = ReadText(node, path);
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        private static string ReadStringId(XmlNode node, string path)
        {
            return ReadText(node, path + "/str");
        }

        // ------------------------------------------------------------------ chart statistics

        private static string ResolveChartPath(MusicInfo mi, NoteInfo n)
        {
            if (mi == null || n == null) return null;
            try
            {
                if (!string.IsNullOrEmpty(n.ChartFile))
                {
                    string p = Path.Combine(mi.DirPath, n.ChartFile);
                    if (File.Exists(p)) return p;
                }
                string guess = string.Format(CultureInfo.InvariantCulture, "{0:D6}_{1:D2}.ma2", mi.Id, n.Level);
                string g = Path.Combine(mi.DirPath, guess);
                if (File.Exists(g)) return g;
                // Any *.ma2 inside the folder as the last resort.
                var files = Directory.GetFiles(mi.DirPath, "*.ma2");
                if (files != null && files.Length > 0) return files[0];
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Reads the statistics footer that every .ma2 chart carries:
        ///   T_NUM_*      note counts (TAP includes slide heads / touch taps)
        ///   TTM_SCR_SS   all-perfect base score (500 * (tap + 2*hold + 3*slide + 5*break))
        ///   TTM_SCR_ALL  same, but break notes get their +100 critical bonus
        /// </summary>
        private static ChartStats ReadChartStats(MusicInfo mi, NoteInfo n)
        {
            string file = ResolveChartPath(mi, n);
            if (string.IsNullOrEmpty(file)) return null;
            var vals = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                using (var sr = new StreamReader(file, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length < 3) continue;
                        if (!line.StartsWith("T_", StringComparison.Ordinal) && !line.StartsWith("TTM_", StringComparison.Ordinal)) continue;
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        string key = line.Substring(0, tab);
                        int v;
                        if (int.TryParse(line.Substring(tab + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                            vals[key] = v;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("ChartCatalog: cannot read chart stats " + file + ": " + e.Message);
                return null;
            }
            if (vals.Count == 0) return null;

            int tap = Get(vals, "T_NUM_TAP");
            int hold = Get(vals, "T_NUM_HLD");
            int slide = Get(vals, "T_NUM_SLD");
            int brk = Get(vals, "T_NUM_BRK");
            if (tap <= 0 && hold <= 0 && slide <= 0 && brk <= 0)
            {
                tap = Get(vals, "T_REC_TAP") + Get(vals, "T_REC_STR") + Get(vals, "T_REC_TTP") + Get(vals, "T_REC_XTP");
                hold = Get(vals, "T_REC_HLD") + Get(vals, "T_REC_THO");
                slide = Get(vals, "T_REC_SLD");
                brk = Get(vals, "T_REC_BRK");
            }
            int total = Get(vals, "T_NUM_ALL");
            if (total <= 0) total = tap + hold + slide + brk;

            double apScore = Get(vals, "TTM_SCR_SS");
            double allScore = Get(vals, "TTM_SCR_ALL");
            double computed = 500.0 * (tap + 2.0 * hold + 3.0 * slide + 5.0 * brk);
            if (apScore <= 0) apScore = computed;
            if (allScore <= 0) allScore = apScore + 100.0 * brk;

            var st = new ChartStats
            {
                MusicId = mi.Id,
                Level = n.Level,
                TapCount = tap,
                HoldCount = hold,
                SlideCount = slide,
                TouchCount = 0,
                TouchTapCount = Get(vals, "T_REC_TTP") + Get(vals, "T_REC_XTP"),
                TouchHoldCount = Get(vals, "T_REC_THO"),
                BreakCount = brk,
                AllPerfectBaseScore = apScore,
                BreakBonusScore = Math.Max(0, allScore - apScore),
                TheoryDx = total * 3,
                NoteCount = total
            };
            if (Math.Abs(computed - apScore) > 1.0 && WarnedSongs.Add(mi.Id * 8 + n.Level + 100000))
                Log.Warn("ChartCatalog: " + mi.Id + "-" + n.Level + " ma2 stats mismatch (computed " + computed.ToString("0") + " vs footer " + apScore.ToString("0") + ")");
            return st;
        }

        private static int Get(Dictionary<string, int> d, string key)
        {
            int v;
            return d.TryGetValue(key, out v) ? v : 0;
        }

        // ------------------------------------------------------------------ musicLevel table (custom packs)

        private static int MusicLevelToNumber(int musicLevelId)
        {
            if (musicLevelId <= 0) return 0;
            if (!_levelTableLoaded)
            {
                _levelTableLoaded = true;
                try
                {
                    var roots = new List<string>(_roots);
                    var sa = GetStreamingAssets();
                    if (!string.IsNullOrEmpty(sa))
                    {
                        if (!roots.Contains(sa)) roots.Add(sa);
                        try
                        {
                            foreach (var d in Directory.GetDirectories(sa)) if (!roots.Contains(d)) roots.Add(d);
                        }
                        catch (Exception) { }
                    }
                    foreach (var root in roots)
                    {
                        string dir = Path.Combine(root, "musicLevel");
                        if (!Directory.Exists(dir)) continue;
                        foreach (var sub in Directory.GetDirectories(dir))
                        {
                            string xml = Path.Combine(sub, "MusicLevel.xml");
                            if (!File.Exists(xml)) continue;
                            try
                            {
                                var doc = new XmlDocument();
                                var settings = new XmlReaderSettings();
                                settings.DtdProcessing = DtdProcessing.Ignore;
                                settings.XmlResolver = null;
                                using (var reader = XmlReader.Create(xml, settings)) doc.Load(reader);
                                var el = doc.DocumentElement;
                                if (el == null) continue;
                                int id = ReadInt(el, "name/id");
                                int num = ReadInt(el, "levelNum");
                                if (id > 0 && num > 0) LevelTable[id] = num;
                            }
                            catch (Exception) { }
                        }
                        if (LevelTable.Count > 0) break;
                    }
                }
                catch (Exception e)
                {
                    Log.Warn("ChartCatalog: musicLevel table unavailable: " + e.Message);
                }
            }
            int lv;
            return LevelTable.TryGetValue(musicLevelId, out lv) ? lv : 0;
        }
    }
}
