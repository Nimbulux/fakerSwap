using System;
using System.Collections.Generic;
using System.Reflection;
using FakerSwap.Core;
using FakerSwap.Network;
using HarmonyLib;
using Net;
using Net.Packet;
using Net.VO;

namespace FakerSwap.Flow
{
    /// <summary>
    /// HTTP request/response taps, used by <see cref="NetworkLog"/> and the payload rewriter.
    ///
    /// Patch points (same ones Sinmai-Assist / AquaMai use):
    ///   * <c>Net.Packet.Packet.ProcImpl</c> prefix  — the packet is <c>Ready</c>, the request object exists,
    ///     <c>Query.GetRequest()</c> gives the JSON that is about to be sent (after <see cref="PayloadRewriter"/>
    ///     rewrote it in its <c>set_State</c> postfix).
    ///   * <c>Net.Packet.Packet.ProcImpl</c> postfix — the packet reached <c>Done</c> / <c>Error</c>;
    ///     the typed response object can be serialized back to JSON through <c>VOSerializer.Serialize()</c>.
    ///
    /// Everything is wrapped in try/catch: a logging hook must never take the game loop down.
    /// </summary>
    public static class NetworkHooks
    {
        private static bool _installed;
        private const int MaxBodyChars = 200000;

        private static readonly Dictionary<object, string> ApiByPacket = new Dictionary<object, string>();
        private static readonly Dictionary<object, int> LastState = new Dictionary<object, int>();

        public static void Install()
        {
            if (_installed) return;
            try
            {
                var harmony = FakerSwapMod.Patcher;
                if (harmony == null)
                {
                    Log.Warn("NetworkHooks.Install: Harmony instance not ready");
                    return;
                }
                var proc = AccessTools.Method(typeof(Packet), "ProcImpl");
                if (proc == null)
                {
                    Log.Error("NetworkHooks.Install: Net.Packet.Packet.ProcImpl not found");
                    return;
                }
                var prefix = AccessTools.Method(typeof(NetworkHooks), "ProcPrefix");
                var postfix = AccessTools.Method(typeof(NetworkHooks), "ProcPostfix");
                harmony.Patch(proc, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                _installed = true;
                Log.Info("NetworkHooks installed (Packet.ProcImpl prefix/postfix)");
            }
            catch (Exception e)
            {
                Log.Error("NetworkHooks.Install failed", e);
            }
        }

        [HarmonyPrefix]
        public static void ProcPrefix(Packet __instance)
        {
            try
            {
                if (__instance == null || __instance.State != PacketState.Ready) return;
                var query = __instance.Query;
                if (query == null) return;
                string api = query.Api ?? "";
                ApiByPacket[__instance] = api;
                LastState[__instance] = (int)PacketState.Ready;
                TrimMaps();
                NetworkLog.Line("REQ", api, SafeRequest(query));
            }
            catch (Exception e)
            {
                Log.Error("NetworkHooks.ProcPrefix failed", e);
            }
        }

        [HarmonyPostfix]
        public static void ProcPostfix(Packet __instance)
        {
            try
            {
                if (__instance == null) return;
                var state = __instance.State;
                if (state != PacketState.Done && state != PacketState.Error && state != PacketState.RetryWait) return;

                int last;
                if (LastState.TryGetValue(__instance, out last) && last == (int)state) return; // already logged
                LastState[__instance] = (int)state;

                string api;
                if (!ApiByPacket.TryGetValue(__instance, out api) || api == null)
                    api = __instance.Query != null ? (__instance.Query.Api ?? "") : "";

                if (state == PacketState.Done)
                {
                    NetworkLog.Line("RES", api, SafeResponse(__instance));
                    ApiByPacket.Remove(__instance);
                    LastState.Remove(__instance);
                }
                else
                {
                    NetworkLog.Line("ERR", api, "state=" + state + " status=" + __instance.Status +
                                                 " error=" + (__instance.ErrorString ?? ""));
                }
                TrimMaps();
            }
            catch (Exception e)
            {
                Log.Error("NetworkHooks.ProcPostfix failed", e);
            }
        }

        private static string SafeRequest(INetQuery query)
        {
            try
            {
                string body = query.GetRequest();
                return Truncate(string.IsNullOrEmpty(body) ? "<empty>" : body);
            }
            catch (Exception e)
            {
                return "<GetRequest failed: " + e.Message + ">";
            }
        }

        private static string SafeResponse(Packet packet)
        {
            try
            {
                var query = packet.Query;
                if (query == null) return "<no query>";
                var prop = query.GetType().GetProperty("Response", BindingFlags.Instance | BindingFlags.Public);
                if (prop == null) return "<no Response property on " + query.GetType().Name + ">";
                object response = prop.GetValue(query, null);
                if (response == null) return "<null response>";
                var serializer = response as VOSerializer;
                if (serializer == null) return "<response is not VOSerializer: " + response.GetType().FullName + ">";
                return Truncate(serializer.Serialize());
            }
            catch (Exception e)
            {
                return "<response serialize failed: " + e.Message + ">";
            }
        }

        private static string Truncate(string s)
        {
            if (s == null) return "";
            if (s.Length <= MaxBodyChars) return s;
            return s.Substring(0, MaxBodyChars) + "…(" + s.Length + " chars truncated)";
        }

        private static void TrimMaps()
        {
            if (ApiByPacket.Count > 64) ApiByPacket.Clear();
            if (LastState.Count > 64) LastState.Clear();
        }
    }
}
