//! Guards for running elevated. PulseRAM's data folder and (for per-user or portable copies) its EXE live
//! where any program the user runs can write, so an elevated PulseRAM must not (1) let a logon task start an
//! EXE from such a folder, (2) follow links planted in its data folder, or (3) hand its admin rights to a
//! browser or Explorer.

use std::ffi::c_void;
use std::mem::{size_of, zeroed};
use std::path::Path;
use std::ptr::{null, null_mut};
use std::sync::OnceLock;

use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, LocalFree, ERROR_NOT_ALL_ASSIGNED, HANDLE, LUID};
use windows_sys::Win32::Security::Authorization::{ConvertSidToStringSidW, GetNamedSecurityInfoW, GetSecurityInfo, SE_FILE_OBJECT};
use windows_sys::Win32::Security::{
    AclSizeInformation, AdjustTokenPrivileges, CheckTokenMembership, CreateWellKnownSid, GetAce, GetAclInformation, LookupPrivilegeValueW,
    WinBuiltinAdministratorsSid, ACE_HEADER, ACL, ACL_SIZE_INFORMATION, DACL_SECURITY_INFORMATION, LUID_AND_ATTRIBUTES, OWNER_SECURITY_INFORMATION,
    PSID, SECURITY_MAX_SID_SIZE, SE_PRIVILEGE_ENABLED, TOKEN_ADJUST_PRIVILEGES, TOKEN_PRIVILEGES, TOKEN_QUERY,
};
use windows_sys::Win32::Storage::FileSystem::{GetFileAttributesW, FILE_ATTRIBUTE_REPARSE_POINT, INVALID_FILE_ATTRIBUTES};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken, ProcessRedirectionTrustPolicy, SetProcessMitigationPolicy};
use windows_sys::Win32::UI::Shell::{FOLDERID_ProgramFiles, FOLDERID_ProgramFilesX86};

use super::{from_wide_ptr, known_folder, wide, wide_path};
use crate::sys::Privileges;

/// True when this process holds an enabled Administrators group, i.e. it is running elevated.
pub fn is_elevated() -> bool {
    static ELEVATED: OnceLock<bool> = OnceLock::new();
    *ELEVATED.get_or_init(|| unsafe {
        let mut sid = [0u8; SECURITY_MAX_SID_SIZE as usize];
        let mut size = sid.len() as u32;
        if CreateWellKnownSid(WinBuiltinAdministratorsSid, null_mut(), sid.as_mut_ptr() as PSID, &mut size) == 0 {
            return false;
        }
        let mut member = 0;
        CheckTokenMembership(null_mut(), sid.as_mut_ptr() as PSID, &mut member) != 0 && member != 0
    })
}

unsafe fn enable_privilege(token: HANDLE, name: &str) -> bool {
    let mut luid: LUID = zeroed();
    if LookupPrivilegeValueW(null(), wide(name).as_ptr(), &mut luid) == 0 {
        return false;
    }
    let tp = TOKEN_PRIVILEGES { PrivilegeCount: 1, Privileges: [LUID_AND_ATTRIBUTES { Luid: luid, Attributes: SE_PRIVILEGE_ENABLED }] };
    // AdjustTokenPrivileges returns TRUE even when the token does not hold the privilege;
    // ERROR_NOT_ALL_ASSIGNED is the only signal that it was not actually enabled.
    if AdjustTokenPrivileges(token, 0, &tp, 0, null_mut(), null_mut()) == 0 {
        return false;
    }
    GetLastError() != ERROR_NOT_ALL_ASSIGNED
}

/// Enables the privileges PulseRAM uses when it runs as administrator. SeDebugPrivilege lets recovery also
/// resume elevated processes; SeProfileSingleProcessPrivilege is what NtSetSystemInformation
/// (SystemMemoryListInformation) and page combining actually require, so it alone decides whether kernel
/// purges are possible.
pub fn enable_privileges() -> Privileges {
    let is_admin = is_elevated();
    let mut purge_rights = false;
    unsafe {
        let mut token: HANDLE = null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &mut token) != 0 {
            let profile = enable_privilege(token, "SeProfileSingleProcessPrivilege");
            enable_privilege(token, "SeIncreaseQuotaPrivilege");
            enable_privilege(token, "SeDebugPrivilege");
            purge_rights = is_admin && profile;
            CloseHandle(token);
        }
    }
    Privileges { is_admin, purge_rights }
}

/// Windows 11 22H2+: makes this (elevated) process ignore junctions created by non-administrators
/// ("RedirectionGuard"). Earlier Windows versions ignore the call; the handle checks in `file` still apply.
pub fn enable_redirection_guard() {
    if !is_elevated() {
        return;
    }
    let flags: u32 = 1; // EnforceRedirectionTrust
    unsafe { SetProcessMitigationPolicy(ProcessRedirectionTrustPolicy, &flags as *const _ as *const c_void, size_of::<u32>()) };
}

// SYSTEM, Administrators, TrustedInstaller: the accounts that may own or write to a protected install folder.
const TRUSTED_OWNERS: [&str; 3] = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
// CREATOR OWNER is only meaningful on inherit-only entries.
const CREATOR_OWNER: &str = "S-1-3-0";

// WriteData | AppendData | WriteExtendedAttributes | DeleteSubdirectoriesAndFiles | WriteAttributes | Delete |
// ChangePermissions | TakeOwnership | GENERIC_ALL | GENERIC_WRITE
const WRITE_RIGHTS: u32 = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x1_0000 | 0x4_0000 | 0x8_0000 | 0x1000_0000 | 0x4000_0000;

unsafe fn sid_to_string(sid: PSID) -> Option<String> {
    if sid.is_null() {
        return None;
    }
    let mut text: *mut u16 = null_mut();
    if ConvertSidToStringSidW(sid, &mut text) == 0 || text.is_null() {
        return None;
    }
    let s = from_wide_ptr(text);
    LocalFree(text as *mut c_void);
    Some(s)
}

fn is_trusted_owner(sid: &str) -> bool {
    TRUSTED_OWNERS.iter().any(|t| t.eq_ignore_ascii_case(sid))
}

/// True if the security descriptor lets anyone but SYSTEM / Administrators / TrustedInstaller change the
/// object: an untrusted owner (the owner can always rewrite the permissions), no DACL, or an allow entry with
/// write rights — including inherit-only grants, which would apply to files added later.
unsafe fn grants_write_to_others(owner: PSID, dacl: *const ACL) -> bool {
    match sid_to_string(owner) {
        Some(owner) if is_trusted_owner(&owner) => {}
        _ => return true,
    }
    if dacl.is_null() {
        return true; // no DACL: everyone has full access
    }
    let mut info: ACL_SIZE_INFORMATION = zeroed();
    if GetAclInformation(dacl, &mut info as *mut _ as *mut c_void, size_of::<ACL_SIZE_INFORMATION>() as u32, AclSizeInformation) == 0 {
        return true;
    }
    for index in 0..info.AceCount {
        let mut ace: *mut c_void = null_mut();
        if GetAce(dacl, index, &mut ace) == 0 || ace.is_null() {
            return true;
        }
        let header = &*(ace as *const ACE_HEADER);
        match header.AceType {
            // ACCESS_ALLOWED_ACE and ACCESS_ALLOWED_CALLBACK_ACE: header, mask, SID.
            0x0 | 0x9 => {
                let mask = *((ace as *const u8).add(4) as *const u32);
                if mask & WRITE_RIGHTS == 0 {
                    continue;
                }
                let sid = (ace as *mut u8).add(8) as PSID;
                match sid_to_string(sid) {
                    Some(s) if is_trusted_owner(&s) || s.eq_ignore_ascii_case(CREATOR_OWNER) => continue,
                    _ => return true,
                }
            }
            // Deny entries never grant anything.
            0x1 | 0x6 | 0xA | 0xC => continue,
            // Anything else (object ACEs …) isn't expected on a file; don't trust what can't be read.
            _ => return true,
        }
    }
    false
}

/// Attributes of a path, or None if it doesn't exist.
fn attributes(path: &Path) -> Option<u32> {
    let attrs = unsafe { GetFileAttributesW(wide_path(path).as_ptr()) };
    (attrs != INVALID_FILE_ATTRIBUTES).then_some(attrs)
}

fn is_locked_down(path: &Path) -> bool {
    match attributes(path) {
        Some(attrs) if attrs & FILE_ATTRIBUTE_REPARSE_POINT == 0 => {}
        _ => return false,
    }
    unsafe {
        let (mut owner, mut dacl, mut descriptor): (PSID, *mut ACL, *mut c_void) = (null_mut(), null_mut(), null_mut());
        let status = GetNamedSecurityInfoW(
            wide_path(path).as_ptr(),
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            &mut owner,
            null_mut(),
            &mut dacl,
            null_mut(),
            &mut descriptor,
        );
        if status != 0 {
            return false;
        }
        let grants = grants_write_to_others(owner, dacl);
        LocalFree(descriptor);
        !grants
    }
}

fn all_entries_locked_down(dir: &Path) -> bool {
    let Ok(entries) = std::fs::read_dir(dir) else { return false };
    for entry in entries {
        let Ok(entry) = entry else { return false };
        let path = entry.path();
        if !is_locked_down(&path) {
            return false;
        }
        match entry.file_type() {
            Ok(t) if t.is_dir() => {
                if !all_entries_locked_down(&path) {
                    return false;
                }
            }
            Ok(_) => {}
            Err(_) => return false,
        }
    }
    true
}

fn starts_with_dir(path: &str, root: &str) -> bool {
    path.len() > root.len() && path.as_bytes()[root.len()] == b'\\' && path[..root.len()].eq_ignore_ascii_case(root)
}

/// True only for an all-users install in Program Files whose folders and files can't be changed by
/// non-administrators, so it is safe to start it elevated without a prompt. Never true for portable copies.
pub fn is_admin_only_writable(exe_path: &Path) -> bool {
    if crate::paths::is_portable() {
        return false;
    }
    let Ok(full) = std::path::absolute(exe_path) else { return false };
    let Some(dir) = full.parent() else { return false };
    let Some(dir_text) = dir.to_str() else { return false };

    let roots: Vec<String> = [known_folder(&FOLDERID_ProgramFiles), known_folder(&FOLDERID_ProgramFilesX86)]
        .into_iter()
        .flatten()
        .map(|r| r.trim_end_matches('\\').to_string())
        .filter(|r| r.len() >= 3)
        .collect();
    let Some(root) = roots.iter().find(|r| starts_with_dir(dir_text, r)) else { return false };

    // The install folder and every folder up to Program Files: no links, no write access for others.
    let mut current = Some(dir);
    while let Some(d) = current {
        if d.as_os_str().len() < root.len() {
            break;
        }
        if !is_locked_down(d) {
            return false;
        }
        current = d.parent();
    }
    // Every file and folder PulseRAM loads from. One writable file would be enough to take over an elevated start.
    all_entries_locked_down(dir)
}

/// Owner (as a SID string) of the object a handle refers to.
pub(super) unsafe fn owner_of_handle(handle: HANDLE) -> Option<String> {
    let (mut owner, mut descriptor): (PSID, *mut c_void) = (null_mut(), null_mut());
    if GetSecurityInfo(handle, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &mut owner, null_mut(), null_mut(), null_mut(), &mut descriptor) != 0 {
        return None;
    }
    let text = sid_to_string(owner);
    LocalFree(descriptor);
    text
}

pub(super) fn is_trusted_link_owner(sid: &str) -> bool {
    is_trusted_owner(sid)
}

pub(super) fn path_attributes(path: &Path) -> Option<u32> {
    attributes(path)
}
