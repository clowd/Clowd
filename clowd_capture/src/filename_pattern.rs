//! The name the SAVE dialog opens with.
//!
//! The shell owns the "Filename pattern" setting (`SettingsCapture.FilenamePattern`
//! — a .NET custom date-format string such as `yyyy-MM-dd HH-mm-ss`) and hands it
//! to the overlay as `--filename-pattern`, alongside the last folder the user saved
//! into as `--save-dir`. Saving the same capture from the editor instead runs the
//! pattern through `NiceDialog.ShowSaveImageDialog` / `PathConstants.GetFreePatternFileName`,
//! so both routes have to produce the same name for the same setting: the rendering
//! below mirrors those two, collision suffix and fallbacks included.
//!
//! Two deliberate divergences from `DateTime.ToString`, neither reachable by a
//! pattern that can name a file:
//!
//! * month and day names (`MMM`, `dddd`) are English, where .NET would use the
//!   user's culture. Rendering those in every culture means shipping a locale
//!   database for a specifier almost nobody puts in a filename.
//! * the timezone specifiers (`z`, `zz`, `zzz`, `K`) are copied out literally
//!   rather than rendered. Their output contains `:` or nothing useful, so a
//!   pattern using them cannot name a file anyway.

use std::path::Path;

/// Mirrors `SettingsCapture.FilenamePattern`'s own default, and the fallback
/// both sides apply when the setting is blank.
pub const DEFAULT_FILENAME_PATTERN: &str = "yyyy-MM-dd HH-mm-ss";

/// Highest collision suffix tried before giving up on the pattern — the loop
/// bound in `PathConstants.GetFreePatternFileName`.
const MAX_COLLISION_SUFFIX: u32 = 100;

/// Broken-down local time, the only input the renderer needs. Split out from
/// the platform call so the formatting can be tested against a fixed instant.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LocalTime {
    pub year: i32,
    /// 1-12
    pub month: u32,
    /// 1-31
    pub day: u32,
    /// 0-23
    pub hour: u32,
    pub minute: u32,
    pub second: u32,
    pub millis: u32,
    /// 0 = Sunday
    pub weekday: u32,
}

/// The file name (without extension) the save dialog should open with: the
/// pattern rendered against now, uniquified against whatever is already in
/// `directory` the way the editor's dialog does — "name", "name (1)", …
///
/// `directory` is `None` in standalone runs and whenever the shell has no last
/// save path to offer; the name is then rendered but not uniquified, which is
/// the best that can be done without knowing where the user will point the
/// dialog.
pub fn suggested_file_name(directory: Option<&Path>, pattern: &str) -> String {
    suggested_file_name_at(local_now(), directory, pattern)
}

fn suggested_file_name_at(now: LocalTime, directory: Option<&Path>, pattern: &str) -> String {
    // an extension typed into the pattern ("yyyy-MM-dd.png") would otherwise be baked into
    // the name and doubled by the one the dialog appends — the shell strips it too.
    let pattern = match Path::new(pattern.trim())
        .file_stem()
        .and_then(|s| s.to_str())
    {
        Some(stem) if !stem.trim().is_empty() => stem,
        _ => DEFAULT_FILENAME_PATTERN,
    };

    let rendered = render(now, pattern);

    // a pattern containing a separator ("yyyy/MM/dd") would write outside the folder the
    // dialog opens in, and one the OS cannot spell has to be replaced outright.
    if rendered.is_empty()
        || rendered
            .chars()
            .any(is_invalid_file_name_char)
    {
        return fallback_name(now);
    }

    let Some(directory) = directory else {
        return rendered;
    };

    let taken = existing_file_stems(directory);
    for i in 0..MAX_COLLISION_SUFFIX {
        let candidate = if i == 0 { rendered.clone() } else { format!("{rendered} ({i})") };
        if !taken
            .iter()
            .any(|f| f.eq_ignore_ascii_case(&candidate))
        {
            return candidate;
        }
    }

    // a hundred files deep with the same name — a literal-only pattern in a busy
    // folder. The timestamp is what the shell falls back to as well.
    fallback_name(now)
}

/// The name used when the pattern cannot produce one: unique to the millisecond,
/// matching `PathConstants.GetFreePatternFileName`'s own last resort.
fn fallback_name(now: LocalTime) -> String {
    render(now, "yyyyMMdd_HHmmss_fff")
}

fn existing_file_stems(directory: &Path) -> Vec<String> {
    let Ok(entries) = std::fs::read_dir(directory) else {
        // an unreadable folder only costs the collision check; the dialog still opens.
        return Vec::new();
    };

    entries
        .flatten()
        .filter_map(|e| {
            let path = e.path();
            if path.is_dir() {
                return None;
            }
            path.file_stem()
                .and_then(|s| s.to_str())
                .map(|s| s.to_owned())
        })
        .collect()
}

/// `Path.GetInvalidFileNameChars()`, per platform, so a pattern the shell would
/// accept is not rejected here (and the reverse).
fn is_invalid_file_name_char(c: char) -> bool {
    #[cfg(windows)]
    {
        matches!(c, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*') || (c as u32) < 0x20
    }
    #[cfg(not(windows))]
    {
        c == '/' || c == '\0'
    }
}

const MONTHS: [&str; 12] = [
    "January",
    "February",
    "March",
    "April",
    "May",
    "June",
    "July",
    "August",
    "September",
    "October",
    "November",
    "December",
];

const DAYS: [&str; 7] = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

/// Render a .NET custom date-format string. Unrecognized characters are copied
/// through as literals, which is what `DateTime.ToString` does with them.
fn render(t: LocalTime, pattern: &str) -> String {
    let chars: Vec<char> = pattern.chars().collect();
    let mut out = String::with_capacity(chars.len() + 8);
    let mut i = 0;

    while i < chars.len() {
        let c = chars[i];
        match c {
            // `\x` escapes exactly one character.
            '\\' => {
                if let Some(&next) = chars.get(i + 1) {
                    out.push(next);
                    i += 2;
                } else {
                    i += 1;
                }
            }
            // quoted literal runs; an unterminated one runs to the end of the pattern.
            '\'' | '"' => {
                let quote = c;
                i += 1;
                while i < chars.len() && chars[i] != quote {
                    // a backslash escape survives inside a quoted run too.
                    if chars[i] == '\\' && i + 1 < chars.len() {
                        out.push(chars[i + 1]);
                        i += 2;
                    } else {
                        out.push(chars[i]);
                        i += 1;
                    }
                }
                i += 1; // past the closing quote (or harmlessly past the end)
            }
            // `%c` forces the single-character reading of the specifier that follows.
            '%' => {
                if let Some(&next) = chars.get(i + 1) {
                    push_specifier(&mut out, t, next, 1);
                    i += 2;
                } else {
                    i += 1;
                }
            }
            _ => {
                let count = chars[i..]
                    .iter()
                    .take_while(|&&x| x == c)
                    .count();
                push_specifier(&mut out, t, c, count);
                i += count;
            }
        }
    }

    out
}

/// Append one specifier run — `count` repeats of `c`. A character that is not a
/// specifier is emitted verbatim, repeats included.
fn push_specifier(out: &mut String, t: LocalTime, c: char, count: usize) {
    match c {
        'd' => match count {
            1 => out.push_str(&t.day.to_string()),
            2 => out.push_str(&format!("{:02}", t.day)),
            3 => out.push_str(&DAYS[t.weekday as usize % 7][..3]),
            _ => out.push_str(DAYS[t.weekday as usize % 7]),
        },
        'M' => match count {
            1 => out.push_str(&t.month.to_string()),
            2 => out.push_str(&format!("{:02}", t.month)),
            3 => out.push_str(&MONTHS[month_index(t.month)][..3]),
            _ => out.push_str(MONTHS[month_index(t.month)]),
        },
        'y' => match count {
            1 => out.push_str(&(t.year % 100).to_string()),
            2 => out.push_str(&format!("{:02}", t.year % 100)),
            n => out.push_str(&format!("{:0width$}", t.year, width = n)),
        },
        'H' => out.push_str(&pad(t.hour, count)),
        'h' => {
            let h12 = match t.hour % 12 {
                0 => 12,
                h => h,
            };
            out.push_str(&pad(h12, count));
        }
        'm' => out.push_str(&pad(t.minute, count)),
        's' => out.push_str(&pad(t.second, count)),
        // fractional seconds. We only ever have millisecond resolution, so the
        // 4th digit onwards is zero — exactly what .NET prints for a DateTime
        // built from a millisecond clock.
        'f' | 'F' => {
            let digits = count.min(7);
            let text: String = format!("{:03}0000", t.millis)
                .chars()
                .take(digits)
                .collect();
            if c == 'f' {
                out.push_str(&text);
            } else {
                // `F` drops trailing zeros, and the whole run when they all are.
                out.push_str(text.trim_end_matches('0'));
            }
        }
        't' => {
            let designator = if t.hour < 12 { "AM" } else { "PM" };
            if count == 1 {
                out.push_str(&designator[..1]);
            } else {
                out.push_str(designator);
            }
        }
        'g' => out.push_str("A.D."),
        _ => {
            for _ in 0..count {
                out.push(c);
            }
        }
    }
}

fn month_index(month: u32) -> usize {
    (month.clamp(1, 12) - 1) as usize
}

/// `count == 1` prints the value as-is, anything longer pads to two digits —
/// .NET has no three-digit hour/minute/second specifier.
fn pad(value: u32, count: usize) -> String {
    if count == 1 {
        value.to_string()
    } else {
        format!("{value:02}")
    }
}

#[cfg(windows)]
fn local_now() -> LocalTime {
    use windows::Win32::System::SystemInformation::GetLocalTime;

    let st = unsafe { GetLocalTime() };
    LocalTime {
        year: st.wYear as i32,
        month: st.wMonth as u32,
        day: st.wDay as u32,
        hour: st.wHour as u32,
        minute: st.wMinute as u32,
        second: st.wSecond as u32,
        millis: st.wMilliseconds as u32,
        weekday: st.wDayOfWeek as u32,
    }
}

#[cfg(not(windows))]
fn local_now() -> LocalTime {
    use std::time::{SystemTime, UNIX_EPOCH};

    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    let secs = now.as_secs() as libc::time_t;
    let mut tm: libc::tm = unsafe { std::mem::zeroed() };
    // localtime_r, unlike localtime, is safe to call from a process this
    // threaded — the overlay runs a render worker per monitor.
    unsafe { libc::localtime_r(&secs, &mut tm) };

    LocalTime {
        year: tm.tm_year + 1900,
        month: (tm.tm_mon + 1) as u32,
        day: tm.tm_mday as u32,
        hour: tm.tm_hour as u32,
        minute: tm.tm_min as u32,
        second: tm.tm_sec as u32,
        millis: now.subsec_millis(),
        weekday: tm.tm_wday as u32,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Wednesday, 5 March 2025, 09:07:03.045 local.
    fn sample() -> LocalTime {
        LocalTime {
            year: 2025,
            month: 3,
            day: 5,
            hour: 9,
            minute: 7,
            second: 3,
            millis: 45,
            weekday: 3,
        }
    }

    #[test]
    fn renders_the_default_pattern_the_way_dotnet_does() {
        assert_eq!(render(sample(), DEFAULT_FILENAME_PATTERN), "2025-03-05 09-07-03");
    }

    #[test]
    fn renders_each_specifier_width() {
        let t = sample();
        assert_eq!(render(t, "d dd ddd dddd"), "5 05 Wed Wednesday");
        assert_eq!(render(t, "M MM MMM MMMM"), "3 03 Mar March");
        assert_eq!(render(t, "y yy yyyy"), "25 25 2025");
        assert_eq!(render(t, "H HH h hh tt t"), "9 09 9 09 AM A");
        assert_eq!(render(t, "m mm s ss"), "7 07 3 03");
        assert_eq!(render(t, "f ff fff ffff"), "0 04 045 0450");
        assert_eq!(render(t, "F FF FFF"), " 04 045");
    }

    #[test]
    fn twelve_hour_clock_wraps_at_noon_and_midnight() {
        let midnight = LocalTime {
            hour: 0,
            ..sample()
        };
        assert_eq!(render(midnight, "hh tt"), "12 AM");

        let noon = LocalTime {
            hour: 12,
            ..sample()
        };
        assert_eq!(render(noon, "hh tt"), "12 PM");

        let evening = LocalTime {
            hour: 23,
            ..sample()
        };
        assert_eq!(render(evening, "hh tt"), "11 PM");
    }

    #[test]
    fn literals_survive_quoting_and_escaping() {
        let t = sample();
        assert_eq!(render(t, "'clowd' yyyy"), "clowd 2025");
        assert_eq!(render(t, "\"shot\"-dd"), "shot-05");
        assert_eq!(render(t, "\\d\\d dd"), "dd 05");
        assert_eq!(render(t, "%d"), "5");
        // an unterminated quote runs to the end, as it does in .NET.
        assert_eq!(render(t, "'tail"), "tail");
    }

    /// Characters that are not specifiers are copied out; ones that are get
    /// rendered even in the middle of a word, which is why literal text in a
    /// pattern has to be quoted ("screenshot" is `sho` + `t`, four specifiers).
    #[test]
    fn unknown_characters_pass_through_unchanged() {
        assert_eq!(render(sample(), "cap!ure_yyyy"), "cap!ure_2025");
        assert_eq!(render(sample(), "'screenshot' yyyy"), "screenshot 2025");
        assert_eq!(render(sample(), "screenshot"), "3creen39oA");
    }

    #[test]
    fn blank_pattern_falls_back_to_the_default() {
        assert_eq!(suggested_file_name_at(sample(), None, "   "), "2025-03-05 09-07-03");
        assert_eq!(suggested_file_name_at(sample(), None, ""), "2025-03-05 09-07-03");
    }

    /// A pattern with its own extension is stripped, exactly as the shell strips it.
    #[test]
    fn extension_in_the_pattern_is_dropped() {
        assert_eq!(suggested_file_name_at(sample(), None, "yyyy-MM-dd.png"), "2025-03-05");
    }

    /// A pattern that renders to nothing, or to something the OS will not spell,
    /// cannot name a file.
    #[test]
    fn unusable_pattern_falls_back_to_a_timestamp() {
        assert_eq!(suggested_file_name_at(sample(), None, "''"), "20250305_090703_045");

        // `|` is invalid on Windows and perfectly legal elsewhere, which is the
        // per-platform split `Path.GetInvalidFileNameChars` gives the shell.
        let piped = suggested_file_name_at(sample(), None, "'a|b'");
        if cfg!(windows) {
            assert_eq!(piped, "20250305_090703_045");
        } else {
            assert_eq!(piped, "a|b");
        }
    }

    #[test]
    fn collisions_take_a_numbered_suffix() {
        let dir = std::env::temp_dir().join("clowd_filename_pattern_test");
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();

        assert_eq!(suggested_file_name_at(sample(), Some(&dir), "'shot'"), "shot");

        std::fs::write(dir.join("shot.png"), b"").unwrap();
        assert_eq!(suggested_file_name_at(sample(), Some(&dir), "'shot'"), "shot (1)");

        // the extension is not part of the comparison — the shell compares stems too.
        std::fs::write(dir.join("shot (1).jpg"), b"").unwrap();
        assert_eq!(suggested_file_name_at(sample(), Some(&dir), "'shot'"), "shot (2)");

        std::fs::remove_dir_all(&dir).unwrap();
    }

    /// A folder that cannot be read costs the collision check, not the dialog.
    #[test]
    fn missing_directory_still_yields_a_name() {
        let dir = std::env::temp_dir().join("clowd_filename_pattern_absent");
        let _ = std::fs::remove_dir_all(&dir);
        assert_eq!(
            suggested_file_name_at(sample(), Some(&dir), DEFAULT_FILENAME_PATTERN),
            "2025-03-05 09-07-03"
        );
    }
}
