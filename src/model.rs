//! Plain data the engine hands to the UI.

use crate::policy::Tier;

#[derive(Clone, Debug, Default)]
pub struct MemorySnapshot {
    pub total_gb: f64,
    pub used_gb: f64,
    pub available_gb: f64,
    /// Standby list size (Core + Normal + Reserve priority). Measured via PDH when available.
    pub standby_gb: f64,
    /// Free + Zero page lists. Measured via PDH when available.
    pub free_gb: f64,
    /// True when Standby/Free values come from the real memory-list counters, false when they are estimates.
    pub memory_lists_measured: bool,
    /// Standby list per priority 0–7 in GB (None when the kernel memory-list query is unavailable).
    pub standby_by_priority_gb: Option<[f64; 8]>,
    /// How fast Windows is evicting standby (cached) pages to satisfy new allocations, in MB/min.
    /// Negative when unknown. Sustained repurposing means real memory pressure.
    pub repurpose_mb_per_min: f64,
    /// True when purging the standby cache would actually help (free memory is low / cache is being evicted).
    pub standby_purge_worthwhile: bool,
    /// "ok", "high" (≥ 90% of the commit limit) or "critical" (≥ 96%), with hysteresis.
    pub commit_level: &'static str,
    pub percent: f64,
    /// "OPTIMAL", "ELEVATED" or "CRITICAL".
    pub pressure_level: &'static str,
    pub commit_total_gb: f64,
    pub commit_limit_gb: f64,
    pub commit_percent: f64,
    pub pagefile_total_gb: f64,
    pub pagefile_used_gb: f64,
    pub pagefile_percent: f64,
    pub kernel_total_mb: f64,
    pub kernel_paged_mb: f64,
    pub kernel_nonpaged_mb: f64,
    pub cpu_percent: f64,
    pub process_count: u32,
    pub foreground_app: String,
    pub standby_purge_supported: bool,
    pub session_freed_mb: f64,
    pub optimization_count: u32,
    pub active_profile_name: String,
    pub effective_threshold_percent: i32,
    pub effective_mode: String,
    pub next_scheduled_seconds: i64,
    /// Current Auto-Pilot cooldown including any adaptive back-off.
    pub effective_cooldown_seconds: i32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Badge {
    Background,
    Foreground,
    Shielded,
    Frozen,
    Eco,
    Leak,
    IdleBloat,
}

impl Badge {
    pub fn label(self) -> &'static str {
        match self {
            Badge::Frozen => "CRYO-FROZEN",
            Badge::Shielded => "SHIELDED",
            Badge::Leak => "LEAK ALERT",
            Badge::Eco => "ECO-QOS",
            Badge::Foreground => "FOREGROUND",
            Badge::IdleBloat => "IDLE BLOAT",
            Badge::Background => "BACKGROUND",
        }
    }
}

#[derive(Clone, Debug)]
pub struct ProcessItem {
    pub name: String,
    pub pid: u32,
    pub process_count: u32,
    /// Working set (physical RAM currently mapped) in MB.
    pub memory_mb: f64,
    /// Private committed memory in MB (what actually grows when an app leaks).
    pub private_mb: f64,
    pub peak_memory_mb: f64,
    pub cpu_percent: f64,
    /// Sustained private-memory growth rate in MB/min.
    pub leak_rate_mb_per_min: f64,
    pub is_leaking: bool,
    pub is_foreground: bool,
    pub is_protected: bool,
    pub is_frozen: bool,
    pub is_eco: bool,
    pub cap_mb: Option<i32>,
    pub smart_score: i32,
    /// Working set a trim could page out (0 when shielded). An upper bound, not a promise.
    pub reclaimable_mb: f64,
    pub badge: Badge,
}

#[derive(Clone, Debug, Default)]
pub struct OptimizationEvent {
    /// Local time "HH:mm:ss".
    pub timestamp: String,
    pub mode: String,
    pub trigger: String,
    pub reason: String,
    /// Measured memory reclaimed: working-set MB actually trimmed, or for a pure standby purge, the measured
    /// growth of the free page list. Never an estimate.
    pub freed_mb: f64,
    /// Measured change in "Available" RAM (free + standby).
    pub available_gain_mb: f64,
    /// Measured change in the free + zero page lists (0 if counters are unavailable).
    pub free_list_gain_mb: f64,
    pub before_percent: f64,
    pub after_percent: f64,
    pub processes_trimmed: u32,
    pub duration_ms: u64,
    pub standby_purged: bool,
    /// Memory saved by combining identical pages, in MB.
    pub combined_mb: f64,
    /// True when nothing was done (e.g. a kernel-only mode without administrator rights).
    pub skipped: bool,
    /// Automatic purge deliberately not done because there was no memory pressure (not logged, no back-off).
    pub skipped_no_pressure: bool,
    /// Extra human-readable detail, e.g. why the kernel purge was skipped.
    pub note: String,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct TelemetryPoint {
    pub used_percent: f64,
    pub cpu_percent: f64,
}

/// What to tell the user when committed memory nears the commit limit.
#[derive(Clone, Debug)]
pub struct CommitAdvice {
    pub level: &'static str,
    pub title: String,
    pub body: String,
    /// True when changing the page file is part of the fix (offer to open its settings).
    pub suggest_page_file: bool,
    /// Same level was already announced within 30 minutes: show the banner, skip the notification.
    pub is_repeat: bool,
}

/// What kind of PC we are on, used to pick sensible defaults and the UI weight.
#[derive(Clone, Copy, Debug)]
pub struct DeviceInfo {
    pub ram_gb: f64,
    pub logical_cores: usize,
    pub tier: Tier,
}

impl DeviceInfo {
    pub fn label(&self) -> String {
        let tier = match self.tier {
            Tier::Low => "Low-end",
            Tier::Mid => "Mid-range",
            Tier::High => "High-end",
        };
        format!("{:.0} GB · {} threads · {}", self.ram_gb.round(), self.logical_cores, tier)
    }
}

/// "1.25 GB" from 1024 MB up, otherwise whole megabytes.
pub fn format_mb(mb: f64) -> String {
    if mb >= 1024.0 {
        format!("{:.2} GB", mb / 1024.0)
    } else {
        format!("{} MB", mb.round())
    }
}

pub fn round_to(value: f64, decimals: i32) -> f64 {
    let f = 10f64.powi(decimals);
    (value * f).round() / f
}
