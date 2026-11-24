//! File writes and deletes in PulseRAM's data folder. That folder can be changed by any program the user
//! runs, so when PulseRAM is elevated a planted junction, symlink or hard link could otherwise redirect an
//! admin write or delete to a system file. Elevated, every operation opens the file itself (never following a
//! final link), then checks on the open handle that it is a plain, single-link file still inside the expected
//! folder. Checking the handle (not the path) leaves no gap to swap things in between.
//! When not elevated it simply uses the normal file APIs.

use std::ffi::c_void;
use std::io::Write;
use std::mem::zeroed;
use std::path::{Path, PathBuf};
use std::ptr::{null, null_mut};
use std::sync::Mutex;

use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, ERROR_ALREADY_EXISTS, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FileDispositionInfo, FileRenameInfo, FlushFileBuffers, GetFileInformationByHandle, GetFinalPathNameByHandleW, SetFileInformationByHandle,
    SetFilePointerEx, WriteFile, BY_HANDLE_FILE_INFORMATION, CREATE_NEW, DELETE, FILE_APPEND_DATA, FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_REPARSE_POINT,
    FILE_END, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_READ_ATTRIBUTES, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE,
    OPEN_ALWAYS, OPEN_EXISTING, READ_CONTROL, SYNCHRONIZE,
};

use super::sec::{is_elevated, is_trusted_link_owner, owner_of_handle, path_attributes};
use super::wide_path;

struct FileHandle(HANDLE);

impl FileHandle {
    fn open(path: &Path, access: u32, share: u32, disposition: u32, flags: u32) -> Option<FileHandle> {
        let h = unsafe { CreateFileW(wide_path(path).as_ptr(), access, share, null(), disposition, flags, null_mut()) };
        (h != INVALID_HANDLE_VALUE && !h.is_null()).then_some(FileHandle(h))
    }

    fn info(&self) -> Option<BY_HANDLE_FILE_INFORMATION> {
        unsafe {
            let mut info: BY_HANDLE_FILE_INFORMATION = zeroed();
            (GetFileInformationByHandle(self.0, &mut info) != 0).then_some(info)
        }
    }

    /// Where the handle really points, without the `\\?\` prefix.
    fn final_path(&self) -> Option<String> {
        let mut buf = vec![0u16; 1024];
        let n = unsafe { GetFinalPathNameByHandleW(self.0, buf.as_mut_ptr(), buf.len() as u32, 0) } as usize;
        if n == 0 || n >= buf.len() {
            return None;
        }
        let real = String::from_utf16_lossy(&buf[..n]);
        Some(if let Some(unc) = real.strip_prefix(r"\\?\UNC\") {
            format!(r"\\{unc}")
        } else if let Some(plain) = real.strip_prefix(r"\\?\") {
            plain.to_string()
        } else {
            real
        })
    }

    fn write_all(&self, bytes: &[u8]) -> bool {
        let mut done = 0usize;
        while done < bytes.len() {
            let mut written = 0u32;
            let chunk = (bytes.len() - done).min(1 << 20) as u32;
            if unsafe { WriteFile(self.0, bytes[done..].as_ptr(), chunk, &mut written, null_mut()) } == 0 || written == 0 {
                return false;
            }
            done += written as usize;
        }
        unsafe { FlushFileBuffers(self.0) != 0 }
    }

    fn delete(&self) -> bool {
        let delete_file: u8 = 1; // FILE_DISPOSITION_INFO { DeleteFile: TRUE }
        unsafe { SetFileInformationByHandle(self.0, FileDispositionInfo, &delete_file as *const _ as *const c_void, 1) != 0 }
    }

    /// FILE_RENAME_INFO (64-bit layout): ReplaceIfExists @0, RootDirectory @8, FileNameLength @16, FileName @20.
    fn rename_to(&self, target: &Path) -> bool {
        let name: Vec<u16> = wide_path(target);
        let name = &name[..name.len() - 1]; // without the terminator
        let name_bytes = name.len() * 2;
        let size = 24 + name_bytes;
        let mut buf = vec![0u64; size.div_ceil(8)]; // u64 elements keep the buffer aligned for the struct
        unsafe {
            let p = buf.as_mut_ptr() as *mut u8;
            *(p as *mut u32) = 1; // ReplaceIfExists
            *(p.add(16) as *mut u32) = name_bytes as u32;
            std::ptr::copy_nonoverlapping(name.as_ptr() as *const u8, p.add(20), name_bytes);
            SetFileInformationByHandle(self.0, FileRenameInfo, p as *const c_void, size as u32) != 0
        }
    }
}

impl Drop for FileHandle {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

fn full(path: &Path) -> PathBuf {
    std::path::absolute(path).unwrap_or_else(|_| path.to_path_buf())
}

fn trimmed(path: &Path) -> String {
    path.to_string_lossy().trim_end_matches('\\').to_string()
}

// ---- Where the data folder really is ----
// Moving C:\Users (or one profile) to another drive with an admin-made junction is legitimate. So the real
// location of the data folder's parent is resolved once, accepting links only when they are owned by
// SYSTEM / Administrators / TrustedInstaller (the same rule as RedirectionGuard). The data folder itself and
// everything inside it must not be links.
static REAL_DATA_DIR: Mutex<Option<(String, Option<String>)>> = Mutex::new(None);

fn real_data_directory() -> Option<String> {
    let data_dir = trimmed(&full(&crate::paths::data_dir()));
    let mut cache = REAL_DATA_DIR.lock().unwrap_or_else(|e| e.into_inner());
    if let Some((key, real)) = cache.as_ref() {
        if *key == data_dir {
            return real.clone();
        }
    }
    let real = resolve_trusted(Path::new(&data_dir));
    *cache = Some((data_dir, real.clone()));
    real
}

fn resolve_trusted(data_dir: &Path) -> Option<String> {
    let parent = data_dir.parent()?;
    let mut current = Some(parent);
    while let Some(dir) = current {
        if let Some(attrs) = path_attributes(dir) {
            if attrs & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
                // The link itself (not what it points to) must belong to an administrator.
                let link = FileHandle::open(
                    dir,
                    READ_CONTROL,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                )?;
                let owner = unsafe { owner_of_handle(link.0) }?;
                if !is_trusted_link_owner(&owner) {
                    return None;
                }
            }
        }
        current = dir.parent();
    }
    let h = FileHandle::open(
        parent,
        FILE_READ_ATTRIBUTES | SYNCHRONIZE,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS,
    )?;
    let real = h.final_path()?;
    Some(format!("{}\\{}", real.trim_end_matches('\\'), data_dir.file_name()?.to_string_lossy()))
}

/// Maps a path inside the data folder to where it really is (None if it's outside or unresolvable).
fn expected_real_path(full_path: &Path) -> Option<String> {
    let data_dir = trimmed(&full(&crate::paths::data_dir()));
    let real_dir = real_data_directory()?;
    let p = trimmed(full_path);
    if p.eq_ignore_ascii_case(&data_dir) {
        return Some(real_dir);
    }
    let inside = p.len() > data_dir.len() && p.as_bytes()[data_dir.len()] == b'\\' && p.is_char_boundary(data_dir.len()) && p[..data_dir.len()].eq_ignore_ascii_case(&data_dir);
    inside.then(|| format!("{real_dir}{}", &p[data_dir.len()..]))
}

/// The handle's real location must be exactly where this path should be inside the data folder.
fn is_at(h: &FileHandle, full_path: &Path) -> bool {
    match (expected_real_path(full_path), h.final_path()) {
        (Some(expected), Some(real)) => real.trim_end_matches('\\').to_lowercase() == expected.trim_end_matches('\\').to_lowercase(),
        _ => false,
    }
}

fn folder_is_real(dir: &Path) -> bool {
    let Some(h) = FileHandle::open(
        dir,
        FILE_READ_ATTRIBUTES | SYNCHRONIZE,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
    ) else {
        return false;
    };
    match h.info() {
        Some(info) if info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT == 0 => is_at(&h, dir),
        _ => false,
    }
}

fn is_plain_file_at(h: &FileHandle, full_path: &Path) -> bool {
    match h.info() {
        // Not a symlink, and not a hard link to some other file.
        Some(info) if info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT == 0 && info.nNumberOfLinks <= 1 => is_at(h, full_path),
        _ => false,
    }
}

fn unique_suffix() -> String {
    use std::sync::atomic::{AtomicU32, Ordering};
    static COUNTER: AtomicU32 = AtomicU32::new(0);
    let nanos = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0);
    format!("{:x}{:x}{:x}", std::process::id(), nanos, COUNTER.fetch_add(1, Ordering::Relaxed))
}

/// Writes (or appends) text. Elevated, a whole-file write goes to a new temporary sibling first and is then
/// renamed over the target through the verified handle, so a crash never leaves a half-written file.
/// Returns false (and leaves nothing behind) if the target isn't safe or the write fails.
pub fn write_text(path: &Path, text: &str, append: bool) -> bool {
    if !is_elevated() {
        return if append {
            std::fs::OpenOptions::new().create(true).append(true).open(path).and_then(|mut f| f.write_all(text.as_bytes())).is_ok()
        } else {
            std::fs::write(path, text).is_ok()
        };
    }

    let full_path = full(path);
    let Some(dir) = full_path.parent() else { return false };
    // Check the folder first so a new file is never created through a redirected folder.
    if !folder_is_real(dir) {
        return false;
    }

    if append {
        let h = unsafe {
            CreateFileW(
                wide_path(&full_path).as_ptr(),
                FILE_APPEND_DATA | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                FILE_SHARE_READ,
                null(),
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT,
                null_mut(),
            )
        };
        let existed = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
        if h == INVALID_HANDLE_VALUE || h.is_null() {
            return false;
        }
        let h = FileHandle(h);
        if !is_plain_file_at(&h, &full_path) {
            if !existed {
                h.delete(); // don't leave a file we created somewhere unexpected
            }
            return false;
        }
        unsafe { SetFilePointerEx(h.0, 0, null_mut(), FILE_END) };
        return h.write_all(text.as_bytes());
    }

    let file_name = full_path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let tmp = dir.join(format!(".{file_name}.{}.tmp", unique_suffix()));
    let Some(th) = FileHandle::open(&tmp, GENERIC_WRITE | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, 0, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT)
    else {
        return false;
    };
    if !is_plain_file_at(&th, &tmp) || !th.write_all(text.as_bytes()) || !th.rename_to(&full_path) {
        th.delete();
        return false;
    }
    true
}

/// Deletes a file (a link is deleted itself, never its target). Missing files count as deleted.
pub fn delete_file(path: &Path) -> bool {
    if !is_elevated() {
        return !path.exists() || std::fs::remove_file(path).is_ok();
    }
    if path_attributes(path).is_none() {
        return true;
    }
    let full_path = full(path);
    let Some(h) = FileHandle::open(&full_path, DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT) else {
        return false;
    };
    // Only the location matters here: deleting a link (opened as the link itself) is harmless.
    is_at(&h, &full_path) && h.delete()
}
