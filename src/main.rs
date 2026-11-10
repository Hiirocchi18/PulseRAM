//! PulseRAM — memory optimizer and process governor for Windows 10 & 11.

// Release builds are a Windows GUI program: no console window.
#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]
// The non-Windows development build stubs out parts of the platform layer.
#![cfg_attr(not(windows), allow(dead_code))]

mod app;
mod clock;
mod config;
mod engine;
mod leaks;
mod log;
mod model;
mod paths;
mod policy;
mod shell;
mod startup;
mod sys;
mod update;

slint::include_modules!();

fn has_arg(args: &[String], name: &str) -> bool {
    args.iter().any(|a| a.eq_ignore_ascii_case(name))
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();

    // Elevated: ignore junctions planted by non-admin programs (Windows 11 22H2+), and take the current
    // directory out of the DLL search order.
    sys::enable_redirection_guard();
    shell::harden_dll_search();

    // Used by the installer before upgrading/uninstalling: resume anything PulseRAM suspended.
    if has_arg(&args, "--thaw-frozen") {
        sys::enable_privileges();
        engine::recover_frozen_processes();
        return;
    }

    // Already running (often hidden in the tray): that instance is asked to show its window instead. A --tray
    // launch (e.g. a leftover startup entry) stays silent.
    let tray = has_arg(&args, "--tray");
    if let shell::Instance::AlreadyRunning = shell::acquire_single_instance(has_arg(&args, "--elevated-relaunch"), tray) {
        return;
    }

    let previous_session_crashed = log::mark_session_start();
    if previous_session_crashed {
        log::warn("Previous session ended without a clean shutdown");
    }
    // Crash logging: at least record why.
    std::panic::set_hook(Box::new(|info| log::error(format!("Unexpected failure: {info}"))));

    // Software rendering: no GPU context, so PulseRAM itself stays small in memory.
    if let Err(e) = slint::BackendSelector::new().backend_name("winit".into()).renderer_name("software".into()).select() {
        log::error(format!("Could not start the interface: {e}"));
        return;
    }

    app::run(app::Launch { tray, compact: has_arg(&args, "--compact"), previous_session_crashed });
}
