using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexInputUsage
{
    internal static class PrivacySecurityChecks
    {
        internal static bool Run(StringBuilder report, string workDir)
        {
            bool switches = Program.GetDiagnostics(new string[0]) == Program.Diagnostics.None
                && Program.GetDiagnostics(new[] { "--diag" }) == Program.Diagnostics.Log
                && Program.GetDiagnostics(new[] { "--diag-capture" }) == Program.Diagnostics.Capture
                && Program.GetDiagnostics(new[] { "--diag", "--diag-capture" }) == (Program.Diagnostics.Log | Program.Diagnostics.Capture);
            bool match = CheckMatchDetails();
            bool config, lru;
            string dir = Path.Combine(workDir, "privacy-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                config = CheckConfig(dir);
                string rollout = Path.Combine(dir, "rollout-synthetic.jsonl");
                File.WriteAllText(rollout, "");
                lru = SessionMatcher.CheckCorpusCache(rollout);
            }
            finally
            {
                foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
                Directory.Delete(dir);
            }

            report.AppendLine(string.Format("privacy_security_tests switches={0} matchDetails={1} corpusLru={2} companionConfig={3}",
                switches, match, lru, config));
            return switches && match && lru && config;
        }

        private static bool CheckMatchDetails()
        {
            const string title = "private-title-sentinel";
            const string body = "private-body-sentinel only visible in the first chat";
            const string miss = "private-miss-sentinel";
            var matcher = new SessionMatcher();
            var candidates = new List<StateThreadRecord>
            {
                new StateThreadRecord { Id = "first", Name = title },
                new StateThreadRecord { Id = "second", Name = "another-title" },
                new StateThreadRecord { Id = "third", Name = "another-title" }
            };
            var corpus = new Dictionary<string, string> { { "second", body } };
            var empty = new string[0];
            var results = new[]
            {
                matcher.ResolveFromCandidatesForTest(candidates, corpus, new[] { title }, empty),
                matcher.ResolveFromCandidatesForTest(candidates, corpus, new[] { "another-title" }, new[] { body }),
                matcher.ResolveFromCandidatesForTest(candidates, corpus, new[] { miss }, empty),
                matcher.ResolveLocalFromCandidatesForTest(candidates, corpus, new[] { title }, empty),
                matcher.ResolveLocalFromCandidatesForTest(candidates, corpus, new[] { miss }, empty)
            };
            return results[0].ThreadId == "first" && results[1].ThreadId == "second" && results[2].ThreadId == null
                && results[3].ThreadId == "first" && results[4].ThreadId == null
                && results.All(r => !string.IsNullOrEmpty(r.Detail)
                    && !r.Detail.Contains(title) && !r.Detail.Contains(body) && !r.Detail.Contains(miss));
        }

        private static bool CheckConfig(string dir)
        {
            var encoding = new UTF8Encoding(false);
            string config = Path.Combine(dir, "config.toml");
            string script = Path.Combine(dir, "owner's companion.mjs");
            string node = Path.Combine(dir, "owner's node.exe");
            string original = "notes = '''\r\n[mcp_servers.codex_input_usage]\r\nliteral\r\n'''\r\n"
                + "[mcp_servers.other]\r\nargs = [\r\n  '[arg]',\r\n]\r\n# keep this comment\r\n";
            File.WriteAllText(config, original, encoding);
            bool noScript = !CodexCompanionConfig.Update(config, script, node, true);
            File.WriteAllText(script, "", encoding);
            bool noNode = !CodexCompanionConfig.Update(config, script, node, true);
            File.WriteAllText(node, "", encoding);
            bool noConfig = !CodexCompanionConfig.Update(config + ".missing", script, node, true);
            bool unchanged = File.ReadAllText(config) == original && !CodexCompanionConfig.IsEnabled(original);
            bool enabled = CodexCompanionConfig.Update(config, script, node, true);
            string installed = File.ReadAllText(config);
            bool repeated = CodexCompanionConfig.Update(config, script, node, true) && File.ReadAllText(config) == installed;
            bool quoted = installed.StartsWith(original, StringComparison.Ordinal)
                && installed.Contains("\"" + script.Replace("\\", "\\\\") + "\"")
                && installed.Contains("\"" + node.Replace("\\", "\\\\") + "\"")
                && File.ReadAllBytes(config)[0] == (byte)'n';
            File.Delete(script);
            File.Delete(node);
            bool removed = CodexCompanionConfig.Update(config, null, null, false)
                && CodexCompanionConfig.Update(config, null, null, false) && File.ReadAllText(config) == original
                && !CodexCompanionConfig.IsEnabled(File.ReadAllText(config));
            return noScript && noNode && noConfig && unchanged && enabled && repeated && quoted && removed;
        }
    }
}
