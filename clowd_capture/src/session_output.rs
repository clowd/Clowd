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

use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::Serialize;

use crate::capture_output::ActionResult;
use crate::geometry::{RectExt, ScreenPoint, ScreenRect};
use crate::image_extract::{composite_cursor_rgba, extract_selection_rgba, extract_selection_rgba_with_peek};
use crate::system::{virtual_desktop_bounds, CapturedDesktop, CursorImage, MonitorInfo, WindowPeekImage};

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
    let origin = ScreenPoint::new(selection.min_x(), selection.min_y());
    match monitor_for_selection(selection, monitors) {
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

/// The monitor a selection belongs to: the one under its top-left corner,
/// else the first it overlaps, else the first monitor. Shared by the rect
/// and point mappings so an action line never mixes two monitors' scales.
#[cfg(target_os = "macos")]
fn monitor_for_selection(selection: ScreenRect, monitors: &[MonitorInfo]) -> Option<&MonitorInfo> {
    let origin = ScreenPoint::new(selection.min_x(), selection.min_y());
    monitors
        .iter()
        .find(|m| m.bounds.contains(origin))
        .or_else(|| {
            monitors
                .iter()
                .find(|m| m.bounds.intersects(&selection))
        })
        .or_else(|| monitors.first())
}

/// Write a SCROLL action payload: an `action.txt` = `scroll X,Y,W,H
/// PX,PY HWND` marker and nothing else. The scrolling capture driver
/// produces every image and the session itself, so the overlay writes no
/// `cropped.png` and no `session.json` — the marker alone is both the
/// payload and the completion signal.
///
/// `PX,PY` is the point the driver parks the cursor at and aims wheel
/// events from; it is clamped into the emitted rect so the wheel can
/// never land outside the region being stitched. `HWND` is the top-level
/// window handle under that point as a decimal integer, or `0` when the
/// walker could not resolve one — the driver then falls back to
/// `WindowFromPoint` at drive time.
///
/// Rect and point are both emitted in the platform capture coordinate
/// space (DESIGN §1.1), the same mapping the `video` marker uses:
/// physical virtual-desktop pixels on Windows, CG points on macOS.
pub fn write_scroll_action(
    session_dir: &Path,
    selection: ScreenRect,
    point: ScreenPoint,
    hwnd: isize,
    monitors: &[MonitorInfo],
) -> ActionResult {
    match write_scroll_action_inner(session_dir, selection, point, hwnd, monitors) {
        Ok(action_path) => {
            log::info!("scroll action written to {:?}", action_path);
            ActionResult::Success
        }
        Err(e) => {
            log::error!("scroll action write failed: {e:#}");
            ActionResult::Failed(format!("Failed to write scroll action: {e}"))
        }
    }
}

fn write_scroll_action_inner(
    session_dir: &Path,
    selection: ScreenRect,
    point: ScreenPoint,
    hwnd: isize,
    monitors: &[MonitorInfo],
) -> anyhow::Result<PathBuf> {
    // The line is built first: an off-desktop selection must fail before
    // any directory appears, so the shell never sees a half-populated dir.
    let line = scroll_action_line(selection, point, hwnd, monitors)?;
    std::fs::create_dir_all(session_dir)?;
    let action_path = session_dir.join(ACTION_FILE);
    std::fs::write(&action_path, line)?;
    Ok(action_path)
}

/// The exact `action.txt` line for a SCROLL action, including its
/// trailing newline. Split out from the writer so the wire format —
/// which `CaptureSessionDispatcher` parses field-by-field — is unit
/// testable without touching the filesystem.
///
/// The selection is clamped to the virtual desktop first, exactly as the
/// video and screenshot writers clamp to the desktop bitmap. Window snaps
/// report unclamped DWM frame bounds, so a window hanging off a monitor
/// edge yields a rect the driver would BitBlt verbatim on every frame:
/// the off-screen band is undefined (black) in all of them, and being
/// pixel-static across every consecutive pair the stitcher would classify
/// it as sticky chrome and crop real content in its place.
fn scroll_action_line(selection: ScreenRect, point: ScreenPoint, hwnd: isize, monitors: &[MonitorInfo]) -> anyhow::Result<String> {
    let desktop = virtual_desktop_bounds(monitors);
    let selection = selection
        .intersection(&desktop)
        .ok_or_else(|| anyhow!("selection {:?} does not intersect desktop bounds {:?}", selection, desktop))?;
    let (x, y, w, h) = video_action_rect(selection, monitors);
    // Clamped rect, not the original: a point picked in the part of the
    // selection that hung off the desktop must land somewhere the driver
    // will actually capture and can actually park the cursor.
    let (px, py) = scroll_action_point(clamp_point_into(point, selection), monitors);
    Ok(format!("scroll {x},{y},{w},{h} {px},{py} {hwnd}\n"))
}

/// Clamp a point into `rect`'s last addressable pixel row/column. A
/// zero-sized rect (never produced by the selection machinery, but cheap
/// to survive) collapses onto its origin rather than panicking.
fn clamp_point_into(point: ScreenPoint, rect: ScreenRect) -> ScreenPoint {
    let clamp = |v: i32, min: i32, max: i32| v.clamp(min, max.max(min));
    ScreenPoint::new(
        clamp(point.x, rect.min_x(), rect.max_x() - 1),
        clamp(point.y, rect.min_y(), rect.max_y() - 1),
    )
}

/// Windows: the scroll point verbatim — the same space `SetCursorPos`
/// takes (physical px, virtual desktop).
#[cfg(not(target_os = "macos"))]
fn scroll_action_point(point: ScreenPoint, _monitors: &[MonitorInfo]) -> (i32, i32) {
    (point.x, point.y)
}

/// macOS: physical pixels → CG points through the monitor the point sits
/// on, matching [`video_action_rect`]'s conversion. Untested;
/// compile-guarded (DESIGN §3.2).
#[cfg(target_os = "macos")]
fn scroll_action_point(point: ScreenPoint, monitors: &[MonitorInfo]) -> (i32, i32) {
    match monitor_for_selection(ScreenRect::from_xy_size(point.x, point.y, 1, 1), monitors) {
        Some(m) => {
            let p = m.screen_to_logical(point);
            (p.x.round() as i32, p.y.round() as i32)
        }
        None => (point.x, point.y),
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
            desktop_job
                .join()
                .expect("desktop.png job panicked"),
            cursor_job
                .join()
                .expect("cursor.png job panicked"),
            preview_job
                .join()
                .expect("cropped.png job panicked"),
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

    let info = SessionJson {
        created_utc: created_utc_now(),
        name: "Screenshot",
        desktop_img_path: desktop_path.to_string_lossy().into_owned(),
        preview_img_path: preview_path.to_string_lossy().into_owned(),
        cursor_img_path: cursor_entry
            .as_ref()
            .map(|(p, _)| p.to_string_lossy().into_owned()),
        cursor_position: cursor_entry.as_ref().map(|(_, r)| {
            r.translate(euclid::Vector2D::new(-origin.x, -origin.y))
                .into()
        }),
        cropped_rect: cropped_rect.into(),
        original_bounds: selection.into(),
    };

    let json_path = session_dir.join("session.json");
    std::fs::write(&json_path, serde_json::to_string_pretty(&info)?)?;
    Ok(json_path)
}

/// Serialized shape of `session.json`, shared with `Clowd.Ui`
/// (`SessionInfo`, MIGRATION.md §2.11) and documented in
/// CAPTURE_PROTOCOL.md — keys are PascalCase to match what
/// Newtonsoft.Json expects there.
///
/// `pub(crate)` because the scrolling-capture driver writes a session of
/// its own (`scroll::output`) and there must be exactly one definition of
/// this contract in the binary.
#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
pub(crate) struct SessionJson {
    pub(crate) created_utc: String,
    pub(crate) name: &'static str,
    pub(crate) desktop_img_path: String,
    pub(crate) preview_img_path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub(crate) cursor_img_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub(crate) cursor_position: Option<RectJson>,
    pub(crate) cropped_rect: RectJson,
    pub(crate) original_bounds: RectJson,
}

/// Serialized shape of `Clowd.PlatformUtil.ScreenRect` (exact key
/// casing, §2.11).
#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
pub(crate) struct RectJson {
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

/// Extract a region of the desktop bitmap as RGBA, compositing the
/// locked peek window when present.
fn extract_region(region: ScreenRect, buffer: &CapturedDesktop, peek: Option<&WindowPeekImage>) -> Option<(Vec<u8>, u32, u32)> {
    match peek {
        Some(p) => extract_selection_rgba_with_peek(region, buffer, p),
        None => extract_selection_rgba(region, buffer),
    }
}

pub(crate) fn save_png(path: &Path, rgba: Vec<u8>, width: u32, height: u32) -> anyhow::Result<()> {
    let img: image::RgbaImage = image::ImageBuffer::from_raw(width, height, rgba).ok_or_else(|| anyhow!("pixel buffer size mismatch"))?;
    img.save_with_format(path, image::ImageFormat::Png)?;
    Ok(())
}

/// Best-effort absolute path without `std::path::absolute` (stabilised
/// after our MSRV). The session dir is normally already absolute.
pub(crate) fn absolute_path(p: &Path) -> PathBuf {
    if p.is_absolute() {
        p.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|d| d.join(p))
            .unwrap_or_else(|_| p.to_path_buf())
    }
}

pub(crate) fn created_utc_now() -> String {
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

    /// A monitor whose logical origin matches its physical one, so the
    /// macOS point/rect conversion is the identity and these tests assert
    /// the same wire bytes on both platforms.
    fn monitor(x: i32, y: i32, w: i32, h: i32) -> MonitorInfo {
        MonitorInfo {
            bounds: ScreenRect::from_xy_size(x, y, w, h),
            scale_factor: 1.0,
            is_primary: true,
            refresh_hz: 60.0,
            name: "test".to_string(),
            adapter_id: None,
            #[cfg(target_os = "macos")]
            logical_origin: crate::geometry::LogicalPoint::new(x as f64, y as f64),
        }
    }

    /// The `scroll` marker is a wire contract with `CaptureSessionDispatcher`:
    /// one line, three space-separated groups, trailing newline.
    #[test]
    fn scroll_action_line_format() {
        let line = scroll_action_line(
            ScreenRect::from_xy_size(100, 200, 800, 600),
            ScreenPoint::new(450, 500),
            123456,
            &[monitor(0, 0, 1920, 1080)],
        )
        .unwrap();
        assert_eq!(line, "scroll 100,200,800,600 450,500 123456\n");
    }

    #[test]
    fn scroll_action_line_negative_virtual_desktop() {
        // Secondary monitor left of/above primary: both the rect origin and
        // the scroll point are negative and must survive verbatim — the
        // desktop clamp may not mistake negative for off-screen.
        let line = scroll_action_line(
            ScreenRect::from_xy_size(-1920, -300, 1000, 900),
            ScreenPoint::new(-1500, -100),
            0,
            &[monitor(-1920, -1080, 1920, 1080), monitor(0, 0, 1920, 1080)],
        )
        .unwrap();
        assert_eq!(line, "scroll -1920,-300,1000,900 -1500,-100 0\n");
    }

    #[test]
    fn scroll_action_point_clamped_into_selection() {
        let monitors = [monitor(-1920, 0, 1920, 1080), monitor(0, 0, 1920, 1080)];
        let sel = ScreenRect::from_xy_size(-100, 50, 200, 100);
        // Far outside on both axes in both directions.
        assert_eq!(
            scroll_action_line(sel, ScreenPoint::new(-9999, -9999), 7, &monitors).unwrap(),
            "scroll -100,50,200,100 -100,50 7\n"
        );
        // The clamp lands on the last addressable pixel, not one past it.
        assert_eq!(
            scroll_action_line(sel, ScreenPoint::new(9999, 9999), 7, &monitors).unwrap(),
            "scroll -100,50,200,100 99,149 7\n"
        );
    }

    /// A window snapped at a monitor edge reports frame bounds that hang
    /// off the desktop; the driver BitBlts the emitted rect verbatim, so
    /// what it cannot see must never be asked for.
    #[test]
    fn scroll_action_clamps_selection_to_desktop() {
        let line = scroll_action_line(
            ScreenRect::from_xy_size(1800, 900, 400, 400),
            ScreenPoint::new(1900, 1000),
            5,
            &[monitor(0, 0, 1920, 1080)],
        )
        .unwrap();
        assert_eq!(line, "scroll 1800,900,120,180 1900,1000 5\n");
    }

    /// A point picked in the part of the selection that fell off the
    /// desktop is re-clamped into the clamped rect, not the original one.
    #[test]
    fn scroll_action_point_clamped_into_clamped_rect() {
        let line = scroll_action_line(
            ScreenRect::from_xy_size(1800, 900, 400, 400),
            ScreenPoint::new(2100, 1200),
            5,
            &[monitor(0, 0, 1920, 1080)],
        )
        .unwrap();
        assert_eq!(line, "scroll 1800,900,120,180 1919,1079 5\n");
    }

    #[test]
    fn scroll_action_fully_off_desktop_fails() {
        let monitors = [monitor(0, 0, 1920, 1080)];
        assert!(scroll_action_line(
            ScreenRect::from_xy_size(2000, 0, 100, 100),
            ScreenPoint::new(2050, 50),
            5,
            &monitors
        )
        .is_err());

        // …and the failure reaches the caller as a retry/cancel-able
        // ActionResult, leaving no session directory behind.
        let dir = temp_session_dir();
        let result = write_scroll_action(
            &dir,
            ScreenRect::from_xy_size(2000, 0, 100, 100),
            ScreenPoint::new(2050, 50),
            5,
            &monitors,
        );
        match result {
            ActionResult::Failed(msg) => assert!(msg.starts_with("Failed to write scroll action:"), "unexpected message: {msg}"),
            _ => panic!("expected Failed"),
        }
        assert!(!dir.exists());
    }

    fn temp_session_dir() -> PathBuf {
        std::env::temp_dir().join(format!(
            "clowd_scroll_action_{}_{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ))
    }

    /// The whole payload is one file: the shell keys the completion of a
    /// SCROLL capture off `action.txt` alone (no poster, no session.json).
    #[test]
    fn scroll_action_writes_only_action_txt() {
        let dir = temp_session_dir();
        let result = write_scroll_action(
            &dir,
            ScreenRect::from_xy_size(0, 0, 640, 480),
            ScreenPoint::new(10, 20),
            -1,
            &[monitor(0, 0, 1920, 1080)],
        );
        assert!(matches!(result, ActionResult::Success));

        let mut names: Vec<String> = std::fs::read_dir(&dir)
            .unwrap()
            .map(|e| {
                e.unwrap()
                    .file_name()
                    .to_string_lossy()
                    .into_owned()
            })
            .collect();
        names.sort();
        assert_eq!(names, vec![ACTION_FILE.to_string()]);
        assert_eq!(
            std::fs::read_to_string(dir.join(ACTION_FILE)).unwrap(),
            "scroll 0,0,640,480 10,20 -1\n"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }
}
