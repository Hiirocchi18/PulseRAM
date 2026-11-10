//! Platform layer. Everything that talks to Windows lives under `sys::windows`; the engine above it is
//! plain Rust. Non-Windows builds get `sys::stub` (fake data) so the UI and engine can be developed and
//! unit-tested on any machine — PulseRAM itself only ships for Windows.

#[derive(Clone, Copy, Debug, Default)]
pub struct MemStatus {
    pub memory_load: u32,
    pub total_phys: u64,
    pub avail_phys: u64,
    pub total_page_file: u64,
    pub avail_page_file: u64,
}

/// Counts are in pages unless noted.
#[derive(Clone, Copy, Debug, Default)]
pub struct PerfInfo {
    pub commit_total: u64,
    pub commit_limit: u64,
    pub system_cache: u64,
    pub kernel_total: u64,
    pub kernel_paged: u64,
    pub kernel_nonpaged: u64,
    pub page_size: u64,
    pub process_count: u32,
}

#[derive(Clone, Debug)]
pub struct ProcInfo {
    pub pid: u32,
    /// Image name, always with ".exe" (pseudo-processes such as "Registry" get it too, as in PulseRAM 1.x).
    pub name: String,
    pub session_id: u32,
    pub working_set: u64,
    pub peak_working_set: u64,
    pub private_bytes: u64,
    /// Kernel + user CPU time in 100 ns units.
    pub cpu_time: u64,
    /// Process creation time as a Windows FILETIME (100 ns since 1601, UTC).
    pub create_time: i64,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct MemoryLists {
    pub standby_pages_by_priority: [u64; 8],
    pub repurposed_pages: u64,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct Privileges {
    pub is_admin: bool,
    /// SeProfileSingleProcessPrivilege is enabled: kernel purges and page combining are possible.
    pub purge_rights: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CapOutcome {
    /// Cap applied; bytes trimmed from the working set as a result.
    Applied(u64),
    AlreadySet,
    Refused,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PageFileKind {
    None,
    SystemManaged,
    Custom,
    Unknown,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HttpResult {
    /// HTTP status and response body.
    Response(u32, String),
    /// The request could not be made (offline, timeout, TLS failure …).
    Failed(String),
}

/// "chrome" → "chrome.exe"; names that already end in .exe are returned unchanged.
pub fn exe_name(name: &str) -> String {
    if has_exe_suffix(name) {
        name.to_string()
    } else {
        format!("{name}.exe")
    }
}

/// "chrome.exe" → "chrome".
pub fn base_name(name: &str) -> &str {
    if has_exe_suffix(name) {
        &name[..name.len() - 4]
    } else {
        name
    }
}

fn has_exe_suffix(name: &str) -> bool {
    name.len() >= 4 && name.is_char_boundary(name.len() - 4) && name[name.len() - 4..].eq_ignore_ascii_case(".exe")
}

/// .NET ticks (100 ns since 0001-01-01) for a FILETIME, the unit the 1.x frozen-process journal uses.
pub fn filetime_to_dotnet_ticks(filetime: i64) -> i64 {
    if filetime <= 0 {
        0
    } else {
        filetime + 504_911_232_000_000_000
    }
}

/// "?:\pagefile.sys" = automatic for all drives; "C:\pagefile.sys 0 0" or a bare path = system managed;
/// "C:\pagefile.sys 4096 8192" = custom initial/maximum in MB.
pub fn parse_paging_files(entries: &[String]) -> (PageFileKind, i32) {
    if entries.is_empty() {
        return (PageFileKind::None, 0);
    }
    let mut max_total = 0;
    for entry in entries {
        let parts: Vec<&str> = entry.split_whitespace().collect();
        if parts.first().is_some_and(|p| p.starts_with("?:")) {
            return (PageFileKind::SystemManaged, 0);
        }
        match parts.get(2).and_then(|m| m.parse::<i32>().ok()) {
            Some(max) if max != 0 => max_total += max,
            _ => return (PageFileKind::SystemManaged, 0),
        }
    }
    (PageFileKind::Custom, max_total)
}

#[cfg(windows)]
mod windows;
#[cfg(windows)]
pub use windows::*;

#[cfg(not(windows))]
mod stub;
#[cfg(not(windows))]
pub use stub::*;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exe_and_base_names() {
        assert_eq!(exe_name("chrome"), "chrome.exe");
        assert_eq!(exe_name("Chrome.EXE"), "Chrome.EXE");
        assert_eq!(exe_name("Memory Compression"), "Memory Compression.exe");
        assert_eq!(base_name("chrome.exe"), "chrome");
        assert_eq!(base_name("Registry"), "Registry");
        assert_eq!(base_name("é.exe"), "é");
        assert_eq!(base_name("exe"), "exe");
    }

    #[test]
    fn page_file_kinds() {
        let e = |list: &[&str]| list.iter().map(|s| s.to_string()).collect::<Vec<_>>();
        assert_eq!(parse_paging_files(&e(&[])), (PageFileKind::None, 0));
        assert_eq!(parse_paging_files(&e(&[r"?:\pagefile.sys"])), (PageFileKind::SystemManaged, 0));
        assert_eq!(parse_paging_files(&e(&[r"C:\pagefile.sys"])), (PageFileKind::SystemManaged, 0));
        assert_eq!(parse_paging_files(&e(&[r"C:\pagefile.sys 0 0"])), (PageFileKind::SystemManaged, 0));
        assert_eq!(parse_paging_files(&e(&[r"C:\pagefile.sys 4096 8192", r"D:\pagefile.sys 1024 2048"])), (PageFileKind::Custom, 10240));
    }
}
