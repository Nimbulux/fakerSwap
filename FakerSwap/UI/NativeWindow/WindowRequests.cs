using System;
using System.Threading;

namespace FakerSwap.UI.NativeWindow
{
    /// <summary>
    /// Tiny one-way mailbox: window thread → game thread (requests) and game thread → window
    /// thread (transient status text).  Never carries game objects.
    /// </summary>
    internal static class WindowRequests
    {
        private static readonly object Sync = new object();
        private static int _refreshSources;
        private static int _refreshAreas;
        private static string _notice;
        private static int _noticeUntil;

        /// <summary>Ask the game thread to re-scan the backup directory.</summary>
        public static void RequestSourcesRefresh()
        {
            Interlocked.Exchange(ref _refreshSources, 1);
        }

        public static bool ConsumeSourcesRefresh()
        {
            return Interlocked.Exchange(ref _refreshSources, 0) != 0;
        }

        /// <summary>Ask the game thread to refresh Navigation.ListAreas() for the crawl panel.</summary>
        public static void RequestAreasRefresh()
        {
            Interlocked.Exchange(ref _refreshAreas, 1);
        }

        public static bool ConsumeAreasRefresh()
        {
            return Interlocked.Exchange(ref _refreshAreas, 0) != 0;
        }

        /// <summary>Queue a short status line shown in the window footer / toast area.</summary>
        public static void Notice(string text)
        {
            lock (Sync)
            {
                _notice = text;
                _noticeUntil = Environment.TickCount + 5000;
            }
        }

        /// <summary>Take the pending notice (or null).  Called from the window thread.</summary>
        public static string TakeNotice()
        {
            lock (Sync)
            {
                if (_notice == null) return null;
                if (Environment.TickCount > _noticeUntil)
                {
                    _notice = null;
                    return null;
                }
                string text = _notice;
                _notice = null;
                return text;
            }
        }
    }
}
