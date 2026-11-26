using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodexInputUsage
{
    public static class CodexCompanionConfig
    {
        private const string Table = "[mcp_servers.codex_input_usage]";
        private static readonly Regex OwnedTable = new Regex(
            @"^\[\[?\s*(?:mcp_servers|""mcp_servers""|'mcp_servers')\s*\.\s*(?:codex_input_usage|""codex_input_usage""|'codex_input_usage')\s*(?:\.|\])");

        public static string FindNodeExecutable()
        {
            foreach (var folder in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs") })
            {
                string candidate = Path.Combine(folder, "node.exe");
                if (File.Exists(candidate)) return candidate;
            }

            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string folder = entry.Trim().Trim('"');
                if (folder.Length == 0) continue;
                try
                {
                    string candidate = Path.GetFullPath(Path.Combine(folder, "node.exe"));
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
            return null;
        }

        public static string EnableProblem(string configPath, string scriptPath, string nodePath)
        {
            if (!File.Exists(scriptPath)) return "需完整仓库";
            if (!File.Exists(nodePath)) return "未找到 Node.js";
            if (!File.Exists(configPath)) return "未找到 Codex 配置";
            return null;
        }

        internal static bool IsEnabled(string text)
        {
            bool found;
            RemoveSection(text, out found);
            return found;
        }

        public static bool Update(string configPath, string scriptPath, string nodePath, bool enable)
        {
            if (enable && EnableProblem(configPath, scriptPath, nodePath) != null) return false;
            if (!File.Exists(configPath)) return !enable;

            using (var mutex = new Mutex(false, @"Local\CodexInputUsage.CompanionConfig"))
            {
                try { mutex.WaitOne(); }
                catch (AbandonedMutexException) { }
                try
                {
                    var encoding = new UTF8Encoding(false, true);
                    string content = File.ReadAllText(configPath, encoding);
                    bool found;
                    string next = RemoveSection(content, out found);
                    if (enable)
                    {
                        string eol = content.Contains("\r\n") ? "\r\n" : "\n";
                        if (next.Length > 0 && !next.EndsWith("\n", StringComparison.Ordinal)) next += eol;
                        next += Table + eol + "command = " + TomlString(nodePath) + eol
                            + "args = [" + TomlString(scriptPath) + "]" + eol;
                    }
                    if (next == content) return true;

                    string dir = Path.GetDirectoryName(Path.GetFullPath(configPath));
                    string temp = Path.Combine(dir, ".inputusage-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        File.WriteAllText(temp, next, encoding);
                        if (File.ReadAllText(configPath, encoding) != content)
                            throw new IOException("Codex configuration changed while writing.");
                        File.Replace(temp, configPath, null);
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                    return true;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static string TomlString(string path)
        {
            return "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string RemoveSection(string text, out bool found)
        {
            found = false;
            bool owned = false;
            char multiline = '\0';
            int arrayDepth = 0;
            var result = new StringBuilder(text.Length);

            for (int start = 0; start < text.Length;)
            {
                int end = text.IndexOf('\n', start);
                end = end < 0 ? text.Length : end + 1;
                string line = text.Substring(start, end - start);
                string trimmed = line.TrimStart(' ', '\t');
                bool valueContinues = multiline != '\0' || arrayDepth > 0;
                if (!valueContinues && trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    owned = OwnedTable.IsMatch(trimmed);
                    found |= owned;
                    if (!owned) result.Append(line);
                }
                else
                {
                    if (!owned || (!valueContinues && (string.IsNullOrWhiteSpace(line) || trimmed.StartsWith("#", StringComparison.Ordinal))))
                        result.Append(line);
                    ReadValueState(line, ref multiline, ref arrayDepth);
                }
                start = end;
            }
            return result.ToString();
        }

        private static void ReadValueState(string line, ref char multiline, ref int arrayDepth)
        {
            char quote = multiline;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (quote == '"' && c == '\\') { i++; continue; }
                    if (c != quote) continue;
                    if (multiline != '\0')
                    {
                        if (i + 2 >= line.Length || line[i + 1] != quote || line[i + 2] != quote) continue;
                        i += 2;
                        while (i + 1 < line.Length && line[i + 1] == quote) i++;
                        multiline = '\0';
                    }
                    quote = '\0';
                }
                else if (c == '#')
                {
                    break;
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                    if (i + 2 < line.Length && line[i + 1] == c && line[i + 2] == c)
                    {
                        multiline = c;
                        i += 2;
                    }
                }
                else if (c == '[') arrayDepth++;
                else if (c == ']' && arrayDepth > 0) arrayDepth--;
            }
        }
    }
}
