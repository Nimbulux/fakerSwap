using System;
using UnityEngine;
using FakerSwap.UI.NativeWindow;

namespace FakerSwap.UI
{
    /// <summary>
    /// Minimal IMGUI fallback shown only when the standalone window could not be created (or its
    /// GDI rendering failed).  It intentionally stays ugly and tiny: it exists so the operator (and
    /// we, while debugging) can still drive the flow from inside the game.
    ///
    /// Runs on the Unity main thread inside <see cref="UIHost.OnGUI"/>; buttons go through the
    /// same <see cref="UiActions"/> path as the standalone window.
    /// </summary>
    internal static class GameFallbackUi
    {
        private static GUIStyle _title;
        private static GUIStyle _text;
        private static GUIStyle _small;
        private static Texture2D _bg;

        public static void Draw()
        {
            var s = WindowState.Current;
            if (s == null) return;
            try
            {
                EnsureStyles();
                string[] lines = LogTail.Get(6);
                float lineH = 18f;
                int textLines = 4 + (string.IsNullOrEmpty(s.LastError) ? 0 : 1);
                float height = 34f + textLines * lineH + 8f + 4 * 32f + 24f + lines.Length * lineH + 12f;
                var rect = new Rect(12f, 60f, 392f, Math.Min(height, Screen.height - 70f));

                GUI.DrawTexture(rect, _bg);
                GUI.Label(new Rect(rect.x + 10f, rect.y + 6f, rect.width - 20f, 22f),
                    "FakerSwap 兜底面板（独立窗口不可用）", _title);

                float y = rect.y + 32f;
                y = Line(rect, "阶段：" + s.PhaseName + "   ·   " + (s.LockActive ? "机台已占用" : "机台空闲"), y, lineH);
                y = Line(rect, "状态：" + s.Status, y, lineH);
                if (!string.IsNullOrEmpty(s.LastError)) y = Line(rect, "错误：" + s.LastError, y, lineH);
                y = Line(rect, "批次：" + (s.CurrentCredit > 0 ? s.CurrentCredit + " / " + s.PlanCredits : "—") +
                               "   轨：" + (s.CurrentTrack > 0 ? s.CurrentTrack + " / 3" : "—") +
                               "   进度：" + (s.Progress * 100.0).ToString("0.0") + "%", y, lineH);
                y = Line(rect, "源数据：" + (string.IsNullOrEmpty(s.SourceDisplay) ? "未选择" : s.SourceDisplay), y, lineH);
                y = Line(rect, "当前曲目：" + (string.IsNullOrEmpty(s.CurrentSong) ? "—" : s.CurrentSong), y, lineH);
                y += 6f;

                float bw = (rect.width - 30f) / 2f;
                float bx1 = rect.x + 10f;
                float bx2 = rect.x + 20f + bw;

                if (GUI.Button(new Rect(bx1, y, bw, 26f), "刷新列表")) UiActions.RefreshSources();
                if (s.Sources != null && s.Sources.Length > 0)
                {
                    if (GUI.Button(new Rect(bx2, y, bw, 26f), "使用最新备份")) UiActions.UseSource(s.Sources[0].Path);
                }
                else
                {
                    GUI.Label(new Rect(bx2, y, bw, 26f), "（没有备份文件）", _small);
                }
                y += 32f;

                if (GUI.Button(new Rect(bx1, y, bw, 26f), "登录获取源数据")) UiActions.BeginSourceAcquire();
                if (GUI.Button(new Rect(bx2, y, bw, 26f), "开始转生")) UiActions.StartTransfer();
                y += 32f;

                if (GUI.Button(new Rect(bx1, y, bw, 26f), "确认开始")) UiActions.ConfirmStart();
                if (GUI.Button(new Rect(bx2, y, bw, 26f), "取消确认")) UiActions.CancelStart();
                y += 32f;

                if (GUI.Button(new Rect(bx1, y, bw, 26f), "中止任务")) UiActions.Abort();
                if (GUI.Button(new Rect(bx2, y, bw, 26f), "显示/隐藏窗口"))
                {
                    if (NativeModWindow.IsVisible) NativeModWindow.SetVisible(false);
                    else NativeModWindow.SetVisible(true);
                }
                y += 34f;

                GUI.Label(new Rect(bx1, y, rect.width - 20f, 16f), "日志（最近 6 行）：", _small);
                y += 18f;
                for (int i = 0; i < lines.Length; i++)
                {
                    GUI.Label(new Rect(bx1, y, rect.width - 20f, lineH), lines[i], _small);
                    y += lineH;
                }
            }
            catch (Exception)
            {
                // Never let the fallback panel break OnGUI.
            }
        }

        private static float Line(Rect rect, string text, float y, float lineH)
        {
            GUI.Label(new Rect(rect.x + 10f, y, rect.width - 20f, lineH), text, _text);
            return y + lineH;
        }

        private static void EnsureStyles()
        {
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0.10f, 0.11f, 0.13f, 0.92f));
                _bg.Apply();
            }
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
                _title.normal.textColor = new Color(0.95f, 0.75f, 0.35f, 1f);
            }
            if (_text == null)
            {
                _text = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
                _text.normal.textColor = new Color(0.90f, 0.91f, 0.93f, 1f);
            }
            if (_small == null)
            {
                _small = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 11,
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
                _small.normal.textColor = new Color(0.68f, 0.70f, 0.74f, 1f);
            }
        }
    }
}
