using System;
using System.Collections.Generic;
using FakerSwap.Core;
using FakerSwap.Data;

namespace FakerSwap.Flow
{
    /// <summary>
    /// Estimates how long a transfer takes:
    ///   per song   : chart length (or the skip cost when skipping)
    ///   per credit : login -> song select -> result -> upload cooldown -> logout
    ///
    /// 每 credit 的固定开销以 <see cref="Navigation"/> 暴露的常量为基础（进选歌页/选曲/过场/结算），
    /// 再和配置里的 <c>BaseOverheadPerCreditSec</c> 取较大值（配置通常把登录/登出/上传冷却一起算进去了）。
    /// </summary>
    public static class TimeEstimator
    {
        /// <summary>Song length in seconds, or -1 when unknown.</summary>
        public delegate double LengthLookup(int musicId);

        /// <summary>整批（一个 credit）的固定开销明细，便于 UI 展示。</summary>
        public sealed class Breakdown
        {
            public int Credits;
            public int Tracks;
            public double ChartSec;
            public double PerCreditOverheadSec;
            public bool SkipCharts;

            public double TotalSec { get { return ChartSec + Credits * PerCreditOverheadSec; } }
            public TimeSpan Total { get { return TimeSpan.FromSeconds(TotalSec); } }
        }

        /// <summary>每 credit 的固定开销（秒）。参数为空/0 时直接取 Navigation 的常量估算。</summary>
        public static double CreditOverheadSec(double configuredOverheadSec, bool skipCharts)
        {
            double navigation = Navigation.EstimateCreditOverheadSec(skipCharts);
            double configured = configuredOverheadSec > 0 ? configuredOverheadSec : 0;
            return configured > navigation ? configured : navigation;
        }

        public static TimeSpan Estimate(IList<TransferItem> items, int batchSize, LengthLookup length,
            double creditOverheadSec, bool skipCharts)
        {
            return BreakdownOf(items, batchSize, length, creditOverheadSec, skipCharts).Total;
        }

        public static Breakdown BreakdownOf(IList<TransferItem> items, int batchSize, LengthLookup length,
            double creditOverheadSec, bool skipCharts)
        {
            var result = new Breakdown { SkipCharts = skipCharts };
            if (items == null || items.Count == 0) return result;
            if (batchSize < 1) batchSize = 3;
            if (batchSize > 3) batchSize = 3;

            result.Tracks = items.Count;
            result.Credits = (items.Count + batchSize - 1) / batchSize;
            result.PerCreditOverheadSec = CreditOverheadSec(creditOverheadSec, skipCharts);

            double chartSec = 0;
            foreach (var it in items)
            {
                if (skipCharts) continue;
                double len = -1;
                if (length != null) len = length(it.MusicId);
                if (len < 0) len = 120; // 未知曲长按 2 分钟
                chartSec += len;
            }
            result.ChartSec = chartSec;
            return result;
        }

        public static string Describe(TimeSpan t)
        {
            if (t.TotalSeconds < 1) return "0 秒";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
            if (t.TotalMinutes >= 1) return $"{t.Minutes} 分 {t.Seconds} 秒";
            return $"{(int)t.TotalSeconds} 秒";
        }

        /// <summary>给 UI 的一行明细：曲数 / 批数 / 曲长 / 固定开销 / 合计。</summary>
        public static string Describe(Breakdown b)
        {
            if (b == null || b.Tracks == 0) return "0 首";
            return b.Tracks + " 首 / " + b.Credits + " 批" +
                   (b.SkipCharts ? "（跳过谱面）" : "，曲长 " + Describe(TimeSpan.FromSeconds(b.ChartSec))) +
                   "，每批固定 " + b.PerCreditOverheadSec.ToString("0") + "s" +
                   "，合计 " + Describe(b.Total);
        }
    }
}
