using System;
using System.Collections.Generic;
using System.Reflection;
using FakerSwap.Core;
using Manager;
using Manager.UserDatas;

namespace FakerSwap.Data
{
    /// <summary>
    /// Reads the *real* collection lists out of <c>UserData</c>.
    ///
    /// Other mods (e.g. Sinmai-Assist's AllCollection) Harmony-patch the
    /// <c>UserData.get_TitleList / get_IconList / …</c> getters to return every master item.  Reading
    /// those properties would make us believe the account already owns everything (and, worse,
    /// writing that patched list back would upload thousands of rows).  The backing fields
    /// (<c>_titleList</c> …) are untouched by those patches, so we read them directly via reflection
    /// and only fall back to the property when a field is missing in this build.
    /// </summary>
    public static class UserDataAccess
    {
        private static readonly string[] CollectionFields =
        {
            "_titleList", "_iconList", "_plateList", "_frameList", "_partnerList"
        };

        private static readonly string[] CollectionProps =
        {
            "TitleList", "IconList", "PlateList", "FrameList", "PartnerList"
        };

        /// <summary>Possible backing-field names of <c>UserData.CharaList</c> (field names differ per build).</summary>
        private static readonly string[] CharaFields =
        {
            "_userCharaList", "userCharaList", "_charaList", "charaList"
        };

        private static readonly Dictionary<string, FieldInfo> FieldCache = new Dictionary<string, FieldInfo>();
        private static readonly object Sync = new object();

        /// <summary>Real (field-backed) collection list for kind 0..4 (title/icon/plate/frame/partner).</summary>
        public static List<UserItem> GetCollectionList(UserData ud, int kind)
        {
            if (ud == null || kind < 0 || kind >= CollectionFields.Length) return null;
            try
            {
                FieldInfo field = FindField(ud.GetType(), CollectionFields[kind]);
                if (field != null)
                {
                    var list = field.GetValue(ud) as List<UserItem>;
                    if (list != null) return list;
                }
            }
            catch (Exception e)
            {
                Log.Warn("UserDataAccess: field " + CollectionFields[kind] + " failed: " + e.Message);
            }
            try { return GetProperty(ud, CollectionProps[kind]); }
            catch (Exception) { return null; }
        }

        /// <summary>Write a collection list back (field when available, otherwise the property).</summary>
        public static void SetCollectionList(UserData ud, int kind, List<UserItem> list)
        {
            if (ud == null || list == null || kind < 0 || kind >= CollectionFields.Length) return;
            try
            {
                FieldInfo field = FindField(ud.GetType(), CollectionFields[kind]);
                if (field != null)
                {
                    field.SetValue(ud, list);
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Warn("UserDataAccess: set field " + CollectionFields[kind] + " failed: " + e.Message);
            }
            try
            {
                var prop = ud.GetType().GetProperty(CollectionProps[kind], BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite) prop.SetValue(ud, list, null);
            }
            catch (Exception e)
            {
                Log.Warn("UserDataAccess: set property " + CollectionProps[kind] + " failed: " + e.Message);
            }
        }

        /// <summary>True when the real lists are readable (diagnostics for the UI/log).</summary>
        public static bool RealListsAvailable(UserData ud)
        {
            if (ud == null) return false;
            try
            {
                FieldInfo field = FindField(ud.GetType(), CollectionFields[0]);
                return field != null && field.GetValue(ud) is List<UserItem>;
            }
            catch (Exception) { return false; }
        }

        private static List<UserItem> GetProperty(UserData ud, string name)
        {
            var prop = ud.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return prop != null ? prop.GetValue(ud, null) as List<UserItem> : null;
        }

        // ---------------------------------------------------------------- characters (旅行伙伴)

        /// <summary>Real character list (field-backed, never throws).</summary>
        public static List<UserChara> GetCharaList(UserData ud)
        {
            if (ud == null) return null;
            for (int i = 0; i < CharaFields.Length; i++)
            {
                try
                {
                    FieldInfo field = FindFieldOfType(ud.GetType(), CharaFields[i], typeof(List<UserChara>));
                    if (field == null) continue;
                    var list = field.GetValue(ud) as List<UserChara>;
                    if (list != null) return list;
                }
                catch (Exception) { }
            }
            try
            {
                var prop = ud.GetType().GetProperty("CharaList", BindingFlags.Public | BindingFlags.Instance);
                return prop != null ? prop.GetValue(ud, null) as List<UserChara> : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// True when the account owns at least one character.  Checks the real list first, then
        /// <c>UserDetail.CharaSlot</c> (the 5 partner slots the game composes at entry).
        /// </summary>
        public static bool HasAnyCharacter(UserData ud)
        {
            if (ud == null) return false;
            try
            {
                var list = GetCharaList(ud);
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var c = list[i];
                        if (c != null && c.ID > 0) return true;
                    }
                }
            }
            catch (Exception) { }
            try
            {
                var detail = ud.Detail;
                if (detail != null)
                {
                    var slots = ReadMember(detail, "CharaSlot") as Array;
                    if (slots != null)
                    {
                        foreach (var value in slots)
                        {
                            if (value == null) continue;
                            if (Convert.ToInt32(value) > 0) return true;
                        }
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        private static object ReadMember(object target, string name)
        {
            if (target == null) return null;
            try
            {
                var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(target, null);
            }
            catch (Exception) { }
            try
            {
                var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null) return field.GetValue(target);
            }
            catch (Exception) { }
            return null;
        }

        private static FieldInfo FindFieldOfType(System.Type type, string name, System.Type fieldType)
        {
            lock (Sync)
            {
                string key = type.FullName + ":" + name + ":" + fieldType.Name;
                FieldInfo cached;
                if (FieldCache.TryGetValue(key, out cached)) return cached;
                FieldInfo found = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (found != null && found.FieldType != fieldType) found = null;
                FieldCache[key] = found;
                return found;
            }
        }

        private static FieldInfo FindField(System.Type type, string name)
        {
            lock (Sync)
            {
                FieldInfo cached;
                string key = type.FullName + ":" + name;
                if (FieldCache.TryGetValue(key, out cached)) return cached;
                FieldInfo found = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (found == null)
                {
                    // Some builds drop the underscore.
                    string alt = name.TrimStart('_');
                    found = type.GetField(alt, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (found == null || found.FieldType != typeof(List<UserItem>)) found = null;
                FieldCache[key] = found;
                return found;
            }
        }
    }
}
