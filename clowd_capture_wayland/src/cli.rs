//! The command line, spawned by `Clowd.Ui` (see the crate docs for the
//! host contract).
//!
//! Unlike the scrolling-capture driver there is no protocol channel that
//! a bad command line would have to be reported on, so `--session-dir` is
//! simply required: a missing flag is a shell bug, and clap's usage error
//! on stderr with exit 2 is the right way for one to surface.

use std::path::PathBuf;

use clap::Parser;

#[derive(Debug, Parser)]
#[command(version, about = "Clowd Wayland screenshot capture (xdg-desktop-portal)")]
pub struct CliArgs {
    /// Directory to write the finished session into. `session.json` is
    /// written last; its presence is what tells the shell the payload is
    /// complete. Created if it does not exist.
    #[arg(long, value_name = "PATH")]
    pub session_dir: PathBuf,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn session_dir_parses() {
        let cli = CliArgs::parse_from(["clowd_capture_wayland", "--session-dir", "/tmp/session"]);
        assert_eq!(cli.session_dir, PathBuf::from("/tmp/session"));
    }

    #[test]
    fn session_dir_is_required() {
        assert!(CliArgs::try_parse_from(["clowd_capture_wayland"]).is_err());
    }
}
