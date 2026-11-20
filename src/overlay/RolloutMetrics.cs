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

    internal sealed class SessionMetricsSnapshot
    {
        internal string ThreadId;
        internal string BindingSource; // "IPC" / "SQLite" / "等待"
        internal string RolloutPath;
        internal bool HasUsage;
        internal bool IsStreaming;

        // Session cumulative
        internal long InputTokens;
        internal long CachedInputTokens;
        internal long OutputTokens;
        internal long ReasoningOutputTokens;
        internal long TotalTokens;
        internal double? SessionCacheHitPct;

        // Context window
        internal long ContextUsedTokens;
        internal long ContextWindowTokens;
        internal double? ContextPct;

        // Rate limits (5h primary, 7d secondary)
        internal double? PrimaryUsedPct;
        internal long? PrimaryResetsAt;
        internal double? SecondaryUsedPct;
        internal long? SecondaryResetsAt;

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

        internal static string[] BuildStripVariants(SessionMetricsSnapshot snap)
        {
            string tps = snap != null ? FormatTps(snap.DisplayTps, snap.TpsIsHeld) : "--";
            string turn = (snap != null && snap.LatestTurn != null && snap.LatestTurn.TotalTokens > 0)
                ? CompactTokens(snap.LatestTurn.TotalTokens)
                : "--";
            string total = (snap != null && snap.HasUsage) ? CompactTokens(snap.TotalTokens) : "--";
            string cache = (snap != null && snap.HasUsage) ? FormatCacheCompact(snap.SessionCacheHitPct) : "--";

            return new string[]
            {
                // Level 0: Full strip with TPS + latest turn + session cumulative + cache rate
                string.Format("\u26A1 {0} tok/s \u00B7 \u672C\u8F6E {1} \u00B7 \u7D2F\u8BA1 {2} \u00B7 \u7F13\u5B58 {3}", tps, turn, total, cache),
                // Level 1: Compact 4-metric form
                string.Format("\u26A1 {0}/s \u00B7 \u8F6E {1} \u00B7 \u7D2F {2} \u00B7 {3}", tps, turn, total, cache),
                // Level 2: Narrower — preserve required 3 metrics (latest turn + cumulative + cache)
                string.Format("\u672C\u8F6E {0} \u00B7 \u7D2F\u8BA1 {1} \u00B7 {2}", turn, total, cache),
                // Level 3: Very narrow
                string.Format("{0} / {1} \u00B7 {2}", turn, total, cache),
                // Level 4: Extreme narrow fallback
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

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        private readonly Dictionary<string, MutableTurn> _turnsById = new Dictionary<string, MutableTurn>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MutableTurn> _turnsOrdered = new List<MutableTurn>();

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
            _cumInput = 0;
            _cumCached = 0;
            _cumOutput = 0;
            _cumReasoning = 0;
            _cumTotal = 0;
            _hasUsage = false;
            _contextUsed = 0;
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
                || line.IndexOf("\"item_completed\"", StringComparison.Ordinal) >= 0;
        }

        private bool ConsumeLine(string line)
        {
            Dictionary<string, object> row;
            try { row = _json.DeserializeObject(line) as Dictionary<string, object>; }
            catch { return false; }
            if (row == null) return false;

            string rowType = GetStr(row, "type");
            var payload = GetDict(row, "payload");
            if (payload == null) return false;

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
                    if (lastUsage != null && lastUsage.Input > 0) _contextUsed = lastUsage.Input;

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
                ContextWindowTokens = _contextWindow,
                ContextPct = (_contextWindow > 0 && _contextUsed > 0)
                    ? (double?)Math.Min(100.0, _contextUsed * 100.0 / _contextWindow)
                    : null,
                PrimaryUsedPct = _primaryUsedPct,
                PrimaryResetsAt = _primaryResetsAt,
                SecondaryUsedPct = _secondaryUsedPct,
                SecondaryResetsAt = _secondaryResetsAt
            };

            for (int i = 0; i < _turnsOrdered.Count; i++)
            {
                var summary = BuildTurnSummary(_turnsOrdered[i]);
                if (summary.TotalTokens > 0 || summary.Complete)
                    snap.Turns.Add(summary);
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
