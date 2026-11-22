using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexInputUsage
{
    internal sealed class StripSlotPreferences
    {
        internal bool ShowTps = true;
        internal bool ShowLatestTurn = true;
        internal bool ShowSessionTotal = true;
        internal bool ShowCache = true;
        internal bool ShowQuota5h = false;

        internal bool AnyEnabled()
        {
            return ShowTps || ShowLatestTurn || ShowSessionTotal || ShowCache || ShowQuota5h;
        }

        internal StripSlotPreferences Clone()
        {
            return new StripSlotPreferences
            {
                ShowTps = ShowTps,
                ShowLatestTurn = ShowLatestTurn,
                ShowSessionTotal = ShowSessionTotal,
                ShowCache = ShowCache,
                ShowQuota5h = ShowQuota5h
            };
        }

        internal string Signature()
        {
            return (ShowTps ? "1" : "0")
                + (ShowLatestTurn ? "1" : "0")
                + (ShowSessionTotal ? "1" : "0")
                + (ShowCache ? "1" : "0")
                + (ShowQuota5h ? "1" : "0");
        }

        internal bool Toggle(string slotKey)
        {
            if (string.Equals(slotKey, "reset", StringComparison.OrdinalIgnoreCase))
            {
                ShowTps = true;
                ShowLatestTurn = true;
                ShowSessionTotal = true;
                ShowCache = true;
                ShowQuota5h = false;
                return true;
            }

            StripSlotPreferences next = Clone();
            if (string.Equals(slotKey, "tps", StringComparison.OrdinalIgnoreCase)) next.ShowTps = !next.ShowTps;
            else if (string.Equals(slotKey, "turn", StringComparison.OrdinalIgnoreCase)) next.ShowLatestTurn = !next.ShowLatestTurn;
            else if (string.Equals(slotKey, "total", StringComparison.OrdinalIgnoreCase)) next.ShowSessionTotal = !next.ShowSessionTotal;
            else if (string.Equals(slotKey, "cache", StringComparison.OrdinalIgnoreCase)) next.ShowCache = !next.ShowCache;
            else if (string.Equals(slotKey, "quota5h", StringComparison.OrdinalIgnoreCase)) next.ShowQuota5h = !next.ShowQuota5h;
            else return false;

            if (!next.AnyEnabled()) return false;

            ShowTps = next.ShowTps;
            ShowLatestTurn = next.ShowLatestTurn;
            ShowSessionTotal = next.ShowSessionTotal;
            ShowCache = next.ShowCache;
            ShowQuota5h = next.ShowQuota5h;
            return true;
        }
    }

    internal static class OverlaySettingsStore
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal static string GetSettingsPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(Path.Combine(localAppData, "CodexInputUsage"), "settings.json");
        }

        internal static StripSlotPreferences Load()
        {
            return LoadFromPath(GetSettingsPath());
        }

        internal static StripSlotPreferences LoadFromPath(string path)
        {
            var prefs = new StripSlotPreferences();
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return prefs;

                string text = File.ReadAllText(path, Encoding.UTF8);
                var root = Json.DeserializeObject(text) as Dictionary<string, object>;
                if (root == null) return prefs;

                object slotsObj;
                var slots = root.TryGetValue("stripSlots", out slotsObj) ? slotsObj as Dictionary<string, object> : root;
                if (slots == null) return prefs;

                prefs.ShowTps = ReadBool(slots, "tps", prefs.ShowTps);
                prefs.ShowLatestTurn = ReadBool(slots, "turn", prefs.ShowLatestTurn);
                prefs.ShowSessionTotal = ReadBool(slots, "total", prefs.ShowSessionTotal);
                prefs.ShowCache = ReadBool(slots, "cache", prefs.ShowCache);
                prefs.ShowQuota5h = ReadBool(slots, "quota5h", prefs.ShowQuota5h);

                if (!prefs.AnyEnabled())
                    return new StripSlotPreferences();
            }
            catch { }
            return prefs;
        }

        internal static bool Save(StripSlotPreferences prefs)
        {
            return SaveToPath(GetSettingsPath(), prefs);
        }

        internal static bool SaveToPath(string path, StripSlotPreferences prefs)
        {
            if (prefs == null || !prefs.AnyEnabled() || string.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                string json = string.Format(
                    "{{\r\n  \"version\": 1,\r\n  \"stripSlots\": {{\r\n    \"tps\": {0},\r\n    \"turn\": {1},\r\n    \"total\": {2},\r\n    \"cache\": {3},\r\n    \"quota5h\": {4}\r\n  }}\r\n}}\r\n",
                    prefs.ShowTps ? "true" : "false",
                    prefs.ShowLatestTurn ? "true" : "false",
                    prefs.ShowSessionTotal ? "true" : "false",
                    prefs.ShowCache ? "true" : "false",
                    prefs.ShowQuota5h ? "true" : "false");

                File.WriteAllText(path, json, new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadBool(Dictionary<string, object> dict, string key, bool fallback)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v) && v is bool)
                return (bool)v;
            return fallback;
        }
    }
}
