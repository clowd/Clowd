//! The driver's command line, spawned by `Clowd.Ui`
//! (`Scroll/ScrollDriver.cs: BuildArguments`) and documented in
//! CAPTURE_PROTOCOL.md §3.
//!
//! Every argument is `Option`al to clap even though the driver needs all
//! but one of them. A missing flag has to come back as a `fatal_error`
//! line on the protocol channel — the only place the shell is listening —
//! and not as a clap usage error on stderr with a non-zero exit, which the
//! shell would only be able to report as "the driver died". Validation
//! therefore lives in `drive::DriveArgs::from_cli`, not in the derive.

use std::path::PathBuf;

use clap::Parser;

use clowd_rust_core::geometry::{RectExt, ScreenPoint, ScreenRect};

#[derive(Debug, Parser)]
#[command(version, about = "Clowd scrolling capture driver")]
pub struct CliArgs {
    /// Directory to write the finished session into — the same directory
    /// the overlay left its `action.txt` marker in. `session.json` is
    /// written last; its presence is what tells the shell the payload is
    /// complete.
    #[arg(long, value_name = "PATH")]
    pub session_dir: Option<PathBuf>,

    /// Capture region, `X,Y,W,H` in the platform capture space — physical
    /// virtual-desktop pixels on Windows, CG points on macOS. The same space
    /// and format the overlay's `scroll` action marker uses, passed through
    /// verbatim by the shell. `allow_hyphen_values`: a monitor left of or
    /// above the primary puts the whole region at negative coordinates, and
    /// without it clap reads a separate-token value like `-1920,0,…` as an
    /// unknown flag and refuses the command line.
    #[arg(long, value_name = "X,Y,W,H", value_parser = parse_region, allow_hyphen_values = true)]
    pub region: Option<ScreenRect>,

    /// Scroll point, `PX,PY` in the same space as `--region` (negative
    /// coordinates included, hence `allow_hyphen_values`). The cursor is
    /// parked here for the whole run and every wheel event is aimed at it,
    /// so it decides which pane scrolls.
    #[arg(long, value_name = "PX,PY", value_parser = parse_point, allow_hyphen_values = true)]
    pub point: Option<ScreenPoint>,

    /// Target window handle as a decimal integer, as resolved by the
    /// overlay when the user picked the scroll point: an `HWND` on Windows, a
    /// `CGWindowID` on macOS. `0` (the default) or a handle that no longer
    /// holds up means "work it out from `--point`" — the driver re-validates
    /// it either way. The flag keeps its Win32 name on both platforms
    /// because it is a wire contract with the shell
    /// (`ScrollDriver.BuildArguments`), and one spelling is easier to keep
    /// honest than two.
    #[arg(long, value_name = "N", default_value_t = 0, allow_hyphen_values = true)]
    pub hwnd: i64,

    /// Start capturing from wherever the document is sitting instead of
    /// rewinding it to the top first.
    ///
    /// Negative because rewinding is the default: a user who selects a
    /// region halfway down a page almost always wants the whole page, and
    /// silently capturing only the bottom half gives them no sign the top
    /// is missing. The shell passes this when the user has turned the
    /// setting off, which is the "capture from here" intent — a long
    /// thread from one particular message.
    #[arg(long)]
    pub no_rewind: bool,
}

/// `--region X,Y,W,H`. Zero-area regions are rejected here rather than
/// deeper in the driver: a 0-wide capture fails with an OS error that says
/// nothing about where the bad rect came from.
fn parse_region(s: &str) -> Result<ScreenRect, String> {
    let n = parse_i32_list::<4>(s)?;
    if n[2] <= 0 || n[3] <= 0 {
        return Err(format!("'{s}' has a non-positive width or height"));
    }
    Ok(ScreenRect::from_xy_size(n[0], n[1], n[2], n[3]))
}

/// `--point PX,PY`. Negative coordinates are legal and common — a monitor
/// left of or above the primary one lives at negative virtual-desktop
/// coordinates.
fn parse_point(s: &str) -> Result<ScreenPoint, String> {
    let n = parse_i32_list::<2>(s)?;
    Ok(ScreenPoint::new(n[0], n[1]))
}

/// Exactly `N` comma-separated decimal integers, no more and no fewer.
fn parse_i32_list<const N: usize>(s: &str) -> Result<[i32; N], String> {
    let mut out = [0i32; N];
    let mut parts = s.split(',');
    for slot in out.iter_mut() {
        let part = parts
            .next()
            .ok_or_else(|| format!("'{s}' needs {N} comma-separated integers"))?
            .trim();
        *slot = part
            .parse()
            .map_err(|_| format!("'{part}' is not an integer"))?;
    }
    if parts.next().is_some() {
        return Err(format!("'{s}' needs exactly {N} comma-separated integers"));
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn region_parses_and_rejects_degenerate() {
        assert_eq!(parse_region("10,20,300,400").unwrap(), ScreenRect::from_xy_size(10, 20, 300, 400));
        // Secondary monitor left of the primary: negative origin, positive size.
        assert_eq!(
            parse_region("-1920,0,1920,1080").unwrap(),
            ScreenRect::from_xy_size(-1920, 0, 1920, 1080)
        );
        assert_eq!(parse_region(" 1 , 2 , 3 , 4 ").unwrap(), ScreenRect::from_xy_size(1, 2, 3, 4));
        assert!(parse_region("10,20,0,400").is_err());
        assert!(parse_region("10,20,300,-1").is_err());
        assert!(parse_region("10,20,300").is_err());
        assert!(parse_region("10,20,300,400,500").is_err());
        assert!(parse_region("10,20,300,x").is_err());
    }

    #[test]
    fn point_parses_negative_coordinates() {
        assert_eq!(parse_point("40,50").unwrap(), ScreenPoint::new(40, 50));
        assert_eq!(parse_point("-40,-50").unwrap(), ScreenPoint::new(-40, -50));
        assert!(parse_point("40").is_err());
        assert!(parse_point("40,50,60").is_err());
    }

    #[test]
    fn flags_parse() {
        let cli = CliArgs::parse_from([
            "clowd_scroll_driver",
            "--session-dir",
            "C:/tmp/session",
            "--region",
            "100,200,800,600",
            "--point",
            "450,500",
            "--hwnd",
            "133756",
        ]);
        assert_eq!(cli.region, Some(ScreenRect::from_xy_size(100, 200, 800, 600)));
        assert_eq!(cli.point, Some(ScreenPoint::new(450, 500)));
        assert_eq!(cli.hwnd, 133756);
        assert_eq!(cli.session_dir.as_deref(), Some(std::path::Path::new("C:/tmp/session")));
    }

    #[test]
    fn flags_accept_negative_origins_as_separate_tokens() {
        // A monitor left of or above the primary puts the region and point
        // at negative virtual-desktop coordinates, and the shell passes
        // each flag and its value as separate argv tokens. Without
        // allow_hyphen_values clap reads "-1920,…" as an unknown flag and
        // the driver dies with a usage error before emitting a single
        // protocol line — making scrolling capture unusable on that
        // monitor.
        let cli = CliArgs::try_parse_from([
            "clowd_scroll_driver",
            "--session-dir",
            "C:/tmp/s",
            "--region",
            "-1920,-1080,1920,1080",
            "--point",
            "-960,-500",
            "--hwnd",
            "133756",
        ])
        .expect("separate-token negative coordinates must parse");
        assert_eq!(cli.region, Some(ScreenRect::from_xy_size(-1920, -1080, 1920, 1080)));
        assert_eq!(cli.point, Some(ScreenPoint::new(-960, -500)));
        assert_eq!(cli.hwnd, 133756);

        // The shell uses the `--flag=value` single-token spelling; both
        // forms must keep parsing.
        let eq_form = CliArgs::try_parse_from([
            "clowd_scroll_driver",
            "--region=-1920,0,1920,1080",
            "--point=-960,500",
            "--hwnd=133756",
        ])
        .expect("single-token negative coordinates must parse");
        assert_eq!(eq_form.region, Some(ScreenRect::from_xy_size(-1920, 0, 1920, 1080)));
        assert_eq!(eq_form.point, Some(ScreenPoint::new(-960, 500)));
        assert_eq!(eq_form.hwnd, 133756);
    }

    #[test]
    fn a_bare_command_line_parses_and_defers_its_complaints() {
        // Nothing is required by the derive on purpose — see the module
        // note. `drive::DriveArgs::from_cli` is what rejects this, as a
        // protocol event.
        let cli = CliArgs::parse_from(["clowd_scroll_driver"]);
        assert_eq!(cli.session_dir, None);
        assert_eq!(cli.region, None);
        assert_eq!(cli.point, None);
        assert_eq!(cli.hwnd, 0);
    }
}
