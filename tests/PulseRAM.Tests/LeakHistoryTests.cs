using System;
using System.Collections.Generic;
using PulseRAM.Core;
using Xunit;

namespace PulseRAM.Tests
{
    public class LeakHistoryTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 9, 0, 0);
        private static readonly HashSet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void Notifies_first_time_then_waits_an_hour()
        {
            var h = new LeakHistory();
            Assert.True(h.Record("chrome", 150, 900, T0, None));
            Assert.False(h.Record("chrome", 160, 1000, T0.AddMinutes(5), None));
            Assert.False(h.Record("chrome", 160, 1000, T0.AddMinutes(59), None));
            Assert.True(h.Record("chrome", 160, 1000, T0.AddMinutes(61), None));
        }

        [Fact]
        public void Tracks_peak_latest_and_episodes()
        {
            var h = new LeakHistory();
            h.Record("app", 200, 500, T0, None);
            h.Record("app", 120, 700, T0.AddMinutes(2), None);
            h.Record("app", 130, 900, T0.AddMinutes(30), None); // > 10 min gap → new episode
            var inc = Assert.Single(h.Snapshot());
            Assert.Equal(200, inc.PeakRateMbPerMin);
            Assert.Equal(130, inc.LastRateMbPerMin);
            Assert.Equal(900, inc.PrivateMb);
            Assert.Equal(2, inc.Episodes);
            Assert.Equal(T0, inc.FirstSeen);
        }

        [Fact]
        public void Ignored_apps_are_never_recorded()
        {
            var h = new LeakHistory();
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Teams" };
            Assert.False(h.Record("teams", 300, 2000, T0, ignored));
            Assert.Equal(0, h.Count);
        }

        [Fact]
        public void Dismissed_app_stays_hidden_while_it_keeps_leaking()
        {
            var h = new LeakHistory();
            Assert.True(h.Record("app", 150, 900, T0, None));
            Assert.True(h.Dismiss("app", T0.AddMinutes(1)));
            for (int m = 2; m < 120; m += 2)          // still leaking every couple of minutes
                Assert.False(h.Record("app", 150, 900, T0.AddMinutes(m), None));
            Assert.Equal(0, h.Count);
        }

        [Fact]
        public void Dismissed_app_returns_after_a_new_episode()
        {
            var h = new LeakHistory();
            Assert.True(h.Record("app", 150, 900, T0, None));
            h.Dismiss("app", T0.AddMinutes(1));
            Assert.False(h.Record("app", 150, 900, T0.AddMinutes(5), None));
            // quiet for > 10 minutes, then leaking again 2 h later → listed and notified
            Assert.True(h.Record("app", 150, 900, T0.AddMinutes(125), None));
            Assert.Equal(1, h.Count);
        }

        [Fact]
        public void New_episode_soon_after_dismiss_is_listed_but_not_renotified()
        {
            var h = new LeakHistory();
            Assert.True(h.Record("app", 150, 900, T0, None));
            h.Dismiss("app", T0.AddMinutes(1));
            Assert.False(h.Record("app", 150, 900, T0.AddMinutes(20), None)); // new episode, but notified < 1 h ago
            Assert.Equal(1, h.Count);
        }

        [Fact]
        public void Forget_clears_everything()
        {
            var h = new LeakHistory();
            Assert.True(h.Record("app", 150, 900, T0, None));
            h.Dismiss("app", T0);
            Assert.False(h.Forget("app"));
            Assert.False(h.IsDismissed("app"));
            Assert.True(h.Record("app", 150, 900, T0.AddMinutes(1), None));
        }

        [Fact]
        public void Keeps_at_most_twenty_newest()
        {
            var h = new LeakHistory();
            for (int i = 0; i < 25; i++) h.Record($"app{i}", 150, 900, T0.AddMinutes(i), None);
            var list = h.Snapshot();
            Assert.Equal(LeakHistory.MaxEntries, list.Count);
            Assert.Equal("app24", list[0].App);              // newest first
            Assert.DoesNotContain(list, x => x.App == "app0"); // oldest dropped
        }

        [Fact]
        public void Snapshot_is_a_copy()
        {
            var h = new LeakHistory();
            h.Record("app", 150, 900, T0, None);
            h.Snapshot()[0].PrivateMb = 1;
            Assert.Equal(900, h.Snapshot()[0].PrivateMb);
        }
    }
}
