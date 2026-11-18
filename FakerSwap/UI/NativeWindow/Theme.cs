namespace FakerSwap.UI.NativeWindow
{
    /// <summary>Dark theme colours for the standalone window (ported from Sinmai-Assist).</summary>
    internal static class Theme
    {
        public static readonly uint Background = Win32.Rgb(32, 33, 36);
        public static readonly uint Panel = Win32.Rgb(44, 46, 50);
        public static readonly uint PanelAlt = Win32.Rgb(38, 40, 44);
        public static readonly uint Border = Win32.Rgb(66, 70, 76);
        public static readonly uint Text = Win32.Rgb(232, 234, 237);
        public static readonly uint Muted = Win32.Rgb(150, 155, 162);
        public static readonly uint Accent = Win32.Rgb(74, 144, 217);
        public static readonly uint AccentDark = Win32.Rgb(55, 105, 160);
        public static readonly uint Input = Win32.Rgb(24, 25, 28);
        public static readonly uint Good = Win32.Rgb(90, 200, 130);
        public static readonly uint GoodDark = Win32.Rgb(52, 120, 82);
        public static readonly uint Warn = Win32.Rgb(235, 170, 70);
        public static readonly uint WarnDark = Win32.Rgb(140, 100, 40);
        public static readonly uint Bad = Win32.Rgb(225, 100, 100);
        public static readonly uint BadDark = Win32.Rgb(150, 62, 62);
        public static readonly uint Track = Win32.Rgb(24, 25, 28);
        public static readonly uint Selected = Win32.Rgb(52, 78, 112);
        public static readonly uint Disabled = Win32.Rgb(70, 72, 76);
    }
}
