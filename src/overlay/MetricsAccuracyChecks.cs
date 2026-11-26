using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexInputUsage
{
    internal static class MetricsAccuracyChecks
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static bool Run(StringBuilder report, string directory)
        {
            bool quota = CheckQuota(report);
            SessionMetricsSnapshot preview;
            bool timing = CheckTiming(report, out preview);
            bool context = CheckContext(report);
            preview.PrimaryUsedPct = 14;
            preview.PrimaryResetsAt = (long)(DateTime.UtcNow - Epoch).TotalSeconds + 900;
            preview.QuotaObservedAtUtc = DateTime.UtcNow.AddMinutes(-7);
            foreach (var scale in new[] { 1f, 1.5f })
            {
                string dpi = scale == 1f ? "96" : "144";
                using (var bmp = DetailPopoverForm.RenderPreviewBitmap(preview, ThemePalette.Light, PopoverTab.Session, scale))
                    bmp.Save(Path.Combine(directory, "preview-accuracy-quota-" + dpi + ".png"), ImageFormat.Png);
                using (var bmp = DetailPopoverForm.RenderPreviewBitmap(preview, ThemePalette.Light, PopoverTab.Turns, scale))
                    bmp.Save(Path.Combine(directory, "preview-accuracy-turns-" + dpi + ".png"), ImageFormat.Png);
            }
            return quota && timing && context;
        }

        private static bool CheckQuota(StringBuilder report)
        {
            var tracker = NewTracker();
            Event(tracker, "token_count", Start, "\"rate_limits\":{\"primary\":{\"used_percent\":23,\"resets_at\":1767230000}}");
            Event(tracker, "token_count", Start.AddSeconds(-1), "\"rate_limits\":{\"primary\":{\"used_percent\":11}}");
            var snap = tracker.BuildSnapshot();
            bool timestamp = snap.QuotaObservedAtUtc == Start && snap.PrimaryUsedPct == 23;
            AccountQuotaReader.ApplyToSnapshot(snap, Quota(Start.AddMinutes(-1), 40));
            AccountQuotaReader.ApplyToSnapshot(snap, new AccountQuotaSnapshot { PrimaryUsedPct = 55 });
            bool stale = snap.PrimaryUsedPct == 23 && snap.QuotaObservedAtUtc == Start;
            AccountQuotaReader.ApplyToSnapshot(snap, Quota(Start.AddMinutes(1), 28));
            bool fresh = snap.PrimaryUsedPct == 28 && snap.QuotaObservedAtUtc == Start.AddMinutes(1);
            bool ages = MetricFormatter.FormatSnapshotAge(Start, Start.AddSeconds(59)) == "刚刚"
                && MetricFormatter.FormatSnapshotAge(Start, Start.AddMinutes(1)) == "1m前"
                && MetricFormatter.FormatSnapshotAge(Start, Start.AddHours(1)) == "1h前"
                && MetricFormatter.FormatSnapshotAge(Start, Start.AddDays(2)) == "2d前"
                && MetricFormatter.FormatSnapshotAge(null, Start) == null;

            var reader = new AccountQuotaReader();
            var top = Tuple.Create("top", 120L, Start);
            var second = Tuple.Create("second", 90L, Start.AddMinutes(-1));
            int reads = 0;
            AccountQuotaSnapshot original = Quota(Start.AddMinutes(-1), 9), update = Quota(Start, 12);
            AccountQuotaSnapshot current = null;
            Func<string, AccountQuotaSnapshot> read = path =>
            {
                reads++;
                return path == top.Item1 ? current : original;
            };
            bool cache = reader.ReadCandidates(new[] { top, second }, read) == original && reads == 2;
            cache &= reader.ReadCandidates(new[] { top, second }, read) == original && reads == 2;
            current = update;
            cache &= reader.ReadCandidates(new[] { Tuple.Create(top.Item1, 130L, Start), second }, read) == update && reads == 3;
            report.AppendLine(string.Format("quota_snapshot_tests timestamp={0} stale={1} fresh={2} ages={3} cache={4}",
                timestamp, stale, fresh, ages, cache));
            return timestamp && stale && fresh && ages && cache;
        }

        private static AccountQuotaSnapshot Quota(DateTime observed, double used)
        {
            return new AccountQuotaSnapshot { ObservedAtUtc = observed, PrimaryUsedPct = used };
        }

        private static bool CheckTiming(StringBuilder report, out SessionMetricsSnapshot preview)
        {
            var tracker = NewTracker();
            Event(tracker, "task_started", Start, "\"turn_id\":\"first\"");
            Item(tracker, "first", "long", "CommandExecution", -2000, 900000);
            Item(tracker, "first", "parallel", "CommandExecution", 600000, 920000);
            Item(tracker, "first", "overlap", "Reasoning", 850000, 860000);
            Usage(tracker, "first", Start.AddSeconds(925), 300);
            Event(tracker, "task_complete", Start.AddSeconds(940),
                "\"turn_id\":\"first\",\"duration_ms\":940000,\"time_to_first_token_ms\":450");

            tracker.ConsumeInterestingLine(Response(Message("developer", "Synthetic system instructions.")));
            tracker.ConsumeInterestingLine(Response(Message("user", "Synthetic long task.")));
            preview = tracker.BuildSnapshot();
            var first = preview.LatestTurn;
            bool intervals = first != null && first.ToolExclusiveMs == 910000 && first.ModelActiveMs == 30000
                && first.WallDurationMs == first.ToolExclusiveMs + first.ModelActiveMs
                && first.Tps == 10 && first.TtftMs == 450;

            Event(tracker, "task_started", Start.AddSeconds(950), "\"turn_id\":\"second\"");
            Usage(tracker, "second", Start.AddSeconds(952), 80);
            bool streaming = tracker.BuildSnapshot().IsStreaming && tracker.BuildSnapshot().DisplayTps == 40;
            Event(tracker, "task_complete", Start.AddSeconds(952), "\"turn_id\":\"second\",\"duration_ms\":2000");
            var completed = tracker.BuildSnapshot();
            bool weighted = completed.LatestTurn.Tps == 40
                && Math.Abs(completed.SessionAvgTps.GetValueOrDefault() - 380000.0 / 32000) < 0.0001;

            Event(tracker, "task_started", Start.AddSeconds(960), "\"turn_id\":\"aborted\"");
            Usage(tracker, "aborted", Start.AddSeconds(961), 90);
            Event(tracker, "turn_aborted", Start.AddSeconds(961), "\"turn_id\":\"aborted\"");
            Event(tracker, "task_complete", Start.AddSeconds(962), "\"turn_id\":\"aborted\",\"duration_ms\":2000");
            var aborted = tracker.BuildSnapshot();
            bool ended = !aborted.IsStreaming && !aborted.LatestTurn.Complete && aborted.DisplayTps == 40;

            Event(tracker, "task_started", Start.AddSeconds(970), "\"turn_id\":\"superseded\"");
            Usage(tracker, "superseded", Start.AddSeconds(971), 90);
            Event(tracker, "task_started", Start.AddSeconds(972), "\"turn_id\":\"new\"");
            Event(tracker, "task_complete", Start.AddSeconds(973), "\"turn_id\":\"superseded\",\"duration_ms\":3000");
            Event(tracker, "task_failed", Start.AddSeconds(974), "\"turn_id\":\"superseded\"");
            var next = tracker.BuildSnapshot();
            bool swapped = next.IsStreaming && next.TpsIsHeld && next.DisplayTps == 40 && !next.LatestTurn.Complete;
            Event(tracker, "task_cancelled", Start.AddSeconds(975), "\"turn_id\":\"new\"");
            ended &= !tracker.BuildSnapshot().IsStreaming;
            foreach (string kind in new[] { "task_aborted", "task_failed" })
            {
                var stopped = NewTracker();
                Event(stopped, "task_started", Start, "\"turn_id\":\"test\"");
                Event(stopped, kind, Start.AddSeconds(1), "\"turn_id\":\"test\"");
                ended &= !stopped.BuildSnapshot().IsStreaming;
            }

            report.AppendLine(string.Format("tps_timing_tests intervals={0} streaming={1} weighted={2} interrupted={3} turnSwitch={4}",
                intervals, streaming, weighted, ended, swapped));
            return intervals && streaming && weighted && ended && swapped;
        }

        private static void Item(RolloutSessionTracker tracker, string turn, string id, string kind, int start, int end)
        {
            long time = (long)(Start - Epoch).TotalMilliseconds;
            Event(tracker, "item_completed", Start.AddMilliseconds(end),
                string.Format(CultureInfo.InvariantCulture,
                    "\"turn_id\":\"{0}\",\"item\":{{\"type\":\"{1}\",\"id\":\"{2}\"}},\"started_at_ms\":{3},\"completed_at_ms\":{4}",
                    turn, kind, id, time + start, time + end));
        }

        private static void Usage(RolloutSessionTracker tracker, string turn, DateTime time, int output)
        {
            tracker.ConsumeInterestingLine(string.Format(CultureInfo.InvariantCulture,
                "{{\"timestamp\":\"{0:o}\",\"type\":\"token_usage_record\",\"payload\":{{\"turn_id\":\"{1}\",\"usage\":{{\"input_tokens\":2000,\"output_tokens\":{2}}}}}}}",
                time, turn, output));
        }

        private static void Event(RolloutSessionTracker tracker, string kind, DateTime time, string fields)
        {
            tracker.ConsumeInterestingLine(string.Format(CultureInfo.InvariantCulture,
                "{{\"timestamp\":\"{0:o}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"{1}\",{2}}}}}", time, kind, fields));
        }

        private static bool CheckContext(StringBuilder report)
        {
            var samples = new Dictionary<string, string>
            {
                { "mcpTools", Message("developer", "<tools>mcp__synthetic</tools>") },
                { "skills", Message("developer", "<skills_instructions>Synthetic skill</skills_instructions>") },
                { "userRules", Message("developer", "Rules in AGENTS.md for a synthetic workspace") },
                { "systemPrompt", Message("developer", "Synthetic system instructions.") },
                { "subagentMsg", Message("user", "<subagent_notification>Synthetic result</subagent_notification>") },
                { "toolResults", "{\"type\":\"function_call_output\",\"output\":\"Synthetic result\"}" }
            };

            bool classifications = samples.All(sample => SingleChannel(Response(sample.Value), sample.Key)
                && SingleChannel("{\"type\": \"response_item\",\"payload\":" + sample.Value + "}", sample.Key));
            string late = "{\"content\":\"" + new string('x', 500) + "<skills_instructions>\",\"role\":\"developer\",\"type\":\"message\"}";
            bool fallback = SingleChannel(Response(late), "skills");

            var tracker = NewTracker();
            tracker.ConsumeInterestingLine(Response(Message("user", "Old content")));
            tracker.ConsumeInterestingLine("{\"type\":\"compacted\",\"payload\":{\"message\":\"Synthetic summary\",\"replacement_history\":["
                + string.Join(",", samples.Values) + "]}}");
            Event(tracker, "token_count", Start, "\"info\":{\"last_token_usage\":{\"input_tokens\":2000,\"output_tokens\":0}}");
            var channels = tracker.BuildSnapshot().ContextChannels.ToDictionary(item => item.Key, item => item.Tokens);
            bool rebuilt = channels["userInput"] == 0 && channels["checkpoint"] > 0
                && samples.Keys.All(key => channels[key] > 0);
            bool total = channels.Values.Sum() == 2000 && channels.Count == 10;

            string plain = "{\"type\":\"reasoning\",\"summary\":[{\"text\":\"keep this summary\"}]}";
            string encrypted = plain.TrimEnd('}') + ",\"encrypted_content\":\"" + new string('A', 8000) + "\"}";
            string image = "before data:image/png;base64,";
            long summaryTokens = ChannelTokens(Response(plain), "assistant");
            long plainImageTokens = ChannelTokens(Response(Message("user", image + " after")), "userInput");
            string imagePayload = Message("user", image + new string('A', 8000) + " after");
            bool noise = summaryTokens > 0 && summaryTokens == ChannelTokens(Response(encrypted), "assistant")
                && plainImageTokens > 0 && plainImageTokens == ChannelTokens(Response(imagePayload), "userInput")
                && plainImageTokens == ChannelTokens("{\"type\": \"response_item\",\"payload\":" + imagePayload + "}", "userInput");
            report.AppendLine(string.Format("context_channel_tests classifications={0} fallback={1} compacted={2} noise={3} total={4}",
                classifications, fallback, rebuilt, noise, total));
            return classifications && fallback && rebuilt && noise && total;
        }

        private static bool SingleChannel(string line, string channel)
        {
            var tracker = NewTracker();
            tracker.ConsumeInterestingLine(line);
            Event(tracker, "token_count", Start, "\"info\":{\"last_token_usage\":{\"input_tokens\":2000,\"output_tokens\":0}}");
            return tracker.BuildSnapshot().ContextChannels.Any(item => item.Key == channel && item.Tokens == 2000);
        }

        private static long ChannelTokens(string line, string channel)
        {
            var tracker = NewTracker();
            tracker.ConsumeInterestingLine(line);
            tracker.ConsumeInterestingLine(Response(Message("developer", new string('s', 200))));
            Event(tracker, "token_count", Start, "\"info\":{\"last_token_usage\":{\"input_tokens\":2000,\"output_tokens\":0}}");
            return tracker.BuildSnapshot().ContextChannels.Single(item => item.Key == channel).Tokens;
        }

        private static string Message(string role, string text)
        {
            return "{\"type\":\"message\",\"role\":\"" + role + "\",\"content\":\"" + text + "\"}";
        }

        private static string Response(string payload)
        {
            return "{\"type\":\"response_item\",\"payload\":" + payload + "}";
        }

        private static RolloutSessionTracker NewTracker()
        {
            var tracker = new RolloutSessionTracker();
            tracker.Reset("synthetic", "synthetic", null);
            return tracker;
        }
    }
}
