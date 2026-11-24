//! The memory engine: samples the system, runs Auto-Pilot on a background thread and carries out sweeps and
//! the per-app governors (shield, freeze, EcoQoS, hard cap). All Windows calls go through `sys`.

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

use crate::config::{self, contains_ignore_case, ConfigPaths, EngineConfig, WorkloadProfile};
use crate::leaks::{LeakHistory, LeakIncident};
use crate::model::*;
use crate::policy::{self, Tier};
use crate::startup::{self, StartupResult, TaskAudit};
use crate::sys::{self, base_name, exe_name, CapOutcome, PageFileKind, Privileges, ProcInfo};
use crate::{clock, log, paths};

const GB: f64 = 1024.0 * 1024.0 * 1024.0;
const MB: f64 = 1024.0 * 1024.0;
const TICK: Duration = Duration::from_millis(1500);

// Stored as lower-case BASE names (no ".exe"); pseudo-processes such as "Registry" and "Memory Compression"
// have no extension, so every check goes through base_name() to compare like with like.
const CRITICAL_SYSTEM_PROCESSES: [&str; 19] = [
    "system", "idle", "registry", "memory compression", "secure system", "smss", "csrss", "wininit", "services", "lsass", "lsaiso", "lsm",
    "winlogon", "dwm", "fontdrvhost", "audiodg", "wudfhost", "spoolsv", "msmpeng",
];

const UNFREEZABLE_PROCESSES: [&str; 13] = [
    "explorer", "svchost", "taskmgr", "sihost", "ctfmon", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "runtimebroker",
    "textinputhost", "conhost", "dllhost", "applicationframehost",
];

pub fn is_critical(name: &str) -> bool {
    let base = base_name(name).to_lowercase();
    base == "pulseram" || CRITICAL_SYSTEM_PROCESSES.contains(&base.as_str())
}

fn is_unfreezable(name: &str) -> bool {
    is_critical(name) || UNFREEZABLE_PROCESSES.contains(&base_name(name).to_lowercase().as_str())
}

fn same_app(a: &str, b: &str) -> bool {
    base_name(a).eq_ignore_ascii_case(base_name(b))
}

/// Sweeps PulseRAM started on its own (as opposed to the user clicking, a hotkey or the tray menu).
fn is_automatic_trigger(trigger: &str) -> bool {
    matches!(trigger, "Auto-Pilot" | "Scheduled" | "Profile Launch Boost")
}

trait LockExt<T> {
    /// Locks, recovering the data if another thread panicked while holding the lock.
    fn lk(&self) -> MutexGuard<'_, T>;
}

impl<T> LockExt<T> for Mutex<T> {
    fn lk(&self) -> MutexGuard<'_, T> {
        self.lock().unwrap_or_else(|e| e.into_inner())
    }
}

/// One suspended process, journaled so a crash, kill or uninstall can never leave apps suspended.
/// Same file format as PulseRAM 1.x (`StartTicksUtc` is in .NET ticks).
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
struct FrozenRecord {
    name: String,
    pid: u32,
    start_ticks_utc: i64,
}

impl Default for FrozenRecord {
    fn default() -> Self {
        Self { name: String::new(), pid: 0, start_ticks_utc: 0 }
    }
}

fn frozen_journal_path() -> std::path::PathBuf {
    paths::data_dir().join("frozen_processes.json")
}

fn read_frozen_journal() -> Vec<FrozenRecord> {
    std::fs::read_to_string(frozen_journal_path())
        .ok()
        .and_then(|text| serde_json::from_str(text.trim_start_matches('\u{feff}')).ok())
        .unwrap_or_default()
}

fn write_frozen_journal(records: &[FrozenRecord]) -> bool {
    if records.is_empty() {
        return sys::delete_file(&frozen_journal_path());
    }
    if std::fs::create_dir_all(paths::data_dir()).is_err() {
        return false;
    }
    match serde_json::to_string(records) {
        Ok(json) => sys::write_text(&frozen_journal_path(), &json, false),
        Err(_) => false,
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum ResumeResult {
    Resumed,
    Gone,
    Failed,
}

/// Resumes exactly the journaled PID (verified by name and start time, so an unrelated process that reused
/// the PID is never touched).
fn try_resume_record(record: &FrozenRecord, processes: &[ProcInfo]) -> ResumeResult {
    let Some(process) = processes.iter().find(|p| p.pid == record.pid) else { return ResumeResult::Gone };
    if !exe_name(&process.name).eq_ignore_ascii_case(&exe_name(&record.name)) {
        return ResumeResult::Gone; // PID reused by another program
    }
    let started = sys::filetime_to_dotnet_ticks(process.create_time);
    if record.start_ticks_utc != 0 && started != 0 && started != record.start_ticks_utc {
        return ResumeResult::Gone; // PID reused by a newer instance
    }
    if sys::resume_process(record.pid) {
        ResumeResult::Resumed
    } else {
        ResumeResult::Failed
    }
}

/// Resumes every process a previous PulseRAM session suspended but never thawed (crash, kill, upgrade or
/// uninstall). Safe to call when nothing is frozen. Returns how many were resumed.
pub fn recover_frozen_processes() -> usize {
    let records = read_frozen_journal();
    if records.is_empty() {
        return 0;
    }
    let processes = sys::list_processes();
    let mut resumed = 0;
    // Records for processes that still exist but could not be resumed (e.g. an elevated app frozen by an
    // elevated PulseRAM, and we are now running as a standard user) are KEPT so a later elevated run can
    // still resume them.
    let mut still_suspended = Vec::new();
    for record in records {
        match try_resume_record(&record, &processes) {
            ResumeResult::Resumed => resumed += 1,
            ResumeResult::Failed => still_suspended.push(record),
            ResumeResult::Gone => {}
        }
    }
    write_frozen_journal(&still_suspended);
    resumed
}

pub fn detect_device() -> DeviceInfo {
    let ram_gb = sys::memory_status().map(|m| m.total_phys as f64 / GB).unwrap_or(8.0);
    let logical_cores = sys::logical_cores();
    DeviceInfo { ram_gb, logical_cores, tier: policy::classify_tier(ram_gb, logical_cores) }
}

pub enum EngineEvent {
    /// A sweep, trim or cap finished (also raised for manual ones).
    Optimization(OptimizationEvent),
    /// Commit charge climbed into "high" or "critical".
    CommitWarning(CommitAdvice),
    /// An app was seen leaking for the first time (then at most hourly per app).
    LeakDetected(LeakIncident),
}

type Listener = Arc<dyn Fn(EngineEvent) + Send + Sync>;

pub struct ActionResult {
    pub ok: bool,
    /// The governor's state after the call (frozen / eco active).
    pub active: bool,
    pub message: String,
}

struct State {
    config: EngineConfig,
    frozen_apps: Vec<String>,
    frozen_records: Vec<FrozenRecord>,
    eco_apps: Vec<String>,
    /// EXEs shielded by the active workload profile (lower-case). Never written to the settings file.
    profile_shields: HashSet<String>,
    last_cap_action: HashMap<String, Instant>,
    last_leak_action: HashMap<String, Instant>,
    leak_history: LeakHistory,
    last_auto_optimize: Option<Instant>,
    auto_backoff_level: i32,
    last_scheduled_sweep: Instant,
    session_freed_mb: f64,
    optimization_count: u32,
    active_matched_profile: Option<WorkloadProfile>,
    last_launched_profile_id: String,
    commit_level: &'static str,
    last_warned_level: &'static str,
    last_warning_rank: u8,
    last_commit_warning: Option<Instant>,
    last_repurposed_pages: u64,
    last_repurpose_sample: Option<Instant>,
    repurpose_mb_per_min: f64,
    mem_list_retry_at: Option<Instant>,
}

#[derive(Default)]
struct Cache {
    events: Vec<OptimizationEvent>,
    telemetry: VecDeque<TelemetryPoint>,
    grouped: Arc<Vec<ProcessItem>>,
    individual: Arc<Vec<ProcessItem>>,
    snapshot: Arc<MemorySnapshot>,
    /// CPU % per PID from the last two scans (what "idle" means for Smart mode).
    pid_cpu: Arc<HashMap<u32, f64>>,
    system_cpu_percent: f64,
}

#[derive(Default)]
struct ScanState {
    last_scan: Option<Instant>,
    proc_cpu_samples: HashMap<u32, (Instant, u64)>,
    app_history: HashMap<String, VecDeque<(Instant, f64)>>,
    last_sys_times: Option<(u64, u64, u64)>,
}

struct Inner {
    paths: ConfigPaths,
    device: DeviceInfo,
    privileges: Privileges,
    first_run: AtomicBool,
    unsafe_task_found: AtomicBool,
    state: Mutex<State>,
    cache: Mutex<Cache>,
    scan: Mutex<ScanState>,
    counters: Mutex<sys::MemCounters>,
    /// Serialises trims so manual + auto never overlap.
    optimize_lock: Mutex<()>,
    /// Serialises freeze/thaw decisions: two overlapping freezes would suspend a process twice, and a single
    /// thaw would then leave it suspended.
    freeze_lock: Mutex<()>,
    save_lock: Mutex<()>,
    listener: Mutex<Option<Listener>>,
    disposed: AtomicBool,
    wake: (Mutex<bool>, Condvar),
}

pub struct Engine {
    inner: Arc<Inner>,
    thread: Mutex<Option<JoinHandle<()>>>,
}

impl Engine {
    pub fn new() -> Engine {
        let _ = std::fs::create_dir_all(paths::data_dir());

        // Enable SeDebugPrivilege first so recovery can also resume elevated processes when we're admin.
        let privileges = sys::enable_privileges();

        // Resume anything a previous session left suspended before we do anything else. Anything we could not
        // resume stays journaled and shows as frozen so the user can thaw it (e.g. after re-launching as
        // administrator).
        recover_frozen_processes();
        let frozen_records = read_frozen_journal();
        let mut frozen_apps: Vec<String> = Vec::new();
        for record in &frozen_records {
            let app = exe_name(&record.name);
            if !contains_ignore_case(&frozen_apps, &app) {
                frozen_apps.push(app);
            }
        }

        let device = detect_device();
        let inner = Arc::new(Inner {
            paths: ConfigPaths::new(&paths::data_dir(), &paths::exe_dir()),
            device,
            privileges,
            first_run: AtomicBool::new(false),
            unsafe_task_found: AtomicBool::new(false),
            state: Mutex::new(State {
                config: EngineConfig::default(),
                frozen_apps,
                frozen_records,
                eco_apps: Vec::new(),
                profile_shields: HashSet::new(),
                last_cap_action: HashMap::new(),
                last_leak_action: HashMap::new(),
                leak_history: LeakHistory::default(),
                last_auto_optimize: None,
                auto_backoff_level: 0,
                last_scheduled_sweep: Instant::now(),
                session_freed_mb: 0.0,
                optimization_count: 0,
                active_matched_profile: None,
                last_launched_profile_id: String::new(),
                commit_level: "ok",
                last_warned_level: "ok",
                last_warning_rank: 0,
                last_commit_warning: None,
                last_repurposed_pages: 0,
                last_repurpose_sample: None,
                repurpose_mb_per_min: -1.0,
                mem_list_retry_at: None,
            }),
            cache: Mutex::new(Cache::default()),
            scan: Mutex::new(ScanState::default()),
            counters: Mutex::new(sys::MemCounters::new()),
            optimize_lock: Mutex::new(()),
            freeze_lock: Mutex::new(()),
            save_lock: Mutex::new(()),
            listener: Mutex::new(None),
            disposed: AtomicBool::new(false),
            wake: (Mutex::new(false), Condvar::new()),
        });

        inner.load_config();
        log::info(format!(
            "PulseRAM {} · {} · admin={} · purgeRights={} · ui={}",
            env!("CARGO_PKG_VERSION"),
            device.label(),
            privileges.is_admin,
            privileges.purge_rights,
            inner.state.lk().config.ui_mode
        ));

        inner.sample(true, true);

        let worker = inner.clone();
        let thread = std::thread::Builder::new()
            .name("pulseram-engine".into())
            .spawn(move || worker.background_loop())
            .map_err(|e| log::error(format!("Could not start the engine thread: {e}")))
            .ok();
        Engine { inner, thread: Mutex::new(thread) }
    }

    /// Receives engine events on a background thread.
    pub fn set_listener(&self, listener: impl Fn(EngineEvent) + Send + Sync + 'static) {
        *self.inner.listener.lk() = Some(Arc::new(listener));
    }

    pub fn device(&self) -> DeviceInfo {
        self.inner.device
    }

    pub fn is_admin(&self) -> bool {
        self.inner.privileges.is_admin
    }

    /// True until the welcome card has been shown once on this install (upgrades from 1.3 count as shown).
    pub fn is_first_run(&self) -> bool {
        self.inner.first_run.load(Ordering::Relaxed)
    }

    /// An old "start as administrator" task points at a folder other programs can change, and removing it
    /// needs administrator rights.
    pub fn unsafe_task_found(&self) -> bool {
        self.inner.unsafe_task_found.load(Ordering::Relaxed)
    }

    pub fn config(&self) -> EngineConfig {
        self.inner.state.lk().config.clone()
    }

    pub fn with_config<T>(&self, read: impl FnOnce(&EngineConfig) -> T) -> T {
        read(&self.inner.state.lk().config)
    }

    /// Changes settings and saves them.
    pub fn update_config(&self, change: impl FnOnce(&mut EngineConfig)) {
        {
            let mut state = self.inner.state.lk();
            change(&mut state.config);
            config::clamp(&mut state.config);
        }
        self.inner.save_config();
    }

    /// Lightweight mode: explicit "lite", or "auto" on a low-end PC.
    pub fn lite_mode(&self) -> bool {
        self.inner.lite_mode()
    }

    /// Returns the latest snapshot. Cheap: the background loop keeps it fresh, so the UI thread never scans
    /// processes.
    pub fn snapshot(&self) -> Arc<MemorySnapshot> {
        self.inner.cache.lk().snapshot.clone()
    }

    /// Returns the cached process list (refreshed by the background loop every ~2 s).
    pub fn processes(&self, grouped: bool) -> Arc<Vec<ProcessItem>> {
        let cache = self.inner.cache.lk();
        if grouped {
            cache.grouped.clone()
        } else {
            cache.individual.clone()
        }
    }

    pub fn events(&self) -> Vec<OptimizationEvent> {
        self.inner.cache.lk().events.clone()
    }

    pub fn telemetry(&self) -> Vec<TelemetryPoint> {
        self.inner.cache.lk().telemetry.iter().copied().collect()
    }

    pub fn governed_counts(&self) -> (usize, usize) {
        let state = self.inner.state.lk();
        (state.frozen_apps.len(), state.eco_apps.len())
    }

    pub fn leak_incidents(&self) -> Vec<LeakIncident> {
        self.inner.state.lk().leak_history.snapshot()
    }

    /// Removes an app from the list (it comes back if it starts leaking again).
    pub fn dismiss_leak(&self, app: &str) {
        self.inner.state.lk().leak_history.dismiss(app, clock::unix_now());
    }

    /// Removes an app from the list and stops tracking it for good (undo by editing LeakIgnoredApps in the
    /// settings file).
    pub fn ignore_leak(&self, app: &str) {
        {
            let mut state = self.inner.state.lk();
            state.leak_history.forget(app);
            if !contains_ignore_case(&state.config.leak_ignored_apps, app) {
                state.config.leak_ignored_apps.push(app.to_string());
            }
        }
        self.inner.save_config();
    }

    pub fn optimize(&self, mode: &str, trigger: &str, reason: &str) -> OptimizationEvent {
        self.inner.optimize(mode, trigger, reason)
    }

    /// Trims one application's processes (working-set trim only).
    pub fn optimize_app(&self, app: &str, reason: &str) -> OptimizationEvent {
        self.inner.optimize_single_app(app, reason)
    }

    /// Adds or removes an app from the shield list. Returns whether it is shielded now.
    pub fn toggle_protect(&self, app: &str) -> bool {
        let now_protected = {
            let mut state = self.inner.state.lk();
            let before = state.config.protected_apps.len();
            state.config.protected_apps.retain(|x| !x.eq_ignore_ascii_case(app));
            if state.config.protected_apps.len() == before {
                state.config.protected_apps.push(app.to_string());
                true
            } else {
                false
            }
        };
        self.inner.save_config();
        self.inner.request_rescan();
        now_protected
    }

    pub fn toggle_freeze(&self, app: &str) -> ActionResult {
        let _guard = self.inner.freeze_lock.lk();
        if self.inner.disposed.load(Ordering::Relaxed) {
            return ActionResult { ok: false, active: false, message: "PulseRAM is shutting down.".into() };
        }
        self.inner.toggle_freeze_core(app)
    }

    /// Resumes every frozen app. Returns how many apps were fully thawed.
    pub fn thaw_all(&self) -> usize {
        self.inner.thaw_all()
    }

    pub fn toggle_eco(&self, app: &str) -> ActionResult {
        self.inner.toggle_eco(app)
    }

    /// Sets (≥ 64 MB) or removes a hard working-set cap for every process of an app.
    pub fn set_app_memory_cap(&self, app: &str, cap_mb: Option<i32>) {
        self.inner.set_app_memory_cap(app, cap_mb);
    }

    pub fn restart_app(&self, app: &str) -> (bool, String) {
        self.inner.restart_app(app)
    }

    pub fn is_startup_enabled(&self) -> bool {
        startup::is_enabled()
    }

    pub fn set_startup(&self, enable: bool) -> StartupResult {
        let result = startup::set_startup(enable, self.inner.privileges.is_admin);
        self.inner.state.lk().config.run_on_startup = result.enabled;
        self.inner.save_config();
        result
    }

    /// Re-applies the hardware-based defaults (thresholds, floor, cooldown, profile triggers).
    pub fn retune_for_this_device(&self) {
        {
            let mut state = self.inner.state.lk();
            config::retune(&mut state.config, self.inner.device.tier);
            state.auto_backoff_level = 0;
        }
        self.inner.save_config();
    }

    /// Stops the background loop and resumes every frozen app. Idempotent.
    pub fn shutdown(&self) {
        if self.inner.disposed.swap(true, Ordering::SeqCst) {
            return;
        }
        {
            let mut stop = self.inner.wake.0.lk();
            *stop = true;
            self.inner.wake.1.notify_all();
        }
        if let Some(thread) = self.thread.lk().take() {
            let _ = thread.join();
        }
        self.inner.thaw_all();
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        self.shutdown();
    }
}

impl Inner {
    fn emit(&self, event: EngineEvent) {
        let listener = self.listener.lk().clone();
        if let Some(listener) = listener {
            listener(event);
        }
    }

    fn lite_mode(&self) -> bool {
        let state = self.state.lk();
        state.config.ui_mode == "lite" || (state.config.ui_mode != "full" && self.device.tier == Tier::Low)
    }

    /// Process scans are the most expensive thing we do; halve their rate in lite mode.
    fn scan_interval(&self) -> Duration {
        Duration::from_millis(if self.lite_mode() { 4400 } else { 2200 })
    }

    // ---- Configuration ----

    fn load_config(&self) {
        let outcome = config::load(&self.paths, self.device.tier);
        if let Some(text) = &outcome.corrupt_text {
            // Keep a copy of an unreadable user config before defaults overwrite it.
            let t = clock::local_now();
            let copy = paths::data_dir().join(format!("pulseram_config.corrupt-{}-{:02}{:02}{:02}.json", t.ymd_compact(), t.hour, t.minute, t.second));
            sys::write_text(&copy, text, false);
            log::warn(format!("Settings could not be read; a copy was kept as {}", copy.display()));
        }
        let mut cfg = outcome.config;
        self.first_run.store(!cfg.welcome_shown, Ordering::Relaxed);

        match startup::audit_elevated_task(self.privileges.is_admin) {
            TaskAudit::UnsafeLeft => self.unsafe_task_found.store(true, Ordering::Relaxed),
            TaskAudit::Fine | TaskAudit::Removed => {}
        }
        let has_elevated_task = startup::elevated_task_exists();
        let mut has_run_key = sys::run_key_exists();
        if has_elevated_task && has_run_key {
            // e.g. an upgrade re-added the installer's Run-key entry: a non-elevated copy started from it could
            // win the single-instance race against the elevated task, so drop it.
            if sys::set_run_key(false, &paths::exe_path()).is_ok() {
                has_run_key = false;
            }
        }
        cfg.run_on_startup = has_elevated_task || has_run_key;

        {
            let mut state = self.state.lk();
            state.eco_apps = cfg.eco_apps.clone();
            state.config = cfg;
        }
        if outcome.needs_save {
            self.save_config();
        }
    }

    fn save_config(&self) {
        let json = {
            let mut state = self.state.lk();
            state.config.eco_apps = state.eco_apps.clone();
            config::to_json(&state.config)
        };
        // Saves come from the UI thread and engine threads; serialise the write + rename.
        let _guard = self.save_lock.lk();
        let path = &self.paths.user;
        if std::fs::create_dir_all(paths::data_dir()).is_err() {
            log::warn(format!("Could not save settings to {}", path.display()));
            return;
        }
        if sys::is_elevated() {
            // Elevated: write through a verified handle (a link planted in the user-writable folder could
            // otherwise redirect the write).
            if !sys::write_text(path, &json, false) {
                log::warn(format!("Not saving settings: {} is a link or redirected", path.display()));
            }
            return;
        }
        let tmp = path.with_extension("json.tmp");
        if let Err(e) = std::fs::write(&tmp, json).and_then(|_| std::fs::rename(&tmp, path)) {
            log::warn(format!("Could not save settings to {}: {e}", path.display()));
        }
    }

    // ---- Background loop: Auto-Pilot, schedule, caps, leak guard ----

    fn background_loop(self: Arc<Self>) {
        loop {
            {
                let stop = self.wake.0.lk();
                let (stop, _) = self.wake.1.wait_timeout_while(stop, TICK, |stop| !*stop).unwrap_or_else(|e| e.into_inner());
                if *stop {
                    return;
                }
            }
            // Never let one bad tick end the loop, but record why it hiccupped.
            let this = self.clone();
            if let Err(panic) = std::panic::catch_unwind(std::panic::AssertUnwindSafe(move || this.tick())) {
                let detail = panic.downcast_ref::<&str>().map(|s| s.to_string()).or_else(|| panic.downcast_ref::<String>().cloned()).unwrap_or_default();
                log::error(format!("Background tick failed: {detail}"));
            }
        }
    }

    fn effective_cooldown_seconds(state: &State) -> i32 {
        policy::backoff_cooldown_seconds(state.config.cooldown_seconds, state.auto_backoff_level)
    }

    fn tick(self: &Arc<Self>) {
        if self.disposed.load(Ordering::Relaxed) {
            return;
        }
        let snap = self.sample(false, true);
        self.check_commit_pressure(&snap);

        // 1. Scheduled maintenance sweep
        let scheduled = {
            let mut state = self.state.lk();
            let due = state.config.scheduled_trim_enabled
                && state.config.scheduled_interval_minutes > 0
                && state.last_scheduled_sweep.elapsed().as_secs() >= state.config.scheduled_interval_minutes as u64 * 60;
            if due {
                state.last_scheduled_sweep = Instant::now();
                Some((state.config.auto_mode.clone(), state.config.scheduled_interval_minutes))
            } else {
                None
            }
        };
        if let Some((mode, minutes)) = scheduled {
            self.optimize(&mode, "Scheduled", &format!("Scheduled {minutes}m maintenance sweep"));
            return;
        }

        // 2. Auto-Pilot thresholds with adaptive back-off
        let threshold = snap.effective_threshold_percent;
        let auto = {
            let mut state = self.state.lk();
            if !state.config.auto_enabled {
                None
            } else {
                let floor = state.config.min_free_ram_gb;
                let over_percent = snap.percent >= f64::from(threshold);
                let under_floor = snap.available_gb <= floor;
                if !over_percent && !under_floor {
                    // Pressure has cleared with some margin: reset the back-off.
                    if snap.percent < f64::from(threshold - 5) && snap.available_gb > floor + 0.5 {
                        state.auto_backoff_level = 0;
                    }
                    None
                } else {
                    let cooled_down = state.last_auto_optimize.is_none_or(|t| t.elapsed().as_secs() >= Self::effective_cooldown_seconds(&state) as u64);
                    let cpu_busy = state.config.pause_on_high_cpu && snap.cpu_percent >= state.config.cpu_guard_percent;
                    if cooled_down && !cpu_busy {
                        state.last_auto_optimize = Some(Instant::now());
                        let reason = if over_percent {
                            format!("RAM reached {:.1}% (Threshold: {threshold}%)", snap.percent)
                        } else {
                            format!("Available RAM dropped to {:.2} GB (Floor: {floor:.1} GB)", snap.available_gb)
                        };
                        Some((reason, floor))
                    } else {
                        None
                    }
                }
            }
        };
        if let Some((reason, floor)) = auto {
            let event = self.optimize(&snap.effective_mode, "Auto-Pilot", &reason);
            // If the sweep did not get us back under the trigger, the memory is genuinely in use: trimming
            // again soon would only cause page-fault churn, so back off exponentially.
            let after = self.cache.lk().snapshot.clone();
            let still_pressured = after.percent >= f64::from(threshold) || after.available_gb <= floor;
            // A deliberate no-pressure skip is temporary: never back off for it, or a later real shortage
            // could wait up to 30 minutes.
            if !event.skipped_no_pressure && (still_pressured || event.skipped) {
                let mut state = self.state.lk();
                state.auto_backoff_level = (state.auto_backoff_level + 1).min(policy::MAX_BACKOFF_LEVEL);
            }
            return;
        }

        // 3. Per-app memory caps & runaway leak guard
        self.enforce_app_caps_and_leak_guard(&snap.foreground_app);
    }

    fn enforce_app_caps_and_leak_guard(self: &Arc<Self>, foreground_app: &str) {
        let grouped = self.cache.lk().grouped.clone();
        let now = Instant::now();

        // Per-process hard caps: Windows enforces the limit on processes that already have it, so this only has
        // to catch NEW processes of a capped app. Checked at most once per CapGuardCooldownSeconds.
        let due_caps: Vec<(String, i32)> = {
            let mut state = self.state.lk();
            let cooldown = state.config.cap_guard_cooldown_seconds as u64;
            let caps: Vec<(String, i32)> = state.config.app_memory_caps_mb.iter().map(|(k, v)| (k.clone(), *v)).collect();
            caps.into_iter()
                .filter(|(app, cap)| *cap >= 64 && !is_critical(app))
                .filter(|(app, _)| {
                    let key = app.to_lowercase();
                    let due = state.last_cap_action.get(&key).is_none_or(|last| now.duration_since(*last).as_secs() >= cooldown);
                    if due {
                        state.last_cap_action.insert(key, now);
                    }
                    due
                })
                .collect()
        };
        for (app, cap) in due_caps {
            self.enforce_hard_cap(&app, cap, false);
        }

        for item in grouped.iter() {
            if is_critical(&item.name) {
                continue;
            }

            // Leak history: remember every app that keeps growing, including the one in the foreground (the
            // guard below never touches that one).
            if item.is_leaking && item.private_mb >= 300.0 {
                let incident = {
                    let mut state = self.state.lk();
                    let ignored = state.config.leak_ignored_apps.clone();
                    let notify = state.leak_history.record(&item.name, item.leak_rate_mb_per_min, item.private_mb, clock::unix_now(), &ignored);
                    if notify { state.leak_history.get(&item.name) } else { None }
                };
                if let Some(incident) = incident {
                    log::warn(format!("Possible leak: {} private memory +{:.0} MB/min ({:.0} MB)", item.name, item.leak_rate_mb_per_min, item.private_mb));
                    self.emit(EngineEvent::LeakDetected(incident));
                }
            }

            if item.name.eq_ignore_ascii_case(foreground_app) {
                continue;
            }

            // Leak guard: sustained growth of PRIVATE memory. Trimming only relieves physical RAM; the leaked
            // memory stays committed until the app is restarted, so we say so.
            let act = {
                let mut state = self.state.lk();
                let wanted = state.config.auto_leak_remediation
                    && !item.is_protected
                    && item.is_leaking
                    && item.leak_rate_mb_per_min >= state.config.leak_velocity_threshold_mb_min
                    && item.private_mb >= 300.0;
                let cooldown = state.config.leak_guard_cooldown_seconds as u64;
                let key = item.name.to_lowercase();
                let due = wanted && state.last_leak_action.get(&key).is_none_or(|last| now.duration_since(*last).as_secs() >= cooldown);
                if due {
                    state.last_leak_action.insert(key, now);
                }
                due
            };
            if act {
                self.optimize_single_app(
                    &item.name,
                    &format!(
                        "Leak Guard: private memory growing +{:.0} MB/min — working set trimmed; restart the app to release leaked memory",
                        item.leak_rate_mb_per_min
                    ),
                );
            }
        }
    }

    // ---- Commit charge (RAM + page file) warning ----

    /// Trimming can't help here: committed memory counts against RAM + page file whether or not it is in RAM.
    /// When it nears the limit, new allocations fail ("out of memory", apps crash). On every rise the banner
    /// is shown; the Windows notification for the same level is not repeated within 30 minutes, so hovering
    /// around a threshold can't spam.
    fn check_commit_pressure(&self, snap: &MemorySnapshot) {
        let level = snap.commit_level;
        let repeat = {
            let mut state = self.state.lk();
            if !state.config.commit_warning_enabled || level == "ok" {
                // While disabled, forget what was announced so re-enabling during pressure warns at once.
                state.last_warned_level = "ok";
                return;
            }
            let rank = if level == "critical" { 2 } else { 1 };
            let last_rank = match state.last_warned_level {
                "critical" => 2,
                "high" => 1,
                _ => 0,
            };
            state.last_warned_level = level;
            if rank <= last_rank {
                return; // steady or easing off: nothing new to say
            }
            // The banner is always (re)shown on escalation; only the Windows notification is rate-limited.
            let repeat = rank <= state.last_warning_rank && state.last_commit_warning.is_some_and(|t| t.elapsed() < Duration::from_secs(30 * 60));
            if !repeat {
                state.last_warning_rank = rank;
                state.last_commit_warning = Some(Instant::now());
            }
            repeat
        };
        let mut advice = self.build_commit_advice(snap);
        advice.is_repeat = repeat;
        if !repeat {
            log::warn(format!("Commit charge {:.0}% ({:.1}/{:.1} GB): {}", snap.commit_percent, snap.commit_total_gb, snap.commit_limit_gb, advice.title));
        }
        self.emit(EngineEvent::CommitWarning(advice));
    }

    fn build_commit_advice(&self, snap: &MemorySnapshot) -> CommitAdvice {
        let top = {
            let grouped = self.cache.lk().grouped.clone();
            grouped
                .iter()
                .filter(|p| !is_critical(&p.name))
                .max_by(|a, b| a.private_mb.total_cmp(&b.private_mb))
                .filter(|p| p.private_mb >= 300.0)
                .map(|p| format!(" {} is using the most ({:.1} GB committed).", p.name, p.private_mb / 1024.0))
                .unwrap_or_default()
        };
        let (kind, max_mb) = sys::page_file_setup();
        let level = snap.commit_level;
        let usage = format!("{:.1} of {:.1} GB committed ({:.0}%).", snap.commit_total_gb, snap.commit_limit_gb, snap.commit_percent);
        let title = if level == "critical" { "Almost out of memory" } else { "Running low on memory" }.to_string();
        let (body, suggest_page_file) = match kind {
            PageFileKind::None => (
                format!("{usage} There is no page file, so apps will crash when this hits 100%. Turn on \"System managed size\" in page-file settings.{top}"),
                true,
            ),
            PageFileKind::Custom => (
                format!("{usage} Your page file is capped at {:.1} GB. Raise the maximum or choose \"System managed size\".{top}", f64::from(max_mb) / 1024.0),
                true,
            ),
            _ => (format!("{usage} Freeing RAM won't help — close or restart apps you don't need.{top}"), kind == PageFileKind::Unknown),
        };
        CommitAdvice { level, title, body, suggest_page_file, is_repeat: false }
    }

    // ---- Sampling ----

    fn foreground(&self) -> (u32, String) {
        let pid = sys::foreground_pid();
        if pid == 0 {
            return (0, String::new());
        }
        match sys::process_name(pid) {
            Some(name) => (pid, name),
            None => (0, String::new()),
        }
    }

    fn sample(self: &Arc<Self>, force_process_scan: bool, record_telemetry: bool) -> Arc<MemorySnapshot> {
        let mem = sys::memory_status().unwrap_or_default();
        let perf = sys::perf_info().unwrap_or_default();

        let page_size = if perf.page_size > 0 { perf.page_size as f64 } else { 4096.0 };
        let total_gb = mem.total_phys as f64 / GB;
        let avail_gb = mem.avail_phys as f64 / GB;
        let used_gb = (total_gb - avail_gb).max(0.0);
        let percent = if total_gb > 0.0 { used_gb / total_gb * 100.0 } else { f64::from(mem.memory_load) };

        let (standby_gb, free_gb, lists_measured) = match self.counters.lk().read() {
            Some((standby_bytes, free_bytes)) => (standby_bytes / GB, free_bytes / GB, true),
            None => {
                // Fallback estimate: system file-cache working set. Flagged via memory_lists_measured = false.
                let standby = avail_gb.min(perf.system_cache as f64 * page_size / GB);
                (standby, (avail_gb - standby).max(0.0), false)
            }
        };

        // Kernel memory lists: standby per priority and the cumulative count of standby pages Windows has
        // repurposed. Needs SeProfileSingleProcessPrivilege.
        let mut standby_by_priority = None;
        if self.privileges.purge_rights {
            let retry_now = self.state.lk().mem_list_retry_at.is_none_or(|at| Instant::now() >= at);
            if retry_now {
                match sys::query_memory_lists() {
                    Some(lists) => {
                        standby_by_priority = Some(lists.standby_pages_by_priority.map(|pages| pages as f64 * page_size / GB));
                        let now = Instant::now();
                        let mut state = self.state.lk();
                        state.mem_list_retry_at = None;
                        let mut advance = state.last_repurpose_sample.is_none() || lists.repurposed_pages < state.last_repurposed_pages;
                        if let (false, Some(last)) = (advance, state.last_repurpose_sample) {
                            let minutes = now.duration_since(last).as_secs_f64() / 60.0;
                            if minutes >= 0.02 {
                                let mb_per_min = (lists.repurposed_pages - state.last_repurposed_pages) as f64 * page_size / MB / minutes;
                                // Smooth it so one burst doesn't flip the decision (weight grows with the interval).
                                let w = (minutes / 0.25).clamp(0.1, 1.0) * 0.5;
                                state.repurpose_mb_per_min =
                                    if state.repurpose_mb_per_min < 0.0 { mb_per_min } else { state.repurpose_mb_per_min * (1.0 - w) + mb_per_min * w };
                                advance = true;
                            }
                        }
                        if advance {
                            state.last_repurposed_pages = lists.repurposed_pages;
                            state.last_repurpose_sample = Some(now);
                        }
                    }
                    None => self.state.lk().mem_list_retry_at = Some(Instant::now() + Duration::from_secs(300)),
                }
            }
        }

        let commit_total_gb = perf.commit_total as f64 * page_size / GB;
        let commit_limit_gb = (perf.commit_limit as f64 * page_size / GB).max(0.1);
        let commit_percent = commit_total_gb / commit_limit_gb * 100.0;

        let pf_total_gb = (mem.total_page_file as f64 / GB - total_gb).max(0.0);
        let pf_avail_gb = (mem.avail_page_file as f64 / GB - avail_gb).max(0.0);
        let pf_used_gb = (pf_total_gb - pf_avail_gb).max(0.0);
        let pf_percent = if pf_total_gb > 0.05 { (pf_used_gb / pf_total_gb * 100.0).min(100.0) } else { 0.0 };

        let (_, fg_name) = self.foreground();

        let scan_due = force_process_scan || {
            let interval = self.scan_interval();
            self.scan.try_lock().map(|scan| scan.last_scan.is_none_or(|t| t.elapsed() >= interval)).unwrap_or(false)
        };
        if scan_due {
            self.refresh_process_snapshots(&fg_name, false);
        }

        let pressure_level = if percent >= 85.0 {
            "CRITICAL"
        } else if percent >= 72.0 {
            "ELEVATED"
        } else {
            "OPTIMAL"
        };

        let mut snapshot = MemorySnapshot {
            total_gb: round_to(total_gb, 2),
            used_gb: round_to(used_gb, 2),
            available_gb: round_to(avail_gb, 2),
            standby_gb: round_to(standby_gb, 2),
            free_gb: round_to(free_gb, 2),
            memory_lists_measured: lists_measured,
            standby_by_priority_gb: standby_by_priority,
            percent: round_to(percent, 1),
            pressure_level,
            commit_total_gb: round_to(commit_total_gb, 2),
            commit_limit_gb: round_to(commit_limit_gb, 2),
            commit_percent: round_to(commit_percent, 1),
            pagefile_total_gb: round_to(pf_total_gb, 2),
            pagefile_used_gb: round_to(pf_used_gb, 2),
            pagefile_percent: round_to(pf_percent, 1),
            kernel_total_mb: (perf.kernel_total as f64 * page_size / MB).round(),
            kernel_paged_mb: (perf.kernel_paged as f64 * page_size / MB).round(),
            kernel_nonpaged_mb: (perf.kernel_nonpaged as f64 * page_size / MB).round(),
            process_count: perf.process_count,
            foreground_app: fg_name,
            standby_purge_supported: self.privileges.purge_rights,
            commit_level: "ok",
            active_profile_name: "Balanced Default".into(),
            ..Default::default()
        };

        {
            let mut state = self.state.lk();
            // Purging standby only helps when free memory is actually scarce: otherwise Windows hands out free
            // pages anyway and the purge just throws away useful cache. Repurposing confirms real pressure.
            snapshot.repurpose_mb_per_min = state.repurpose_mb_per_min.round();
            snapshot.standby_purge_worthwhile = policy::standby_purge_worthwhile(total_gb, standby_gb, free_gb, state.repurpose_mb_per_min);
            // Hysteresis so the warning doesn't flicker around a threshold.
            state.commit_level = policy::next_commit_level(state.commit_level, commit_percent);
            snapshot.commit_level = state.commit_level;

            // Resolve the effective profile & threshold.
            let cfg = &state.config;
            snapshot.effective_threshold_percent = cfg.ram_threshold_percent;
            snapshot.effective_mode = cfg.auto_mode.clone();
            let forced = if cfg.forced_profile_id.trim().is_empty() { None } else { cfg.profiles.iter().find(|p| p.id == cfg.forced_profile_id) };
            if cfg.focus_boost_active {
                snapshot.active_profile_name = "Focus Boost Overdrive".into();
                snapshot.effective_threshold_percent = cfg.focus_boost_threshold_percent;
                snapshot.effective_mode = "smart".into();
            } else if let Some(profile) = forced.or(if cfg.forced_profile_id.trim().is_empty() { state.active_matched_profile.as_ref() } else { None }) {
                snapshot.active_profile_name = profile.name.clone();
                snapshot.effective_threshold_percent = profile.ram_threshold_percent;
                snapshot.effective_mode = profile.auto_mode.clone();
            }

            if cfg.scheduled_trim_enabled && cfg.scheduled_interval_minutes > 0 {
                let total = i64::from(cfg.scheduled_interval_minutes) * 60;
                snapshot.next_scheduled_seconds = (total - state.last_scheduled_sweep.elapsed().as_secs() as i64).max(0);
            }
            snapshot.session_freed_mb = round_to(state.session_freed_mb, 1);
            snapshot.optimization_count = state.optimization_count;
            snapshot.effective_cooldown_seconds = Self::effective_cooldown_seconds(&state);
        }

        let mut cache = self.cache.lk();
        snapshot.cpu_percent = round_to(cache.system_cpu_percent, 1);
        if record_telemetry {
            cache.telemetry.push_back(TelemetryPoint { used_percent: snapshot.percent, cpu_percent: snapshot.cpu_percent });
            // One point per 1.5 s tick → 40 points ≈ the 60-second window the chart advertises.
            while cache.telemetry.len() > 40 {
                cache.telemetry.pop_front();
            }
        }
        let snapshot = Arc::new(snapshot);
        cache.snapshot = snapshot.clone();
        snapshot
    }

    fn refresh_process_snapshots(self: &Arc<Self>, foreground_app: &str, wait_for_lock: bool) {
        let mut scan = if wait_for_lock {
            self.scan.lk()
        } else {
            match self.scan.try_lock() {
                Ok(guard) => guard,
                Err(std::sync::TryLockError::Poisoned(e)) => e.into_inner(),
                Err(std::sync::TryLockError::WouldBlock) => return,
            }
        };
        let now = Instant::now();
        scan.last_scan = Some(now);
        let core_count = sys::logical_cores().max(1) as f64;

        let raw = sys::list_processes();

        let (frozen, eco, protected, shields, caps, protect_fg, leak_threshold, auto_switch, profiles) = {
            let state = self.state.lk();
            (
                state.frozen_apps.clone(),
                state.eco_apps.clone(),
                state.config.protected_apps.clone(),
                state.profile_shields.clone(),
                state.config.app_memory_caps_mb.clone(),
                state.config.protect_foreground,
                state.config.leak_velocity_threshold_mb_min,
                state.config.profiles_auto_switch,
                state.config.profiles.clone(),
            )
        };
        let cap_for = |name: &str| caps.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| *v);
        let is_shielded = |name: &str| is_critical(name) || contains_ignore_case(&protected, name) || shields.contains(&name.to_lowercase());

        let mut individual: Vec<ProcessItem> = Vec::with_capacity(raw.len());
        let mut running: HashSet<String> = HashSet::with_capacity(raw.len());
        let mut seen_pids: HashSet<u32> = HashSet::with_capacity(raw.len());
        let mut pid_cpu: HashMap<u32, f64> = HashMap::with_capacity(raw.len());

        for process in &raw {
            if process.pid <= 4 {
                continue;
            }
            running.insert(process.name.to_lowercase());
            seen_pids.insert(process.pid);

            let mut cpu_percent = 0.0;
            if let Some((previous_time, previous_cpu)) = scan.proc_cpu_samples.get(&process.pid) {
                let dt = now.duration_since(*previous_time).as_secs_f64();
                if dt > 0.1 {
                    let cpu_used = process.cpu_time.saturating_sub(*previous_cpu) as f64 / 10_000_000.0;
                    cpu_percent = (cpu_used / (dt * core_count) * 100.0).clamp(0.0, 100.0);
                    pid_cpu.insert(process.pid, cpu_percent);
                }
            }
            scan.proc_cpu_samples.insert(process.pid, (now, process.cpu_time));

            let ws_mb = process.working_set as f64 / MB;
            if ws_mb < 4.0 {
                continue;
            }
            let is_foreground = process.name.eq_ignore_ascii_case(foreground_app);
            let is_protected = is_shielded(&process.name);
            let is_frozen = contains_ignore_case(&frozen, &process.name);
            let is_eco = contains_ignore_case(&eco, &process.name);
            let smart_score = compute_smart_score(ws_mb, cpu_percent, 0.0, is_foreground, is_protected);
            individual.push(ProcessItem {
                name: process.name.clone(),
                pid: process.pid,
                process_count: 1,
                memory_mb: round_to(ws_mb, 1),
                private_mb: round_to(process.private_bytes as f64 / MB, 1),
                peak_memory_mb: round_to(process.peak_working_set as f64 / MB, 1),
                cpu_percent: round_to(cpu_percent, 1),
                leak_rate_mb_per_min: 0.0,
                is_leaking: false,
                is_foreground,
                is_protected,
                is_frozen,
                is_eco,
                cap_mb: cap_for(&process.name),
                smart_score,
                reclaimable_mb: if is_protected || (is_foreground && protect_fg) { 0.0 } else { round_to(ws_mb, 1) },
                badge: resolve_badge(is_frozen, is_eco, is_foreground, is_protected, false, cpu_percent, smart_score),
            });
        }

        // System CPU %: GetSystemTimes covers every process (including ones we can't open) and isn't skewed by
        // processes starting or exiting. Kernel time includes idle time.
        let mut system_cpu = None;
        if let Some((idle, kernel, user)) = sys::system_times() {
            if let Some((last_idle, last_kernel, last_user)) = scan.last_sys_times {
                let total = kernel.wrapping_sub(last_kernel) as f64 + user.wrapping_sub(last_user) as f64;
                let idle_delta = idle.wrapping_sub(last_idle) as f64;
                if total > 0.0 {
                    system_cpu = Some(((1.0 - idle_delta / total) * 100.0).clamp(0.0, 100.0));
                }
            }
            scan.last_sys_times = Some((idle, kernel, user));
        }

        // Drop per-PID / per-app history for processes that have exited (prevents unbounded growth).
        scan.proc_cpu_samples.retain(|pid, _| seen_pids.contains(pid));
        scan.app_history.retain(|app, _| running.contains(app));

        // Group by application name.
        let mut group_index: HashMap<String, usize> = HashMap::new();
        let mut groups: Vec<Vec<&ProcessItem>> = Vec::new();
        for item in &individual {
            let index = *group_index.entry(item.name.to_lowercase()).or_insert_with(|| {
                groups.push(Vec::new());
                groups.len() - 1
            });
            groups[index].push(item);
        }
        let mut grouped: Vec<ProcessItem> = groups
            .iter()
            .map(|members| {
                let name = members[0].name.clone();
                let sum_mb: f64 = members.iter().map(|m| m.memory_mb).sum();
                let sum_private: f64 = members.iter().map(|m| m.private_mb).sum();
                let peak_mb = members.iter().map(|m| m.peak_memory_mb).fold(0.0, f64::max);
                let sum_cpu = members.iter().map(|m| m.cpu_percent).sum::<f64>().min(100.0);
                let is_foreground = members.iter().any(|m| m.is_foreground);
                let is_protected = members.iter().any(|m| m.is_protected);
                let is_frozen = contains_ignore_case(&frozen, &name);
                let is_eco = contains_ignore_case(&eco, &name);

                let leak_rate = update_leak_rate(&mut scan.app_history, &name, sum_private, now);
                let is_leaking = leak_rate >= leak_threshold && sum_private >= 200.0;
                let smart_score = compute_smart_score(sum_mb, sum_cpu, leak_rate, is_foreground, is_protected);
                ProcessItem {
                    pid: members[0].pid,
                    process_count: members.len() as u32,
                    memory_mb: round_to(sum_mb, 1),
                    private_mb: round_to(sum_private, 1),
                    peak_memory_mb: round_to(peak_mb, 1),
                    cpu_percent: round_to(sum_cpu, 1),
                    leak_rate_mb_per_min: round_to(leak_rate, 1),
                    is_leaking,
                    is_foreground,
                    is_protected,
                    is_frozen,
                    is_eco,
                    cap_mb: cap_for(&name),
                    smart_score,
                    reclaimable_mb: if is_protected || (is_foreground && protect_fg) { 0.0 } else { round_to(sum_mb, 1) },
                    badge: resolve_badge(is_frozen, is_eco, is_foreground, is_protected, is_leaking, sum_cpu, smart_score),
                    name,
                }
            })
            .collect();
        grouped.sort_by(|a, b| b.memory_mb.total_cmp(&a.memory_mb));
        grouped.truncate(65);
        drop(groups);
        individual.sort_by(|a, b| b.memory_mb.total_cmp(&a.memory_mb));
        individual.truncate(65);

        // Auto-detected workload profiles. Shields for matched EXEs live only while the profile is active;
        // they are never written into the user's ProtectedApps list.
        let mut launch_boost = None;
        {
            let mut state = self.state.lk();
            if auto_switch {
                let matched = profiles.iter().find(|p| p.enabled && p.match_exes.iter().any(|exe| running.contains(&exe.to_lowercase())));
                state.profile_shields = match matched {
                    Some(profile) if profile.auto_shield_matched => {
                        profile.match_exes.iter().map(|exe| exe.to_lowercase()).filter(|exe| running.contains(exe)).collect()
                    }
                    _ => HashSet::new(),
                };
                match matched {
                    Some(profile) if profile.boost_on_launch && state.last_launched_profile_id != profile.id => {
                        state.last_launched_profile_id = profile.id.clone();
                        state.last_auto_optimize = Some(Instant::now());
                        launch_boost = Some(profile.clone());
                    }
                    None => state.last_launched_profile_id.clear(),
                    _ => {}
                }
                state.active_matched_profile = matched.cloned();
            } else {
                state.profile_shields = HashSet::new();
                state.active_matched_profile = None;
            }
        }

        {
            let mut cache = self.cache.lk();
            cache.grouped = Arc::new(grouped);
            cache.individual = Arc::new(individual);
            cache.pid_cpu = Arc::new(pid_cpu);
            if let Some(cpu) = system_cpu {
                cache.system_cpu_percent = cpu;
            }
        }
        drop(scan);

        if let Some(profile) = launch_boost {
            let this = self.clone();
            std::thread::spawn(move || {
                if !this.disposed.load(Ordering::Relaxed) {
                    this.optimize(&profile.auto_mode, "Profile Launch Boost", &format!("Auto-activated '{}' profile", profile.name));
                }
            });
        }
    }

    /// Runs the rescan off the caller's (UI) thread.
    fn request_rescan(self: &Arc<Self>) {
        let this = self.clone();
        std::thread::spawn(move || {
            if !this.disposed.load(Ordering::Relaxed) {
                let (_, foreground) = this.foreground();
                this.refresh_process_snapshots(&foreground, false);
            }
        });
    }

    // ---- Core memory optimization ----

    /// Modes:
    ///  smart   — trims idle (low-CPU) background processes above MinProcessWorkingSetMb.
    ///  deep    — trims every eligible process; with admin also combines pages, flushes the modified list and
    ///            purges the LOW-priority standby list.
    ///  standby — admin only: flushes the modified list and purges the whole standby list.
    ///  max     — deep trim + page combine + modified flush + full standby purge (admin for the kernel part).
    /// Shielded, critical and (optionally) foreground processes are never trimmed in any mode.
    fn optimize(self: &Arc<Self>, mode: &str, trigger: &str, reason: &str) -> OptimizationEvent {
        let _guard = self.optimize_lock.lk();
        let _boost = sys::SweepBoost::begin();
        self.optimize_core(mode, trigger, reason)
    }

    fn optimize_core(self: &Arc<Self>, mode: &str, trigger: &str, reason: &str) -> OptimizationEvent {
        let started = Instant::now();
        let before = self.sample(false, false);
        let (_, foreground) = self.foreground();

        let mode = mode.to_lowercase();
        let mode = if config::is_known_mode(&mode) { mode.as_str() } else { "smart" };
        let purge_rights = self.privileges.purge_rights;
        let wants_kernel = matches!(mode, "deep" | "standby" | "max");

        let (smart_purge, min_ws, idle_ceiling, protect_fg, protected, shields) = {
            let state = self.state.lk();
            (
                state.config.smart_standby_purge,
                state.config.min_process_working_set_mb,
                state.config.idle_cpu_ceiling_percent,
                state.config.protect_foreground,
                state.config.protected_apps.clone(),
                state.profile_shields.clone(),
            )
        };

        // Smart standby purge: on automatic sweeps, skip the cache purge unless it would actually help.
        let skip_purge_no_pressure = wants_kernel && purge_rights && smart_purge && is_automatic_trigger(trigger) && !before.standby_purge_worthwhile;
        let no_pressure_note = if before.standby_gb < (before.total_gb * 0.10).max(0.5) {
            format!("Standby purge skipped: the cache is small ({:.1} GB), so there is little to gain.", before.standby_gb)
        } else {
            format!(
                "Standby purge skipped: {:.1} GB already free and Windows isn't evicting cache, so purging would only discard useful cache.",
                before.free_gb
            )
        };

        let base_event = |skipped_note: String| OptimizationEvent {
            timestamp: clock::local_now().hms(),
            mode: "STANDBY".into(),
            trigger: trigger.into(),
            reason: reason.into(),
            before_percent: before.percent,
            after_percent: before.percent,
            duration_ms: started.elapsed().as_millis() as u64,
            skipped: true,
            note: skipped_note,
            ..Default::default()
        };

        if mode == "standby" && skip_purge_no_pressure {
            // Nothing worth doing right now: not logged or announced, and the next tick simply re-checks.
            return OptimizationEvent { skipped_no_pressure: true, ..base_event(no_pressure_note) };
        }
        if mode == "standby" && !purge_rights {
            return self.record_event(base_event("Standby purge needs administrator rights — nothing was changed.".into()), false, trigger);
        }

        let mut trimmed_count = 0u32;
        let mut trimmed_bytes = 0u64;
        if matches!(mode, "smart" | "deep" | "max") {
            // Smart mode needs per-process CPU deltas (two scans). Right after launch only the baseline scan
            // exists, so take the second one now instead of trimming nothing.
            if mode == "smart" && self.cache.lk().pid_cpu.is_empty() {
                self.refresh_process_snapshots(&foreground, true);
            }
            let pid_cpu = self.cache.lk().pid_cpu.clone();
            let is_shielded = |name: &str| is_critical(name) || contains_ignore_case(&protected, name) || shields.contains(&name.to_lowercase());

            for process in sys::list_processes() {
                if process.pid <= 4 || is_shielded(&process.name) {
                    continue;
                }
                if protect_fg && process.name.eq_ignore_ascii_case(&foreground) {
                    continue;
                }
                if mode == "smart" {
                    if (process.working_set as f64 / MB) < min_ws {
                        continue;
                    }
                    // "Idle only": skip processes we have no CPU sample for, or that are busy.
                    match pid_cpu.get(&process.pid) {
                        Some(cpu) if *cpu <= idle_ceiling => {}
                        _ => continue,
                    }
                }
                if let Some(bytes) = sys::trim_process(process.pid, true) {
                    trimmed_count += 1;
                    trimmed_bytes += bytes;
                }
            }
            // Note: the system-wide MemoryEmptyWorkingSets command is deliberately NOT used in "max": it would
            // also trim shielded, critical and foreground processes.
        }

        let mut standby_purged = false;
        let mut combined_mb = 0.0;
        if wants_kernel && purge_rights {
            if matches!(mode, "deep" | "max") {
                if let Some(pages) = sys::combine_pages() {
                    combined_mb = pages as f64 * 4096.0 / MB;
                }
            }
            if !skip_purge_no_pressure {
                sys::flush_modified_list();
                standby_purged = sys::purge_standby(mode == "deep");
            }
        }

        std::thread::sleep(Duration::from_millis(150));
        let after = self.sample(true, false);

        let available_gain_mb = ((after.available_gb - before.available_gb) * 1024.0).max(0.0);
        let free_gain_mb =
            if before.memory_lists_measured && after.memory_lists_measured { ((after.free_gb - before.free_gb) * 1024.0).max(0.0) } else { 0.0 };
        let trimmed_mb = trimmed_bytes as f64 / MB;

        let note = if wants_kernel && !purge_rights {
            "Kernel purge skipped — run PulseRAM as administrator to purge the standby list.".to_string()
        } else if skip_purge_no_pressure {
            no_pressure_note
        } else {
            String::new()
        };

        self.record_event(
            OptimizationEvent {
                timestamp: clock::local_now().hms(),
                mode: mode.to_uppercase(),
                trigger: trigger.into(),
                reason: reason.into(),
                // A pure standby purge trims nothing; its measurable effect is pages moved to the free list.
                freed_mb: round_to(if mode == "standby" { free_gain_mb } else { trimmed_mb }, 1),
                available_gain_mb: round_to(available_gain_mb, 1),
                free_list_gain_mb: round_to(free_gain_mb, 1),
                before_percent: before.percent,
                after_percent: after.percent,
                processes_trimmed: trimmed_count,
                duration_ms: started.elapsed().as_millis() as u64,
                standby_purged,
                combined_mb: round_to(combined_mb, 1),
                note,
                ..Default::default()
            },
            true,
            trigger,
        )
    }

    fn record_event(&self, event: OptimizationEvent, count_as_optimization: bool, trigger: &str) -> OptimizationEvent {
        let chime = {
            let mut state = self.state.lk();
            if count_as_optimization {
                // The session counter is "working set trimmed"; a pure standby purge trims nothing.
                if event.mode != "STANDBY" {
                    state.session_freed_mb += event.freed_mb;
                }
                state.optimization_count += 1;
            }
            let mut cache = self.cache.lk();
            cache.events.insert(0, event.clone());
            cache.events.truncate(40);
            // Refresh the cached snapshot's session counters for the UI.
            let mut snapshot = (*cache.snapshot).clone();
            snapshot.session_freed_mb = round_to(state.session_freed_mb, 1);
            snapshot.optimization_count = state.optimization_count;
            cache.snapshot = Arc::new(snapshot);
            count_as_optimization && state.config.auto_sound_chime && trigger != "Manual"
        };
        log::info(format!(
            "{} [{}] {} → trimmed {:.0} MB from {}, avail +{:.0} MB, free +{:.0} MB, standby={}, {} ms{}",
            event.mode,
            event.trigger,
            event.reason,
            event.freed_mb,
            event.processes_trimmed,
            event.available_gain_mb,
            event.free_list_gain_mb,
            event.standby_purged,
            event.duration_ms,
            if event.note.is_empty() { String::new() } else { format!(" · {}", event.note) }
        ));
        if chime {
            sys::message_beep();
        }
        self.emit(EngineEvent::Optimization(event.clone()));
        event
    }

    fn processes_named(app: &str) -> Vec<ProcInfo> {
        sys::list_processes().into_iter().filter(|p| p.pid > 4 && same_app(&p.name, app)).collect()
    }

    /// Caps, freezing and Eco only ever touch processes in the user's own desktop session. Settings files can
    /// be edited by any program the user runs, so an elevated PulseRAM must not let them aim it at services or
    /// security software (session 0) or other users' sessions.
    fn in_user_session(process: &ProcInfo) -> bool {
        if process.session_id == 0 || process.session_id != sys::own_session_id() {
            return false;
        }
        // Elevated PulseRAM: also leave elevated / SYSTEM processes on the desktop alone (installers, admin
        // tools, vendor agents). Only medium-or-lower integrity apps, i.e. ordinary user apps, qualify.
        if sys::is_elevated() {
            return sys::integrity_level(process.pid).is_some_and(|level| level < 0x3000);
        }
        true
    }

    /// Applies a HARD per-process working-set maximum to every process of an app. With `force` false, only
    /// processes that don't already carry the cap are touched, so repeated checks cost nothing and never
    /// re-trim capped processes. Returns the number of processes the cap was applied to.
    fn enforce_hard_cap(&self, app: &str, cap_mb: i32, force: bool) -> u32 {
        let _guard = self.optimize_lock.lk();
        let started = Instant::now();
        let cap_bytes = cap_mb.max(0) as u64 * 1024 * 1024;
        let before_percent = self.cache.lk().snapshot.percent;
        let (mut applied, mut refused, mut trimmed_bytes) = (0u32, 0u32, 0u64);

        for process in Self::processes_named(app) {
            if !Self::in_user_session(&process) {
                refused += 1;
                continue;
            }
            match sys::apply_hard_cap(process.pid, cap_bytes, force) {
                CapOutcome::Applied(bytes) => {
                    applied += 1;
                    trimmed_bytes += bytes;
                }
                CapOutcome::AlreadySet => {}
                CapOutcome::Refused => refused += 1,
            }
        }

        if applied > 0 || (force && refused > 0) {
            self.record_event(
                OptimizationEvent {
                    timestamp: clock::local_now().hms(),
                    mode: "APP CAP".into(),
                    trigger: "Targeted".into(),
                    reason: format!("{app}: {cap_mb} MB hard working-set cap per process"),
                    freed_mb: round_to(trimmed_bytes as f64 / MB, 1),
                    before_percent,
                    after_percent: self.cache.lk().snapshot.percent,
                    processes_trimmed: applied,
                    duration_ms: started.elapsed().as_millis() as u64,
                    skipped: applied == 0,
                    note: if refused > 0 { format!("{refused} process(es) refused the cap (access denied).") } else { String::new() },
                    ..Default::default()
                },
                applied > 0,
                "Targeted",
            );
        }
        applied
    }

    fn optimize_single_app(self: &Arc<Self>, app: &str, reason: &str) -> OptimizationEvent {
        let _guard = self.optimize_lock.lk();
        let _boost = sys::SweepBoost::begin();
        let started = Instant::now();
        let before = self.sample(false, false);
        let event = |after_percent: f64| OptimizationEvent {
            timestamp: clock::local_now().hms(),
            mode: "APP TRIM".into(),
            trigger: "Targeted".into(),
            reason: format!("{app}: {reason}"),
            before_percent: before.percent,
            after_percent,
            ..Default::default()
        };

        if is_critical(app) {
            return self.record_event(
                OptimizationEvent { skipped: true, note: format!("{app} is a critical system process and is never trimmed."), ..event(before.percent) },
                false,
                "Targeted",
            );
        }

        let (mut trimmed_count, mut trimmed_bytes) = (0u32, 0u64);
        for process in Self::processes_named(app) {
            if let Some(bytes) = sys::trim_process(process.pid, false) {
                trimmed_count += 1;
                trimmed_bytes += bytes;
            }
        }
        let after = self.sample(true, false);

        self.record_event(
            OptimizationEvent {
                freed_mb: round_to(trimmed_bytes as f64 / MB, 1),
                available_gain_mb: round_to(((after.available_gb - before.available_gb) * 1024.0).max(0.0), 1),
                processes_trimmed: trimmed_count,
                duration_ms: started.elapsed().as_millis() as u64,
                skipped: trimmed_count == 0,
                note: if trimmed_count == 0 { "No process could be trimmed (not running or access denied).".into() } else { String::new() },
                ..event(after.percent)
            },
            trimmed_count > 0,
            "Targeted",
        )
    }

    // ---- Process governors: freeze, EcoQoS, memory cap ----

    fn toggle_freeze_core(self: &Arc<Self>, app: &str) -> ActionResult {
        if is_unfreezable(app) {
            return ActionResult { ok: false, active: false, message: format!("{app} is a critical system process and cannot be frozen.") };
        }
        let (target_freeze, existing) = {
            let state = self.state.lk();
            (!contains_ignore_case(&state.frozen_apps, app), state.frozen_records.iter().filter(|r| same_app(&r.name, app)).cloned().collect::<Vec<_>>())
        };

        if target_freeze {
            let (mut affected, mut trimmed) = (0, 0);
            let mut new_records = Vec::new();
            for process in Self::processes_named(app) {
                if !Self::in_user_session(&process) {
                    continue;
                }
                if let Some(was_trimmed) = sys::suspend_process(process.pid) {
                    affected += 1;
                    trimmed += u32::from(was_trimmed);
                    new_records.push(FrozenRecord { name: exe_name(app), pid: process.pid, start_ticks_utc: sys::filetime_to_dotnet_ticks(process.create_time) });
                }
            }
            if affected == 0 {
                return ActionResult { ok: false, active: false, message: format!("Could not suspend {app} (not running or access denied).") };
            }

            let journaled = {
                let mut state = self.state.lk();
                state.frozen_apps.push(app.to_string());
                state.frozen_records.extend(new_records.iter().cloned());
                // Journal every frozen PID so a crash, kill or uninstall can never leave apps suspended.
                let journaled = write_frozen_journal(&state.frozen_records);
                if !journaled {
                    state.frozen_apps.retain(|a| !a.eq_ignore_ascii_case(app));
                    state.frozen_records.retain(|r| !new_records.contains(r));
                }
                journaled
            };
            if !journaled {
                // Never keep apps suspended without a record that lets a crashed PulseRAM resume them.
                for record in &new_records {
                    sys::resume_process(record.pid);
                }
                return ActionResult {
                    ok: false,
                    active: false,
                    message: format!("Couldn't freeze {app}: PulseRAM can't save its safety record in {}.", paths::data_dir().display()),
                };
            }
            self.request_rescan();
            return ActionResult { ok: true, active: true, message: format!("Froze {app}: {affected} process(es) suspended, {trimmed} trimmed") };
        }

        // Thaw: resume exactly the PIDs we suspended. Keep the records we could not resume (access denied) so
        // they are retried later instead of being forgotten.
        let processes = sys::list_processes();
        let mut resumed = 0;
        let mut keep = Vec::new();
        for record in existing {
            match try_resume_record(&record, &processes) {
                ResumeResult::Resumed => resumed += 1,
                ResumeResult::Failed => keep.push(record),
                ResumeResult::Gone => {}
            }
        }
        let kept = keep.len();
        {
            let mut state = self.state.lk();
            state.frozen_records.retain(|r| !same_app(&r.name, app));
            state.frozen_records.extend(keep);
            if kept == 0 {
                state.frozen_apps.retain(|a| !a.eq_ignore_ascii_case(app));
            }
            write_frozen_journal(&state.frozen_records);
        }
        self.request_rescan();

        if kept > 0 {
            ActionResult { ok: false, active: true, message: format!("Resumed {resumed}, but {kept} process(es) of {app} refused (run PulseRAM as administrator).") }
        } else {
            ActionResult { ok: true, active: false, message: format!("Thawed {app}: {resumed} process(es) resumed") }
        }
    }

    fn thaw_all(self: &Arc<Self>) -> usize {
        let _guard = self.freeze_lock.lk();
        let apps = self.state.lk().frozen_apps.clone();
        // Records that refused to resume stay in the journal for a later retry.
        apps.iter().filter(|app| self.toggle_freeze_core(app).ok).count()
    }

    fn toggle_eco(self: &Arc<Self>, app: &str) -> ActionResult {
        if is_critical(app) {
            return ActionResult { ok: false, active: false, message: format!("{app} cannot be throttled.") };
        }
        if self.disposed.load(Ordering::Relaxed) {
            return ActionResult { ok: false, active: false, message: "PulseRAM is shutting down.".into() };
        }
        let enable = !contains_ignore_case(&self.state.lk().eco_apps, app);

        let affected = Self::processes_named(app).iter().filter(|p| Self::in_user_session(p) && sys::set_eco(p.pid, enable)).count();
        if enable && affected == 0 {
            return ActionResult { ok: false, active: false, message: format!("Could not throttle {app} (not running or access denied).") };
        }
        {
            let mut state = self.state.lk();
            state.eco_apps.retain(|a| !a.eq_ignore_ascii_case(app));
            if enable {
                state.eco_apps.push(app.to_string());
            }
        }
        self.save_config();
        self.request_rescan();
        ActionResult {
            ok: true,
            active: enable,
            message: if enable { format!("Enabled EcoQoS Efficiency Mode on {app} ({affected} PIDs)") } else { format!("Restored normal priority on {app}") },
        }
    }

    fn set_app_memory_cap(self: &Arc<Self>, app: &str, cap_mb: Option<i32>) {
        match cap_mb.filter(|cap| *cap >= 64) {
            Some(cap) => {
                {
                    let mut state = self.state.lk();
                    state.config.set_cap(app, Some(cap));
                    state.last_cap_action.insert(app.to_lowercase(), Instant::now());
                }
                self.enforce_hard_cap(app, cap, true);
            }
            None => {
                {
                    let mut state = self.state.lk();
                    state.config.set_cap(app, None);
                    state.last_cap_action.remove(&app.to_lowercase());
                }
                for process in Self::processes_named(app) {
                    sys::release_hard_cap(process.pid);
                }
            }
        }
        self.save_config();
        self.request_rescan();
    }

    /// Restarts an app the way a user would: asks each of its windows to close (so it can save or prompt),
    /// waits up to 15 s, then starts it again. Never force-kills, so no unsaved work is thrown away.
    /// Blocking; call it off the UI thread.
    fn restart_app(&self, app: &str) -> (bool, String) {
        let name = base_name(app).to_string();
        if is_unfreezable(&name) {
            return (
                false,
                format!("{name} is part of Windows, so PulseRAM won't restart it. Signing out and back in (or restarting the PC) releases its memory."),
            );
        }
        let forget = || {
            self.state.lk().leak_history.forget(app);
        };

        let processes = Self::processes_named(app);
        if processes.is_empty() {
            forget();
            return (true, format!("{name} is no longer running."));
        }
        let path = processes.iter().find_map(|p| sys::process_image_path(p.pid));
        let pids: Vec<u32> = processes.iter().map(|p| p.pid).collect();
        if sys::close_main_windows(&pids) == 0 {
            return (false, format!("{name} has no window PulseRAM can close. Close it yourself, then start it again."));
        }

        let still_running = || {
            let now = sys::list_processes();
            processes.iter().any(|old| now.iter().any(|p| p.pid == old.pid && p.create_time == old.create_time))
        };
        let deadline = Instant::now() + Duration::from_secs(15);
        let mut running = still_running();
        while running && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(300));
            running = still_running();
        }
        if running {
            return (false, format!("{name} is still open (it may be asking to save). Finish closing it, then start it again."));
        }

        forget(); // a fresh process: if it leaks again it should show up again
        let Some(path) = path else { return (true, format!("{name} closed. Start it again from the Start menu.")) };
        // Don't hand our administrator rights to the app: without a running Explorer shell to start it for us,
        // leave the relaunch to the user.
        if self.privileges.is_admin && !sys::shell_window_exists() {
            return (true, format!("{name} closed. Start it again from the Start menu."));
        }
        if !sys::launch_as_user(std::path::Path::new(&path)) {
            log::warn(format!("Could not start {path} again"));
            return (true, format!("{name} closed but couldn't be started again. Start it from the Start menu."));
        }
        // Explorer returns before the app starts, so confirm it actually came back.
        let until = Instant::now() + Duration::from_secs(8);
        let mut back = false;
        while !back && Instant::now() < until {
            std::thread::sleep(Duration::from_millis(400));
            back = !Self::processes_named(app).is_empty();
        }
        log::info(if back { format!("Restarted {name} to release leaked memory") } else { format!("Closed {name}; it did not start again within 8 s") });
        if back {
            (true, format!("{name} restarted."))
        } else {
            (true, format!("{name} closed, but Windows didn't start it again. Start it from the Start menu."))
        }
    }
}

/// Sustained private-memory growth in MB/min over a rolling 2-minute window. Requires most samples to be
/// rising so one-off allocations (opening a file, loading a level) don't count.
fn update_leak_rate(history: &mut HashMap<String, VecDeque<(Instant, f64)>>, app: &str, private_mb: f64, now: Instant) -> f64 {
    let list = history.entry(app.to_lowercase()).or_default();
    list.push_back((now, private_mb));
    while list.front().is_some_and(|(t, _)| now.duration_since(*t).as_secs_f64() > 120.0) {
        list.pop_front();
    }
    leak_rate(list.make_contiguous(), now)
}

fn leak_rate(samples: &[(Instant, f64)], now: Instant) -> f64 {
    if samples.len() < 6 {
        return 0.0;
    }
    let (oldest_time, oldest_mb) = samples[0];
    let minutes = now.duration_since(oldest_time).as_secs_f64() / 60.0;
    if minutes < 0.5 {
        return 0.0;
    }
    let delta_mb = samples[samples.len() - 1].1 - oldest_mb;
    if delta_mb <= 20.0 {
        return 0.0;
    }
    let rising = samples.windows(2).filter(|w| w[1].1 >= w[0].1 - 1.0).count();
    if (rising as f64) < (samples.len() - 1) as f64 * 0.8 {
        return 0.0;
    }
    delta_mb / minutes
}

fn compute_smart_score(memory_mb: f64, cpu_percent: f64, leak_rate: f64, is_foreground: bool, is_protected: bool) -> i32 {
    if is_protected {
        return 0;
    }
    let mut score = (memory_mb / 28.0).min(55.0);
    if !is_foreground && cpu_percent < 1.2 {
        score += 28.0;
    } else if !is_foreground && cpu_percent < 5.0 {
        score += 14.0;
    }
    if leak_rate > 30.0 {
        score += (leak_rate / 8.0).min(20.0);
    }
    if is_foreground {
        score *= 0.25;
    }
    (score.round() as i32).clamp(1, 99)
}

fn resolve_badge(is_frozen: bool, is_eco: bool, is_foreground: bool, is_protected: bool, is_leaking: bool, cpu_percent: f64, score: i32) -> Badge {
    if is_frozen {
        Badge::Frozen
    } else if is_protected {
        Badge::Shielded
    } else if is_leaking {
        Badge::Leak
    } else if is_eco {
        Badge::Eco
    } else if is_foreground {
        Badge::Foreground
    } else if score >= 62 && cpu_percent < 1.5 {
        Badge::IdleBloat
    } else {
        Badge::Background
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn critical_and_unfreezable_names() {
        assert!(is_critical("csrss.exe"));
        assert!(is_critical("Memory Compression"));
        assert!(is_critical("MsMpEng.exe"));
        assert!(is_critical("PulseRAM.exe"));
        assert!(!is_critical("chrome.exe"));
        assert!(is_unfreezable("explorer.exe"));
        assert!(is_unfreezable("LSASS.EXE"));
        assert!(!is_unfreezable("notepad.exe"));
        assert!(same_app("Chrome", "chrome.exe"));
    }

    #[test]
    fn smart_score_ranks_idle_bloat_highest() {
        assert_eq!(compute_smart_score(2000.0, 0.0, 0.0, false, true), 0, "shielded apps score 0");
        let idle = compute_smart_score(1400.0, 0.2, 0.0, false, false);
        let busy = compute_smart_score(1400.0, 30.0, 0.0, false, false);
        let foreground = compute_smart_score(1400.0, 0.2, 0.0, true, false);
        assert_eq!(idle, 78);
        assert!(idle > busy && busy > foreground);
        assert_eq!(compute_smart_score(1.0, 50.0, 0.0, false, false), 1, "never below 1");
        assert_eq!(compute_smart_score(9000.0, 0.0, 400.0, false, false), 99, "never above 99");
    }

    #[test]
    fn badges_follow_priority_order() {
        assert_eq!(resolve_badge(true, true, true, true, true, 0.0, 99), Badge::Frozen);
        assert_eq!(resolve_badge(false, true, true, true, true, 0.0, 99), Badge::Shielded);
        assert_eq!(resolve_badge(false, true, true, false, true, 0.0, 99), Badge::Leak);
        assert_eq!(resolve_badge(false, true, true, false, false, 0.0, 99), Badge::Eco);
        assert_eq!(resolve_badge(false, false, true, false, false, 0.0, 99), Badge::Foreground);
        assert_eq!(resolve_badge(false, false, false, false, false, 0.5, 62), Badge::IdleBloat);
        assert_eq!(resolve_badge(false, false, false, false, false, 3.0, 62), Badge::Background);
    }

    fn samples(start: Instant, values: &[f64], step_secs: u64) -> Vec<(Instant, f64)> {
        values.iter().enumerate().map(|(i, v)| (start + Duration::from_secs(i as u64 * step_secs), *v)).collect()
    }

    #[test]
    fn leak_rate_needs_sustained_growth() {
        let t0 = Instant::now();
        // +50 MB every 10 s for a minute and a half → 300 MB/min.
        let steady = samples(t0, &[500.0, 550.0, 600.0, 650.0, 700.0, 750.0, 800.0, 850.0, 900.0, 950.0], 10);
        let now = steady[steady.len() - 1].0;
        assert!((leak_rate(&steady, now) - 300.0).abs() < 0.01);

        // One big allocation, then flat: mostly "rising" (flat counts), so it reads as growth once…
        let burst = samples(t0, &[500.0, 900.0, 900.0, 900.0, 900.0, 900.0, 900.0], 10);
        assert!(leak_rate(&burst, burst[6].0) > 0.0);
        // …but a saw-tooth (allocate, free, allocate) does not.
        let sawtooth = samples(t0, &[500.0, 700.0, 520.0, 720.0, 540.0, 740.0, 560.0], 10);
        assert_eq!(leak_rate(&sawtooth, sawtooth[6].0), 0.0);

        assert_eq!(leak_rate(&steady[..5], steady[4].0), 0.0, "too few samples");
        let tiny = samples(t0, &[500.0, 502.0, 504.0, 506.0, 508.0, 510.0, 512.0], 10);
        assert_eq!(leak_rate(&tiny, tiny[6].0), 0.0, "growth under 20 MB is noise");
        let quick = samples(t0, &[500.0, 550.0, 600.0, 650.0, 700.0, 750.0], 2);
        assert_eq!(leak_rate(&quick, quick[5].0), 0.0, "needs at least half a minute");
    }

    #[test]
    fn frozen_journal_matches_the_1x_file_format() {
        let records: Vec<FrozenRecord> = serde_json::from_str(r#"[{"Name":"notepad.exe","Pid":4800,"StartTicksUtc":638000000000000000}]"#).unwrap();
        assert_eq!(records, vec![FrozenRecord { name: "notepad.exe".into(), pid: 4800, start_ticks_utc: 638_000_000_000_000_000 }]);
        assert_eq!(serde_json::to_string(&records).unwrap(), r#"[{"Name":"notepad.exe","Pid":4800,"StartTicksUtc":638000000000000000}]"#);
    }
}
