//! `obs-share-region`: mirrors a rectangular screen region into an ordinary
//! titled window, so meeting apps that can only share "a whole screen" or
//! "one window" can be pointed at that window and effectively share a region
//! (SHARE_REGION_PLAN §1). Fully self-contained: CLI args in, runs until the
//! user closes it — no stdout protocol, no stdin commands; stderr carries the
//! libobs chatter.
//!
//! Exit codes match obs-express: 0 user quit, 1 runtime/obs error, 2 argument
//! validation. Every exit routes through `obs_platform::exit_process` — libobs
//! is never shut down (known OBS teardown crashes; see crates/obs/src/context.rs).

mod geometry;
mod mirror;
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
    /// X/Y may be negative; same space and parser as obs-express --region.
    #[arg(long, allow_hyphen_values = true)]
    region: String,

    /// Canvas frame rate of the mirror.
    #[arg(long, default_value = "30", value_parser = clap::value_parser!(u32).range(1..))]
    fps: u32,

    /// Mirror window title — the string the user has to find in the meeting
    /// app's window picker, so callers should set something recognisable.
    #[arg(long, default_value = "Shared Region")]
    title: String,

    /// Frame + handle-cluster color, R,G,B (0-255 each).
    #[arg(long, default_value = "0,120,215", value_parser = parse_accent)]
    accent: (u8, u8, u8),

    /// Frame border thickness in capture units.
    #[arg(long, default_value = "3", value_parser = clap::value_parser!(u32).range(1..=32))]
    border: u32,

    /// Do not capture the cursor (passed through to the display capture source).
    #[arg(long)]
    no_cursor: bool,

    /// Suppress the frame window: mirror + mask only (close via the mirror
    /// window or Ctrl+C).
    #[arg(long)]
    no_frame: bool,

    /// Frame can be moved but not resized.
    #[arg(long)]
    no_resize: bool,

    /// Skip the "share this window, then press OK" step and start mirroring
    /// immediately. The mirror then opens at the back under the mask, which
    /// share pickers that require clicking the window on screen cannot reach —
    /// only pass this when the caller selects the window some other way.
    #[arg(long)]
    no_prompt: bool,
}

/// `R,G,B` accent parser; a clap value_parser, so a bad value is a usage
/// error (exit 2) like any other malformed flag.
fn parse_accent(s: &str) -> Result<(u8, u8, u8), String> {
    let parts: Vec<&str> = s.split(',').map(str::trim).collect();
    if parts.len() != 3 {
        return Err("accent must be R,G,B".to_string());
    }
    let mut rgb = [0u8; 3];
    for (slot, part) in rgb.iter_mut().zip(&parts) {
        *slot = part
            .parse()
            .map_err(|_| format!("invalid accent component '{part}' (expected 0-255)"))?;
    }
    Ok((rgb[0], rgb[1], rgb[2]))
}

/// The `AppEvents` implementation: a trivial shim from UI callbacks onto
/// [`Mirror`].
///
/// Threading contract: the platform UI layer delivers every callback on the
/// main/UI thread — the same thread that ran `main` and bootstrapped the
/// mirror — so `Mirror` needs no synchronization. The only app code running
/// on any other thread is the obs_display draw callback (obs graphics
/// thread), registered inside `attach_display`, which touches no app state.
struct App(Mirror);

impl AppEvents for App {
    fn mirror_ready(&mut self, handle: *mut std::ffi::c_void) {
        self.0.attach_display(handle);
    }

    fn region_moved(&mut self, region: Rect) {
        self.0.move_region(region);
    }

    fn region_committed(&mut self, region: Rect) -> Rect {
        self.0.commit_region(region)
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
    // icon and no menu bar, but the mirror window still appears in window
    // pickers, which is the whole point. Kept minimal here — the heavy AppKit
    // work (windows, event loop) lives in ui/appkit.rs, which runs later on
    // this same main thread.
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

    // Ctrl+C / SIGTERM behave like closing the mirror window: clean exit 0.
    // exit_process is `_exit`, which is async-signal-safe.
    #[cfg(unix)]
    {
        if let Err(e) = ctrlc::set_handler(|| obs_platform::exit_process(0)) {
            eprintln!("Warning: failed to install console signal handler: {e}");
        }
    }

    // Exits the process itself on any construction failure (never unwinds —
    // partial OBS state is never torn down).
    let mirror = Mirror::bootstrap(region, cli.fps, !cli.no_cursor);

    let cfg = UiConfig {
        title: cli.title,
        accent: cli.accent,
        border: cli.border,
        resizable: !cli.no_resize,
        show_frame: !cli.no_frame,
        prompt: !cli.no_prompt,
    };

    // Never returns: the platform event loop runs until events.quit() →
    // exit_process(0), or a fatal error exits underneath it.
    ui::run(region, cfg, Box::new(App(mirror)))
}
