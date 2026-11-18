using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FakerSwap.Core;
using FakerSwap.Data;
using HarmonyLib;
using Manager;
using Process;

namespace FakerSwap.Flow
{
    /// <summary>
    /// Login / logout for the simulated cabinet.
    ///
    /// Login  : the daemon emulates the card reader by reading <c>DEVICE/aime.txt</c> when the
    ///          "read card" key (Enter) is pressed.  We write the access code, tap Enter and put the
    ///          previous file content back.  (Same idea as Sinmai-Assist DummyAimeLogin.ReadCard.)
    ///
    /// Logout : the only server-correct way out of a session is the game's own
    ///          DataSave -> UserDataULProcess -> StateULUserLogout (UserLogout type=1) chain, which
    ///          also uploads the pending playlogs.  So "simulated timeout" here means:
    ///             * normal case  -> let the credit end (continue declined) so the game saves itself;
    ///             * while stuck in a menu -> force a DataSaveProcess transition, which is exactly
    ///               what AquaMai's OneKeyEntryEnd ("在选歌界面直接结束本局游戏") does;
    ///             * last resort  -> ProcessManager.ForceTimeUp(), the game's own timeout hook.
    ///          We never kill the process: force-killing leaves isLogin=True on the server and the
    ///          account is locked out for ~15 minutes.
    /// </summary>
    public static class LoginHooks
    {
        // ---------------------------------------------------------------- state

        public const int AccessCodeLength = 20;

        /// <summary>Last observed UserLogin response (see NetDataManager.SetLoginVO).</summary>
        public static int LoginResponseCount { get; private set; }
        public static double LastLoginResponseAt { get; private set; } = -1;
        public static ulong LastLoginId { get; private set; }
        public static long LastLoginServerTime { get; private set; }
        public static int LastLoginReturnCode { get; private set; } = -1;

        /// <summary>Set when the server answered returnCode=100 ("already logged in").  Do not retry.</summary>
        public static bool LoginBlocked { get; private set; }
        public static string LoginBlockedReason { get; private set; } = "";

        public static int SwipeCount { get; private set; }
        public static double LastSwipeAt { get; private set; } = -1;
        public static string LastSwipeCode { get; private set; } = "";
        public static int DataSaveCount { get; private set; }
        public static double LastDataSaveAt { get; private set; } = -1;
        public static double LastEntryAt { get; private set; } = -1;

        /// <summary>
        /// True once at least one chart of the current session has been started.  The logout
        /// strategy depends on it: with no playlog we only simulate the cabinet timeout, because
        /// forcing a DataSave from a screen without a credit can make the game go OnApplicationQuit.
        /// </summary>
        public static bool SessionHasPlaylog { get; private set; }

        public static void NotePlaylog()
        {
            if (!SessionHasPlaylog)
            {
                SessionHasPlaylog = true;
                Log.Info("LoginHooks: this session now has a playlog (logout may use DataSave)");
            }
        }

        private static readonly List<FileState> TouchedFiles = new List<FileState>();
        private static double _enterUpAt = -1;
        private static bool _enterDown;
        private static IntPtr _unityWindow;

        private sealed class FileState
        {
            public string Path;
            public bool Existed;
            public string Original;
            /// <summary>Byte-exact copy of the original file: the daemon reads this file, so we must
            /// restore it byte for byte (e.g. keep the original CRLF after the 20-digit code).</summary>
            public byte[] OriginalBytes;
        }

        // ---------------------------------------------------------------- install

        public static void Install(HarmonyLib.Harmony harmony)
        {
            // NetDataManager.SetLoginVO is the single reliable "a session just started" signal: it
            // carries loginId / loginDateTime / returnCode (100 == server still thinks we are in).
            PatchMethod(harmony, typeof(Manager.NetDataManager), "SetLoginVO", nameof(PostSetLoginVO));
            PatchMethod(harmony, typeof(Process.DataSaveProcess), "OnStart", nameof(PostDataSaveStart));
            PatchMethod(harmony, typeof(Process.DataSaveProcess), "OnRelease", nameof(PostDataSaveRelease));
            PatchMethod(harmony, typeof(Process.EntryProcess), "OnStart", nameof(PostEntryStart));
            PatchMethod(harmony, typeof(Process.EntryProcess), "OnRelease", nameof(PostEntryRelease));

            // Navigation must not implement its own card swipe: it forwards to us.
            try
            {
                Navigation.LoginHandler = RequestLogin;
                Navigation.LogoutHandler = RequestLogoutByTimeout;
                Log.Info("LoginHooks: Navigation.LoginHandler/LogoutHandler wired");
            }
            catch (Exception e)
            {
                Log.Warn("LoginHooks: could not wire Navigation handlers: " + e.Message);
            }

            CheckAimeFileResidue();
            EnsureAccessCodeFallback();
            Log.Info("LoginHooks installed (aime path probes: " + DescribeAimePaths() + ")");
        }

        /// <summary>
        /// `ModConfig.AimeCode` 为空时，从 AMDaemon 的 `DEVICE/aime.txt`（虚拟卡）读出 20 位卡号作为
        /// 自动刷卡码。否则 `AutoSwipe` 直接 return，登出后只能干等 AimeGen 自己再读卡 ——
        /// 实机 22:45 就是这样等到 flow 90s 超时判"重新登录后无法进入选歌页"。
        /// 只在内存里用，不写回 config。
        /// </summary>
        private static void EnsureAccessCodeFallback()
        {
            try
            {
                if (NormalizeCode(ModConfig.Data.AimeCode) != null) return;
                foreach (var path in AimePaths())
                {
                    if (!File.Exists(path)) continue;
                    string text;
                    try { text = File.ReadAllText(path); }
                    catch (IOException) { continue; }
                    string code = NormalizeCode(text);
                    if (code == null) continue;
                    ModConfig.Data.AimeCode = code;
                    Log.Info("LoginHooks: AimeCode 为空 → 从 aime.txt 取卡号作为自动刷卡码 " + MaskCode(code));
                    return;
                }
                Log.Warn("LoginHooks: AimeCode 为空且 aime.txt 不可读 → 只能依赖 AimeGen 自动读卡");
            }
            catch (Exception e)
            {
                Log.Warn("LoginHooks: 读取 aime.txt 卡号失败: " + e.Message);
            }
        }

        /// <summary>
        /// Warn when aime.txt looks like the leftover of a previous swipe (exactly 20 digit bytes and
        /// no trailing newline) - the daemon normally writes 20 digits + CRLF.
        /// </summary>
        private static void CheckAimeFileResidue()
        {
            try
            {
                foreach (var path in AimePaths())
                {
                    if (!File.Exists(path)) continue;
                    byte[] bytes = File.ReadAllBytes(path);
                    if (bytes.Length != 20) continue;
                    bool digits = true;
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        if (bytes[i] < (byte)'0' || bytes[i] > (byte)'9') { digits = false; break; }
                    }
                    if (digits)
                    {
                        Log.Warn("LoginHooks: " + path + " 是 20 字节纯数字（无换行），可能是上次刷卡残留；" +
                                 "AMDaemon 读卡异常时请先恢复该文件。");
                    }
                }
            }
            catch (Exception e) { Log.Warn("LoginHooks: aime.txt residue check failed: " + e.Message); }
        }

        private static void PatchMethod(HarmonyLib.Harmony harmony, System.Type type, string method, string postfix)
        {
            var m = AccessTools.Method(type, method);
            if (m == null)
            {
                Log.Warn("LoginHooks: " + type.Name + "." + method + " not found");
                return;
            }
            try
            {
                harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(LoginHooks), postfix)));
                Log.Info("LoginHooks: patched " + type.Name + "." + method);
            }
            catch (Exception e)
            {
                Log.Warn("LoginHooks: patch " + type.Name + "." + method + " failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- harmony postfixes

        private static void PostSetLoginVO(int index, global::Net.VO.Mai2.UserLoginResponseVO vo)
        {
            try
            {
                if (vo == null) return;
                LoginResponseCount++;
                LastLoginResponseAt = UnityEngine.Time.realtimeSinceStartup;
                LastLoginId = vo.loginId;
                LastLoginServerTime = ReadLoginTimestamp(vo);
                LastLoginReturnCode = vo.returnCode;
                if (vo.returnCode == 100)
                {
                    LoginBlocked = true;
                    LoginBlockedReason = "服务端认为该账号仍处于登录状态 (returnCode=100)";
                    Log.Error("LoginHooks: UserLogin returnCode=100 on monitor " + index +
                              " - server still has isLogin=True. 等待约15分钟或人工登出，不自动重试。");
                }
                else if (vo.returnCode == 0)
                {
                    LoginBlocked = false;
                    LoginBlockedReason = "";
                    SessionHasPlaylog = false;
                    Log.Info("LoginHooks: UserLogin ok  monitor=" + index + " loginId=" + vo.loginId +
                             " loginDateTime=" + LastLoginServerTime + " (#" + LoginResponseCount + ")");
                }
                else
                {
                    Log.Warn("LoginHooks: UserLogin returnCode=" + vo.returnCode + " monitor=" + index);
                }
            }
            catch (Exception e) { Log.Error("LoginHooks: SetLoginVO postfix failed", e); }
        }

        /// <summary>
        /// loginDateTime as read by reflection: the field name differs between builds (some 1.65
        /// variants expose lastLoginDate/loginDate instead), and a missing field must never break us.
        /// </summary>
        private static long ReadLoginTimestamp(global::Net.VO.Mai2.UserLoginResponseVO vo)
        {
            try
            {
                var t = vo.GetType();
                var f = t.GetField("loginDateTime", BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return Convert.ToInt64(f.GetValue(vo));
                f = t.GetField("loginDate", BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return Convert.ToInt64(f.GetValue(vo));
                var p = t.GetProperty("loginDateTime", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanRead) return Convert.ToInt64(p.GetValue(vo));
            }
            catch (Exception e)
            {
                Log.Warn("LoginHooks: reading login timestamp failed: " + e.Message);
            }
            return 0;
        }

        private static void PostDataSaveStart(Process.DataSaveProcess __instance)
        {
            try
            {
                DataSaveCount++;
                LastDataSaveAt = UnityEngine.Time.realtimeSinceStartup;
                Log.Info("LoginHooks: DataSave started (#" + DataSaveCount + ") - upload + logout in progress");
            }
            catch (Exception e) { Log.Error("LoginHooks: DataSave postfix failed", e); }
        }

        private static void PostDataSaveRelease()
        {
            SessionHasPlaylog = false;
            Log.Info("LoginHooks: DataSave released - server side session should be closed now");
        }

        private static void PostEntryStart()
        {
            LastEntryAt = UnityEngine.Time.realtimeSinceStartup;
            Log.Info("LoginHooks: EntryProcess started (cabinet idle / logged out)");
        }

        private static void PostEntryRelease()
        {
            if (ModConfig.Data.Verbose) Log.Info("LoginHooks: EntryProcess released");
        }

        // ---------------------------------------------------------------- login

        /// <summary>
        /// Simulated card swipe.  Idempotent-ish: repeated calls within 2.5s for the same code are
        /// ignored so callers can just spam it every frame.
        /// </summary>
        public static bool RequestLogin(string accessCode)
        {
            try
            {
                string code = NormalizeCode(accessCode);
                if (code == null) code = NormalizeCode(ModConfig.Data.AimeCode);
                if (code == null)
                {
                    Log.Warn("LoginHooks.RequestLogin: 没有有效的 20 位刷卡码 (参数和 ModConfig.AimeCode 都为空/非法)");
                    return false;
                }
                double now = UnityEngine.Time.realtimeSinceStartup;
                if (code == LastSwipeCode && now - LastSwipeAt < 2.5) return true;

                if (LoginBlocked)
                {
                    Log.Warn("LoginHooks.RequestLogin: 跳过刷卡 - " + LoginBlockedReason);
                    return false;
                }

                WriteAccessCode(code);
                TapEnter();
                LastSwipeAt = now;
                LastSwipeCode = code;
                SwipeCount++;
                Log.Info("LoginHooks: swipe #" + SwipeCount + " code=" + MaskCode(code) +
                         " (aime.txt written, Enter sent)");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("LoginHooks.RequestLogin failed", e);
                return false;
            }
        }

        public static string NormalizeCode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var sb = new StringBuilder(24);
            foreach (char c in raw)
            {
                if (c >= '0' && c <= '9') sb.Append(c);
                else if (c != ' ' && c != '\r' && c != '\n' && c != '\t') return null; // non digit
            }
            if (sb.Length == 0 || sb.Length > AccessCodeLength) return null;
            while (sb.Length < AccessCodeLength) sb.Insert(0, '0');
            return sb.ToString();
        }

        private static string MaskCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length <= 6) return code;
            return code.Substring(0, 4) + "****" + code.Substring(code.Length - 4);
        }

        /// <summary>Candidate aime.txt files, most likely first (existing files before existing dirs).</summary>
        private static List<string> AimePaths()
        {
            var list = new List<string>(6);
            string cwd = Environment.CurrentDirectory;
            var raw = new List<string>(6);
            raw.Add(Path.Combine(cwd, "DEVICE", "aime.txt"));
            raw.Add(Path.GetFullPath(Path.Combine(cwd, "..", "AMDaemon", "DEVICE", "aime.txt")));
            raw.Add(Path.GetFullPath(Path.Combine(cwd, "..", "daemon", "DEVICE", "aime.txt")));
            raw.Add(Path.GetFullPath(Path.Combine(cwd, "..", "..", "AMDaemon", "DEVICE", "aime.txt")));
            raw.Add(Path.Combine(cwd, "AMDaemon", "DEVICE", "aime.txt"));

            // 1) files that already exist (this is the one the daemon really reads)
            foreach (var p in raw) if (File.Exists(p)) Add(list, p);
            // 2) paths whose directory exists
            foreach (var p in raw)
            {
                if (list.Contains(p)) continue;
                try { if (Directory.Exists(Path.GetDirectoryName(p))) Add(list, p); }
                catch (Exception) { }
            }
            // 3) nothing at all: keep the classic Sinmai location last as a creation target
            if (list.Count == 0) Add(list, raw[0]);
            return list;
        }

        private static void Add(List<string> list, string p)
        {
            try
            {
                if (!list.Contains(p)) list.Add(p);
            }
            catch (Exception) { }
        }

        private static string DescribeAimePaths()
        {
            var sb = new StringBuilder();
            foreach (var p in AimePaths())
            {
                sb.Append(File.Exists(p) ? "[exists] " : "[      ] ").Append(p).Append("; ");
            }
            return sb.ToString();
        }

        private static void WriteAccessCode(string code)
        {
            bool wrote = false;
            foreach (var path in AimePaths())
            {
                string dir;
                try { dir = Path.GetDirectoryName(path); }
                catch (Exception) { continue; }
                if (string.IsNullOrEmpty(dir)) continue;
                if (!Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); }
                    catch (Exception e)
                    {
                        Log.Warn("LoginHooks: cannot create " + dir + ": " + e.Message);
                        continue;
                    }
                }

                var state = FindState(path);
                if (state == null)
                {
                    state = new FileState { Path = path, Existed = File.Exists(path) };
                    if (state.Existed)
                    {
                        try { state.Original = File.ReadAllText(path); }
                        catch (Exception) { state.Original = null; }
                        try { state.OriginalBytes = File.ReadAllBytes(path); }
                        catch (Exception) { state.OriginalBytes = null; }
                    }
                    TouchedFiles.Add(state);
                }
                try
                {
                    // Byte-faithful write: keep the original line ending so a 22-byte "code+CRLF"
                    // file stays 22 bytes instead of being normalised to 20 bytes.
                    string ending = OriginalLineEnding(state);
                    File.WriteAllBytes(path, Encoding.ASCII.GetBytes(code + ending));
                    wrote = true;
                    Log.Info("LoginHooks: aime.txt written -> " + path +
                             (state.Existed ? " (will restore byte-exact)" : " (new file)") +
                             (ending.Length > 0 ? " [kept " + (ending.Length == 2 ? "CRLF" : "LF") + "]" : ""));
                }
                catch (Exception e)
                {
                    Log.Warn("LoginHooks: cannot write " + path + ": " + e.Message);
                }
            }

            if (!wrote)
            {
                Log.Error("LoginHooks: no writable aime.txt location found; candidates: " + DescribeAimePaths());
            }
        }

        private static FileState FindState(string path)
        {
            foreach (var s in TouchedFiles) if (s.Path == path) return s;
            return null;
        }

        /// <summary>Line ending of the original file ("" when it had none).</summary>
        private static string OriginalLineEnding(FileState state)
        {
            try
            {
                if (state.OriginalBytes != null && state.OriginalBytes.Length > 0)
                {
                    int n = state.OriginalBytes.Length;
                    if (n >= 2 && state.OriginalBytes[n - 2] == 0x0D && state.OriginalBytes[n - 1] == 0x0A) return "\r\n";
                    if (state.OriginalBytes[n - 1] == 0x0A) return "\n";
                    return "";
                }
                if (state.Original != null)
                {
                    if (state.Original.EndsWith("\r\n", StringComparison.Ordinal)) return "\r\n";
                    if (state.Original.EndsWith("\n", StringComparison.Ordinal)) return "\n";
                }
            }
            catch (Exception) { }
            return "";
        }

        /// <summary>Put the old access code back so the cabinet behaves as before our swipe.</summary>
        public static void RestoreAccessCodeFiles()
        {
            foreach (var s in TouchedFiles)
            {
                try
                {
                    if (s.Existed && s.OriginalBytes != null)
                    {
                        File.WriteAllBytes(s.Path, s.OriginalBytes); // byte-exact restore
                        Log.Info("LoginHooks: aime.txt restored byte-exact -> " + s.Path + " (" + s.OriginalBytes.Length + " bytes)");
                    }
                    else if (s.Existed && s.Original != null)
                    {
                        File.WriteAllText(s.Path, s.Original);
                        Log.Info("LoginHooks: aime.txt restored (text) -> " + s.Path);
                    }
                    // If the file did not exist before we leave our code in place: that is the code
                    // the operator wants the machine to keep using, and deleting it could make the
                    // daemon fail to read any card at all.
                }
                catch (Exception e) { Log.Warn("LoginHooks: restore " + s.Path + " failed: " + e.Message); }
            }
            TouchedFiles.Clear();
        }

        private static void TapEnter()
        {
            FocusUnityWindow();
            SendEnterDown();
            _enterDown = true;
            _enterUpAt = UnityEngine.Time.realtimeSinceStartup + 0.30;
        }

        /// <summary>Called every frame by the flow: pumps the deferred key-up and second confirm tap.</summary>
        public static void Tick()
        {
            try
            {
                if (_enterDown && UnityEngine.Time.realtimeSinceStartup >= _enterUpAt)
                {
                    SendEnterUp();
                    _enterDown = false;
                }
            }
            catch (Exception e) { Log.Error("LoginHooks.Tick failed", e); }
        }

        // ---------------------------------------------------------------- login detection

        public static bool IsLoggedIn(out int monitor)
        {
            ulong uid;
            string name;
            return IsLoggedIn(out monitor, out uid, out name);
        }

        /// <summary>True when at least one monitor has a real (non guest/ghost/npc) account loaded.</summary>
        public static bool IsLoggedIn(out int monitor, out ulong userId, out string userName)
        {
            monitor = -1;
            userId = 0;
            userName = "";
            try
            {
                var mgr = Manager.UserDataManager.Instance;
                if (mgr == null) return false;
                for (int i = 0; i < 2; i++)
                {
                    var ud = mgr.GetUserData(i);
                    if (ud == null || !ud.IsEntry) continue;
                    if (ud.IsGuest()) continue;

                    var type = ud.UserType;
                    if (type == UserData.UserIDType.Guest || type == UserData.UserIDType.Ghost
                        || type == UserData.UserIDType.Npc || type == UserData.UserIDType.Boss)
                        continue;

                    var detail = ud.Detail;
                    ulong id = detail != null ? detail.UserID : 0UL;
                    string name = detail != null ? detail.UserName : "";
                    if (id == 0UL && string.IsNullOrEmpty(name)) continue;

                    monitor = i;
                    userId = id;
                    userName = name ?? "";
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.Error("LoginHooks.IsLoggedIn failed", e);
            }
            return false;
        }

        public static bool IsLoggedIn() { int m; return IsLoggedIn(out m); }

        // ---------------------------------------------------------------- logout

        private static bool _logoutActive;
        private static double _logoutStartedAt = -1;
        private static double _lastForceAt = -100;
        private static double _lastPreWriteBackupAt = -100;
        private static bool _forceSaveIssued;
        private static bool _timeUpIssued;
        private static bool _noPlaylogTimeUpIssued;
        private static double _noPlaylogTimeUpAt = -1;
        /// <summary>Start of the passive wait for the game's own post-credit teardown.</summary>
        private static double _passiveWaitAt = -1;
        private static double _postCreditWaitAt = -1;
        private static bool _logoutGaveUp;
        public static string LogoutReason { get; private set; } = "";
        public static bool LogoutGaveUp { get { return _logoutGaveUp; } }

        public static double LogoutElapsed
        {
            get { return _logoutActive ? UnityEngine.Time.realtimeSinceStartup - _logoutStartedAt : 0; }
        }

        public static bool IsLogoutActive { get { return _logoutActive; } }

        /// <summary>Start the "end this session" sequence.  Safe to call repeatedly.</summary>
        public static void BeginLogout(string reason)
        {
            if (_logoutActive)
            {
                if (reason != null && reason != LogoutReason) Log.Info("LoginHooks: logout reason -> " + reason);
                return;
            }
            _logoutActive = true;
            _logoutStartedAt = UnityEngine.Time.realtimeSinceStartup;
            _lastForceAt = -100;
            _forceSaveIssued = false;
            _timeUpIssued = false;
            _noPlaylogTimeUpIssued = false;
            _noPlaylogTimeUpAt = -1;
            _passiveWaitAt = -1;
            _postCreditWaitAt = -1;
            _timerZeroIssued = false;
            _timerZeroAt = -1;
            _postDirectTimerZeroDone = false;
            _logoutGaveUp = false;
            LogoutReason = reason ?? "";
            Log.Info("LoginHooks: logout begin (" + LogoutReason + ")  stage=" + GameStagePump.Stage);
        }

        public static void EndLogout()
        {
            if (!_logoutActive) { _logoutStartedAt = -1; return; }
            double elapsed = UnityEngine.Time.realtimeSinceStartup - _logoutStartedAt;
            _logoutActive = false;
            _logoutStartedAt = -1;
            Log.Info("LoginHooks: logout done after " + elapsed.ToString("0.0") + "s");
        }

        /// <summary>
        /// Single-shot "simulated timeout logout" used by Navigation.LogoutHandler and by the flow's
        /// cleanup paths.  Returns true when the session is already gone or the request was issued;
        /// the actual progress is observable through <see cref="IsLogoutActive"/> / <see cref="IsLoggedIn()"/>.
        /// </summary>
        public static bool RequestLogoutByTimeout()
        {
            if (!IsLoggedIn())
            {
                if (_logoutActive) EndLogout();
                return true;
            }
            BeginLogout("request");
            string detail;
            return PollLogout(out detail);
        }

        /// <summary>
        /// Drives the logout.  Returns true once the session is gone (or we gave up).
        /// <paramref name="detail"/> is a short human readable state for the UI/log.
        /// </summary>
        public static bool PollLogout(out string detail)
        {
            detail = "";
            bool active = _logoutActive;
            try
            {
                double now = UnityEngine.Time.realtimeSinceStartup;
                if (!active)
                {
                    BeginLogout("implicit");
                    active = true;
                }
                double elapsed = now - _logoutStartedAt;
                GameStage stage = GameStageTracker.Current;
                int mon;
                bool loggedIn = IsLoggedIn(out mon);

                if (!loggedIn)
                {
                    detail = "已登出 (stage=" + stage + ")";
                    _logoutActive = false;
                    SessionHasPlaylog = false;
                    return true;
                }

                // Direct UserLogoutApi path: the local game may still show a logged-in screen after
                // the server session is closed, so IsLoggedIn() is not a usable success signal here.
                if (DirectLogoutDone)
                {
                    detail = "直接登出报文完成（服务端会话已关闭）";
                    _logoutActive = false;
                    SessionHasPlaylog = false;
                    _directLogoutPending = false;
                    // Best effort: also push the LOCAL client back to Title (timer -> 0 -> time up),
                    // so we do not leave the cabinet sitting on MODE SELECT pretending to be logged in.
                    if (!_postDirectTimerZeroDone && GameStageTracker.Current == GameStage.ModeSelect)
                    {
                        _postDirectTimerZeroDone = true;
                        ForceTimerZero("post-direct");
                    }
                    Log.Info("[logout] 服务端会话已关闭；已尝试把本机倒计时归零回 Title");
                    return true;
                }
                if (_directLogoutPending)
                {
                    double waited = now - _directLogoutAt;
                    if (waited < 10.0)
                    {
                        detail = "等待 UserLogout 报文回应… (" + waited.ToString("0.0") + "s)";
                        return false;
                    }
                    Log.Warn("[logout] direct UserLogout got no callback within 10s - falling back");
                    _directLogoutPending = false;
                }

                // ---- still logged in: pick the least invasive way to end the session ----
                switch (stage)
                {
                    case GameStage.DataSave:
                        detail = "数据上传/登出中 (DataSave)";
                        break;

                    case GameStage.GamePlay:
                    case GameStage.TrackStart:
                        detail = "游戏中，需先结束当前曲才能登出";
                        // caller (flow) is responsible for skipping the chart; do not fight it here
                        break;

                    case GameStage.PhotoEdit:
                        detail = "跳过拍照界面 -> DataSave";
                        ForceDataSave("photo-edit");
                        break;

                    case GameStage.Entry:
                    case GameStage.Title:
                    case GameStage.Startup:
                        // account data is still loaded but the machine is already back at the reader
                        // screen; give the game a moment, the game will clear it.
                        detail = "已回到登录界面，等待账号数据清空";
                        break;

                    case GameStage.MusicSelect:
                    default:
                        detail = ChooseLogoutAction(now);
                        break;
                }

                // ---- watchdog ----
                if (elapsed >= ModConfig.Data.LogoutTimeoutSec && !_timeUpIssued)
                {
                    _timeUpIssued = true;
                    Log.Warn("LoginHooks: logout still not done after " + elapsed.ToString("0.0") +
                             "s - falling back to ProcessManager.ForceTimeUp()");
                    ForceTimeUp();
                    detail += " | 超时兜底 ForceTimeUp";
                }
                else if (elapsed >= 3.0 * ModConfig.Data.LogoutTimeoutSec)
                {
                    _logoutGaveUp = true;
                    _logoutActive = false;
                    Log.Error("LoginHooks: logout gave up after " + elapsed.ToString("0.0") +
                              "s (stage=" + stage + ") - 不会杀进程；请人工检查");
                    detail = "登出超时（未杀进程，请人工检查）";
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                Log.Error("LoginHooks.PollLogout failed", e);
                detail = "登出异常: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// Which logout primitive to use.  Decision is driven by "does this session have a playlog",
        /// NOT by the stage: with no playlog the cabinet timeout (ForceTimeUp) is the only safe
        /// simulation - forcing a DataSave out of a screen without a credit can make the game run
        /// into OnApplicationQuit (observed on the real machine).
        /// </summary>
        private static string ChooseLogoutAction(double now)
        {
            GameStage stage = GameStageTracker.Current;
            bool noSaveScreen = stage == GameStage.Entry || stage == GameStage.Title
                                || stage == GameStage.Startup || stage == GameStage.Unknown;

            // Read-only mode: only the cabinet timeout is allowed, regardless of playlog.
            if (!WriteGuard.WritesAllowed)
                return NoUploadLogout(now, stage, "只读模式");

            if (!SessionHasPlaylog)
                return NoUploadLogout(now, stage, "无 playlog");

            // There is at least one playlog in this session: a DataSave is the correct upload+logout.
            if (noSaveScreen) return "已到 " + stage + "，等待账号数据清空…";
            if (!ForceDataSave("logout")) return "有 playlog 但无可用进程，无法强制 DataSave";
            return "强制 DataSave (上传+type=1 登出)  stage=" + stage;
        }

        /// <summary>
        /// Logout that uploads nothing (read-only / no playlog).
        ///
        /// MODE SELECT gets a passive wait first: the game's own idle timeout logs out after ~30s
        /// (verified by the user), while injecting input there races the navigation timeskip/OK
        /// automation into STARTING a credit.  Everywhere else (reception/Entry) ForceTimeUp is the
        /// documented "simulated timeout" and is issued immediately.
        /// </summary>
        private static string NoUploadLogout(double now, GameStage stage, string tag)
        {
            if (DirectLogoutDone) return tag + ": 直接登出已完成";

            // After a credit's DataSave the game still walks Result -> MapResult -> DataSave ->
            // Continue -> GameOver -> Title and logs itself out.  Do NOT inject anything while one
            // of those screens is up: adding another process on top is what made the stacks pile up
            // and crash GameProcess.OnUpdate.
            if (stage == GameStage.Result || stage == GameStage.MapResult
                || stage == GameStage.Continue || stage == GameStage.PhotoEdit)
            {
                if (_postCreditWaitAt < 0)
                {
                    _postCreditWaitAt = now;
                    Log.Info("LoginHooks: 等待游戏自然收尾 (" + stage + ") 进入 DataSave/登出，不注入进程…");
                }
                if (now - _postCreditWaitAt < 40.0)
                    return tag + ": 等待游戏自然收尾… stage=" + stage;
            }

            if (stage == GameStage.ModeSelect)
            {
                // PREFERRED: make the countdown really reach zero -> the game shows its own timeout,
                // returns to Title and performs its own UserLogout.  This is the correct semantics
                // (server + client both end up logged out).
                if (!_timerZeroIssued)
                {
                    bool ok = ForceTimerZero("modeselect-logout");
                    return tag + (ok ? ": 倒计时归零，等待游戏自身回 Title…" : ": 倒计时归零失败，准备直连登出");
                }
                if (!DirectLogoutAttempted && now - _timerZeroAt >= 8.0)
                {
                    if (TryDirectLogout(tag))
                        return tag + ": 倒计时 8s 未回 Title，改发 UserLogout 报文（保底）";
                }
                if (now - _timerZeroAt < 35.0)
                    return tag + ": 等待倒计时归零回 Title… stage=" + stage;
            }
            else
            {
                // Reception/Entry screen: ForceTimeUp is the documented "simulated timeout" (nav's
                // timeskip automation makes it fast).  Direct packet is the fallback.
                if (!_noPlaylogTimeUpIssued)
                {
                    _noPlaylogTimeUpIssued = true;
                    _noPlaylogTimeUpAt = now;
                    bool issued = ForceTimeUp();
                    return issued ? tag + ": 模拟超时 (ForceTimeUp)" : tag + ": ForceTimeUp 不可用";
                }
                if (!DirectLogoutAttempted && now - _noPlaylogTimeUpAt >= 8.0)
                {
                    if (TryDirectLogout(tag))
                        return tag + ": 8s 未登出，改发 UserLogout 报文（保底）";
                }
                if (now - _noPlaylogTimeUpAt < 35.0)
                    return tag + ": 等待超时登出… stage=" + stage;
            }

            // Final fallback chain: ForceTimeUp -> direct packet.
            if (!_noPlaylogTimeUpIssued)
            {
                _noPlaylogTimeUpIssued = true;
                _noPlaylogTimeUpAt = now;
                bool ok = ForceTimeUp();
                return ok ? tag + ": 模拟超时 (ForceTimeUp)" : tag + ": ForceTimeUp 不可用（等待/放弃）";
            }
            if (!DirectLogoutAttempted && !DirectLogoutDone)
            {
                if (TryDirectLogout(tag)) return tag + ": 已直接发送 UserLogout 报文（保底）";
            }
            return tag + ": 等待中（不会上传） stage=" + stage;
        }

        /// <summary>
        /// HARD SAFETY: forced DataSave uploads the whole live UserData (server overwrites the whole
        /// account).  Only allow it when the live data is provably a real, non-empty account.
        /// Empty-data upload is what wiped the test account, so this stays false.
        /// </summary>
        public static bool AllowEmptyUserDataSave = false;

        /// <summary>
        /// "Is it safe to run the save+logout chain for the currently logged in account?"
        /// Requires a real (non guest) account with a UserID, and a non-empty score list unless
        /// <see cref="AllowEmptyUserDataSave"/> was explicitly enabled.
        /// </summary>
        public static bool HasSafeUserData(out int monitor, out string why)
        {
            monitor = -1;
            why = "";
            ulong uid;
            string name;
            if (!IsLoggedIn(out monitor, out uid, out name) || uid == 0UL)
            {
                why = "not logged in / uid=0";
                return false;
            }
            var ud = GetLiveUserData(monitor);
            if (ud == null) { why = "UserData==null"; return false; }
            try
            {
                if (ud.IsGuest()) { why = "guest account"; return false; }
                var detail = ud.Detail;
                if (detail == null || detail.UserID == 0UL) { why = "Detail.UserID=0"; return false; }
                int scores = 0;
                try
                {
                    var list = UserDataPatcher.ReadScores(monitor);
                    if (list != null) scores = list.Count;
                }
                catch (Exception e)
                {
                    why = "reading scores failed: " + e.Message;
                    return false;
                }
                if (scores == 0 && !AllowEmptyUserDataSave)
                {
                    why = "score list is EMPTY (0 rows) - refusing to upload (would overwrite the account with defaults)";
                    return false;
                }
                why = "ok (uid=" + detail.UserID + ", rating=" + detail.Rating + ", scores=" + scores + ")";
                return true;
            }
            catch (Exception e)
            {
                why = "UserData check failed: " + e.Message;
                return false;
            }
        }

        private static UserData GetLiveUserData(int monitor)
        {
            try
            {
                var mgr = Manager.UserDataManager.Instance;
                if (mgr == null) return null;
                return mgr.GetUserData(monitor);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Only these screens may be finished by injecting a DataSaveProcess.  Anything else
        /// (Title / Entry / ModeSelect / Other / wrapper processes) is refused.
        /// </summary>
        private static bool IsSafeStageToFinish(out string reason)
        {
            reason = "";
            try
            {
                if (Navigation.InGameplay) { reason = "nav.InGameplay"; return true; }
                if (Navigation.AtContinue) { reason = "nav.AtContinue"; return true; }
                if (Navigation.AtMusicSelect) { reason = "nav.AtMusicSelect"; return true; }
            }
            catch (Exception) { }

            GameStage st = GameStageTracker.Current;
            switch (st)
            {
                case GameStage.MusicSelect:
                case GameStage.GamePlay:
                case GameStage.TrackStart:
                case GameStage.Result:
                case GameStage.Continue:
                case GameStage.PhotoEdit:
                case GameStage.MapResult:
                case GameStage.DataSave:
                    reason = st.ToString();
                    return true;
            }

            try
            {
                var list = ProcessHooks.GetProcessList();
                if (list != null)
                {
                    for (var node = list.Last; node != null; node = node.Previous)
                    {
                        var ctrl = node.Value;
                        if (ctrl == null || ctrl.Process == null) continue;
                        if (ctrl.State.ToString() == "Released") continue;
                        string n = ctrl.Process.GetType().FullName ?? "";
                        if (IsSafeFinishProcessName(n)) { reason = n; return true; }
                    }
                }
            }
            catch (Exception) { }

            reason = "stage=" + st + " raw=" + GameStageTracker.RawProcess;
            return false;
        }

        private static bool IsSafeFinishProcessName(string fullName)
        {
            switch (fullName)
            {
                case "Process.MusicSelectProcess":
                case "Process.GameProcess":
                case "Process.TrackStartProcess":
                case "Process.ResultProcess":
                case "Process.ContinueProcess":
                case "Process.PhotoEditProcess":
                case "Process.MapResultProcess":
                case "Process.DataSaveProcess":
                case "Process.UserDataULProcess":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Force the game to run its save+logout chain by fading to a DataSaveProcess.</summary>
        public static bool ForceDataSave(string why)
        {
            if (!FakerSwap.Core.WriteGuard.Allow("LoginHooks.ForceDataSave(" + why + ")")) return false;

            try
            {
                // ---- HARD GATE 1: real, non-empty account loaded --------------------------------
                int mon;
                string safety;
                if (!HasSafeUserData(out mon, out safety))
                {
                    Log.Warn("LoginHooks: force DataSave rejected: unsafe user data (" + safety + ") - " + why);
                    return false;
                }

                // ---- HARD GATE 2: only finish from a screen where a credit exists ---------------
                string stageWhy;
                if (!IsSafeStageToFinish(out stageWhy))
                {
                    Log.Warn("LoginHooks: force DataSave rejected: unsafe process (" + stageWhy + ") - " + why);
                    return false;
                }

                // ---- HARD GATE 3: never add a DataSave on top of the post-credit screens -------
                // The game walks Result -> MapResult -> DataSave by itself; injecting a process here
                // piles up the stack ("forced DataSave from Process.MapResultProcess").
                GameStage nowStage = GameStageTracker.Current;
                if (nowStage == GameStage.Result || nowStage == GameStage.MapResult
                    || nowStage == GameStage.Continue || nowStage == GameStage.PhotoEdit)
                {
                    Log.Warn("LoginHooks: force DataSave rejected: stage=" + nowStage +
                             " - 等待游戏自然进入 DataSave（禁止叠加进程）(" + why + ")");
                    return false;
                }

                var container = ProcessHooks.Container;
                var pm = ProcessHooks.Manager;
                var cur = ProcessHooks.CurrentProcess;
                if (cur != null && !IsUsableProcess(cur)) cur = null;
                if (cur == null) cur = ProcessHooks.NewestLiveProcess();
                if (container == null || pm == null)
                {
                    Log.Warn("LoginHooks.ForceDataSave(" + why + "): container/manager not ready");
                    return false;
                }
                if (GameStageTracker.Current == GameStage.DataSave) return true;

                double now = UnityEngine.Time.realtimeSinceStartup;
                if (_forceSaveIssued && now - _lastForceAt < 5) return true;
                _forceSaveIssued = true;
                _lastForceAt = now;

                if (cur == null || !IsUsableProcess(cur))
                {
                    // Never fade from GenericProcess/FadeProcess/CommonProcess: on the real machine
                    // that pushes the game into OnApplicationQuit instead of the save screen.
                    Log.Warn("LoginHooks.ForceDataSave(" + why + "): no usable current process (stage=" +
                             GameStageTracker.Current + ", raw=" + GameStageTracker.RawProcess + ") - skipped");
                    return false;
                }

                // Mandatory pre-write backup, but AT MOST ONCE PER CREDIT WINDOW: the old placement
                // (before the 5s de-dupe) wrote ~74 files in one second and would fill the disk.
                // A failed backup means "do not touch the account".
                if (now - _lastPreWriteBackupAt >= 60.0)
                {
                    string backupError;
                    if (!FakerSwap.Data.UserDataPatcher.ForcePreWriteBackup(out backupError))
                    {
                        Log.Warn("LoginHooks.ForceDataSave(" + why + "): 写前备份失败 (" + backupError + ") -> 拒绝 DataSave");
                        return false;
                    }
                    _lastPreWriteBackupAt = now;
                    CleanupPreWriteBackups(10);
                }

                if (cur is Process.PhotoEditProcess)
                {
                    // Same as AquaMai ExitToSave: jump straight from the photo screen to the upload.
                    pm.AddProcess(new Process.DataSaveProcess(container));
                    pm.ReleaseProcess(cur);
                }
                else
                {
                    pm.AddProcess(new FadeProcess(container, cur, new Process.DataSaveProcess(container)));
                    // AquaMai needs this after a mid-menu add to keep the timer/process wiring sane.
                    pm.PrepareTimer(0, 0, false, null, false);
                }
                Log.Info("LoginHooks: forced DataSave (" + why + ") from " + (cur.GetType().FullName ?? "?"));
                return true;
            }
            catch (Exception e)
            {
                Log.Error("LoginHooks.ForceDataSave failed", e);
                return false;
            }
        }

        /// <summary>The game's own timeout path: used as the last resort instead of killing anything.</summary>
        public static bool ForceTimeUp()        {
            try
            {
                var pm = ProcessHooks.Manager;
                if (pm == null) return false;
                pm.ForceTimeUp();
                Log.Info("LoginHooks: ProcessManager.ForceTimeUp() issued");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("LoginHooks.ForceTimeUp failed", e);
                return false;
            }
        }

        /// <summary>
        /// 给外部注入点（目前只有 `CrashFixes` 的 MapResultGuard 旁路）复用的一整套硬闸门：
        /// WriteGuard + 真实非空账号 + 写前强制备份。任何一条不过就返回 false —— 旁路也必须
        /// 和 ForceDataSave 一样安全，否则就是 incident-2026-11-14 的翻版。
        /// </summary>
        public static bool AllowInjectedDataSave(string why)
        {
            if (!FakerSwap.Core.WriteGuard.Allow("LoginHooks.AllowInjectedDataSave(" + why + ")"))
            {
                Log.Warn("LoginHooks: injected DataSave 被 WriteGuard 拒绝 (" + why + ")");
                return false;
            }
            int mon;
            string safety;
            if (!HasSafeUserData(out mon, out safety))
            {
                Log.Warn("LoginHooks: injected DataSave 被拒绝：用户数据不安全 (" + safety + ") - " + why);
                return false;
            }
            string backupError;
            if (!FakerSwap.Data.UserDataPatcher.ForcePreWriteBackup(out backupError))
            {
                Log.Warn("LoginHooks: injected DataSave 被拒绝：写前备份失败 (" + backupError + ") - " + why);
                return false;
            }
            return true;
        }

        /// <summary>Force DataSave again if the previous request silently failed (called by the flow).</summary>
        public static void RetryForceDataSave()        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (!_forceSaveIssued || now - _lastForceAt > 5)
            {
                _forceSaveIssued = false;
                ForceDataSave("retry");
            }
        }

        // ---------------------------------------------------------------- process helpers

        /// <summary>Keep only the newest <paramref name="keep"/> pre-write-*.json files.</summary>
        private static void CleanupPreWriteBackups(int keep)
        {
            try
            {
                string dir = Fs.BackupDir;
                if (!Directory.Exists(dir)) return;
                var files = new List<string>(Directory.GetFiles(dir, "pre-write-*.json"));
                if (files.Count <= keep) return;
                files.Sort(delegate (string a, string b)
                {
                    return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a));
                });
                int removed = 0;
                for (int i = keep; i < files.Count; i++)
                {
                    try { File.Delete(files[i]); removed++; }
                    catch (Exception) { }
                }
                if (removed > 0)
                    Log.Info("LoginHooks: cleaned " + removed + " old pre-write backup(s), kept " + keep);
            }
            catch (Exception e) { Log.Warn("LoginHooks: pre-write backup cleanup failed: " + e.Message); }
        }

        /// <summary>A process we can safely hand to FadeProcess as the "from" argument.</summary>
        private static bool IsUsableProcess(ProcessBase process)
        {
            if (process == null) return false;
            if (!IsProcessAlive(process)) return false;
            string name = process.GetType().FullName ?? "";
            return !GameStagePump.IsTransitional(name);
        }

        private static bool IsProcessAlive(ProcessBase process)
        {
            if (process == null) return false;
            var list = ProcessHooks.GetProcessList();
            if (list == null) return true; // cannot verify (patched list unavailable) - assume alive
            for (var node = list.Last; node != null; node = node.Previous)
            {
                var ctrl = node.Value;
                if (ctrl == null || !ReferenceEquals(ctrl.Process, process)) continue;
                return ctrl.State != Manager.ProcessManager.ProcessState.Released;
            }
            return false;
        }

        // ---------------------------------------------------------------- timer force-zero (preferred logout)

        private static bool _timerZeroIssued;
        private static double _timerZeroAt = -1;
        private static bool _postDirectTimerZeroDone;

        /// <summary>
        /// Drive the cabinet countdown to zero so the GAME performs its own "time up" -> Title ->
        /// UserLogout (correct client + server semantics).  Primary logout path on MODE SELECT where
        /// the countdown does not run by itself (AquaMai DisableTimeout etc.).
        /// </summary>
        public static bool ForceTimerZero(string why)
        {
            try
            {
                var pm = ProcessHooks.Manager;
                if (pm == null)
                {
                    Log.Warn("[logout] timer force-zero: processManager missing (" + why + ")");
                    _timerZeroIssued = true;
                    _timerZeroAt = UnityEngine.Time.realtimeSinceStartup;
                    return false;
                }
                bool before0 = false, before1 = false, after0 = false, after1 = false;
                int tcBefore = -1, tcAfter = -1;
                try { before0 = pm.IsTimeUp(0); before1 = pm.IsTimeUp(1); } catch (Exception) { }
                try { pm.IsTimeCounting(true); } catch (Exception) { }
                try { pm.SetVisibleTimers(true); } catch (Exception) { }

                try
                {
                    pm.DecrementTime(0, 9999);
                    pm.DecrementTime(1, 9999);
                }
                catch (Exception e)
                {
                    Log.Warn("[logout] timer force-zero: DecrementTime failed: " + e.Message);
                }

                // Direct field write as well: some builds keep the countdown in TimerController and
                // only poll it inside UpdateTimer.
                tcBefore = ZeroTimerControllers(pm, out tcAfter);
                try { after0 = pm.IsTimeUp(0); after1 = pm.IsTimeUp(1); } catch (Exception) { }

                bool ok = after0 || after1 || (tcBefore >= 0 && tcAfter == 0) || tcBefore >= 0;
                Log.Info("[logout] timer force-zero (" + why + "): beforeTimeUp=(" + before0 + "," + before1 +
                         ") afterTimeUp=(" + after0 + "," + after1 + ") timerCountdown=" + tcBefore + "->" + tcAfter +
                         " -> " + (ok ? "issued" : "no timer found"));
                _timerZeroIssued = true;
                _timerZeroAt = UnityEngine.Time.realtimeSinceStartup;
                return ok;
            }
            catch (Exception e)
            {
                Log.Warn("[logout] timer force-zero failed: " + e.Message);
                _timerZeroIssued = true;
                _timerZeroAt = UnityEngine.Time.realtimeSinceStartup;
                return false;
            }
        }

        /// <summary>Find TimerController fields on ProcessManager (and one level below) and zero their countdown.</summary>
        private static int ZeroTimerControllers(object owner, out int after)
        {
            after = -1;
            int before = -1;
            try
            {
                if (owner == null) return -1;
                System.Type tcType = typeof(TimerController);
                foreach (var f in owner.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    object v;
                    try { v = f.GetValue(owner); } catch (Exception) { continue; }
                    if (v == null) continue;

                    if (f.FieldType == tcType)
                    {
                        try
                        {
                            var counter = v.GetType().GetProperty("CountDownSecond", BindingFlags.Public | BindingFlags.Instance);
                            if (counter != null && counter.CanRead)
                            {
                                object b = counter.GetValue(v, null);
                                if (b != null && before < 0) before = Convert.ToInt32(b);
                            }
                            var field = v.GetType().GetField("_countDownSecond", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (field != null)
                            {
                                field.SetValue(v, 0);
                                after = 0;
                            }
                            try
                            {
                                var counting = v.GetType().GetProperty("IsTimeCounting", BindingFlags.Public | BindingFlags.Instance);
                                if (counting != null && counting.CanWrite) counting.SetValue(v, true, null);
                            }
                            catch (Exception) { }
                            try
                            {
                                var fast = v.GetType().GetProperty("IsFastSkip", BindingFlags.Public | BindingFlags.Instance);
                                if (fast != null && fast.CanWrite) fast.SetValue(v, true, null);
                            }
                            catch (Exception) { }
                        }
                        catch (Exception e) { Log.Warn("[logout] timer force-zero: zeroing TimerController failed: " + e.Message); }
                    }
                    else if (v.GetType().Namespace != null && v.GetType().Namespace.StartsWith("Manager", StringComparison.Ordinal))
                    {
                        // one level down (e.g. ProcessManager._genericManager / OperationManager)
                        int dummy = -1;
                        int deeper = ZeroTimerControllersOnce(v, ref before, ref dummy);
                        if (deeper > 0) after = 0;
                    }
                }
            }
            catch (Exception) { }
            return before;
        }

        private static int ZeroTimerControllersOnce(object owner, ref int before, ref int after)
        {
            int found = 0;
            try
            {
                System.Type tcType = typeof(TimerController);
                foreach (var f in owner.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    object v;
                    try { v = f.GetValue(owner); } catch (Exception) { continue; }
                    if (v == null || f.FieldType != tcType) continue;
                    found++;
                    try
                    {
                        var counter = v.GetType().GetProperty("CountDownSecond", BindingFlags.Public | BindingFlags.Instance);
                        if (counter != null && counter.CanRead && before < 0)
                        {
                            object b = counter.GetValue(v, null);
                            if (b != null) before = Convert.ToInt32(b);
                        }
                        var field = v.GetType().GetField("_countDownSecond", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (field != null) { field.SetValue(v, 0); after = 0; }
                        var counting = v.GetType().GetProperty("IsTimeCounting", BindingFlags.Public | BindingFlags.Instance);
                        if (counting != null && counting.CanWrite) counting.SetValue(v, true, null);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return found;
        }

        // ---------------------------------------------------------------- direct logout (no UI, no upload)

        /// <summary>True once we tried to send the UserLogout packet directly.</summary>
        public static bool DirectLogoutAttempted { get; private set; }
        public static bool DirectLogoutDone { get; private set; }

        private static double _directLogoutAt = -1;
        private static bool _directLogoutPending;

        /// <summary>
        /// Send UserLogoutApi straight to the server - no UI, no DataSave, no UpsertUserAll.
        /// This is the last-resort exit from a session when the cabinet screens will not time out
        /// (e.g. MODE SELECT on this build never self-logs-out).
        /// The local game keeps showing the logged-in screen: that is expected and harmless, the
        /// server side session (isLogin) is what matters for the next login.
        /// </summary>
        public static bool TryDirectLogout(string why)
        {
            if (DirectLogoutDone) return true;
            int monitor;
            ulong uid;
            string uname;
            if (!IsLoggedIn(out monitor, out uid, out uname))
            {
                DirectLogoutDone = true;
                return true;
            }

            DirectLogoutAttempted = true;
            string accessCode = GetLiveAccessCode(monitor);
            Log.Info("[logout] direct attempt (" + why + ") uid=" + uid + " monitor=" + monitor +
                     " accessCode=" + MaskCode(accessCode));

            if (SendLogoutPacket(monitor, uid, accessCode, true)) return true;
            Log.Warn("[logout] PacketUserLogout(Logout) failed - trying LogoutType.Quit");
            if (SendLogoutPacket(monitor, uid, accessCode, false)) return true;
            Log.Error("[logout] direct logout unavailable (packet construction/send failed)");
            _directLogoutPending = false;
            return false;
        }

        private static string GetLiveAccessCode(int monitor)
        {
            try
            {
                var ud = GetLiveUserData(monitor);
                if (ud != null && ud.Detail != null) return ud.Detail.AccessCode ?? "";
            }
            catch (Exception) { }
            return "";
        }

        private static bool SendLogoutPacket(int monitor, ulong uid, string accessCode, bool logoutType)
        {
            try
            {
                // Reflective construction: the ctor argument list differs between builds, so try
                // every public ctor and fill what we can (int index / ulong user id / LogoutType /
                // access code string / Action / optional Action<PacketStatus>).
                var packetType = typeof(global::Net.Packet.Mai2.PacketUserLogout);
                ConstructorInfo[] ctors = packetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                object packet = null;
                foreach (var c in ctors)
                {
                    ParameterInfo[] ps = c.GetParameters();
                    var args = new object[ps.Length];
                    bool usable = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        System.Type pt = ps[i].ParameterType;
                        if (pt == typeof(int)) args[i] = monitor;
                        else if (pt == typeof(ulong)) args[i] = uid;
                        else if (pt == typeof(string)) args[i] = accessCode ?? "";
                        else if (pt.IsEnum && pt.Name == "LogoutType") args[i] = Enum.ToObject(pt, logoutType ? 1 : 5);
                        else if (pt == typeof(Action)) args[i] = new Action(OnDirectLogoutDone);
                        else if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(Action<>)) args[i] = null;
                        else { usable = false; break; }
                    }
                    if (!usable) continue;
                    try
                    {
                        packet = c.Invoke(args);
                        if (packet != null)
                        {
                            Log.Info("[logout] PacketUserLogout ctor: " + c);
                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        var inner = e.InnerException ?? e;
                        Log.Warn("[logout] ctor " + c + " failed: " + inner.Message);
                    }
                }
                if (packet == null)
                {
                    Log.Warn("[logout] no usable PacketUserLogout constructor");
                    return false;
                }

                var helper = typeof(global::Net.Packet.Helper.PacketHelper);
                var start = helper.GetMethod("StartPacket", BindingFlags.Public | BindingFlags.Static);
                if (start == null || start.GetParameters().Length != 1)
                {
                    Log.Warn("[logout] PacketHelper.StartPacket not found");
                    return false;
                }
                start.Invoke(null, new object[] { packet });

                _directLogoutAt = UnityEngine.Time.realtimeSinceStartup;
                _directLogoutPending = true;
                DirectLogoutDone = false;
                Log.Info("[logout] PacketUserLogout sent userId=" + uid + " monitor=" + monitor +
                         " type=" + (logoutType ? "Logout(1)" : "Quit(5)") + " code=" + MaskCode(accessCode));
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[logout] SendLogoutPacket failed", e);
                return false;
            }
        }

        private static void OnDirectLogoutDone()
        {
            DirectLogoutDone = true;
            _directLogoutPending = false;
            Log.Info("[logout] PacketUserLogout onDone - 服务端会话已关闭" +
                     "（本机游戏画面可能仍显示已登录，下次测试请重启游戏）");
        }

        // ---------------------------------------------------------------- win32

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "keybd_event")]
        private static extern void KeybdEvent(byte vk, byte scan, uint flags, UIntPtr extra);

        private const byte VkReturn = 0x0D;
        private const uint KeyUp = 0x02;

        private static void FocusUnityWindow()
        {
            try
            {
                if (_unityWindow != IntPtr.Zero && IsWindow(_unityWindow))
                {
                    SetForegroundWindow(_unityWindow);
                    return;
                }
                _unityWindow = FindUnityWindow();
                if (_unityWindow != IntPtr.Zero) SetForegroundWindow(_unityWindow);
            }
            catch (Exception) { }
        }

        private static IntPtr FindUnityWindow()
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                uint self = (uint)global::System.Diagnostics.Process.GetCurrentProcess().Id;
                EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd)) return true;
                        uint pid;
                        GetWindowThreadProcessId(hWnd, out pid);
                        if (pid != self) return true;
                        var sb = new StringBuilder(128);
                        GetClassName(hWnd, sb, sb.Capacity);
                        if (sb.ToString() == "UnityWndClass")
                        {
                            found = hWnd;
                            return false;
                        }
                    }
                    catch (Exception) { }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception e) { Log.Warn("LoginHooks: window lookup failed: " + e.Message); }
            return found;
        }

        private static void SendEnterDown()
        {
            try { KeybdEvent(VkReturn, 0, 0, UIntPtr.Zero); }
            catch (Exception e) { Log.Warn("LoginHooks: keybd_event down failed: " + e.Message); }
        }

        private static void SendEnterUp()
        {
            try { KeybdEvent(VkReturn, 0, KeyUp, UIntPtr.Zero); }
            catch (Exception e) { Log.Warn("LoginHooks: keybd_event up failed: " + e.Message); }
        }
    }
}
