//! Writing the portal's screenshot into the session directory.
//!
//! The shell is already watching this directory (`CaptureSessionDispatcher`),
//! and the ordering invariant from the overlay's `session_output` holds here
//! too: `session.json` appears **last**, because its presence is what tells
//! the shell the payload is complete. Everything else is written before it.
//!
//! The portal has already cropped the image to whatever the user chose —
//! screen, window or area — and tells us nothing about where on the desktop
//! it came from. So, as for a scrolling capture, the whole image is the
//! selection: `CroppedRect` is `0,0,W,H` and there is no larger desktop
//! bitmap for it to be a crop of. `OriginalBounds` is the same rect rather
//! than the empty one the scroll driver writes: the editor uses it to open
//! over the capture's spot, which we cannot know, and a rect at the origin
//! makes it place the window like any small capture rather than centre a
//! possibly full-screen one. There is no cursor.

use std::path::{Path, PathBuf};

use clowd_rust_core::geometry::{RectExt, ScreenRect};
use clowd_rust_core::session::{absolute_path, created_utc_now, save_png, SessionJson};

/// Write `desktop.png`, `cropped.png` and then `session.json`. Returns the
/// path of the session file.
pub fn write_session(session_dir: &Path, rgba: Vec<u8>, width: u32, height: u32) -> anyhow::Result<PathBuf> {
    std::fs::create_dir_all(session_dir)?;
    let session_dir = absolute_path(session_dir);

    let desktop_path = session_dir.join("desktop.png");
    let preview_path = session_dir.join("cropped.png");

    save_png(&desktop_path, rgba, width, height)?;

    // cropped.png is the same pixels, at full resolution — not a thumbnail.
    // It is what `SessionInfo.UploadSourcePath` sends when the user uploads
    // straight from Recents without opening the editor. Copying the encoded
    // file avoids a second PNG compression of the same image.
    std::fs::copy(&desktop_path, &preview_path)?;

    let whole = ScreenRect::from_xy_size(0, 0, width as i32, height as i32);
    let info = SessionJson {
        created_utc: created_utc_now(),
        name: "Screenshot",
        desktop_img_path: desktop_path.to_string_lossy().into_owned(),
        preview_img_path: preview_path.to_string_lossy().into_owned(),
        cursor_img_path: None,
        cursor_position: None,
        cropped_rect: whole.into(),
        original_bounds: whole.into(),
        // The portal's window shots are already rounded (or not) in the
        // pixels themselves; there is no OS radius to carry.
        corner_radius: 0.0,
    };

    let json_path = session_dir.join("session.json");
    std::fs::write(&json_path, serde_json::to_string_pretty(&info)?)?;
    Ok(json_path)
}
