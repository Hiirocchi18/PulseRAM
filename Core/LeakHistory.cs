using System;
using System.Collections.Generic;
using System.Linq;

namespace PulseRAM.Core
{
    public sealed class LeakIncident
    {
        public string App { get; set; } = "";
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public double PeakRateMbPerMin { get; set; }
        public double LastRateMbPerMin { get; set; }
        public double PrivateMb { get; set; }
        /// <summary>Separate growth episodes (a gap of more than 10 minutes starts a new one).</summary>
        public int Episodes { get; set; } = 1;
    }

    /// <summary>
    /// Remembers apps whose private memory kept growing, so the user can see them after the fact.
    /// Decides when to notify: the first time an app is seen, and again only after an hour without a new notice.
    /// Not thread-safe; the engine calls it under its own lock.
    /// </summary>
    public sealed class LeakHistory
    {
        public static readonly TimeSpan EpisodeGap = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan RenotifyAfter = TimeSpan.FromHours(1);
        public const int MaxEntries = 20;

        private readonly Dictionary<string, LeakIncident> _byApp = new Dictionary<string, LeakIncident>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastNotified = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // Dismissed apps stay hidden while they keep leaking; they come back only after a fresh episode
        // (no growth for more than EpisodeGap, then growth again). Value = last time it was seen leaking.
        private readonly Dictionary<string, DateTime> _dismissed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records a sample of a leaking app. Returns true when the user should be told about it now.</summary>
        public bool Record(string app, double rateMbPerMin, double privateMb, DateTime now, ICollection<string> ignored)
        {
            if (string.IsNullOrWhiteSpace(app) || ignored.Contains(app)) return false;

            if (_dismissed.TryGetValue(app, out var lastDismissedSeen))
            {
                if (now - lastDismissedSeen <= EpisodeGap)
                {
                    _dismissed[app] = now;
                    return false;
                }
                _dismissed.Remove(app); // it stopped growing for a while and started again: a new episode
            }

            if (_byApp.TryGetValue(app, out var inc))
            {
                if (now - inc.LastSeen > EpisodeGap) inc.Episodes++;
                inc.LastSeen = now;
                inc.LastRateMbPerMin = rateMbPerMin;
                inc.PeakRateMbPerMin = Math.Max(inc.PeakRateMbPerMin, rateMbPerMin);
                inc.PrivateMb = privateMb;
            }
            else
            {
                _byApp[app] = new LeakIncident
                {
                    App = app, FirstSeen = now, LastSeen = now,
                    PeakRateMbPerMin = rateMbPerMin, LastRateMbPerMin = rateMbPerMin, PrivateMb = privateMb
                };
                if (_byApp.Count > MaxEntries)
                {
                    var oldest = _byApp.Values.OrderBy(i => i.LastSeen).First();
                    _byApp.Remove(oldest.App);
                    _lastNotified.Remove(oldest.App);
                }
            }

            if (_lastNotified.TryGetValue(app, out var last) && now - last < RenotifyAfter) return false;
            _lastNotified[app] = now;
            return true;
        }

        /// <summary>Most recent first.</summary>
        public List<LeakIncident> Snapshot() =>
            _byApp.Values.OrderByDescending(i => i.LastSeen)
                .Select(i => new LeakIncident
                {
                    App = i.App, FirstSeen = i.FirstSeen, LastSeen = i.LastSeen, PeakRateMbPerMin = i.PeakRateMbPerMin,
                    LastRateMbPerMin = i.LastRateMbPerMin, PrivateMb = i.PrivateMb, Episodes = i.Episodes
                }).ToList();

        /// <summary>
        /// Hides an app from the list until it starts a new growth episode. It does not notify again within
        /// <see cref="RenotifyAfter"/> of the last notice either way.
        /// </summary>
        public bool Dismiss(string app, DateTime now)
        {
            _dismissed[app] = now;
            return _byApp.Remove(app);
        }

        /// <summary>Forgets an app completely (used when the user chooses Ignore, which also adds it to the ignore list).</summary>
        public bool Forget(string app)
        {
            _dismissed.Remove(app);
            _lastNotified.Remove(app);
            return _byApp.Remove(app);
        }

        public bool IsDismissed(string app) => _dismissed.ContainsKey(app);

        public int Count => _byApp.Count;
    }
}
