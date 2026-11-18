using System;
using System.Collections.Generic;
using System.Threading;

namespace FakerSwap.Core
{
    /// <summary>Queue of actions that must run on the Unity (game) main thread.</summary>
    public static class MainThread
    {
        private static readonly Queue<Action> Queue = new Queue<Action>();
        private static readonly object Sync = new object();

        public static void Post(Action action)
        {
            if (action == null) return;
            lock (Sync) Queue.Enqueue(action);
        }

        public static void Drain(int max = 128)
        {
            for (int i = 0; i < max; i++)
            {
                Action a;
                lock (Sync)
                {
                    if (Queue.Count == 0) return;
                    a = Queue.Dequeue();
                }
                try { a(); }
                catch (Exception e) { Log.Error("main-thread action failed", e); }
            }
        }

        public static void Clear()
        {
            lock (Sync) Queue.Clear();
        }
    }
}
