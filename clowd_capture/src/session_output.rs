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
//! Actions the shell must perform are signaled through an `action.txt`
//! sidecar in the same directory: `upload` (session payload present,
//! upload instead of edit) or `select-color #RRGGBB` (no session
//! payload). No file means edit — the historical default.

use std::path::{Path, PathBuf};

use clowd_rust_core::geometry::{RectExt, ScreenPoint, ScreenRect};
use clowd_rust_core::session::{absolute_path, created_utc_now, save_png, SessionJson};

use crate::capture_output::ActionResult;
use crate::image_extract::{
    apply_rounded_corners, composite_cursor_rgba, corners_to_round, extract_selection_rgba, extract_selection_rgba_with_peek,
};
use crate::system::{virtual_desktop_bounds, CapturedDesktop, CursorImage, MonitorInfo, WindowPeekImage};

/// Name of the sidecar file the shell reads to route the finished
/// capture. Matches `CaptureSessionDispatcher` in Clowd.Ui.
const ACTION_FILE: &str = "action.txt";

/// Sidecar carrying the payload of an `ocr-upload` action: the recognized
/// text, verbatim UTF-8. Read by `ProcessFinishedSession` before the
/// session directory is deleted.
const OCR_TEXT_FILE: &str = "ocr.txt";

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
    corner_radius: f32,
    buffer: &CapturedDesktop,
    peek: Option<&WindowPeekImage>,
    cursor_visible: bool,
    action: SessionAction,
) -> ActionResult {
    match write_session_inner(session_dir, selection, corner_radius, buffer, peek, cursor_visible, action) {
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
/// shell opens its color viewer with this color and deletes the
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
/// frame) plus an `action.txt` = `video X,Y,W,H [R]` marker written LAST
/// so its appearance is the completion signal. No `desktop.png` and no
/// `session.json` — the session is created by Clowd.Ui when recording
/// finishes (DESIGN §3.2).
///
/// Unlike the screenshot path this **never composites peeked windows**:
/// obs-express records the real screen (obstructions included), so a
/// peek-composited poster would show content the video does not. Nor is
/// anything rounded: the recorder captures the raw region, and `R` is
/// metadata the video editor turns into a rounded-rect mask on the
/// screen track — the counterpart of `session.json`'s `CornerRadius`.
///
/// The rect in `action.txt` is emitted in the platform capture
/// coordinate space (DESIGN §1.1): physical pixels (virtual-desktop,
/// NOT origin-shifted) on Windows, CG points on macOS — so Clowd.Ui
/// passes it through verbatim to obs-express `--region`. `R` rides in
/// that same space, and is omitted entirely for a square selection.
pub fn write_video_action(
    session_dir: &Path,
    selection: ScreenRect,
    corner_radius: f32,
    buffer: &CapturedDesktop,
    cursor_visible: bool,
    monitors: &[MonitorInfo],
) -> ActionResult {
    match write_video_action_inner(session_dir, selection, corner_radius, buffer, cursor_visible, monitors) {
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
    corner_radius: f32,
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
    let radius = video_action_radius(corner_radius, selection, w, h);
    let action_path = session_dir.join(ACTION_FILE);
    let marker = if radius > 0.0 {
        format!("video {x},{y},{w},{h} {radius:.2}\n")
    } else {
        format!("video {x},{y},{w},{h}\n")
    };
    std::fs::write(&action_path, marker)?;
    Ok(action_path)
}

/// The picked window's corner radius in the marker's coordinate space, or 0
/// for a square selection. The rect was converted into that space by
/// [`video_action_rect`] (a no-op on Windows, physical px → CG points on
/// macOS); the radius follows by the same factor rather than repeating the
/// conversion, so the two can never disagree about which space they are in.
/// Capped at half the shorter side — the largest radius the region has room
/// for — so a tiny recording region cannot carry a nonsense curve.
fn video_action_radius(corner_radius: f32, selection: ScreenRect, w: i32, h: i32) -> f32 {
    if corner_radius <= 0.0 || selection.width() <= 0 {
        return 0.0;
    }
    let scaled = corner_radius * (w as f32 / selection.width() as f32);
    scaled.min(w.min(h) as f32 / 2.0).max(0.0)
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
/// on, matching [`video_action_rect`]'s conversion. CG points are what the
/// driver's `CGWarpMouseCursorPosition`, `kCGWindowBounds` and
/// `CGWindowListCreateImage` all take, so the marker hands it numbers it can
/// use unchanged (`clowd_scroll_driver`'s `input`/`frame` modules).
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

/// Write an OCR-UPLOAD action payload: `ocr.txt` holding the recognized
/// text and an `action.txt` = `ocr-upload` marker written LAST, so its
/// appearance is the completion signal (the same rule
/// [`write_video_action`] documents). No `cropped.png` and no
/// `session.json` — the shell uploads text through `UploadManager`, which
/// needs no session at all; uploading the image too would just duplicate
/// what the plain UPLOAD button already does.
///
/// The text rides in a sidecar rather than on the marker line because
/// `action.txt` markers are single-line and prefix-matched by
/// `CaptureSessionDispatcher`: a multi-line, arbitrary-content payload
/// cannot live there without inventing an escaping scheme, and the
/// recognized text is arbitrary by definition (newlines, `#`, commas, any
/// script). A separate file sidesteps the question entirely.
///
/// `text` is written byte-for-byte as UTF-8 with no BOM and no trailing
/// newline added: the caller has already joined the lines with `\n`, and
/// whatever it produced is what gets pasted.
pub fn write_ocr_upload_action(session_dir: &Path, text: &str) -> ActionResult {
    match write_ocr_upload_action_inner(session_dir, text) {
        Ok(action_path) => {
            // The text itself is never logged — it is the user's screen
            // contents and these logs are mirrored into Sentry.
            log::info!("ocr-upload action written to {:?} ({} bytes of text)", action_path, text.len());
            ActionResult::Success
        }
        Err(e) => {
            log::error!("ocr-upload action write failed: {e:#}");
            ActionResult::Failed(format!("Failed to write OCR upload action: {e}"))
        }
    }
}

fn write_ocr_upload_action_inner(session_dir: &Path, text: &str) -> anyhow::Result<PathBuf> {
    std::fs::create_dir_all(session_dir)?;
    let session_dir = absolute_path(session_dir);

    // ocr.txt first. `fs::write` of a &str emits its UTF-8 bytes and
    // nothing else — no BOM, which the C# side relies on: it reads the
    // file as UTF-8 and would otherwise paste a leading U+FEFF.
    std::fs::write(session_dir.join(OCR_TEXT_FILE), text)?;

    // action.txt last: a half-written payload is invisible to the shell
    // because the marker it watches for is not there yet, so a failure
    // above leaves a directory the shell simply ignores and later cleans
    // up — the same guarantee the video and screenshot writers give.
    let action_path = session_dir.join(ACTION_FILE);
    std::fs::write(&action_path, "ocr-upload\n")?;
    Ok(action_path)
}

fn write_session_inner(
    session_dir: &Path,
    selection: ScreenRect,
    corner_radius: f32,
    buffer: &CapturedDesktop,
    peek: Option<&WindowPeekImage>,
    cursor_visible: bool,
    action: SessionAction,
) -> anyhow::Result<PathBuf> {
    std::fs::create_dir_all(session_dir)?;
    let session_dir = absolute_path(session_dir);

    // Selection clamped to the desktop bitmap; this is the region the
    // preview contains and what CroppedRect must describe.
    let requested = selection;
    let selection = selection
        .intersection(&buffer.bounds)
        .ok_or_else(|| anyhow!("selection {:?} does not intersect desktop bounds {:?}", selection, buffer.bounds))?;
    // A picked window's corners go transparent in the PREVIEW only (the
    // image UPLOAD ships and the editor shows first). desktop.png stays a
    // plain bitmap — the editor crops it by CroppedRect itself — and a
    // corner the clamp cut off is not a corner.
    let preview_corners = if corner_radius > 0.0 {
        corners_to_round(requested, selection)
    } else {
        [false; 4]
    };

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
            apply_rounded_corners(&mut rgba, w, h, corner_radius, preview_corners);
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
    // success signal (missing file = capture canceled).
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
        corner_radius,
    };

    let json_path = session_dir.join("session.json");
    std::fs::write(&json_path, serde_json::to_string_pretty(&info)?)?;
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

#[cfg(test)]
mod tests {
    use std::time::{SystemTime, UNIX_EPOCH};

    use super::*;

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
    fn video_action_radius_passes_through_unscaled_rect() {
        // Windows: the marker rect IS the selection, so the radius is untouched.
        let sel = ScreenRect::from_xy_size(0, 0, 800, 600);
        assert_eq!(video_action_radius(8.0, sel, 800, 600), 8.0);
    }

    #[test]
    fn video_action_radius_follows_the_rect_conversion() {
        // macOS Retina: a 1600x1200 physical selection emits an 800x600 point
        // rect, so a 24 px radius has to travel as 12 pt.
        let sel = ScreenRect::from_xy_size(0, 0, 1600, 1200);
        assert_eq!(video_action_radius(24.0, sel, 800, 600), 12.0);
    }

    #[test]
    fn video_action_radius_caps_at_half_the_shorter_side() {
        let sel = ScreenRect::from_xy_size(0, 0, 10, 4);
        assert_eq!(video_action_radius(16.0, sel, 10, 4), 2.0);
    }

    #[test]
    fn video_action_radius_zero_for_square_selection() {
        let sel = ScreenRect::from_xy_size(0, 0, 800, 600);
        assert_eq!(video_action_radius(0.0, sel, 800, 600), 0.0);
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
            logical_origin: clowd_rust_core::geometry::LogicalPoint::new(x as f64, y as f64),
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

    /// The `ocr-upload` marker is a wire contract with
    /// `CaptureSessionDispatcher`: the bare verb, no payload, one trailing
    /// newline — and the payload lives beside it in `ocr.txt`.
    #[test]
    fn ocr_upload_action_writes_marker_and_sidecar() {
        let dir = temp_session_dir();
        assert!(matches!(write_ocr_upload_action(&dir, "hello"), ActionResult::Success));

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
        assert_eq!(names, vec![ACTION_FILE.to_string(), OCR_TEXT_FILE.to_string()]);
        assert_eq!(std::fs::read_to_string(dir.join(ACTION_FILE)).unwrap(), "ocr-upload\n");
        assert_eq!(std::fs::read_to_string(dir.join(OCR_TEXT_FILE)).unwrap(), "hello");

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// Recognized text is multi-line by nature. It must reach `ocr.txt`
    /// byte-for-byte — no re-wrapping, no trailing newline of our own —
    /// while `action.txt` stays exactly one line, because the dispatcher
    /// prefix-matches the marker's first line and would mis-route a
    /// payload that leaked into it.
    #[test]
    fn ocr_upload_text_is_verbatim_and_marker_stays_one_line() {
        let dir = temp_session_dir();
        let text = "first line\nsecond, with a comma\n\n#fourth after a blank";
        assert!(matches!(write_ocr_upload_action(&dir, text), ActionResult::Success));

        let marker = std::fs::read_to_string(dir.join(ACTION_FILE)).unwrap();
        assert_eq!(marker.lines().count(), 1, "marker must stay single-line: {marker:?}");
        assert_eq!(marker, "ocr-upload\n");
        assert_eq!(std::fs::read_to_string(dir.join(OCR_TEXT_FILE)).unwrap(), text);

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// The PP-OCRv6 small model reads CJK natively, so non-Latin output is
    /// an ordinary case rather than an exotic one; the bytes on disk are
    /// UTF-8 and the C# reader decodes them as such.
    #[test]
    fn ocr_upload_text_round_trips_cjk() {
        let dir = temp_session_dir();
        let text = "日本語のテキスト\n中文文本\n한국어";
        assert!(matches!(write_ocr_upload_action(&dir, text), ActionResult::Success));

        let path = dir.join(OCR_TEXT_FILE);
        assert_eq!(std::fs::read_to_string(&path).unwrap(), text);
        assert_eq!(std::fs::read(&path).unwrap(), text.as_bytes());

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// No BOM. `File.ReadAllText` on the C# side would silently strip one,
    /// but the text is pasted into an upload verbatim elsewhere, and a
    /// leading U+FEFF is invisible in every viewer that would show it.
    #[test]
    fn ocr_upload_text_has_no_bom() {
        let dir = temp_session_dir();
        assert!(matches!(write_ocr_upload_action(&dir, "ascii start"), ActionResult::Success));

        let bytes = std::fs::read(dir.join(OCR_TEXT_FILE)).unwrap();
        assert_eq!(bytes.first(), Some(&b'a'));
        assert_ne!(bytes.first(), Some(&0xEF), "UTF-8 BOM leaked into ocr.txt");

        let _ = std::fs::remove_dir_all(&dir);
    }
}
