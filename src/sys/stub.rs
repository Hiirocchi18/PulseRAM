//! Fake platform for non-Windows development builds and unit tests: a made-up 16 GB PC with a handful of
//! processes that respond to trims, freezes and caps. Nothing here ships in PulseRAM.exe.

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use super::{CapOutcome, HttpResult, MemStatus, MemoryLists, PageFileKind, PerfInfo, Privileges, ProcInfo};

const MB: u64 = 1024 * 1024;
const GB: u64 = 1024 * MB;
const TOTAL: u64 = 16 * GB;

struct FakeProc {
    pid: u32,
    name: &'static str,
    session: u32,
    working_set: u64,
    private: u64,
    /// CPU use in percent of one core.
    busy: f64,
    cpu_time: u64,
    /// Private bytes gained per scan (a leaking app).
    growth: u64,
}

struct World {
    procs: Vec<FakeProc>,
    suspended: HashSet<u32>,
    capped: HashSet<u32>,
    standby: u64,
    scans: u64,
    run_key: bool,
}

fn world() -> std::sync::MutexGuard<'static, Option<World>> {
    static WORLD: Mutex<Option<World>> = Mutex::new(None);
    let mut guard = WORLD.lock().unwrap_or_else(|e| e.into_inner());
    if guard.is_none() {
        let p = |pid, name, session, ws_mb: u64, busy, growth_mb: u64| FakeProc {
            pid,
            name,
            session,
            working_set: ws_mb * MB,
            private: ws_mb * MB * 9 / 10,
            busy,
            cpu_time: 0,
            growth: growth_mb * MB,
        };
        *guard = Some(World {
            procs: vec![
                p(4, "System.exe", 0, 12, 0.5, 0),
                p(120, "Registry.exe", 0, 60, 0.0, 0),
                p(600, "csrss.exe", 1, 8, 0.1, 0),
                p(900, "dwm.exe", 1, 140, 2.0, 0),
                p(1200, "explorer.exe", 1, 210, 0.4, 0),
                p(2000, "chrome.exe", 1, 620, 6.0, 0),
                p(2004, "chrome.exe", 1, 410, 0.2, 0),
                p(2008, "chrome.exe", 1, 380, 0.0, 0),
                p(2012, "chrome.exe", 1, 240, 0.0, 0),
                p(3000, "Code.exe", 1, 540, 3.5, 0),
                p(3004, "Code.exe", 1, 320, 0.3, 0),
                p(3100, "rust-analyzer.exe", 1, 880, 1.0, 0),
                p(4000, "Teams.exe", 1, 470, 0.1, 30),
                p(4100, "Spotify.exe", 1, 260, 0.6, 0),
                p(4200, "Discord.exe", 1, 330, 0.2, 0),
                p(4300, "OneDrive.exe", 1, 95, 0.0, 0),
                p(4400, "SearchHost.exe", 1, 180, 0.0, 0),
                p(4500, "MsMpEng.exe", 0, 290, 0.8, 0),
                p(4600, "svchost.exe", 0, 75, 0.1, 0),
                p(4604, "svchost.exe", 0, 48, 0.0, 0),
                p(4700, "PulseRAM.exe", 1, 14, 0.1, 0),
                p(4800, "notepad.exe", 1, 22, 0.0, 0),
                p(4900, "steamwebhelper.exe", 1, 310, 0.0, 0),
                p(5000, "obs64.exe", 1, 280, 0.0, 0),
            ],
            suspended: HashSet::new(),
            capped: HashSet::new(),
            standby: 5 * GB + 350 * MB,
            scans: 0,
            run_key: false,
        });
    }
    guard
}

fn with_world<T>(f: impl FnOnce(&mut World) -> T) -> T {
    f(world().as_mut().expect("world initialised"))
}

fn used_bytes(w: &World) -> u64 {
    2 * GB + w.procs.iter().map(|p| p.working_set).sum::<u64>()
}

pub fn memory_status() -> Option<MemStatus> {
    with_world(|w| {
        let used = used_bytes(w).min(TOTAL - 256 * MB);
        Some(MemStatus {
            memory_load: (used * 100 / TOTAL) as u32,
            total_phys: TOTAL,
            avail_phys: TOTAL - used,
            total_page_file: TOTAL + 3 * GB,
            avail_page_file: TOTAL + 3 * GB - used - GB,
        })
    })
}

pub fn perf_info() -> Option<PerfInfo> {
    with_world(|w| {
        let committed = 3 * GB + w.procs.iter().map(|p| p.private).sum::<u64>();
        Some(PerfInfo {
            commit_total: committed / 4096,
            commit_limit: (TOTAL + 3 * GB) / 4096,
            system_cache: w.standby / 4096,
            kernel_total: 357 * MB / 4096,
            kernel_paged: 186 * MB / 4096,
            kernel_nonpaged: 171 * MB / 4096,
            page_size: 4096,
            process_count: w.procs.len() as u32 + 125,
        })
    })
}

pub struct MemCounters;

impl MemCounters {
    pub fn new() -> Self {
        MemCounters
    }

    pub fn read(&mut self) -> Option<(f64, f64)> {
        with_world(|w| {
            let avail = TOTAL.saturating_sub(used_bytes(w));
            let standby = w.standby.min(avail);
            Some((standby as f64, (avail - standby) as f64))
        })
    }
}

pub fn query_memory_lists() -> Option<MemoryLists> {
    if !is_elevated() {
        return None;
    }
    with_world(|w| {
        let pages = w.standby / 4096;
        let mut lists = MemoryLists::default();
        for (i, share) in [2u64, 6, 10, 8, 14, 40, 15, 5].iter().enumerate() {
            lists.standby_pages_by_priority[i] = pages * share / 100;
        }
        lists.repurposed_pages = w.scans * 600;
        Some(lists)
    })
}

pub fn flush_modified_list() -> bool {
    is_elevated()
}

pub fn purge_standby(low_priority_only: bool) -> bool {
    if !is_elevated() {
        return false;
    }
    with_world(|w| w.standby = if low_priority_only { w.standby * 3 / 4 } else { 200 * MB });
    true
}

pub fn combine_pages() -> Option<u64> {
    is_elevated().then_some(18_000)
}

pub fn list_processes() -> Vec<ProcInfo> {
    with_world(|w| {
        w.scans += 1;
        let suspended = w.suspended.clone();
        for p in &mut w.procs {
            if suspended.contains(&p.pid) {
                continue;
            }
            // Each scan stands for ~2 s on a 12-thread PC.
            p.cpu_time += (p.busy / 100.0 * 2.0 * 10_000_000.0) as u64;
            p.private += p.growth;
            p.working_set += p.growth;
            // Trimmed pages fault back in over time.
            let resting = p.private * 10 / 9;
            if p.working_set < resting && !w.capped.contains(&p.pid) {
                p.working_set += (resting - p.working_set) / 12;
            }
        }
        w.procs
            .iter()
            .map(|p| ProcInfo {
                pid: p.pid,
                name: p.name.to_string(),
                session_id: p.session,
                working_set: p.working_set,
                peak_working_set: p.working_set * 5 / 4,
                private_bytes: p.private,
                cpu_time: p.cpu_time,
                create_time: 133_000_000_000_000_000 + i64::from(p.pid),
            })
            .collect()
    })
}

pub fn system_times() -> Option<(u64, u64, u64)> {
    let elapsed = SystemTime::now().duration_since(UNIX_EPOCH).ok()?.as_nanos() as u64 / 100;
    let total = elapsed * 12;
    let wobble = ((elapsed / 10_000_000) % 20) as u64; // 0..19
    let busy = total / 100 * (6 + wobble / 4);
    Some((total - busy, total - busy / 2, busy / 2))
}

pub fn foreground_pid() -> u32 {
    3000
}

pub fn process_image_path(pid: u32) -> Option<String> {
    with_world(|w| w.procs.iter().find(|p| p.pid == pid).map(|p| format!("C:\\Program Files\\Fake\\{}", p.name)))
}

pub fn process_name(pid: u32) -> Option<String> {
    with_world(|w| w.procs.iter().find(|p| p.pid == pid).map(|p| p.name.to_string()))
}

pub fn own_session_id() -> u32 {
    1
}

pub fn integrity_level(pid: u32) -> Option<u32> {
    with_world(|w| w.procs.iter().find(|p| p.pid == pid).map(|p| if p.session == 0 { 0x4000 } else { 0x2000 }))
}

fn protected_process(p: &FakeProc) -> bool {
    p.session == 0 && !is_elevated()
}

pub fn trim_process(pid: u32, _shrink_fallback: bool) -> Option<u64> {
    with_world(|w| {
        let p = w.procs.iter_mut().find(|p| p.pid == pid)?;
        if protected_process(p) {
            return None;
        }
        let keep = p.working_set * 3 / 10;
        let trimmed = p.working_set - keep;
        p.working_set = keep;
        Some(trimmed)
    })
}

pub fn suspend_process(pid: u32) -> Option<bool> {
    trim_process(pid, false)?;
    with_world(|w| w.suspended.insert(pid));
    Some(true)
}

pub fn resume_process(pid: u32) -> bool {
    with_world(|w| w.suspended.remove(&pid))
}

pub fn set_eco(pid: u32, enable: bool) -> bool {
    if enable {
        trim_process(pid, false).is_some()
    } else {
        true
    }
}

pub fn set_self_efficiency(_on: bool) -> bool {
    true
}

pub fn apply_hard_cap(pid: u32, cap_bytes: u64, force: bool) -> CapOutcome {
    with_world(|w| {
        if !force && w.capped.contains(&pid) {
            return CapOutcome::AlreadySet;
        }
        let Some(p) = w.procs.iter_mut().find(|p| p.pid == pid) else { return CapOutcome::Refused };
        if protected_process(p) {
            return CapOutcome::Refused;
        }
        let trimmed = p.working_set.saturating_sub(cap_bytes);
        p.working_set -= trimmed;
        w.capped.insert(pid);
        CapOutcome::Applied(trimmed)
    })
}

pub fn release_hard_cap(pid: u32) {
    with_world(|w| w.capped.remove(&pid));
}

pub struct SweepBoost;

impl SweepBoost {
    pub fn begin() -> SweepBoost {
        SweepBoost
    }
}

pub fn close_main_windows(pids: &[u32]) -> usize {
    with_world(|w| {
        let before = w.procs.len();
        w.procs.retain(|p| !pids.contains(&p.pid));
        before - w.procs.len()
    })
}

pub fn is_elevated() -> bool {
    std::env::var_os("PULSERAM_FAKE_ADMIN").is_some()
}

pub fn enable_privileges() -> Privileges {
    Privileges { is_admin: is_elevated(), purge_rights: is_elevated() }
}

pub fn enable_redirection_guard() {}

pub fn is_admin_only_writable(_exe_path: &Path) -> bool {
    false
}

pub fn write_text(path: &Path, text: &str, append: bool) -> bool {
    use std::io::Write;
    if append {
        std::fs::OpenOptions::new().create(true).append(true).open(path).and_then(|mut f| f.write_all(text.as_bytes())).is_ok()
    } else {
        std::fs::write(path, text).is_ok()
    }
}

pub fn delete_file(path: &Path) -> bool {
    !path.exists() || std::fs::remove_file(path).is_ok()
}

pub fn utc_offset_secs() -> i64 {
    0
}

pub fn logical_cores() -> usize {
    12
}

pub fn message_beep() {}

pub fn system_directory() -> PathBuf {
    PathBuf::from("/usr/bin")
}

pub fn windows_directory() -> PathBuf {
    PathBuf::from("/usr")
}

pub fn local_app_data() -> PathBuf {
    std::env::var_os("PULSERAM_DATA_HOME").map(PathBuf::from).unwrap_or_else(|| std::env::temp_dir().join("pulseram-dev"))
}

pub fn run_key_exists() -> bool {
    with_world(|w| w.run_key)
}

pub fn set_run_key(enable: bool, _exe_path: &Path) -> Result<(), String> {
    with_world(|w| w.run_key = enable);
    Ok(())
}

pub fn page_file_setup() -> (PageFileKind, i32) {
    (PageFileKind::SystemManaged, 0)
}

pub fn system_uses_light_theme() -> bool {
    std::env::var_os("PULSERAM_FAKE_LIGHT").is_some()
}

pub fn run_schtasks(_args: &[&str]) -> (i32, String) {
    (1, String::new())
}

pub fn current_user_name() -> Option<String> {
    Some("DEV\\user".into())
}

pub fn shell_window_exists() -> bool {
    true
}

pub fn open_as_user(_target: &str) -> bool {
    true
}

pub fn launch_as_user(_exe: &Path) -> bool {
    true
}

pub fn open_page_file_settings() -> bool {
    true
}

pub fn relaunch_elevated(_exe: &Path, _args: &str) -> bool {
    false
}

pub fn https_get(_host: &str, _path: &str, _user_agent: &str, _accept: &str) -> HttpResult {
    match std::env::var("PULSERAM_FAKE_RELEASE") {
        Ok(tag) => HttpResult::Response(
            200,
            format!(r#"{{"tag_name":"{tag}","html_url":"https://github.com/Hiirocchi18/PulseRAM/releases/tag/{tag}","name":"PulseRAM {tag}","draft":false,"prerelease":false}}"#),
        ),
        Err(_) => HttpResult::Failed("offline (development build)".into()),
    }
}
