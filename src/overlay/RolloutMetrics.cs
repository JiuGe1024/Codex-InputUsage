using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexInputUsage
{
    internal sealed class TurnSummary
    {
        internal int Index;
        internal string TurnId;
        internal bool Complete;
        internal DateTime? CompletedAtLocal;
        internal long InputTokens;
        internal long CachedInputTokens;
        internal long OutputTokens;
        internal long ReasoningOutputTokens;
        internal long TotalTokens;
        internal long ModelActiveMs;
        internal long? TtftMs;
        internal long? WallDurationMs;
        internal double? Tps;
        internal double? CacheHitPct;
    }

    internal sealed class ContextChannelItem
    {
        internal string Key;
        internal string Label;
        internal long Tokens;
    }

    internal sealed class AccountQuotaSnapshot
    {
        internal double? PrimaryUsedPct;
        internal long? PrimaryResetsAt;
        internal double? SecondaryUsedPct;
        internal long? SecondaryResetsAt;
        internal bool HasCredits;
        internal bool UnlimitedCredits;
        internal string CreditsBalance;
        internal DateTime ObservedAtUtc;
        internal string SourcePath;
    }

    internal sealed class SessionMetricsSnapshot
    {
        internal string ThreadId;
        internal string BindingSource; // "IPC+标题" / "IPC+正文" / "IPC" / "待确认" / "IPC 断线"
        internal string RolloutPath;
        internal string ModelName;
        internal string ReasoningEffort;
        internal bool HasUsage;
        internal bool IsStreaming;

        // Session cumulative
        internal long InputTokens;
        internal long CachedInputTokens;
        internal long OutputTokens;
        internal long ReasoningOutputTokens;
        internal long TotalTokens;
        internal double? SessionCacheHitPct;
        internal double? SessionAvgTps;

        // Context window
        internal long ContextUsedTokens;
        internal long ContextCachedTokens;
        internal long ContextUncachedInputTokens;
        internal long ContextLastOutputTokens;
        internal long ContextWindowTokens;
        internal long ContextRemainingTokens;
        internal double? ContextPct;
        internal List<ContextChannelItem> ContextChannels = new List<ContextChannelItem>();

        // Rate limits (account-level real-time preferred; falls back to session rollout)
        internal double? PrimaryUsedPct;
        internal long? PrimaryResetsAt;
        internal double? SecondaryUsedPct;
        internal long? SecondaryResetsAt;
        internal bool HasCredits;
        internal bool UnlimitedCredits;
        internal string CreditsBalance;
        internal bool QuotaIsAccountLive;

        // Latest turn highlights
        internal double? DisplayTps;
        internal bool TpsIsHeld;
        internal long? LatestModelActiveMs;
        internal long? LatestTtftMs;
        internal TurnSummary LatestTurn;

        // Per-turn history (chronological)
        internal List<TurnSummary> Turns = new List<TurnSummary>();
    }

    internal static class MetricFormatter
    {
        internal static string CompactTokens(long value)
        {
            long mag = Math.Abs(value);
            if (mag >= 1000000000L) return (value / 1000000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "B";
            if (mag >= 1000000L) return (value / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (mag >= 1000L) return (value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        internal static string CompactTokensLower(long value)
        {
            long mag = Math.Abs(value);
            if (mag >= 1000000000L) return (value / 1000000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "B";
            if (mag >= 1000000L) return (value / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (mag >= 1000L) return (value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        internal static string ExactTokens(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        internal static string FormatTps(double? tps, bool isHeld)
        {
            if (!tps.HasValue || double.IsNaN(tps.Value) || double.IsInfinity(tps.Value) || tps.Value <= 0)
                return "--";
            string num = Math.Round(tps.Value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
            return isHeld ? "~" + num : num;
        }

        internal static string FormatCacheCompact(double? pct)
        {
            if (!pct.HasValue || double.IsNaN(pct.Value) || double.IsInfinity(pct.Value)) return "--";
            return Math.Round(pct.Value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        internal static string FormatCachePrecise(double? pct)
        {
            if (!pct.HasValue || double.IsNaN(pct.Value) || double.IsInfinity(pct.Value)) return "--";
            return pct.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        internal static string FormatCacheTwoDecimals(double? pct)
        {
            if (!pct.HasValue || double.IsNaN(pct.Value) || double.IsInfinity(pct.Value)) return "--";
            return pct.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        internal static string FormatSeconds(long? ms)
        {
            if (!ms.HasValue || ms.Value < 0) return "--";
            double s = ms.Value / 1000.0;
            if (s < 1.0) return s.ToString("0.00", CultureInfo.InvariantCulture) + "s";
            if (s < 100.0) return s.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            return Math.Round(s).ToString("0", CultureInfo.InvariantCulture) + "s";
        }

        internal static string ShortThreadId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "--";
            if (id.Length <= 12) return id;
            return id.Substring(0, 6) + "…" + id.Substring(id.Length - 4);
        }

        // 4-tier semantic classification matching Antigravity usage-monitor
        internal static MetricTier ClassifySpeed(double? tps)
        {
            if (!tps.HasValue || double.IsNaN(tps.Value) || tps.Value <= 0) return MetricTier.Muted;
            if (tps.Value >= 60.0) return MetricTier.Good;
            if (tps.Value >= 35.0) return MetricTier.Info;
            if (tps.Value >= 20.0) return MetricTier.Warn;
            return MetricTier.Danger;
        }

        internal static MetricTier ClassifyCache(double? pct)
        {
            if (!pct.HasValue || double.IsNaN(pct.Value)) return MetricTier.Muted;
            if (pct.Value >= 85.0) return MetricTier.Good;
            if (pct.Value >= 65.0) return MetricTier.Info;
            if (pct.Value >= 30.0) return MetricTier.Warn;
            return MetricTier.Danger;
        }

        internal static MetricTier ClassifyTtft(long? ttftMs)
        {
            if (!ttftMs.HasValue || ttftMs.Value < 0) return MetricTier.Muted;
            double sec = ttftMs.Value / 1000.0;
            if (sec >= 30.0) return MetricTier.Danger;
            if (sec >= 18.0) return MetricTier.Warn;
            return MetricTier.Good;
        }

        internal static MetricTier ClassifyQuotaRemaining(double remainingPct)
        {
            if (remainingPct >= 50.0) return MetricTier.Good;
            if (remainingPct >= 30.0) return MetricTier.Info;
            if (remainingPct >= 15.0) return MetricTier.Warn;
            return MetricTier.Danger;
        }

        internal static string FormatResetCountdown(long? resetsAtUnix, bool isWeekly)
        {
            if (!resetsAtUnix.HasValue || resetsAtUnix.Value <= 0) return string.Empty;
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime resetUtc = epoch.AddSeconds(resetsAtUnix.Value);
            TimeSpan diff = resetUtc - DateTime.UtcNow;
            if (diff.TotalSeconds <= 0) return "已重置";
            if (!isWeekly || diff.TotalHours < 24.0)
            {
                int totalMins = Math.Max(1, (int)Math.Ceiling(diff.TotalMinutes));
                int h = totalMins / 60;
                int m = totalMins % 60;
                if (h <= 0) return string.Format("{0}m 后重置", m);
                if (m == 0) return string.Format("{0}h 后重置", h);
                return string.Format("{0}h {1}m 后重置", h, m);
            }
            DateTime local = resetUtc.ToLocalTime();
            string[] wk = new string[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            return string.Format("{0} {1:HH:mm} 重置", wk[(int)local.DayOfWeek], local);
        }

        internal static string FormatModelBadge(string model, string effort)
        {
            if (string.IsNullOrWhiteSpace(model)) return string.Empty;
            string m = model.Trim();
            if (string.IsNullOrWhiteSpace(effort)) return m;
            string e = effort.Trim().ToLowerInvariant();
            string effLabel = e == "high" ? "高" : (e == "medium" ? "中" : (e == "low" ? "低" : effort.Trim()));
            return m + " · " + effLabel;
        }

        internal static string[] BuildStripVariants(SessionMetricsSnapshot snap)
        {
            string tps = snap != null ? FormatTps(snap.DisplayTps, snap.TpsIsHeld) : "--";
            bool hasTurn = snap != null && snap.LatestTurn != null && snap.LatestTurn.TotalTokens > 0;
            string turn = hasTurn ? CompactTokens(snap.LatestTurn.TotalTokens) : "--";
            string turnCache = hasTurn ? FormatCacheCompact(snap.LatestTurn.CacheHitPct) : "--";
            string total = (snap != null && snap.HasUsage) ? CompactTokens(snap.TotalTokens) : "--";
            string cache = (snap != null && snap.HasUsage) ? FormatCacheCompact(snap.SessionCacheHitPct) : "--";

            return new string[]
            {
                // Level 0: Full 5-metric strip (TPS + latest turn + turn cache + session total + session cache)
                string.Format("\u26A1 {0} tok/s \u00B7 \u672C\u8F6E {1} \u7F13{2} \u00B7 \u7D2F\u8BA1 {3} \u7F13{4}", tps, turn, turnCache, total, cache),
                // Level 1: Compact 5-metric form
                string.Format("\u26A1 {0}/s \u00B7 \u672C\u8F6E {1} ({2}) \u00B7 \u7D2F\u8BA1 {3} ({4})", tps, turn, turnCache, total, cache),
                // Level 2: Narrow 5-metric form
                string.Format("\u26A1 {0}/s \u00B7 \u8F6E {1}/{2} \u00B7 \u7D2F {3}/{4}", tps, turn, turnCache, total, cache),
                // Level 3: Drop TPS, keep turn + turn cache + cumulative + cumulative cache
                string.Format("\u672C\u8F6E {0} \u7F13{1} \u00B7 \u7D2F\u8BA1 {2} \u7F13{3}", turn, turnCache, total, cache),
                // Level 4: Very narrow
                string.Format("{0}/{1} \u00B7 {2}/{3}", turn, turnCache, total, cache),
                // Level 5: Extreme narrow fallback
                string.Format("{0} \u00B7 {1}", total, cache)
            };
        }
    }

    internal sealed class RolloutSessionTracker
    {
        private const long MaxSingleItemMs = 600000L;
        private const double MaxPlausibleTps = 100000.0;

        private sealed class Interval
        {
            internal long Start;
            internal long End;
        }

        private sealed class UsageBag
        {
            internal long Input;
            internal long Cached;
            internal long Output;
            internal long Reasoning;
            internal long Total;
        }

        private sealed class MutableTurn
        {
            internal int Order;
            internal string TurnId;
            internal bool Complete;
            internal DateTime? CompletedAtLocal;
            internal long? StartedAtMs;
            internal long? CompletedAtMs;
            internal long? LastEventAtMs;
            internal long? TtftMs;
            internal long? WallDurationMs;
            internal Dictionary<string, UsageBag> Responses = new Dictionary<string, UsageBag>(StringComparer.Ordinal);
            internal Dictionary<string, Interval> ModelItems = new Dictionary<string, Interval>(StringComparer.Ordinal);
            internal Dictionary<string, Interval> ToolItems = new Dictionary<string, Interval>(StringComparer.Ordinal);
        }

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private readonly Dictionary<string, MutableTurn> _turnsById = new Dictionary<string, MutableTurn>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MutableTurn> _turnsOrdered = new List<MutableTurn>();
        private readonly Dictionary<string, double> _ctxWeights = new Dictionary<string, double>(StringComparer.Ordinal);

        private static readonly string[] ChannelKeys = new string[]
        {
            "assistant", "toolResults", "systemPrompt", "systemTools", "userInput",
            "skills", "userRules", "mcpTools", "subagentMsg", "checkpoint"
        };

        private static readonly Dictionary<string, string> ChannelLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "assistant", "模型回复" },
            { "toolResults", "工具结果" },
            { "systemPrompt", "系统提示词" },
            { "systemTools", "系统工具" },
            { "userInput", "用户消息" },
            { "skills", "Skill" },
            { "userRules", "用户规则" },
            { "mcpTools", "MCP 工具" },
            { "subagentMsg", "子代理通知" },
            { "checkpoint", "压缩摘要" }
        };

        private bool _hasWorldStateStatic;
        private double _wsUserRulesWeight;
        private double _wsSkillsWeight;
        private double _wsSystemToolsWeight;
        private double _baseInstructionsWeight;

        private string _threadId;
        private string _bindingSource = "等待";
        private string _rolloutPath;
        private long _offset;
        private long _lastFileLength;
        private DateTime _lastWriteUtc;
        private bool _modernUsageSeen;
        private bool _isStreaming;
        private string _activeTurnId;

        private long _cumInput;
        private long _cumCached;
        private long _cumOutput;
        private long _cumReasoning;
        private long _cumTotal;
        private bool _hasUsage;

        private long _contextUsed;
        private long _contextCached;
        private long _contextLastOutput;
        private long _contextWindow;

        private double? _primaryUsedPct;
        private long? _primaryResetsAt;
        private double? _secondaryUsedPct;
        private long? _secondaryResetsAt;

        private double? _lastCompletedTps;
        private long? _lastCompletedModelMs;
        private long? _lastCompletedTtftMs;

        internal string CurrentThreadId { get { return _threadId; } }

        internal void Reset(string threadId, string bindingSource, string rolloutPath)
        {
            _threadId = threadId;
            _bindingSource = string.IsNullOrWhiteSpace(bindingSource) ? "等待" : bindingSource;
            _rolloutPath = rolloutPath;
            _offset = 0;
            _lastFileLength = -1;
            _lastWriteUtc = DateTime.MinValue;
            _modernUsageSeen = false;
            _isStreaming = false;
            _activeTurnId = null;
            _turnsById.Clear();
            _turnsOrdered.Clear();
            _ctxWeights.Clear();
            for (int i = 0; i < ChannelKeys.Length; i++) _ctxWeights[ChannelKeys[i]] = 0.0;
            _hasWorldStateStatic = false;
            _wsUserRulesWeight = 0.0;
            _wsSkillsWeight = 0.0;
            _wsSystemToolsWeight = 0.0;
            _baseInstructionsWeight = 0.0;
            _cumInput = 0;
            _cumCached = 0;
            _cumOutput = 0;
            _cumReasoning = 0;
            _cumTotal = 0;
            _hasUsage = false;
            _contextUsed = 0;
            _contextCached = 0;
            _contextLastOutput = 0;
            _contextWindow = 0;
            _primaryUsedPct = null;
            _primaryResetsAt = null;
            _secondaryUsedPct = null;
            _secondaryResetsAt = null;
            _lastCompletedTps = null;
            _lastCompletedModelMs = null;
            _lastCompletedTtftMs = null;
        }

        internal bool UpdateFromDisk(string threadId, string bindingSource, string rolloutPath)
        {
            bool changed = false;
            if (!string.Equals(_threadId, threadId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_rolloutPath, rolloutPath, StringComparison.OrdinalIgnoreCase))
            {
                Reset(threadId, bindingSource, rolloutPath);
                changed = true;
            }
            else if (!string.Equals(_bindingSource, bindingSource, StringComparison.Ordinal))
            {
                _bindingSource = bindingSource;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_rolloutPath) || !File.Exists(_rolloutPath))
                return changed;

            try
            {
                var fi = new FileInfo(_rolloutPath);
                long len = fi.Length;
                DateTime writeUtc = fi.LastWriteTimeUtc;
                if (len == _lastFileLength && writeUtc == _lastWriteUtc && _offset > 0)
                    return changed;

                if (len < _offset)
                {
                    Reset(threadId, bindingSource, rolloutPath);
                    changed = true;
                }

                using (var fs = new FileStream(_rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(_offset, SeekOrigin.Begin);
                    long bytesToRead = fs.Length - _offset;
                    if (bytesToRead <= 0)
                    {
                        _lastFileLength = len;
                        _lastWriteUtc = writeUtc;
                        return changed;
                    }

                    // Stream line-by-line keeping exact byte offset of the last complete '\n'
                    byte[] chunk = new byte[65536];
                    var lineBuf = new MemoryStream(4096);
                    long consumedOffset = _offset;
                    int read;
                    while ((read = fs.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        int segStart = 0;
                        for (int i = 0; i < read; i++)
                        {
                            if (chunk[i] == (byte)'\n')
                            {
                                int segLen = i - segStart;
                                if (segLen > 0) lineBuf.Write(chunk, segStart, segLen);
                                long lineBytes = lineBuf.Length;
                                if (lineBytes > 0)
                                {
                                    string line = Encoding.UTF8.GetString(lineBuf.GetBuffer(), 0, (int)lineBytes).Trim();
                                    if (line.Length > 0 && MightBeInteresting(line))
                                    {
                                        if (ConsumeLine(line)) changed = true;
                                    }
                                }
                                lineBuf.SetLength(0);
                                consumedOffset += (segLen + 1);
                                segStart = i + 1;
                            }
                        }
                        if (segStart < read)
                        {
                            lineBuf.Write(chunk, segStart, read - segStart);
                        }
                    }

                    // If trailing fragment without newline is a complete JSON object, parse it too
                    if (lineBuf.Length > 2 && lineBuf.Length < 2 * 1024 * 1024)
                    {
                        string tail = Encoding.UTF8.GetString(lineBuf.GetBuffer(), 0, (int)lineBuf.Length).Trim();
                        if (tail.Length > 2 && tail[0] == '{' && tail[tail.Length - 1] == '}')
                        {
                            if (MightBeInteresting(tail))
                            {
                                if (ConsumeLine(tail)) changed = true;
                            }
                            consumedOffset += lineBuf.Length;
                        }
                    }

                    _offset = consumedOffset;
                    _lastFileLength = len;
                    _lastWriteUtc = writeUtc;
                }
            }
            catch { }

            return changed;
        }

        private static bool MightBeInteresting(string line)
        {
            return line.IndexOf("\"token_usage_record\"", StringComparison.Ordinal) >= 0
                || line.IndexOf("\"token_count\"", StringComparison.Ordinal) >= 0
                || line.IndexOf("\"task_started\"", StringComparison.Ordinal) >= 0
                || line.IndexOf("\"task_complete\"", StringComparison.Ordinal) >= 0
                || line.IndexOf("\"item_completed\"", StringComparison.Ordinal) >= 0
                || line.IndexOf("\"response_item\"", StringComparison.Ordinal) >= 0
                || line.IndexOf("\"world_state\"", StringComparison.Ordinal) >= 0
                || line.IndexOf("\"compacted\"", StringComparison.Ordinal) >= 0
                || line.IndexOf("\"session_meta\"", StringComparison.Ordinal) >= 0;
        }

        private bool ConsumeLine(string line)
        {
            // Fast path for high-volume response_item lines (avoid JSON deserializing 100KB tool outputs!)
            if (line.IndexOf("\"type\":\"response_item\"", StringComparison.Ordinal) >= 0
                && line.IndexOf("\"additional_tools\"", StringComparison.Ordinal) < 0)
            {
                return ConsumeFastResponseItem(line);
            }

            Dictionary<string, object> row;
            try { row = _json.DeserializeObject(line) as Dictionary<string, object>; }
            catch { return false; }
            if (row == null) return false;

            string rowType = GetStr(row, "type");
            var payload = GetDict(row, "payload");
            if (payload == null) return false;

            if (rowType == "session_meta")
            {
                var bi = GetDict(payload, "base_instructions");
                string biText = GetStr(bi, "text");
                if (!string.IsNullOrEmpty(biText))
                    _baseInstructionsWeight = EstimateTokensFast(biText);
                return false;
            }

            if (rowType == "world_state")
            {
                ConsumeWorldState(payload);
                return true;
            }

            if (rowType == "compacted")
            {
                ConsumeCompacted(payload);
                return true;
            }

            if (rowType == "response_item")
            {
                ConsumeParsedResponseItem(payload);
                return true;
            }

            if (rowType == "token_usage_record")
            {
                string recThread = GetStr(payload, "thread_id");
                if (!string.IsNullOrWhiteSpace(_threadId) && !string.IsNullOrWhiteSpace(recThread)
                    && !string.Equals(recThread, _threadId, StringComparison.OrdinalIgnoreCase))
                    return false;

                string turnId = GetStr(payload, "turn_id");
                string respId = GetStr(payload, "response_id");
                var own = ParseUsage(GetDict(payload, "usage"));
                var threadCum = ParseUsage(GetDict(payload, "thread_token_usage"));

                if (!string.IsNullOrWhiteSpace(turnId) && own != null)
                {
                    var turn = GetOrCreateTurn(turnId);
                    string key = !string.IsNullOrWhiteSpace(respId)
                        ? respId
                        : string.Format("{0}:{1}:{2}:{3}", own.Input, own.Cached, own.Output, own.Total);
                    if (!turn.Responses.ContainsKey(key))
                    {
                        turn.Responses[key] = own;
                    }
                    long? evMs = ParseTimestampMs(GetStr(row, "timestamp"));
                    if (evMs.HasValue && (!turn.LastEventAtMs.HasValue || evMs.Value > turn.LastEventAtMs.Value))
                        turn.LastEventAtMs = evMs.Value;
                    _contextUsed = own.Input;
                    _contextCached = own.Cached;
                    _contextLastOutput = own.Output;
                    _modernUsageSeen = true;
                }

                if (threadCum != null)
                {
                    ApplyCumulative(threadCum);
                    _modernUsageSeen = true;
                }
                return true;
            }

            if (rowType != "event_msg") return false;
            string pType = GetStr(payload, "type");

            if (pType == "token_count")
            {
                var info = GetDict(payload, "info");
                if (info != null)
                {
                    long? cw = GetLong(info, "model_context_window");
                    if (cw.HasValue && cw.Value > 0) _contextWindow = cw.Value;

                    var lastUsage = ParseUsage(GetDict(info, "last_token_usage"));
                    if (lastUsage != null && lastUsage.Input > 0)
                    {
                        _contextUsed = lastUsage.Input;
                        _contextCached = lastUsage.Cached;
                        _contextLastOutput = lastUsage.Output;
                    }

                    if (!_modernUsageSeen)
                    {
                        var totUsage = ParseUsage(GetDict(info, "total_token_usage"));
                        if (totUsage != null) ApplyCumulative(totUsage);
                    }
                }

                var rateLimits = GetDict(payload, "rate_limits");
                if (rateLimits != null)
                {
                    var primary = GetDict(rateLimits, "primary");
                    if (primary != null)
                    {
                        double? used = GetDouble(primary, "used_percent");
                        if (used.HasValue) _primaryUsedPct = used.Value;
                        long? resets = GetLong(primary, "resets_at");
                        if (resets.HasValue) _primaryResetsAt = resets.Value;
                    }
                    var secondary = GetDict(rateLimits, "secondary");
                    if (secondary != null)
                    {
                        double? used = GetDouble(secondary, "used_percent");
                        if (used.HasValue) _secondaryUsedPct = used.Value;
                        long? resets = GetLong(secondary, "resets_at");
                        if (resets.HasValue) _secondaryResetsAt = resets.Value;
                    }
                }
                return true;
            }

            if (pType == "task_started")
            {
                string turnId = GetStr(payload, "turn_id") ?? GetStr(GetDict(payload, "turn_attribution"), "turn_id");
                if (!string.IsNullOrWhiteSpace(turnId))
                {
                    var turn = GetOrCreateTurn(turnId);
                    long? tsMs = ParseTimestampMs(GetStr(row, "timestamp"));
                    if (tsMs.HasValue && !turn.StartedAtMs.HasValue) turn.StartedAtMs = tsMs.Value;
                    _activeTurnId = turnId;
                    _isStreaming = true;
                }
                return true;
            }

            if (pType == "item_completed")
            {
                string turnId = GetStr(payload, "turn_id") ?? GetStr(GetDict(payload, "turn_attribution"), "turn_id");
                var item = GetDict(payload, "item");
                if (string.IsNullOrWhiteSpace(turnId) || item == null) return false;

                string itemType = GetStr(item, "type");
                string itemKind = GetStr(item, "kind");
                bool isModel = IsModelOutputItem(itemType);
                bool isTool = IsToolItem(itemType, itemKind);
                if (!isModel && !isTool) return false;

                long? startMs = GetLong(payload, "started_at_ms");
                long? endMs = GetLong(payload, "completed_at_ms");
                if (!startMs.HasValue || !endMs.HasValue) return false;
                long dur = endMs.Value - startMs.Value;
                if (dur < 0 || dur > MaxSingleItemMs) return false;

                string itemId = GetStr(item, "id") ?? (startMs.Value + "-" + endMs.Value);
                var turn = GetOrCreateTurn(turnId);
                var iv = new Interval { Start = startMs.Value, End = endMs.Value };
                if (isModel) turn.ModelItems[itemId] = iv;
                else turn.ToolItems[itemId] = iv;
                if (!turn.LastEventAtMs.HasValue || endMs.Value > turn.LastEventAtMs.Value)
                    turn.LastEventAtMs = endMs.Value;
                return true;
            }

            if (pType == "task_complete")
            {
                string turnId = GetStr(payload, "turn_id") ?? GetStr(GetDict(payload, "turn_attribution"), "turn_id");
                if (!string.IsNullOrWhiteSpace(turnId))
                {
                    var turn = GetOrCreateTurn(turnId);
                    turn.Complete = true;
                    long? dur = GetLong(payload, "duration_ms");
                    if (dur.HasValue && dur.Value >= 0) turn.WallDurationMs = dur.Value;
                    long? ttft = GetLong(payload, "time_to_first_token_ms");
                    if (ttft.HasValue && ttft.Value >= 0) turn.TtftMs = ttft.Value;

                    string ts = GetStr(row, "timestamp");
                    DateTime parsedTs;
                    if (!string.IsNullOrWhiteSpace(ts) && DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsedTs))
                    {
                        turn.CompletedAtLocal = parsedTs.ToLocalTime();
                        turn.CompletedAtMs = (long)(parsedTs - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                    }

                    var summary = BuildTurnSummary(turn);
                    if (summary.Tps.HasValue)
                    {
                        _lastCompletedTps = summary.Tps;
                        _lastCompletedModelMs = summary.ModelActiveMs;
                    }
                    if (summary.TtftMs.HasValue) _lastCompletedTtftMs = summary.TtftMs;

                    if (string.Equals(_activeTurnId, turnId, StringComparison.OrdinalIgnoreCase))
                        _isStreaming = false;
                }
                return true;
            }

            return false;
        }

        internal SessionMetricsSnapshot BuildSnapshot()
        {
            var snap = new SessionMetricsSnapshot
            {
                ThreadId = _threadId,
                BindingSource = _bindingSource,
                RolloutPath = _rolloutPath,
                HasUsage = _hasUsage,
                IsStreaming = _isStreaming,
                InputTokens = _cumInput,
                CachedInputTokens = _cumCached,
                OutputTokens = _cumOutput,
                ReasoningOutputTokens = _cumReasoning,
                TotalTokens = _cumTotal > 0 ? _cumTotal : (_cumInput + _cumOutput),
                SessionCacheHitPct = _cumInput > 0 ? (double?)(_cumCached * 100.0 / _cumInput) : null,
                ContextUsedTokens = _contextUsed,
                ContextCachedTokens = Math.Min(_contextCached, _contextUsed),
                ContextUncachedInputTokens = Math.Max(0, _contextUsed - _contextCached),
                ContextLastOutputTokens = _contextLastOutput,
                ContextWindowTokens = _contextWindow,
                ContextRemainingTokens = _contextWindow > 0 ? Math.Max(0, _contextWindow - _contextUsed) : 0,
                ContextPct = (_contextWindow > 0 && _contextUsed > 0)
                    ? (double?)Math.Min(100.0, _contextUsed * 100.0 / _contextWindow)
                    : null,
                PrimaryUsedPct = _primaryUsedPct,
                PrimaryResetsAt = _primaryResetsAt,
                SecondaryUsedPct = _secondaryUsedPct,
                SecondaryResetsAt = _secondaryResetsAt,
                ContextChannels = BuildContextChannels(_contextUsed)
            };

            long avgOut = 0;
            long avgModelMs = 0;
            for (int i = 0; i < _turnsOrdered.Count; i++)
            {
                var summary = BuildTurnSummary(_turnsOrdered[i]);
                if (summary.TotalTokens > 0)
                {
                    snap.Turns.Add(summary);
                    if (summary.OutputTokens > 0 && summary.ModelActiveMs >= 200)
                    {
                        avgOut += summary.OutputTokens;
                        avgModelMs += summary.ModelActiveMs;
                    }
                }
            }

            if (avgOut > 0 && avgModelMs >= 500)
            {
                double avg = avgOut * 1000.0 / avgModelMs;
                if (!double.IsNaN(avg) && !double.IsInfinity(avg) && avg > 0 && avg <= MaxPlausibleTps)
                    snap.SessionAvgTps = avg;
            }

            if (snap.Turns.Count > 0)
                snap.LatestTurn = snap.Turns[snap.Turns.Count - 1];

            // Determine displayed TPS: live turn if measurable, else held last completed TPS
            if (_isStreaming && !string.IsNullOrWhiteSpace(_activeTurnId) && _turnsById.ContainsKey(_activeTurnId))
            {
                var live = BuildTurnSummary(_turnsById[_activeTurnId]);
                if (live.Tps.HasValue)
                {
                    snap.DisplayTps = live.Tps;
                    snap.TpsIsHeld = false;
                    snap.LatestModelActiveMs = live.ModelActiveMs;
                    snap.LatestTtftMs = live.TtftMs ?? _lastCompletedTtftMs;
                }
                else if (_lastCompletedTps.HasValue)
                {
                    snap.DisplayTps = _lastCompletedTps;
                    snap.TpsIsHeld = true;
                    snap.LatestModelActiveMs = _lastCompletedModelMs;
                    snap.LatestTtftMs = _lastCompletedTtftMs;
                }
            }
            else if (snap.LatestTurn != null && snap.LatestTurn.Tps.HasValue)
            {
                snap.DisplayTps = snap.LatestTurn.Tps;
                snap.TpsIsHeld = false;
                snap.LatestModelActiveMs = snap.LatestTurn.ModelActiveMs;
                snap.LatestTtftMs = snap.LatestTurn.TtftMs ?? _lastCompletedTtftMs;
            }
            else if (_lastCompletedTps.HasValue)
            {
                snap.DisplayTps = _lastCompletedTps;
                snap.TpsIsHeld = false;
                snap.LatestModelActiveMs = _lastCompletedModelMs;
                snap.LatestTtftMs = _lastCompletedTtftMs;
            }

            return snap;
        }

        private MutableTurn GetOrCreateTurn(string turnId)
        {
            MutableTurn t;
            if (!_turnsById.TryGetValue(turnId, out t))
            {
                t = new MutableTurn { Order = _turnsOrdered.Count + 1, TurnId = turnId };
                _turnsById[turnId] = t;
                _turnsOrdered.Add(t);
            }
            return t;
        }

        private void ApplyCumulative(UsageBag u)
        {
            if (u == null) return;
            _cumInput = u.Input;
            _cumCached = u.Cached;
            _cumOutput = u.Output;
            _cumReasoning = u.Reasoning;
            _cumTotal = u.Total > 0 ? u.Total : (u.Input + u.Output);
            if (_cumInput > 0 || _cumOutput > 0 || _cumTotal > 0) _hasUsage = true;
        }

        private static TurnSummary BuildTurnSummary(MutableTurn turn)
        {
            long inp = 0, cached = 0, outp = 0, reason = 0;
            foreach (var r in turn.Responses.Values)
            {
                inp += r.Input;
                cached += r.Cached;
                outp += r.Output;
                reason += r.Reasoning;
            }
            long total = inp + outp;
            long modelMs = ComputeDurationWithoutToolsMs(turn);

            double? tps = null;
            if (outp > 0 && modelMs >= 200)
            {
                double v = outp * 1000.0 / modelMs;
                if (!double.IsNaN(v) && !double.IsInfinity(v) && v > 0 && v <= MaxPlausibleTps)
                    tps = v;
            }

            return new TurnSummary
            {
                Index = turn.Order,
                TurnId = turn.TurnId,
                Complete = turn.Complete,
                CompletedAtLocal = turn.CompletedAtLocal,
                InputTokens = inp,
                CachedInputTokens = cached,
                OutputTokens = outp,
                ReasoningOutputTokens = reason,
                TotalTokens = total,
                ModelActiveMs = modelMs,
                TtftMs = turn.TtftMs,
                WallDurationMs = turn.WallDurationMs,
                Tps = tps,
                CacheHitPct = inp > 0 ? (double?)(cached * 100.0 / inp) : null
            };
        }

        private void AddCtxWeight(string channel, double weight)
        {
            if (weight <= 0 || string.IsNullOrEmpty(channel)) return;
            double cur;
            _ctxWeights.TryGetValue(channel, out cur);
            _ctxWeights[channel] = cur + weight;
        }

        private bool ConsumeFastResponseItem(string line)
        {
            int headLen = Math.Min(line.Length, 320);
            string head = line.Substring(0, headLen);
            // Subtract ~120 chars of JSON envelope overhead so empty/short records stay realistic
            int payloadChars = Math.Max(0, line.Length - 120);
            if (payloadChars <= 0) return false;
            double est = EstimateTokensFast(line) - 24.0;
            if (est < 1.0) est = 1.0;

            if (head.IndexOf("\"custom_tool_call_output\"", StringComparison.Ordinal) >= 0
                || head.IndexOf("\"function_call_output\"", StringComparison.Ordinal) >= 0
                || head.IndexOf("\"web_search_call\"", StringComparison.Ordinal) >= 0)
            {
                AddCtxWeight("toolResults", est);
                return false;
            }
            if (head.IndexOf("\"custom_tool_call\"", StringComparison.Ordinal) >= 0
                || head.IndexOf("\"function_call\"", StringComparison.Ordinal) >= 0
                || head.IndexOf("\"reasoning\"", StringComparison.Ordinal) >= 0)
            {
                AddCtxWeight("assistant", est);
                return false;
            }
            if (head.IndexOf("\"compaction\"", StringComparison.Ordinal) >= 0)
            {
                AddCtxWeight("checkpoint", est);
                return false;
            }
            if (head.IndexOf("\"role\":\"assistant\"", StringComparison.Ordinal) >= 0)
            {
                AddCtxWeight("assistant", est);
                return false;
            }
            if (head.IndexOf("\"role\":\"user\"", StringComparison.Ordinal) >= 0)
            {
                if (line.IndexOf("<subagent_notification>", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddCtxWeight("subagentMsg", est);
                else
                    AddCtxWeight("userInput", est);
                return false;
            }
            if (head.IndexOf("\"role\":\"developer\"", StringComparison.Ordinal) >= 0)
            {
                if (line.IndexOf("mcp__", StringComparison.OrdinalIgnoreCase) >= 0
                    && line.IndexOf("<tools>", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddCtxWeight("mcpTools", est);
                else
                    AddCtxWeight("systemPrompt", est);
                return false;
            }
            return false;
        }

        private void ConsumeParsedResponseItem(Dictionary<string, object> payload)
        {
            string pType = GetStr(payload, "type");
            if (pType == "additional_tools")
            {
                object toolsObj;
                if (payload.TryGetValue("tools", out toolsObj) && toolsObj is object[])
                {
                    var arr = (object[])toolsObj;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var td = arr[i] as Dictionary<string, object>;
                        string name = GetStr(td, "name") ?? string.Empty;
                        string serialized = _json.Serialize(arr[i]);
                        double w = EstimateTokensFast(serialized);
                        if (name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
                            AddCtxWeight("mcpTools", w);
                        else
                            AddCtxWeight("systemTools", w);
                    }
                }
                return;
            }

            string role = GetStr(payload, "role");
            string text = _json.Serialize(payload);
            double est = EstimateTokensFast(text);
            if (pType == "compaction") AddCtxWeight("checkpoint", est);
            else if (pType == "custom_tool_call_output" || pType == "function_call_output" || pType == "web_search_call")
                AddCtxWeight("toolResults", est);
            else if (pType == "custom_tool_call" || pType == "function_call" || pType == "reasoning" || role == "assistant")
                AddCtxWeight("assistant", est);
            else if (role == "user")
            {
                if (text.IndexOf("<subagent_notification>", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddCtxWeight("subagentMsg", est);
                else
                    AddCtxWeight("userInput", est);
            }
            else if (role == "developer")
            {
                AddCtxWeight("systemPrompt", est);
            }
        }

        private void ConsumeWorldState(Dictionary<string, object> payload)
        {
            var st = GetDict(payload, "state");
            if (st == null) return;

            object v;
            if (st.TryGetValue("agents_md", out v) && v != null)
                _wsUserRulesWeight = EstimateTokensFast(_json.Serialize(v));

            double skillsW = 0.0;
            if (st.TryGetValue("host_skills", out v) && v != null) skillsW += EstimateTokensFast(_json.Serialize(v));
            if (st.TryGetValue("cloud_skills", out v) && v != null) skillsW += EstimateTokensFast(_json.Serialize(v));
            if (st.TryGetValue("skills", out v) && v != null) skillsW += EstimateTokensFast(_json.Serialize(v));
            if (skillsW > 0) _wsSkillsWeight = skillsW;

            double toolsW = 0.0;
            if (st.TryGetValue("tools", out v) && v != null) toolsW += EstimateTokensFast(_json.Serialize(v));
            if (st.TryGetValue("top_level_tools", out v) && v != null) toolsW += EstimateTokensFast(_json.Serialize(v));
            if (toolsW > 0) _wsSystemToolsWeight = toolsW;

            _hasWorldStateStatic = true;
        }

        private void ConsumeCompacted(Dictionary<string, object> payload)
        {
            // Compaction replaces all prior active conversation history!
            for (int i = 0; i < ChannelKeys.Length; i++) _ctxWeights[ChannelKeys[i]] = 0.0;

            string msg = GetStr(payload, "message");
            if (!string.IsNullOrWhiteSpace(msg))
                AddCtxWeight("checkpoint", EstimateTokensFast(msg));

            object rhObj;
            if (payload.TryGetValue("replacement_history", out rhObj) && rhObj is object[])
            {
                var arr = (object[])rhObj;
                for (int i = 0; i < arr.Length; i++)
                {
                    var item = arr[i] as Dictionary<string, object>;
                    if (item != null) ConsumeParsedResponseItem(item);
                }
            }
        }

        private List<ContextChannelItem> BuildContextChannels(long officialContextUsed)
        {
            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < ChannelKeys.Length; i++)
            {
                double w;
                _ctxWeights.TryGetValue(ChannelKeys[i], out w);
                weights[ChannelKeys[i]] = w;
            }

            // Merge static world_state channels (user rules & skills) and base instructions fallback
            if (_hasWorldStateStatic)
            {
                if (_wsUserRulesWeight > 0) weights["userRules"] = Math.Max(weights["userRules"], _wsUserRulesWeight);
                if (_wsSkillsWeight > 0) weights["skills"] = Math.Max(weights["skills"], _wsSkillsWeight);
                if (_wsSystemToolsWeight > 0 && weights["systemTools"] <= 0) weights["systemTools"] = _wsSystemToolsWeight;
            }
            if (weights["systemPrompt"] <= 0 && _baseInstructionsWeight > 0)
                weights["systemPrompt"] = _baseInstructionsWeight;

            var scaled = ScaleBucketsExact(weights, ChannelKeys, officialContextUsed);
            var list = new List<ContextChannelItem>(ChannelKeys.Length);
            for (int i = 0; i < ChannelKeys.Length; i++)
            {
                string k = ChannelKeys[i];
                list.Add(new ContextChannelItem
                {
                    Key = k,
                    Label = ChannelLabels[k],
                    Tokens = scaled[k]
                });
            }

            // Sort descending by tokens (keeping canonical order on ties) just like usage-monitor!
            return list.OrderByDescending(x => x.Tokens).ToList();
        }

        private static Dictionary<string, long> ScaleBucketsExact(Dictionary<string, double> weights, string[] keys, long targetTotal)
        {
            var outMap = new Dictionary<string, long>(StringComparer.Ordinal);
            double sum = 0.0;
            for (int i = 0; i < keys.Length; i++)
            {
                double w = weights.ContainsKey(keys[i]) ? Math.Max(0.0, weights[keys[i]]) : 0.0;
                sum += w;
                outMap[keys[i]] = 0;
            }

            if (targetTotal <= 0 || sum <= 0.0) return outMap;

            var remainders = new List<Tuple<string, double, double>>();
            long assigned = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                string k = keys[i];
                double w = weights.ContainsKey(k) ? Math.Max(0.0, weights[k]) : 0.0;
                if (w <= 0.0) continue;
                double exact = (w * targetTotal) / sum;
                long floored = (long)Math.Floor(exact);
                outMap[k] = floored;
                assigned += floored;
                remainders.Add(Tuple.Create(k, exact - floored, w));
            }

            long diff = targetTotal - assigned;
            if (diff > 0 && remainders.Count > 0)
            {
                remainders.Sort(delegate(Tuple<string, double, double> a, Tuple<string, double, double> b)
                {
                    int c = b.Item2.CompareTo(a.Item2);
                    return c != 0 ? c : b.Item3.CompareTo(a.Item3);
                });
                for (long i = 0; i < diff; i++)
                {
                    string k = remainders[(int)(i % remainders.Count)].Item1;
                    outMap[k] = outMap[k] + 1;
                }
            }
            return outMap;
        }

        private static double EstimateTokensFast(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0.0;
            int ascii = 0;
            int nonAscii = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < 128) ascii++;
                else nonAscii++;
            }
            return (ascii * 0.25) + (nonAscii * 0.65);
        }

        private static long ComputeDurationWithoutToolsMs(MutableTurn turn)
        {
            // Match src/usage-state.mjs exact formula:
            //   durationWithoutTools = turnWallDurationMs - (union(toolItems) minus overlap with modelItems)
            // This properly includes model time spent generating tool-call arguments (which emit no
            // Reasoning/AgentMessage item_completed) while strictly excluding tool execution time.
            long wallMs;
            long startMs;
            long endMs;

            if (turn.WallDurationMs.HasValue && turn.WallDurationMs.Value > 0 && turn.CompletedAtMs.HasValue)
            {
                wallMs = turn.WallDurationMs.Value;
                endMs = turn.CompletedAtMs.Value;
                startMs = endMs - wallMs;
            }
            else if (turn.StartedAtMs.HasValue && turn.LastEventAtMs.HasValue && turn.LastEventAtMs.Value > turn.StartedAtMs.Value)
            {
                startMs = turn.StartedAtMs.Value;
                endMs = turn.LastEventAtMs.Value;
                wallMs = endMs - startMs;
            }
            else
            {
                // Last-resort fallback if turn boundary timestamps were not observed
                return MergeIntervalsMs(turn.ModelItems.Values);
            }

            var tools = new List<Interval>();
            foreach (var iv in turn.ToolItems.Values)
            {
                long a = Math.Max(startMs, iv.Start);
                long b = Math.Min(endMs, iv.End);
                if (b > a) tools.Add(new Interval { Start = a, End = b });
            }
            var models = new List<Interval>();
            foreach (var iv in turn.ModelItems.Values)
            {
                long a = Math.Max(startMs, iv.Start);
                long b = Math.Min(endMs, iv.End);
                if (b > a) models.Add(new Interval { Start = a, End = b });
            }

            var toolUnion = MergeIntervals(tools);
            var modelUnion = MergeIntervals(models);
            long toolMs = 0;
            for (int i = 0; i < toolUnion.Count; i++)
                toolMs += (toolUnion[i].End - toolUnion[i].Start);

            // Preserve model activity that overlapped with tool execution
            int mi = 0;
            for (int i = 0; i < toolUnion.Count; i++)
            {
                long a = toolUnion[i].Start;
                long b = toolUnion[i].End;
                while (mi < modelUnion.Count && modelUnion[mi].End <= a) mi++;
                for (int j = mi; j < modelUnion.Count && modelUnion[j].Start < b; j++)
                {
                    long overlap = Math.Min(b, modelUnion[j].End) - Math.Max(a, modelUnion[j].Start);
                    if (overlap > 0) toolMs -= overlap;
                }
            }

            long result = wallMs - Math.Max(0, toolMs);
            return result > 0 ? result : 0;
        }

        private static List<Interval> MergeIntervals(IEnumerable<Interval> intervals)
        {
            var list = intervals.Where(x => x.End > x.Start).OrderBy(x => x.Start).ToList();
            var merged = new List<Interval>();
            if (list.Count == 0) return merged;
            long curStart = list[0].Start;
            long curEnd = list[0].End;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].Start <= curEnd)
                {
                    if (list[i].End > curEnd) curEnd = list[i].End;
                }
                else
                {
                    merged.Add(new Interval { Start = curStart, End = curEnd });
                    curStart = list[i].Start;
                    curEnd = list[i].End;
                }
            }
            merged.Add(new Interval { Start = curStart, End = curEnd });
            return merged;
        }

        private static long MergeIntervalsMs(IEnumerable<Interval> intervals)
        {
            var merged = MergeIntervals(intervals);
            long total = 0;
            for (int i = 0; i < merged.Count; i++)
                total += (merged[i].End - merged[i].Start);
            return total;
        }

        private static bool IsModelOutputItem(string itemType)
        {
            if (string.IsNullOrEmpty(itemType)) return false;
            return string.Equals(itemType, "Reasoning", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "AgentMessage", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "Plan", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsToolItem(string itemType, string itemKind)
        {
            if (string.IsNullOrEmpty(itemType)) return false;
            if (string.Equals(itemType, "Extension", StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(itemType, "CommandExecution", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "FileChange", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "McpToolCall", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "WebSearch", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "ImageView", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "DynamicToolCall", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "CollabAgentToolCall", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "ImageGeneration", StringComparison.OrdinalIgnoreCase);
        }

        private static long? ParseTimestampMs(string ts)
        {
            DateTime parsed;
            if (string.IsNullOrWhiteSpace(ts)) return null;
            if (!DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                return null;
            return (long)(parsed - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static UsageBag ParseUsage(Dictionary<string, object> dict)
        {
            if (dict == null) return null;
            long? inp = GetLong(dict, "input_tokens");
            long? outp = GetLong(dict, "output_tokens");
            if (!inp.HasValue || !outp.HasValue || inp.Value < 0 || outp.Value < 0) return null;
            long cached = GetLong(dict, "cached_input_tokens") ?? 0;
            long reasoning = GetLong(dict, "reasoning_output_tokens") ?? 0;
            long total = GetLong(dict, "total_tokens") ?? (inp.Value + outp.Value);
            return new UsageBag
            {
                Input = inp.Value,
                Cached = Math.Max(0, Math.Min(cached, inp.Value)),
                Output = outp.Value,
                Reasoning = Math.Max(0, reasoning),
                Total = total
            };
        }

        private static string GetStr(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as string : null;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }

        private static long? GetLong(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return null;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is decimal) return (long)(decimal)v;
            if (v is double) return (long)(double)v;
            long parsed;
            if (long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed)) return parsed;
            return null;
        }

        private static double? GetDouble(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return null;
            if (v is double) return (double)v;
            if (v is float) return (float)v;
            if (v is decimal) return (double)(decimal)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            double parsed;
            if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return null;
        }
    }
}
