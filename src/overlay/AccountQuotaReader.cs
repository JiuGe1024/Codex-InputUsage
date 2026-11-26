using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexInputUsage
{
    /// <summary>
    /// Reads the freshest account-level Codex rate_limits (5h primary + weekly secondary + credits)
    /// from the most recently updated rollout files, so opening an older chat still displays
    /// current live quota remaining and reset countdown rather than an expired snapshot.
    /// </summary>
    internal sealed class AccountQuotaReader
    {
        private const int TailScanBytes = 192 * 1024;

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private DateTime _lastCheckUtc = DateTime.MinValue;
        private Tuple<string, long, DateTime>[] _checkedCandidates;
        private AccountQuotaSnapshot _cached;

        internal AccountQuotaSnapshot GetLatestQuota(bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && (now - _lastCheckUtc).TotalSeconds < 4.0)
                return _cached;
            _lastCheckUtc = now;

            try
            {
                var recent = CodexStateDb.TryReadRecentThreads(12);
                var files = new List<FileInfo>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < recent.Count; i++)
                {
                    string p = recent[i].RolloutPath;
                    if (string.IsNullOrWhiteSpace(p) || !seen.Add(p)) continue;
                    if (File.Exists(p))
                    {
                        try { files.Add(new FileInfo(p)); } catch { }
                    }
                }

                // Sort strictly by actual disk LastWriteTimeUtc descending so the latest flushed
                // token_count across all conversations is inspected first.
                files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });

                var candidates = files.Take(5).Select(file =>
                    Tuple.Create(file.FullName, file.Length, file.LastWriteTimeUtc)).ToArray();
                return ReadCandidates(candidates, TryReadLastRateLimits);
            }
            catch { }

            return _cached;
        }

        internal AccountQuotaSnapshot ReadCandidates(Tuple<string, long, DateTime>[] candidates,
            Func<string, AccountQuotaSnapshot> read)
        {
            if (_checkedCandidates != null && _checkedCandidates.SequenceEqual(candidates)) return _cached;

            foreach (var candidate in candidates)
            {
                var snapshot = read(candidate.Item1);
                if (snapshot == null) continue;
                if (_cached != null && _cached.ObservedAtUtc.HasValue
                    && (!snapshot.ObservedAtUtc.HasValue || snapshot.ObservedAtUtc < _cached.ObservedAtUtc)) continue;
                _cached = snapshot;
                break;
            }
            _checkedCandidates = candidates;
            return _cached;
        }

        internal static void ApplyToSnapshot(SessionMetricsSnapshot snap, AccountQuotaSnapshot live)
        {
            if (snap == null || live == null) return;
            if (snap.QuotaObservedAtUtc.HasValue &&
                (!live.ObservedAtUtc.HasValue || live.ObservedAtUtc < snap.QuotaObservedAtUtc)) return;
            if (live.PrimaryUsedPct.HasValue)
            {
                snap.PrimaryUsedPct = live.PrimaryUsedPct;
                snap.PrimaryResetsAt = live.PrimaryResetsAt;
            }
            if (live.SecondaryUsedPct.HasValue)
            {
                snap.SecondaryUsedPct = live.SecondaryUsedPct;
                snap.SecondaryResetsAt = live.SecondaryResetsAt;
            }
            snap.HasCredits = live.HasCredits;
            snap.UnlimitedCredits = live.UnlimitedCredits;
            snap.CreditsBalance = live.CreditsBalance;
            snap.QuotaObservedAtUtc = live.ObservedAtUtc;
        }

        private AccountQuotaSnapshot TryReadLastRateLimits(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - TailScanBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    var matchingLines = new List<string>();
                    using (var sr = new StreamReader(fs, Encoding.UTF8, false, 32768))
                    {
                        if (start > 0) sr.ReadLine();
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) >= 0)
                                matchingLines.Add(line);
                        }
                    }

                    for (int i = matchingLines.Count - 1; i >= 0; i--)
                    {
                        var parsed = ParseRateLimitsLine(matchingLines[i], path);
                        if (parsed != null) return parsed;
                    }
                }
            }
            catch { }
            return null;
        }

        private AccountQuotaSnapshot ParseRateLimitsLine(string line, string path)
        {
            Dictionary<string, object> row;
            try { row = _json.DeserializeObject(line) as Dictionary<string, object>; }
            catch { return null; }
            if (row == null) return null;

            var payload = GetDict(row, "payload");
            var rl = GetDict(payload, "rate_limits");
            if (rl == null || GetStr(row, "type") != "event_msg" || GetStr(payload, "type") != "token_count") return null;

            var primary = GetDict(rl, "primary");
            var secondary = GetDict(rl, "secondary");
            double? pUsed = GetDouble(primary, "used_percent");
            long? pReset = GetLong(primary, "resets_at");
            double? sUsed = GetDouble(secondary, "used_percent");
            long? sReset = GetLong(secondary, "resets_at");
            if (!pUsed.HasValue && !sUsed.HasValue) return null;

            var credits = GetDict(rl, "credits");
            bool hasCredits = GetBool(credits, "has_credits");
            bool unlimited = GetBool(credits, "unlimited");
            string balance = GetStr(credits, "balance");

            DateTime? obs = MetricFormatter.ParseUtcTimestamp(GetStr(row, "timestamp"));

            return new AccountQuotaSnapshot
            {
                PrimaryUsedPct = pUsed,
                PrimaryResetsAt = pReset,
                SecondaryUsedPct = sUsed,
                SecondaryResetsAt = sReset,
                HasCredits = hasCredits,
                UnlimitedCredits = unlimited,
                CreditsBalance = balance,
                ObservedAtUtc = obs,
                SourcePath = path
            };
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }

        private static string GetStr(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as string : null;
        }

        private static bool GetBool(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v is bool && (bool)v;
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
