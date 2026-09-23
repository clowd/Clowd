//! Monitor enumeration on X11, through RandR.
//!
//! `RRGetMonitors` (RandR 1.5) is the server's own notion of a display —
//! one entry per physical monitor, mirrored outputs folded into one, in
//! root-window pixels, which are physical pixels: X11 has no logical
//! coordinate space, so `MonitorInfo::bounds` is the RandR rect as-is and
//! the virtual desktop is the root window. The refresh rate comes from the
//! mode of the CRTC driving the monitor's first output.
//!
//! X11 has no per-monitor DPI either. The one scale the desktop agrees on
//! is `Xft.dpi` in the root window's resource database (what GNOME/KDE
//! write for the session's UI scale, and what winit reads for its own
//! scale factor), so every monitor gets that one scale, or 1.0 when it is
//! unreadable.

use anyhow::{Context, Result};
use x11rb::connection::Connection;
use x11rb::protocol::randr::{self, ConnectionExt as _, ModeFlag};
use x11rb::protocol::xproto::{AtomEnum, ConnectionExt as _};
use x11rb::rust_connection::RustConnection;

use crate::system::MonitorInfo;
use clowd_rust_core::geometry::{RectExt, ScreenRect};

pub fn all_monitors() -> Result<Vec<MonitorInfo>> {
    let (conn, screen_num) = x11rb::connect(None).context("cannot connect to the X server")?;
    let screen = &conn.setup().roots[screen_num];
    let root = screen.root;
    let scale_factor = xft_scale_factor(&conn, root).unwrap_or(1.0);

    let monitors = match conn
        .randr_get_monitors(root, true)
        .context("RRGetMonitors")?
        .reply()
    {
        Ok(reply) => reply.monitors,
        // No RandR 1.5 (Xvfb, an ancient server): the root window is the
        // one and only monitor, refresh unknown.
        Err(err) => {
            warn!("RRGetMonitors failed ({err}); treating the root window as one monitor");
            return Ok(vec![MonitorInfo {
                bounds: ScreenRect::from_xy_size(0, 0, i32::from(screen.width_in_pixels), i32::from(screen.height_in_pixels)),
                scale_factor,
                is_primary: true,
                refresh_hz: 60.0,
                name: "Screen".to_string(),
                adapter_id: None,
                low_vram_adapter: false,
            }]);
        }
    };

    // The mode table is one round-trip for every monitor; the per-output
    // CRTC lookups below index into it.
    let resources = conn
        .randr_get_screen_resources_current(root)
        .context("RRGetScreenResourcesCurrent")?
        .reply()
        .context("RRGetScreenResourcesCurrent")?;

    let mut out = Vec::with_capacity(monitors.len());
    for (i, m) in monitors.iter().enumerate() {
        if m.width == 0 || m.height == 0 {
            continue;
        }
        let name = conn
            .get_atom_name(m.name)
            .ok()
            .and_then(|c| c.reply().ok())
            .and_then(|r| String::from_utf8(r.name).ok())
            .unwrap_or_else(|| format!("Monitor {}", i + 1));
        let refresh_hz = m
            .outputs
            .iter()
            .find_map(|&output| output_refresh_hz(&conn, &resources, output))
            .unwrap_or(60.0);
        out.push(MonitorInfo {
            bounds: ScreenRect::from_xy_size(i32::from(m.x), i32::from(m.y), i32::from(m.width), i32::from(m.height)),
            scale_factor,
            is_primary: m.primary,
            refresh_hz,
            name,
            adapter_id: None,
            low_vram_adapter: false,
        });
    }

    // RandR flags at most one monitor primary and may flag none (a bare
    // xrandr setup); the app wants exactly one.
    if !out.is_empty() && !out.iter().any(|m| m.is_primary) {
        out[0].is_primary = true;
    }
    Ok(out)
}

/// Refresh rate of the mode the CRTC behind `output` is showing, computed
/// the way `xrandr` does it from the mode's timings. `None` when the output
/// is disconnected, has no CRTC, or the mode is not in the table.
fn output_refresh_hz(conn: &RustConnection, resources: &randr::GetScreenResourcesCurrentReply, output: randr::Output) -> Option<f32> {
    let info = conn
        .randr_get_output_info(output, resources.config_timestamp)
        .ok()?
        .reply()
        .ok()?;
    if info.crtc == x11rb::NONE {
        return None;
    }
    let crtc = conn
        .randr_get_crtc_info(info.crtc, resources.config_timestamp)
        .ok()?
        .reply()
        .ok()?;
    let mode = resources
        .modes
        .iter()
        .find(|mode| mode.id == crtc.mode)?;
    mode_refresh_hz(mode)
}

/// `dot_clock / (htotal * vtotal)`, with the doublescan and interlace
/// flags applied as xrandr does: doublescan halves the rate, interlace
/// doubles it.
fn mode_refresh_hz(mode: &randr::ModeInfo) -> Option<f32> {
    let mut vtotal = f64::from(mode.vtotal);
    if mode
        .mode_flags
        .contains(ModeFlag::DOUBLE_SCAN)
    {
        vtotal *= 2.0;
    }
    if mode.mode_flags.contains(ModeFlag::INTERLACE) {
        vtotal /= 2.0;
    }
    let denominator = f64::from(mode.htotal) * vtotal;
    if mode.dot_clock == 0 || denominator <= 0.0 {
        return None;
    }
    let hz = f64::from(mode.dot_clock) / denominator;
    (hz > 0.0).then_some(hz as f32)
}

/// The session's UI scale from `Xft.dpi` in the root window's
/// `RESOURCE_MANAGER` property (96 DPI = 1.0), or `None` when the property
/// is absent or carries no such line.
fn xft_scale_factor(conn: &RustConnection, root: x11rb::protocol::xproto::Window) -> Option<f32> {
    let reply = conn
        .get_property(false, root, AtomEnum::RESOURCE_MANAGER, AtomEnum::STRING, 0, u32::MAX)
        .ok()?
        .reply()
        .ok()?;
    let text = String::from_utf8_lossy(&reply.value);
    let dpi = parse_xft_dpi(&text)?;
    let scale = dpi / 96.0;
    (scale > 0.0).then_some(scale)
}

/// `Xft.dpi` out of an X resource database dump (`key:<tab>value` lines).
fn parse_xft_dpi(resources: &str) -> Option<f32> {
    resources.lines().find_map(|line| {
        let (key, value) = line.split_once(':')?;
        if key.trim() != "Xft.dpi" {
            return None;
        }
        value
            .trim()
            .parse::<f32>()
            .ok()
            .filter(|dpi| *dpi > 0.0)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn xft_dpi_parses_the_gnome_and_kde_spellings() {
        assert_eq!(parse_xft_dpi("Xft.dpi:\t96\nXft.antialias:\t1\n"), Some(96.0));
        assert_eq!(parse_xft_dpi("Xft.antialias: 1\nXft.dpi: 192\n"), Some(192.0));
        assert_eq!(parse_xft_dpi("Xft.dpi:\t120.5\n"), Some(120.5));
        assert_eq!(parse_xft_dpi("Xft.antialias:\t1\n"), None);
        assert_eq!(parse_xft_dpi("Xft.dpi:\tnope\n"), None);
        assert_eq!(parse_xft_dpi("Xft.dpi:\t0\n"), None);
    }

    #[test]
    fn mode_refresh_matches_xrandr() {
        let mode = |dot_clock, htotal, vtotal, flags| randr::ModeInfo {
            id: 1,
            width: 1920,
            height: 1080,
            dot_clock,
            hsync_start: 0,
            hsync_end: 0,
            htotal,
            hskew: 0,
            vsync_start: 0,
            vsync_end: 0,
            vtotal,
            name_len: 0,
            mode_flags: flags,
        };
        // 1920x1080 at 60 Hz: the CVT timings xrandr prints as 59.96.
        let hz = mode_refresh_hz(&mode(148_500_000, 2200, 1125, ModeFlag::default())).unwrap();
        assert!((hz - 60.0).abs() < 0.01, "{hz}");
        let interlaced = mode_refresh_hz(&mode(148_500_000, 2200, 1125, ModeFlag::INTERLACE)).unwrap();
        assert!((interlaced - 120.0).abs() < 0.01, "{interlaced}");
        let doublescan = mode_refresh_hz(&mode(148_500_000, 2200, 1125, ModeFlag::DOUBLE_SCAN)).unwrap();
        assert!((doublescan - 30.0).abs() < 0.01, "{doublescan}");
        assert_eq!(mode_refresh_hz(&mode(0, 2200, 1125, ModeFlag::default())), None);
        assert_eq!(mode_refresh_hz(&mode(148_500_000, 0, 1125, ModeFlag::default())), None);
    }
}
