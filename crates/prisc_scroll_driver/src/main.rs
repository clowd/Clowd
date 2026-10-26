//! Scrolling capture — the second half of the feature.
//!
//! By the time this binary runs the overlay (`clowd_capture`) has
//! already done its part: the user picked a region, pressed SCROLL,
//! clicked the point to scroll at, and the overlay wrote `action.txt` =
//! `scroll X,Y,W,H PX,PY HWND` and exited. Clowd.Ui reads that marker,
//! puts its border window up around the region, and spawns *this* process
//! to do the mechanical part — scroll the target window a step at a time,
//! photograph the region after each step, stitch the frames into one tall
//! image, and write a finished session the shell can hand straight to the
//! editor.
//!
//! It is a separate binary from the overlay because it shares nothing with
//! it at runtime: no window, no event loop, no GPU. Starting any of that
//! would put pixels on screen in front of the very content it is about to
//! photograph.
//!
//! Windows and macOS. The loop, the caps, the stitcher, the protocol and the
//! session output are the same code on both; everything that touches the OS
//! — injecting a wheel, parking the cursor, resolving and raising the target
//! window, photographing the region — lives behind [`input`] and [`frame`],
//! which each have a `win` and a `mac` half. The two platforms differ in one
//! way that reaches the shared code: on Windows the region and the point are
//! physical virtual-desktop pixels, on macOS they are CG points, so nothing
//! here may compare a coordinate against a *pixel* count taken from a
//! captured frame (see `drive`'s note on the viewport height).
//!
//! Layout:
//! - [`drive`] — the loop, the caps, and the NDJSON conversation with the shell.
//! - [`input`] — synthetic wheel injection, cursor parking, abort polling.
//! - [`frame`] — one screenshot of the fixed region.
//! - [`stitch`] — frame registration and the composite.
//! - [`output`] — `desktop.png` / `cropped.png` / `session.json`.
//!
//! The protocol is documented in `clowd_capture/CAPTURE_PROTOCOL.md` §2.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

// Clowd ships on Windows and macOS only (the overlay's `system` module has no
// third backend either), and a driver that compiled elsewhere without an
// input or capture path would be a binary that cannot do anything.
#[cfg(not(any(windows, target_os = "macos")))]
compile_error!("clowd_scroll_driver supports Windows and macOS only");

mod cli;
mod drive;
mod frame;
mod input;
mod output;
mod stitch;

#[macro_use]
extern crate log;

#[macro_use]
extern crate anyhow;

use clap::Parser;

fn main() -> anyhow::Result<()> {
    let args = cli::CliArgs::parse();

    // stdout is the protocol channel (`drive::emit`), so terminal logging
    // goes to stderr only — the shell pumps it into its own diagnostics.
    // The file mirror is the session's `scroll.log` rather than
    // `capture.log`: the overlay already wrote the latter into this very
    // directory, and truncating it would erase the diagnostics for the
    // half of the capture that came first.
    let mut loggers: Vec<Box<dyn simplelog::SharedLogger>> = vec![simplelog::TermLogger::new(
        log::LevelFilter::Info,
        simplelog::Config::default(),
        simplelog::TerminalMode::Stderr,
        simplelog::ColorChoice::Auto,
    )];
    let log_file = args
        .session_dir
        .as_ref()
        .and_then(|dir| std::fs::File::create(dir.join("scroll.log")).ok());
    if let Some(file) = log_file {
        loggers.push(simplelog::WriteLogger::new(
            log::LevelFilter::Info,
            simplelog::Config::default(),
            std::io::LineWriter::new(file),
        ));
    }
    clowd_rust_core::telemetry::install_logger(simplelog::CombinedLogger::new(loggers));

    // held for the rest of main: dropping the guard flushes anything still queued
    let _sentry = clowd_rust_core::telemetry::init("clowd_scroll_driver");

    let result = drive::run(args);
    if let Err(err) = &result {
        clowd_rust_core::telemetry::capture_error(err);
    }
    result
}
