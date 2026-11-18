using System;

namespace FakerSwap.UI.NativeWindow
{
    /// <summary>
    /// Picks a CJK-capable font family through System.Drawing (which is shipped with the game's
    /// Mono in Package\Sinmai_Data\Managed\System.Drawing.dll).  Every entry point is kept behind
    /// try/catch because a missing GDI+ / System.Drawing assembly must never take the window down:
    /// we simply fall back to a hard-coded family name, and GDI itself falls back again.
    /// </summary>
    internal static class Fonts
    {
        private static string _family;

        /// <summary>"Microsoft YaHei UI" → "Microsoft YaHei" → "SimSun" → "Segoe UI".</summary>
        public static string Family
        {
            get
            {
                if (_family == null) _family = Resolve();
                return _family;
            }
        }

        private static string Resolve()
        {
            string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun", "Segoe UI" };
            foreach (var name in candidates)
            {
                if (Exists(name)) return name;
            }
            return "SimSun";
        }

        private static bool Exists(string name)
        {
            try
            {
                // The call is inside try/catch on purpose: if the runtime cannot load
                // System.Drawing, the JIT failure surfaces here instead of escaping.
                return InstalledFonts.Has(name);
            }
            catch (Exception)
            {
                // System.Drawing missing or GDI+ failed: let the caller try the next name and
                // ultimately let GDI pick a fallback via CreateFontW.
                return false;
            }
        }
    }

    /// <summary>Isolated System.Drawing usage.</summary>
    internal static class InstalledFonts
    {
        public static bool Has(string name)
        {
            using (var family = new System.Drawing.FontFamily(name))
            {
                return family.Name.Length > 0;
            }
        }
    }
}
