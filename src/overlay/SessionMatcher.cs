using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexInputUsage
{
    internal sealed class SessionMatchResult
    {
        internal string ThreadId;
        internal string RolloutPath;
        internal string ModelName;
        internal string ReasoningEffort;
        internal string Source; // "IPC+标题", "IPC+正文", "IPC", "IPC 断线", "待确认"
        internal string Detail;
    }

    internal sealed class SessionMatcher
    {
        private const int TailCorpusBytes = 2 * 1024 * 1024;
        private const int HeadCorpusBytes = 256 * 1024;
        private const int MaxLocalBodyCandidates = 80;
        private static readonly Regex MultiSpace = new Regex(@"\s+", RegexOptions.Compiled);

        private sealed class CachedCorpus
        {
            internal string Path;
            internal long FileLength;
            internal string NormalizedText;
        }

        private readonly Dictionary<string, CachedCorpus> _corpusCache = new Dictionary<string, CachedCorpus>(StringComparer.OrdinalIgnoreCase);
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        private Dictionary<string, string> _sessionIndexNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _sessionIndexWriteUtc = DateTime.MinValue;
        private List<StateThreadRecord> _recentThreadsCache;
        private string _recentThreadsDbStamp = string.Empty;

        internal SessionMatchResult Resolve(
            IpcRouteStatus ipc,
            string[] headerTexts,
            string[] bodyTexts)
        {
            if (ipc == null || !ipc.IsConnected)
            {
                return ResolveLocal(headerTexts, bodyTexts);
            }

            string[] followed = ipc.FollowedThreadIds ?? new string[0];
            if (followed.Length == 0)
            {
                return new SessionMatchResult
                {
                    Source = "等待",
                    Detail = "ipc-no-followed-threads"
                };
            }

            RefreshSessionIndexIfNeeded();
            var candidates = LoadCandidates(followed);
            if (candidates.Count == 0)
            {
                return new SessionMatchResult
                {
                    Source = "待确认",
                    Detail = "followed=" + followed.Length + " no-metadata"
                };
            }

            string[] normHeaders = NormalizeSnippets(headerTexts, 3, 160);
            string[] normBodies = NormalizeSnippets(bodyTexts, 12, 220);

            // Step 1: Match foreground chat header bar (strictly right of left sidebar) against candidate Name/Title.
            var headerHits = MatchByHeader(candidates, normHeaders);
            if (headerHits.Count == 1)
            {
                var win = headerHits[0];
                return new SessionMatchResult
                {
                    ThreadId = win.Id,
                    RolloutPath = ResolveRollout(win),
                    ModelName = win.Model,
                    ReasoningEffort = win.ReasoningEffort,
                    Source = "IPC+标题",
                    Detail = string.Format("followed={0} headerHit=\"{1}\"", followed.Length, Truncate(win.Name ?? win.Title, 32))
                };
            }

            // Step 2: Match visible chat transcript text above composer against candidate rollout text corpus.
            // If Step 1 had multiple threads with identical titles, narrow Step 2 to those tied candidates first.
            var bodyPool = headerHits.Count > 1 ? headerHits : candidates;
            if (normBodies.Length > 0)
            {
                int bestScore, secondScore, bestMaxLen;
                StateThreadRecord bodyWinner = MatchByBodyText(bodyPool, normBodies, out bestScore, out secondScore, out bestMaxLen);
                if (bodyWinner != null && IsClearBodyWinner(bestScore, secondScore))
                {
                    return new SessionMatchResult
                    {
                        ThreadId = bodyWinner.Id,
                        RolloutPath = ResolveRollout(bodyWinner),
                        ModelName = bodyWinner.Model,
                        ReasoningEffort = bodyWinner.ReasoningEffort,
                        Source = "IPC+正文",
                        Detail = string.Format("followed={0} bodyScore={1} second={2}", followed.Length, bestScore, secondScore)
                    };
                }
            }

            // Step 3: If ONLY ONE conversation is followed in IPC and UIA didn't contradict it, bind it.
            // (Covers brand-new empty chats or chats whose title hasn't appeared yet.)
            if (followed.Length == 1)
            {
                var only = candidates[0];
                return new SessionMatchResult
                {
                    ThreadId = only.Id,
                    RolloutPath = ResolveRollout(only),
                    ModelName = only.Model,
                    ReasoningEffort = only.ReasoningEffort,
                    Source = "IPC",
                    Detail = "single-followed-thread"
                };
            }

            // Step 4: Multiple warm threads in IPC and foreground UIA could not uniquely confirm which one is displayed.
            // Strictly refuse to guess so we never display another chat's metrics!
            return new SessionMatchResult
            {
                ThreadId = null,
                RolloutPath = null,
                Source = "待确认",
                Detail = string.Format("ambiguous followed={0} headerHits={1} uiHeaders=[{2}] uiBodies={3}",
                    followed.Length,
                    headerHits.Count,
                    string.Join("|", normHeaders.Take(4).Select(s => Truncate(s, 24)).ToArray()),
                    normBodies.Length)
            };
        }

        internal SessionMatchResult ResolveFromCandidatesForTest(
            List<StateThreadRecord> candidates,
            Dictionary<string, string> corpusById,
            string[] headerTexts,
            string[] bodyTexts)
        {
            string[] normHeaders = NormalizeSnippets(headerTexts, 3, 160);
            string[] normBodies = NormalizeSnippets(bodyTexts, 12, 220);

            var headerHits = MatchByHeader(candidates, normHeaders);
            if (headerHits.Count == 1)
            {
                return new SessionMatchResult { ThreadId = headerHits[0].Id, Source = "IPC+标题", Detail = "test-header" };
            }

            var pool = headerHits.Count > 1 ? headerHits : candidates;
            if (normBodies.Length > 0)
            {
                int best = 0, second = 0;
                StateThreadRecord win = null;
                for (int i = 0; i < pool.Count; i++)
                {
                    string corpus;
                    if (!corpusById.TryGetValue(pool[i].Id, out corpus)) corpus = string.Empty;
                    string normCorpus = NormalizeText(corpus);
                    int maxLen;
                    int score = CountSnippetHits(normCorpus, normBodies, out maxLen);
                    if (score > best) { second = best; best = score; win = pool[i]; }
                    else if (score > second) { second = score; }
                }
                if (win != null && IsClearBodyWinner(best, second))
                {
                    return new SessionMatchResult { ThreadId = win.Id, Source = "IPC+正文", Detail = "test-body" };
                }
            }

            if (candidates.Count == 1)
                return new SessionMatchResult { ThreadId = candidates[0].Id, Source = "IPC", Detail = "test-single" };

            return new SessionMatchResult { ThreadId = null, Source = "待确认", Detail = "test-ambiguous" };
        }

        internal SessionMatchResult ResolveLocalFromCandidatesForTest(
            List<StateThreadRecord> candidates,
            Dictionary<string, string> corpusById,
            string[] headerTexts,
            string[] bodyTexts)
        {
            string[] headers = NormalizeSnippets(headerTexts, 3, 160);
            string[] bodies = NormalizeSnippets(bodyTexts, 14, 220);
            if (headers.Length == 0 && bodies.Length == 0)
                return new SessionMatchResult { Source = "IPC 断线", Detail = "test-local-empty" };

            var hits = MatchByHeader(candidates, headers, false, 6);
            if (hits.Count == 1)
                return new SessionMatchResult { ThreadId = hits[0].Id, Source = "本地+标题", Detail = "test-local-header" };

            var pool = hits.Count > 1 ? hits : candidates;
            if (bodies.Length > 0)
            {
                int best = 0, second = 0, bestMaxLen = 0;
                StateThreadRecord win = null;
                for (int i = 0; i < pool.Count; i++)
                {
                    string corpus;
                    if (!corpusById.TryGetValue(pool[i].Id, out corpus)) corpus = string.Empty;
                    string normCorpus = NormalizeText(corpus);
                    int maxLen;
                    int score = CountSnippetHits(normCorpus, bodies, out maxLen);
                    if (score > best)
                    {
                        second = best;
                        best = score;
                        bestMaxLen = maxLen;
                        win = pool[i];
                    }
                    else if (score > second)
                    {
                        second = score;
                    }
                }
                if (win != null && IsClearLocalBodyWinner(best, second, bestMaxLen))
                    return new SessionMatchResult { ThreadId = win.Id, Source = "本地+正文", Detail = "test-local-body" };
            }

            return new SessionMatchResult { ThreadId = null, Source = "待确认", Detail = "test-local-ambiguous" };
        }

        private List<StateThreadRecord> LoadCandidates(string[] followedIds)
        {
            var list = new List<StateThreadRecord>(followedIds.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < followedIds.Length; i++)
            {
                string id = followedIds[i];
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                StateThreadRecord rec = CodexStateDb.TryReadThread(id) ?? new StateThreadRecord { Id = id };
                string idxName;
                if (string.IsNullOrWhiteSpace(rec.Name) && _sessionIndexNames.TryGetValue(id, out idxName))
                    rec.Name = idxName;
                list.Add(rec);
            }
            return list;
        }

        private List<StateThreadRecord> LoadAllLocalCandidates()
        {
            RefreshSessionIndexIfNeeded();
            string dbPath = CodexStateDb.ResolveDatabasePath();
            string walPath = dbPath + "-wal";
            string stamp = BuildFileStamp(dbPath) + "|" + BuildFileStamp(walPath) + "|" + _sessionIndexWriteUtc.Ticks;
            if (_recentThreadsCache != null && string.Equals(_recentThreadsDbStamp, stamp, StringComparison.Ordinal))
                return _recentThreadsCache;

            var candidates = CodexStateDb.TryReadRecentThreads(0);
            for (int i = 0; i < candidates.Count; i++)
            {
                string name;
                if (string.IsNullOrWhiteSpace(candidates[i].Name) && _sessionIndexNames.TryGetValue(candidates[i].Id, out name))
                    candidates[i].Name = name;
            }
            _recentThreadsCache = candidates;
            _recentThreadsDbStamp = stamp;
            return candidates;
        }

        private static string BuildFileStamp(string path)
        {
            try
            {
                if (!File.Exists(path)) return "0";
                var fi = new FileInfo(path);
                return fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
            }
            catch { return "err"; }
        }

        private SessionMatchResult ResolveLocal(string[] headerTexts, string[] bodyTexts)
        {
            var headers = NormalizeSnippets(headerTexts, 3, 160);
            var bodies = NormalizeSnippets(bodyTexts, 14, 220);
            if (headers.Length == 0 && bodies.Length == 0)
            {
                return new SessionMatchResult
                {
                    Source = "IPC 断线",
                    Detail = "ipc-disconnected-no-ui"
                };
            }

            var candidates = LoadAllLocalCandidates();
            if (candidates.Count == 0)
            {
                return new SessionMatchResult
                {
                    Source = "待确认",
                    Detail = "local-no-candidates"
                };
            }

            // Step 1: Exact header match first, then safe truncated prefix match (>= 6 chars) across all real user threads.
            var headerHits = MatchByHeader(candidates, headers, false, 6);
            if (headerHits.Count == 1)
            {
                var win = headerHits[0];
                string rollout = ResolveRollout(win);
                return new SessionMatchResult
                {
                    ThreadId = win.Id,
                    RolloutPath = rollout,
                    ModelName = win.Model,
                    ReasoningEffort = win.ReasoningEffort,
                    Source = "本地+标题",
                    Detail = string.Format("local-hdr=\"{0}\" rollout={1}",
                        Truncate(win.Name ?? win.Title, 28),
                        !string.IsNullOrEmpty(rollout) ? "ok" : "missing")
                };
            }

            // Step 2: Visible body text match across all real user threads that have an existing rollout file
            // (narrowed to tied header hits when duplicate titles exist).
            int best = 0, second = 0, bestMaxLen = 0;
            int bodyPoolCount = 0;
            if (bodies.Length > 0)
            {
                var sourcePool = headerHits.Count > 1 ? headerHits : candidates;
                var bodyPool = new List<StateThreadRecord>();
                for (int i = 0; i < sourcePool.Count && bodyPool.Count < MaxLocalBodyCandidates; i++)
                {
                    string rp = sourcePool[i].RolloutPath;
                    if (!string.IsNullOrWhiteSpace(rp) && File.Exists(rp))
                        bodyPool.Add(sourcePool[i]);
                }
                bodyPoolCount = bodyPool.Count;

                if (bodyPool.Count > 0)
                {
                    StateThreadRecord winner = MatchByBodyText(bodyPool, bodies, out best, out second, out bestMaxLen);
                    if (winner != null && IsClearLocalBodyWinner(best, second, bestMaxLen))
                    {
                        return new SessionMatchResult
                        {
                            ThreadId = winner.Id,
                            RolloutPath = winner.RolloutPath,
                            ModelName = winner.Model,
                            ReasoningEffort = winner.ReasoningEffort,
                            Source = "本地+正文",
                            Detail = string.Format("local-body pool={0} best={1} sec={2} maxLen={3}",
                                bodyPoolCount, best, second, bestMaxLen)
                        };
                    }
                }
            }

            return new SessionMatchResult
            {
                ThreadId = null,
                RolloutPath = null,
                Source = "待确认",
                Detail = string.Format("local-miss cands={0} bodyPool={1} hdrHits={2} best={3} sec={4} hdrs=[{5}] bodies={6}",
                    candidates.Count,
                    bodyPoolCount,
                    headerHits.Count,
                    best,
                    second,
                    string.Join("|", headers.Take(4).Select(s => Truncate(s, 22)).ToArray()),
                    bodies.Length)
            };
        }

        private static List<StateThreadRecord> MatchByHeader(
            List<StateThreadRecord> candidates,
            string[] normHeaders,
            bool exactOnly = false,
            int minPrefixLen = 6)
        {
            var hits = new List<StateThreadRecord>();
            if (normHeaders == null || normHeaders.Length == 0) return hits;

            // Pass 1: Exact normalized match on Name or Title
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                string nName = NormalizeText(c.Name);
                string nTitle = NormalizeText(c.Title);
                bool exact = false;
                for (int j = 0; j < normHeaders.Length; j++)
                {
                    string h = normHeaders[j];
                    if ((nName.Length >= 3 && string.Equals(h, nName, StringComparison.OrdinalIgnoreCase))
                        || (nTitle.Length >= 4 && string.Equals(h, nTitle, StringComparison.OrdinalIgnoreCase)))
                    {
                        exact = true;
                        break;
                    }
                }
                if (exact) hits.Add(c);
            }
            if (exactOnly || hits.Count > 0) return hits;

            // Pass 2: Truncated header prefix match (Codex truncates long titles with "…" or "...")
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                string nName = NormalizeText(c.Name);
                string nTitle = NormalizeText(c.Title);
                bool prefixMatch = false;
                for (int j = 0; j < normHeaders.Length; j++)
                {
                    string h = normHeaders[j].TrimEnd('.', '\u2026', ' ');
                    if (h.Length < minPrefixLen) continue;
                    if ((nName.Length >= h.Length && nName.StartsWith(h, StringComparison.OrdinalIgnoreCase))
                        || (nTitle.Length >= h.Length && nTitle.StartsWith(h, StringComparison.OrdinalIgnoreCase))
                        || (nName.Length >= minPrefixLen && h.StartsWith(nName, StringComparison.OrdinalIgnoreCase)))
                    {
                        prefixMatch = true;
                        break;
                    }
                }
                if (prefixMatch) hits.Add(c);
            }
            return hits;
        }

        private StateThreadRecord MatchByBodyText(
            List<StateThreadRecord> pool,
            string[] normBodies,
            out int bestScore,
            out int secondScore,
            out int bestMaxSnippetLen)
        {
            bestScore = 0;
            secondScore = 0;
            bestMaxSnippetLen = 0;
            StateThreadRecord winner = null;

            for (int i = 0; i < pool.Count; i++)
            {
                var c = pool[i];
                string corpus = GetNormalizedCorpus(c);
                if (string.IsNullOrEmpty(corpus)) continue;
                int maxLen;
                int score = CountSnippetHits(corpus, normBodies, out maxLen);
                if (score > bestScore)
                {
                    secondScore = bestScore;
                    bestScore = score;
                    bestMaxSnippetLen = maxLen;
                    winner = c;
                }
                else if (score > secondScore)
                {
                    secondScore = score;
                }
            }

            return winner;
        }

        private static bool IsClearBodyWinner(int best, int second)
        {
            if (best <= 0) return false;
            if (second == 0) return best >= 1;
            return best >= 3 && best >= (second * 2);
        }

        private static bool IsClearLocalBodyWinner(int best, int second, int bestMaxSnippetLen)
        {
            if (best <= 0) return false;
            if (best >= 2 && (second == 0 || best >= (second * 3))) return true;
            return best == 1 && second == 0 && bestMaxSnippetLen >= 28;
        }

        private static int CountSnippetHits(string normCorpus, string[] normBodies, out int maxMatchedLen)
        {
            int hits = 0;
            maxMatchedLen = 0;
            for (int i = 0; i < normBodies.Length; i++)
            {
                string s = normBodies[i];
                if (s.Length < 12) continue;
                // Take up to first 80 chars of snippet so formatting differences at end don't break substring match
                string probe = s.Length > 80 ? s.Substring(0, 80) : s;
                if (normCorpus.IndexOf(probe, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hits++;
                    if (probe.Length > maxMatchedLen) maxMatchedLen = probe.Length;
                }
            }
            return hits;
        }

        private string GetNormalizedCorpus(StateThreadRecord rec)
        {
            string path = ResolveRollout(rec);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return NormalizeText((rec.Name ?? string.Empty) + " " + (rec.Title ?? string.Empty) + " " + (rec.FirstUserMessage ?? string.Empty));
            }

            try
            {
                var fi = new FileInfo(path);
                CachedCorpus cached;
                if (_corpusCache.TryGetValue(rec.Id, out cached)
                    && string.Equals(cached.Path, path, StringComparison.OrdinalIgnoreCase)
                    && cached.FileLength == fi.Length)
                {
                    return cached.NormalizedText;
                }

                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(rec.Name)) sb.AppendLine(rec.Name);
                if (!string.IsNullOrWhiteSpace(rec.Title)) sb.AppendLine(rec.Title);
                if (!string.IsNullOrWhiteSpace(rec.FirstUserMessage)) sb.AppendLine(rec.FirstUserMessage);

                // Read head (for long chats scrolled to top) + tail of rollout JSONL to collect visible messages
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    if (len > TailCorpusBytes)
                    {
                        ScanRolloutRange(fs, 0, HeadCorpusBytes, sb);
                    }
                    long tailStart = Math.Max(0, len - TailCorpusBytes);
                    ScanRolloutRange(fs, tailStart, len - tailStart, sb);
                }

                string norm = NormalizeText(sb.ToString());
                _corpusCache[rec.Id] = new CachedCorpus
                {
                    Path = path,
                    FileLength = fi.Length,
                    NormalizedText = norm
                };
                return norm;
            }
            catch
            {
                return NormalizeText((rec.Name ?? string.Empty) + " " + (rec.Title ?? string.Empty));
            }
        }

        private void ScanRolloutRange(FileStream fs, long startOffset, long maxBytes, StringBuilder sb)
        {
            if (maxBytes <= 0) return;
            fs.Seek(startOffset, SeekOrigin.Begin);
            using (var sr = new StreamReader(fs, Encoding.UTF8, false, 32768, true))
            {
                long readApprox = 0;
                if (startOffset > 0)
                {
                    string skip = sr.ReadLine();
                    if (skip != null) readApprox += skip.Length + 1;
                }
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    readApprox += line.Length + 1;
                    if (line.IndexOf("\"last_agent_message\"", StringComparison.Ordinal) >= 0
                        || line.IndexOf("\"AgentMessage\"", StringComparison.Ordinal) >= 0
                        || line.IndexOf("\"UserMessage\"", StringComparison.Ordinal) >= 0)
                    {
                        ExtractMessageTextFromLine(line, sb);
                    }
                    if (readApprox >= maxBytes) break;
                }
            }
        }

        private void ExtractMessageTextFromLine(string line, StringBuilder sb)
        {
            Dictionary<string, object> row;
            try { row = _json.DeserializeObject(line) as Dictionary<string, object>; }
            catch { return; }
            if (row == null) return;
            object pObj;
            if (!row.TryGetValue("payload", out pObj)) return;
            var payload = pObj as Dictionary<string, object>;
            if (payload == null) return;

            object lastMsg;
            if (payload.TryGetValue("last_agent_message", out lastMsg) && lastMsg is string)
            {
                sb.AppendLine((string)lastMsg);
                return;
            }

            object itemObj;
            if (!payload.TryGetValue("item", out itemObj)) return;
            var item = itemObj as Dictionary<string, object>;
            if (item == null) return;

            object contentObj;
            if (!item.TryGetValue("content", out contentObj)) return;
            var contentArr = contentObj as object[];
            if (contentArr == null) return;

            for (int i = 0; i < contentArr.Length; i++)
            {
                var part = contentArr[i] as Dictionary<string, object>;
                if (part == null) continue;
                object txt;
                if (part.TryGetValue("text", out txt) && txt is string)
                    sb.AppendLine((string)txt);
            }
        }

        private string ResolveRollout(StateThreadRecord rec)
        {
            if (rec == null) return null;
            if (!string.IsNullOrWhiteSpace(rec.RolloutPath) && File.Exists(rec.RolloutPath))
                return rec.RolloutPath;
            return CodexStateDb.FindRolloutFileByThreadId(rec.Id);
        }

        private void RefreshSessionIndexIfNeeded()
        {
            string idxPath = Path.Combine(CodexThemeSource.GetCodexHome(), "session_index.jsonl");
            if (!File.Exists(idxPath)) return;
            try
            {
                DateTime writeUtc = File.GetLastWriteTimeUtc(idxPath);
                if (writeUtc == _sessionIndexWriteUtc && _sessionIndexNames.Count > 0) return;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadLines(idxPath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var row = _json.DeserializeObject(line) as Dictionary<string, object>;
                    if (row == null) continue;
                    object idObj, nameObj;
                    if (row.TryGetValue("id", out idObj) && row.TryGetValue("thread_name", out nameObj)
                        && idObj is string && nameObj is string && !string.IsNullOrWhiteSpace((string)nameObj))
                    {
                        map[(string)idObj] = (string)nameObj;
                    }
                }
                _sessionIndexNames = map;
                _sessionIndexWriteUtc = writeUtc;
            }
            catch { }
        }

        private static string[] NormalizeSnippets(string[] raw, int minLen, int maxLen)
        {
            if (raw == null || raw.Length == 0) return new string[0];
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < raw.Length; i++)
            {
                string s = NormalizeText(raw[i]);
                if (s.Length < minLen) continue;
                if (s.Length > maxLen) s = s.Substring(0, maxLen);
                if (seen.Add(s)) list.Add(s);
            }
            return list.ToArray();
        }

        private static string NormalizeText(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            return MultiSpace.Replace(s.Trim(), " ");
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : (s.Substring(0, max) + "…");
        }
    }
}
