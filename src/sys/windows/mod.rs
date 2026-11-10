//! Windows implementation of the platform layer (Win32 + a handful of NT calls).

mod file;
mod mem;
mod misc;
mod proc;
mod sec;

pub use file::{delete_file, write_text};
pub use mem::*;
pub use misc::*;
pub use proc::*;
pub use sec::{enable_privileges, enable_redirection_guard, is_admin_only_writable, is_elevated};

use std::os::windows::ffi::OsStrExt;
use std::path::Path;

/// NT system calls that have no Win32 wrapper.
mod nt {
    use std::ffi::c_void;

    #[link(name = "ntdll")]
    extern "system" {
        pub fn NtQuerySystemInformation(class: i32, info: *mut c_void, length: u32, return_length: *mut u32) -> i32;
        pub fn NtSetSystemInformation(class: i32, info: *mut c_void, length: u32) -> i32;
        pub fn NtSuspendProcess(process: *mut c_void) -> i32;
        pub fn NtResumeProcess(process: *mut c_void) -> i32;
    }
}

/// NUL-terminated UTF-16 for Win32 "W" functions.
pub(crate) fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

pub(crate) fn wide_path(path: &Path) -> Vec<u16> {
    path.as_os_str().encode_wide().chain(std::iter::once(0)).collect()
}

/// Reads a NUL-terminated UTF-16 string.
pub(crate) unsafe fn from_wide_ptr(ptr: *const u16) -> String {
    if ptr.is_null() {
        return String::new();
    }
    let mut len = 0;
    while *ptr.add(len) != 0 {
        len += 1;
    }
    String::from_utf16_lossy(std::slice::from_raw_parts(ptr, len))
}

/// A shell known folder (Program Files, LocalAppData …) straight from Windows, not from environment variables.
pub(crate) fn known_folder(id: &windows_sys::core::GUID) -> Option<String> {
    use windows_sys::Win32::System::Com::CoTaskMemFree;
    use windows_sys::Win32::UI::Shell::SHGetKnownFolderPath;
    unsafe {
        let mut path: *mut u16 = std::ptr::null_mut();
        let hr = SHGetKnownFolderPath(id, 0, std::ptr::null_mut(), &mut path);
        let result = (hr >= 0 && !path.is_null()).then(|| from_wide_ptr(path));
        CoTaskMemFree(path as *const std::ffi::c_void);
        result.filter(|p| !p.is_empty())
    }
}
