using System;
using PulseRAM.Core;
using Xunit;

namespace PulseRAM.Tests
{
    public class TierTests
    {
        [Theory]
        [InlineData(4.0, 8, Tier.Low)]     // 4 GB laptop
        [InlineData(6.9, 8, Tier.Low)]
        [InlineData(16.0, 2, Tier.Low)]    // plenty of RAM but a dual-thread CPU
        [InlineData(7.9, 8, Tier.Mid)]     // "8 GB" reports ~7.9
        [InlineData(15.9, 8, Tier.High)]   // "16 GB" reports ~15.9
        [InlineData(14.9, 8, Tier.Mid)]
        [InlineData(32.0, 4, Tier.Mid)]    // quad-thread CPU stays mid
        [InlineData(32.0, 16, Tier.High)]
        public void Classifies_hardware(double ram, int cores, Tier expected) =>
            Assert.Equal(expected, MemoryPolicy.ClassifyTier(ram, cores));

        [Fact]
        public void Low_end_gets_higher_trigger_and_longer_cooldown()
        {
            var low = MemoryPolicy.TuningFor(Tier.Low);
            var high = MemoryPolicy.TuningFor(Tier.High);
            Assert.True(low.TriggerPercent > high.TriggerPercent);
            Assert.True(low.CooldownSeconds > high.CooldownSeconds);
            Assert.True(low.MinFreeGb < high.MinFreeGb);
        }

        [Theory]
        [InlineData(68, Tier.High, 68)]
        [InlineData(68, Tier.Mid, 73)]
        [InlineData(68, Tier.Low, 78)]
        [InlineData(88, Tier.Low, 92)]   // capped
        [InlineData(40, Tier.High, 50)]  // floored
        public void Profile_triggers_are_bumped_and_clamped(int trigger, Tier tier, int expected) =>
            Assert.Equal(expected, MemoryPolicy.BumpProfileTrigger(trigger, tier));
    }

    public class StandbyPurgeTests
    {
        [Fact]
        public void Not_worthwhile_when_free_memory_is_plentiful() =>
            Assert.False(MemoryPolicy.StandbyPurgeWorthwhile(totalGb: 16, standbyGb: 6, freeGb: 4, repurposeMbPerMin: 0));

        [Fact]
        public void Worthwhile_when_free_memory_is_scarce_and_cache_is_large() =>
            Assert.True(MemoryPolicy.StandbyPurgeWorthwhile(16, 6, 0.3, 0));

        [Fact]
        public void Worthwhile_when_windows_is_already_evicting_cache() =>
            Assert.True(MemoryPolicy.StandbyPurgeWorthwhile(16, 6, 3, 64));

        [Fact]
        public void Not_worthwhile_when_cache_is_small_even_under_pressure() =>
            Assert.False(MemoryPolicy.StandbyPurgeWorthwhile(16, 1.0, 0.2, 500));

        [Fact]
        public void Small_pc_uses_half_gigabyte_minimums() =>
            Assert.True(MemoryPolicy.StandbyPurgeWorthwhile(4, 0.5, 0.49, 0));
    }

    public class CommitLevelTests
    {
        [Theory]
        [InlineData("ok", 89.9, "ok")]
        [InlineData("ok", 90, "high")]
        [InlineData("ok", 96, "critical")]
        [InlineData("high", 86, "high")]     // hysteresis: stays high until below 85
        [InlineData("high", 84.9, "ok")]
        [InlineData("high", 96, "critical")]
        [InlineData("critical", 93, "critical")]
        [InlineData("critical", 92.9, "high")]
        [InlineData("critical", 80, "ok")]
        public void Moves_between_levels_with_hysteresis(string prev, double pct, string expected) =>
            Assert.Equal(expected, MemoryPolicy.NextCommitLevel(prev, pct));
    }

    public class BackoffTests
    {
        [Theory]
        [InlineData(120, 0, 120)]
        [InlineData(120, 1, 240)]
        [InlineData(120, 3, 960)]
        [InlineData(120, 4, 1800)]   // capped at 30 min
        [InlineData(10, 0, 30)]      // at least 30 s
        [InlineData(300, -1, 300)]
        public void Cooldown_doubles_and_caps(int baseSec, int level, int expected) =>
            Assert.Equal(expected, MemoryPolicy.BackoffCooldownSeconds(baseSec, level));
    }

    public class HotkeyTests
    {
        [Fact]
        public void Parses_default_hotkey()
        {
            Assert.True(HotkeyParser.TryParse("Ctrl+Alt+Shift+M", out uint mods, out uint vk, out string label));
            Assert.Equal(HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_ALT | HotkeyParser.MOD_SHIFT, mods);
            Assert.Equal((uint)'M', vk);
            Assert.Equal("Ctrl + Alt + Shift + M", label);
        }

        [Fact]
        public void Parses_function_keys_and_win()
        {
            Assert.True(HotkeyParser.TryParse("ctrl + win + f9", out uint mods, out uint vk, out string label));
            Assert.Equal(HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_WIN, mods);
            Assert.Equal(0x78u, vk); // VK_F9
            Assert.Equal("Ctrl + Win + F9", label);
        }

        [Theory]
        [InlineData("")]
        [InlineData("M")]               // no modifier
        [InlineData("Ctrl+Hyper+M")]    // unknown modifier
        [InlineData("Ctrl+F25")]
        [InlineData("Ctrl+Space")]
        [InlineData(null)]
        public void Rejects_invalid(string? text)
        {
            Assert.False(HotkeyParser.TryParse(text, out uint mods, out _, out _));
            Assert.Equal(0u, mods);
        }
    }

    public class VersionTests
    {
        [Theory]
        [InlineData("v1.4.0", "1.4.0")]
        [InlineData("1.4", "1.4.0")]
        [InlineData("v2", "2.0.0")]
        [InlineData("v1.4.1-beta.2", "1.4.1")]
        public void Parses_tags(string tag, string expected)
        {
            Assert.True(VersionInfo.TryParseTag(tag, out var v));
            Assert.Equal(Version.Parse(expected), v);
        }

        [Theory]
        [InlineData("v1.4.1", "1.4.0", true)]
        [InlineData("v1.4.0", "1.4.0", false)]
        [InlineData("v1.3.9", "1.4.0", false)]
        [InlineData("v2.0.0", "1.9.9", true)]
        [InlineData("v1.5.0-beta", "1.4.0", false)]   // pre-releases are never offered
        [InlineData("latest", "1.4.0", false)]
        [InlineData("", "1.4.0", false)]
        public void Offers_only_newer_stable_releases(string tag, string current, bool expected) =>
            Assert.Equal(expected, VersionInfo.IsNewer(tag, Version.Parse(current)));

        [Fact]
        public void Four_part_assembly_version_compares_as_three() =>
            Assert.False(VersionInfo.IsNewer("v1.4.0", new Version(1, 4, 0, 0)));
    }
}
