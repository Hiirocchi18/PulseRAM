using System;
using System.Collections.Generic;

namespace PulseRAM.Core
{
    public enum Tier { Low, Mid, High }

    /// <summary>Thresholds tuned per hardware tier (see README "Built for any PC").</summary>
    public readonly record struct TierTuning(int TriggerPercent, double MinFreeGb, int CooldownSeconds, double MinProcessWorkingSetMb, int ProfileBump);

    /// <summary>
    /// Pure decision logic shared by the engine and the unit tests. Nothing here touches Windows,
    /// so it can be tested on any OS.
    /// </summary>
    public static class MemoryPolicy
    {
        /// <summary>Low-end: under ~7 GB RAM or ≤ 2 threads. Mid-range: under ~15 GB or ≤ 4 threads. High-end: the rest.</summary>
        public static Tier ClassifyTier(double ramGb, int logicalCores)
        {
            if (ramGb < 7.0 || logicalCores <= 2) return Tier.Low;
            if (ramGb < 15.0 || logicalCores <= 4) return Tier.Mid;
            return Tier.High;
        }

        public static TierTuning TuningFor(Tier tier) => tier switch
        {
            Tier.Low => new TierTuning(88, 0.5, 300, 30.0, 10),
            Tier.Mid => new TierTuning(85, 0.75, 180, 20.0, 5),
            _ => new TierTuning(80, 1.5, 120, 15.0, 0)
        };

        /// <summary>Profile triggers are raised by the tier's bump and kept in 50–92%.</summary>
        public static int BumpProfileTrigger(int trigger, Tier tier) => Math.Clamp(trigger + TuningFor(tier).ProfileBump, 50, 92);

        /// <summary>
        /// Purging the standby cache only helps when free memory is actually scarce (or Windows is already
        /// evicting cache); otherwise it throws away useful cache. The cache must also be big enough to matter.
        /// </summary>
        public static bool StandbyPurgeWorthwhile(double totalGb, double standbyGb, double freeGb, double repurposeMbPerMin)
        {
            double lowFreeGb = Math.Max(0.5, totalGb * 0.04);
            bool cacheIsLarge = standbyGb >= Math.Max(0.5, totalGb * 0.10);
            return cacheIsLarge && (freeGb < lowFreeGb || repurposeMbPerMin >= 64.0);
        }

        /// <summary>Commit-charge warning level with hysteresis: enter high ≥ 90, critical ≥ 96; leave critical below 93, high below 85.</summary>
        public static string NextCommitLevel(string previous, double commitPercent) => previous switch
        {
            "critical" => commitPercent >= 93 ? "critical" : (commitPercent >= 85 ? "high" : "ok"),
            "high" => commitPercent >= 96 ? "critical" : (commitPercent >= 85 ? "high" : "ok"),
            _ => commitPercent >= 96 ? "critical" : (commitPercent >= 90 ? "high" : "ok")
        };

        /// <summary>Auto-Pilot cooldown doubles per back-off level (base at least 30 s), capped at 30 minutes.</summary>
        public static int BackoffCooldownSeconds(int baseSeconds, int level)
        {
            double cd = Math.Max(30, baseSeconds) * Math.Pow(2, Math.Clamp(level, 0, 10));
            return (int)Math.Min(cd, 1800);
        }

        public const int MaxBackoffLevel = 6;
    }

    /// <summary>Parses "Ctrl+Alt+Shift+M" style hotkeys (modifiers Ctrl/Control, Alt, Shift, Win; key A–Z, 0–9, F1–F24).</summary>
    public static class HotkeyParser
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        public static bool TryParse(string? text, out uint modifiers, out uint vk, out string label)
        {
            modifiers = 0;
            vk = 0;
            label = "";
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                    case "alt": modifiers |= MOD_ALT; break;
                    case "shift": modifiers |= MOD_SHIFT; break;
                    case "win": case "windows": modifiers |= MOD_WIN; break;
                    default: modifiers = 0; return false;
                }
            }
            var labels = new List<string>();
            if ((modifiers & MOD_CONTROL) != 0) labels.Add("Ctrl");
            if ((modifiers & MOD_ALT) != 0) labels.Add("Alt");
            if ((modifiers & MOD_SHIFT) != 0) labels.Add("Shift");
            if ((modifiers & MOD_WIN) != 0) labels.Add("Win");

            string key = parts[^1].ToUpperInvariant();
            if (key.Length == 1 && ((key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9')))
                vk = key[0];
            else if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.Substring(1), out int fn) && fn >= 1 && fn <= 24)
                vk = (uint)(0x70 + fn - 1); // VK_F1..VK_F24
            else
            {
                modifiers = 0;
                return false;
            }
            labels.Add(key);
            label = string.Join(" + ", labels);
            return true;
        }
    }

    /// <summary>Version comparison for the update check ("v1.4.0", "1.4", "1.4.0-beta" …).</summary>
    public static class VersionInfo
    {
        /// <summary>Parses a release tag; pre-release suffixes ("-beta") and build metadata are ignored. Returns false if not a version.</summary>
        public static bool TryParseTag(string? tag, out Version version)
        {
            version = new Version(0, 0);
            if (string.IsNullOrWhiteSpace(tag)) return false;
            string t = tag.Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);
            int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) t = t.Substring(0, cut);
            if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) return false;
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }

        public static bool IsPreRelease(string? tag) => tag != null && tag.Contains('-');

        /// <summary>True if <paramref name="tag"/> is a stable release newer than <paramref name="current"/>.</summary>
        public static bool IsNewer(string? tag, Version current)
        {
            if (IsPreRelease(tag) || !TryParseTag(tag, out var latest)) return false;
            var cur = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            return latest > cur;
        }
    }
}
