//! Asks GitHub for the latest PulseRAM release, at most once a day. Sends nothing but a plain HTTPS GET (no
//! identifiers, no telemetry). The result only ever opens the release page in the browser: PulseRAM never
//! downloads or runs anything by itself.

use serde_json::Value;

use crate::config::EngineConfig;
use crate::policy::{self, Version};
use crate::sys::{self, HttpResult};
use crate::{clock, log};

pub const REPO: &str = "Hiirocchi18/PulseRAM";
const API_HOST: &str = "api.github.com";
const RELEASE_PAGE_PREFIX: &str = "https://github.com/Hiirocchi18/PulseRAM/releases/";

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct UpdateInfo {
    pub version: String,
    pub page_url: String,
}

pub fn current_version() -> Version {
    policy::parse_version_tag(env!("CARGO_PKG_VERSION")).unwrap_or(Version(0, 0, 0))
}

pub fn latest_release_page() -> String {
    format!("{RELEASE_PAGE_PREFIX}latest")
}

/// True if a scheduled check is due (enabled and not checked in the last 24 h).
pub fn is_due(cfg: &EngineConfig, now_unix: i64) -> bool {
    if !cfg.check_for_updates {
        return false;
    }
    match cfg.last_update_check_utc.as_deref().and_then(clock::parse_iso_utc) {
        Some(last) => now_unix - last >= 24 * 3600 || last > now_unix,
        None => true,
    }
}

/// What a release document means for this install: the newer release, or None when up to date.
fn evaluate_release(body: &str, current: Version, skipped_version: &str, ignore_skipped: bool) -> Result<Option<UpdateInfo>, ()> {
    let root: Value = serde_json::from_str(body).map_err(|_| ())?;
    let text = |key: &str| root.get(key).and_then(Value::as_str).unwrap_or("");
    let flag = |key: &str| root.get(key).and_then(Value::as_bool).unwrap_or(false);
    let tag = text("tag_name");

    if flag("draft") || flag("prerelease") || !policy::is_newer(tag, current) {
        log::info(format!("Update check: up to date (latest release {tag})"));
        return Ok(None);
    }
    let Some(latest) = policy::parse_version_tag(tag) else { return Ok(None) };
    let latest = latest.to_string();
    if !ignore_skipped && skipped_version.eq_ignore_ascii_case(&latest) {
        return Ok(None);
    }
    // Only ever open this repository's own release page.
    let url = text("html_url");
    let page_url = if url.to_ascii_lowercase().starts_with(&RELEASE_PAGE_PREFIX.to_ascii_lowercase()) { url.to_string() } else { latest_release_page() };
    log::info(format!("Update available: {latest} (running {current})"));
    Ok(Some(UpdateInfo { version: latest, page_url }))
}

/// (answered, info): `info` is the newer release, or None when up to date or no release exists. `answered` is
/// false when GitHub couldn't be asked (offline, timeout, rate limit), so the caller can try again later
/// instead of waiting a day. `ignore_skipped` is for a manual "Check now". Blocking; call it off the UI thread.
pub fn check(skipped_version: &str, ignore_skipped: bool) -> (bool, Option<UpdateInfo>) {
    let user_agent = format!("PulseRAM/{}", current_version());
    match sys::https_get(API_HOST, &format!("/repos/{REPO}/releases/latest"), &user_agent, "application/vnd.github+json") {
        HttpResult::Response(404, _) => {
            log::info("Update check: no published release yet");
            (true, None)
        }
        HttpResult::Response(200..=299, body) => match evaluate_release(&body, current_version(), skipped_version, ignore_skipped) {
            Ok(info) => (true, info),
            Err(()) => {
                log::info("Update check skipped: unreadable answer");
                (false, None)
            }
        },
        HttpResult::Response(status, _) => {
            log::info(format!("Update check: GitHub answered {status}; will try again later"));
            (false, None)
        }
        HttpResult::Failed(why) => {
            log::info(format!("Update check skipped: {why}"));
            (false, None)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn release(tag: &str, extra: &str) -> String {
        format!(r#"{{"tag_name":"{tag}","html_url":"https://github.com/Hiirocchi18/PulseRAM/releases/tag/{tag}","name":"x"{extra}}}"#)
    }

    #[test]
    fn offers_a_newer_stable_release() {
        let info = evaluate_release(&release("v2.1.0", ""), Version(2, 0, 0), "", false).unwrap().unwrap();
        assert_eq!(info.version, "2.1.0");
        assert_eq!(info.page_url, "https://github.com/Hiirocchi18/PulseRAM/releases/tag/v2.1.0");
    }

    #[test]
    fn ignores_same_older_draft_and_prerelease() {
        let current = Version(2, 0, 0);
        assert_eq!(evaluate_release(&release("v2.0.0", ""), current, "", false), Ok(None));
        assert_eq!(evaluate_release(&release("v1.4.1", ""), current, "", false), Ok(None));
        assert_eq!(evaluate_release(&release("v2.1.0", r#","draft":true"#), current, "", false), Ok(None));
        assert_eq!(evaluate_release(&release("v2.1.0", r#","prerelease":true"#), current, "", false), Ok(None));
        assert_eq!(evaluate_release(&release("v2.1.0-beta", ""), current, "", false), Ok(None));
    }

    #[test]
    fn skipped_version_is_only_offered_on_a_manual_check() {
        let body = release("v2.1.0", "");
        assert_eq!(evaluate_release(&body, Version(2, 0, 0), "2.1.0", false), Ok(None));
        assert!(evaluate_release(&body, Version(2, 0, 0), "2.1.0", true).unwrap().is_some());
    }

    #[test]
    fn never_opens_a_page_outside_this_repository() {
        let body = r#"{"tag_name":"v9.0.0","html_url":"https://evil.example/download"}"#;
        let info = evaluate_release(body, Version(2, 0, 0), "", false).unwrap().unwrap();
        assert_eq!(info.page_url, "https://github.com/Hiirocchi18/PulseRAM/releases/latest");
        assert!(evaluate_release("<html>rate limited</html>", Version(2, 0, 0), "", false).is_err());
    }

    #[test]
    fn due_once_a_day() {
        let now = 1_794_212_130;
        let mut cfg = EngineConfig::default();
        assert!(is_due(&cfg, now), "never checked");
        cfg.last_update_check_utc = Some(clock::format_iso_utc(now - 3600));
        assert!(!is_due(&cfg, now));
        cfg.last_update_check_utc = Some(clock::format_iso_utc(now - 25 * 3600));
        assert!(is_due(&cfg, now));
        cfg.last_update_check_utc = Some(clock::format_iso_utc(now + 5 * 3600));
        assert!(is_due(&cfg, now), "a clock set back shouldn't block checks");
        cfg.check_for_updates = false;
        assert!(!is_due(&cfg, now));
    }
}
