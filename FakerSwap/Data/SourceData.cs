using System;
using System.Collections.Generic;
using System.IO;
using FakerSwap.Core;

namespace FakerSwap.Data
{
    /// <summary>
    /// Loading of "source account" data.  Two shapes are understood:
    ///   * Astral capture:  data/&lt;userId&gt;.json  ->  { userData..., userMusicDetailList: [ ... ] }
    ///   * FakerSwap backup:  FakerSwap/Backup/用户名-日期-rating.json
    /// Both end up as a <see cref="SourceSnapshot"/>.
    /// </summary>
    public static class SourceData
    {
        /// <summary>Sniff the format and load.  Never throws.</summary>
        public static bool TryLoad(string path, out SourceSnapshot snapshot, out string error)
        {
            snapshot = null;
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    error = "文件不存在: " + path;
                    return false;
                }
                var text = File.ReadAllText(path);
                JsonValue root;
                string perr;
                if (!JsonValue.TryParse(text, out root, out perr))
                {
                    error = "JSON 解析失败: " + perr;
                    return false;
                }

                if (root.Has("records") && root.Has("userName"))
                {
                    snapshot = LoadBackup(root, path);
                    return snapshot != null;
                }

                var snap = LoadAstral(root, path);
                if (snap != null) { snapshot = snap; return true; }
                error = "无法识别的数据格式（既不是 FakerSwap 备份也不是 Astral 抓包）";
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                Log.Error("SourceData.TryLoad failed: " + path, e);
                return false;
            }
        }

        // ---------------------------------------------------------------- Astral

        private static SourceSnapshot LoadAstral(JsonValue root, string path)
        {
            var list = root["userMusicDetailList"];
            if (list.Kind != JsonKind.Array)
            {
                // Astral stores either the bare UpsertUserAll body or a wrapper; try one level down.
                foreach (var kv in root.Props)
                {
                    if (kv.Value.Kind == JsonKind.Object && kv.Value["userMusicDetailList"].Kind == JsonKind.Array)
                    {
                        root = kv.Value;
                        list = root["userMusicDetailList"];
                        break;
                    }
                }
            }
            if (list.Kind != JsonKind.Array) return null;

            var snap = new SourceSnapshot { Path = path, Format = "astral", SavedAt = File.GetLastWriteTime(path) };
            var userData = root["userData"];
            snap.UserName = root["userName"].AsString(userData["userName"].AsString(""));
            snap.UserId = root["userId"].AsLong(userData["userId"].AsLong(0));
            snap.Rating = root["playerRating"].AsInt(userData["playerRating"].AsInt(0));
            snap.HighestRating = root["highestRating"].AsInt(snap.Rating);
            if (string.IsNullOrEmpty(snap.UserName)) snap.UserName = "uid" + snap.UserId;

            for (int i = 0; i < list.Count; i++)
            {
                var r = ReadRecord(list[i]);
                if (r != null) snap.Records.Add(r);
            }
            return snap;
        }

        private static ScoreRecord ReadRecord(JsonValue v)
        {
            if (v.Kind != JsonKind.Object) return null;
            int musicId = v["musicId"].AsInt(-1);
            int level = v["level"].AsInt(-1);
            if (musicId < 0 || level < 0) return null;
            return new ScoreRecord
            {
                MusicId = musicId,
                Level = level,
                PlayCount = v["playCount"].AsInt(1),
                Achievement = v["achievement"].AsInt(0),
                ComboStatus = v["comboStatus"].AsInt(0),
                SyncStatus = v["syncStatus"].AsInt(0),
                DeluxscoreMax = v["deluxscoreMax"].AsInt(0),
                ScoreRank = v["scoreRank"].AsInt(0),
                ExtNum1 = v["extNum1"].AsInt(0)
            };
        }

        // ---------------------------------------------------------------- FakerSwap backup

        private static SourceSnapshot LoadBackup(JsonValue root, string path)
        {
            var snap = new SourceSnapshot
            {
                Path = path,
                Format = "backup",
                UserName = root["userName"].AsString(""),
                UserId = root["userId"].AsLong(0),
                Rating = root["rating"].AsInt(0),
                HighestRating = root["highestRating"].AsInt(0),
                SavedAt = File.GetLastWriteTime(path)
            };
            var arr = root["records"];
            for (int i = 0; i < arr.Count; i++)
            {
                var r = ReadRecord(arr[i]);
                if (r != null) snap.Records.Add(r);
            }
            return snap;
        }
    }
}
