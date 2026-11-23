using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexInputUsage
{
    internal static class UsageStatsChecks
    {
        internal static bool Run(StringBuilder report, string workDir)
        {
            var now = new DateTime(2026, 11, 23, 15, 30, 0, DateTimeKind.Local);
            var rows = new List<StateThreadRecord>
            {
                new StateThreadRecord { Id = "detailed", Model = "gpt-6.2-sol", TokensUsed = 9999, RecencyAtMs = Millis(now) },
                new StateThreadRecord { Id = "fallback", Model = "gpt-6.2-sol", TokensUsed = 50, RecencyAtMs = Millis(now.AddHours(-3)) },
                new StateThreadRecord { Id = "archived", Model = "gpt-6-astra", TokensUsed = 600, RecencyAtMs = Millis(now.AddDays(-400)) }
            };
            string path = Path.Combine(workDir, "usage-check-" + Guid.NewGuid().ToString("N") + ".jsonl");
            UsageResponse[] parsed;
            try
            {
                File.WriteAllLines(path, new[]
                {
                    Record(now.AddHours(-1), "r1", "t1", "detailed"),
                    Record(now.AddHours(-1), "r1", "t1", "detailed"),
                    Record(now.AddHours(-1), "foreign", "t1", "foreign"),
                    Record(now.AddHours(-25), "r2", "t2", "detailed"),
                    "{\"type\":\"token_usage_record\",\"payload\":{\"usage\":",
                    "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"t1\",\"model\":\"gpt-6-astra\"}}"
                });
                parsed = HistoryStatsReader.ReadRollout(path, "detailed");
            }
            finally { File.Delete(path); }
            var data = new UsageHistoryData(rows, new Dictionary<string, UsageResponse[]> { { "detailed", parsed } });

            var today = data.Build(UsagePeriod.Today, now, now, now);
            var day = data.Build(UsagePeriod.Day, now, now, now);
            var week = data.Build(UsagePeriod.Week, now, now, now);
            var month = data.Build(UsagePeriod.Month, now, now, now);
            var all = data.Build(UsagePeriod.All, now, now, now);
            var custom = data.Build(UsagePeriod.Custom, now, now, now.AddHours(-2));
            var longer = data.Build(UsagePeriod.Custom, now, now.AddHours(-49), now);
            var edge = data.Build(UsagePeriod.Day, now.Date, now, now);
            var midnight = data.Build(UsagePeriod.Today, now.Date.AddMinutes(5), now, now);
            bool dates = today.Grain == UsageGrain.Hour && today.Points.Count == 16 && today.Total.Tokens == 170
                && midnight.Points.Count == 2 && midnight.ElapsedBuckets == 1
                && day.Points.Count == 25 && day.Total.Tokens == 170 && edge.Points.Count == 25
                && week.Points.Count == 7 && week.Total.Tokens == 290 && month.Points.Count == 30 && month.Total.Tokens == 290
                && all.Grain == UsageGrain.Month && all.Total.Tokens == 890 && custom.Grain == UsageGrain.Hour
                && custom.Points.Count == 3 && custom.Total.Tokens == 120
                && longer.Grain == UsageGrain.Day && longer.Total.Tokens == 290;

            var total = week.Total;
            bool sources = parsed.Length == 2 && parsed[0].Model == "gpt-6-astra" && parsed[1].Model == null
                && total.Sessions == 2 && total.DetailedThreads == 1 && total.UnbrokenTokens == 50 && total.Calls == 2
                && total.UncachedInputTokens == 80 && total.CachedInputTokens == 120 && total.ReasoningTokens == 10 && total.OutputTokens == 30
                && total.Tokens == total.UncachedInputTokens + total.CachedInputTokens + total.ReasoningTokens + total.OutputTokens + total.UnbrokenTokens
                && total.CacheHitPct == 60 && week.Points.Sum(point => point.Totals.Tokens) == total.Tokens
                && week.Models.Single(model => model.Key == "6.2sol").Value.Tokens == 170
                && week.TopThreads.Length == 2 && week.AllTimeTokens == 890
                && all.Points.First(point => point.Totals.Tokens == 600).Totals.CacheHitPct == null;
            bool curves = CheckCurves();
            bool interaction = new[] { 1f, 1.25f, 1.5f, 2f }.All(scale => DetailPopoverForm.CheckUsageInteraction(data, now, scale));
            report.AppendLine(string.Format("usage_stats_tests dates={0} sources={1} curves={2} interaction={3}", dates, sources, curves, interaction));
            return dates && sources && curves && interaction;
        }

        private static bool CheckCurves()
        {
            var points = new[] { new PointF(0, 40), new PointF(10, 0), new PointF(20, 90), new PointF(30, 70) };
            for (int n = 0; n <= points.Length; n++)
            {
                var samples = points.Take(n).ToArray();
                using (var curve = OverlayCharts.BuildSmoothCurvePath(samples))
                using (var area = OverlayCharts.BuildSmoothAreaPath(samples, 100))
                using (var band = OverlayCharts.BuildSmoothBandPath(samples, samples.Select(p => new PointF(p.X, p.Y + 5)).ToArray()))
                {
                    if (n < 2)
                    {
                        if (curve.PointCount + area.PointCount + band.PointCount != 0) return false;
                        continue;
                    }
                    var controls = curve.PathPoints;
                    if (controls.Length != 3 * (n - 1) + 1 || area.PointCount <= controls.Length || band.PointCount <= area.PointCount)
                        return false;
                    for (int i = 0; i < n - 1; i++)
                    {
                        float low = Math.Min(samples[i].Y, samples[i + 1].Y);
                        float high = Math.Max(samples[i].Y, samples[i + 1].Y);
                        if (controls.Skip(i * 3).Take(4).Any(p => p.Y < low || p.Y > high)) return false;
                    }
                }
            }
            return true;
        }

        private static long Millis(DateTime time) { return (long)(time.ToUniversalTime() - UsageHistoryData.Epoch).TotalMilliseconds; }

        private static string Record(DateTime time, string response, string turn, string thread)
        {
            return "{\"timestamp\":\"" + time.ToUniversalTime().ToString("o") + "\",\"type\":\"token_usage_record\",\"payload\":{" +
                "\"thread_id\":\"" + thread + "\",\"turn_id\":\"" + turn + "\",\"response_id\":\"" + response + "\"," +
                "\"usage\":{\"input_tokens\":100,\"cached_input_tokens\":60,\"output_tokens\":20,\"reasoning_output_tokens\":5,\"total_tokens\":120}}}";
        }
    }
}
