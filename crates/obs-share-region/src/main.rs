//! `obs-share-region`: mirrors a rectangular screen region into an ordinary
//! window, so meeting apps that can only share "a whole screen" or "one window"
//! can be pointed at that window and effectively share a region.
//!
//! This binary is a headless helper driven by the Clowd shell over pipes, the
//! same way `obs-express` is: line-oriented commands in on stdin, JSON status
//! lines out on stdout, free-form chatter on stderr. It owns no appearance —
//! the border around the live region and the floating controls are Clowd's
//! windows (Clowd.Ui/Video/BorderWindow and FloatingToolbarWindow), and drawing
//! anything here would put a second border on top of theirs.
//!
//! # The one window
//!
//! The only user interface this process has is a prompt: a small ordinary
//! titled window, dark-chromed to match the Clowd shell, whose client area
//! says "Share this window" over an OK button.
//! The user points their meeting app's share picker at it, then presses OK.
//! From that moment the SAME window — never a new one, because the share the
//! meeting app just started is bound to that window's identity (its HWND /
//! NSWindow) — sheds its title bar, is resized to the region, is moved OFF
//! SCREEN, and becomes the surface the obs display paints the mirrored region
//! into.
//!
//! Off screen is the whole trick. The mirror is fed by a display capture of the
//! region, so a mirror window sitting anywhere on a captured display would be
//! photographed by that capture and show a picture of itself, forever. Parked
//! outside every display's bounds there is nothing on screen to photograph, yet
//! the window is still composited and still capturable by the meeting app's
//! *window* capture — which is what makes the share keep working. (The previous
//! design instead hid the on-screen mirror under an opaque mask window; that
//! mask, the frame window and all their geometry are gone.)
//!
//! # stdin: commands (one per line, first token case-insensitive)
//!
//! ```text
//!   quit | q                        exit 0
//!   move X,Y,W,H                    new region; also accepted spaced: move X Y W H
//!   obscure blur [strength]         gaussian blur the preview, strength 1..=100 (default 50)
//!   obscure pixelate [strength]     pixelate the preview, same range and default
//!   obscure hide                    preview goes black with a centred eye-with-slash icon
//!   obscure none | unobscure        back to the live preview
//!   <EOF>                           equivalent to quit (orphan safety)
//! ```
//!
//! A malformed or unknown line is answered with `command_error` and otherwise
//! ignored; nothing arriving on stdin is ever fatal.
//!
//! # stdout: protocol (exactly one JSON object per line, flushed)
//!
//! ```text
//!   {"type":"initialized"}                                  obs is up, prompt window is showing
//!   {"type":"sharing_started","region":{"x","y","w","h"}}    user pressed OK; mirroring
//!   {"type":"region_changed","region":{"x","y","w","h"}}     ack of `move` (region ACTUALLY applied)
//!   {"type":"obscure","mode":"none|blur|pixelate|hide","strength":N}   ack of obscure/unobscure
//!   {"type":"status","fps":29.9}                            1 Hz, only after sharing_started
//!   {"type":"command_error","message":"..."}                a line was refused, or failed
//! ```
//!
//! Both regions on the wire are in **capture space** — the same space as
//! `--region` (Windows: physical px on the virtual desktop, X/Y may be
//! negative; macOS: CG points) — and both are what was actually applied after
//! clamping, never what was asked for.
//!
//! Exit codes match obs-express: 0 user quit, 1 runtime/obs error, 2 argument
//! validation. Every exit routes through `obs_platform::exit_process` — libobs
//! is never shut down (known OBS teardown crashes; see crates/obs/src/context.rs).

// Release Windows builds link as a GUI-subsystem binary. The shell spawns this
// process with pipes for all three standard streams and shows the user only the
// prompt window, so a console-subsystem exe would flash (or, launched from
// Explorer, keep open) a console window that is never meant to be part of the
// product. Nothing about the pipe protocol changes: a GUI-subsystem process
// still inherits the handles its parent hands it, which is where every stdout
// line and every stderr line goes. Debug builds stay on the console subsystem
// so that running the binary by hand still prints where it was started.
#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

mod commands;
mod mirror;
mod obscure;
mod status;
mod ui;

use clap::Parser;
use obs_platform::region::{self, Rect};

use crate::mirror::Mirror;
use crate::ui::{AppEvents, UiConfig};

// clap itself exits 2 on invalid arguments (bad flags, out-of-range values,
// failed value parsers); the region string is parsed in main so its error can
// route through exit_process like every other exit.
#[derive(Parser, Debug)]
#[command(version, about = "Mirror a screen region into a shareable window")]
struct Cli {
    /// Region to mirror: X,Y,W,H in the platform capture coordinate space
    /// (Windows: physical px on the virtual desktop; macOS: CG points).
    /// X/Y may be negative; same space and parser as obs-express --region and
    /// as the `move` command on stdin.
    #[arg(long, allow_hyphen_values = true)]
    region: String,

    /// Canvas frame rate of the mirror.
    #[arg(long, default_value = "30", value_parser = clap::value_parser!(u32).range(1..))]
    fps: u32,

    /// Window title — the string the user has to find in the meeting app's
    /// window picker, so callers should set something recognisable. It stays
    /// the window's title after the caption is dropped, because pickers list
    /// the title rather than the caption bar.
    #[arg(long, default_value = "Clowd Shared Region")]
    title: String,

    /// Do not capture the cursor (passed through to the display capture source).
    #[arg(long)]
    no_cursor: bool,
}

/// The `AppEvents` implementation: the shim from UI callbacks onto [`Mirror`],
/// and the place the stdout acks that belong to those callbacks are written.
///
/// Threading contract: the platform UI layer delivers every callback on the
/// main/UI thread — the same thread that ran `main` and bootstrapped the
/// mirror — so `Mirror` needs no synchronization. The only app code running on
/// another thread is the obs_display draw callback (`obscure::draw`, on the obs
/// graphics thread, registered inside `attach_display`), which touches nothing
/// in here, plus the stdin reader and the status thread, neither of which can
/// reach `Mirror` at all.
struct App {
    mirror: Mirror,
    /// The region currently being mirrored, i.e. the last one `Mirror` actually
    /// applied. Tracked here only so `sharing_started` can report the truth:
    /// the shell is free to send `move` while the prompt is still up (Clowd
    /// repositions its border before the user has pressed anything), and the
    /// region at that point is no longer the one from `--region`.
    region: Rect,
}

impl AppEvents for App {
    fn mirror_ready(&mut self, handle: *mut std::ffi::c_void) {
        self.mirror.attach_display(handle);

        // `sharing_started` is emitted HERE, not in the platform layer, and the
        // platform layers must not emit it themselves. Two reasons: the ui
        // contract already guarantees this callback fires exactly once and only
        // after the window has been restyled, resized and parked off screen, so
        // emitting from here is exactly-once for free and needs no flag; and it
        // is the first instant at which the message is actually true, because
        // the obs display does not exist — nothing is being mirrored — until
        // the `attach_display` above returns.
        //
        // `region_changed` and `obscure` are the other way round and stay with
        // the platform layer's command drain: a `move` is not fully applied
        // until the window itself has been resized, which happens after
        // `set_region` returns.
        status::emit_sharing_started(self.region);

        // Opens the gate on the 1 Hz `status` lines, which have nothing to
        // report until frames are being drawn. Ordered after the line above so
        // the shell can never see an fps reading before it has been told that
        // sharing began.
        status::set_sharing(true);
    }

    fn set_region(&mut self, region: Rect) -> Result<Rect, String> {
        // On `Err` nothing changed — `Mirror::set_region` plans before it
        // mutates — so the cached region is deliberately left alone and the
        // caller turns the reason into `command_error` instead of an ack.
        let applied = self.mirror.set_region(region)?;
        self.region = applied;
        Ok(applied)
    }

    fn set_obscure(&mut self, mode: obscure::Mode) {
        obscure::set_mode(mode);
    }

    fn quit(&mut self) -> ! {
        obs_platform::exit_process(0)
    }
}

fn main() {
    // Route every libobs log line to stderr (and install the crash handler)
    // before anything else can touch libobs.
    obs::log::install_handlers();

    let cli = Cli::parse();
    let region = match region::parse_region(&cli.region) {
        Ok(r) => r,
        Err(e) => {
            eprintln!("Error: {e}");
            obs_platform::exit_process(2);
        }
    };

    // DPI awareness (Windows) must be set before any window is created or
    // monitor is enumerated; no-op on macOS.
    obs_platform::init_process();

    // macOS: the NSApplication must exist, with its activation policy set,
    // BEFORE the obs bootstrap brings up Metal graphics. Accessory = no Dock
    // icon and no menu bar, but the prompt window still appears in window
    // pickers, which is the whole point. Kept minimal here — the heavy AppKit
    // work (the window, the event loop) lives in ui/appkit.rs, which runs later
    // on this same main thread.
    #[cfg(target_os = "macos")]
    {
        use objc2::MainThreadMarker;
        use objc2_app_kit::{NSApplication, NSApplicationActivationPolicy};
        let Some(mtm) = MainThreadMarker::new() else {
            eprintln!("Fatal: main() is not on the main thread");
            obs_platform::exit_process(1);
        };
        let app = NSApplication::sharedApplication(mtm);
        let _ = app.setActivationPolicy(NSApplicationActivationPolicy::Accessory);
    }

    // Ctrl+C / SIGTERM behave like a `quit` command: clean exit 0. exit_process
    // is `_exit`, which is async-signal-safe.
    #[cfg(unix)]
    {
        if let Err(e) = ctrlc::set_handler(|| obs_platform::exit_process(0)) {
            eprintln!("Warning: failed to install console signal handler: {e}");
        }
    }

    // Exits the process itself on any construction failure (never unwinds —
    // partial OBS state is never torn down).
    let mirror = Mirror::bootstrap(region, cli.fps, !cli.no_cursor);
    // The region the mirror actually adopted, not the one that was asked for:
    // `bootstrap` floors and evens the rect exactly as `move` does. Everything
    // downstream — the window's size, `sharing_started` — must use this, or the
    // very first region on the wire would be one no later `move` could
    // reproduce.
    let region = mirror.region();

    // Both helper threads start before `initialized` goes out, because that
    // line is the shell's cue to start talking to us: a `quit` sent the instant
    // it is read must find a reader on stdin, and the status thread is silent
    // until `set_sharing(true)` anyway, so there is no cost to having it up
    // early. Commands that arrive before the event loop exists are not lost
    // either — `ui::post_command` queues them, and the loop drains the queue
    // once it starts.
    status::start_status_thread();
    commands::spawn_stdin_thread();

    // `initialized` is deliberately NOT emitted here, even though libobs is up
    // and the region has resolved by this point: the message also promises that
    // the prompt window is showing, and that window does not exist until
    // `ui::run` creates it. The platform layer emits the line itself, the
    // instant the window has been created, shown and activated. Anything less
    // races the shell's out-of-band reactions to `initialized` — finding the
    // window by title or class to point the user at it, screenshotting it for a
    // picker hint — which are not stdin traffic and so are not covered by the
    // queue that protects commands sent this early.
    //
    // Never returns: the platform event loop runs until events.quit() →
    // exit_process(0), or a fatal error exits underneath it.
    ui::run(
        region,
        UiConfig { title: cli.title },
        Box::new(App { mirror, region }),
    )
}
