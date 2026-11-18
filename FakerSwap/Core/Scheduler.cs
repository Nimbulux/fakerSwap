using System;
using System.Collections.Generic;
using UnityEngine;

namespace FakerSwap.Core
{
    /// <summary>Frame-driven timer queue.  Everything runs on the Unity main thread.</summary>
    public static class Scheduler
    {
        public sealed class Job
        {
            internal double Due;
            internal double Period;
            internal Action Action;
            internal string Name;
            internal bool Cancelled;
            internal bool OneShot;
        }

        private static readonly List<Job> Jobs = new List<Job>();
        private static readonly List<Job> Scratch = new List<Job>();

        private static double Now => Time.realtimeSinceStartup;

        public static Job After(double seconds, Action action, string name = null)
        {
            return Add(seconds, false, action, name);
        }

        public static Job Every(double seconds, Action action, string name = null)
        {
            return Add(seconds, true, action, name);
        }

        private static Job Add(double seconds, bool repeat, Action action, string name)
        {
            if (action == null) return null;
            var job = new Job
            {
                Due = Now + Math.Max(0.0, seconds),
                Period = Math.Max(0.02, seconds),
                Action = action,
                Name = name,
                OneShot = !repeat
            };
            Jobs.Add(job);
            return job;
        }

        public static void Cancel(Job job)
        {
            if (job != null) job.Cancelled = true;
        }

        public static void CancelNamed(string name)
        {
            if (name == null) return;
            foreach (var j in Jobs) if (j.Name == name) j.Cancelled = true;
        }

        public static bool HasNamed(string name)
        {
            foreach (var j in Jobs) if (!j.Cancelled && j.Name == name) return true;
            return false;
        }

        public static void Clear()
        {
            Jobs.Clear();
        }

        public static void Tick()
        {
            double now = Now;
            Scratch.Clear();
            Scratch.AddRange(Jobs);

            foreach (var job in Scratch)
            {
                if (job.Cancelled) continue;
                if (now < job.Due) continue;
                try { job.Action(); }
                catch (Exception e) { Log.Error("scheduled action failed (" + (job.Name ?? "anon") + ")", e); }

                if (job.Cancelled) continue;
                if (job.OneShot) job.Cancelled = true;
                else job.Due = now + job.Period;
            }

            for (int i = Jobs.Count - 1; i >= 0; i--) if (Jobs[i].Cancelled) Jobs.RemoveAt(i);
        }
    }
}
