//! Screenshot capture on a Wayland session — the Linux counterpart of the
//! `clowd_capture` overlay for the desktop that gives no process a copy of
//! the screen to draw one over.
//!
//! On X11 the overlay works as it does everywhere: photograph the desktop,
//! show it fullscreen, let the user select. A Wayland compositor hands out
//! no screen pixels, so the only sanctioned route is the desktop's own
//! screenshot UI, reached through the `org.freedesktop.portal.Screenshot`
//! interface of xdg-desktop-portal. This binary asks for an *interactive*
//! screenshot, waits while the user picks a screen, window or area in that
//! UI, and turns the PNG the portal hands back into the same session
//! directory the overlay would have written — which is all the shell needs
//! to open the editor. No window, no event loop, no GPU, and none of the
//! overlay's features (peek, scrolling capture, video, share, copy, colour
//! pick): the desktop's UI decides what the user can do.
//!
//! It is one-shot only. There is no warm/standby mode: the process costs
//! nothing to start and the wait is on the user, not on us.
//!
//! Layout:
//! - [`cli`] — `--session-dir`.
//! - [`portal`] — the portal request and the user's answer.
//! - [`output`] — `desktop.png` / `cropped.png` / `session.json`.
//!
//! # Host contract (`Clowd.Ui`)
//!
//! Until this is folded into `CAPTURE_PROTOCOL.md`, the contract the shell
//! implements against lives here.
//!
//! **When.** On Linux, when the session is Wayland: `WAYLAND_DISPLAY` set
//! (equivalently `XDG_SESSION_TYPE=wayland`). Otherwise `clowd_capture`.
//! Under a Wayland session `clowd_capture` would still start via XWayland
//! but see only X clients in its desktop photograph, so the session type,
//! not the availability of an X display, decides.
//!
//! **Where.** Beside `clowd_capture`, named `clowd_capture_wayland` (no
//! extension). It links no desktop library; the portal is reached over
//! D-Bus, so the only runtime requirement is a session bus with an
//! xdg-desktop-portal frontend (`xdg-desktop-portal-gnome`, `-kde`,
//! `-wlr`, ...) on it.
//!
//! **Spawn.** `clowd_capture_wayland --session-dir <dir>` — that flag and
//! nothing else. The overlay's flags (`--capture-mode`, `--video`,
//! `--shell-pid`, hotkeys, ...) are not accepted: the shell must not forward
//! its `clowd_capture` command line. The directory is created if missing.
//! Nothing is written to stdout; stderr carries the log lines, mirrored
//! into `capture.log` in the session directory. `CLOWD_DISABLE_TELEMETRY`
//! is honoured as by every other Clowd process. There is no stdin protocol
//! and nothing to signal: the process ends when the user is done. It may
//! live as long as the user leaves the desktop's screenshot UI open; do not
//! time it out.
//!
//! **Exit codes** (`clowd_rust_core::exit`).
//!
//! | Code | Meaning |
//! |---|---|
//! | 0 | Done — capture written, **or cancelled** in the desktop's UI. Tell them apart by the presence of `session.json`, as for the overlay. |
//! | 2 | Bad command line (clap). A shell bug. |
//! | 4 | `CAPTURE_FAILED` — no portal frontend, the portal refused or failed, or the screenshot could not be decoded or the session written. The reason is one line on stderr / in `capture.log`; nothing is written otherwise. |
//!
//! Any other non-zero exit is a crash; report it with the stderr and
//! `capture.log` tails, as for the overlay.
//!
//! **Session files.** Completion signal = process exit, then key off
//! `session.json` (written last):
//!
//! | File | Notes |
//! |---|---|
//! | `desktop.png` | The portal's screenshot, already cropped to what the user chose. |
//! | `cropped.png` | Same pixels, full resolution (see `output`). |
//! | `session.json` | §1.3 schema. `CroppedRect` = `OriginalBounds` = `0,0,W,H`; no `CursorImgPath`/`CursorPosition`; no `CornerRadius`. `W,H` are the portal's pixels and need not match any monitor. |
//! | `capture.log` | Log mirror, read after a non-zero exit. |
//!
//! No `action.txt`: opening the editor is the only outcome. The portal
//! keeps its own copy of the screenshot wherever it chose to save it
//! (typically `~/Pictures`); it is the user's file and is left alone.
//!
//! **Not Linux.** The binary builds on every platform so the workspace
//! does, but outside Linux `main` is a stub that exits 4; the shell never
//! spawns it there.

// Every module is Linux-only so the stub stays a stub: a `mod` the stub
// cannot use would just be dead code on Windows and macOS.
#[cfg(target_os = "linux")]
mod cli;
#[cfg(target_os = "linux")]
mod output;
#[cfg(target_os = "linux")]
mod portal;

#[cfg(target_os = "linux")]
#[macro_use]
extern crate log;

use std::process::ExitCode;

#[cfg(target_os = "linux")]
fn main() -> ExitCode {
    use clap::Parser;

    let args = cli::CliArgs::parse();

    // Before the logger, so the log file has somewhere to go. A failure here
    // has no logger to speak through, hence the bare eprintln.
    if let Err(err) = std::fs::create_dir_all(&args.session_dir) {
        eprintln!("cannot create session directory {:?}: {err}", args.session_dir);
        return capture_failed();
    }

    // Stderr, never Mixed — the shell pumps stderr into its diagnostics and
    // stdout is left silent so it cannot mistake a log line for output.
    // The file mirror is the session's `capture.log`, the same name the
    // overlay uses, because the shell reads it by that name after a
    // non-zero exit.
    let mut loggers: Vec<Box<dyn simplelog::SharedLogger>> = vec![simplelog::TermLogger::new(
        log::LevelFilter::Info,
        simplelog::Config::default(),
        simplelog::TerminalMode::Stderr,
        simplelog::ColorChoice::Auto,
    )];
    if let Ok(file) = std::fs::File::create(args.session_dir.join("capture.log")) {
        loggers.push(simplelog::WriteLogger::new(
            log::LevelFilter::Info,
            simplelog::Config::default(),
            std::io::LineWriter::new(file),
        ));
    }
    clowd_rust_core::telemetry::install_logger(simplelog::CombinedLogger::new(loggers));

    // held for the rest of main: dropping the guard flushes anything still
    // queued, which is why failures return an ExitCode instead of calling
    // process::exit
    let _sentry = clowd_rust_core::telemetry::init("clowd_capture_wayland");

    match run(&args.session_dir) {
        Ok(Run::Done) => ExitCode::SUCCESS,
        // Not an `Err`, so it never reaches Sentry: a desktop without a
        // portal frontend is the user's environment, not our bug, and every
        // retry would otherwise file another event. The reason went to
        // stderr / `capture.log` in `run`, where the shell reads it.
        Ok(Run::NoPortal) => capture_failed(),
        Err(err) => {
            // `capture_error` is the one Sentry event for this failure (the
            // full anyhow chain); the log line is a breadcrumb-level mirror
            // for stderr and `capture.log`, where the shell reads it, and
            // deliberately not an `error!`, which would file a second event
            // for the same failure.
            warn!("capture failed: {err:#}");
            clowd_rust_core::telemetry::capture_error(&err);
            capture_failed()
        }
    }
}

/// How `run` ended when nothing went wrong on our side.
#[cfg(target_os = "linux")]
enum Run {
    /// Capture written, or cancelled by the user (the shell tells the two
    /// apart by the presence of `session.json`).
    Done,
    /// No portal frontend to ask; exit 4 with the reason already logged.
    NoPortal,
}

/// The whole capture: ask the portal, wait for the user, write the session.
/// `Ok(Done)` for a cancel too — the shell reads the absence of
/// `session.json`, not an exit code, as "nothing to open". `Err` is a
/// failure worth reporting: the portal refused or broke, or the file it
/// wrote could not be read or the session could not be written.
#[cfg(target_os = "linux")]
fn run(session_dir: &std::path::Path) -> anyhow::Result<Run> {
    use anyhow::Context;

    info!("session mode: payload will be written to {session_dir:?}");

    let path = match portal::take_screenshot()? {
        portal::Outcome::Captured(path) => path,
        portal::Outcome::Cancelled => {
            info!("screenshot cancelled by the user; no session written");
            return Ok(Run::Done);
        }
        portal::Outcome::NoPortal(reason) => {
            // `warn!`, not `error!`: the latter is mirrored into Sentry as an
            // event, and this is the user's environment, not our bug.
            warn!("cannot take a screenshot on this desktop: {reason} (install an xdg-desktop-portal backend)");
            return Ok(Run::NoPortal);
        }
    };
    info!("portal wrote the screenshot to {path:?}");

    // Decode rather than copy: the session's PNGs then come from the same
    // encoder as every other capture's, and a file the portal misdescribed
    // fails here, with the path in the message, rather than in the editor.
    // Format sniffed from the bytes, not the name — the spec says PNG, but a
    // backend that wrote something else with a `.png` name should still open.
    let image = image::ImageReader::open(&path)
        .with_context(|| format!("opening the portal's screenshot {path:?}"))?
        .with_guessed_format()
        .with_context(|| format!("reading the portal's screenshot {path:?}"))?
        .decode()
        .with_context(|| format!("decoding the portal's screenshot {path:?}"))?
        .into_rgba8();
    let (width, height) = image.dimensions();
    // A 0×0 image is a portal bug rather than a decode failure, and would
    // otherwise reach the editor as an image it cannot show.
    anyhow::ensure!(width > 0 && height > 0, "the portal's screenshot {path:?} has no pixels");

    let json_path = output::write_session(session_dir, image.into_raw(), width, height)?;
    info!("session written to {json_path:?} ({width}x{height})");
    Ok(Run::Done)
}

#[cfg(not(target_os = "linux"))]
fn main() -> ExitCode {
    // Spawned only on a Wayland session; anywhere else the shell has a real
    // capturer. Exiting with the capture-failed code rather than a panic
    // means a misrouted spawn is reported as a plain failure with this line
    // attached, not as a crash.
    eprintln!("clowd_capture_wayland is the Linux (Wayland) capturer and does nothing on this platform");
    capture_failed()
}

/// `CAPTURE_FAILED` as a process exit code. The constant is an `i32` for
/// the C# side's sake; every value in `exit` fits a `u8`.
fn capture_failed() -> ExitCode {
    ExitCode::from(clowd_rust_core::exit::CAPTURE_FAILED as u8)
}
