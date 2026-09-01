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

    /// Frame + handle-cluster color, as hex `#RRGGBB` or `#RRGGBBAA` (leading
    /// `#` optional). Same flag name, syntax and default as Clowd's wgpu
    /// capturer, so the shell can pass one accent string to both.
    #[arg(long, value_name = "HEX", default_value = "#2F7CAE", value_parser = parse_hex_color)]
    accent_color: (u8, u8, u8),

    /// TOTAL border thickness in logical (DPI-independent) px — not the accent
    /// line alone, which is what this flag used to mean. The total is split
    /// into an inner white hairline and an outer accent line, each a whole
    /// number of device pixels once DPI-scaled: 2+2 at 100%, 2+3 at 125%, 3+3
    /// at 150%. The odd pixel always goes to the accent, so the accent is never
    /// thinner than the hairline. Four is the floor simply because each line
    /// needs at least one device pixel at every scale we support; the resize
    /// handles have their own, larger floor and do not track this value
    /// (crates/obs-share-region/DESIGN.md §2).
    #[arg(long, default_value = "4", value_parser = clap::value_parser!(u32).range(4..=32))]
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

/// Hex color parser matching `clowd_capture`'s `parse_hex_color` byte for
/// byte in accepted syntax, so the shell can hand the same accent string to
/// the capturer and to us. A clap value_parser, so a bad value is a usage
/// error (exit 2) like any other malformed flag.
///
/// An `AA` suffix is accepted and ignored: the frame is painted with opaque
/// platform primitives (a GDI solid brush has no alpha at all), and silently
/// rejecting a string the capturer accepts would be worse than ignoring a
/// channel we cannot honour.
fn parse_hex_color(s: &str) -> Result<(u8, u8, u8), String> {
    let hex = s.trim_start_matches('#');
    if !matches!(hex.len(), 6 | 8) || !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(format!("'{s}' is not a #RRGGBB or #RRGGBBAA color"));
    }
    let channel = |i: usize| u8::from_str_radix(&hex[i..i + 2], 16).unwrap();
    let rgb = [channel(0), channel(2), channel(4)];
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
        accent: cli.accent_color,
        border: cli.border,
        resizable: !cli.no_resize,
        show_frame: !cli.no_frame,
        prompt: !cli.no_prompt,
    };

    // Never returns: the platform event loop runs until events.quit() →
    // exit_process(0), or a fatal error exits underneath it.
    ui::run(region, cfg, Box::new(App(mirror)))
}
