//! Remembers apps whose private memory kept growing, so the user can see them after the fact.

use std::collections::HashMap;

/// Times are Unix seconds.
#[derive(Clone, Debug, PartialEq)]
pub struct LeakIncident {
    pub app: String,
    pub first_seen: i64,
    pub last_seen: i64,
    pub peak_rate_mb_per_min: f64,
    pub last_rate_mb_per_min: f64,
    pub private_mb: f64,
    /// Separate growth episodes (a gap of more than 10 minutes starts a new one).
    pub episodes: u32,
}

pub const EPISODE_GAP_SECS: i64 = 10 * 60;
pub const RENOTIFY_AFTER_SECS: i64 = 60 * 60;
pub const MAX_ENTRIES: usize = 20;

/// Decides when to notify: the first time an app is seen, and again only after an hour without a new
/// notice. App names are compared case-insensitively. Not thread-safe; the engine calls it under its lock.
#[derive(Default)]
pub struct LeakHistory {
    by_app: HashMap<String, LeakIncident>,
    last_notified: HashMap<String, i64>,
    /// Dismissed apps stay hidden while they keep leaking; they come back only after a fresh episode
    /// (no growth for more than the episode gap, then growth again). Value = last time seen leaking.
    dismissed: HashMap<String, i64>,
}

fn key(app: &str) -> String {
    app.to_lowercase()
}

impl LeakHistory {
    /// Records a sample of a leaking app. Returns true when the user should be told about it now.
    /// `ignored` holds lower-case app names.
    pub fn record(&mut self, app: &str, rate_mb_per_min: f64, private_mb: f64, now: i64, ignored: &[String]) -> bool {
        let k = key(app);
        if app.trim().is_empty() || ignored.iter().any(|i| i.to_lowercase() == k) {
            return false;
        }

        if let Some(&last_seen) = self.dismissed.get(&k) {
            if now - last_seen <= EPISODE_GAP_SECS {
                self.dismissed.insert(k, now);
                return false;
            }
            self.dismissed.remove(&k); // it stopped growing for a while and started again: a new episode
        }

        if let Some(inc) = self.by_app.get_mut(&k) {
            if now - inc.last_seen > EPISODE_GAP_SECS {
                inc.episodes += 1;
            }
            inc.last_seen = now;
            inc.last_rate_mb_per_min = rate_mb_per_min;
            inc.peak_rate_mb_per_min = inc.peak_rate_mb_per_min.max(rate_mb_per_min);
            inc.private_mb = private_mb;
        } else {
            self.by_app.insert(
                k.clone(),
                LeakIncident {
                    app: app.to_string(),
                    first_seen: now,
                    last_seen: now,
                    peak_rate_mb_per_min: rate_mb_per_min,
                    last_rate_mb_per_min: rate_mb_per_min,
                    private_mb,
                    episodes: 1,
                },
            );
            if self.by_app.len() > MAX_ENTRIES {
                if let Some(oldest) = self.by_app.iter().min_by_key(|(_, i)| i.last_seen).map(|(k, _)| k.clone()) {
                    self.by_app.remove(&oldest);
                    self.last_notified.remove(&oldest);
                }
            }
        }

        if self.last_notified.get(&k).is_some_and(|&last| now - last < RENOTIFY_AFTER_SECS) {
            return false;
        }
        self.last_notified.insert(k, now);
        true
    }

    /// Most recent first.
    pub fn snapshot(&self) -> Vec<LeakIncident> {
        let mut list: Vec<LeakIncident> = self.by_app.values().cloned().collect();
        list.sort_by(|a, b| b.last_seen.cmp(&a.last_seen).then_with(|| a.app.cmp(&b.app)));
        list
    }

    pub fn get(&self, app: &str) -> Option<LeakIncident> {
        self.by_app.get(&key(app)).cloned()
    }

    /// Hides an app from the list until it starts a new growth episode. It does not notify again within
    /// the re-notify window of the last notice either way.
    pub fn dismiss(&mut self, app: &str, now: i64) -> bool {
        self.dismissed.insert(key(app), now);
        self.by_app.remove(&key(app)).is_some()
    }

    /// Forgets an app completely (used when the user chooses Ignore, or after the app was restarted).
    pub fn forget(&mut self, app: &str) -> bool {
        let k = key(app);
        self.dismissed.remove(&k);
        self.last_notified.remove(&k);
        self.by_app.remove(&k).is_some()
    }

    #[cfg(test)]
    pub fn is_dismissed(&self, app: &str) -> bool {
        self.dismissed.contains_key(&key(app))
    }

    #[cfg(test)]
    pub fn len(&self) -> usize {
        self.by_app.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const T0: i64 = 1_792_314_000;
    const MIN: i64 = 60;
    const NONE: &[String] = &[];

    #[test]
    fn notifies_first_time_then_waits_an_hour() {
        let mut h = LeakHistory::default();
        assert!(h.record("chrome", 150.0, 900.0, T0, NONE));
        assert!(!h.record("chrome", 160.0, 1000.0, T0 + 5 * MIN, NONE));
        assert!(!h.record("chrome", 160.0, 1000.0, T0 + 59 * MIN, NONE));
        assert!(h.record("chrome", 160.0, 1000.0, T0 + 61 * MIN, NONE));
    }

    #[test]
    fn tracks_peak_latest_and_episodes() {
        let mut h = LeakHistory::default();
        h.record("app", 200.0, 500.0, T0, NONE);
        h.record("app", 120.0, 700.0, T0 + 2 * MIN, NONE);
        h.record("app", 130.0, 900.0, T0 + 30 * MIN, NONE); // > 10 min gap → new episode
        let list = h.snapshot();
        assert_eq!(list.len(), 1);
        let inc = &list[0];
        assert_eq!(inc.peak_rate_mb_per_min, 200.0);
        assert_eq!(inc.last_rate_mb_per_min, 130.0);
        assert_eq!(inc.private_mb, 900.0);
        assert_eq!(inc.episodes, 2);
        assert_eq!(inc.first_seen, T0);
    }

    #[test]
    fn ignored_apps_are_never_recorded() {
        let mut h = LeakHistory::default();
        assert!(!h.record("teams", 300.0, 2000.0, T0, &["Teams".to_string()]));
        assert_eq!(h.len(), 0);
    }

    #[test]
    fn dismissed_app_stays_hidden_while_it_keeps_leaking() {
        let mut h = LeakHistory::default();
        assert!(h.record("app", 150.0, 900.0, T0, NONE));
        assert!(h.dismiss("app", T0 + MIN));
        for m in (2..120).step_by(2) {
            assert!(!h.record("app", 150.0, 900.0, T0 + m * MIN, NONE)); // still leaking every couple of minutes
        }
        assert_eq!(h.len(), 0);
    }

    #[test]
    fn dismissed_app_returns_after_a_new_episode() {
        let mut h = LeakHistory::default();
        assert!(h.record("app", 150.0, 900.0, T0, NONE));
        h.dismiss("app", T0 + MIN);
        assert!(!h.record("app", 150.0, 900.0, T0 + 5 * MIN, NONE));
        // quiet for > 10 minutes, then leaking again 2 h later → listed and notified
        assert!(h.record("app", 150.0, 900.0, T0 + 125 * MIN, NONE));
        assert_eq!(h.len(), 1);
    }

    #[test]
    fn new_episode_soon_after_dismiss_is_listed_but_not_renotified() {
        let mut h = LeakHistory::default();
        assert!(h.record("app", 150.0, 900.0, T0, NONE));
        h.dismiss("app", T0 + MIN);
        assert!(!h.record("app", 150.0, 900.0, T0 + 20 * MIN, NONE)); // new episode, but notified < 1 h ago
        assert_eq!(h.len(), 1);
    }

    #[test]
    fn restarted_app_that_leaks_again_is_listed_and_notified() {
        let mut h = LeakHistory::default();
        assert!(h.record("app", 150.0, 900.0, T0, NONE));
        h.forget("app"); // restarted
        assert!(h.record("app", 150.0, 400.0, T0 + 4 * MIN, NONE)); // the new process leaks too
        assert_eq!(h.len(), 1);
    }

    #[test]
    fn forget_clears_everything() {
        let mut h = LeakHistory::default();
        assert!(h.record("app", 150.0, 900.0, T0, NONE));
        h.dismiss("app", T0);
        assert!(!h.forget("app"));
        assert!(!h.is_dismissed("app"));
        assert!(h.record("app", 150.0, 900.0, T0 + MIN, NONE));
    }

    #[test]
    fn keeps_at_most_twenty_newest() {
        let mut h = LeakHistory::default();
        for i in 0..25 {
            h.record(&format!("app{i}"), 150.0, 900.0, T0 + i * MIN, NONE);
        }
        let list = h.snapshot();
        assert_eq!(list.len(), MAX_ENTRIES);
        assert_eq!(list[0].app, "app24"); // newest first
        assert!(!list.iter().any(|x| x.app == "app0")); // oldest dropped
    }

    #[test]
    fn names_are_case_insensitive() {
        let mut h = LeakHistory::default();
        assert!(h.record("Chrome.exe", 150.0, 900.0, T0, NONE));
        assert!(!h.record("chrome.exe", 150.0, 950.0, T0 + MIN, NONE));
        assert_eq!(h.len(), 1);
        assert_eq!(h.get("CHROME.EXE").unwrap().private_mb, 950.0);
    }
}
