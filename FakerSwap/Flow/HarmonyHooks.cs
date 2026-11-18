using System;
using FakerSwap.Core;
using FakerSwap.Network;

namespace FakerSwap.Flow
{
    /// <summary>
    /// Single place that installs every Harmony patch used by the mod.  Each sub-module is
    /// patched independently so one broken patch cannot take the whole mod down.
    /// </summary>
    public static class HarmonyHooks
    {
        public static void Install(HarmonyLib.Harmony harmony)
        {
            // Infrastructure first: the other groups use ProcessHooks.Container / stage tracking.
            Step("ProcessHooks", () => ProcessHooks.Install(harmony));
            Step("LoginHooks", () => LoginHooks.Install(harmony));
            Step("InputLock", () => InputLock.Install(harmony));
            Step("PayloadRewriter", () => PayloadRewriter.Install(harmony));

            Step("InputInject", () => InputInject.Install(harmony));
            Step("Navigation", () => Navigation.Install());
            Step("ScoreHooks", () => ScoreHooks.Install());
            Step("MapHooks", () => MapHooks.Install());
            Step("NetworkHooks", () => NetworkHooks.Install());
        }

        private static void Step(string name, Action action)
        {
            try
            {
                action();
                Log.Info("hook group installed: " + name);
            }
            catch (Exception e)
            {
                Log.Error("hook group failed: " + name, e);
            }
        }
    }
}
