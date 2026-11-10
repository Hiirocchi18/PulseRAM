//! Wall-clock helpers (no date/time crate: PulseRAM only needs timestamps for logs, labels and the daily
//! update check).

use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Civil {
    pub year: i32,
    pub month: u32,
    pub day: u32,
    pub hour: u32,
    pub minute: u32,
    pub second: u32,
    pub millis: u32,
}

pub fn unix_now() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
}

fn unix_millis_now() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_millis() as i64).unwrap_or(0)
}

/// Days since 1970-01-01 → (year, month, day). Howard Hinnant's civil-from-days algorithm.
fn civil_from_days(days: i64) -> (i32, u32, u32) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let month = (if mp < 10 { mp + 3 } else { mp - 9 }) as u32;
    let year = (yoe + era * 400 + i64::from(month <= 2)) as i32;
    (year, month, day)
}

fn days_from_civil(year: i32, month: u32, day: u32) -> i64 {
    let y = i64::from(year) - i64::from(month <= 2);
    let era = y.div_euclid(400);
    let yoe = y.rem_euclid(400);
    let mp = (i64::from(month) + 9) % 12;
    let doy = (153 * mp + 2) / 5 + i64::from(day) - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

pub fn civil_from_unix(secs: i64, millis: u32) -> Civil {
    let days = secs.div_euclid(86_400);
    let rem = secs.rem_euclid(86_400);
    let (year, month, day) = civil_from_days(days);
    Civil { year, month, day, hour: (rem / 3600) as u32, minute: (rem % 3600 / 60) as u32, second: (rem % 60) as u32, millis }
}

pub fn unix_from_civil(c: &Civil) -> i64 {
    days_from_civil(c.year, c.month, c.day) * 86_400 + i64::from(c.hour) * 3600 + i64::from(c.minute) * 60 + i64::from(c.second)
}

/// Local time zone offset from UTC in seconds (0 on non-Windows development builds).
pub fn utc_offset_secs() -> i64 {
    crate::sys::utc_offset_secs()
}

pub fn local_now() -> Civil {
    let ms = unix_millis_now();
    civil_from_unix(ms.div_euclid(1000) + utc_offset_secs(), ms.rem_euclid(1000) as u32)
}

pub fn local_from_unix(secs: i64) -> Civil {
    civil_from_unix(secs + utc_offset_secs(), 0)
}

impl Civil {
    /// "HH:mm:ss"
    pub fn hms(&self) -> String {
        format!("{:02}:{:02}:{:02}", self.hour, self.minute, self.second)
    }

    /// "HH:mm"
    pub fn hm(&self) -> String {
        format!("{:02}:{:02}", self.hour, self.minute)
    }

    /// "yyyyMMdd"
    pub fn ymd_compact(&self) -> String {
        format!("{:04}{:02}{:02}", self.year, self.month, self.day)
    }

    pub fn month_abbrev(&self) -> &'static str {
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"][(self.month.clamp(1, 12) - 1) as usize]
    }
}

/// "2026-11-09T08:15:30Z"
pub fn format_iso_utc(secs: i64) -> String {
    let c = civil_from_unix(secs, 0);
    format!("{:04}-{:02}-{:02}T{:02}:{:02}:{:02}Z", c.year, c.month, c.day, c.hour, c.minute, c.second)
}

/// Parses "yyyy-MM-ddTHH:mm:ss[.fraction][Z|±hh:mm]" (what PulseRAM 1.x and 2.x write). None if malformed.
pub fn parse_iso_utc(text: &str) -> Option<i64> {
    let t = text.trim();
    let b = t.as_bytes();
    if b.len() < 19 || b[4] != b'-' || b[7] != b'-' || (b[10] != b'T' && b[10] != b' ') || b[13] != b':' || b[16] != b':' {
        return None;
    }
    let num = |range: std::ops::Range<usize>| t.get(range)?.parse::<u32>().ok();
    let c = Civil {
        year: num(0..4)? as i32,
        month: num(5..7)?,
        day: num(8..10)?,
        hour: num(11..13)?,
        minute: num(14..16)?,
        second: num(17..19)?,
        millis: 0,
    };
    if !(1..=12).contains(&c.month) || !(1..=31).contains(&c.day) || c.hour > 23 || c.minute > 59 || c.second > 60 {
        return None;
    }
    let mut secs = unix_from_civil(&c);
    // Optional fraction, then an optional zone.
    let rest = t[19..].trim_start_matches(|ch: char| ch == '.' || ch.is_ascii_digit());
    if let Some(sign) = rest.chars().next().filter(|ch| *ch == '+' || *ch == '-') {
        let hh: i64 = rest.get(1..3)?.parse().ok()?;
        let mm: i64 = rest.get(4..6).and_then(|m| m.parse().ok()).unwrap_or(0);
        let offset = hh * 3600 + mm * 60;
        secs -= if sign == '+' { offset } else { -offset };
    }
    Some(secs)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn civil_round_trip() {
        for secs in [0, 951_782_400 /* 2000-02-29 */, 1_792_314_000, 4_102_444_799] {
            let c = civil_from_unix(secs, 0);
            assert_eq!(unix_from_civil(&c), secs);
        }
        let c = civil_from_unix(951_782_400, 0);
        assert_eq!((c.year, c.month, c.day), (2000, 2, 29));
    }

    #[test]
    fn iso_round_trip_and_variants() {
        let secs = 1_794_212_130;
        assert_eq!(parse_iso_utc(&format_iso_utc(secs)), Some(secs));
        // .NET round-trip format with seven fractional digits
        assert_eq!(parse_iso_utc("2026-11-09T08:15:30.1234567Z"), parse_iso_utc("2026-11-09T08:15:30Z"));
        assert_eq!(parse_iso_utc("2026-11-09T10:15:30+02:00"), parse_iso_utc("2026-11-09T08:15:30Z"));
        assert_eq!(parse_iso_utc("yesterday"), None);
        assert_eq!(parse_iso_utc(""), None);
    }
}
