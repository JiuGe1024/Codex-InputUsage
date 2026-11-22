using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexInputUsage
{
    internal sealed class DailyUsagePoint
    {
        internal DateTime LocalDate;
        internal string DayLabel;
        internal string WeekdayLabel;
        internal long Tokens;
        internal int ThreadCount;
        internal bool IsToday;
    }

    internal sealed class ModelUsageBucket
    {
        internal string ModelName;
        internal long Tokens;
        internal int ThreadCount;
        internal double SharePct;
    }

    internal sealed class TopThreadItem
    {
        internal string Id;
        internal string DisplayName;
        internal string ModelName;
        internal long Tokens;
        internal DateTime LastActiveLocal;
    }

    internal sealed class HistoryStatsSnapshot
    {
        internal long TodayTokens;
        internal int TodayThreads;
        internal long SevenDayTokens;
        internal int SevenDayThreads;
        internal long SevenDayAvgDailyTokens;
        internal long AllTimeTokens;
        internal int AllTimeThreads;
        internal List<DailyUsagePoint> Last7Days = new List<DailyUsagePoint>();
        internal List<ModelUsageBucket> Models7d = new List<ModelUsageBucket>();
        internal List<TopThreadItem> TopThreads7d = new List<TopThreadItem>();
        internal DateTime ComputedAtLocal = DateTime.Now;
    }

    /// <summary>
    /// Aggregates today & 7-day Codex usage directly from state_5.sqlite (real user threads only,
    /// subagents excluded), cached by database file timestamp + local calendar date for sub-ms reads.
    /// </summary>
    internal sealed class HistoryStatsReader
    {
        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly string[] ChineseWeekdays = new string[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

        private string _cachedStamp = string.Empty;
        private HistoryStatsSnapshot _cached;

        internal HistoryStatsSnapshot GetSnapshot(bool force)
        {
            DateTime todayLocal = DateTime.Now.Date;
            string dbPath = CodexStateDb.ResolveDatabasePath();
            string walPath = dbPath + "-wal";
            string stamp = BuildStamp(dbPath) + "|" + BuildStamp(walPath) + "|" + todayLocal.ToString("yyyyMMdd");

            if (!force && _cached != null && string.Equals(_cachedStamp, stamp, StringComparison.Ordinal))
                return _cached;

            var rows = CodexStateDb.TryReadRecentThreads(0);
            _cached = BuildFromRows(rows, todayLocal);
            _cachedStamp = stamp;
            return _cached;
        }

        internal static HistoryStatsSnapshot BuildFromRows(List<StateThreadRecord> rows, DateTime todayLocal)
        {
            var snap = new HistoryStatsSnapshot
            {
                ComputedAtLocal = DateTime.Now
            };

            DateTime startDay = todayLocal.AddDays(-6);
            var dayMap = new Dictionary<DateTime, DailyUsagePoint>();
            for (int i = 0; i < 7; i++)
            {
                DateTime d = startDay.AddDays(i);
                var pt = new DailyUsagePoint
                {
                    LocalDate = d,
                    DayLabel = i == 6 ? "今天" : d.ToString("MM/dd"),
                    WeekdayLabel = ChineseWeekdays[(int)d.DayOfWeek],
                    IsToday = (i == 6)
                };
                dayMap[d] = pt;
                snap.Last7Days.Add(pt);
            }

            if (rows == null || rows.Count == 0) return snap;

            var model7d = new Dictionary<string, ModelUsageBucket>(StringComparer.OrdinalIgnoreCase);
            var top7d = new List<TopThreadItem>();

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r == null) continue;
                long tok = Math.Max(0L, r.TokensUsed);
                snap.AllTimeTokens += tok;
                snap.AllTimeThreads++;

                if (r.RecencyAtMs <= 0) continue;
                DateTime activeLocal;
                try
                {
                    activeLocal = UnixEpochUtc.AddMilliseconds(r.RecencyAtMs).ToLocalTime();
                }
                catch
                {
                    continue;
                }

                DateTime activeDate = activeLocal.Date;
                DailyUsagePoint dayPt;
                if (!dayMap.TryGetValue(activeDate, out dayPt))
                    continue;

                dayPt.Tokens += tok;
                dayPt.ThreadCount++;
                snap.SevenDayTokens += tok;
                snap.SevenDayThreads++;

                if (dayPt.IsToday)
                {
                    snap.TodayTokens += tok;
                    snap.TodayThreads++;
                }

                string modelKey = string.IsNullOrWhiteSpace(r.Model) ? "未知模型" : r.Model.Trim();
                ModelUsageBucket mb;
                if (!model7d.TryGetValue(modelKey, out mb))
                {
                    mb = new ModelUsageBucket { ModelName = modelKey };
                    model7d[modelKey] = mb;
                }
                mb.Tokens += tok;
                mb.ThreadCount++;

                string title = !string.IsNullOrWhiteSpace(r.Name)
                    ? r.Name.Trim()
                    : (!string.IsNullOrWhiteSpace(r.Title) ? r.Title.Trim() : MetricFormatter.ShortThreadId(r.Id));
                title = title.Replace('\r', ' ').Replace('\n', ' ');

                top7d.Add(new TopThreadItem
                {
                    Id = r.Id,
                    DisplayName = title,
                    ModelName = modelKey,
                    Tokens = tok,
                    LastActiveLocal = activeLocal
                });
            }

            snap.SevenDayAvgDailyTokens = snap.SevenDayTokens / 7L;

            var orderedModels = model7d.Values.OrderByDescending(m => m.Tokens).ThenByDescending(m => m.ThreadCount).ToList();
            for (int i = 0; i < orderedModels.Count; i++)
            {
                orderedModels[i].SharePct = snap.SevenDayTokens > 0
                    ? (orderedModels[i].Tokens * 100.0 / snap.SevenDayTokens)
                    : 0.0;
            }
            snap.Models7d = orderedModels;

            snap.TopThreads7d = top7d
                .OrderByDescending(t => t.Tokens)
                .ThenByDescending(t => t.LastActiveLocal)
                .Take(5)
                .ToList();

            return snap;
        }

        private static string BuildStamp(string path)
        {
            try
            {
                if (!File.Exists(path)) return "0";
                var fi = new FileInfo(path);
                return fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
            }
            catch { return "err"; }
        }
    }
}
