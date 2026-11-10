//! Glue between the engine and the Slint interface: fills the UI with readings, carries out what the user
//! clicks, and owns the tray icon, hotkeys, toasts and window switching.

use std::cell::RefCell;
use std::collections::HashSet;
use std::rc::Rc;
use std::sync::Arc;
use std::time::{Duration, Instant};

use slint::{Color, ComponentHandle, Model, ModelRc, PhysicalPosition, PhysicalSize, SharedString, Timer, TimerMode, VecModel};

use crate::engine::{ActionResult, Engine, EngineEvent};
use crate::leaks::LeakIncident;
use crate::model::{format_mb, Badge, CommitAdvice, MemorySnapshot, OptimizationEvent, ProcessItem, TelemetryPoint};
use crate::policy::{self, Hotkey};
use crate::shell::{self, MenuItem, ShellEvent};
use crate::update::{self, UpdateInfo};
use crate::{clock, log, paths, sys};
use crate::{App, EventRow, LeakRow, MainWindow, MiniWindow, ProcRow, ProfileRow, Theme};

const MODES: [&str; 4] = ["smart", "deep", "standby", "max"];
const COOLDOWN_CHOICES: [i32; 5] = [30, 60, 120, 300, 600];
const SCHEDULE_CHOICES: [i32; 4] = [10, 15, 30, 60];
const UI_MODES: [&str; 3] = ["auto", "lite", "full"];
const THEMES: [&str; 3] = ["system", "dark", "light"];
const HOTKEY_SMART: i32 = 9001;
const HOTKEY_DEEP: i32 = 9002;
const MINI_WIDTH: f32 = 304.0;
const MINI_SEE_THROUGH_ALPHA: u8 = 150;

pub struct Launch {
    /// Start hidden in the tray (logon start).
    pub tray: bool,
    /// Open straight into the compact widget.
    pub compact: bool,
    /// The previous session did not shut down cleanly.
    pub previous_session_crashed: bool,
}

#[derive(Default)]
struct UiState {
    /// The compact widget is the active window (shown or hidden), rather than the dashboard.
    mini_mode: bool,
    /// The active window is on screen (not hidden in the tray).
    visible: bool,
    light_theme: bool,
    tray_ok: bool,
    main_hwnd: isize,
    mini_hwnd: isize,
    attach_tries: u32,
    bring_to_front: bool,
    pending: HashSet<String>,
    restarting: HashSet<String>,
    hotkey_smart: String,
    hotkey_deep: String,
    hotkey_status: String,
    commit_advice: Option<CommitAdvice>,
    pending_update: Option<UpdateInfo>,
    update_check_running: bool,
    welcome_showing: bool,
    self_efficiency: bool,
    mini_status_until: Option<Instant>,
    mini_hovered: bool,
    mini_menu_open: bool,
    cap_app: String,
    shutdown_done: bool,
    lite: bool,
}

pub struct Ctx {
    engine: Arc<Engine>,
    main: MainWindow,
    mini: MiniWindow,
    top_apps: Rc<VecModel<ProcRow>>,
    processes: Rc<VecModel<ProcRow>>,
    events: Rc<VecModel<EventRow>>,
    leaks: Rc<VecModel<LeakRow>>,
    profiles: Rc<VecModel<ProfileRow>>,
    st: RefCell<UiState>,
    refresh_timer: Timer,
    toast_timer: Timer,
    update_timer: Timer,
    fade_timer: Timer,
    attach_timer: Timer,
}

thread_local! {
    static CTX: RefCell<Option<Rc<Ctx>>> = const { RefCell::new(None) };
}

fn with_ctx(f: impl FnOnce(&Rc<Ctx>)) {
    let ctx = CTX.with(|c| c.borrow().clone());
    if let Some(ctx) = ctx {
        f(&ctx);
    }
}

/// Runs on the UI thread from any thread.
fn on_ui(f: impl FnOnce(&Rc<Ctx>) + Send + 'static) {
    let _ = slint::invoke_from_event_loop(move || with_ctx(f));
}

fn s(text: impl AsRef<str>) -> SharedString {
    SharedString::from(text.as_ref())
}

/// Updates a list model in place, so rows that didn't change keep their hover and tooltip state.
fn sync_model<T: Clone + PartialEq + 'static>(model: &VecModel<T>, rows: Vec<T>) {
    let old = model.row_count();
    for (index, row) in rows.iter().enumerate().take(old) {
        if model.row_data(index).as_ref() != Some(row) {
            model.set_row_data(index, row.clone());
        }
    }
    if rows.len() > old {
        for row in rows.into_iter().skip(old) {
            model.push(row);
        }
    } else {
        for index in (rows.len()..old).rev() {
            model.remove(index);
        }
    }
}

/// "2", "2.5" (C#'s 0.# format).
fn one_decimal_if_needed(value: f64) -> String {
    let tenths = (value * 10.0).round() as i64;
    if tenths % 10 == 0 {
        format!("{}", tenths / 10)
    } else {
        format!("{:.1}", tenths as f64 / 10.0)
    }
}

/// "0.75", "1.5", "2" (C#'s 0.## format).
fn two_decimals_if_needed(value: f64) -> String {
    let text = format!("{value:.2}");
    text.trim_end_matches('0').trim_end_matches('.').to_string()
}

/// Describes an optimization using only measured values: (headline, details).
pub fn describe_event(event: &OptimizationEvent) -> (String, String) {
    if event.skipped {
        return (format!("{}: nothing changed", event.mode), if event.note.is_empty() { event.reason.clone() } else { event.note.clone() });
    }
    let standby = event.mode == "STANDBY";
    let title = if standby {
        if event.standby_purged {
            format!("Standby cache purged · free memory +{}", format_mb(event.free_list_gain_mb))
        } else {
            "Standby purge failed".to_string()
        }
    } else {
        format!("Trimmed {} from {} process(es)", format_mb(event.freed_mb), event.processes_trimmed)
    };
    let mut parts = Vec::new();
    if !standby {
        parts.push(format!("Available +{}", format_mb(event.available_gain_mb)));
    }
    parts.push(format!("RAM {:.1}% → {:.1}%", event.before_percent, event.after_percent));
    if event.standby_purged && !standby {
        parts.push("standby purged".into());
    }
    if event.combined_mb >= 1.0 {
        parts.push(format!("{} deduplicated", format_mb(event.combined_mb)));
    }
    if !event.note.is_empty() {
        parts.push(event.note.clone());
    }
    (title, parts.join(" · "))
}

fn process_row(item: &ProcessItem, grouped: bool) -> ProcRow {
    let mut meta = if grouped && item.process_count > 1 {
        format!("{} procs · {:.1}% CPU", item.process_count, item.cpu_percent)
    } else {
        format!("PID {} · {:.1}% CPU", item.pid, item.cpu_percent)
    };
    if let Some(cap) = item.cap_mb {
        meta.push_str(&format!(" · Cap {cap}M"));
    }
    if item.leak_rate_mb_per_min >= 15.0 {
        meta.push_str(&format!(" · +{:.0}M/m", item.leak_rate_mb_per_min));
    }
    let initials: String = item.name.chars().take(2).collect::<String>().to_uppercase();
    ProcRow {
        name: s(&item.name),
        initials: s(if initials.chars().count() >= 2 { initials } else { "AP".into() }),
        meta: s(meta),
        badge: s(item.badge.label()),
        badge_kind: match item.badge {
            Badge::Background => 0,
            Badge::Foreground => 1,
            Badge::Shielded => 2,
            Badge::Frozen => 3,
            Badge::Eco => 4,
            Badge::Leak => 5,
            Badge::IdleBloat => 6,
        },
        memory: s(format_mb(item.memory_mb)),
        score: s(format!("Trim Score: {}", item.smart_score)),
        eco: item.is_eco,
        frozen: item.is_frozen,
        shielded: item.is_protected,
        cap: s(item.cap_mb.map(|cap| format!("{cap}M")).unwrap_or_default()),
    }
}

fn event_row(event: &OptimizationEvent) -> EventRow {
    let title = if event.skipped {
        format!("Skipped ({})", event.mode)
    } else if event.mode == "STANDBY" {
        format!("+{} freed ({})", format_mb(event.free_list_gain_mb), event.mode)
    } else {
        format!("{} trimmed ({})", format_mb(event.freed_mb), event.mode)
    };
    let mut detail = format!("RAM {:.1}% → {:.1}% · {} processes trimmed", event.before_percent, event.after_percent, event.processes_trimmed);
    if !event.note.is_empty() {
        detail.push_str(&format!(" · {}", event.note));
    }
    EventRow { title: s(title), time: s(format!("{} · {}ms", event.timestamp, event.duration_ms)), reason: s(&event.reason), detail: s(detail) }
}

fn leak_row(incident: &LeakIncident, busy: bool) -> LeakRow {
    let first = clock::local_from_unix(incident.first_seen);
    let today = clock::local_now();
    let when = if (first.year, first.month, first.day) == (today.year, today.month, today.day) {
        first.hm()
    } else {
        format!("{} {} {}", first.month_abbrev(), first.day, first.hm())
    };
    let mut meta = format!("up to +{:.0} MB/min · {} · since {when}", incident.peak_rate_mb_per_min, format_mb(incident.private_mb));
    if incident.episodes > 1 {
        meta.push_str(&format!(" · {} times", incident.episodes));
    }
    LeakRow { app: s(&incident.app), meta: s(meta), busy }
}

/// SVG path data for the 60-second chart: (RAM line, RAM area, CPU line).
fn chart_paths(points: &[TelemetryPoint], width: f32, height: f32) -> (String, String, String) {
    if points.len() < 2 || width < 40.0 || height < 30.0 {
        return (String::new(), String::new(), String::new());
    }
    let step = width / 59.0;
    let y = |percent: f64| height - (percent.clamp(0.0, 100.0) as f32 / 100.0) * height;
    let mut ram = String::with_capacity(points.len() * 14);
    let mut cpu = String::with_capacity(points.len() * 14);
    for (index, point) in points.iter().enumerate() {
        let x = index as f32 * step;
        let command = if index == 0 { 'M' } else { 'L' };
        ram.push_str(&format!("{command} {x:.1} {:.1} ", y(point.used_percent)));
        cpu.push_str(&format!("{command} {x:.1} {:.1} ", y(point.cpu_percent)));
    }
    let last_x = (points.len() - 1) as f32 * step;
    let area = format!("M 0 {height:.1} L{} L {last_x:.1} {height:.1} Z", &ram[1..]);
    (ram, area, cpu)
}

/// Standby cache explained: per-priority sizes, eviction rate, and whether a purge would help.
fn standby_tooltip(snap: &MemorySnapshot, smart_purge: bool) -> String {
    let mut text = String::from("Cached files Windows can drop instantly when an app needs RAM.");
    match snap.standby_by_priority_gb {
        Some(p) => {
            text.push_str("\n\nBy priority (0 = least important):");
            text.push_str(&format!("\n0–1: {:.2} GB · 2–4: {:.2} GB · 5–7: {:.2} GB", p[0] + p[1], p[2] + p[3] + p[4], p[5] + p[6] + p[7]));
            if snap.repurpose_mb_per_min >= 0.0 {
                text.push_str(&format!("\nBeing evicted: {:.0} MB/min", snap.repurpose_mb_per_min));
            }
        }
        None => text.push_str("\n\nRun as Admin to see it by priority."),
    }
    text.push_str(if !snap.standby_purge_supported {
        "\n\nPurging the cache needs Admin."
    } else if snap.standby_purge_worthwhile {
        "\n\nFree memory is low — a purge would help."
    } else if !smart_purge {
        "\n\nSmart purge is off — automatic sweeps purge anyway."
    } else if snap.standby_gb < (snap.total_gb * 0.10).max(0.5) {
        "\n\nThe cache is small — Auto-Pilot leaves it alone."
    } else {
        "\n\nFree memory is fine — Auto-Pilot leaves the cache alone."
    });
    text
}

fn pressure_index(level: &str) -> i32 {
    match level {
        "CRITICAL" => 2,
        "ELEVATED" => 1,
        _ => 0,
    }
}

/// Creates the windows, wires everything up and runs until PulseRAM exits.
pub fn run(launch: Launch) {
    let engine = Arc::new(Engine::new());
    let main = MainWindow::new().expect("the dashboard window could not be created");
    let mini = MiniWindow::new().expect("the compact window could not be created");

    let ctx = Rc::new(Ctx {
        engine,
        main,
        mini,
        top_apps: Rc::new(VecModel::default()),
        processes: Rc::new(VecModel::default()),
        events: Rc::new(VecModel::default()),
        leaks: Rc::new(VecModel::default()),
        profiles: Rc::new(VecModel::default()),
        st: RefCell::new(UiState::default()),
        refresh_timer: Timer::default(),
        toast_timer: Timer::default(),
        update_timer: Timer::default(),
        fade_timer: Timer::default(),
        attach_timer: Timer::default(),
    });
    CTX.with(|c| *c.borrow_mut() = Some(ctx.clone()));

    ctx.wire();
    ctx.engine.set_listener(|event| on_ui(move |ctx| ctx.on_engine_event(event)));

    shell::init(on_shell_event);
    ctx.st.borrow_mut().tray_ok = shell::tray_add();
    ctx.register_hotkeys();
    ctx.apply_theme(true);
    ctx.sync_settings();
    ctx.apply_performance_mode();
    ctx.start_update_checks();

    if launch.tray {
        ctx.refresh(false);
    } else {
        // --compact: open straight into the compact widget (e.g. from a shortcut). The welcome card waits for a
        // normal launch, since it doesn't fit in the widget.
        ctx.st.borrow_mut().mini_mode = launch.compact;
        ctx.show_window();
        if !launch.compact {
            ctx.show_welcome_if_first_run();
        }
    }

    let hotkey_status = ctx.st.borrow().hotkey_status.clone();
    if !hotkey_status.is_empty() {
        ctx.toast("Hotkey unavailable", &hotkey_status);
    } else if ctx.engine.unsafe_task_found() {
        ctx.toast(
            "Unsafe startup task found",
            "An old 'start as administrator' task points to a folder other programs can change. Click Admin once and PulseRAM removes it.",
        );
    } else if paths::portable_unavailable() {
        ctx.toast(
            "Portable mode is off",
            "This folder can't be written to, so settings are kept in %LocalAppData%\\PulseRAM. Move the folder somewhere writable to keep them next to the EXE.",
        );
    } else if launch.previous_session_crashed {
        ctx.toast("PulseRAM closed unexpectedly last time", "Details are in the log: Profiles → Device → Logs.");
    }

    dev_snapshot(&ctx);

    if let Err(e) = slint::run_event_loop_until_quit() {
        log::error(format!("The event loop failed: {e}"));
    }
    ctx.shutdown("Clean shutdown");
    CTX.with(|c| *c.borrow_mut() = None);
}

fn on_shell_event(event: ShellEvent) {
    with_ctx(|ctx| match event {
        ShellEvent::ShowWindow | ShellEvent::TrayOpen => ctx.show_window(),
        ShellEvent::ExitRequested => ctx.exit(),
        ShellEvent::Hotkey(HOTKEY_SMART) => {
            let label = ctx.st.borrow().hotkey_smart.clone();
            ctx.run_optimization("smart", &format!("Global Hotkey ({label})"));
        }
        ShellEvent::Hotkey(HOTKEY_DEEP) => {
            let label = ctx.st.borrow().hotkey_deep.clone();
            ctx.run_optimization("deep", &format!("Global Hotkey ({label})"));
        }
        ShellEvent::Hotkey(_) => {}
        ShellEvent::TrayMenu => ctx.show_tray_menu(),
        ShellEvent::MinimizeToTray => ctx.hide_to_tray(),
        ShellEvent::SessionEnding => ctx.shutdown("Windows session ending"),
        ShellEvent::ColorsChanged => {
            if ctx.engine.with_config(|c| c.theme == "system") {
                ctx.apply_theme(false);
            }
        }
        ShellEvent::TaskbarCreated => {
            // Explorer restarted: the old icon is gone, add it again.
            let ok = shell::tray_add();
            ctx.st.borrow_mut().tray_ok = ok;
            ctx.update_minimize_hook();
            ctx.update_tray(&ctx.engine.snapshot());
        }
    });
}

impl Ctx {
    fn app(&self) -> App<'_> {
        self.main.global::<App>()
    }

    fn selected_mode(&self) -> &'static str {
        MODES[self.app().get_mode().clamp(0, 3) as usize]
    }

    // ---- Wiring ----

    fn wire(self: &Rc<Self>) {
        let app = self.app();
        app.set_top_apps(ModelRc::from(self.top_apps.clone()));
        app.set_processes(ModelRc::from(self.processes.clone()));
        app.set_events(ModelRc::from(self.events.clone()));
        app.set_leaks(ModelRc::from(self.leaks.clone()));
        app.set_profiles(ModelRc::from(self.profiles.clone()));

        app.on_command(|name, arg| with_ctx(|ctx| ctx.command(name.as_str(), arg.as_str())));
        app.on_set_flag(|name, value| with_ctx(|ctx| ctx.set_flag(name.as_str(), value)));
        app.on_set_choice(|name, index| with_ctx(|ctx| ctx.set_choice(name.as_str(), index)));
        app.on_set_number(|name, value| with_ctx(|ctx| ctx.set_number(name.as_str(), f64::from(value))));
        app.on_refresh(|| with_ctx(|ctx| ctx.refresh(true)));

        // Closing the window ends PulseRAM (hiding is what minimize-to-tray is for).
        self.main.window().on_close_requested(|| {
            with_ctx(|ctx| ctx.exit());
            slint::CloseRequestResponse::KeepWindowShown
        });
        self.mini.window().on_close_requested(|| {
            with_ctx(|ctx| ctx.exit());
            slint::CloseRequestResponse::KeepWindowShown
        });

        self.mini.on_clean(|| with_ctx(|ctx| ctx.run_optimization(ctx.selected_mode(), "Compact view")));
        self.mini.on_expand(|| with_ctx(|ctx| ctx.toggle_mini_mode()));
        self.mini.on_hide_to_tray(|| with_ctx(|ctx| ctx.hide_to_tray()));
        self.mini.on_context_menu(|| with_ctx(|ctx| ctx.show_mini_menu()));
        self.mini.on_dragged(|dx, dy| {
            with_ctx(|ctx| {
                let window = ctx.mini.window();
                let scale = window.scale_factor();
                let position = window.position();
                window.set_position(PhysicalPosition::new(position.x + (dx * scale).round() as i32, position.y + (dy * scale).round() as i32));
            })
        });
        self.mini.on_hover(|hovered| {
            with_ctx(|ctx| {
                ctx.st.borrow_mut().mini_hovered = hovered;
                if hovered {
                    ctx.set_mini_see_through(false);
                } else {
                    ctx.schedule_mini_see_through();
                }
            })
        });
    }

    // ---- Commands from the interface ----

    fn command(self: &Rc<Self>, name: &str, arg: &str) {
        match name {
            "clean" => {
                let mode = self.selected_mode();
                self.run_optimization(mode, &format!("Manual {} optimization", mode.to_uppercase()));
            }
            "focus" => self.toggle_focus_boost(),
            "compact" => self.toggle_mini_mode(),
            "admin" => self.request_elevation(),
            "thaw-all" => self.thaw_all(),
            "use-auto-detect" => {
                self.engine.update_config(|c| c.forced_profile_id.clear());
                self.refresh(true);
                self.toast("Profile Auto-Detection Active", "PulseRAM will automatically match running games and workloads.");
            }
            "check-updates" => self.check_for_updates(true),
            "retune" => {
                self.engine.retune_for_this_device();
                self.sync_settings();
                self.refresh(true);
                let (trigger, floor, cooldown) = self.engine.with_config(|c| (c.ram_threshold_percent, c.min_free_ram_gb, c.cooldown_seconds));
                self.toast(
                    "Tuned for this PC",
                    &format!("Trigger {trigger}% · floor {floor:.2} GB · cooldown {} min", one_decimal_if_needed(f64::from(cooldown) / 60.0)),
                );
            }
            "logs" => log::open_folder(),
            "commit-fix" => {
                if !sys::open_page_file_settings() {
                    log::warn("Could not open page-file settings");
                }
            }
            "commit-dismiss" => {
                self.st.borrow_mut().commit_advice = None;
                self.app().set_commit_banner(false);
            }
            "update-download" => {
                let url = self.st.borrow().pending_update.as_ref().map(|u| u.page_url.clone()).unwrap_or_else(update::latest_release_page);
                if !sys::open_as_user(&url) {
                    log::warn("Could not open the release page");
                }
            }
            "update-skip" => {
                let version = self.st.borrow().pending_update.as_ref().map(|u| u.version.clone());
                if let Some(version) = version {
                    self.engine.update_config(|c| c.skipped_update_version = version);
                }
                self.hide_update_banner();
            }
            "update-later" => self.hide_update_banner(),
            "preset" => self.apply_preset(arg),
            "profile" => self.toggle_forced_profile(arg),
            "trim" => {
                let app = arg.to_string();
                let name = app.clone();
                self.run_action(Some(format!("trim:{app}")), move |engine| engine.optimize_app(&app, "Manual App Trim"), move |ctx, event| {
                    if event.skipped {
                        ctx.toast(&format!("Could not trim {name}"), &event.note);
                    } else {
                        ctx.toast(&format!("Trimmed {name}"), &format!("{} paged out of {} process(es).", format_mb(event.freed_mb), event.processes_trimmed));
                    }
                });
            }
            "eco" => {
                let app = arg.to_string();
                let name = app.clone();
                self.run_action(Some(format!("eco:{app}")), move |engine| engine.toggle_eco(&app), move |ctx, result: ActionResult| {
                    let title = match (result.ok, result.active) {
                        (true, true) => format!("EcoQoS Active: {name}"),
                        (true, false) => format!("EcoQoS Off: {name}"),
                        _ => "Not Changed".to_string(),
                    };
                    ctx.toast(&title, &result.message);
                });
            }
            "freeze" => {
                let app = arg.to_string();
                let name = app.clone();
                self.run_action(Some(format!("freeze:{app}")), move |engine| engine.toggle_freeze(&app), move |ctx, result: ActionResult| {
                    let title = match (result.ok, result.active) {
                        (true, true) => format!("Frozen {name}"),
                        (true, false) => format!("Thawed {name}"),
                        (false, true) => "Still frozen".to_string(),
                        (false, false) => "Not frozen".to_string(),
                    };
                    ctx.toast(&title, &result.message);
                });
            }
            "shield" => {
                let app = arg.to_string();
                let name = app.clone();
                self.run_action(Some(format!("shield:{app}")), move |engine| engine.toggle_protect(&app), move |ctx, shielded| {
                    if shielded {
                        ctx.toast(&format!("Shielded {name}"), "Never trimmed by any sweep.");
                    } else {
                        ctx.toast(&format!("Unshielded {name}"), "Removed from the shield list.");
                    }
                });
            }
            "cap" => self.open_cap_dialog(arg),
            "cap-apply" => self.apply_cap(arg),
            "cap-remove" => {
                let app = self.st.borrow().cap_app.clone();
                self.app().set_cap_open(false);
                let name = app.clone();
                self.run_action(None, move |engine| engine.set_app_memory_cap(&app, None), move |ctx, _| {
                    ctx.toast(&format!("Removed Cap on {name}"), "Hard working-set limit released.");
                });
            }
            "leak-restart" => {
                let app = arg.to_string();
                let name = app.clone();
                self.st.borrow_mut().restarting.insert(app.to_lowercase());
                self.render_leaks();
                self.run_action(Some(format!("restart:{app}")), move |engine| engine.restart_app(&app), move |ctx, (ok, message)| {
                    ctx.st.borrow_mut().restarting.remove(&name.to_lowercase());
                    ctx.toast(if ok { "Restart" } else { "Couldn't restart" }, &message);
                    ctx.render_leaks();
                });
            }
            "leak-dismiss" => {
                self.engine.dismiss_leak(arg);
                self.render_leaks();
            }
            "leak-ignore" => {
                self.engine.ignore_leak(arg);
                self.render_leaks();
            }
            "welcome-start" => self.finish_welcome(false),
            "welcome-compact" => self.finish_welcome(true),
            _ => log::warn(format!("Unknown command: {name}")),
        }
    }

    fn set_flag(self: &Rc<Self>, name: &str, value: bool) {
        match name {
            "auto" => self.engine.update_config(|c| c.auto_enabled = value),
            "protect-foreground" => self.engine.update_config(|c| c.protect_foreground = value),
            "chime" => self.engine.update_config(|c| c.auto_sound_chime = value),
            "leak-guard" => self.engine.update_config(|c| c.auto_leak_remediation = value),
            "scheduled" => self.engine.update_config(|c| c.scheduled_trim_enabled = value),
            "profiles-auto" => self.engine.update_config(|c| c.profiles_auto_switch = value),
            "smart-purge" => self.engine.update_config(|c| c.smart_standby_purge = value),
            "eco-in-tray" => self.engine.update_config(|c| c.efficiency_mode_when_hidden = value),
            "memory-warnings" => self.engine.update_config(|c| c.commit_warning_enabled = value),
            "updates" => self.engine.update_config(|c| c.check_for_updates = value),
            "tray-percent" => {
                self.engine.update_config(|c| c.tray_shows_percent = value);
                self.update_tray(&self.engine.snapshot());
            }
            "minimize-to-tray" => {
                self.engine.update_config(|c| c.minimize_to_tray = value);
                self.update_minimize_hook();
            }
            "hotkeys" => {
                self.engine.update_config(|c| c.hotkeys_enabled = value);
                self.register_hotkeys();
                let status = self.st.borrow().hotkey_status.clone();
                if !status.is_empty() {
                    self.toast("Hotkey unavailable", &status);
                }
            }
            "startup" => self.set_startup(value),
            _ => log::warn(format!("Unknown setting: {name}")),
        }
        self.refresh(false);
    }

    fn set_choice(self: &Rc<Self>, name: &str, index: i32) {
        let index = index.max(0) as usize;
        match name {
            "mode" => {}
            "auto-mode" => self.engine.update_config(|c| c.auto_mode = MODES[index.min(3)].into()),
            "cooldown" => self.engine.update_config(|c| c.cooldown_seconds = COOLDOWN_CHOICES[index.min(4)]),
            "schedule-interval" => self.engine.update_config(|c| c.scheduled_interval_minutes = SCHEDULE_CHOICES[index.min(3)]),
            "ui-mode" => {
                self.engine.update_config(|c| c.ui_mode = UI_MODES[index.min(2)].into());
                self.apply_performance_mode();
            }
            "theme" => {
                self.engine.update_config(|c| c.theme = THEMES[index.min(2)].into());
                self.apply_theme(false);
            }
            _ => log::warn(format!("Unknown choice: {name}")),
        }
        self.refresh(true);
    }

    fn set_number(self: &Rc<Self>, name: &str, value: f64) {
        match name {
            "trigger" => self.engine.update_config(|c| c.ram_threshold_percent = value.round() as i32),
            "min-free" => self.engine.update_config(|c| c.min_free_ram_gb = (value * 2.0).round() / 2.0),
            _ => log::warn(format!("Unknown number: {name}")),
        }
        self.refresh(true);
    }

    /// Copies the saved settings into the interface's controls.
    fn sync_settings(&self) {
        let cfg = self.engine.config();
        let app = self.app();
        app.set_auto_on(cfg.auto_enabled);
        app.set_trigger(cfg.ram_threshold_percent as f32);
        app.set_min_free(cfg.min_free_ram_gb as f32);
        app.set_auto_mode(MODES.iter().position(|m| *m == cfg.auto_mode).unwrap_or(0) as i32);
        app.set_cooldown(COOLDOWN_CHOICES.iter().position(|c| *c == cfg.cooldown_seconds).map_or(-1, |i| i as i32));
        app.set_protect_foreground(cfg.protect_foreground);
        app.set_chime(cfg.auto_sound_chime);
        app.set_leak_guard(cfg.auto_leak_remediation);
        app.set_scheduled(cfg.scheduled_trim_enabled);
        app.set_schedule_interval(SCHEDULE_CHOICES.iter().position(|c| *c == cfg.scheduled_interval_minutes).map_or(-1, |i| i as i32));
        app.set_startup(cfg.run_on_startup);
        app.set_startup_tip(s(if self.engine.is_admin() {
            "Starts elevated in the tray at logon."
        } else {
            "Starts in the tray at logon (standard user — run as Admin and re-enable to start elevated)."
        }));
        app.set_profiles_auto(cfg.profiles_auto_switch);
        app.set_hotkeys_on(cfg.hotkeys_enabled);
        app.set_hotkeys_tip(s(format!("Edit keys in {}", paths::config_file().display())));
        app.set_tray_percent(cfg.tray_shows_percent);
        app.set_eco_in_tray(cfg.efficiency_mode_when_hidden);
        app.set_smart_purge(cfg.smart_standby_purge);
        app.set_minimize_to_tray(cfg.minimize_to_tray);
        app.set_memory_warnings(cfg.commit_warning_enabled);
        app.set_updates(cfg.check_for_updates);
        app.set_ui_mode(UI_MODES.iter().position(|m| *m == cfg.ui_mode).unwrap_or(0) as i32);
        app.set_theme_choice(THEMES.iter().position(|t| *t == cfg.theme).unwrap_or(0) as i32);
        app.set_is_admin(self.engine.is_admin());
        app.set_device_label(s(format!(
            "{} · PulseRAM {}{}",
            self.engine.device().label(),
            update::current_version(),
            if paths::is_portable() { " (portable)" } else { "" }
        )));
        let st = self.st.borrow();
        app.set_hotkey_smart(s(&st.hotkey_smart));
        app.set_hotkey_deep(s(&st.hotkey_deep));
    }

    // ---- Engine work off the UI thread ----

    /// Runs an engine call (process enumeration, OpenProcess…) off the UI thread, then refreshes. Repeat
    /// clicks for the same `key` are ignored until the first one finishes.
    fn run_action<T: Send + 'static>(
        self: &Rc<Self>,
        key: Option<String>,
        work: impl FnOnce(&Engine) -> T + Send + 'static,
        done: impl FnOnce(&Rc<Ctx>, T) + Send + 'static,
    ) {
        if let Some(key) = &key {
            if !self.st.borrow_mut().pending.insert(key.clone()) {
                return;
            }
        }
        let engine = self.engine.clone();
        std::thread::spawn(move || {
            let result = work(&engine);
            on_ui(move |ctx| {
                if let Some(key) = key {
                    ctx.st.borrow_mut().pending.remove(&key);
                }
                ctx.refresh(true);
                done(ctx, result);
            });
        });
    }

    fn set_cleaning(&self, cleaning: bool) {
        self.app().set_cleaning(cleaning);
        self.mini.set_cleaning(cleaning);
    }

    fn run_optimization(self: &Rc<Self>, mode: &str, reason: &str) {
        if self.st.borrow().pending.contains("sweep") {
            return;
        }
        self.set_cleaning(true);
        let (mode, reason) = (mode.to_string(), reason.to_string());
        self.run_action(Some("sweep".into()), move |engine| engine.optimize(&mode, "Manual", &reason), |ctx, event| {
            ctx.set_cleaning(false);
            let (title, sub) = describe_event(&event);
            ctx.toast(&if event.skipped { title } else { format!("{title} in {} ms", event.duration_ms) }, &sub);
        });
    }

    fn on_engine_event(self: &Rc<Self>, event: EngineEvent) {
        match event {
            // Manual sweeps toast from run_optimization; per-app buttons toast themselves.
            EngineEvent::Optimization(event) if event.trigger == "Manual" => {}
            EngineEvent::Optimization(event) if event.trigger == "Targeted" => self.refresh(false),
            EngineEvent::Optimization(event) => {
                self.refresh(false);
                let (title, sub) = describe_event(&event);
                self.toast(&format!("{}: {title}", event.trigger), &format!("{} · {sub}", event.reason));
            }
            EngineEvent::CommitWarning(advice) => self.show_commit_warning(advice, true),
            EngineEvent::LeakDetected(incident) => {
                self.render_leaks();
                let title = format!("{} may be leaking memory", incident.app);
                let body = format!(
                    "+{:.0} MB/min, {} now. Restarting it frees the memory (Processes tab).",
                    incident.last_rate_mb_per_min,
                    format_mb(incident.private_mb)
                );
                if self.dashboard_visible() {
                    self.toast(&title, &body);
                } else {
                    shell::tray_balloon(&format!("PulseRAM: {title}"), &body, true);
                }
            }
        }
    }

    fn toggle_focus_boost(self: &Rc<Self>) {
        let active = !self.engine.with_config(|c| c.focus_boost_active);
        self.engine.update_config(|c| c.focus_boost_active = active);
        if active {
            self.run_optimization("smart", "Focus Boost Overdrive Activated");
        } else {
            self.toast("Focus Boost Deactivated", "Returned Auto-Pilot to normal profile thresholds.");
        }
        self.refresh(true);
    }

    fn thaw_all(self: &Rc<Self>) {
        self.run_action(Some("thaw-all".into()), |engine| engine.thaw_all(), |ctx, count| {
            ctx.toast("Thawed All Processes", &format!("Resumed {count} suspended application(s)."));
        });
    }

    fn apply_preset(self: &Rc<Self>, name: &str) {
        let (trigger, floor, mode) = match name {
            "Gaming" => (70, 3.0, "smart"),
            "Heavy" => (88, 1.5, "deep"),
            _ => (80, 2.0, "smart"),
        };
        self.engine.update_config(|c| {
            c.ram_threshold_percent = trigger;
            c.min_free_ram_gb = floor;
            c.auto_mode = mode.into();
        });
        self.sync_settings();
        self.refresh(true);
        self.toast(&format!("{name} preset"), &format!("Trigger {trigger}% · floor {floor:.1} GB · {mode}"));
    }

    fn toggle_forced_profile(self: &Rc<Self>, id: &str) {
        let (was_forced, name) = self.engine.with_config(|c| {
            (c.forced_profile_id == id, c.profiles.iter().find(|p| p.id == id).map(|p| p.name.clone()).unwrap_or_default())
        });
        self.engine.update_config(|c| c.forced_profile_id = if was_forced { String::new() } else { id.to_string() });
        self.refresh(true);
        if was_forced {
            self.toast("Returned to Auto-Detect", "Automatic workload detection restored.");
        } else {
            self.toast(&format!("Activated {name}"), &format!("Locked Auto-Pilot to {name} profile."));
        }
    }

    fn set_startup(self: &Rc<Self>, enable: bool) {
        self.run_action(Some("startup".into()), move |engine| engine.set_startup(enable), move |ctx, result| {
            ctx.app().set_startup(result.enabled);
            let title = if !result.ok {
                "Startup Not Changed"
            } else if enable {
                "Windows Startup Enabled"
            } else {
                "Windows Startup Disabled"
            };
            ctx.toast(title, &result.message);
        });
    }

    fn request_elevation(self: &Rc<Self>) {
        if self.engine.is_admin() {
            self.toast("Kernel Admin Active", "SeProfileSingleProcessPrivilege & Standby List Purge are unlocked.");
            return;
        }
        // --elevated-relaunch tells the new instance to wait for this one to exit instead of bowing out because
        // the single-instance mutex is still held.
        if sys::relaunch_elevated(&paths::exe_path(), "--elevated-relaunch") {
            self.exit();
        } else {
            self.toast("Elevation Cancelled", "Continuing in standard user working-set trim mode.");
        }
    }

    // ---- Memory cap dialog ----

    fn open_cap_dialog(&self, app_name: &str) {
        let grouped = self.app().get_grouping() == 0;
        let list = self.engine.processes(grouped);
        let Some(item) = list.iter().find(|p| p.name.eq_ignore_ascii_case(app_name)) else { return };
        self.st.borrow_mut().cap_app = item.name.clone();
        let app = self.app();
        app.set_cap_app(s(&item.name));
        app.set_cap_intro(s(format!("Now {}. Hard limit per process — set it too low and the app slows down.", format_mb(item.memory_mb))));
        app.set_cap_value(s(item.cap_mb.unwrap_or_else(|| ((item.memory_mb * 0.75) as i32).max(256)).to_string()));
        app.set_cap_open(true);
    }

    fn apply_cap(self: &Rc<Self>, value: &str) {
        let cap = value.trim().parse::<f64>().ok().map(|v| v.round() as i32).filter(|v| *v >= 64);
        let Some(cap) = cap else {
            self.toast("Cap not applied", "Enter a limit of at least 64 MB.");
            return;
        };
        let app = self.st.borrow().cap_app.clone();
        self.app().set_cap_open(false);
        let name = app.clone();
        self.run_action(None, move |engine| engine.set_app_memory_cap(&app, Some(cap)), move |ctx, _| {
            ctx.toast(
                &format!("Capped {name} at {cap} MB"),
                "Hard working-set limit applied. Windows pages the app out above this, so a cap that is too low makes it slow.",
            );
        });
    }

    // ---- Toasts, banners ----

    fn mini_color(&self, dark: u32, light: u32) -> Color {
        let rgb = if self.st.borrow().light_theme { light } else { dark };
        Color::from_rgb_u8((rgb >> 16) as u8, (rgb >> 8) as u8, rgb as u8)
    }

    fn toast(self: &Rc<Self>, title: &str, sub: &str) {
        if self.st.borrow().mini_mode {
            // The widget has no room for a banner: show the headline on its status line for a few seconds.
            self.mini.set_status(s(title));
            self.mini.set_status_color(self.mini_color(0x38BDF8, 0x0284C7));
            self.st.borrow_mut().mini_status_until = Some(Instant::now() + Duration::from_secs(6));
        }
        let app = self.app();
        app.set_toast_title(s(title));
        app.set_toast_sub(s(sub));
        app.set_toast(true);
        self.toast_timer.start(TimerMode::SingleShot, Duration::from_secs(6), || with_ctx(|ctx| ctx.app().set_toast(false)));
    }

    /// Shows the commit-charge warning as a banner, and as a tray notification while hidden.
    fn show_commit_warning(self: &Rc<Self>, advice: CommitAdvice, announce: bool) {
        let app = self.app();
        app.set_commit_critical(advice.level == "critical");
        app.set_commit_title(s(&advice.title));
        app.set_commit_body(s(&advice.body));
        app.set_commit_fix(advice.suggest_page_file);
        app.set_commit_banner(true);
        if announce && !advice.is_repeat && !self.dashboard_visible() {
            shell::tray_balloon(&format!("PulseRAM: {}", advice.title), &advice.body, true);
        }
        self.st.borrow_mut().commit_advice = Some(advice);
    }

    fn hide_update_banner(&self) {
        self.st.borrow_mut().pending_update = None;
        self.app().set_update_banner(false);
    }

    fn show_update_banner(self: &Rc<Self>, info: UpdateInfo) {
        let app = self.app();
        app.set_update_title(s(format!("PulseRAM {} is available (you have {})", info.version, update::current_version())));
        app.set_update_banner(true);
        let (mini, visible) = {
            let st = self.st.borrow();
            (st.mini_mode, st.visible)
        };
        if mini && visible {
            self.toast(&format!("Update {} available", info.version), "Open the full view to download it.");
        } else if !self.dashboard_visible() {
            shell::tray_balloon(&format!("PulseRAM {} is available", info.version), "Open PulseRAM to download it.", false);
        }
        self.st.borrow_mut().pending_update = Some(info);
    }

    // ---- Updates ----

    /// First check shortly after start, then every few hours (each check itself is limited to once a day).
    fn start_update_checks(self: &Rc<Self>) {
        self.update_timer.start(TimerMode::Repeated, Duration::from_secs(30), || {
            with_ctx(|ctx| {
                // Wait for the user's answer on the welcome card (it has the opt-out).
                if ctx.st.borrow().welcome_showing {
                    return;
                }
                // Every 4 h after a check; after a failed attempt (offline at logon, rate limit) retry in 30 min.
                ctx.update_timer.set_interval(Duration::from_secs(4 * 3600));
                if ctx.engine.with_config(|c| update::is_due(c, clock::unix_now())) {
                    ctx.check_for_updates(false);
                }
            })
        });
    }

    fn check_for_updates(self: &Rc<Self>, manual: bool) {
        if std::mem::replace(&mut self.st.borrow_mut().update_check_running, true) {
            return;
        }
        let skipped = self.engine.with_config(|c| c.skipped_update_version.clone());
        std::thread::spawn(move || {
            let (answered, info) = update::check(&skipped, manual);
            on_ui(move |ctx| {
                ctx.st.borrow_mut().update_check_running = false;
                if answered {
                    ctx.engine.update_config(|c| c.last_update_check_utc = Some(clock::format_iso_utc(clock::unix_now())));
                } else if !manual {
                    ctx.update_timer.set_interval(Duration::from_secs(30 * 60));
                }
                match info {
                    Some(info) => ctx.show_update_banner(info),
                    None if manual && answered => {
                        ctx.toast("You're up to date", &format!("PulseRAM {} is the latest release.", update::current_version()));
                    }
                    None if manual => ctx.toast("Couldn't check for updates", "GitHub couldn't be reached. Try again later."),
                    None => {}
                }
            });
        });
    }

    // ---- Welcome card ----

    /// Shown once, on the first launch with no saved settings (never for upgrades or --tray starts).
    fn show_welcome_if_first_run(self: &Rc<Self>) {
        let cfg = self.engine.config();
        if !self.engine.is_first_run() || cfg.welcome_shown || self.st.borrow().welcome_showing {
            return;
        }
        self.st.borrow_mut().welcome_showing = true;
        let app = self.app();
        app.set_welcome_device(s(format!("Your PC: {}. PulseRAM has tuned itself for it.", self.engine.device().label())));
        app.set_welcome_auto(s(format!(
            "Auto-Pilot is on. When RAM use passes {}% or free memory drops below {} GB, it trims apps you aren't using. The app you're working in is never touched, and nothing is closed or deleted.",
            cfg.ram_threshold_percent,
            two_decimals_if_needed(cfg.min_free_ram_gb)
        )));
        app.set_welcome_startup(self.engine.is_startup_enabled());
        app.set_welcome_updates(cfg.check_for_updates);
        app.set_welcome_open(true);
    }

    fn finish_welcome(self: &Rc<Self>, compact: bool) {
        let app = self.app();
        let (startup, updates) = (app.get_welcome_startup(), app.get_welcome_updates());
        app.set_welcome_open(false);
        self.st.borrow_mut().welcome_showing = false;
        self.engine.update_config(|c| {
            c.welcome_shown = true;
            c.check_for_updates = updates;
        });
        self.sync_settings();
        if startup != self.engine.is_startup_enabled() {
            self.app().set_startup(startup);
            self.set_startup(startup);
        }
        if compact && !self.st.borrow().mini_mode {
            self.toggle_mini_mode();
        }
    }

    // ---- Theme, performance ----

    fn apply_theme(self: &Rc<Self>, initial: bool) {
        let light = match self.engine.with_config(|c| c.theme.clone()).as_str() {
            "light" => true,
            "dark" => false,
            _ => sys::system_uses_light_theme(),
        };
        let changed = std::mem::replace(&mut self.st.borrow_mut().light_theme, light) != light;
        if !initial && !changed {
            return;
        }
        self.main.global::<Theme>().set_dark(!light);
        self.mini.global::<Theme>().set_dark(!light);
        let (main_hwnd, mini_hwnd) = {
            let st = self.st.borrow();
            (st.main_hwnd, st.mini_hwnd)
        };
        for hwnd in [main_hwnd, mini_hwnd] {
            if hwnd != 0 {
                shell::set_title_bar_dark(hwnd, !light);
            }
        }
        if !initial {
            log::info(format!("Theme switched to {}", if light { "light" } else { "dark" }));
            self.refresh(true);
        }
    }

    /// Lite mode (auto on low-end PCs): slower refresh and fewer process rows.
    fn apply_performance_mode(self: &Rc<Self>) {
        let lite = self.engine.lite_mode();
        self.st.borrow_mut().lite = lite;
        self.refresh_timer.start(TimerMode::Repeated, Duration::from_millis(if lite { 3000 } else { 1500 }), || with_ctx(|ctx| ctx.refresh(false)));
    }

    // ---- Hotkeys ----

    fn register_hotkeys(&self) {
        let cfg = self.engine.config();
        let primary = policy::parse_hotkey(&cfg.hotkey_primary);
        let secondary = policy::parse_hotkey(&cfg.hotkey_secondary);
        let label = |hotkey: &Option<Hotkey>, text: &str| hotkey.as_ref().map(|h| h.label.clone()).unwrap_or_else(|| format!("Invalid: {text}"));

        shell::unregister_hotkey(HOTKEY_SMART);
        shell::unregister_hotkey(HOTKEY_DEEP);
        let mut problems = Vec::new();
        if cfg.hotkeys_enabled {
            for (id, hotkey, text) in [(HOTKEY_SMART, &primary, &cfg.hotkey_primary), (HOTKEY_DEEP, &secondary, &cfg.hotkey_secondary)] {
                match hotkey {
                    None => problems.push(format!("\"{text}\" is not a valid hotkey")),
                    Some(h) if !shell::register_hotkey(id, h.modifiers, h.vk) => problems.push(format!("{} is already used by another app", h.label)),
                    Some(_) => {}
                }
            }
        }
        let mut st = self.st.borrow_mut();
        st.hotkey_smart = label(&primary, &cfg.hotkey_primary);
        st.hotkey_deep = label(&secondary, &cfg.hotkey_secondary);
        st.hotkey_status = if problems.is_empty() {
            String::new()
        } else {
            format!("{}. Change HotkeyPrimary / HotkeySecondary in {}.", problems.join("; "), paths::config_file().display())
        };
    }

    // ---- Tray ----

    fn update_tray(&self, snap: &MemorySnapshot) {
        if !self.st.borrow().tray_ok {
            return;
        }
        let tip = format!("PulseRAM: {:.1}% used · {:.2} GB available", snap.percent, snap.available_gb);
        let badge = self.engine.with_config(|c| c.tray_shows_percent).then(|| {
            let percent = snap.percent.round().clamp(0.0, 99.0) as u32;
            match snap.pressure_level {
                "CRITICAL" => (percent, 0xE11D48, false),
                "ELEVATED" => (percent, 0xF59E0B, true),
                _ => (percent, 0x059669, false),
            }
        });
        shell::tray_update(&tip, badge);
    }

    fn show_tray_menu(self: &Rc<Self>) {
        let cfg = self.engine.config();
        let (smart, deep) = {
            let st = self.st.borrow();
            if cfg.hotkeys_enabled {
                (format!(" ({})", st.hotkey_smart), format!(" ({})", st.hotkey_deep))
            } else {
                (String::new(), String::new())
            }
        };
        let items = [
            MenuItem::new(1, "Open PulseRAM Dashboard"),
            MenuItem::separator(),
            MenuItem::new(2, format!("Smart Trim Idle Apps Now{smart}")),
            MenuItem::new(3, format!("Deep Sweep Now{deep}")),
            MenuItem::new(4, if cfg.focus_boost_active { "Disable Focus Boost Overdrive" } else { "Enable Focus Boost Overdrive (62%)" }),
            MenuItem::new(5, "Thaw All Frozen Processes"),
            MenuItem::separator(),
            MenuItem::new(6, "Exit PulseRAM"),
        ];
        match shell::popup_menu(&items) {
            1 => self.show_window(),
            2 => self.run_optimization("smart", "System Tray Quick Trim"),
            3 => self.run_optimization("deep", "System Tray Deep Sweep"),
            4 => self.toggle_focus_boost(),
            5 => self.thaw_all(),
            6 => self.exit(),
            _ => {}
        }
    }

    fn show_mini_menu(self: &Rc<Self>) {
        let cfg = self.engine.config();
        let mode = self.app().get_mode();
        let mut items: Vec<MenuItem> =
            ["Smart", "Deep", "Standby", "Max"].iter().enumerate().map(|(i, label)| MenuItem::checked(10 + i as u32, format!("Clean mode: {label}"), mode == i as i32)).collect();
        items.push(MenuItem::separator());
        items.push(MenuItem::checked(20, "Focus Boost", cfg.focus_boost_active));
        items.push(MenuItem::checked(21, "Auto-Pilot", cfg.auto_enabled));
        items.push(MenuItem::checked(22, "See-through when idle", cfg.mini_hud_see_through));
        items.push(MenuItem::separator());
        items.push(MenuItem::new(30, "Full view"));
        items.push(MenuItem::new(31, "Hide to tray"));

        self.st.borrow_mut().mini_menu_open = true;
        self.set_mini_see_through(false);
        let choice = shell::popup_menu(&items);
        self.st.borrow_mut().mini_menu_open = false;
        match choice {
            10..=13 => {
                self.app().set_mode(choice as i32 - 10);
                self.refresh(true);
            }
            20 => self.toggle_focus_boost(),
            21 => {
                self.engine.update_config(|c| c.auto_enabled = !cfg.auto_enabled);
                self.sync_settings();
                self.refresh(true);
            }
            22 => {
                let on = !cfg.mini_hud_see_through;
                self.engine.update_config(|c| c.mini_hud_see_through = on);
                if !on {
                    self.set_mini_alpha(None);
                }
            }
            30 => self.toggle_mini_mode(),
            31 => self.hide_to_tray(),
            _ => {}
        }
        self.schedule_mini_see_through();
    }

    // ---- Windows: show, hide, compact view, placement ----

    /// The dashboard (not the widget) is on screen and not minimized.
    fn dashboard_visible(&self) -> bool {
        let st = self.st.borrow();
        st.visible && !st.mini_mode && !self.main.window().is_minimized()
    }

    fn update_minimize_hook(&self) {
        let enabled = self.st.borrow().tray_ok && self.engine.with_config(|c| c.minimize_to_tray);
        shell::set_minimize_to_tray(enabled);
    }

    /// Shows, restores and focuses the active window (dashboard or widget), then renders it immediately.
    fn show_window(self: &Rc<Self>) {
        let (mini_mode, was_visible) = {
            let st = self.st.borrow();
            (st.mini_mode, st.visible)
        };
        if mini_mode {
            if !was_visible {
                self.place_mini();
            }
            if let Err(e) = self.mini.show() {
                log::error(format!("Could not show the compact view: {e}"));
            }
        } else {
            if !was_visible {
                self.restore_main_placement();
            }
            if let Err(e) = self.main.show() {
                log::error(format!("Could not show the dashboard: {e}"));
            }
            let window = self.main.window();
            if window.is_minimized() {
                window.set_minimized(false);
            }
            if !was_visible && self.engine.with_config(|c| c.window_maximized) {
                window.set_maximized(true);
            }
        }
        {
            let mut st = self.st.borrow_mut();
            st.visible = true;
            st.bring_to_front = true;
            st.attach_tries = 0;
        }
        self.update_minimize_hook();
        self.schedule_attach();
        self.refresh(true);
    }

    /// A window's native handle only exists once the event loop has created it, so finish setting it up
    /// (icon, title-bar colours, minimize hook, focus) shortly after showing it.
    fn schedule_attach(self: &Rc<Self>) {
        self.attach_timer.start(TimerMode::Repeated, Duration::from_millis(40), || with_ctx(|ctx| ctx.attach_step()));
    }

    fn attach_step(self: &Rc<Self>) {
        let (mini_mode, visible, tries) = {
            let mut st = self.st.borrow_mut();
            st.attach_tries += 1;
            (st.mini_mode, st.visible, st.attach_tries)
        };
        if !visible || tries > 100 {
            self.attach_timer.stop();
            return;
        }
        let window = if mini_mode { self.mini.window() } else { self.main.window() };
        let Some(hwnd) = shell::window_handle(window) else { return };
        self.attach_timer.stop();

        let (dark, previous, front) = {
            let mut st = self.st.borrow_mut();
            let previous = if mini_mode { std::mem::replace(&mut st.mini_hwnd, hwnd) } else { std::mem::replace(&mut st.main_hwnd, hwnd) };
            (!st.light_theme, previous, std::mem::take(&mut st.bring_to_front))
        };
        if previous != hwnd {
            shell::attach_window(hwnd, dark, !mini_mode);
            if mini_mode {
                // Now that the widget is on its screen its real scale is known.
                self.place_mini();
            }
        }
        if front {
            shell::bring_to_front(hwnd);
        }
        if mini_mode {
            self.schedule_mini_see_through();
        }
    }

    /// Hides the window to the tray (it keeps running; the tray icon or a second launch brings it back).
    fn hide_to_tray(self: &Rc<Self>) {
        let (mini_mode, visible) = {
            let st = self.st.borrow();
            (st.mini_mode, st.visible)
        };
        if !visible {
            return;
        }
        self.save_placement();
        self.fade_timer.stop();
        let result = if mini_mode { self.mini.hide() } else { self.main.hide() };
        if let Err(e) = result {
            log::warn(format!("Hide to tray failed: {e}"));
        }
        {
            let mut st = self.st.borrow_mut();
            st.visible = false;
            st.main_hwnd = 0;
            st.mini_hwnd = 0;
        }
        self.refresh(false); // switches to Efficiency Mode while hidden
        if !self.engine.with_config(|c| c.tray_hint_shown) {
            self.engine.update_config(|c| c.tray_hint_shown = true);
            shell::tray_balloon(
                "PulseRAM is still running",
                "It's in the system tray. Click the icon to open it. You can change this under Profiles → Device → Minimize to tray.",
                false,
            );
        }
    }

    fn toggle_mini_mode(self: &Rc<Self>) {
        let to_mini = !self.st.borrow().mini_mode;
        self.save_placement(); // remember the window we are leaving
        self.fade_timer.stop();
        let result = if to_mini { self.main.hide() } else { self.mini.hide() };
        if let Err(e) = result {
            log::warn(format!("Compact view switch failed: {e}"));
        }
        {
            let mut st = self.st.borrow_mut();
            st.mini_mode = to_mini;
            st.visible = false;
            st.main_hwnd = 0;
            st.mini_hwnd = 0;
            st.mini_status_until = None;
        }
        self.show_window();
    }

    /// Restores the last size/position if it is still on a connected screen; otherwise centres a size that fits
    /// the primary work area (1440×930 at most, so small laptop screens aren't overflowed).
    fn restore_main_placement(&self) {
        let cfg = self.engine.config();
        let window = self.main.window();
        if let (Some(x), Some(y), Some(w), Some(h)) = (cfg.window_x, cfg.window_y, cfg.window_width, cfg.window_height) {
            if w >= 300 && h >= 300 {
                if let Some(area) = shell::work_area_at(x + w.min(200) / 2, y + 16) {
                    window.set_position(PhysicalPosition::new(x, y));
                    window.set_size(PhysicalSize::new(w.min(area.width) as u32, h.min(area.height) as u32));
                    return;
                }
            }
        }
        let area = shell::primary_work_area();
        let width = 1440.min((f64::from(area.width) * 0.95) as i32);
        let height = 930.min((f64::from(area.height) * 0.95) as i32);
        window.set_size(PhysicalSize::new(width as u32, height as u32));
        window.set_position(PhysicalPosition::new(area.x + (area.width - width) / 2, area.y + (area.height - height) / 2));
    }

    /// Puts the widget where it was last time, or in the top-right corner of the primary screen.
    fn place_mini(&self) {
        let cfg = self.engine.config();
        let window = self.mini.window();
        if let (Some(x), Some(y)) = (cfg.mini_hud_x, cfg.mini_hud_y) {
            if shell::work_area_at(x + 40, y + 20).is_some() {
                window.set_position(PhysicalPosition::new(x, y));
                return;
            }
        }
        let scale = window.scale_factor().max(1.0);
        let area = shell::primary_work_area();
        let margin = (16.0 * scale) as i32;
        window.set_position(PhysicalPosition::new(area.x + area.width - (MINI_WIDTH * scale) as i32 - margin, area.y + margin));
    }

    /// Remembers where the visible window is (called on every refresh; saves only when something changed).
    fn save_placement(&self) {
        let (mini_mode, visible) = {
            let st = self.st.borrow();
            (st.mini_mode, st.visible)
        };
        if !visible {
            return;
        }
        let cfg = self.engine.config();
        if mini_mode {
            // Only once the native window exists; before that the position is not meaningful.
            if self.st.borrow().mini_hwnd == 0 {
                return;
            }
            let position = self.mini.window().position();
            if cfg.mini_hud_x != Some(position.x) || cfg.mini_hud_y != Some(position.y) {
                self.engine.update_config(|c| {
                    c.mini_hud_x = Some(position.x);
                    c.mini_hud_y = Some(position.y);
                });
            }
            return;
        }
        let window = self.main.window();
        if self.st.borrow().main_hwnd == 0 || window.is_minimized() {
            return;
        }
        let maximized = window.is_maximized();
        let (position, size) = (window.position(), window.size());
        let (w, h) = (size.width as i32, size.height as i32);
        let moved = !maximized && (cfg.window_x != Some(position.x) || cfg.window_y != Some(position.y) || cfg.window_width != Some(w) || cfg.window_height != Some(h));
        if moved || maximized != cfg.window_maximized {
            self.engine.update_config(|c| {
                c.window_maximized = maximized;
                if !maximized {
                    c.window_x = Some(position.x);
                    c.window_y = Some(position.y);
                    c.window_width = Some(w);
                    c.window_height = Some(h);
                }
            });
        }
    }

    // ---- See-through widget ----

    fn set_mini_alpha(&self, alpha: Option<u8>) {
        let hwnd = self.st.borrow().mini_hwnd;
        if hwnd != 0 {
            shell::set_window_alpha(hwnd, alpha);
        }
    }

    fn see_through_wanted(&self) -> bool {
        let st = self.st.borrow();
        st.mini_mode && st.visible && !st.mini_menu_open && !st.mini_hovered && self.engine.with_config(|c| c.mini_hud_see_through)
    }

    /// Fades the widget after a moment unless the mouse is over it.
    fn schedule_mini_see_through(self: &Rc<Self>) {
        if !self.see_through_wanted() {
            return;
        }
        self.fade_timer.start(TimerMode::SingleShot, Duration::from_millis(1200), || {
            with_ctx(|ctx| {
                if ctx.see_through_wanted() {
                    ctx.set_mini_see_through(true);
                }
            })
        });
    }

    fn set_mini_see_through(&self, see_through: bool) {
        self.fade_timer.stop();
        if see_through {
            self.set_mini_alpha(Some(MINI_SEE_THROUGH_ALPHA));
        } else if self.engine.with_config(|c| c.mini_hud_see_through) {
            self.set_mini_alpha(Some(255));
        }
    }

    // ---- Rendering ----

    fn set_self_efficiency(&self, on: bool) {
        if self.st.borrow().self_efficiency != on && sys::set_self_efficiency(on) {
            self.st.borrow_mut().self_efficiency = on;
        }
    }

    fn render_leaks(&self) {
        let restarting = self.st.borrow().restarting.clone();
        let rows = self.engine.leak_incidents().iter().take(6).map(|incident| leak_row(incident, restarting.contains(&incident.app.to_lowercase()))).collect();
        sync_model(&self.leaks, rows);
    }

    fn update_mini(&self, snap: &MemorySnapshot) {
        self.mini.set_ratio((snap.percent / 100.0).clamp(0.0, 1.0) as f32);
        self.mini.set_pressure(pressure_index(snap.pressure_level));
        self.mini.set_percent_text(s(format!("{:.0}%", snap.percent)));
        self.mini.set_used_text(s(format!("{:.1} / {:.1} GB", snap.used_gb, snap.total_gb)));
        self.mini.set_clean_label(s(match self.selected_mode() {
            "deep" => "Clean · Deep",
            "standby" => "Clean · Standby",
            "max" => "Clean · Max",
            _ => "Clean",
        }));
        if self.st.borrow().mini_status_until.is_some_and(|until| Instant::now() < until) {
            return; // a recent result is still showing
        }
        if snap.commit_level != "ok" {
            self.mini.set_status(s(format!("Memory limit {:.0}% used", snap.commit_percent)));
            self.mini.set_status_color(if snap.commit_level == "critical" { self.mini_color(0xF43F5E, 0xE11D48) } else { self.mini_color(0xF59E0B, 0xB45309) });
        } else {
            let auto = if self.engine.with_config(|c| c.auto_enabled) { format!("Auto {}%", snap.effective_threshold_percent) } else { "Auto off".to_string() };
            self.mini.set_status(s(format!("{:.1} GB free · {auto}", snap.available_gb)));
            self.mini.set_status_color(self.mini_color(0x94A3B8, 0x475569));
        }
    }

    pub fn refresh(self: &Rc<Self>, force: bool) {
        let snap = self.engine.snapshot();
        self.update_tray(&snap);
        self.save_placement();

        let (mini_mode, visible, lite) = {
            let st = self.st.borrow();
            (st.mini_mode, st.visible, st.lite)
        };
        // While PulseRAM sits in the tray, only the tray icon needs updating: filling process lists for an
        // invisible window every 1.5 s just burns CPU and battery.
        if !visible {
            self.set_self_efficiency(self.engine.with_config(|c| c.efficiency_mode_when_hidden));
            return;
        }
        self.set_self_efficiency(false);
        if mini_mode {
            // The widget only needs one snapshot; skip the process list and the chart.
            self.update_mini(&snap);
            return;
        }
        if !force && self.main.window().is_minimized() {
            return;
        }

        let cfg = self.engine.config();
        let app = self.app();
        let grouped = app.get_grouping() == 0;
        let processes = self.engine.processes(grouped);
        let events = self.engine.events();

        // Top bar
        app.set_clock(s(clock::local_now().hms()));
        app.set_process_count(s(processes.len().to_string()));
        app.set_focus_on(cfg.focus_boost_active);
        app.set_auto_on(cfg.auto_enabled);
        app.set_auto_pill(s(if cfg.auto_enabled { format!("Auto {}%", snap.effective_threshold_percent) } else { "Auto off".into() }));
        app.set_auto_tip(s(if cfg.auto_enabled {
            format!(
                "Auto-Pilot sweeps at {}% RAM · profile: {} · cooldown {} min",
                snap.effective_threshold_percent,
                snap.active_profile_name,
                one_decimal_if_needed(f64::from(snap.effective_cooldown_seconds) / 60.0)
            )
        } else {
            "Auto-Pilot is paused".into()
        }));

        // Memory gauge
        app.set_percent_text(s(format!("{:.1}%", snap.percent)));
        app.set_used_text(s(format!("{:.2} / {:.2} GB", snap.used_gb, snap.total_gb)));
        app.set_pressure(pressure_index(snap.pressure_level));
        app.set_pressure_text(s(format!("{} LOAD", snap.pressure_level)));
        let used_ratio = (snap.percent / 100.0).clamp(0.01, 0.999);
        // Standby is part of "Available", so used + standby + free ≈ total.
        let cache_ratio = (snap.standby_gb / snap.total_gb.max(0.1)).clamp(0.0, (0.98 - used_ratio).max(0.0));
        app.set_used_ratio(used_ratio as f32);
        app.set_combined_ratio((used_ratio + cache_ratio).clamp(0.01, 0.999) as f32);
        app.set_threshold(snap.effective_threshold_percent as f32);

        // Physical memory map
        let free_ratio = (1.0 - used_ratio - cache_ratio).max(0.02);
        app.set_bar_used((used_ratio * 100.0).max(2.0) as f32);
        app.set_bar_cache((cache_ratio * 100.0).max(2.0) as f32);
        app.set_bar_free((free_ratio * 100.0).max(2.0) as f32);
        app.set_avail_summary(s(format!("{:.2} GB Available", snap.available_gb)));
        app.set_legend_used(s(format!("{:.2} GB", snap.used_gb)));
        app.set_legend_cache(s(if snap.memory_lists_measured { format!("{:.2} GB", snap.standby_gb) } else { format!("~{:.2} GB (est.)", snap.standby_gb) }));
        app.set_legend_avail(s(format!("{:.2} GB", snap.available_gb)));
        app.set_standby_tip(s(standby_tooltip(&snap, cfg.smart_standby_purge)));

        app.set_clean_sub(s(if self.selected_mode() == "standby" {
            if snap.standby_purge_supported { format!("{:.2} GB standby", snap.standby_gb) } else { "needs Admin".into() }
        } else {
            format!("up to {} trimmable", format_mb(processes.iter().map(|p| p.reclaimable_mb).sum()))
        }));

        // Vitals
        app.set_trimmed(s(format_mb(snap.session_freed_mb)));
        app.set_trimmed_sub(s(format!("{} sweeps", snap.optimization_count)));
        app.set_commit(s(format!("{:.1} / {:.1} GB", snap.commit_total_gb, snap.commit_limit_gb)));
        app.set_commit_sub(s(if snap.commit_level == "ok" {
            format!("{:.0}% used", snap.commit_percent)
        } else {
            format!("{:.0}% used — near the limit", snap.commit_percent)
        }));
        app.set_commit_level(match snap.commit_level {
            "critical" => 2,
            "high" => 1,
            _ => 0,
        });
        if snap.commit_level == "ok" && app.get_commit_banner() {
            app.set_commit_banner(false);
            self.st.borrow_mut().commit_advice = None;
        }
        let has_pagefile = snap.pagefile_total_gb > 0.05;
        app.set_pagefile(s(if has_pagefile { format!("{:.2} GB", snap.pagefile_used_gb) } else { "0.00 GB".into() }));
        app.set_pagefile_sub(s(if has_pagefile { format!("{:.0}% of {:.1} GB", snap.pagefile_percent, snap.pagefile_total_gb) } else { "idle".into() }));
        app.set_kernel(s(format!("{:.0} MB", snap.kernel_total_mb)));
        app.set_kernel_sub(s(format!("paged {:.0} · non-paged {:.0} MB", snap.kernel_paged_mb, snap.kernel_nonpaged_mb)));
        app.set_cpu(s(format!("{:.1}%", snap.cpu_percent)));
        app.set_cpu_sub(s(format!("{} processes", snap.process_count)));
        let (frozen, eco) = self.engine.governed_counts();
        app.set_rules(s(format!("{} shield · {} cap", cfg.protected_apps.len(), cfg.app_memory_caps_mb.len())));
        app.set_rules_sub(s(format!("{frozen} frozen · {eco} eco")));

        app.set_schedule_countdown(s(if cfg.scheduled_trim_enabled && snap.next_scheduled_seconds > 0 {
            format!("next in {:02}:{:02}", snap.next_scheduled_seconds / 60, snap.next_scheduled_seconds % 60)
        } else {
            "off".into()
        }));

        if let Some(latest) = events.first() {
            app.set_last_sweep(s(format!("{} · {}", latest.timestamp, describe_event(latest).0)));
        }
        app.set_event_count(s(format!("{} sweeps", events.len())));

        let view = app.get_view();
        if view == 0 {
            let (ram, area, cpu) = chart_paths(&self.engine.telemetry(), app.get_chart_width(), app.get_chart_height());
            app.set_chart_ram(s(ram));
            app.set_chart_area(s(area));
            app.set_chart_cpu(s(cpu));
            sync_model(&self.events, events.iter().take(15).map(event_row).collect());
            // Already sorted by memory.
            sync_model(&self.top_apps, processes.iter().take(if lite { 5 } else { 8 }).map(|p| process_row(p, grouped)).collect());
        } else if view == 1 {
            self.render_leaks();
            self.render_process_list(&processes, grouped, lite);
        } else {
            sync_model(
                &self.profiles,
                cfg.profiles
                    .iter()
                    .take(4)
                    .map(|p| ProfileRow {
                        id: s(&p.id),
                        name: s(&p.name),
                        summary: s(format!("{}% · {}{}", p.ram_threshold_percent, p.auto_mode, if p.auto_shield_matched { " · shields app" } else { "" })),
                        description: s(&p.description),
                        active: cfg.forced_profile_id == p.id,
                    })
                    .collect(),
            );
        }
    }

    fn render_process_list(&self, processes: &[ProcessItem], grouped: bool, lite: bool) {
        let app = self.app();
        let query = app.get_search().trim().to_lowercase();
        let filter = app.get_filter();
        let mut rows: Vec<&ProcessItem> = processes
            .iter()
            .filter(|p| query.is_empty() || p.name.to_lowercase().contains(&query) || p.pid.to_string().contains(&query))
            .filter(|p| match filter {
                1 => (p.smart_score >= 55 && !p.is_protected) || p.is_leaking,
                2 => p.is_frozen || p.is_eco,
                3 => p.is_protected || p.cap_mb.is_some(),
                _ => true,
            })
            .collect();
        let by_memory = |a: &&ProcessItem, b: &&ProcessItem| b.memory_mb.total_cmp(&a.memory_mb);
        match app.get_sort() {
            1 => rows.sort_by(|a, b| b.smart_score.cmp(&a.smart_score).then_with(|| by_memory(a, b))),
            2 => rows.sort_by(|a, b| b.cpu_percent.total_cmp(&a.cpu_percent).then_with(|| by_memory(a, b))),
            3 => rows.sort_by(|a, b| b.leak_rate_mb_per_min.total_cmp(&a.leak_rate_mb_per_min).then_with(|| by_memory(a, b))),
            _ => rows.sort_by(by_memory),
        }
        sync_model(&self.processes, rows.into_iter().take(if lite { 25 } else { 50 }).map(|p| process_row(p, grouped)).collect());
    }

    // ---- Exit ----

    /// Idempotent: runs on exit and when Windows restarts or signs out.
    fn shutdown(&self, reason: &str) {
        if std::mem::replace(&mut self.st.borrow_mut().shutdown_done, true) {
            return;
        }
        for timer in [&self.refresh_timer, &self.toast_timer, &self.update_timer, &self.fade_timer, &self.attach_timer] {
            timer.stop();
        }
        self.save_placement();
        shell::unregister_hotkey(HOTKEY_SMART);
        shell::unregister_hotkey(HOTKEY_DEEP);
        shell::tray_remove();
        self.engine.shutdown();
        shell::destroy();
        log::info(reason);
        log::mark_session_end();
    }

    fn exit(&self) {
        self.shutdown("Clean shutdown");
        let _ = self.main.hide();
        let _ = self.mini.hide();
        let _ = slint::quit_event_loop();
    }
}

/// Development aid (non-Windows builds only): PULSERAM_SNAPSHOT=<file.ppm>[:<delay ms>[:<view 0-2>]] renders the
/// window to an image and exits, so the interface can be checked without a desktop.
#[cfg(not(windows))]
fn dev_snapshot(ctx: &Rc<Ctx>) {
    let Ok(spec) = std::env::var("PULSERAM_SNAPSHOT") else { return };
    let mut parts = spec.split(':');
    let path = parts.next().unwrap_or("snapshot.ppm").to_string();
    let delay: u64 = parts.next().and_then(|d| d.parse().ok()).unwrap_or(4000);
    let view: i32 = parts.next().and_then(|v| v.parse().ok()).unwrap_or(0);
    ctx.app().set_view(view);
    if let Ok(dialog) = std::env::var("PULSERAM_SNAPSHOT_DIALOG") {
        if dialog == "cap" {
            ctx.open_cap_dialog("chrome.exe");
        }
    }
    Timer::single_shot(Duration::from_millis(delay), move || {
        with_ctx(|ctx| ctx.refresh(true));
        let path = path.clone();
        // Give the refreshed frame a moment to render.
        Timer::single_shot(Duration::from_millis(700), move || {
            with_ctx(|ctx| {
                let window = if ctx.st.borrow().mini_mode { ctx.mini.window() } else { ctx.main.window() };
                if let Ok(buffer) = window.take_snapshot() {
                    let mut out = format!("P6\n{} {}\n255\n", buffer.width(), buffer.height()).into_bytes();
                    for pixel in buffer.as_slice() {
                        out.extend_from_slice(&[pixel.r, pixel.g, pixel.b]);
                    }
                    let _ = std::fs::write(&path, out);
                }
                ctx.exit();
            });
        });
    });
}

#[cfg(windows)]
fn dev_snapshot(_ctx: &Rc<Ctx>) {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn describes_sweeps_with_measured_values_only() {
        let event = OptimizationEvent {
            mode: "DEEP".into(),
            freed_mb: 1536.0,
            processes_trimmed: 12,
            available_gain_mb: 900.0,
            before_percent: 81.0,
            after_percent: 64.5,
            standby_purged: true,
            combined_mb: 70.3,
            ..Default::default()
        };
        let (title, sub) = describe_event(&event);
        assert_eq!(title, "Trimmed 1.50 GB from 12 process(es)");
        assert_eq!(sub, "Available +900 MB · RAM 81.0% → 64.5% · standby purged · 70 MB deduplicated");

        let standby = OptimizationEvent { mode: "STANDBY".into(), standby_purged: true, free_list_gain_mb: 2048.0, before_percent: 50.0, after_percent: 50.0, ..Default::default() };
        assert_eq!(describe_event(&standby), ("Standby cache purged · free memory +2.00 GB".to_string(), "RAM 50.0% → 50.0%".to_string()));

        let skipped = OptimizationEvent { mode: "STANDBY".into(), skipped: true, reason: "Manual".into(), note: "needs admin".into(), ..Default::default() };
        assert_eq!(describe_event(&skipped), ("STANDBY: nothing changed".to_string(), "needs admin".to_string()));
    }

    #[test]
    fn chart_scales_to_the_canvas() {
        let points = [TelemetryPoint { used_percent: 50.0, cpu_percent: 0.0 }, TelemetryPoint { used_percent: 100.0, cpu_percent: 150.0 }];
        let (ram, area, cpu) = chart_paths(&points, 590.0, 100.0);
        assert_eq!(ram, "M 0.0 50.0 L 10.0 0.0 ");
        assert_eq!(cpu, "M 0.0 100.0 L 10.0 0.0 ");
        assert_eq!(area, "M 0 100.0 L 0.0 50.0 L 10.0 0.0  L 10.0 100.0 Z");
        assert_eq!(chart_paths(&points[..1], 590.0, 100.0).0, "");
    }

    #[test]
    fn number_formats_match_the_old_ui() {
        assert_eq!(one_decimal_if_needed(2.0), "2");
        assert_eq!(one_decimal_if_needed(2.5), "2.5");
        assert_eq!(two_decimals_if_needed(0.75), "0.75");
        assert_eq!(two_decimals_if_needed(1.5), "1.5");
        assert_eq!(two_decimals_if_needed(2.0), "2");
    }

    #[test]
    fn process_rows_show_caps_growth_and_group_size() {
        let item = ProcessItem {
            name: "chrome.exe".into(),
            pid: 2000,
            process_count: 4,
            memory_mb: 1650.0,
            private_mb: 1400.0,
            peak_memory_mb: 1700.0,
            cpu_percent: 6.25,
            leak_rate_mb_per_min: 42.0,
            is_leaking: false,
            is_foreground: false,
            is_protected: false,
            is_frozen: false,
            is_eco: true,
            cap_mb: Some(2048),
            smart_score: 61,
            reclaimable_mb: 1650.0,
            badge: Badge::Eco,
        };
        let row = process_row(&item, true);
        assert_eq!(row.meta.as_str(), "4 procs · 6.2% CPU · Cap 2048M · +42M/m");
        assert_eq!((row.initials.as_str(), row.memory.as_str(), row.cap.as_str(), row.badge_kind), ("CH", "1.61 GB", "2048M", 4));
        assert_eq!(process_row(&item, false).meta.as_str(), "PID 2000 · 6.2% CPU · Cap 2048M · +42M/m");
    }
}
