//! Settings: the same `pulseram_config.json` schema PulseRAM 1.x wrote, so upgrades keep their settings.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::policy::{self, Tier};

pub const CURRENT_VERSION: i32 = 3;
pub const DEFAULT_HOTKEY_PRIMARY: &str = "Ctrl+Alt+Shift+M";
pub const DEFAULT_HOTKEY_SECONDARY: &str = "Ctrl+Alt+Shift+P";

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct WorkloadProfile {
    pub id: String,
    pub name: String,
    pub description: String,
    pub match_exes: Vec<String>,
    pub ram_threshold_percent: i32,
    pub auto_mode: String,
    pub auto_shield_matched: bool,
    pub boost_on_launch: bool,
    pub enabled: bool,
}

impl Default for WorkloadProfile {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            description: String::new(),
            match_exes: Vec::new(),
            ram_threshold_percent: 75,
            auto_mode: "smart".into(),
            auto_shield_matched: true,
            boost_on_launch: true,
            enabled: true,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct EngineConfig {
    /// Absent in v1 files, so it defaults to 1 and triggers migration; saved files carry CURRENT_VERSION.
    pub config_version: i32,
    pub auto_enabled: bool,
    pub ram_threshold_percent: i32,
    pub min_free_ram_gb: f64,
    /// Base Auto-Pilot cooldown. Doubles automatically (up to 30 min) while sweeps fail to relieve pressure.
    pub cooldown_seconds: i32,
    pub auto_mode: String,
    pub protect_foreground: bool,
    /// Smart mode only trims processes using less CPU than this (i.e. idle ones).
    pub idle_cpu_ceiling_percent: f64,
    /// Smart mode ignores processes with a smaller working set than this.
    pub min_process_working_set_mb: f64,
    /// Skip Auto-Pilot sweeps while total CPU is above CpuGuardPercent (trimming adds page faults to a busy system).
    pub pause_on_high_cpu: bool,
    pub cpu_guard_percent: f64,
    pub auto_leak_remediation: bool,
    pub leak_velocity_threshold_mb_min: f64,
    pub leak_guard_cooldown_seconds: i32,
    /// Apps the user chose to stop hearing about in the leak list.
    pub leak_ignored_apps: Vec<String>,
    pub cap_guard_cooldown_seconds: i32,
    pub scheduled_trim_enabled: bool,
    pub scheduled_interval_minutes: i32,
    pub auto_sound_chime: bool,
    pub focus_boost_active: bool,
    pub focus_boost_threshold_percent: i32,
    pub run_on_startup: bool,
    pub profiles_auto_switch: bool,
    pub forced_profile_id: String,
    pub hotkeys_enabled: bool,
    pub hotkey_primary: String,
    pub hotkey_secondary: String,
    pub protected_apps: Vec<String>,
    pub eco_apps: Vec<String>,
    pub app_memory_caps_mb: BTreeMap<String, i32>,
    pub profiles: Vec<WorkloadProfile>,
    /// "auto" (lite on low-end PCs), "lite" (slower refresh, fewer rows) or "full".
    pub ui_mode: String,
    /// Automatic sweeps only purge the standby cache when free memory is low or Windows is already evicting
    /// cache. Purging a healthy cache just makes apps reload from disk. Manual sweeps always purge.
    pub smart_standby_purge: bool,
    /// Draw the current RAM % into the tray icon.
    pub tray_shows_percent: bool,
    /// Run PulseRAM itself in Windows Efficiency Mode (EcoQoS) while it is hidden in the tray.
    pub efficiency_mode_when_hidden: bool,
    /// "system" (follow Windows), "dark" or "light".
    pub theme: String,
    /// Warn when committed memory nears the commit limit (RAM + page file) — the real cause of "out of memory".
    pub commit_warning_enabled: bool,
    /// Last window placement in physical pixels (None = centre on the primary screen).
    pub window_x: Option<i32>,
    pub window_y: Option<i32>,
    pub window_width: Option<i32>,
    pub window_height: Option<i32>,
    pub window_maximized: bool,
    /// Check GitHub Releases for a newer version, at most once a day. The only network request PulseRAM makes.
    pub check_for_updates: bool,
    /// ISO-8601 UTC time of the last answered update check.
    pub last_update_check_utc: Option<String>,
    /// A version the user chose to skip ("1.4.1"); not offered again.
    pub skipped_update_version: String,
    pub welcome_shown: bool,
    /// The window's minimize button hides PulseRAM to the tray instead of the taskbar.
    pub minimize_to_tray: bool,
    /// The one-time "still running in the tray" notice has been shown.
    pub tray_hint_shown: bool,
    /// Compact widget becomes see-through while the mouse isn't over it.
    pub mini_hud_see_through: bool,
    /// Last position of the compact widget (None = top-right corner).
    pub mini_hud_x: Option<i32>,
    pub mini_hud_y: Option<i32>,
    /// Tier the defaults were tuned for on first run ("low", "mid", "high"; empty = not tuned).
    pub tuned_for_tier: String,
}

impl Default for EngineConfig {
    fn default() -> Self {
        Self {
            config_version: 1,
            auto_enabled: true,
            ram_threshold_percent: 80,
            min_free_ram_gb: 1.0,
            cooldown_seconds: 120,
            auto_mode: "smart".into(),
            protect_foreground: true,
            idle_cpu_ceiling_percent: 2.5,
            min_process_working_set_mb: 15.0,
            pause_on_high_cpu: true,
            cpu_guard_percent: 75.0,
            auto_leak_remediation: true,
            leak_velocity_threshold_mb_min: 120.0,
            leak_guard_cooldown_seconds: 300,
            leak_ignored_apps: Vec::new(),
            cap_guard_cooldown_seconds: 60,
            scheduled_trim_enabled: false,
            scheduled_interval_minutes: 15,
            auto_sound_chime: false,
            focus_boost_active: false,
            focus_boost_threshold_percent: 62,
            run_on_startup: false,
            profiles_auto_switch: true,
            forced_profile_id: String::new(),
            hotkeys_enabled: true,
            hotkey_primary: DEFAULT_HOTKEY_PRIMARY.into(),
            hotkey_secondary: DEFAULT_HOTKEY_SECONDARY.into(),
            protected_apps: Vec::new(),
            eco_apps: Vec::new(),
            app_memory_caps_mb: BTreeMap::new(),
            profiles: Vec::new(),
            ui_mode: "auto".into(),
            smart_standby_purge: true,
            tray_shows_percent: true,
            efficiency_mode_when_hidden: true,
            theme: "system".into(),
            commit_warning_enabled: true,
            window_x: None,
            window_y: None,
            window_width: None,
            window_height: None,
            window_maximized: false,
            check_for_updates: true,
            last_update_check_utc: None,
            skipped_update_version: String::new(),
            welcome_shown: false,
            minimize_to_tray: true,
            tray_hint_shown: false,
            mini_hud_see_through: false,
            mini_hud_x: None,
            mini_hud_y: None,
            tuned_for_tier: String::new(),
        }
    }
}

impl EngineConfig {
    #[cfg(test)]
    pub fn cap_for(&self, app: &str) -> Option<i32> {
        self.app_memory_caps_mb.iter().find(|(k, _)| k.eq_ignore_ascii_case(app)).map(|(_, v)| *v)
    }

    pub fn set_cap(&mut self, app: &str, cap_mb: Option<i32>) {
        self.app_memory_caps_mb.retain(|k, _| !k.eq_ignore_ascii_case(app));
        if let Some(cap) = cap_mb {
            self.app_memory_caps_mb.insert(app.to_string(), cap);
        }
    }
}

pub fn contains_ignore_case(list: &[String], item: &str) -> bool {
    list.iter().any(|x| x.eq_ignore_ascii_case(item))
}

pub fn is_known_mode(mode: &str) -> bool {
    matches!(mode, "smart" | "deep" | "standby" | "max")
}

/// Built-in profiles; kept identical to the Profiles in pulseram_defaults.json.
pub fn default_profiles() -> Vec<WorkloadProfile> {
    fn exes(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| s.to_string()).collect()
    }
    vec![
        WorkloadProfile {
            id: "gaming-boost".into(),
            name: "Gaming Boost".into(),
            description: "Trims idle background apps on game launch, shields the game process, and sweeps at 68%.".into(),
            match_exes: exes(&[
                "cs2.exe", "valorant.exe", "valorant-win64-shipping.exe", "fortniteclient-win64-shipping.exe", "eldenring.exe",
                "genshinimpact.exe", "starrail.exe", "robloxplayerbeta.exe", "dota2.exe", "league of legends.exe", "r5apex.exe",
                "cyberpunk2077.exe", "helldivers2.exe", "overwatch.exe", "javaw.exe", "minecraft.windows.exe",
            ]),
            ram_threshold_percent: 68,
            auto_mode: "smart".into(),
            auto_shield_matched: true,
            boost_on_launch: true,
            enabled: true,
        },
        WorkloadProfile {
            id: "creator-studio".into(),
            name: "Creator & 3D Studio".into(),
            description: "Shields active rendering/editing apps while trimming idle background apps at 72%.".into(),
            match_exes: exes(&[
                "blender.exe", "afterfx.exe", "adobe premiere pro.exe", "photoshop.exe", "resolve.exe", "unrealeditor.exe", "unity.exe",
                "obs64.exe",
            ]),
            ram_threshold_percent: 72,
            auto_mode: "smart".into(),
            auto_shield_matched: true,
            boost_on_launch: true,
            enabled: true,
        },
        WorkloadProfile {
            id: "dev-workstation".into(),
            name: "Dev & Local AI".into(),
            description: "Shields your IDE and local LLM runners while trimming idle helpers at 76%.".into(),
            match_exes: exes(&["code.exe", "devenv.exe", "idea64.exe", "pycharm64.exe", "ollama.exe", "lm-studio.exe", "docker.exe"]),
            ram_threshold_percent: 76,
            auto_mode: "smart".into(),
            auto_shield_matched: true,
            boost_on_launch: false,
            enabled: true,
        },
        WorkloadProfile {
            id: "browser-tamer".into(),
            name: "Browser Tab Tamer".into(),
            description: "Trims idle browser subprocesses when total memory passes 78%.".into(),
            match_exes: exes(&["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe"]),
            ram_threshold_percent: 78,
            auto_mode: "smart".into(),
            auto_shield_matched: false,
            boost_on_launch: false,
            enabled: true,
        },
    ]
}

/// Picks thresholds that suit the hardware. Low-RAM PCs normally sit at a high memory load, so they get a
/// higher trigger, a smaller floor and a longer cooldown (otherwise Auto-Pilot would trim constantly and make
/// the PC slower). Big-RAM PCs can afford a larger free-memory floor. Profile triggers are raised by the same
/// margin so an always-matching profile (e.g. a browser) can't undo this.
pub fn apply_device_tuning(cfg: &mut EngineConfig, tier: Tier) {
    let t = policy::tuning_for(tier);
    cfg.ram_threshold_percent = t.trigger_percent;
    cfg.min_free_ram_gb = t.min_free_gb;
    cfg.cooldown_seconds = t.cooldown_seconds;
    cfg.min_process_working_set_mb = t.min_process_working_set_mb;
    for p in &mut cfg.profiles {
        p.ram_threshold_percent = policy::bump_profile_trigger(p.ram_threshold_percent, tier);
    }
    cfg.tuned_for_tier = tier.name().into();
}

/// Re-applies the hardware-based defaults, keeping the user's enabled/disabled choice and custom EXE lists.
pub fn retune(cfg: &mut EngineConfig, tier: Tier) {
    let fresh = default_profiles();
    for p in &mut cfg.profiles {
        if let Some(d) = fresh.iter().find(|f| f.id == p.id) {
            p.ram_threshold_percent = d.ram_threshold_percent;
        }
    }
    apply_device_tuning(cfg, tier);
}

fn migrate(cfg: &mut EngineConfig, existing_user_settings: bool) {
    if cfg.config_version >= CURRENT_VERSION {
        return;
    }
    // v3 (PulseRAM 1.4): the welcome card is for new installs only, not for people upgrading.
    if cfg.config_version < 3 && existing_user_settings {
        cfg.welcome_shown = true;
    }
    if cfg.config_version >= 2 {
        cfg.config_version = CURRENT_VERSION;
        return;
    }

    // v1 shipped aggressive defaults that trimmed every 30 s on low-RAM PCs.
    if cfg.cooldown_seconds < 120 {
        cfg.cooldown_seconds = 120;
    }
    if (cfg.min_free_ram_gb - 2.0).abs() < 0.01 {
        cfg.min_free_ram_gb = 1.0;
    }

    // v1 hard-coded Ctrl+Shift+M / Ctrl+Shift+P, which collide with Teams (mute) and VS Code (command palette).
    let is_old = |hk: &str, old: &str| hk.trim().is_empty() || hk.replace(' ', "").eq_ignore_ascii_case(old);
    if is_old(&cfg.hotkey_primary, "Ctrl+Shift+M") {
        cfg.hotkey_primary = DEFAULT_HOTKEY_PRIMARY.into();
    }
    if is_old(&cfg.hotkey_secondary, "Ctrl+Shift+P") {
        cfg.hotkey_secondary = DEFAULT_HOTKEY_SECONDARY.into();
    }

    // v1 permanently copied auto-shielded profile EXEs (games, editors…) into ProtectedApps.
    // Shields are now temporary while a profile is active, so remove those leftovers.
    let profile_exes: Vec<String> = cfg.profiles.iter().filter(|p| p.auto_shield_matched).flat_map(|p| p.match_exes.iter().cloned()).collect();
    cfg.protected_apps.retain(|app| !contains_ignore_case(&profile_exes, app));

    cfg.config_version = CURRENT_VERSION;
}

pub fn clamp(cfg: &mut EngineConfig) {
    cfg.ram_threshold_percent = cfg.ram_threshold_percent.clamp(50, 95);
    cfg.min_free_ram_gb = clamp_f(cfg.min_free_ram_gb, 0.5, 8.0, 1.0);
    cfg.cooldown_seconds = cfg.cooldown_seconds.clamp(30, 3600);
    cfg.idle_cpu_ceiling_percent = clamp_f(cfg.idle_cpu_ceiling_percent, 0.1, 50.0, 2.5);
    cfg.min_process_working_set_mb = clamp_f(cfg.min_process_working_set_mb, 0.0, 4096.0, 15.0);
    cfg.cpu_guard_percent = clamp_f(cfg.cpu_guard_percent, 30.0, 100.0, 75.0);
    cfg.leak_velocity_threshold_mb_min = clamp_f(cfg.leak_velocity_threshold_mb_min, 10.0, 10000.0, 120.0);
    cfg.leak_guard_cooldown_seconds = cfg.leak_guard_cooldown_seconds.clamp(60, 3600);
    cfg.cap_guard_cooldown_seconds = cfg.cap_guard_cooldown_seconds.clamp(15, 3600);
    cfg.scheduled_interval_minutes = cfg.scheduled_interval_minutes.clamp(5, 1440);
    cfg.focus_boost_threshold_percent = cfg.focus_boost_threshold_percent.clamp(50, 95);
    if !is_known_mode(&cfg.auto_mode) {
        cfg.auto_mode = "smart".into();
    }
    if !matches!(cfg.ui_mode.as_str(), "auto" | "lite" | "full") {
        cfg.ui_mode = "auto".into();
    }
    if !matches!(cfg.theme.as_str(), "system" | "dark" | "light") {
        cfg.theme = "system".into();
    }
    for p in &mut cfg.profiles {
        p.ram_threshold_percent = p.ram_threshold_percent.clamp(50, 95);
        if !is_known_mode(&p.auto_mode) {
            p.auto_mode = "smart".into();
        }
    }
}

fn clamp_f(value: f64, min: f64, max: f64, fallback: f64) -> f64 {
    if value.is_finite() {
        value.clamp(min, max)
    } else {
        fallback
    }
}

/// Removes `//` and `/* */` comments and trailing commas, so a hand-edited settings file still loads.
fn strip_comments_and_trailing_commas(text: &str) -> String {
    let b = text.as_bytes();
    let mut out: Vec<u8> = Vec::with_capacity(b.len());
    let mut i = 0;
    let mut in_string = false;
    while i < b.len() {
        let c = b[i];
        if in_string {
            out.push(c);
            if c == b'\\' && i + 1 < b.len() {
                out.push(b[i + 1]);
                i += 1;
            } else if c == b'"' {
                in_string = false;
            }
            i += 1;
        } else if c == b'"' {
            in_string = true;
            out.push(c);
            i += 1;
        } else if c == b'/' && b.get(i + 1) == Some(&b'/') {
            while i < b.len() && b[i] != b'\n' {
                i += 1;
            }
        } else if c == b'/' && b.get(i + 1) == Some(&b'*') {
            i += 2;
            while i + 1 < b.len() && !(b[i] == b'*' && b[i + 1] == b'/') {
                i += 1;
            }
            i = (i + 2).min(b.len());
        } else if c == b']' || c == b'}' {
            // Drop a comma that only whitespace separates from this closing bracket.
            let mut j = out.len();
            while j > 0 && out[j - 1].is_ascii_whitespace() {
                j -= 1;
            }
            if j > 0 && out[j - 1] == b',' {
                out.remove(j - 1);
            }
            out.push(c);
            i += 1;
        } else {
            out.push(c);
            i += 1;
        }
    }
    String::from_utf8(out).unwrap_or_default()
}

fn parse_lenient(text: &str) -> Option<Value> {
    let text = text.trim_start_matches('\u{feff}');
    serde_json::from_str(text).or_else(|_| serde_json::from_str(&strip_comments_and_trailing_commas(text))).ok()
}

/// Renames keys to their canonical spelling (property names are matched case-insensitively) and drops
/// nulls, so a missing or null value falls back to its default.
fn normalize_object(value: &mut Value, canonical: &Value) {
    let (Some(obj), Some(names)) = (value.as_object_mut(), canonical.as_object()) else { return };
    let entries: Vec<(String, Value)> = std::mem::take(obj).into_iter().collect();
    for (key, val) in entries {
        if val.is_null() {
            continue;
        }
        let name = names.keys().find(|n| n.eq_ignore_ascii_case(&key)).cloned().unwrap_or(key);
        obj.entry(name).or_insert(val);
    }
}

/// Parses a settings file. Errors mean the file is unreadable as settings.
pub fn parse(text: &str) -> Result<EngineConfig, String> {
    let mut value = parse_lenient(text).ok_or("not valid JSON")?;
    if !value.is_object() {
        return Err("not a JSON object".into());
    }
    let canonical = serde_json::to_value(EngineConfig::default()).map_err(|e| e.to_string())?;
    normalize_object(&mut value, &canonical);
    let profile_shape = serde_json::to_value(WorkloadProfile::default()).map_err(|e| e.to_string())?;
    if let Some(profiles) = value.get_mut("Profiles").and_then(Value::as_array_mut) {
        profiles.retain(|p| p.is_object());
        for p in profiles {
            normalize_object(p, &profile_shape);
        }
    }
    serde_json::from_value(value).map_err(|e| e.to_string())
}

/// True only for a file written in EngineConfig's own schema. v1.0 also shipped a snake_case file with the
/// same name whose keys the engine never understood; that one is ignored.
fn is_engine_config_file(path: &Path) -> bool {
    let Ok(text) = std::fs::read_to_string(path) else { return false };
    parse_lenient(&text)
        .and_then(|v| v.as_object().map(|o| o.keys().any(|k| k.eq_ignore_ascii_case("RamThresholdPercent") || k.eq_ignore_ascii_case("AutoEnabled"))))
        .unwrap_or(false)
}

pub struct ConfigPaths {
    /// User settings: `<data>\pulseram_config.json`. They always save, even for an all-users install.
    pub user: PathBuf,
    /// Defaults shipped next to the EXE (always overwritten by the installer).
    pub seed: PathBuf,
    /// v1.0 kept user settings next to the EXE; imported once if present.
    pub legacy: PathBuf,
}

impl ConfigPaths {
    pub fn new(data_dir: &Path, exe_dir: &Path) -> Self {
        Self {
            user: data_dir.join("pulseram_config.json"),
            seed: exe_dir.join("pulseram_defaults.json"),
            legacy: exe_dir.join("pulseram_config.json"),
        }
    }
}

pub struct LoadOutcome {
    pub config: EngineConfig,
    /// The file on disk is missing, older or came from the defaults, so it should be written now.
    pub needs_save: bool,
    /// The user's own file existed but could not be read; its text, to keep a copy before it is overwritten.
    pub corrupt_text: Option<String>,
}

pub fn load(paths: &ConfigPaths, tier: Tier) -> LoadOutcome {
    let path = if paths.user.exists() {
        Some(&paths.user)
    } else if is_engine_config_file(&paths.legacy) {
        Some(&paths.legacy)
    } else if paths.seed.exists() {
        Some(&paths.seed)
    } else {
        None
    };
    let fresh_defaults = path.is_none() || path == Some(&paths.seed);
    let loaded_from_seed = path.is_some() && path != Some(&paths.user);

    let mut corrupt_text = None;
    let mut loaded = None;
    if let Some(path) = path {
        match std::fs::read(path) {
            Ok(bytes) => {
                let text = String::from_utf8_lossy(&bytes).into_owned();
                match parse(&text) {
                    Ok(cfg) => loaded = Some(cfg),
                    Err(_) if path == &paths.user => corrupt_text = Some(text),
                    Err(_) => {}
                }
            }
            Err(_) => {}
        }
    }

    let had_file = loaded.is_some();
    let (config, needs_save) = finalize(loaded, loaded_from_seed, fresh_defaults, tier);
    LoadOutcome { config, needs_save: needs_save || !had_file, corrupt_text }
}

/// Fills in defaults, migrates old versions, tunes fresh settings for the hardware and clamps every value.
pub fn finalize(loaded: Option<EngineConfig>, loaded_from_seed: bool, fresh_defaults: bool, tier: Tier) -> (EngineConfig, bool) {
    let was_loaded = loaded.is_some();
    let mut cfg = loaded.unwrap_or_default();

    if cfg.profiles.is_empty() {
        cfg.profiles = default_profiles();
    }
    if cfg.protected_apps.is_empty() {
        cfg.protected_apps = vec!["explorer.exe".into(), "dwm.exe".into(), "audiodg.exe".into()];
    }

    let needs_save = !was_loaded || loaded_from_seed || cfg.config_version < CURRENT_VERSION;
    migrate(&mut cfg, !fresh_defaults && was_loaded);
    if fresh_defaults || !was_loaded {
        apply_device_tuning(&mut cfg, tier);
    }
    clamp(&mut cfg);
    cfg.config_version = cfg.config_version.max(CURRENT_VERSION);
    (cfg, needs_save)
}

pub fn to_json(cfg: &EngineConfig) -> String {
    serde_json::to_string_pretty(cfg).unwrap_or_else(|_| "{}".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shipped_defaults_file_matches_built_in_profiles() {
        let text = include_str!("../pulseram_defaults.json");
        let cfg = parse(text).expect("pulseram_defaults.json must parse");
        assert_eq!(cfg.profiles, default_profiles());
        assert_eq!(cfg.config_version, CURRENT_VERSION);
        assert_eq!(cfg.hotkey_primary, DEFAULT_HOTKEY_PRIMARY);
    }

    #[test]
    fn round_trips_through_json() {
        let (mut cfg, _) = finalize(None, false, true, Tier::High);
        cfg.set_cap("Chrome.exe", Some(2048));
        cfg.window_x = Some(-12);
        cfg.last_update_check_utc = Some("2026-11-09T08:15:30Z".into());
        let back = parse(&to_json(&cfg)).unwrap();
        assert_eq!(back, cfg);
    }

    #[test]
    fn reads_pascal_case_keys_written_by_1x() {
        let cfg = parse(r#"{ "ConfigVersion": 3, "RamThresholdPercent": 77, "MinFreeRamGb": 2.5, "WindowX": null, "LastUpdateCheckUtc": "2026-10-01T10:00:00.1234567Z", "AppMemoryCapsMb": { "chrome.exe": 1024 } }"#).unwrap();
        assert_eq!(cfg.ram_threshold_percent, 77);
        assert_eq!(cfg.min_free_ram_gb, 2.5);
        assert_eq!(cfg.window_x, None);
        assert_eq!(cfg.cap_for("CHROME.EXE"), Some(1024));
        assert!(cfg.auto_enabled, "missing keys keep their defaults");
    }

    #[test]
    fn tolerates_comments_trailing_commas_nulls_and_key_case() {
        let text = r#"{
            // my hotkey
            "hotkeyprimary": "Ctrl+Alt+F9", /* inline */
            "PROFILES": null,
            "ProtectedApps": ["a.exe", "b // not a comment.exe",],
        }"#;
        let cfg = parse(text).unwrap();
        assert_eq!(cfg.hotkey_primary, "Ctrl+Alt+F9");
        assert_eq!(cfg.protected_apps, vec!["a.exe", "b // not a comment.exe"]);
        assert!(cfg.profiles.is_empty());
    }

    #[test]
    fn rejects_non_settings() {
        assert!(parse("[1, 2]").is_err());
        assert!(parse("not json").is_err());
        assert!(parse(r#"{ "RamThresholdPercent": "eighty" }"#).is_err());
    }

    #[test]
    fn fresh_install_is_tuned_for_the_hardware() {
        let (cfg, needs_save) = finalize(None, false, true, Tier::Low);
        assert!(needs_save);
        assert_eq!(cfg.ram_threshold_percent, 88);
        assert_eq!(cfg.cooldown_seconds, 300);
        assert_eq!(cfg.tuned_for_tier, "low");
        assert_eq!(cfg.profiles[0].ram_threshold_percent, 78); // 68 + 10
        assert!(!cfg.welcome_shown);
        assert_eq!(cfg.config_version, CURRENT_VERSION);
        assert_eq!(cfg.protected_apps.len(), 3);
    }

    #[test]
    fn existing_settings_are_not_retuned() {
        let user = EngineConfig { config_version: 3, ram_threshold_percent: 70, profiles: default_profiles(), ..Default::default() };
        let (cfg, needs_save) = finalize(Some(user), false, false, Tier::Low);
        assert!(!needs_save);
        assert_eq!(cfg.ram_threshold_percent, 70);
        assert_eq!(cfg.tuned_for_tier, "");
    }

    #[test]
    fn v1_settings_are_migrated() {
        let v1 = EngineConfig {
            config_version: 1,
            cooldown_seconds: 30,
            min_free_ram_gb: 2.0,
            hotkey_primary: "Ctrl + Shift + M".into(),
            hotkey_secondary: "".into(),
            protected_apps: vec!["explorer.exe".into(), "CS2.exe".into(), "chrome.exe".into()],
            profiles: default_profiles(),
            ..Default::default()
        };
        let (cfg, needs_save) = finalize(Some(v1), false, false, Tier::High);
        assert!(needs_save);
        assert_eq!(cfg.config_version, CURRENT_VERSION);
        assert_eq!(cfg.cooldown_seconds, 120);
        assert_eq!(cfg.min_free_ram_gb, 1.0);
        assert_eq!(cfg.hotkey_primary, DEFAULT_HOTKEY_PRIMARY);
        assert_eq!(cfg.hotkey_secondary, DEFAULT_HOTKEY_SECONDARY);
        // cs2.exe was an auto-shielded profile EXE; chrome.exe belongs to a profile that doesn't shield.
        assert_eq!(cfg.protected_apps, vec!["explorer.exe", "chrome.exe"]);
        assert!(cfg.welcome_shown, "people upgrading don't get the welcome card");
    }

    #[test]
    fn v2_upgrade_skips_the_welcome_card_only() {
        let v2 = EngineConfig { config_version: 2, cooldown_seconds: 45, profiles: default_profiles(), ..Default::default() };
        let (cfg, _) = finalize(Some(v2), false, false, Tier::High);
        assert!(cfg.welcome_shown);
        assert_eq!(cfg.cooldown_seconds, 45);
    }

    #[test]
    fn out_of_range_values_are_clamped() {
        let mut cfg = EngineConfig {
            ram_threshold_percent: 5,
            min_free_ram_gb: f64::NAN,
            cooldown_seconds: 1,
            auto_mode: "nuke".into(),
            ui_mode: "ultra".into(),
            theme: "pink".into(),
            scheduled_interval_minutes: 0,
            ..Default::default()
        };
        clamp(&mut cfg);
        assert_eq!(cfg.ram_threshold_percent, 50);
        assert_eq!(cfg.min_free_ram_gb, 1.0);
        assert_eq!(cfg.cooldown_seconds, 30);
        assert_eq!(cfg.auto_mode, "smart");
        assert_eq!(cfg.ui_mode, "auto");
        assert_eq!(cfg.theme, "system");
        assert_eq!(cfg.scheduled_interval_minutes, 5);
    }

    #[test]
    fn retune_resets_triggers_but_keeps_user_choices() {
        let (mut cfg, _) = finalize(None, false, true, Tier::High);
        cfg.profiles[0].enabled = false;
        cfg.profiles[0].ram_threshold_percent = 90;
        cfg.ram_threshold_percent = 60;
        retune(&mut cfg, Tier::Mid);
        assert!(!cfg.profiles[0].enabled);
        assert_eq!(cfg.profiles[0].ram_threshold_percent, 73);
        assert_eq!(cfg.ram_threshold_percent, 85);
    }
}
