//! Scrolling capture — the `--scroll-drive` mode of this binary.
//!
//! By the time we get here the overlay has already run its normal cycle:
//! the user picked a region, pressed SCROLL, clicked the point to scroll at,
//! and the overlay wrote `action.txt` = `scroll X,Y,W,H PX,PY HWND` and
//! exited. Clowd.Ui reads that marker, puts its border window up around the
//! region, and spawns this binary again — same exe, no overlay, no winit, no
//! wgpu — to do the mechanical part: scroll the target window a step at a
//! time, photograph the region after each step, stitch the frames into one
//! tall image, and write a finished session the shell can hand straight to
//! the editor.
//!
//! The mode is Windows-only. Everything it does (synthetic wheel input,
//! GDI region capture, `WM_MOUSEWHEEL`) is Win32; macOS would need the
//! CGEvent/AX equivalents, so [`run`] there just refuses.
//!
//! Layout:
//! - [`drive`] — the loop, the caps, and the NDJSON conversation with the shell.
//! - [`input`] — synthetic wheel injection, cursor parking, abort polling.
//! - [`frame`] — one BitBlt of the fixed region.
//! - [`stitch`] — frame registration and the composite.
//! - [`output`] — `desktop.png` / `cropped.png` / `session.json`.

#[cfg(windows)]
mod drive;
#[cfg(windows)]
mod frame;
#[cfg(windows)]
mod input;
#[cfg(windows)]
mod output;
#[cfg(windows)]
mod stitch;

#[cfg(windows)]
pub use drive::run;

/// macOS stub. The shell never routes a `scroll` action here (the panel
/// button is compiled out), so reaching this means something upstream is
/// confused — exit with the same code a failed capture uses rather than
/// pretending to have produced a session.
#[cfg(not(windows))]
pub fn run(_args: crate::settings::CliArgs) -> anyhow::Result<()> {
    error!("--scroll-drive is not implemented on this platform");
    crate::telemetry::crash::flush();
    std::process::exit(crate::system::EXIT_CAPTURE_FAILED);
}
