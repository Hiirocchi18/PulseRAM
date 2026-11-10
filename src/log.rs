//! Small, dependency-free file logger: `<data>\logs\pulseram-yyyyMMdd.log`.
//! Keeps 7 days and at most ~20 MB in total, rolls files at 2 MB, collapses an identical message repeated
//! within a minute into one line with a count, and never fails the caller.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{Duration, Instant, SystemTime};

use crate::{clock, paths, sys};

const MAX_FILE_BYTES: u64 = 2 * 1024 * 1024;
const KEEP_DAYS: u64 = 7;
const MAX_FOLDER_BYTES: u64 = 20 * 1024 * 1024;
const REPEAT_WINDOW: Duration = Duration::from_secs(60);

#[derive(Default)]
struct State {
    recent: HashMap<String, (Instant, u32)>,
    pruned_on: String,
}

static STATE: Mutex<Option<State>> = Mutex::new(None);

pub fn info(message: impl AsRef<str>) {
    write("INFO ", message.as_ref());
}

pub fn warn(message: impl AsRef<str>) {
    write("WARN ", message.as_ref());
}

pub fn error(message: impl AsRef<str>) {
    write("ERROR", message.as_ref());
}

fn thread_tag() -> String {
    format!("{:?}", std::thread::current().id()).chars().filter(char::is_ascii_digit).collect()
}

fn write(level: &str, message: &str) {
    let mut guard = STATE.lock().unwrap_or_else(|e| e.into_inner());
    let state = guard.get_or_insert_with(State::default);

    // Collapse repeats (e.g. a tick that fails every 1.5 s) so the log can't balloon.
    let now = Instant::now();
    let key = format!("{level}{message}");
    let mut suppressed = 0;
    if let Some((last, count)) = state.recent.get_mut(&key) {
        if now.duration_since(*last) < REPEAT_WINDOW {
            *count += 1;
            return;
        }
        suppressed = *count;
    }
    if state.recent.len() > 200 {
        state.recent.clear();
    }
    state.recent.insert(key, (now, 0));

    let t = clock::local_now();
    let mut line = format!(
        "{:04}-{:02}-{:02} {}.{:03} {} [{}] {}",
        t.year, t.month, t.day, t.hms(), t.millis, level, thread_tag(), message
    );
    if suppressed > 0 {
        line.push_str(&format!(" (repeated {suppressed} more time(s) in the last minute)"));
    }
    line.push_str("\r\n");

    let dir = paths::log_dir();
    if std::fs::create_dir_all(&dir).is_err() {
        return;
    }
    let day = t.ymd_compact();
    if state.pruned_on != day {
        state.pruned_on = day.clone();
        prune();
    }
    // Roll to pulseram-yyyyMMdd-1.log, -2 … when a file passes 2 MB (no renames: an elevated rename in this
    // user-writable folder could be redirected).
    let current = dir.join(format!("pulseram-{day}.log"));
    let mut file = current.clone();
    let mut part = 1;
    while part <= 20 && std::fs::metadata(&file).map(|m| m.len() > MAX_FILE_BYTES).unwrap_or(false) {
        file = dir.join(format!("pulseram-{day}-{part}.log"));
        part += 1;
    }
    if file != current && !file.exists() {
        prune(); // enforce the folder cap as files roll
    }
    sys::write_text(&file, &line, true);
}

/// Runs once per day (tray apps run for weeks): drops old files, then the oldest until under the size cap.
fn prune() {
    let Ok(entries) = std::fs::read_dir(paths::log_dir()) else { return };
    let mut files: Vec<(PathBuf, SystemTime, u64)> = entries
        .flatten()
        .filter(|e| {
            let name = e.file_name().to_string_lossy().to_lowercase();
            name.starts_with("pulseram-") && name.ends_with(".log")
        })
        .filter_map(|e| {
            let meta = e.metadata().ok()?;
            meta.is_file().then(|| (e.path(), meta.modified().unwrap_or(SystemTime::UNIX_EPOCH), meta.len()))
        })
        .collect();
    files.sort_by_key(|f| f.1);

    let cutoff = SystemTime::now().checked_sub(Duration::from_secs(KEEP_DAYS * 86_400)).unwrap_or(SystemTime::UNIX_EPOCH);
    files.retain(|(path, modified, _)| {
        if *modified < cutoff {
            sys::delete_file(path);
            false
        } else {
            true
        }
    });
    let mut total: u64 = files.iter().map(|f| f.2).sum();
    for (path, _, len) in &files {
        if total <= MAX_FOLDER_BYTES {
            break;
        }
        total -= len;
        sys::delete_file(path);
    }
}

fn running_marker() -> PathBuf {
    paths::data_dir().join("running.marker")
}

/// Call once at startup. Returns true if the previous session ended without a clean shutdown: the marker
/// is present while PulseRAM runs, so finding it at the next start means the last session crashed.
pub fn mark_session_start() -> bool {
    let _ = std::fs::create_dir_all(paths::data_dir());
    let crashed = running_marker().exists();
    sys::write_text(&running_marker(), &std::process::id().to_string(), false);
    crashed
}

pub fn mark_session_end() {
    sys::delete_file(&running_marker());
}

pub fn open_folder() {
    let dir = paths::log_dir();
    let _ = std::fs::create_dir_all(&dir);
    sys::open_as_user(&dir.to_string_lossy());
}
