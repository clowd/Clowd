//! The `session.json` contract, and the file helpers that go with it.
//!
//! Both binaries write this file — the overlay for an ordinary capture
//! (`clowd_capture/src/session_output.rs`), the driver for a finished
//! scrolling capture (`clowd_scroll_driver/src/output.rs`) — and
//! `Clowd.Ui` reads it (`SessionInfo`, MIGRATION.md §2.11). One definition
//! here means a field added on one side cannot go missing on the other;
//! the authoritative prose description is CAPTURE_PROTOCOL.md.
//!
//! Two rules the writers share and this module cannot enforce: `session.json`
//! is written **last** (its presence is the shell's completion signal), and
//! paths in it are absolute (hence [`absolute_path`]).
//!
//! A scrolling capture never has a cursor, so its two fields are skipped
//! when absent rather than being required of every writer.

use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::Serialize;

use crate::geometry::ScreenRect;

/// Serialized shape of `session.json`. PascalCase keys to match what
/// Newtonsoft.Json expects on the C# side.
#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct SessionJson {
    pub created_utc: String,
    pub name: &'static str,
    pub desktop_img_path: String,
    pub preview_img_path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cursor_img_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cursor_position: Option<RectJson>,
    pub cropped_rect: RectJson,
    pub original_bounds: RectJson,
}

/// Serialized shape of `Clowd.PlatformUtil.ScreenRect` (exact key
/// casing, §2.11).
#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct RectJson {
    x: i32,
    y: i32,
    width: i32,
    height: i32,
}

impl From<ScreenRect> for RectJson {
    fn from(r: ScreenRect) -> Self {
        Self {
            x: r.min_x(),
            y: r.min_y(),
            width: r.width(),
            height: r.height(),
        }
    }
}

pub fn save_png(path: &Path, rgba: Vec<u8>, width: u32, height: u32) -> anyhow::Result<()> {
    let img: image::RgbaImage =
        image::ImageBuffer::from_raw(width, height, rgba).ok_or_else(|| anyhow::anyhow!("pixel buffer size mismatch"))?;
    img.save_with_format(path, image::ImageFormat::Png)?;
    Ok(())
}

/// Best-effort absolute path without `std::path::absolute` (stabilized
/// after our MSRV). The session dir is normally already absolute.
pub fn absolute_path(p: &Path) -> PathBuf {
    if p.is_absolute() {
        p.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|d| d.join(p))
            .unwrap_or_else(|_| p.to_path_buf())
    }
}

pub fn created_utc_now() -> String {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    format_iso8601_utc(secs)
}

/// Format Unix seconds as ISO 8601 UTC (`2026-06-12T18:30:00Z`) — a
/// shape Newtonsoft.Json parses into `DateTime` directly. Uses the
/// days-to-civil algorithm (Howard Hinnant) to avoid a date-time crate.
fn format_iso8601_utc(unix_secs: u64) -> String {
    let days = (unix_secs / 86_400) as i64;
    let rem = unix_secs % 86_400;
    let (hh, mm, ss) = (rem / 3600, (rem % 3600) / 60, rem % 60);

    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + if m <= 2 { 1 } else { 0 };

    format!("{:04}-{:02}-{:02}T{:02}:{:02}:{:02}Z", y, m, d, hh, mm, ss)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::geometry::RectExt;

    #[test]
    fn iso8601_epoch() {
        assert_eq!(format_iso8601_utc(0), "1970-01-01T00:00:00Z");
    }

    #[test]
    fn iso8601_known_timestamp() {
        // 2001-09-09T01:46:40Z is the well-known 10^9 Unix timestamp.
        assert_eq!(format_iso8601_utc(1_000_000_000), "2001-09-09T01:46:40Z");
    }

    #[test]
    fn iso8601_leap_year_day() {
        // 2024-02-29T12:00:00Z
        assert_eq!(format_iso8601_utc(1_709_208_000), "2024-02-29T12:00:00Z");
    }

    #[test]
    fn rect_json_shape() {
        let r: RectJson = ScreenRect::from_xy_size(-10, 20, 300, 400).into();
        assert_eq!(serde_json::to_string(&r).unwrap(), r#"{"X":-10,"Y":20,"Width":300,"Height":400}"#);
    }

    #[test]
    fn session_json_omits_cursor_when_absent() {
        let info = SessionJson {
            created_utc: "2026-01-01T00:00:00Z".to_string(),
            name: "Screenshot",
            desktop_img_path: "C:\\s\\desktop.png".to_string(),
            preview_img_path: "C:\\s\\cropped.png".to_string(),
            cursor_img_path: None,
            cursor_position: None,
            cropped_rect: ScreenRect::from_xy_size(0, 0, 10, 10).into(),
            original_bounds: ScreenRect::from_xy_size(5, 5, 10, 10).into(),
        };
        let json = serde_json::to_string(&info).unwrap();
        assert!(!json.contains("CursorImgPath"));
        assert!(!json.contains("CursorPosition"));
        assert!(json.contains(r#""DesktopImgPath":"C:\\s\\desktop.png""#));
    }
}
