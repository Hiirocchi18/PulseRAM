//! Processes: enumeration, working-set trims and the governors (freeze, EcoQoS, hard caps).

use std::ffi::c_void;
use std::mem::{size_of, zeroed};
use std::ptr::null_mut;
use std::sync::Mutex;

use windows_sys::Win32::Foundation::{CloseHandle, FILETIME, HANDLE, HWND, LPARAM};
use windows_sys::Win32::Security::{GetSidSubAuthority, GetSidSubAuthorityCount, GetTokenInformation, TokenIntegrityLevel, TOKEN_MANDATORY_LABEL, TOKEN_QUERY};
use windows_sys::Win32::System::Memory::{
    GetProcessWorkingSetSizeEx, SetProcessWorkingSetSizeEx, QUOTA_LIMITS_HARDWS_MAX_DISABLE, QUOTA_LIMITS_HARDWS_MAX_ENABLE,
    QUOTA_LIMITS_HARDWS_MIN_DISABLE,
};
use windows_sys::Win32::System::ProcessStatus::{K32EmptyWorkingSet, K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
use windows_sys::Win32::System::RemoteDesktop::ProcessIdToSessionId;
use windows_sys::Win32::System::Threading::{
    GetCurrentProcess, GetCurrentProcessId, GetCurrentThread, GetSystemTimes, OpenProcess, OpenProcessToken, ProcessPowerThrottling,
    QueryFullProcessImageNameW, SetPriorityClass, SetProcessInformation, SetThreadInformation, SetThreadPriority, ThreadPowerThrottling,
    BELOW_NORMAL_PRIORITY_CLASS, IDLE_PRIORITY_CLASS, NORMAL_PRIORITY_CLASS, PROCESS_NAME_WIN32, PROCESS_POWER_THROTTLING_CURRENT_VERSION,
    PROCESS_POWER_THROTTLING_EXECUTION_SPEED, PROCESS_POWER_THROTTLING_STATE, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SET_INFORMATION,
    PROCESS_SET_QUOTA, PROCESS_SUSPEND_RESUME, THREAD_POWER_THROTTLING_STATE, THREAD_PRIORITY_HIGHEST, THREAD_PRIORITY_NORMAL,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetForegroundWindow, GetWindow, GetWindowLongPtrW, GetWindowThreadProcessId, IsWindowVisible, PostMessageW, GWL_STYLE, GW_OWNER,
    WM_CLOSE, WS_DISABLED,
};

use super::nt;
use crate::sys::{exe_name, CapOutcome, ProcInfo};

// EmptyWorkingSet / SetProcessWorkingSetSizeEx require QUERY(_LIMITED) + SET_QUOTA.
const TRIM_ACCESS: u32 = PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA;

/// Closes the handle when dropped.
struct Handle(HANDLE);

impl Handle {
    fn open(access: u32, pid: u32) -> Option<Handle> {
        let h = unsafe { OpenProcess(access, 0, pid) };
        (!h.is_null()).then_some(Handle(h))
    }
}

impl Drop for Handle {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

#[repr(C)]
struct UnicodeString {
    length: u16,
    maximum_length: u16,
    buffer: *const u16,
}

/// SYSTEM_PROCESS_INFORMATION (64-bit layout), up to the fields PulseRAM reads.
#[repr(C)]
struct SystemProcessInformation {
    next_entry_offset: u32,
    number_of_threads: u32,
    working_set_private_size: i64,
    hard_fault_count: u32,
    number_of_threads_high_watermark: u32,
    cycle_time: u64,
    create_time: i64,
    user_time: i64,
    kernel_time: i64,
    image_name: UnicodeString,
    base_priority: i32,
    unique_process_id: usize,
    inherited_from_unique_process_id: usize,
    handle_count: u32,
    session_id: u32,
    unique_process_key: usize,
    peak_virtual_size: usize,
    virtual_size: usize,
    page_fault_count: u32,
    peak_working_set_size: usize,
    working_set_size: usize,
    quota_peak_paged_pool_usage: usize,
    quota_paged_pool_usage: usize,
    quota_peak_non_paged_pool_usage: usize,
    quota_non_paged_pool_usage: usize,
    pagefile_usage: usize,
    peak_pagefile_usage: usize,
    private_page_count: usize,
}

const _: () = assert!(size_of::<SystemProcessInformation>() == 208);

const SYSTEM_PROCESS_INFORMATION_CLASS: i32 = 5;
const STATUS_INFO_LENGTH_MISMATCH: i32 = 0xC000_0004_u32 as i32;

/// Reused between scans so a scan doesn't allocate (u64 elements keep the buffer 8-byte aligned).
static SCAN_BUFFER: Mutex<Vec<u64>> = Mutex::new(Vec::new());

/// Every process on the system from one NtQuerySystemInformation call: no per-process handles are opened,
/// so it also covers protected processes.
pub fn list_processes() -> Vec<ProcInfo> {
    let mut buffer = SCAN_BUFFER.lock().unwrap_or_else(|e| e.into_inner());
    if buffer.is_empty() {
        buffer.resize(64 * 1024, 0); // 512 KB
    }
    let mut out = Vec::with_capacity(256);
    unsafe {
        loop {
            let mut needed = 0u32;
            let bytes = (buffer.len() * 8) as u32;
            let status = nt::NtQuerySystemInformation(SYSTEM_PROCESS_INFORMATION_CLASS, buffer.as_mut_ptr() as *mut c_void, bytes, &mut needed);
            if status == STATUS_INFO_LENGTH_MISMATCH && buffer.len() < 32 * 1024 * 1024 {
                let want = (needed.max(bytes) as usize + 128 * 1024) / 8;
                buffer.resize(want, 0);
                continue;
            }
            if status != 0 {
                return out;
            }
            break;
        }

        let base = buffer.as_ptr() as *const u8;
        let limit = buffer.len() * 8;
        let mut offset = 0usize;
        while offset + size_of::<SystemProcessInformation>() <= limit {
            // Read by value: nothing guarantees the entry (or its name) is aligned for these types.
            let entry = std::ptr::read_unaligned(base.add(offset) as *const SystemProcessInformation);
            let pid = entry.unique_process_id as u32;
            let name = if entry.image_name.buffer.is_null() || entry.image_name.length == 0 {
                if pid == 0 { "Idle".to_string() } else { "System".to_string() }
            } else {
                let bytes = std::slice::from_raw_parts(entry.image_name.buffer as *const u8, (entry.image_name.length as usize) & !1);
                let chars: Vec<u16> = bytes.chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect();
                String::from_utf16_lossy(&chars)
            };
            out.push(ProcInfo {
                pid,
                name: exe_name(&name),
                session_id: entry.session_id,
                working_set: entry.working_set_size as u64,
                peak_working_set: entry.peak_working_set_size as u64,
                private_bytes: entry.private_page_count as u64,
                cpu_time: (entry.user_time.max(0) + entry.kernel_time.max(0)) as u64,
                create_time: entry.create_time,
            });
            if entry.next_entry_offset == 0 {
                break;
            }
            offset += entry.next_entry_offset as usize;
        }
    }
    out
}

fn filetime_value(ft: &FILETIME) -> u64 {
    (u64::from(ft.dwHighDateTime) << 32) | u64::from(ft.dwLowDateTime)
}

/// (idle, kernel, user) CPU time across all processors in 100 ns units. Kernel time includes idle time.
pub fn system_times() -> Option<(u64, u64, u64)> {
    unsafe {
        let (mut idle, mut kernel, mut user): (FILETIME, FILETIME, FILETIME) = (zeroed(), zeroed(), zeroed());
        if GetSystemTimes(&mut idle, &mut kernel, &mut user) == 0 {
            return None;
        }
        Some((filetime_value(&idle), filetime_value(&kernel), filetime_value(&user)))
    }
}

/// PID that owns the foreground window (0 if none).
pub fn foreground_pid() -> u32 {
    unsafe {
        let hwnd = GetForegroundWindow();
        if hwnd.is_null() {
            return 0;
        }
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, &mut pid);
        pid
    }
}

/// Full path of a process's EXE, if it can be opened.
pub fn process_image_path(pid: u32) -> Option<String> {
    let h = Handle::open(PROCESS_QUERY_LIMITED_INFORMATION, pid)?;
    let mut buf = vec![0u16; 1024];
    let mut len = buf.len() as u32;
    unsafe {
        if QueryFullProcessImageNameW(h.0, PROCESS_NAME_WIN32, buf.as_mut_ptr(), &mut len) == 0 {
            return None;
        }
    }
    Some(String::from_utf16_lossy(&buf[..len as usize]))
}

/// EXE name ("chrome.exe") of a process by PID.
pub fn process_name(pid: u32) -> Option<String> {
    let path = process_image_path(pid)?;
    path.rsplit(['\\', '/']).next().filter(|n| !n.is_empty()).map(exe_name)
}

pub fn own_session_id() -> u32 {
    let mut session = 0u32;
    unsafe { ProcessIdToSessionId(GetCurrentProcessId(), &mut session) };
    session
}

/// Mandatory integrity level RID of a process (0x2000 medium, 0x3000 high, 0x4000 system), or None.
pub fn integrity_level(pid: u32) -> Option<u32> {
    let process = Handle::open(PROCESS_QUERY_LIMITED_INFORMATION, pid)?;
    unsafe {
        let mut token: HANDLE = null_mut();
        if OpenProcessToken(process.0, TOKEN_QUERY, &mut token) == 0 {
            return None;
        }
        let token = Handle(token);
        let mut len = 0u32;
        GetTokenInformation(token.0, TokenIntegrityLevel, null_mut(), 0, &mut len);
        if len == 0 {
            return None;
        }
        let mut buf = vec![0u64; (len as usize + 7) / 8];
        if GetTokenInformation(token.0, TokenIntegrityLevel, buf.as_mut_ptr() as *mut c_void, len, &mut len) == 0 {
            return None;
        }
        let label = &*(buf.as_ptr() as *const TOKEN_MANDATORY_LABEL);
        let sid = label.Label.Sid;
        let count = *GetSidSubAuthorityCount(sid);
        if count == 0 {
            return None;
        }
        Some(*GetSidSubAuthority(sid, u32::from(count) - 1))
    }
}

fn working_set(h: HANDLE) -> Option<u64> {
    unsafe {
        let mut counters: PROCESS_MEMORY_COUNTERS = zeroed();
        counters.cb = size_of::<PROCESS_MEMORY_COUNTERS>() as u32;
        (K32GetProcessMemoryInfo(h, &mut counters, counters.cb) != 0).then_some(counters.WorkingSetSize as u64)
    }
}

fn trimmed_since(h: HANDLE, before: Option<u64>) -> u64 {
    match (before, working_set(h)) {
        (Some(before), Some(after)) => before.saturating_sub(after),
        _ => 0,
    }
}

/// Pages a process's working set out. Returns the bytes trimmed (measured), or None if the process could
/// not be opened or refused. `shrink_fallback` also tries SetProcessWorkingSetSize(-1, -1).
pub fn trim_process(pid: u32, shrink_fallback: bool) -> Option<u64> {
    let h = Handle::open(TRIM_ACCESS, pid)?;
    let before = working_set(h.0);
    let ok = unsafe { K32EmptyWorkingSet(h.0) != 0 || (shrink_fallback && SetProcessWorkingSetSizeEx(h.0, usize::MAX, usize::MAX, 0) != 0) };
    ok.then(|| trimmed_since(h.0, before))
}

/// Suspends every thread of a process and trims it. Some(true) = suspended and trimmed, Some(false) =
/// suspended only, None = could not suspend.
pub fn suspend_process(pid: u32) -> Option<bool> {
    // QUERY_LIMITED is required for EmptyWorkingSet; without it the trim silently fails.
    let h = Handle::open(PROCESS_SUSPEND_RESUME | TRIM_ACCESS, pid)?;
    unsafe {
        if nt::NtSuspendProcess(h.0) != 0 {
            return None;
        }
        Some(K32EmptyWorkingSet(h.0) != 0)
    }
}

pub fn resume_process(pid: u32) -> bool {
    match Handle::open(PROCESS_SUSPEND_RESUME, pid) {
        Some(h) => unsafe { nt::NtResumeProcess(h.0) == 0 },
        None => false,
    }
}

fn set_power_throttling(process: HANDLE, control_mask: u32, state_mask: u32) -> bool {
    let state = PROCESS_POWER_THROTTLING_STATE { Version: PROCESS_POWER_THROTTLING_CURRENT_VERSION, ControlMask: control_mask, StateMask: state_mask };
    unsafe {
        SetProcessInformation(process, ProcessPowerThrottling, &state as *const _ as *const c_void, size_of::<PROCESS_POWER_THROTTLING_STATE>() as u32) != 0
    }
}

/// Efficiency Mode for another process: idle priority + EcoQoS (and a trim when switching it on).
pub fn set_eco(pid: u32, enable: bool) -> bool {
    let Some(h) = Handle::open(PROCESS_SET_INFORMATION | TRIM_ACCESS, pid) else { return false };
    unsafe {
        let priority_ok = SetPriorityClass(h.0, if enable { IDLE_PRIORITY_CLASS } else { NORMAL_PRIORITY_CLASS }) != 0;
        let qos_ok = set_power_throttling(h.0, PROCESS_POWER_THROTTLING_EXECUTION_SPEED, if enable { PROCESS_POWER_THROTTLING_EXECUTION_SPEED } else { 0 });
        if enable {
            K32EmptyWorkingSet(h.0);
        }
        priority_ok || qos_ok
    }
}

/// Runs PulseRAM itself in Efficiency Mode (EcoQoS + below-normal priority) or back to normal.
pub fn set_self_efficiency(on: bool) -> bool {
    unsafe {
        let process = GetCurrentProcess();
        // ControlMask 0 = let Windows decide again.
        let qos = set_power_throttling(process, u32::from(on), u32::from(on));
        let priority = SetPriorityClass(process, if on { BELOW_NORMAL_PRIORITY_CLASS } else { NORMAL_PRIORITY_CLASS }) != 0;
        qos || priority
    }
}

/// Applies a HARD working-set maximum to one process. With `force` false, a process that already carries
/// exactly this cap is left alone.
pub fn apply_hard_cap(pid: u32, cap_bytes: u64, force: bool) -> CapOutcome {
    let Some(h) = Handle::open(TRIM_ACCESS, pid) else { return CapOutcome::Refused };
    unsafe {
        if !force {
            let (mut min, mut max, mut flags) = (0usize, 0usize, 0u32);
            let needs = GetProcessWorkingSetSizeEx(h.0, &mut min, &mut max, &mut flags) == 0
                || flags & QUOTA_LIMITS_HARDWS_MAX_ENABLE == 0
                || max as u64 != cap_bytes;
            if !needs {
                return CapOutcome::AlreadySet;
            }
        }
        let before = working_set(h.0);
        if SetProcessWorkingSetSizeEx(h.0, 1024 * 1024, cap_bytes as usize, QUOTA_LIMITS_HARDWS_MAX_ENABLE | QUOTA_LIMITS_HARDWS_MIN_DISABLE) != 0 {
            CapOutcome::Applied(trimmed_since(h.0, before))
        } else {
            CapOutcome::Refused
        }
    }
}

/// Turns the hard working-set maximum back into the normal soft limit.
pub fn release_hard_cap(pid: u32) {
    let Some(h) = Handle::open(TRIM_ACCESS, pid) else { return };
    unsafe {
        let (mut min, mut max, mut flags) = (0usize, 0usize, 0u32);
        if GetProcessWorkingSetSizeEx(h.0, &mut min, &mut max, &mut flags) != 0 {
            SetProcessWorkingSetSizeEx(h.0, min, max, QUOTA_LIMITS_HARDWS_MAX_DISABLE | QUOTA_LIMITS_HARDWS_MIN_DISABLE);
        }
    }
}

/// While PulseRAM sits in Efficiency Mode, a sweep must still run at full speed — it is needed exactly when
/// the PC is under load. Opts the current thread out of EcoQoS and raises it to the normal-class level for
/// the duration of the sweep, then restores it.
pub struct SweepBoost;

fn set_thread_qos(control_mask: u32, priority: i32) {
    let state = THREAD_POWER_THROTTLING_STATE { Version: 1, ControlMask: control_mask, StateMask: 0 };
    unsafe {
        let thread = GetCurrentThread();
        SetThreadInformation(thread, ThreadPowerThrottling, &state as *const _ as *const c_void, size_of::<THREAD_POWER_THROTTLING_STATE>() as u32);
        SetThreadPriority(thread, priority);
    }
}

impl SweepBoost {
    pub fn begin() -> SweepBoost {
        set_thread_qos(1, THREAD_PRIORITY_HIGHEST); // high QoS; below-normal class + highest ≈ normal-class normal
        SweepBoost
    }
}

impl Drop for SweepBoost {
    fn drop(&mut self) {
        set_thread_qos(0, THREAD_PRIORITY_NORMAL); // back to default
    }
}

struct CloseRequest<'a> {
    pids: &'a [u32],
    asked: Vec<u32>,
}

unsafe extern "system" fn close_window_proc(hwnd: HWND, lparam: LPARAM) -> i32 {
    let request = &mut *(lparam as *mut CloseRequest);
    let mut pid = 0u32;
    GetWindowThreadProcessId(hwnd, &mut pid);
    // A process's main window: visible, top-level, not owned by another window. One per process.
    if request.pids.contains(&pid) && !request.asked.contains(&pid) && IsWindowVisible(hwnd) != 0 && GetWindow(hwnd, GW_OWNER).is_null() {
        let disabled = GetWindowLongPtrW(hwnd, GWL_STYLE) as u32 & WS_DISABLED != 0; // a modal dialog is open
        if !disabled && PostMessageW(hwnd, WM_CLOSE, 0, 0) != 0 {
            request.asked.push(pid);
        }
    }
    1
}

/// Asks the main window of each given process to close, the way clicking its X does (the app can save or
/// prompt). Returns how many processes were asked.
pub fn close_main_windows(pids: &[u32]) -> usize {
    let mut request = CloseRequest { pids, asked: Vec::new() };
    unsafe { EnumWindows(Some(close_window_proc), &mut request as *mut _ as LPARAM) };
    request.asked.len()
}
