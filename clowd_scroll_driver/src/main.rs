//! Scrolling capture — the second half of the feature.
//!
//! By the time this binary runs the overlay (`clowd_capture_wgpu`) has
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
//! it at runtime: no window, no event loop, no GPU, no screen-recording
//! permission dance. Starting any of that would put pixels on screen in
//! front of the very content it is about to photograph.
//!
//! Windows-only. Everything it does (synthetic wheel input, GDI region
//! capture, `WM_MOUSEWHEEL`) is Win32; macOS would need the CGEvent/AX
//! equivalents, so the five driver modules are `#[cfg(windows)]` and the
//! entry point elsewhere refuses.
//!
//! Layout:
//! - [`drive`] — the loop, the caps, and the NDJSON conversation with the shell.
//! - [`input`] — synthetic wheel injection, cursor parking, abort polling.
//! - [`frame`] — one BitBlt of the fixed region.
//! - [`stitch`] — frame registration and the composite.
//! - [`output`] — `desktop.png` / `cropped.png` / `session.json`.
//!
//! The protocol is documented in `clowd_capture/CAPTURE_PROTOCOL.md` §3.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod cli;

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

#[macro_use]
extern crate log;

// Every `anyhow!`/`bail!` in this crate lives in the five `#[cfg(windows)]`
// modules, so off Windows the import is unused and `-D warnings` in CI would
// fail the macOS leg. `anyhow::Result` is a path, not a macro, and needs no
// import either way.
#[cfg(windows)]
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

    let result = run(args);
    if let Err(err) = &result {
        clowd_rust_core::telemetry::capture_error(err);
    }
    result
}

#[cfg(windows)]
fn run(args: cli::CliArgs) -> anyhow::Result<()> {
    drive::run(args)
}

/// macOS stub. The shell never routes a `scroll` action here (the overlay's
/// panel button is compiled out), so reaching this means something upstream
/// is confused — exit with the same code a failed capture uses rather than
/// pretending to have produced a session.
#[cfg(not(windows))]
fn run(_args: cli::CliArgs) -> anyhow::Result<()> {
    error!("the scrolling capture driver is not implemented on this platform");
    // `process::exit` skips the init guard's drop, so without this the one
    // event this stub exists to report is queued and silently dropped.
    clowd_rust_core::telemetry::flush();
    std::process::exit(clowd_rust_core::exit::CAPTURE_FAILED);
}
