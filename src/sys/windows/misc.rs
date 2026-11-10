//! Registry, Task Scheduler, shell launches, clock, theme and the update check's HTTPS request.

use std::ffi::c_void;
use std::mem::{size_of, zeroed};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::ptr::{null, null_mut};

use windows_sys::Win32::Foundation::SYSTEMTIME;
use windows_sys::Win32::Networking::WinHttp::{
    WinHttpCloseHandle, WinHttpConnect, WinHttpOpen, WinHttpOpenRequest, WinHttpQueryHeaders, WinHttpReadData, WinHttpReceiveResponse, WinHttpSendRequest,
    WinHttpSetTimeouts, INTERNET_DEFAULT_HTTPS_PORT, WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_FLAG_SECURE, WINHTTP_QUERY_FLAG_NUMBER,
    WINHTTP_QUERY_STATUS_CODE,
};
use windows_sys::Win32::Security::Authentication::Identity::{GetUserNameExW, NameSamCompatible};
use windows_sys::Win32::System::Diagnostics::Debug::MessageBeep;
use windows_sys::Win32::System::Registry::{
    RegCloseKey, RegDeleteValueW, RegGetValueW, RegOpenKeyExW, RegSetValueExW, HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_SET_VALUE, REG_SZ,
    RRF_RT_ANY, RRF_RT_REG_DWORD, RRF_RT_REG_MULTI_SZ,
};
use windows_sys::Win32::System::SystemInformation::{GetLocalTime, GetSystemDirectoryW, GetSystemTime, GetSystemWindowsDirectoryW};
use windows_sys::Win32::System::Threading::CREATE_NO_WINDOW;
use windows_sys::Win32::UI::Shell::{FOLDERID_LocalAppData, ShellExecuteW};
use windows_sys::Win32::UI::WindowsAndMessaging::{GetShellWindow, MB_ICONASTERISK, SW_SHOWNORMAL};

use super::sec::is_elevated;
use super::{known_folder, wide, wide_path};
use crate::clock::{unix_from_civil, Civil};
use crate::sys::{HttpResult, PageFileKind};

fn civil(st: &SYSTEMTIME) -> Civil {
    Civil {
        year: i32::from(st.wYear),
        month: u32::from(st.wMonth),
        day: u32::from(st.wDay),
        hour: u32::from(st.wHour),
        minute: u32::from(st.wMinute),
        second: u32::from(st.wSecond),
        millis: u32::from(st.wMilliseconds),
    }
}

/// Local time zone offset from UTC in seconds, including daylight saving.
pub fn utc_offset_secs() -> i64 {
    unsafe {
        let (mut local, mut utc): (SYSTEMTIME, SYSTEMTIME) = (zeroed(), zeroed());
        GetSystemTime(&mut utc);
        GetLocalTime(&mut local);
        let diff = unix_from_civil(&civil(&local)) - unix_from_civil(&civil(&utc));
        // Both clocks are read a few microseconds apart; time zones are whole minutes.
        (diff as f64 / 60.0).round() as i64 * 60
    }
}

pub fn logical_cores() -> usize {
    std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1).max(1)
}

pub fn message_beep() {
    unsafe { MessageBeep(MB_ICONASTERISK) };
}

fn system_dir_from(get: unsafe extern "system" fn(*mut u16, u32) -> u32, fallback: &str) -> PathBuf {
    let mut buf = [0u16; 512];
    let n = unsafe { get(buf.as_mut_ptr(), buf.len() as u32) } as usize;
    if n == 0 || n >= buf.len() {
        return PathBuf::from(fallback);
    }
    PathBuf::from(String::from_utf16_lossy(&buf[..n]))
}

/// `C:\Windows\System32`, asked from Windows rather than read from an environment variable.
pub fn system_directory() -> PathBuf {
    system_dir_from(GetSystemDirectoryW, r"C:\Windows\System32")
}

pub fn windows_directory() -> PathBuf {
    system_dir_from(GetSystemWindowsDirectoryW, r"C:\Windows")
}

pub fn local_app_data() -> PathBuf {
    known_folder(&FOLDERID_LocalAppData)
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("LOCALAPPDATA").map(PathBuf::from))
        .unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"))
}

// ---- Registry ----

unsafe fn reg_get(root: HKEY, subkey: &str, value: &str, flags: u32) -> Option<Vec<u8>> {
    let (subkey, value) = (wide(subkey), wide(value));
    let mut size = 0u32;
    if RegGetValueW(root, subkey.as_ptr(), value.as_ptr(), flags, null_mut(), null_mut(), &mut size) != 0 {
        return None;
    }
    let mut buf = vec![0u8; size as usize + 2];
    let mut size = buf.len() as u32;
    if RegGetValueW(root, subkey.as_ptr(), value.as_ptr(), flags, null_mut(), buf.as_mut_ptr() as *mut c_void, &mut size) != 0 {
        return None;
    }
    buf.truncate(size as usize);
    Some(buf)
}

fn reg_dword(root: HKEY, subkey: &str, value: &str) -> Option<u32> {
    let bytes = unsafe { reg_get(root, subkey, value, RRF_RT_REG_DWORD) }?;
    Some(u32::from_le_bytes(bytes.get(..4)?.try_into().ok()?))
}

const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
const RUN_VALUE: &str = "PulseRAM";

/// True if the per-user "start with Windows" Run-key entry exists.
pub fn run_key_exists() -> bool {
    unsafe { reg_get(HKEY_CURRENT_USER, RUN_KEY, RUN_VALUE, RRF_RT_ANY).is_some() }
}

/// Adds or removes the Run-key entry (`"<exe>" --tray`).
pub fn set_run_key(enable: bool, exe_path: &Path) -> Result<(), String> {
    unsafe {
        let mut key: HKEY = null_mut();
        if RegOpenKeyExW(HKEY_CURRENT_USER, wide(RUN_KEY).as_ptr(), 0, KEY_SET_VALUE, &mut key) != 0 {
            return Ok(()); // no Run key: nothing to change (as 1.x)
        }
        let name = wide(RUN_VALUE);
        let status = if enable {
            let data = wide(&format!("\"{}\" --tray", exe_path.display()));
            RegSetValueExW(key, name.as_ptr(), 0, REG_SZ, data.as_ptr() as *const u8, (data.len() * 2) as u32)
        } else {
            match RegDeleteValueW(key, name.as_ptr()) {
                2 => 0, // ERROR_FILE_NOT_FOUND: already gone
                other => other,
            }
        };
        RegCloseKey(key);
        if status == 0 {
            Ok(())
        } else {
            Err(format!("registry error {status}"))
        }
    }
}

/// Reads the page-file configuration (readable without admin rights): its kind and, for a custom size, the
/// total maximum in MB.
pub fn page_file_setup() -> (PageFileKind, i32) {
    let Some(bytes) =
        (unsafe { reg_get(HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles", RRF_RT_REG_MULTI_SZ) })
    else {
        return (PageFileKind::Unknown, 0);
    };
    let units: Vec<u16> = bytes.chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect();
    let entries: Vec<String> = units.split(|&u| u == 0).map(String::from_utf16_lossy).filter(|e| !e.trim().is_empty()).collect();
    crate::sys::parse_paging_files(&entries)
}

/// Windows' "app mode" setting: true when apps should use the light theme.
pub fn system_uses_light_theme() -> bool {
    reg_dword(HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme").is_some_and(|v| v != 0)
}

// ---- Task Scheduler, shell ----

/// Runs schtasks.exe hidden and returns (exit code, stdout). -1 if it could not be run or timed out.
pub fn run_schtasks(args: &[&str]) -> (i32, String) {
    let mut child = match Command::new(system_directory().join("schtasks.exe"))
        .args(args)
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
    {
        Ok(child) => child,
        Err(_) => return (-1, String::new()),
    };
    // Read on another thread so a stuck schtasks can be abandoned after 15 s.
    let stdout = child.stdout.take();
    let reader = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        if let Some(mut out) = stdout {
            let _ = std::io::Read::read_to_end(&mut out, &mut bytes);
        }
        bytes
    });
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(15);
    let code = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status.code().unwrap_or(-1),
            Ok(None) if std::time::Instant::now() < deadline => std::thread::sleep(std::time::Duration::from_millis(40)),
            _ => {
                let _ = child.kill();
                let _ = child.wait();
                break -1;
            }
        }
    };
    let bytes = reader.join().unwrap_or_default();
    (code, decode_console_text(&bytes))
}

/// `schtasks /XML` prints UTF-16 when redirected on some Windows builds and the OEM code page on others.
fn decode_console_text(bytes: &[u8]) -> String {
    let looks_utf16 = bytes.starts_with(&[0xFF, 0xFE]) || (bytes.len() >= 4 && bytes[1] == 0 && bytes[3] == 0);
    if looks_utf16 {
        let units: Vec<u16> = bytes.chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect();
        String::from_utf16_lossy(&units).trim_start_matches('\u{feff}').to_string()
    } else {
        String::from_utf8_lossy(bytes).into_owned()
    }
}

/// "DOMAIN\user" of the account PulseRAM runs as.
pub fn current_user_name() -> Option<String> {
    let mut buf = [0u16; 512];
    let mut len = buf.len() as u32;
    unsafe {
        if !GetUserNameExW(NameSamCompatible, buf.as_mut_ptr(), &mut len) {
            return None;
        }
    }
    Some(String::from_utf16_lossy(&buf[..len as usize])).filter(|n| !n.is_empty())
}

/// True when the Explorer shell (desktop + taskbar) is running.
pub fn shell_window_exists() -> bool {
    unsafe { !GetShellWindow().is_null() }
}

fn shell_open(target: &str) -> bool {
    unsafe { ShellExecuteW(null_mut(), wide("open").as_ptr(), wide(target).as_ptr(), null(), null(), SW_SHOWNORMAL) as isize > 32 }
}

fn explorer_open(arg: &str) -> bool {
    Command::new(windows_directory().join("explorer.exe")).arg(arg).spawn().is_ok()
}

fn file_uri(path: &Path) -> String {
    let full = std::path::absolute(path).unwrap_or_else(|_| path.to_path_buf());
    let mut uri = String::from("file:///");
    for byte in full.to_string_lossy().replace('\\', "/").bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' | b'/' | b':' => uri.push(byte as char),
            _ => uri.push_str(&format!("%{byte:02X}")),
        }
    }
    uri
}

/// Opens a web page or folder. When PulseRAM is elevated it asks the (non-elevated) Explorer shell to open
/// it, so the browser or folder window doesn't inherit administrator rights; without a shell it does nothing.
pub fn open_as_user(target: &str) -> bool {
    if !is_elevated() {
        return shell_open(target);
    }
    if !shell_window_exists() {
        return false;
    }
    // Explorer splits its command line at commas, so pass folders as a file:// URI (commas escaped).
    let arg = if target.to_ascii_lowercase().starts_with("https://") { target.to_string() } else { file_uri(Path::new(target)) };
    explorer_open(&arg)
}

/// Starts an app the way the Start menu would. Elevated, the running (non-elevated) Explorer shell is asked
/// to start it so the app doesn't inherit PulseRAM's administrator rights.
pub fn launch_as_user(exe: &Path) -> bool {
    if is_elevated() {
        explorer_open(&exe.to_string_lossy())
    } else {
        shell_open(&exe.to_string_lossy())
    }
}

/// Opens Performance Options (Advanced → Virtual memory).
pub fn open_page_file_settings() -> bool {
    shell_open(&system_directory().join("SystemPropertiesPerformance.exe").to_string_lossy())
}

/// Restarts PulseRAM elevated (UAC prompt). False if the user declined.
pub fn relaunch_elevated(exe: &Path, args: &str) -> bool {
    unsafe { ShellExecuteW(null_mut(), wide("runas").as_ptr(), wide_path(exe).as_ptr(), wide(args).as_ptr(), null(), SW_SHOWNORMAL) as isize > 32 }
}

// ---- HTTPS (WinHTTP: the system's own TLS stack, nothing bundled) ----

struct Internet(*mut c_void);

impl Drop for Internet {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe { WinHttpCloseHandle(self.0) };
        }
    }
}

/// A plain HTTPS GET with a 15-second timeout. Sends nothing but the request line, User-Agent and Accept.
pub fn https_get(host: &str, path: &str, user_agent: &str, accept: &str) -> HttpResult {
    const TIMEOUT_MS: i32 = 15_000;
    const MAX_BODY: usize = 2 * 1024 * 1024;
    unsafe {
        let session = Internet(WinHttpOpen(wide(user_agent).as_ptr(), WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, null(), null(), 0));
        if session.0.is_null() {
            return HttpResult::Failed("WinHttpOpen".into());
        }
        WinHttpSetTimeouts(session.0, TIMEOUT_MS, TIMEOUT_MS, TIMEOUT_MS, TIMEOUT_MS);
        let connection = Internet(WinHttpConnect(session.0, wide(host).as_ptr(), INTERNET_DEFAULT_HTTPS_PORT, 0));
        if connection.0.is_null() {
            return HttpResult::Failed("WinHttpConnect".into());
        }
        let request = Internet(WinHttpOpenRequest(connection.0, wide("GET").as_ptr(), wide(path).as_ptr(), null(), null(), null(), WINHTTP_FLAG_SECURE));
        if request.0.is_null() {
            return HttpResult::Failed("WinHttpOpenRequest".into());
        }
        let headers = wide(&format!("Accept: {accept}\r\n"));
        if WinHttpSendRequest(request.0, headers.as_ptr(), (headers.len() - 1) as u32, null(), 0, 0, 0) == 0 {
            return HttpResult::Failed("send".into());
        }
        if WinHttpReceiveResponse(request.0, null_mut()) == 0 {
            return HttpResult::Failed("receive".into());
        }
        let mut status = 0u32;
        let mut size = size_of::<u32>() as u32;
        if WinHttpQueryHeaders(request.0, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, null(), &mut status as *mut _ as *mut c_void, &mut size, null_mut()) == 0 {
            return HttpResult::Failed("status".into());
        }
        let mut body = Vec::new();
        let mut chunk = [0u8; 8192];
        loop {
            let mut read = 0u32;
            if WinHttpReadData(request.0, chunk.as_mut_ptr() as *mut c_void, chunk.len() as u32, &mut read) == 0 {
                return HttpResult::Failed("read".into());
            }
            if read == 0 {
                break;
            }
            body.extend_from_slice(&chunk[..read as usize]);
            if body.len() > MAX_BODY {
                return HttpResult::Failed("response too large".into());
            }
        }
        HttpResult::Response(status, String::from_utf8_lossy(&body).into_owned())
    }
}
