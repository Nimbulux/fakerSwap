using System;
using System.Collections.Generic;
using FakerSwap.Core;
using FakerSwap.Flow;

namespace FakerSwap.UI
{
    /// <summary>UI-side copy of one map area, so the window thread never touches nav objects.</summary>
    internal sealed class AreaInfoDto
    {
        public int Index;
        public int Id = -1;
        public string Name = "";
        public bool IsClear;
        public bool IsComplete;
        public bool IsLock;
        public bool IsEvent;
        public bool IsInfinity;
        public bool IsCurrent;
        public uint Distance;
        public int Progress;
        public int Total;
    }

    /// <summary>
    /// Direct access to the area-crawl feature (config + nav API).
    ///
    /// Read/write rules:
    ///   * happens on the Unity main thread only (`UIHost.Tick` / `MainThread.Post` handlers),
    ///   * writes go through <see cref="ModConfig.Data"/> + <see cref="ModConfig.Save()"/>,
    ///   * the window thread only ever sees the immutable snapshot.
    /// </summary>
    internal static class AreaBridge
    {
        public static string LastError { get; private set; } = "";

        public static bool ConfigAvailable
        {
            get { return true; }
        }

        public static bool NavigationAvailable
        {
            get { return true; }
        }

        public static int SelectedAreaId
        {
            get
            {
                try { return MapHooks.SelectedAreaId; }
                catch (Exception) { return -1; }
            }
        }

        public static string SelectedAreaName
        {
            get
            {
                try { return MapHooks.SelectedAreaName ?? ""; }
                catch (Exception) { return ""; }
            }
        }

        // ---------------------------------------------------------------- selection (main thread)

        public static void ReadSelection(List<int> ids, out bool fallback)
        {
            fallback = false;
            if (ids == null) return;
            ids.Clear();
            try
            {
                var data = ModConfig.Data;
                if (data.CrawlAreaIds != null)
                {
                    for (int i = 0; i < data.CrawlAreaIds.Count; i++) ids.Add(data.CrawlAreaIds[i]);
                }
                fallback = data.CrawlAllAreasFallback;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log.Warn("AreaBridge: reading CrawlAreaIds failed: " + e.Message);
            }
        }

        /// <summary>Main thread only.  Writes ConfigData.CrawlAreaIds and saves config.json.</summary>
        public static bool WriteSelection(int[] ids)
        {
            try
            {
                var data = ModConfig.Data;
                if (data.CrawlAreaIds == null) data.CrawlAreaIds = new List<int>();
                data.CrawlAreaIds.Clear();
                if (ids != null)
                {
                    for (int i = 0; i < ids.Length; i++) data.CrawlAreaIds.Add(ids[i]);
                }
                ModConfig.Save();
                Log.Info("AreaBridge: CrawlAreaIds saved (" + (ids != null ? ids.Length : 0) + " area(s))");
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log.Error("AreaBridge: WriteSelection failed", e);
                return false;
            }
        }

        /// <summary>Main thread only.  Writes CrawlAllAreasFallback and saves config.json.</summary>
        public static bool WriteFallback(bool value)
        {
            try
            {
                ModConfig.Data.CrawlAllAreasFallback = value;
                ModConfig.Save();
                Log.Info("AreaBridge: CrawlAllAreasFallback = " + value);
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log.Error("AreaBridge: WriteFallback failed", e);
                return false;
            }
        }

        // ---------------------------------------------------------------- area list (main thread)

        /// <summary>
        /// Calls <see cref="Navigation.ListAreas"/> (only lists areas while the game is on the
        /// area-select screen; the UI keeps the last non-empty result as a cache).
        /// </summary>
        public static List<AreaInfoDto> ReadAreas()
        {
            var result = new List<AreaInfoDto>();
            LastError = "";
            try
            {
                List<Navigation.AreaInfo> areas = Navigation.ListAreas(0);
                if (areas == null)
                {
                    LastError = "区域列表暂不可用（等待 nav 接口）";
                    return result;
                }
                for (int i = 0; i < areas.Count; i++)
                {
                    var area = areas[i];
                    if (area == null) continue;
                    var dto = new AreaInfoDto();
                    dto.Index = area.Index;
                    dto.Id = area.Id;
                    dto.Name = area.Name ?? "";
                    dto.IsClear = area.IsClear;
                    dto.IsComplete = area.IsComplete;
                    dto.IsLock = area.IsLock;
                    dto.IsEvent = area.IsEvent;
                    dto.IsInfinity = area.IsInfinity;
                    dto.IsCurrent = area.IsCurrent;
                    dto.Distance = area.Distance;
                    // nav's AreaInfo currently has no Progress/Total counters; Distance is the
                    // crawl progress.  Keep the fields so a future nav extension maps in easily.
                    dto.Progress = 0;
                    dto.Total = 0;
                    if (dto.Name.Length == 0) dto.Name = "区域 #" + dto.Id;
                    if (dto.Id >= 0) result.Add(dto);
                }
                if (result.Count == 0)
                    LastError = "当前不在区域选择页 — 区域列表会在选区页出现时刷新（也可点「刷新列表」）";
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log.Warn("AreaBridge: ListAreas failed: " + e.Message);
            }
            return result;
        }
    }
}
