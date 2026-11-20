using System;
using FakerSwap.Core;
using FakerSwap.Data;
using FakerSwap.Flow;
using FakerSwap.UI.NativeWindow;

namespace FakerSwap.UI
{
    /// <summary>
    /// Every button in the standalone window funnels through here.  The window thread must never
    /// touch Unity or a game object, so each action is marshalled back to the game main thread with
    /// <see cref="MainThread.Post"/>; the only exceptions are window-local operations
    /// (hide/show/topmost) which just post a Win32 message to the window thread itself.
    /// </summary>
    internal static class UiActions
    {
        // ---------------------------------------------------------------- source data

        public static void UseSource(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                WindowRequests.Notice("请先选择一个备份文件");
                return;
            }
            WindowRequests.Notice("正在载入：" + SafeFileName(path));
            MainThread.Post(() =>
            {
                try
                {
                    string error;
                    if (RebirthFlow.UseSourceFile(path, out error))
                    {
                        Log.Info("UI: source file selected: " + path);
                        WindowRequests.Notice("已载入源数据");
                    }
                    else
                    {
                        WindowRequests.Notice("载入失败：" + error);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("UI: UseSourceFile failed", e);
                    WindowRequests.Notice("载入失败：" + e.Message);
                }
            });
        }

        public static void BeginSourceAcquire()
        {
            WindowRequests.Notice("已请求源账号登录，请在机台刷卡");
            MainThread.Post(() =>
            {
                try
                {
                    RebirthFlow.BeginSourceAcquire();
                }
                catch (Exception e)
                {
                    Log.Error("UI: BeginSourceAcquire failed", e);
                    WindowRequests.Notice("启动源账号登录失败：" + e.Message);
                }
            });
        }

        public static void ClearSource()
        {
            WindowRequests.Notice("已请求清除源数据");
            MainThread.Post(() =>
            {
                try
                {
                    RebirthFlow.ClearSource();
                }
                catch (Exception e)
                {
                    Log.Error("UI: ClearSource failed", e);
                }
            });
        }

        public static void RefreshSources()
        {
            WindowRequests.RequestSourcesRefresh();
            WindowRequests.Notice("正在刷新备份列表…");
        }

        // ---------------------------------------------------------------- area crawl panel

        public static void RefreshAreas()
        {
            try { AreaCatalog.MarkStale(); }
            catch (Exception) { }
            WindowRequests.RequestAreasRefresh();
            WindowRequests.Notice("正在刷新区域列表…");
        }

        public static void SetCrawlSelection(int[] ids)
        {
            int[] copy = ids != null ? (int[])ids.Clone() : new int[0];
            MainThread.Post(() =>
            {
                try
                {
                    if (!AreaBridge.WriteSelection(copy))
                    {
                        WindowRequests.Notice("保存区域选择失败");
                        return;
                    }
                    WindowRequests.Notice("已保存：区域选择（" + copy.Length + " 个）");
                }
                catch (Exception e)
                {
                    Log.Error("UI: SetCrawlSelection failed", e);
                    WindowRequests.Notice("保存区域选择失败：" + e.Message);
                }
            });
        }

        public static void SetCrawlFallback(bool value)
        {
            MainThread.Post(() =>
            {
                try
                {
                    AreaBridge.WriteFallback(value);
                    WindowRequests.Notice(value ? "已保存：爬完再来一轮 = 开" : "已保存：爬完再来一轮 = 关");
                }
                catch (Exception e)
                {
                    Log.Error("UI: SetCrawlFallback failed", e);
                }
            });
        }

        // ---------------------------------------------------------------- secret menu: catalog + unlock

        /// <summary>Re-read the game's music/collection tables into the window-safe copy.</summary>
        public static void RefreshGameCatalog()
        {
            WindowRequests.Notice("正在读取游戏数据…");
            MainThread.Post(() =>
            {
                try
                {
                    GameCatalog.Refresh();
                    string message = GameCatalog.HasData
                        ? "数据已刷新：" + GameCatalog.Songs.Length + " 首曲目"
                        : (string.IsNullOrEmpty(GameCatalog.Error) ? "游戏数据为空" : GameCatalog.Error);
                    WindowRequests.Notice(message);
                }
                catch (Exception e)
                {
                    Log.Error("UI: RefreshGameCatalog failed", e);
                    WindowRequests.Notice("读取失败：" + e.Message);
                }
            });
        }

        /// <summary>Write the given songs into the live unlock lists (game uploads them later).</summary>
        public static void UnlockSongs(int[] ids, bool master, bool remaster)
        {
            if (ids == null || ids.Length == 0)
            {
                WindowRequests.Notice("没有需要解锁的曲目");
                return;
            }
            int[] copy = (int[])ids.Clone();
            MainThread.Post(() =>
            {
                try
                {
                    int cap = ModConfig.Data.UnlockBatchRows;
                    var result = UnlockService.UnlockSongs(0, copy, true, master, remaster, cap);
                    GameCatalog.MarkStale();
                    WindowRequests.Notice("解锁歌曲：" + result);
                    Log.Info("[unlock] " + result);
                }
                catch (Exception e)
                {
                    Log.Error("UI: UnlockSongs failed", e);
                    WindowRequests.Notice("解锁失败：" + e.Message);
                }
            });
        }

        /// <summary>Add every song that is not yet in the unlock lists (batched by UnlockBatchRows).</summary>
        public static void UnlockAllSongs(bool master, bool remaster)
        {
            MainThread.Post(() =>
            {
                try
                {
                    int cap = ModConfig.Data.UnlockBatchRows;
                    var result = UnlockService.UnlockAllSongs(0, true, master, remaster, cap);
                    GameCatalog.MarkStale();
                    WindowRequests.Notice("全曲解锁：" + result);
                    Log.Info("[unlock] " + result);
                }
                catch (Exception e)
                {
                    Log.Error("UI: UnlockAllSongs failed", e);
                    WindowRequests.Notice("全曲解锁失败：" + e.Message);
                }
            });
        }

        /// <summary>Add every collection item (titles/icons/plates/frames/partners) to the live UserData.</summary>
        public static void UnlockAllCollections()
        {
            UnlockCollections(0x1F);
        }

        /// <summary>Same, but only the kinds selected by <paramref name="mask"/> (bit0 titles .. bit4 partners).</summary>
        public static void UnlockCollections(int mask)
        {
            if (mask == 0) mask = 0x1F;
            int bits = mask;
            MainThread.Post(() =>
            {
                try
                {
                    var result = UnlockService.UnlockCollections(0,
                        (bits & 0x01) != 0, (bits & 0x02) != 0, (bits & 0x04) != 0,
                        (bits & 0x08) != 0, (bits & 0x10) != 0);
                    GameCatalog.MarkStale();
                    WindowRequests.Notice("收藏品解锁：" + result);
                    Log.Info("[unlock] " + result);
                }
                catch (Exception e)
                {
                    Log.Error("UI: UnlockCollections failed", e);
                    WindowRequests.Notice("收藏品解锁失败：" + e.Message);
                }
            });
        }

        /// <summary>Ask the game to run its own DataSave chain so the written lists reach the server.</summary>
        public static void FlushUnlocks()
        {
            MainThread.Post(() =>
            {
                try
                {
                    bool ok = UnlockService.Flush("manual");
                    WindowRequests.Notice(ok
                        ? "已请求游戏存档上传（等它走到 DataSave 完成）"
                        : "无法立即上传：请在选歌页再点一次，或打一首 B·秒跳 结算后自动上传");
                }
                catch (Exception e)
                {
                    Log.Error("UI: FlushUnlocks failed", e);
                    WindowRequests.Notice("上传请求失败：" + e.Message);
                }
            });
        }

        // ---------------------------------------------------------------- editable settings

        private static void ApplySetting(string label, Action<ConfigData> apply)
        {
            MainThread.Post(() =>
            {
                try
                {
                    apply(ModConfig.Data);
                    ModConfig.Save();
                    Log.Info("UI: setting saved: " + label);
                    WindowRequests.Notice("已保存：" + label);
                }
                catch (Exception e)
                {
                    Log.Error("UI: saving setting failed: " + label, e);
                    WindowRequests.Notice("保存失败：" + label + "（" + e.Message + "）");
                }
            });
        }

        public static void SetPlayRealism(string value)
        {
            ApplySetting("游玩模式=" + (value == "Judge" ? "Judge（秒跳）" : "Duration（真实时长）"),
                d => d.PlayRealism = value);
        }

        public static void SetBatchSize(int value)
        {
            int size = value == 1 ? 1 : 3;
            ApplySetting("批大小=" + size, d => d.BatchSize = size);
        }

        public static void SetMinLevel(int value)
        {
            ApplySetting("难度下限=" + value, d => d.MinLevel = value);
        }

        public static void SetMaxLevel(int value)
        {
            ApplySetting("难度上限=" + value, d => d.MaxLevel = value);
        }

        public static void SetFillerMusicId(int value)
        {
            ApplySetting("填充曲=" + value, d => d.FillerMusicId = value);
        }

        public static void SetConfirmBeforeStart(bool value)
        {
            ApplySetting("二次确认=" + (value ? "开" : "关"), d => d.ConfirmBeforeStart = value);
        }

        public static void SetContinueRelogin(bool value)
        {
            ApplySetting("续关优先=" + (value ? "开（实验性）" : "关"), d => d.ContinueRelogin = value);
        }

        public static void SetLockInput(bool value)
        {
            ApplySetting("机台锁=" + (value ? "开" : "关"), d => d.LockInput = value);
        }

        public static void SetLongSongMode(string value)
        {
            ApplySetting("长版歌曲=" + value, d => d.LongSongMode = value);
        }
        public static void SetWriteMode(string value)
        {
            ApplySetting("写入方式=" + value, d => d.WriteMode = value);
        }

        public static void SetAreaSelectTimeoutSec(double value)
        {
            ApplySetting("选区等待=" + value.ToString("0.#") + " 秒", d => d.AreaSelectTimeoutSec = value);
        }

        public static void SetAllowConfirmNewUser(bool value)
        {
            ApplySetting("自动建号=" + (value ? "开（DEV）" : "关"), d => d.AllowConfirmNewUser = value);
        }

        public static void SetForceOverwriteExisting(bool value)
        {
            ApplySetting("强制覆盖已有成绩=" + (value ? "开" : "关"), d => d.ForceOverwriteExisting = value);
        }

        public static void SetTicketUseKeep(bool value)
        {
            ApplySetting("选票保持=" + (value ? "开（每次用上次手选的票）" : "关"), d => d.TicketUseKeep = value);
        }

        /// <summary>下次选票页不自动跳过（交给操作员手选一次）。选完导航会自动记住票 ID。</summary>
        public static void SetTicketUseNext(bool value)
        {
            ApplySetting("下次用票=" + (value ? "开（本次手动选票）" : "关"), d => d.TicketUseNext = value);
        }
        public static void SetMapResultGuard(bool value)
        {
            ApplySetting("地图结果兜底=" + (value ? "开（老账号）" : "关"), d => d.MapResultGuard = value);
        }

        /// <summary>Switching read-only ON is safe and immediate; switching it OFF must go through
        /// the existing two-step unlock dialog instead of silently enabling writes.</summary>
        public static void EnableReadOnly()
        {
            ApplySetting("只读模式=开", d => d.SafeNoWrite = true);
        }

        // ---------------------------------------------------------------- task control

        public static void StartTransfer()
        {
            WindowRequests.Notice("已请求开始转生…");
            MainThread.Post(() =>
            {
                try
                {
                    string error;
                    if (!RebirthFlow.StartTransfer(out error))
                    {
                        WindowRequests.Notice("无法开始：" + error);
                        Log.Warn("UI: StartTransfer refused: " + error);
                    }
                    else
                    {
                        WindowRequests.Notice("已开始转生");
                    }
                }
                catch (Exception e)
                {
                    Log.Error("UI: StartTransfer failed", e);
                    WindowRequests.Notice("开始失败：" + e.Message);
                }
            });
        }

        public static void ConfirmStart()
        {
            MainThread.Post(() =>
            {
                try
                {
                    if (RebirthFlow.Phase != FlowPhase.AwaitingConfirm)
                    {
                        WindowRequests.Notice("当前阶段不能确认（" + RebirthFlow.Phase + "）");
                        return;
                    }
                    RebirthFlow.ConfirmStart();
                    WindowRequests.Notice("已确认，开始转生");
                }
                catch (Exception e)
                {
                    Log.Error("UI: ConfirmStart failed", e);
                    WindowRequests.Notice("确认失败：" + e.Message);
                }
            });
        }

        public static void CancelStart()
        {
            MainThread.Post(() =>
            {
                try
                {
                    RebirthFlow.CancelStart();
                    WindowRequests.Notice("已取消，源数据仍保留");
                }
                catch (Exception e)
                {
                    Log.Error("UI: CancelStart failed", e);
                }
            });
        }

        public static void Abort()
        {
            WindowRequests.Notice("已发送中止请求…");
            MainThread.Post(() =>
            {
                try
                {
                    if (!RebirthFlow.IsBusy)
                    {
                        WindowRequests.Notice("当前没有任务在运行");
                        return;
                    }
                    RebirthFlow.Abort();
                    Log.Info("UI: abort requested at " + RebirthFlow.Phase);
                }
                catch (Exception e)
                {
                    Log.Error("UI: Abort failed", e);
                    WindowRequests.Notice("中止失败：" + e.Message);
                }
            });
        }

        // ---------------------------------------------------------------- window

        public static void SetTopMost(bool value)
        {
            MainThread.Post(() =>
            {
                try
                {
                    ModConfig.Data.WindowTopMost = value;
                    ModConfig.Save();
                    NativeModWindow.SetTopMost(value);
                    WindowRequests.Notice(value ? "窗口已置顶" : "已取消窗口置顶");
                }
                catch (Exception e)
                {
                    Log.Error("UI: SetTopMost failed", e);
                }
            });
        }

        public static void HideWindow()
        {
            NativeModWindow.SetVisible(false);
            WindowRequests.Notice("窗口已隐藏，按 F8 重新打开");
        }

        public static void ShowWindow()
        {
            NativeModWindow.SetVisible(true);
        }

        /// <summary>
        /// Second-confirmation result: write SafeNoWrite=false into config.json **without**
        /// mutating the live ConfigData (WriteGuard reads that field every write, so flipping it
        /// in memory would silently enable writes immediately instead of after a restart).
        /// </summary>
        public static void DisableSafeNoWriteOnDisk()
        {
            WindowRequests.Notice("正在写入 config.json…");
            MainThread.Post(() =>
            {
                try
                {
                    var path = Fs.ConfigPath;
                    if (!System.IO.File.Exists(path))
                    {
                        WindowRequests.Notice("config.json 不存在：" + path);
                        return;
                    }
                    string text = System.IO.File.ReadAllText(path);
                    JsonValue root;
                    string parseError;
                    if (!JsonValue.TryParse(text, out root, out parseError) || root.Kind != JsonKind.Object)
                    {
                        WindowRequests.Notice("config.json 解析失败：" + parseError);
                        return;
                    }
                    root["SafeNoWrite"] = JsonValue.Of(false);
                    System.IO.File.WriteAllText(path, root.ToJson(true));
                    Log.Warn("UI: operator disabled SafeNoWrite in config.json (takes effect after restart)");
                    WindowRequests.Notice("已写入 SafeNoWrite=false；重启游戏后生效");
                }
                catch (Exception e)
                {
                    Log.Error("UI: disabling SafeNoWrite failed", e);
                    WindowRequests.Notice("写入失败：" + e.Message);
                }
            });
        }

        // ---------------------------------------------------------------- helpers

        private static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return System.IO.Path.GetFileName(path); }
            catch (Exception) { return path; }
        }
    }
}
