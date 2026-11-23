using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace CodexInputUsage
{
    internal enum UsagePeriod { Today, Day, Week, Month, All, Custom }
    internal enum UsageGrain { Hour, Day, Week, Month }

    internal sealed class UsageResponse
    {
        internal long TimestampUtcMs;
        internal string TurnId, Model;
        internal RolloutSessionTracker.UsageBag Usage;
    }

    internal sealed class UsageTotals
    {
        internal long Tokens, UncachedInputTokens, CachedInputTokens, ReasoningTokens, OutputTokens, UnbrokenTokens;
        internal int Calls;
        internal readonly HashSet<string> SessionIds = new HashSet<string>();
        internal readonly HashSet<string> DetailedIds = new HashSet<string>();
        internal int Sessions { get { return SessionIds.Count; } }
        internal int DetailedThreads { get { return DetailedIds.Count; } }
        internal long DetailedTokens { get { return Tokens - UnbrokenTokens; } }
        internal double? CacheHitPct
        {
            get
            {
                long input = UncachedInputTokens + CachedInputTokens;
                return input > 0 ? CachedInputTokens * 100.0 / input : (double?)null;
            }
        }

        internal void Add(string id, long tokens, RolloutSessionTracker.UsageBag usage)
        {
            SessionIds.Add(id);
            Tokens += tokens;
            if (usage == null)
            {
                UnbrokenTokens += tokens;
                return;
            }
            DetailedIds.Add(id);
            Calls++;
            UncachedInputTokens += usage.Input - usage.Cached;
            CachedInputTokens += usage.Cached;
            ReasoningTokens += usage.Reasoning;
            OutputTokens += Math.Max(0, usage.Output - usage.Reasoning);
        }

        internal void Merge(UsageTotals source)
        {
            SessionIds.UnionWith(source.SessionIds);
            DetailedIds.UnionWith(source.DetailedIds);
            Tokens += source.Tokens;
            UncachedInputTokens += source.UncachedInputTokens;
            CachedInputTokens += source.CachedInputTokens;
            ReasoningTokens += source.ReasoningTokens;
            OutputTokens += source.OutputTokens;
            UnbrokenTokens += source.UnbrokenTokens;
            Calls += source.Calls;
        }
    }

    internal sealed class UsageTrendPoint
    {
        internal DateTime StartLocal, EndLocal;
        internal string AxisLabel, TooltipLabel;
        internal readonly UsageTotals Totals = new UsageTotals();
        internal readonly Dictionary<string, long> ModelTokens = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class TopThreadItem
    {
        internal string DisplayName, ModelName;
        internal long Tokens;
        internal DateTime LastActiveLocal;
    }

    internal sealed class HistoryStatsSnapshot
    {
        internal UsagePeriod Period;
        internal UsageGrain Grain;
        internal DateTime Start, End;
        internal long AllTimeTokens;
        internal int AllTimeThreads, ElapsedBuckets;
        internal readonly UsageTotals Total = new UsageTotals();
        internal readonly List<UsageTrendPoint> Points = new List<UsageTrendPoint>();
        internal KeyValuePair<string, UsageTotals>[] Models = new KeyValuePair<string, UsageTotals>[0];
        internal TopThreadItem[] TopThreads = new TopThreadItem[0];
        internal long Peak { get { return Points.Count == 0 ? 0 : Points.Max(p => p.Totals.Tokens); } }

        internal string PeriodLabel
        {
            get { return new[] { "今天", "近 24 小时", "近 7 天", "近 30 天", "全部", "自定义" }[(int)Period]; }
        }
    }

    internal sealed class UsageHistoryData
    {
        internal static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        internal readonly List<StateThreadRecord> Rows;
        internal readonly Dictionary<string, UsageResponse[]> Responses;

        internal UsageHistoryData(List<StateThreadRecord> rows, Dictionary<string, UsageResponse[]> responses)
        {
            Rows = rows;
            Responses = responses;
        }

        internal static DateTime FloorHour(DateTime date) { return date.AddTicks(-(date.Ticks % TimeSpan.TicksPerHour)); }
        internal static DateTime LocalTime(long millis) { return Epoch.AddMilliseconds(millis).ToLocalTime(); }

        internal HistoryStatsSnapshot Build(UsagePeriod period, DateTime now, DateTime start, DateTime end)
        {
            DateTime hour = FloorHour(now);
            var snap = new HistoryStatsSnapshot { Period = period, End = now.AddTicks(1), Grain = UsageGrain.Day };
            switch (period)
            {
                case UsagePeriod.Today:
                    snap.Start = now.Date;
                    snap.Grain = UsageGrain.Hour;
                    break;
                case UsagePeriod.Day:
                    snap.Start = now.AddHours(-24);
                    snap.Grain = UsageGrain.Hour;
                    break;
                case UsagePeriod.Month:
                    snap.Start = now.Date.AddDays(-29);
                    break;
                case UsagePeriod.All:
                    snap.Start = now.Date;
                    bool found = false;
                    foreach (var row in Rows)
                    {
                        UsageResponse[] records;
                        if (Responses.TryGetValue(row.Id, out records) && records.Length > 0)
                        {
                            snap.Start = Min(snap.Start, LocalTime(records.Min(r => r.TimestampUtcMs)).Date);
                            found = true;
                        }
                        else if (IsValidRecency(row.RecencyAtMs))
                        {
                            snap.Start = Min(snap.Start, LocalTime(row.RecencyAtMs).Date);
                            found = true;
                        }
                    }
                    if (!found) snap.Start = now.Date.AddDays(-6);
                    snap.Grain = ChooseGrain(snap.End - snap.Start, false);
                    break;
                case UsagePeriod.Custom:
                    snap.Start = FloorHour(Min(start, end));
                    DateTime lastHour = FloorHour(start > end ? start : end);
                    snap.End = lastHour.AddHours(1);
                    snap.Grain = ChooseGrain(lastHour - snap.Start, true);
                    break;
                default:
                    snap.Start = now.Date.AddDays(-6);
                    break;
            }

            DateTime bucket = snap.Grain == UsageGrain.Hour ? FloorHour(snap.Start) : snap.Start.Date;
            if (snap.Grain == UsageGrain.Month) bucket = bucket.AddDays(1 - bucket.Day);
            while (bucket < snap.End)
            {
                DateTime next = NextBucket(bucket, snap.Grain);
                snap.Points.Add(CreatePoint(bucket, next, snap.Grain));
                bucket = next;
            }
            snap.ElapsedBuckets = snap.Points.Count;
            if (period == UsagePeriod.Today && snap.Points.Count == 1)
                snap.Points.Add(CreatePoint(hour.AddHours(1), hour.AddHours(2), snap.Grain));

            var models = new Dictionary<string, UsageTotals>(StringComparer.OrdinalIgnoreCase);
            var top = new List<TopThreadItem>();
            foreach (var row in Rows)
            {
                UsageResponse[] records;
                if (!Responses.TryGetValue(row.Id, out records)) records = new UsageResponse[0];
                snap.AllTimeThreads++;
                snap.AllTimeTokens += records.Length > 0 ? records.Sum(record => record.Usage.Total) : Math.Max(0, row.TokensUsed);
                var threadModels = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                DateTime last = DateTime.MinValue;
                long tokens = 0;
                if (records.Length > 0)
                {
                    foreach (var record in records)
                        AddResponse(row.Id, LocalTime(record.TimestampUtcMs), record.Model ?? row.Model, record.Usage.Total,
                            record.Usage, snap, models, threadModels, ref tokens, ref last);
                }
                else if (IsValidRecency(row.RecencyAtMs))
                    AddResponse(row.Id, LocalTime(row.RecencyAtMs), row.Model, Math.Max(0, row.TokensUsed),
                        null, snap, models, threadModels, ref tokens, ref last);
                if (last != DateTime.MinValue)
                {
                    string title = !string.IsNullOrWhiteSpace(row.Name) ? row.Name : row.Title;
                    top.Add(new TopThreadItem
                    {
                        DisplayName = string.IsNullOrWhiteSpace(title) ? MetricFormatter.ShortThreadId(row.Id)
                            : title.Trim().Replace('\r', ' ').Replace('\n', ' '),
                        ModelName = threadModels.OrderByDescending(m => m.Value).ThenBy(m => m.Key).First().Key,
                        Tokens = tokens,
                        LastActiveLocal = last
                    });
                }
            }
            var sorted = models.OrderByDescending(m => m.Value.Tokens).ThenByDescending(m => m.Value.Sessions)
                .ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (sorted.Count > 5)
            {
                var other = new UsageTotals();
                foreach (var pair in sorted.Skip(5)) other.Merge(pair.Value);
                sorted = sorted.Take(5).ToList();
                sorted.Add(new KeyValuePair<string, UsageTotals>("其他模型", other));
            }
            snap.Models = sorted.ToArray();
            snap.TopThreads = top.OrderByDescending(t => t.Tokens).ThenByDescending(t => t.LastActiveLocal).Take(5).ToArray();
            return snap;
        }

        private static void AddResponse(string id, DateTime time, string model, long tokens, RolloutSessionTracker.UsageBag usage,
            HistoryStatsSnapshot snap, Dictionary<string, UsageTotals> models, Dictionary<string, long> threadModels,
            ref long threadTokens, ref DateTime last)
        {
            if (time < snap.Start || time >= snap.End) return;
            int left = 0, right = snap.Points.Count;
            while (left < right)
            {
                int mid = left + (right - left) / 2;
                if (time >= snap.Points[mid].EndLocal) left = mid + 1;
                else right = mid;
            }
            var point = snap.Points[left];
            point.Totals.Add(id, tokens, usage);
            snap.Total.Add(id, tokens, usage);
            string key = MetricFormatter.FormatModelName(model);
            if (key.Length == 0) key = "未知模型";
            long pointModelTokens;
            point.ModelTokens.TryGetValue(key, out pointModelTokens);
            point.ModelTokens[key] = pointModelTokens + tokens;
            UsageTotals modelTotal;
            if (!models.TryGetValue(key, out modelTotal))
                models.Add(key, modelTotal = new UsageTotals());
            modelTotal.Add(id, tokens, usage);
            long prior;
            threadModels.TryGetValue(key, out prior);
            threadModels[key] = prior + tokens;
            threadTokens += tokens;
            if (time > last) last = time;
        }

        private static bool IsValidRecency(long millis)
        {
            return millis > 0 && millis < (DateTime.MaxValue - Epoch).TotalMilliseconds;
        }

        private static DateTime Min(DateTime a, DateTime b) { return a < b ? a : b; }

        private static UsageGrain ChooseGrain(TimeSpan span, bool allowHours)
        {
            if (allowHours && span.TotalHours <= 48) return UsageGrain.Hour;
            if (span.TotalDays <= 90) return UsageGrain.Day;
            return span.TotalDays <= 365 ? UsageGrain.Week : UsageGrain.Month;
        }

        private static DateTime NextBucket(DateTime value, UsageGrain grain)
        {
            switch (grain)
            {
                case UsageGrain.Hour: return value.AddHours(1);
                case UsageGrain.Week: return value.AddDays(7);
                case UsageGrain.Month: return value.AddMonths(1);
                default: return value.AddDays(1);
            }
        }

        private static UsageTrendPoint CreatePoint(DateTime start, DateTime end, UsageGrain grain)
        {
            string axis = start.ToString(grain == UsageGrain.Hour ? "HH:mm" : grain == UsageGrain.Month ? "yy/MM" : "MM/dd");
            string tip = grain == UsageGrain.Hour ? start.ToString("MM/dd HH:mm") :
                grain == UsageGrain.Week ? start.ToString("MM/dd") + "–" + end.AddDays(-1).ToString("MM/dd") :
                grain == UsageGrain.Month ? start.ToString("yyyy/MM") : axis;
            return new UsageTrendPoint { StartLocal = start, EndLocal = end, AxisLabel = axis, TooltipLabel = tip };
        }
    }

    internal sealed class HistoryStatsReader
    {
        private sealed class RolloutCache
        {
            internal long Length;
            internal DateTime WriteTime;
            internal UsageResponse[] Records;
        }

        private readonly Dictionary<string, RolloutCache> _rollouts = new Dictionary<string, RolloutCache>(StringComparer.OrdinalIgnoreCase);

        internal UsageHistoryData Refresh()
        {
            var rows = CodexStateDb.TryReadRecentThreads(0);
            var data = new Dictionary<string, UsageResponse[]>(StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.RolloutPath) || !File.Exists(row.RolloutPath)) continue;
                string key = row.Id + "|" + row.RolloutPath;
                paths.Add(key);
                var file = new FileInfo(row.RolloutPath);
                RolloutCache cached;
                _rollouts.TryGetValue(key, out cached);
                try
                {
                    long length = file.Length;
                    DateTime writeTime = file.LastWriteTimeUtc;
                    if (cached == null || cached.Length != length || cached.WriteTime != writeTime)
                    {
                        var records = ReadRollout(row.RolloutPath, row.Id);
                        cached = new RolloutCache { Length = length, WriteTime = writeTime, Records = records };
                        _rollouts[key] = cached;
                    }
                }
                catch (IOException e) { Program.LogDiag("usage rollout read: " + e.GetType().Name); }
                catch (UnauthorizedAccessException e) { Program.LogDiag("usage rollout read: " + e.GetType().Name); }
                if (cached != null && cached.Records.Length > 0) data[row.Id] = cached.Records;
            }
            foreach (string key in _rollouts.Keys.Where(key => !paths.Contains(key)).ToArray())
                _rollouts.Remove(key);
            return new UsageHistoryData(rows, data);
        }

        internal static UsageResponse[] ReadRollout(string path, string threadId)
        {
            var result = new List<UsageResponse>();
            var models = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(file))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.IndexOf("\"token_usage_record\"", StringComparison.Ordinal) < 0 &&
                        line.IndexOf("\"turn_context\"", StringComparison.Ordinal) < 0) continue;
                    Dictionary<string, object> row;
                    try { row = json.DeserializeObject(line) as Dictionary<string, object>; }
                    catch (ArgumentException) { continue; }
                    catch (InvalidOperationException) { continue; }
                    string type = RolloutSessionTracker.GetStr(row, "type");
                    var payload = RolloutSessionTracker.GetDict(row, "payload");
                    string turn = RolloutSessionTracker.GetStr(payload, "turn_id");
                    if (type == "turn_context")
                    {
                        string model = RolloutSessionTracker.GetStr(payload, "model");
                        if (!string.IsNullOrWhiteSpace(turn) && !string.IsNullOrWhiteSpace(model)) models[turn] = model;
                        continue;
                    }
                    if (type != "token_usage_record") continue;
                    string recordThread = RolloutSessionTracker.GetStr(payload, "thread_id");
                    if (!string.IsNullOrEmpty(recordThread) && !string.Equals(recordThread, threadId, StringComparison.OrdinalIgnoreCase)) continue;
                    var time = RolloutSessionTracker.ParseTimestampMs(RolloutSessionTracker.GetStr(row, "timestamp"));
                    var usage = RolloutSessionTracker.ParseUsage(RolloutSessionTracker.GetDict(payload, "usage"));
                    string response = RolloutSessionTracker.GetStr(payload, "response_id");
                    if (!time.HasValue || usage == null || usage.Total < 0 ||
                        (!string.IsNullOrWhiteSpace(response) && !seen.Add(response))) continue;
                    result.Add(new UsageResponse { TimestampUtcMs = time.Value, TurnId = turn, Usage = usage });
                }
            }
            foreach (var response in result)
                if (!string.IsNullOrEmpty(response.TurnId)) models.TryGetValue(response.TurnId, out response.Model);
            return result.ToArray();
        }
    }
}
