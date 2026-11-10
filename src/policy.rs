//! Pure decision logic shared by the engine and the unit tests. Nothing here touches Windows.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Tier {
    Low,
    Mid,
    High,
}

impl Tier {
    pub fn name(self) -> &'static str {
        match self {
            Tier::Low => "low",
            Tier::Mid => "mid",
            Tier::High => "high",
        }
    }
}

/// Thresholds tuned per hardware tier (see README "Built for any PC").
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TierTuning {
    pub trigger_percent: i32,
    pub min_free_gb: f64,
    pub cooldown_seconds: i32,
    pub min_process_working_set_mb: f64,
    pub profile_bump: i32,
}

/// Low-end: under ~7 GB RAM or ≤ 2 threads. Mid-range: under ~15 GB or ≤ 4 threads. High-end: the rest.
pub fn classify_tier(ram_gb: f64, logical_cores: usize) -> Tier {
    if ram_gb < 7.0 || logical_cores <= 2 {
        Tier::Low
    } else if ram_gb < 15.0 || logical_cores <= 4 {
        Tier::Mid
    } else {
        Tier::High
    }
}

pub fn tuning_for(tier: Tier) -> TierTuning {
    match tier {
        Tier::Low => TierTuning { trigger_percent: 88, min_free_gb: 0.5, cooldown_seconds: 300, min_process_working_set_mb: 30.0, profile_bump: 10 },
        Tier::Mid => TierTuning { trigger_percent: 85, min_free_gb: 0.75, cooldown_seconds: 180, min_process_working_set_mb: 20.0, profile_bump: 5 },
        Tier::High => TierTuning { trigger_percent: 80, min_free_gb: 1.5, cooldown_seconds: 120, min_process_working_set_mb: 15.0, profile_bump: 0 },
    }
}

/// Profile triggers are raised by the tier's bump and kept in 50–92%.
pub fn bump_profile_trigger(trigger: i32, tier: Tier) -> i32 {
    (trigger + tuning_for(tier).profile_bump).clamp(50, 92)
}

/// Purging the standby cache only helps when free memory is actually scarce (or Windows is already
/// evicting cache); otherwise it throws away useful cache. The cache must also be big enough to matter.
pub fn standby_purge_worthwhile(total_gb: f64, standby_gb: f64, free_gb: f64, repurpose_mb_per_min: f64) -> bool {
    let low_free_gb = (total_gb * 0.04).max(0.5);
    let cache_is_large = standby_gb >= (total_gb * 0.10).max(0.5);
    cache_is_large && (free_gb < low_free_gb || repurpose_mb_per_min >= 64.0)
}

/// Commit-charge warning level with hysteresis: enter high ≥ 90, critical ≥ 96; leave critical below 93,
/// high below 85.
pub fn next_commit_level(previous: &str, commit_percent: f64) -> &'static str {
    let (critical_at, high_at) = match previous {
        "critical" => (93.0, 85.0),
        "high" => (96.0, 85.0),
        _ => (96.0, 90.0),
    };
    if commit_percent >= critical_at {
        "critical"
    } else if commit_percent >= high_at {
        "high"
    } else {
        "ok"
    }
}

/// Auto-Pilot cooldown doubles per back-off level (base at least 30 s), capped at 30 minutes.
pub fn backoff_cooldown_seconds(base_seconds: i32, level: i32) -> i32 {
    let cd = f64::from(base_seconds.max(30)) * 2f64.powi(level.clamp(0, 10));
    cd.min(1800.0) as i32
}

pub const MAX_BACKOFF_LEVEL: i32 = 6;

pub const MOD_ALT: u32 = 0x0001;
pub const MOD_CONTROL: u32 = 0x0002;
pub const MOD_SHIFT: u32 = 0x0004;
pub const MOD_WIN: u32 = 0x0008;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Hotkey {
    pub modifiers: u32,
    pub vk: u32,
    pub label: String,
}

/// Parses "Ctrl+Alt+Shift+M" style hotkeys (modifiers Ctrl/Control, Alt, Shift, Win; key A–Z, 0–9, F1–F24).
pub fn parse_hotkey(text: &str) -> Option<Hotkey> {
    let parts: Vec<&str> = text.split('+').map(str::trim).filter(|p| !p.is_empty()).collect();
    if parts.len() < 2 {
        return None;
    }
    let mut modifiers = 0;
    for part in &parts[..parts.len() - 1] {
        modifiers |= match part.to_ascii_lowercase().as_str() {
            "ctrl" | "control" => MOD_CONTROL,
            "alt" => MOD_ALT,
            "shift" => MOD_SHIFT,
            "win" | "windows" => MOD_WIN,
            _ => return None,
        };
    }
    let key = parts[parts.len() - 1].to_ascii_uppercase();
    let bytes = key.as_bytes();
    let vk = if bytes.len() == 1 && (bytes[0].is_ascii_uppercase() || bytes[0].is_ascii_digit()) {
        u32::from(bytes[0])
    } else if bytes.len() >= 2 && bytes[0] == b'F' {
        match key[1..].parse::<u32>() {
            Ok(n) if (1..=24).contains(&n) && key[1..].bytes().all(|b| b.is_ascii_digit()) => 0x70 + n - 1, // VK_F1..VK_F24
            _ => return None,
        }
    } else {
        return None;
    };

    let mut labels: Vec<&str> = Vec::new();
    if modifiers & MOD_CONTROL != 0 {
        labels.push("Ctrl");
    }
    if modifiers & MOD_ALT != 0 {
        labels.push("Alt");
    }
    if modifiers & MOD_SHIFT != 0 {
        labels.push("Shift");
    }
    if modifiers & MOD_WIN != 0 {
        labels.push("Win");
    }
    labels.push(&key);
    Some(Hotkey { modifiers, vk, label: labels.join(" + ") })
}

/// A three-part version ("1.4.1"). Used for the update check.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct Version(pub u32, pub u32, pub u32);

impl std::fmt::Display for Version {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}.{}.{}", self.0, self.1, self.2)
    }
}

/// Parses a release tag ("v1.4.0", "1.4", "1.4.0-beta" …); pre-release suffixes and build metadata are
/// ignored, and a fourth part is dropped. Returns None if it isn't a version.
pub fn parse_version_tag(tag: &str) -> Option<Version> {
    let mut t = tag.trim();
    if t.is_empty() {
        return None;
    }
    if t.starts_with(['v', 'V']) {
        t = &t[1..];
    }
    if let Some(cut) = t.find(['-', '+', ' ']) {
        t = &t[..cut];
    }
    let parts: Vec<&str> = t.split('.').collect();
    if parts.is_empty() || parts.len() > 4 {
        return None;
    }
    let mut nums = [0u32; 3];
    for (i, part) in parts.iter().enumerate() {
        if part.is_empty() || !part.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        let n: u32 = part.parse().ok()?;
        if i < 3 {
            nums[i] = n;
        }
    }
    Some(Version(nums[0], nums[1], nums[2]))
}

pub fn is_prerelease_tag(tag: &str) -> bool {
    tag.contains('-')
}

/// True if `tag` is a stable release newer than `current`.
pub fn is_newer(tag: &str, current: Version) -> bool {
    !is_prerelease_tag(tag) && parse_version_tag(tag).is_some_and(|latest| latest > current)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn classifies_hardware() {
        for (ram, cores, expected) in [
            (4.0, 8, Tier::Low),   // 4 GB laptop
            (6.9, 8, Tier::Low),
            (16.0, 2, Tier::Low),  // plenty of RAM but a dual-thread CPU
            (7.9, 8, Tier::Mid),   // "8 GB" reports ~7.9
            (15.9, 8, Tier::High), // "16 GB" reports ~15.9
            (14.9, 8, Tier::Mid),
            (32.0, 4, Tier::Mid),  // quad-thread CPU stays mid
            (32.0, 16, Tier::High),
        ] {
            assert_eq!(classify_tier(ram, cores), expected, "{ram} GB, {cores} threads");
        }
    }

    #[test]
    fn low_end_gets_higher_trigger_and_longer_cooldown() {
        let (low, high) = (tuning_for(Tier::Low), tuning_for(Tier::High));
        assert!(low.trigger_percent > high.trigger_percent);
        assert!(low.cooldown_seconds > high.cooldown_seconds);
        assert!(low.min_free_gb < high.min_free_gb);
    }

    #[test]
    fn profile_triggers_are_bumped_and_clamped() {
        assert_eq!(bump_profile_trigger(68, Tier::High), 68);
        assert_eq!(bump_profile_trigger(68, Tier::Mid), 73);
        assert_eq!(bump_profile_trigger(68, Tier::Low), 78);
        assert_eq!(bump_profile_trigger(88, Tier::Low), 92); // capped
        assert_eq!(bump_profile_trigger(40, Tier::High), 50); // floored
    }

    #[test]
    fn standby_purge_decisions() {
        assert!(!standby_purge_worthwhile(16.0, 6.0, 4.0, 0.0), "free memory is plentiful");
        assert!(standby_purge_worthwhile(16.0, 6.0, 0.3, 0.0), "free memory is scarce and the cache is large");
        assert!(standby_purge_worthwhile(16.0, 6.0, 3.0, 64.0), "Windows is already evicting cache");
        assert!(!standby_purge_worthwhile(16.0, 1.0, 0.2, 500.0), "cache is small even under pressure");
        assert!(standby_purge_worthwhile(4.0, 0.5, 0.49, 0.0), "small PCs use half-gigabyte minimums");
    }

    #[test]
    fn commit_level_moves_with_hysteresis() {
        for (prev, pct, expected) in [
            ("ok", 89.9, "ok"),
            ("ok", 90.0, "high"),
            ("ok", 96.0, "critical"),
            ("high", 86.0, "high"), // stays high until below 85
            ("high", 84.9, "ok"),
            ("high", 96.0, "critical"),
            ("critical", 93.0, "critical"),
            ("critical", 92.9, "high"),
            ("critical", 80.0, "ok"),
        ] {
            assert_eq!(next_commit_level(prev, pct), expected, "{prev} at {pct}%");
        }
    }

    #[test]
    fn cooldown_doubles_and_caps() {
        assert_eq!(backoff_cooldown_seconds(120, 0), 120);
        assert_eq!(backoff_cooldown_seconds(120, 1), 240);
        assert_eq!(backoff_cooldown_seconds(120, 3), 960);
        assert_eq!(backoff_cooldown_seconds(120, 4), 1800); // capped at 30 min
        assert_eq!(backoff_cooldown_seconds(10, 0), 30); // at least 30 s
        assert_eq!(backoff_cooldown_seconds(300, -1), 300);
    }

    #[test]
    fn parses_default_hotkey() {
        let hk = parse_hotkey("Ctrl+Alt+Shift+M").unwrap();
        assert_eq!(hk.modifiers, MOD_CONTROL | MOD_ALT | MOD_SHIFT);
        assert_eq!(hk.vk, u32::from(b'M'));
        assert_eq!(hk.label, "Ctrl + Alt + Shift + M");
    }

    #[test]
    fn parses_function_keys_and_win() {
        let hk = parse_hotkey("ctrl + win + f9").unwrap();
        assert_eq!(hk.modifiers, MOD_CONTROL | MOD_WIN);
        assert_eq!(hk.vk, 0x78); // VK_F9
        assert_eq!(hk.label, "Ctrl + Win + F9");
    }

    #[test]
    fn rejects_invalid_hotkeys() {
        for text in ["", "M", "Ctrl+Hyper+M", "Ctrl+F25", "Ctrl+Space", "Ctrl+F+1"] {
            assert_eq!(parse_hotkey(text), None, "{text:?}");
        }
    }

    #[test]
    fn parses_version_tags() {
        assert_eq!(parse_version_tag("v1.4.0"), Some(Version(1, 4, 0)));
        assert_eq!(parse_version_tag("1.4"), Some(Version(1, 4, 0)));
        assert_eq!(parse_version_tag("v2"), Some(Version(2, 0, 0)));
        assert_eq!(parse_version_tag("v1.4.1-beta.2"), Some(Version(1, 4, 1)));
        assert_eq!(parse_version_tag("1.4.0.0"), Some(Version(1, 4, 0)));
        assert_eq!(parse_version_tag("latest"), None);
        assert_eq!(parse_version_tag(""), None);
    }

    #[test]
    fn offers_only_newer_stable_releases() {
        for (tag, current, expected) in [
            ("v1.4.1", Version(1, 4, 0), true),
            ("v1.4.0", Version(1, 4, 0), false),
            ("v1.3.9", Version(1, 4, 0), false),
            ("v2.0.0", Version(1, 9, 9), true),
            ("v1.5.0-beta", Version(1, 4, 0), false), // pre-releases are never offered
            ("latest", Version(1, 4, 0), false),
            ("", Version(1, 4, 0), false),
        ] {
            assert_eq!(is_newer(tag, current), expected, "{tag} vs {current}");
        }
    }
}
