//! Linux: no share-region UI. `main` already exits with "not supported on
//! Linux" before the mirror is bootstrapped (see
//! `refuse_unsupported_platform`), so this `run` is never reached; it exists
//! only so the crate — and with it the workspace — builds on Linux, and it
//! refuses the same way should that ordering ever change.

use obs_platform::region::Rect;

use super::{AppEvents, UiConfig};

pub fn run(_region: Rect, _cfg: UiConfig, _events: Box<dyn AppEvents>) -> ! {
    eprintln!("Error: clowd_share_region is not supported on Linux");
    obs_platform::exit_process(1)
}
