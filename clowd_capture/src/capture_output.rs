use std::thread;
use std::time::Duration;

use winit::window::Window;

use crate::image_extract::{
    apply_rounded_corners, composite_cursor_rgba, corners_to_round, extract_selection_rgba, extract_selection_rgba_with_peek,
};
use crate::system::{CapturedCursor, CapturedDesktop, WindowPeekImage};
use clowd_rust_core::geometry::ScreenRect;

/// Result of a Copy or Save action.
pub enum ActionResult {
    /// Operation completed successfully.
    Success,
    /// User canceled the operation (e.g. dismissed save dialog).
    Canceled,
    /// Operation failed with an error message.
    Failed(String),
}

/// Extract the selection as straight-alpha RGBA with the peek composited,
/// the cursor drawn if visible, and — for a picked window — its OS corner
/// radius cut into the alpha (`corner_radius` > 0, see
/// `InteractionState::selection_radius`). Shared by copy and save so the
/// two can never disagree about what "the image" is.
pub fn extract_output_image(
    selection: ScreenRect,
    corner_radius: f32,
    buffer: &CapturedDesktop,
    peek: Option<&WindowPeekImage>,
    cursor: Option<&CapturedCursor>,
    cursor_visible: bool,
) -> Option<(Vec<u8>, u32, u32)> {
    let (mut rgba, width, height) = match peek {
        Some(p) => extract_selection_rgba_with_peek(selection, buffer, p),
        None => extract_selection_rgba(selection, buffer),
    }?;
    if cursor_visible {
        if let Some(cur) = cursor {
            composite_cursor_rgba(&mut rgba, width, height, selection, cur);
        }
    }
    if corner_radius > 0.0 {
        // The extract clamps to the desktop bitmap; a corner that was cut
        // off by that clamp is not a window corner and stays square.
        let clamped = selection
            .intersection(&buffer.bounds)
            .unwrap_or(selection);
        apply_rounded_corners(&mut rgba, width, height, corner_radius, corners_to_round(selection, clamped));
    }
    Some((rgba, width, height))
}

/// Copy the selected region to the clipboard.
pub fn copy_to_clipboard_with_peek(
    selection: ScreenRect,
    corner_radius: f32,
    buffer: &CapturedDesktop,
    peek: Option<&WindowPeekImage>,
    cursor: Option<&CapturedCursor>,
    cursor_visible: bool,
) -> ActionResult {
    let Some((rgba, width, height)) = extract_output_image(selection, corner_radius, buffer, peek, cursor, cursor_visible) else {
        log::warn!("copy: no selection or failed to extract");
        return ActionResult::Failed("No selection to copy".to_string());
    };

    match set_clipboard_image(&rgba, width as usize, height as usize) {
        Ok(()) => {
            log::info!("copied {}x{} image to clipboard", width, height);
            ActionResult::Success
        }
        Err(e) => {
            log::error!("copy: clipboard write failed after {CLIPBOARD_SET_ATTEMPTS} attempts: {e}");
            ActionResult::Failed(format!("Failed to copy to clipboard: {e}"))
        }
    }
}

/// Copy plain text to the clipboard. Used by the OCR COPY action, which has no image to offer —
/// the user asked for the *words*, so text is the only format written.
///
/// Nothing waits on this and nothing hands ownership back afterwards, which is correct on Windows
/// and Windows only: `SetClipboardData` takes ownership of the `HGLOBAL` the moment it succeeds
/// (clipboard-win documents this explicitly), and `arboard` never registers delayed rendering, so
/// the text is already the OS's by the time this returns and survives our process exiting — which
/// it does, immediately, on every copy. A Linux port would *not* inherit that: X11 clipboard
/// ownership lives in the owning process, so the X11 backend would need `SetExtLinux::wait()` (or
/// a surviving daemon) before the process is allowed to die.
pub fn copy_text_to_clipboard(text: &str) -> ActionResult {
    match set_clipboard_text(text) {
        Ok(()) => {
            log::info!("copied {} chars of text to clipboard", text.chars().count());
            ActionResult::Success
        }
        Err(e) => {
            log::error!("copy: clipboard text write failed after {CLIPBOARD_SET_ATTEMPTS} attempts: {e}");
            ActionResult::Failed(format!("Failed to copy to clipboard: {e}"))
        }
    }
}

/// How many times the whole clipboard write is attempted before giving up.
const CLIPBOARD_SET_ATTEMPTS: u32 = 5;

/// Delay before the first retry; doubles each time. 15/30/60/120ms is ~225ms in the worst case,
/// which stays under what reads as a lag on a copy the user explicitly asked for.
const CLIPBOARD_RETRY_BACKOFF: Duration = Duration::from_millis(15);

/// Puts an image on the clipboard, re-running the whole write if it loses a race for it.
///
/// Windows lets exactly one process hold the clipboard open at a time, and a write is not one
/// atomic step — it opens, empties, then hands over each format. Anything else reacting to the
/// copy can take the clipboard away between those steps: clipboard managers, and Windows' own
/// clipboard-history service, which wakes up on precisely the `EmptyClipboard` we just issued.
///
/// `arboard` retries its `OpenClipboard` internally, so that half is already covered — but the
/// failure we actually saw (CLOWD-Y) was error 1418 `ERROR_CLIPBOARD_NOT_OPEN` coming out of
/// `SetClipboardData`, i.e. the open had *succeeded* and the clipboard was gone by the time the
/// pixels were handed over. Nothing short of re-running open/empty/set recovers from that.
///
/// The window is small but real, and it is widened by `arboard` opening the clipboard with a null
/// owner: `EmptyClipboard` then sets the clipboard owner to NULL, which Windows documents as a
/// cause of `SetClipboardData` failing. Fixing that properly means owning the Win32 image path
/// ourselves rather than calling `arboard`, so retrying is the proportionate fix — but it is a
/// mitigation, and if this starts failing with retries exhausted, that is the thread to pull.
fn set_clipboard_image(rgba: &[u8], width: usize, height: usize) -> Result<(), String> {
    retry_clipboard_write(|| {
        // a fresh Clipboard per attempt: on Windows the open happens inside the write and is what
        // has to be redone, and on the other backends this re-establishes the connection.
        arboard::Clipboard::new()
            .and_then(|mut clipboard| {
                clipboard.set_image(arboard::ImageData {
                    width,
                    height,
                    bytes: std::borrow::Cow::Borrowed(rgba),
                })
            })
            .map_err(|e| e.to_string())
    })
}

/// Puts text on the clipboard, under the same retry policy as [`set_clipboard_image`].
///
/// The policy is not copied out of caution: on Windows `arboard`'s text path runs the *identical*
/// `OpenClipboard` / `EmptyClipboard` / `SetClipboardData` sequence the image path does — only the
/// format handed over differs — so it is exposed to exactly the race described above, including
/// the clipboard-history service waking on our own `EmptyClipboard`. Error 1418 out of
/// `SetClipboardData` is as reachable here as it was for images, and re-running the whole write is
/// the only thing that recovers from it.
fn set_clipboard_text(text: &str) -> Result<(), String> {
    retry_clipboard_write(|| {
        // a fresh Clipboard per attempt — see [`set_clipboard_image`]: the open happens inside the
        // write and is the part that has to be redone.
        arboard::Clipboard::new()
            .and_then(|mut clipboard| clipboard.set_text(text))
            .map_err(|e| e.to_string())
    })
}

/// Runs `write` until it succeeds or [`CLIPBOARD_SET_ATTEMPTS`] is exhausted, backing off between
/// tries and returning the last failure. Split out from [`set_clipboard_image`] so the retry
/// policy is exercised by tests without needing a real clipboard.
fn retry_clipboard_write<F>(mut write: F) -> Result<(), String>
where
    F: FnMut() -> Result<(), String>,
{
    let mut backoff = CLIPBOARD_RETRY_BACKOFF;

    for attempt in 1..=CLIPBOARD_SET_ATTEMPTS {
        match write() {
            Ok(()) => {
                if attempt > 1 {
                    log::info!("copy: clipboard write succeeded on attempt {attempt}");
                }
                return Ok(());
            }
            // the last attempt's error is the one that gets reported, so don't sleep past it
            Err(e) if attempt == CLIPBOARD_SET_ATTEMPTS => return Err(e),
            Err(e) => {
                // warn!, not error!: an attempt a later one recovers from is a breadcrumb.
                // Logging it at error! would file a Sentry issue per lost race — the exact
                // noise this is meant to remove.
                log::warn!("copy: clipboard write attempt {attempt} failed: {e}");
                thread::sleep(backoff);
                backoff *= 2;
            }
        }
    }

    // unreachable while CLIPBOARD_SET_ATTEMPTS >= 1: the loop either returns or exhausts.
    Err(String::from("clipboard unavailable"))
}

/// Save the selected region to a file via save dialog.
pub fn save_to_file_with_peek(
    selection: ScreenRect,
    corner_radius: f32,
    buffer: &CapturedDesktop,
    peek: Option<&WindowPeekImage>,
    cursor: Option<&CapturedCursor>,
    cursor_visible: bool,
    window: &Window,
) -> ActionResult {
    let Some((rgba, width, height)) = extract_output_image(selection, corner_radius, buffer, peek, cursor, cursor_visible) else {
        log::warn!("save: no selection or failed to extract");
        return ActionResult::Failed("No selection to save".to_string());
    };

    let path = rfd::FileDialog::new()
        .add_filter("PNG Image", &["png"])
        .add_filter("JPEG Image", &["jpg", "jpeg"])
        .set_file_name("screenshot.png")
        .set_parent(window)
        .save_file();

    let Some(mut path) = path else {
        log::info!("save: dialog canceled");
        return ActionResult::Canceled;
    };

    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .map(|s| s.to_lowercase());
    let format = match ext.as_deref() {
        Some("png") => image::ImageFormat::Png,
        Some("jpg") | Some("jpeg") => image::ImageFormat::Jpeg,
        _ => {
            path.set_extension("png");
            image::ImageFormat::Png
        }
    };

    let img: image::RgbaImage = image::ImageBuffer::from_raw(width, height, rgba).expect("buffer size matches");

    if let Err(e) = img.save_with_format(&path, format) {
        log::error!("save: failed to write {:?}: {e}", path);
        ActionResult::Failed(format!("Failed to save file: {e}"))
    } else {
        log::info!("saved {}x{} image to {:?}", width, height, path);
        ActionResult::Success
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;

    /// A write that never loses the race is attempted exactly once.
    #[test]
    fn retry_clipboard_write_succeeds_without_retrying() {
        let calls = Cell::new(0);
        let result = retry_clipboard_write(|| {
            calls.set(calls.get() + 1);
            Ok(())
        });

        assert!(result.is_ok());
        assert_eq!(calls.get(), 1);
    }

    /// The CLOWD-Y case: the clipboard is taken away for the first couple of tries and then
    /// available. This must report success, because the image did reach the clipboard.
    #[test]
    fn retry_clipboard_write_recovers_after_transient_failures() {
        let calls = Cell::new(0);
        let result = retry_clipboard_write(|| {
            calls.set(calls.get() + 1);
            if calls.get() < 3 {
                Err(String::from("SetClipboardData failed with error: (os error 1418)"))
            } else {
                Ok(())
            }
        });

        assert!(result.is_ok());
        assert_eq!(calls.get(), 3);
    }

    /// A clipboard that is never available gives up after the full budget and surfaces the last
    /// error — this is the only path that reaches `error!`, and so Sentry.
    #[test]
    fn retry_clipboard_write_gives_up_and_reports_the_last_error() {
        let calls = Cell::new(0);
        let result = retry_clipboard_write(|| {
            calls.set(calls.get() + 1);
            Err(format!("failure {}", calls.get()))
        });

        assert_eq!(calls.get(), CLIPBOARD_SET_ATTEMPTS);
        assert_eq!(result, Err(format!("failure {CLIPBOARD_SET_ATTEMPTS}")));
    }

    /// The retry budget must not stall a copy the user is waiting on. Worst case is the sum of
    /// the backoffs, and the final attempt must not be followed by one.
    #[test]
    fn retry_clipboard_write_stays_within_its_time_budget() {
        let started = std::time::Instant::now();
        let _ = retry_clipboard_write(|| Err(String::from("nope")));
        let elapsed = started.elapsed();

        // 15 + 30 + 60 + 120 = 225ms of backoff across 5 attempts, with no sleep after the last.
        assert!(elapsed >= Duration::from_millis(225), "backed off too little: {elapsed:?}");
        // A loaded CI runner overshoots every thread::sleep: this took 697ms on a macOS
        // runner against the 600ms ceiling it used to have. A trailing sleep after the
        // final attempt would only reach 465ms, well inside that noise, so wall clock
        // cannot be what rules one out — retry_clipboard_write_gives_up_and_reports_the_
        // last_error asserts the exact attempt count and does that job precisely. What is
        // left here is a guard against a runaway retry loop, so the ceiling is set at
        // roughly 5x the intended backoff rather than just above it.
        assert!(elapsed < Duration::from_millis(1200), "backed off too much: {elapsed:?}");
    }
}
