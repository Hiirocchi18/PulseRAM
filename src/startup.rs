//! "Start with Windows": the per-user Run key, or — only for an elevated PulseRAM installed where ordinary
//! programs can't change its files — a Task Scheduler task that starts it elevated at logon.

use std::io::Write;
use std::path::{Path, PathBuf};

use crate::{log, sys};

pub const ELEVATED_TASK_NAME: &str = "PulseRAM Elevated Startup";

pub fn elevated_task_exists() -> bool {
    sys::run_schtasks(&["/Query", "/TN", ELEVATED_TASK_NAME]).0 == 0
}

pub fn delete_elevated_task() -> bool {
    sys::run_schtasks(&["/Delete", "/TN", ELEVATED_TASK_NAME, "/F"]).0 == 0
}

pub fn is_enabled() -> bool {
    sys::run_key_exists() || elevated_task_exists()
}

fn xml_escape(text: &str) -> String {
    text.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;").replace('\'', "&apos;")
}

fn xml_unescape(text: &str) -> String {
    text.replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&apos;", "'").replace("&#39;", "'").replace("&amp;", "&")
}

/// XML (rather than schtasks flags) so the task also starts on battery power and never times out.
fn task_xml(user: &str, exe_path: &Path) -> String {
    let user = xml_escape(user);
    let exe = xml_escape(&exe_path.to_string_lossy());
    format!(
        r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Starts PulseRAM elevated in the system tray at logon.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--tray</Arguments></Exec></Actions>
</Task>"#
    )
}

/// The `<Command>` of a task definition, as schtasks prints it.
fn command_from_task_xml(xml: &str) -> Option<String> {
    let start = xml.find("<Command>")? + "<Command>".len();
    let end = start + xml[start..].find("</Command>")?;
    let command = xml_unescape(&xml[start..end]).trim().trim_matches('"').to_string();
    (!command.is_empty()).then_some(command)
}

/// The EXE the elevated startup task runs, or None if there is no task (or it can't be read).
pub fn elevated_task_command() -> Option<String> {
    let (code, output) = sys::run_schtasks(&["/Query", "/XML", "/TN", ELEVATED_TASK_NAME]);
    if code != 0 {
        return None;
    }
    command_from_task_xml(&output)
}

fn same_path(a: &Path, b: &Path) -> bool {
    let full = |p: &Path| std::path::absolute(p).unwrap_or_else(|_| p.to_path_buf()).to_string_lossy().to_lowercase();
    full(a) == full(b)
}

fn open_exclusive_for_schtasks(path: &Path) -> std::io::Result<std::fs::File> {
    let mut options = std::fs::OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options.share_mode(1); // FILE_SHARE_READ: schtasks may read it, nothing may replace or rewrite it
    }
    options.open(path)
}

pub fn create_elevated_task(exe_path: &Path) -> bool {
    let Some(user) = sys::current_user_name() else { return false };
    let xml = task_xml(&user, exe_path);

    // schtasks needs a file. %TEMP% is writable by every program the user runs, so keep the file open without
    // write/delete sharing while schtasks reads it (nothing can swap its contents), then check that the
    // registered task runs exactly our EXE.
    let tmp: PathBuf = std::env::temp_dir().join(format!("pulseram_task_{}_{}.xml", std::process::id(), crate::clock::unix_now()));
    let created = match open_exclusive_for_schtasks(&tmp) {
        Ok(mut file) => {
            let mut bytes = vec![0xFF, 0xFE]; // UTF-16 LE byte-order mark
            bytes.extend(xml.encode_utf16().flat_map(u16::to_le_bytes));
            let written = file.write_all(&bytes).and_then(|_| file.sync_all()).is_ok();
            let ok = written && sys::run_schtasks(&["/Create", "/F", "/TN", ELEVATED_TASK_NAME, "/XML", &tmp.to_string_lossy()]).0 == 0;
            drop(file);
            ok
        }
        Err(e) => {
            log::warn(format!("Could not create the elevated startup task: {e}"));
            false
        }
    };
    let _ = std::fs::remove_file(&tmp);
    if !created {
        return false;
    }

    match elevated_task_command() {
        Some(registered) if same_path(Path::new(&registered), exe_path) => true,
        registered => {
            log::warn(format!("Elevated startup task does not run {} (found {registered:?}); removing it", exe_path.display()));
            delete_elevated_task();
            false
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TaskAudit {
    /// No task, or it points at an EXE ordinary programs can't change.
    Fine,
    /// An unsafe task was found and removed.
    Removed,
    /// An unsafe task was found but removing it needs administrator rights.
    UnsafeLeft,
}

/// An elevated logon task that runs an EXE from a folder ordinary programs can write to (e.g. an older
/// per-user install, a portable copy, or a folder left behind after uninstalling) would let any program gain
/// administrator rights at the next logon. Remove it (needs admin); otherwise report it.
pub fn audit_elevated_task(is_admin: bool) -> TaskAudit {
    if !elevated_task_exists() {
        return TaskAudit::Fine;
    }
    let command = elevated_task_command();
    let safe = command.as_deref().is_some_and(|c| Path::new(c).is_file() && sys::is_admin_only_writable(Path::new(c)));
    if safe {
        return TaskAudit::Fine;
    }
    if is_admin && delete_elevated_task() {
        log::warn(format!("Removed an unsafe elevated startup task (it ran {command:?} from a folder other programs can write to)"));
        return TaskAudit::Removed;
    }
    log::warn(format!("Unsafe elevated startup task found ({command:?}); run PulseRAM as administrator once to remove it"));
    TaskAudit::UnsafeLeft
}

pub struct StartupResult {
    pub ok: bool,
    /// Whether "start with Windows" is on after this call.
    pub enabled: bool,
    pub message: String,
}

/// Enables or disables launch at logon. When PulseRAM is running as administrator from a protected install
/// folder, startup uses an elevated Task Scheduler task so kernel purges keep working after a reboot (a
/// Run-key entry always starts as a standard user). Otherwise it falls back to the per-user Run key.
pub fn set_startup(enable: bool, is_admin: bool) -> StartupResult {
    let exe_path = crate::paths::exe_path();
    if enable {
        let protected_location = sys::is_admin_only_writable(&exe_path);
        if is_admin && protected_location && create_elevated_task(&exe_path) {
            // Remove the Run-key entry: a non-elevated copy launched by it could win the single-instance race
            // and the elevated one would exit.
            let _ = sys::set_run_key(false, &exe_path);
            return StartupResult { ok: true, enabled: true, message: "PulseRAM will start elevated in the tray at logon (Task Scheduler).".into() };
        }
        if let Err(e) = sys::set_run_key(true, &exe_path) {
            return StartupResult { ok: false, enabled: is_enabled(), message: format!("Could not change startup: {e}") };
        }
        let message = if !is_admin {
            "Starts at logon as a standard user. Run PulseRAM as administrator and re-enable to start elevated."
        } else if !protected_location {
            "Starts at logon as a standard user. PulseRAM is installed where other programs can change its files, so it won't start itself as administrator. Install it for all users (in Program Files) to allow that."
        } else {
            "Starts at logon as a standard user (the elevated task could not be created)."
        };
        StartupResult { ok: true, enabled: true, message: message.into() }
    } else {
        if let Err(e) = sys::set_run_key(false, &exe_path) {
            return StartupResult { ok: false, enabled: is_enabled(), message: format!("Could not change startup: {e}") };
        }
        let task_removed = !elevated_task_exists() || delete_elevated_task();
        if task_removed {
            StartupResult { ok: true, enabled: false, message: "Removed PulseRAM from Windows startup.".into() }
        } else {
            StartupResult { ok: false, enabled: true, message: "The elevated startup task can only be removed while PulseRAM runs as administrator.".into() }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn task_xml_round_trips_the_command() {
        let exe = Path::new(r"C:\Program Files\R&D <Tools>\Pulse'RAM\PulseRAM.exe");
        let xml = task_xml(r"PC\o'brien & co", exe);
        assert!(xml.contains("<UserId>PC\\o&apos;brien &amp; co</UserId>"));
        assert_eq!(command_from_task_xml(&xml).as_deref(), exe.to_str());
    }

    #[test]
    fn command_is_trimmed_and_unquoted() {
        assert_eq!(command_from_task_xml("<Exec>\n<Command> \"C:\\a b\\x.exe\" </Command></Exec>").as_deref(), Some(r"C:\a b\x.exe"));
        assert_eq!(command_from_task_xml("<Command></Command>"), None);
        assert_eq!(command_from_task_xml("no task here"), None);
    }
}
