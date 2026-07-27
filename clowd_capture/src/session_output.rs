//! Session payload writer — used when the capturer is launched by the
//! Clowd UI shell with `--session-dir <path>`.
//!
//! Mirrors `DxScreenCapture::SaveSession` (clowd_capture_dx/DxScreenCapture.cpp:1980):
//! writes `desktop.png` (full virtual-desktop bitmap, no cursor),
//! `cursor.png` (desktop crop at the cursor rect with the cursor
//! composited), `cropped.png` (preview of the selection) and
//! `session.json` into the session directory. The JSON schema is shared
//! with `Clowd.Ui` (`SessionInfo`, MIGRATION.md §2.11) and documented in
//! CAPTURE_PROTOCOL.md at the repo root.
//!
//! Actions the shell must perform are signalled through an `action.txt`
//! sidecar in the same directory: `upload` (session payload present,
//! upload instead of edit) or `select-color #RRGGBB` (no session
//! payload). No file means edit — the historical default.
//!
//! The repo intentionally has no serde; the fixed-schema JSON is written
//! by hand below.

use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use crate::capture_output::ActionResult;
use crate::geometry::{RectExt, ScreenRect};
use crate::image_extract::{composite_cursor_rgba, extract_selection_rgba, extract_selection_rgba_with_peek};
use crate::system::{CapturedDesktop, CursorImage, MonitorInfo, WindowPeekImage};

/// Name of the sidecar file the shell reads to route the finished
/// capture. Matches `CaptureSessionDispatcher` in Clowd.Ui.
const ACTION_FILE: &str = "action.txt";

/// Which shell action a session payload is for. `Edit` is the default
/// and writes no marker, so shells that pre-date `action.txt` behave
/// unchanged; `Upload` writes the marker alongside the payload.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionAction {
    Edit,
    Upload,
}

/// Write the full session payload into `session_dir`. Returns
/// `ActionResult` so the app can route failures through the same
/// retry/cancel dialog used by Copy and Save.
pub fn write_session(
    session_dir: &Path,
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    peek: Option<&WindowPeekImage>,
    cursor_visible: bool,
    action: SessionAction,
) -> ActionResult {
    match write_session_inner(session_dir, selection, buffer, peek, cursor_visible, action) {
        Ok(json_path) => {
            log::info!("session written to {:?}", json_path);
            ActionResult::Success
        }
        Err(e) => {
            log::error!("session write failed: {e:#}");
            ActionResult::Failed(format!("Failed to write session: {e}"))
        }
    }
}

/// Write only the `action.txt` marker for a SELECT-COLOR capture: the
/// shell opens its colour viewer with this colour and deletes the
/// directory — no session payload is produced.
pub fn write_color_action(session_dir: &Path, r: u8, g: u8, b: u8) -> ActionResult {
    let write = std::fs::create_dir_all(session_dir)
        .and_then(|_| std::fs::write(session_dir.join(ACTION_FILE), format!("select-color #{r:02X}{g:02X}{b:02X}\n")));
    match write {
        Ok(()) => {
            log::info!("color action written to {:?}", session_dir.join(ACTION_FILE));
            ActionResult::Success
        }
        Err(e) => {
            log::error!("color action write failed: {e:#}");
            ActionResult::Failed(format!("Failed to write color: {e}"))
        }
    }
}

/// Write a VIDEO action payload: `cropped.png` (the recording's poster
/// frame) plus an `action.txt` = `video X,Y,W,H` marker written LAST so
/// its appearance is the completion signal. No `desktop.png` and no
/// `session.json` — the session is created by Clowd.Ui when recording
/// finishes (DESIGN §3.2).
///
/// Unlike the screenshot path this **never composites peeked windows**:
/// obs-express records the real screen (obstructions included), so a
/// peek-composited poster would show content the video does not.
///
/// The rect in `action.txt` is emitted in the platform capture
/// coordinate space (DESIGN §1.1): physical pixels (virtual-desktop,
/// NOT origin-shifted) on Windows, CG points on macOS — so Clowd.Ui
/// passes it through verbatim to obs-express `--region`.
pub fn write_video_action(
    session_dir: &Path,
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    cursor_visible: bool,
    monitors: &[MonitorInfo],
) -> ActionResult {
    match write_video_action_inner(session_dir, selection, buffer, cursor_visible, monitors) {
        Ok(action_path) => {
            log::info!("video action written to {:?}", action_path);
            ActionResult::Success
        }
        Err(e) => {
            log::error!("video action write failed: {e:#}");
            ActionResult::Failed(format!("Failed to write video action: {e}"))
        }
    }
}

fn write_video_action_inner(
    session_dir: &Path,
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    cursor_visible: bool,
    monitors: &[MonitorInfo],
) -> anyhow::Result<PathBuf> {
    std::fs::create_dir_all(session_dir)?;
    let session_dir = absolute_path(session_dir);

    // Selection clamped to the desktop bitmap, then grown to the contract
    // minimum of 2×2 — this is the region the poster contains and the rect
    // obs-express records (obs-express rejects W,H < 2 with exit 2, §1.1).
    let selection = selection
        .intersection(&buffer.bounds)
        .ok_or_else(|| anyhow!("selection {:?} does not intersect desktop bounds {:?}", selection, buffer.bounds))?;
    let selection = ensure_min_video_size(selection, buffer.bounds)?;

    // cropped.png — poster frame. No peek compositing (see doc above);
    // cursor included only if the user has it visible, matching the
    // screenshot preview.
    let preview_path = session_dir.join("cropped.png");
    {
        let (mut rgba, w, h) = extract_selection_rgba(selection, buffer).ok_or_else(|| anyhow!("failed to extract selection preview"))?;
        if cursor_visible {
            if let Some(cur) = buffer.cursor.as_ref() {
                composite_cursor_rgba(&mut rgba, w, h, selection, cur);
            }
        }
        save_png(&preview_path, rgba, w, h)?;
    }

    // action.txt — written LAST so its presence is the completion signal.
    let (x, y, w, h) = video_action_rect(selection, monitors);
    let action_path = session_dir.join(ACTION_FILE);
    std::fs::write(&action_path, format!("video {x},{y},{w},{h}\n"))?;
    Ok(action_path)
}

/// Grow a clamped selection to the §1.1 contract minimum of 2×2 (a drag only
/// needs one axis to cross the threshold, so 300×1 slivers are reachable),
/// staying inside `bounds`. Errors when `bounds` itself cannot fit 2×2 — the
/// same failure path as a selection outside the desktop.
fn ensure_min_video_size(selection: ScreenRect, bounds: ScreenRect) -> anyhow::Result<ScreenRect> {
    const MIN: i32 = 2;
    if selection.width() >= MIN && selection.height() >= MIN {
        return Ok(selection);
    }
    if bounds.width() < MIN || bounds.height() < MIN {
        return Err(anyhow!("desktop bounds {:?} cannot fit a {}x{} capture region", bounds, MIN, MIN));
    }
    // Extend forward to MIN, shifting back if that runs past the bounds edge.
    let grow = |min: i32, len: i32, b_min: i32, b_max: i32| -> (i32, i32) {
        if len >= MIN {
            (min, len)
        } else {
            (min.min(b_max - MIN).max(b_min), MIN)
        }
    };
    let (x, w) = grow(selection.min_x(), selection.width(), bounds.min_x(), bounds.max_x());
    let (y, h) = grow(selection.min_y(), selection.height(), bounds.min_y(), bounds.max_y());
    Ok(ScreenRect::from_xy_size(x, y, w, h))
}

/// The `action.txt` rect in the platform capture coordinate space
/// (DESIGN §1.1). Windows: the clamped selection verbatim (physical px,
/// virtual desktop).
#[cfg(not(target_os = "macos"))]
fn video_action_rect(selection: ScreenRect, _monitors: &[MonitorInfo]) -> (i32, i32, i32, i32) {
    (selection.min_x(), selection.min_y(), selection.width(), selection.height())
}

/// macOS: convert the physical-pixel selection to CG points via the
/// monitor under its top-left corner (ScreenUnit is physical px on both
/// platforms — emitting raw ScreenUnit on a Retina display would hand
/// obs-express a 2× rect). Untested; compile-guarded (DESIGN §3.2).
#[cfg(target_os = "macos")]
fn video_action_rect(selection: ScreenRect, monitors: &[MonitorInfo]) -> (i32, i32, i32, i32) {
    let origin = crate::geometry::ScreenPoint::new(selection.min_x(), selection.min_y());
    let monitor = monitors
        .iter()
        .find(|m| m.bounds.contains(origin))
        .or_else(|| {
            monitors
                .iter()
                .find(|m| m.bounds.intersects(&selection))
        })
        .or_else(|| monitors.first());
    match monitor {
        Some(m) => {
            let tl = m.screen_to_logical(origin);
            let size = m.physical_to_logical_size(selection.width() as u32, selection.height() as u32);
            (
                tl.x.round() as i32,
                tl.y.round() as i32,
                size.width.round() as i32,
                size.height.round() as i32,
            )
        }
        None => (selection.min_x(), selection.min_y(), selection.width(), selection.height()),
    }
}

fn write_session_inner(
    session_dir: &Path,
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    peek: Option<&WindowPeekImage>,
    cursor_visible: bool,
    action: SessionAction,
) -> anyhow::Result<PathBuf> {
    std::fs::create_dir_all(session_dir)?;
    let session_dir = absolute_path(session_dir);

    // Selection clamped to the desktop bitmap; this is the region the
    // preview contains and what CroppedRect must describe.
    let selection = selection
        .intersection(&buffer.bounds)
        .ok_or_else(|| anyhow!("selection {:?} does not intersect desktop bounds {:?}", selection, buffer.bounds))?;

    // The three image extract+encode jobs are independent; run them on
    // scoped threads so the wall time is the largest single encode
    // (desktop.png) rather than the sum. session.json stays strictly
    // after the join, preserving the "payload appears last" protocol.
    let desktop_path = session_dir.join("desktop.png");
    let preview_path = session_dir.join("cropped.png");
    let (desktop_res, cursor_res, preview_res) = std::thread::scope(|s| {
        // desktop.png — the full virtual-desktop bitmap with the locked
        // peek window composited (matches Dx GetCombinedBitmap(merge, no
        // crop)), but never the cursor: the editor toggles cursor
        // visibility itself.
        let desktop_job = s.spawn(|| -> anyhow::Result<()> {
            let (rgba, w, h) = extract_region(buffer.bounds, buffer, peek).ok_or_else(|| anyhow!("failed to extract desktop bitmap"))?;
            save_png(&desktop_path, rgba, w, h)
        });

        // cursor.png — desktop crop at the cursor rect with the cursor
        // composited over it (matches Dx GetCursorBitmap). Skipped when
        // no cursor was captured or the OS reported it hidden.
        let cursor_job = s.spawn(|| -> anyhow::Result<Option<(PathBuf, ScreenRect)>> {
            let Some(cur) = buffer.cursor.as_ref().filter(|c| c.visible) else {
                return Ok(None);
            };
            let (cw, ch) = match &cur.image {
                CursorImage::AlphaBlended {
                    width,
                    height,
                    ..
                } => (*width, *height),
                CursorImage::Masked {
                    width,
                    height,
                    ..
                } => (*width, *height),
            };
            let cursor_rect =
                ScreenRect::from_xy_size(cur.position.x - cur.hotspot_x, cur.position.y - cur.hotspot_y, cw as i32, ch as i32);
            let Some(clamped) = cursor_rect.intersection(&buffer.bounds) else {
                return Ok(None);
            };
            let Some((mut rgba, w, h)) = extract_selection_rgba(clamped, buffer) else {
                return Ok(None);
            };
            composite_cursor_rgba(&mut rgba, w, h, clamped, cur);
            let cursor_path = session_dir.join("cursor.png");
            save_png(&cursor_path, rgba, w, h)?;
            Ok(Some((cursor_path, clamped)))
        });

        // cropped.png — preview of the selection, peek composited, cursor
        // included only if the user has it visible (Dx `copyCursor`).
        let preview_job = s.spawn(|| -> anyhow::Result<()> {
            let (mut rgba, w, h) = extract_region(selection, buffer, peek).ok_or_else(|| anyhow!("failed to extract selection preview"))?;
            if cursor_visible {
                if let Some(cur) = buffer.cursor.as_ref() {
                    composite_cursor_rgba(&mut rgba, w, h, selection, cur);
                }
            }
            save_png(&preview_path, rgba, w, h)
        });

        (
            desktop_job.join().expect("desktop.png job panicked"),
            cursor_job.join().expect("cursor.png job panicked"),
            preview_job.join().expect("cropped.png job panicked"),
        )
    });
    desktop_res?;
    preview_res?;
    let cursor_entry: Option<(PathBuf, ScreenRect)> = cursor_res?;

    // action.txt — routing marker, written before session.json so the
    // payload stays the last file to appear. Edit removes any marker a
    // previously failed-and-retried action left behind.
    match action {
        SessionAction::Upload => std::fs::write(session_dir.join(ACTION_FILE), "upload\n")?,
        SessionAction::Edit => match std::fs::remove_file(session_dir.join(ACTION_FILE)) {
            Ok(()) => {}
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(e.into()),
        },
    }

    // session.json — written last; the shell treats its presence as the
    // success signal (missing file = capture cancelled).
    let origin = buffer.bounds.origin;
    let cropped_rect = selection.translate(euclid::Vector2D::new(-origin.x, -origin.y));

    let mut json = String::new();
    json.push_str("{\n");
    json.push_str(&format!("    \"CreatedUtc\": \"{}\",\n", created_utc_now()));
    json.push_str("    \"Name\": \"Screenshot\",\n");
    json.push_str(&format!(
        "    \"DesktopImgPath\": \"{}\",\n",
        json_escape(&desktop_path.to_string_lossy())
    ));
    json.push_str(&format!(
        "    \"PreviewImgPath\": \"{}\",\n",
        json_escape(&preview_path.to_string_lossy())
    ));
    if let Some((cursor_path, cursor_rect)) = &cursor_entry {
        let pos = cursor_rect.translate(euclid::Vector2D::new(-origin.x, -origin.y));
        json.push_str(&format!(
            "    \"CursorImgPath\": \"{}\",\n",
            json_escape(&cursor_path.to_string_lossy())
        ));
        json.push_str(&format!("    \"CursorPosition\": {},\n", rect_json(pos)));
    }
    json.push_str(&format!("    \"CroppedRect\": {},\n", rect_json(cropped_rect)));
    json.push_str(&format!("    \"OriginalBounds\": {}\n", rect_json(selection)));
    json.push_str("}\n");

    let json_path = session_dir.join("session.json");
    std::fs::write(&json_path, json)?;
    Ok(json_path)
}

/// Extract a region of the desktop bitmap as RGBA, compositing the
/// locked peek window when present.
fn extract_region(region: ScreenRect, buffer: &CapturedDesktop, peek: Option<&WindowPeekImage>) -> Option<(Vec<u8>, u32, u32)> {
    match peek {
        Some(p) => extract_selection_rgba_with_peek(region, buffer, p),
        None => extract_selection_rgba(region, buffer),
    }
}

fn save_png(path: &Path, rgba: Vec<u8>, width: u32, height: u32) -> anyhow::Result<()> {
    let img: image::RgbaImage = image::ImageBuffer::from_raw(width, height, rgba).ok_or_else(|| anyhow!("pixel buffer size mismatch"))?;
    img.save_with_format(path, image::ImageFormat::Png)?;
    Ok(())
}

/// Best-effort absolute path without `std::path::absolute` (stabilised
/// after our MSRV). The session dir is normally already absolute.
fn absolute_path(p: &Path) -> PathBuf {
    if p.is_absolute() {
        p.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|d| d.join(p))
            .unwrap_or_else(|_| p.to_path_buf())
    }
}

/// `{ "X": …, "Y": …, "Width": …, "Height": … }` — the serialized shape
/// of `Clowd.PlatformUtil.ScreenRect` (exact key casing, §2.11).
fn rect_json(r: ScreenRect) -> String {
    format!(
        "{{ \"X\": {}, \"Y\": {}, \"Width\": {}, \"Height\": {} }}",
        r.min_x(),
        r.min_y(),
        r.width(),
        r.height()
    )
}

/// Escape a string for embedding in a JSON string literal (backslashes
/// in Windows paths, quotes, control characters).
fn json_escape(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 8);
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out
}

fn created_utc_now() -> String {
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
    fn json_escape_windows_path() {
        assert_eq!(
            json_escape("C:\\Users\\me\\session \"1\""),
            "C:\\\\Users\\\\me\\\\session \\\"1\\\""
        );
    }

    #[test]
    fn rect_json_shape() {
        let r = ScreenRect::from_xy_size(-10, 20, 300, 400);
        assert_eq!(rect_json(r), "{ \"X\": -10, \"Y\": 20, \"Width\": 300, \"Height\": 400 }");
    }

    #[test]
    fn min_video_size_passthrough() {
        let bounds = ScreenRect::from_xy_size(0, 0, 1920, 1080);
        let sel = ScreenRect::from_xy_size(10, 10, 300, 200);
        assert_eq!(ensure_min_video_size(sel, bounds).unwrap(), sel);
    }

    #[test]
    fn min_video_size_grows_sliver() {
        let bounds = ScreenRect::from_xy_size(0, 0, 1920, 1080);
        let sel = ScreenRect::from_xy_size(10, 10, 300, 1);
        let grown = ensure_min_video_size(sel, bounds).unwrap();
        assert_eq!((grown.min_x(), grown.min_y(), grown.width(), grown.height()), (10, 10, 300, 2));
    }

    #[test]
    fn min_video_size_shifts_at_bounds_edge() {
        let bounds = ScreenRect::from_xy_size(0, 0, 1920, 1080);
        // 1x1 selection in the bottom-right corner: must shift back inside.
        let sel = ScreenRect::from_xy_size(1919, 1079, 1, 1);
        let grown = ensure_min_video_size(sel, bounds).unwrap();
        assert_eq!((grown.min_x(), grown.min_y(), grown.width(), grown.height()), (1918, 1078, 2, 2));
    }

    #[test]
    fn min_video_size_negative_virtual_desktop() {
        // Secondary monitor left of primary: negative coords.
        let bounds = ScreenRect::from_xy_size(-1920, 0, 3840, 1080);
        let sel = ScreenRect::from_xy_size(-1920, 500, 1, 300);
        let grown = ensure_min_video_size(sel, bounds).unwrap();
        assert_eq!((grown.min_x(), grown.min_y(), grown.width(), grown.height()), (-1920, 500, 2, 300));
    }

    #[test]
    fn min_video_size_degenerate_desktop_errors() {
        let bounds = ScreenRect::from_xy_size(0, 0, 1, 1);
        let sel = ScreenRect::from_xy_size(0, 0, 1, 1);
        assert!(ensure_min_video_size(sel, bounds).is_err());
    }
}
