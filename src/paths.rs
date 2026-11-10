//! Where PulseRAM keeps its files.

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;

pub fn exe_path() -> PathBuf {
    std::env::current_exe().unwrap_or_else(|_| PathBuf::from("PulseRAM.exe"))
}

pub fn exe_dir() -> PathBuf {
    exe_path().parent().map(PathBuf::from).unwrap_or_else(|| PathBuf::from("."))
}

static PORTABLE_UNAVAILABLE: AtomicBool = AtomicBool::new(false);

/// Portable mode: a file named portable.txt next to PulseRAM.exe keeps all settings, logs and the frozen
/// journal in a "data" folder beside the EXE instead of %LocalAppData%\PulseRAM.
pub fn is_portable() -> bool {
    static PORTABLE: OnceLock<bool> = OnceLock::new();
    *PORTABLE.get_or_init(|| {
        if !exe_dir().join("portable.txt").exists() {
            return false;
        }
        // Only if the folder is writable (e.g. not unpacked into Program Files); otherwise use %LocalAppData%.
        let dir = exe_dir().join("data");
        let probe = dir.join(format!(".write-test-{}-{}", std::process::id(), crate::clock::unix_now()));
        // create_new never follows an existing file or link.
        let writable = std::fs::create_dir_all(&dir).is_ok() && std::fs::OpenOptions::new().write(true).create_new(true).open(&probe).is_ok();
        if writable {
            let _ = std::fs::remove_file(&probe);
        } else {
            PORTABLE_UNAVAILABLE.store(true, Ordering::Relaxed);
        }
        writable
    })
}

/// portable.txt is present but its folder can't be written, so settings went to %LocalAppData%.
pub fn portable_unavailable() -> bool {
    is_portable();
    PORTABLE_UNAVAILABLE.load(Ordering::Relaxed)
}

pub fn data_dir() -> PathBuf {
    if is_portable() {
        exe_dir().join("data")
    } else {
        crate::sys::local_app_data().join("PulseRAM")
    }
}

pub fn config_file() -> PathBuf {
    data_dir().join("pulseram_config.json")
}

pub fn log_dir() -> PathBuf {
    data_dir().join("logs")
}
