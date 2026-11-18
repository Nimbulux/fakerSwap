using System;
using System.IO;
using System.Text;

namespace FakerSwap.Core
{
    /// <summary>
    /// Scripted control / debugging hatch.  Writes to <c>FakerSwap/command.txt</c> are picked up
    /// within a second and dispatched on the Unity main thread.  Handy for automated testing and
    /// for driving the flow when the operator would rather not click the window.
    ///
    /// Supported lines (one command per line, '#' starts a comment):
    ///   source-acquire            -> ask for the source card
    ///   source-file &lt;path&gt;       -> use an existing backup / Astral json
    ///   source-clear              -> drop the loaded source
    ///   start                     -> start the transfer (target login)
    ///   confirm                   -> confirm the second-confirmation dialog
    ///   cancel                    -> dismiss the confirmation
    ///   abort                     -> abort the running task
    ///   unlock                    -> release the cabinet lock
    ///   state                     -> log the current state
    ///   ildump &lt;type&gt; [method]    -> dump the loaded (decrypted) IL of matching methods
    /// </summary>
    public static class CommandFile
    {
        private static string _lastSeen = "";
        private static DateTime _lastWriteUtc = DateTime.MinValue;
        /// <summary>
        /// False until the first poll.  A `command.txt` that was already on disk when the game
        /// started is *ignored* - otherwise a leftover "start" from the previous session replayed
        /// itself on every launch (observed: the mod started a transfer on its own at boot).
        /// Only content written after startup is executed.
        /// </summary>
        private static bool _primed;
        private static double _nextPoll;
        private const double PollIntervalSec = 0.75;

        public static string Path => System.IO.Path.Combine(Fs.Root, "command.txt");

        public static void Tick()
        {
            try
            {
                double now = UnityEngine.Time.realtimeSinceStartup;
                if (now < _nextPoll) return;
                _nextPoll = now + PollIntervalSec;

                var path = Path;
                if (!File.Exists(path)) return;
                string text;
                DateTime writeUtc;
                try
                {
                    writeUtc = File.GetLastWriteTimeUtc(path);
                    text = File.ReadAllText(path);
                }
                catch (IOException) { return; }   // being written right now

                if (!_primed)
                {
                    _primed = true;
                    _lastSeen = text;
                    _lastWriteUtc = writeUtc;
                    if (text.Trim().Length > 0)
                        Log.Info("[cmd] 启动时已存在的 command.txt 被忽略（防止旧命令重放）；写入新内容才会执行");
                    return;
                }

                if (text == _lastSeen && writeUtc == _lastWriteUtc) return;
                _lastSeen = text;
                _lastWriteUtc = writeUtc;
                Execute(text);
            }
            catch (Exception e)
            {
                Log.Error("CommandFile.Tick failed", e);
            }
        }

        private static void Execute(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int sp = line.IndexOf(' ');
                string cmd = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
                string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
                try
                {
                    Dispatch(cmd, arg);
                }
                catch (Exception e)
                {
                    Log.Error("command '" + line + "' failed", e);
                }
            }
        }

        private static void Dispatch(string cmd, string arg)
        {
            Log.Info("[cmd] " + cmd + (arg.Length > 0 ? " " + arg : ""));
            switch (cmd)
            {
                case "source-acquire":
                    FakerSwap.Flow.RebirthFlow.BeginSourceAcquire();
                    break;
                case "source-file":
                {
                    string err;
                    if (FakerSwap.Flow.RebirthFlow.UseSourceFile(arg, out err)) Log.Info("[cmd] source loaded: " + arg);
                    else Log.Warn("[cmd] source load failed: " + err);
                    break;
                }
                case "source-clear":
                    FakerSwap.Flow.RebirthFlow.ClearSource();
                    break;
                case "start":
                {
                    string err;
                    if (!FakerSwap.Flow.RebirthFlow.StartTransfer(out err)) Log.Warn("[cmd] start rejected: " + err);
                    break;
                }
                case "confirm":
                    FakerSwap.Flow.RebirthFlow.ConfirmStart();
                    break;
                case "cancel":
                    FakerSwap.Flow.RebirthFlow.CancelStart();
                    break;
                case "abort":
                    FakerSwap.Flow.RebirthFlow.Abort();
                    break;
                case "unlock":
                    MachineLock.Off();
                    break;
                case "code":
                    FakerSwap.Flow.RebirthFlow.SetSourceAccessCode(arg);
                    FakerSwap.Flow.RebirthFlow.SetTargetAccessCode(arg);
                    Log.Info("[cmd] access code set to " + (arg.Length > 6 ? arg.Substring(0, 4) + "****" + arg.Substring(arg.Length - 4) : "***"));
                    break;
                case "stack":
                {
                    var list = FakerSwap.Flow.ProcessHooks.GetProcessList();
                    if (list == null) { Log.Info("[cmd] process list unavailable (Ready=" + FakerSwap.Flow.ProcessHooks.Ready + ")"); break; }
                    Log.Info("[cmd] process stack (" + list.Count + "):");
                    int i = 0;
                    foreach (var ctrl in list)
                    {
                        try
                        {
                            var p = ctrl.Process;
                            Log.Info("[cmd]   [" + (i++) + "] " + (p == null ? "<null>" : p.GetType().FullName) + " state=" + ctrl.State);
                        }
                        catch (Exception e) { Log.Warn("[cmd]   [" + (i++) + "] <error: " + e.Message + ">"); }
                    }
                    break;
                }
                case "myes":
                    FakerSwap.Flow.RebirthFlow.ManualContinueYes = !FakerSwap.Flow.RebirthFlow.ManualContinueYes;
                    Log.Warn("[cmd] ManualContinueYes = " + FakerSwap.Flow.RebirthFlow.ManualContinueYes);
                    break;
                case "areaok":
                    FakerSwap.Flow.RebirthFlow.ConfirmAreaSelect();
                    break;
                case "mmselect":
                    FakerSwap.Flow.Navigation.ManualModeSelect = !FakerSwap.Flow.Navigation.ManualModeSelect;
                    Log.Warn("[cmd] ManualModeSelect = " + FakerSwap.Flow.Navigation.ManualModeSelect);
                    break;
                case "modediag":
                    FakerSwap.Flow.ModeDiag.Toggle();
                    break;
                case "missiondump":
                    FakerSwap.Flow.MapHooks.DumpCategories();
                    break;
                case "queue":
                    FakerSwap.Flow.RebirthFlow.DumpQueue();
                    break;
                case "cdiag":
                    FakerSwap.Flow.ContinueDiag.Toggle();
                    break;
                case "cmute":
                    FakerSwap.Flow.ContinueDiag.InputSilenced = !FakerSwap.Flow.ContinueDiag.InputSilenced;
                    Log.Warn("[cmd] InputSilenced = " + FakerSwap.Flow.ContinueDiag.InputSilenced +
                             "（true = 我们完全不注入按键，交给人工）");
                    break;
                case "ildump":
                {
                    string typeFilter = arg, methodFilter = "";
                    int sp2 = arg.IndexOf(' ');
                    if (sp2 > 0)
                    {
                        typeFilter = arg.Substring(0, sp2);
                        methodFilter = arg.Substring(sp2 + 1).Trim();
                    }
                    IlDump.Dump(typeFilter, methodFilter);
                    break;
                }
                case "state":
                    Log.Info("[cmd] phase=" + FakerSwap.Flow.RebirthFlow.Phase +
                             " status=" + FakerSwap.Flow.RebirthFlow.StatusText +
                             " stage=" + GameStageTracker.Current +
                             " lock=" + MachineLock.Active);
                    break;
                default:
                    Log.Warn("[cmd] unknown command: " + cmd);
                    break;
            }
        }
    }
}
