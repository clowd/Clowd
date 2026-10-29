//! The helper protocol: the one stdio contract between a parent (Prisc.Ui, or prisc_capture
//! spawning prisc_ai) and every child process Prisc ships. The spec is
//! `crates/prisc_capture/HELPER_PROTOCOL.md`.
//!
//! The message catalogs in [`catalog`] are the single source of truth: the C# records, codecs
//! and golden samples in `ui/Prisc.Ipc/Generated/HelperMessages.g.cs` are generated from them
//! by `crates/prisc_codegen` (`cargo codegen`), as is the environment variable table in [`env`].
//!
//! A helper's `main` starts like this (§3.1):
//!
//! ```ignore
//! let out = prisc_ipc::open::<catalog::job::Event>("prisc_vid2gif");
//! prisc_ipc::lifetime::install(move |_| cancel.cancel(), catalog::job::QUIT_GRACE + prisc_ipc::SELF_EXIT_MARGIN);
//! prisc_ipc::read_commands::<catalog::job::Command>(|inbound| { /* ... */ ControlFlow::Continue(()) });
//! ```

use std::time::Duration;

/// The envelope version (framing, `hello`, `error`, `quit`), sent in every `hello`. Bumped by
/// hand and should almost never change; catalog changes are caught by `catalog_hash` instead.
pub const PROTOCOL: u32 = 1;

/// Added to a catalog's `QUIT_GRACE` for the helper's own hard-exit timer (§3.3), so a living
/// parent's kill always fires first and the timer only matters for orphans.
pub const SELF_EXIT_MARGIN: Duration = Duration::from_secs(5);

pub mod catalog;
mod channel;
pub mod env;
pub mod exit;
mod guard;
pub mod lifetime;
pub mod locate;
mod stdin;

pub use channel::{open, Channel};
pub use guard::claim_stdout;
pub use stdin::{read_commands, Inbound, MAX_LINE};

/// Collapses every whitespace run (newlines included) to one space and trims, so arbitrary error
/// text can never break the line protocol.
pub fn single_line(s: &str) -> String {
    s.split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
}

#[cfg(test)]
mod tests {
    #[test]
    fn single_line_collapses_whitespace() {
        assert_eq!(super::single_line("  a\r\nb\n\t c  "), "a b c");
        assert_eq!(super::single_line(""), "");
    }
}
