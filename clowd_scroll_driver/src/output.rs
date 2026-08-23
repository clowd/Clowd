//! Writing the finished scrolling capture into the session directory.
//!
//! The shell is already watching this directory (`CaptureSessionDispatcher`),
//! and the ordering invariant from the overlay's `session_output` holds here too:
//! `session.json` appears **last**, because its presence is what tells the
//! shell the payload is complete. Everything else is written before it.
//!
//! Two fields differ from a normal screenshot session, both deliberately:
//!
//! - `OriginalBounds` is the empty rect. It normally says where on the
//!   virtual desktop the capture came from, and the editor uses it to open
//!   the window over that spot — which for a 20,000 px tall composite would
//!   mean a window taller than every monitor stacked. Empty bounds make the
//!   editor centre instead.
//! - `CroppedRect` is `0,0,W,H`: the whole composite is the selection. There
//!   is no larger desktop bitmap for it to be a crop of.

use std::path::{Path, PathBuf};

use clowd_rust_core::geometry::{RectExt, ScreenRect};
use clowd_rust_core::session::{absolute_path, created_utc_now, save_png, SessionJson};

use crate::stitch::Composite;

/// Write `desktop.png`, `cropped.png` and then `session.json`. Returns the
/// path of the session file.
pub fn write_session(session_dir: &Path, composite: Composite) -> anyhow::Result<PathBuf> {
    std::fs::create_dir_all(session_dir)?;
    let session_dir = absolute_path(session_dir);

    let desktop_path = session_dir.join("desktop.png");
    let preview_path = session_dir.join("cropped.png");

    let (width, height) = (composite.width, composite.height);
    save_png(&desktop_path, composite.rgba, width, height)?;

    // cropped.png is the same pixels, at full resolution — not a thumbnail.
    // It is what `SessionInfo.UploadSourcePath` sends when the user uploads
    // straight from Recents without opening the editor, so downscaling here
    // would quietly degrade every un-edited share. Copying the encoded file
    // avoids paying for a second PNG compression of a very tall image.
    std::fs::copy(&desktop_path, &preview_path)?;

    let info = SessionJson {
        created_utc: created_utc_now(),
        name: "Screenshot",
        desktop_img_path: desktop_path.to_string_lossy().into_owned(),
        preview_img_path: preview_path.to_string_lossy().into_owned(),
        cursor_img_path: None,
        cursor_position: None,
        cropped_rect: ScreenRect::from_xy_size(0, 0, width as i32, height as i32).into(),
        original_bounds: ScreenRect::zero().into(),
        // A stitched page is a rectangle of content, not a window frame.
        corner_radius: 0.0,
    };

    let json_path = session_dir.join("session.json");
    std::fs::write(&json_path, serde_json::to_string_pretty(&info)?)?;
    Ok(json_path)
}
