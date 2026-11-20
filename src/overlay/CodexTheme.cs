using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CodexInputUsage
{
    internal enum CodexThemeKind
    {
        Light = 0,
        Dark = 1
    }

    internal enum MetricTier
    {
        Muted = 0,
        Good = 1,
        Info = 2,
        Warn = 3,
        Danger = 4
    }

    internal sealed class ThemePalette
    {
        internal CodexThemeKind Kind;
        internal Color StripText;
        internal Color StripTextSecondary;
        internal Color StripHoverFill;
        internal Color StripHoverBorder;

        internal Color SurfaceBg;
        internal Color CardBg;
        internal Color RowAltBg;
        internal Color RowHoverBg;
        internal Color TrackBg;
        internal Color Border;
        internal Color BorderStrong;
        internal Color TextPrimary;
        internal Color TextSecondary;
        internal Color TextMuted;

        internal Color AccentBlue;
        internal Color AccentSpeed;
        internal Color TierGood;
        internal Color TierInfo;
        internal Color TierWarn;
        internal Color TierDanger;
        internal Color TierMuted;

        // 10-channel context window palette (matches Antigravity usage-monitor tokens.css)
        internal Color CtxAssistant;
        internal Color CtxToolResults;
        internal Color CtxSystemPrompt;
        internal Color CtxSystemTools;
        internal Color CtxUserInput;
        internal Color CtxSkills;
        internal Color CtxUserRules;
        internal Color CtxMcpTools;
        internal Color CtxSubagentMsg;
        internal Color CtxCheckpoint;

        internal static readonly ThemePalette Light = new ThemePalette
        {
            Kind = CodexThemeKind.Light,
            // Matches Codex Light composer toolbar native text (#525252 / #404040)
            StripText = Color.FromArgb(245, 58, 62, 68),
            StripTextSecondary = Color.FromArgb(220, 108, 114, 124),
            StripHoverFill = Color.FromArgb(20, 15, 23, 42),
            StripHoverBorder = Color.FromArgb(38, 15, 23, 42),

            // Matches Antigravity usage-monitor light tokens
            SurfaceBg = Color.FromArgb(255, 255, 255, 255),
            CardBg = Color.FromArgb(255, 248, 249, 251),
            RowAltBg = Color.FromArgb(255, 250, 251, 253),
            RowHoverBg = Color.FromArgb(255, 241, 245, 249),
            TrackBg = Color.FromArgb(255, 229, 233, 240),
            Border = Color.FromArgb(255, 226, 230, 236),
            BorderStrong = Color.FromArgb(255, 203, 210, 220),
            TextPrimary = Color.FromArgb(255, 30, 37, 48),
            TextSecondary = Color.FromArgb(255, 98, 110, 126),
            TextMuted = Color.FromArgb(255, 138, 149, 165),

            AccentBlue = Color.FromArgb(255, 37, 99, 235),
            AccentSpeed = Color.FromArgb(255, 226, 88, 40),
            TierGood = Color.FromArgb(255, 38, 172, 94),
            TierInfo = Color.FromArgb(255, 21, 152, 214),
            TierWarn = Color.FromArgb(255, 217, 132, 14),
            TierDanger = Color.FromArgb(255, 224, 62, 52),
            TierMuted = Color.FromArgb(255, 138, 149, 165),

            CtxAssistant = Color.FromArgb(255, 56, 117, 209),
            CtxToolResults = Color.FromArgb(255, 32, 172, 152),
            CtxSystemPrompt = Color.FromArgb(255, 108, 130, 164),
            CtxSystemTools = Color.FromArgb(255, 237, 125, 43),
            CtxUserInput = Color.FromArgb(255, 124, 77, 255),
            CtxSkills = Color.FromArgb(255, 42, 168, 103),
            CtxUserRules = Color.FromArgb(255, 229, 62, 82),
            CtxMcpTools = Color.FromArgb(255, 224, 71, 133),
            CtxSubagentMsg = Color.FromArgb(255, 185, 87, 217),
            CtxCheckpoint = Color.FromArgb(255, 238, 173, 14)
        };

        internal static readonly ThemePalette Dark = new ThemePalette
        {
            Kind = CodexThemeKind.Dark,
            // Matches Codex Dark composer toolbar native text
            StripText = Color.FromArgb(238, 214, 218, 226),
            StripTextSecondary = Color.FromArgb(205, 154, 162, 174),
            StripHoverFill = Color.FromArgb(28, 255, 255, 255),
            StripHoverBorder = Color.FromArgb(52, 255, 255, 255),

            // Matches Antigravity usage-monitor dark tokens
            SurfaceBg = Color.FromArgb(255, 28, 31, 38),
            CardBg = Color.FromArgb(255, 36, 40, 49),
            RowAltBg = Color.FromArgb(255, 32, 36, 44),
            RowHoverBg = Color.FromArgb(255, 44, 49, 60),
            TrackBg = Color.FromArgb(255, 52, 58, 70),
            Border = Color.FromArgb(255, 54, 60, 72),
            BorderStrong = Color.FromArgb(255, 72, 80, 96),
            TextPrimary = Color.FromArgb(255, 236, 239, 244),
            TextSecondary = Color.FromArgb(255, 154, 164, 178),
            TextMuted = Color.FromArgb(255, 112, 122, 138),

            AccentBlue = Color.FromArgb(255, 96, 165, 250),
            AccentSpeed = Color.FromArgb(255, 251, 146, 60),
            TierGood = Color.FromArgb(255, 74, 222, 128),
            TierInfo = Color.FromArgb(255, 56, 189, 248),
            TierWarn = Color.FromArgb(255, 251, 191, 36),
            TierDanger = Color.FromArgb(255, 248, 113, 113),
            TierMuted = Color.FromArgb(255, 118, 128, 144),

            CtxAssistant = Color.FromArgb(255, 118, 143, 179),
            CtxToolResults = Color.FromArgb(255, 52, 211, 180),
            CtxSystemPrompt = Color.FromArgb(255, 152, 169, 196),
            CtxSystemTools = Color.FromArgb(255, 245, 146, 71),
            CtxUserInput = Color.FromArgb(255, 165, 133, 250),
            CtxSkills = Color.FromArgb(255, 69, 208, 136),
            CtxUserRules = Color.FromArgb(255, 239, 98, 115),
            CtxMcpTools = Color.FromArgb(255, 238, 112, 163),
            CtxSubagentMsg = Color.FromArgb(255, 206, 131, 230),
            CtxCheckpoint = Color.FromArgb(255, 248, 196, 54)
        };

        internal static ThemePalette For(CodexThemeKind kind)
        {
            return kind == CodexThemeKind.Dark ? Dark : Light;
        }

        internal Color ForTier(MetricTier tier)
        {
            switch (tier)
            {
                case MetricTier.Good: return TierGood;
                case MetricTier.Info: return TierInfo;
                case MetricTier.Warn: return TierWarn;
                case MetricTier.Danger: return TierDanger;
                default: return TierMuted;
            }
        }

        internal Color ForContextChannel(string key)
        {
            switch (key)
            {
                case "assistant": return CtxAssistant;
                case "toolResults": return CtxToolResults;
                case "systemPrompt": return CtxSystemPrompt;
                case "systemTools": return CtxSystemTools;
                case "userInput": return CtxUserInput;
                case "skills": return CtxSkills;
                case "userRules": return CtxUserRules;
                case "mcpTools": return CtxMcpTools;
                case "subagentMsg": return CtxSubagentMsg;
                case "checkpoint": return CtxCheckpoint;
                default: return TextMuted;
            }
        }
    }

    internal static class CodexThemeSource
    {
        private static readonly Regex AppearancePattern = new Regex(
            "^\\s*appearanceTheme\\s*=\\s*[\"']?(?<value>[A-Za-z_\\-]+)[\"']?\\s*(?:#.*)?$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static DateTime _lastReadUtc = DateTime.MinValue;
        private static CodexThemeKind _cached = CodexThemeKind.Light;

        internal static string GetCodexHome()
        {
            string root = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(root)) return root;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        internal static CodexThemeKind Current
        {
            get
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _lastReadUtc).TotalSeconds < 5.0) return _cached;
                _lastReadUtc = now;
                _cached = Detect();
                return _cached;
            }
        }

        internal static CodexThemeKind Detect()
        {
            string configPath = Path.Combine(GetCodexHome(), "config.toml");
            try
            {
                if (File.Exists(configPath))
                {
                    string[] lines = File.ReadAllLines(configPath, Encoding.UTF8);
                    string table = string.Empty;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string trimmed = lines[i].TrimStart();
                        if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                        if (trimmed[0] == '[')
                        {
                            table = trimmed.Trim('[', ']', ' ', '\t').ToLowerInvariant();
                            continue;
                        }
                        if (table.Length != 0 && table != "desktop" && table != "desktop.appearance") continue;
                        Match m = AppearancePattern.Match(lines[i]);
                        if (!m.Success) continue;
                        string val = m.Groups["value"].Value.ToLowerInvariant();
                        if (val == "light") return CodexThemeKind.Light;
                        if (val == "dark") return CodexThemeKind.Dark;
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AppsUseLightTheme");
                        if (val is int) return ((int)val) == 0 ? CodexThemeKind.Dark : CodexThemeKind.Light;
                    }
                }
            }
            catch { }

            return CodexThemeKind.Light;
        }
    }
}
