using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FakerSwap.Core;

namespace FakerSwap.Data
{
    /// <summary>
    /// Persists "which areas were already crawled this round" so a restart does not re-run the
    /// areas the operator already finished.  Stored next to the mod: <c>FakerSwap/crawl-state.json</c>.
    /// The saved list is tied to the current CrawlAreaIds signature: when the operator changes the
    /// selection the round is reset.
    /// </summary>
    public static class CrawlState
    {
        private static string StatePath
        {
            get { return Path.Combine(Fs.Root, "crawl-state.json"); }
        }

        public static string Signature(IList<int> ids)
        {
            if (ids == null || ids.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ids[i]);
            }
            return sb.ToString();
        }

        /// <summary>Load the persisted "done" set into <paramref name="done"/>; returns false when the
        /// file is missing / unreadable / belongs to a different selection.</summary>
        public static bool Load(HashSet<int> done, out int round, IList<int> selection)
        {
            round = 1;
            if (done == null) return false;
            done.Clear();
            try
            {
                if (!File.Exists(StatePath)) return false;
                JsonValue root;
                string error;
                if (!JsonValue.TryParse(File.ReadAllText(StatePath), out root, out error)) return false;
                if (root.Kind != JsonKind.Object) return false;
                string saved = root["signature"].AsString("");
                if (saved != Signature(selection)) return false;   // selection changed -> new round
                round = Math.Max(1, root["round"].AsInt(1));
                var arr = root["done"];
                if (arr.Kind == JsonKind.Array)
                {
                    for (int i = 0; i < arr.Count; i++)
                    {
                        int id = arr[i].AsInt(0);
                        if (id > 0) done.Add(id);
                    }
                }
                return done.Count > 0;
            }
            catch (Exception e)
            {
                Log.Warn("CrawlState.Load failed: " + e.Message);
                return false;
            }
        }

        public static void Save(HashSet<int> done, int round, IList<int> selection)
        {
            try
            {
                Fs.EnsureDirs();
                var root = JsonValue.NewObject();
                root["signature"] = JsonValue.Of(Signature(selection));
                root["round"] = JsonValue.Of(round);
                var arr = JsonValue.NewArray();
                if (done != null)
                {
                    var ids = new List<int>(done);
                    ids.Sort();
                    for (int i = 0; i < ids.Count; i++) arr.Add(JsonValue.Of(ids[i]));
                }
                root["done"] = arr;
                root["savedAt"] = JsonValue.Of(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                File.WriteAllText(StatePath, root.ToJson(true));
            }
            catch (Exception e)
            {
                Log.Warn("CrawlState.Save failed: " + e.Message);
            }
        }

        public static void Clear()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); }
            catch (Exception e) { Log.Warn("CrawlState.Clear failed: " + e.Message); }
        }
    }
}
