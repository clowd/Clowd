//! Window corner radius — the platform-neutral half.
//!
//! Every OS since Windows 11 / macOS 11 composites top-level windows with
//! rounded corners, and the radius differs per OS, per OS version, and (on
//! macOS Tahoe) per window style. The capturer wants that radius for two
//! things: drawing the selection border as the same rounded shape the user
//! sees, and leaving the corner pixels transparent in the copied/saved
//! image instead of shipping a few pixels of whatever sat behind the
//! window.
//!
//! This module holds the decisions that need no OS call so they can be
//! unit-tested on any host: the Windows 11 rounding policy
//! ([`windows_corner_radius_logical`]), the macOS lookup table used until
//! (or if) the window server can be asked ([`macos_fallback_radius_points`]),
//! and the estimator that turns a captured corner's alpha channel into a
//! radius ([`estimate_radius_from_alpha`]). The Win32 / CoreGraphics glue
//! lives next to each walker.

// Each half is only called from its own platform's walker glue — the
// Windows policy from `win_corners`, the macOS table + alpha estimator from
// `mac_corners` — but the whole module is compiled (and its tests run) on
// every host so either build still checks the other platform's logic.
#![allow(dead_code)]

/// First Windows 11 build. Windows 10 never rounds.
pub const WINDOWS_11_FIRST_BUILD: u32 = 22000;

/// DWM's `DWMWCP_ROUND` radius at 96 DPI (Windows 11 Geometry: top-level
/// windows round at 8 px, auxiliary UI at 4 px).
pub const WIN11_ROUND_LOGICAL: f32 = 8.0;
/// DWM's `DWMWCP_ROUNDSMALL` radius at 96 DPI.
pub const WIN11_ROUND_SMALL_LOGICAL: f32 = 4.0;

/// Below this (physical px) a measured radius is noise — an AA fringe on a
/// square window, not a curve.
pub const MIN_MEASURED_RADIUS_PX: f32 = 2.0;

/// `DWMWA_WINDOW_CORNER_PREFERENCE`, as read back from DWM.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CornerPreference {
    /// `DWMWCP_DEFAULT` — the system decides.
    Default,
    /// `DWMWCP_DONOTROUND`.
    DoNotRound,
    /// `DWMWCP_ROUND`.
    Round,
    /// `DWMWCP_ROUNDSMALL`.
    RoundSmall,
}

/// The inputs to the Windows 11 rounding policy for one top-level window.
#[derive(Debug, Clone, Copy)]
pub struct WindowsCornerInputs {
    /// `RtlGetVersion().dwBuildNumber`.
    pub build: u32,
    /// `GetSystemMetrics(SM_REMOTESESSION) != 0` — DWM does not round in
    /// remote / VM sessions.
    pub remote_session: bool,
    /// `IsZoomed(hwnd)` — maximized windows are never rounded.
    pub maximized: bool,
    /// `IsWindowArranged(hwnd)` — snapped windows are never rounded.
    pub arranged: bool,
    /// What `DwmGetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)` said,
    /// or `None` when the attribute could not be read.
    pub preference: Option<CornerPreference>,
    /// `GetWindowRgn` returned a real region — windows shaped with
    /// `SetWindowRgn` are never rounded.
    pub has_region: bool,
    /// The window has a frame DWM can round: full `WS_CAPTION` or
    /// `WS_THICKFRAME`.
    pub has_frame: bool,
}

/// Corner radius in *logical* (96-DPI) pixels for a Windows top-level
/// window. 0 = square. Mirrors the policy documented in "Apply rounded
/// corners in desktop apps": never on Windows 10, never when maximized /
/// snapped / remote, an explicit DWM preference wins, otherwise only
/// framed, un-regioned windows round at the default 8 px.
pub fn windows_corner_radius_logical(i: WindowsCornerInputs) -> f32 {
    if i.build < WINDOWS_11_FIRST_BUILD || i.remote_session || i.maximized || i.arranged {
        return 0.0;
    }
    match i.preference {
        Some(CornerPreference::DoNotRound) => 0.0,
        Some(CornerPreference::Round) => WIN11_ROUND_LOGICAL,
        Some(CornerPreference::RoundSmall) => WIN11_ROUND_SMALL_LOGICAL,
        Some(CornerPreference::Default) | None => {
            if i.has_region || !i.has_frame {
                0.0
            } else {
                WIN11_ROUND_LOGICAL
            }
        }
    }
}

/// macOS window corner radius in points for an OS major version, used when
/// the window server cannot be asked (the probe in `mac_corners` is the
/// primary source — Tahoe alone has at least three radii depending on the
/// window's chrome). Big Sur through Sequoia: 10 pt. Tahoe: 16 pt is the
/// titlebar-only default (toolbar windows are 26 pt, which the probe picks
/// up). Anything older or newer than the table: 0, i.e. square.
pub fn macos_fallback_radius_points(major: i64) -> f32 {
    match major {
        11..=15 => 10.0,
        26 => 16.0,
        _ => 0.0,
    }
}

/// Which corner of the window a probe image covers; the estimator flips
/// the buffer so the curve is always analysed as if it were top-left.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Corner {
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// Estimate a circular corner radius (in the buffer's pixel units) from
/// the alpha channel of a `w`×`h` image whose `corner` coincides with the
/// window's corner. `alpha(x, y)` returns 0..=255.
///
/// Method: the transparent area of a quarter circle cut from a square is
/// `(1 - π/4)·r²`, so `r = sqrt(A / (1 - π/4))`. Two corrections make that
/// robust against the window server's anti-aliasing: the straight edges
/// adjoining the corner also carry a fractional-alpha fringe, estimated
/// from the 8 rows / columns farthest from the corner (straight edge there
/// as long as the probe is larger than the radius) and subtracted; and
/// radii under [`MIN_MEASURED_RADIUS_PX`] collapse to 0, since a square
/// window still measures a fraction of a pixel from its edge fringe.
pub fn estimate_radius_from_alpha(w: usize, h: usize, corner: Corner, alpha: impl Fn(usize, usize) -> u8) -> f32 {
    if w < 4 || h < 4 {
        return 0.0;
    }
    // Re-index so the corner of interest is at (0, 0).
    let at = |x: usize, y: usize| -> f32 {
        let (sx, sy) = match corner {
            Corner::TopLeft => (x, y),
            Corner::TopRight => (w - 1 - x, y),
            Corner::BottomLeft => (x, h - 1 - y),
            Corner::BottomRight => (w - 1 - x, h - 1 - y),
        };
        1.0 - alpha(sx, sy) as f32 / 255.0
    };

    let mut total = 0.0f32;
    for y in 0..h {
        for x in 0..w {
            total += at(x, y);
        }
    }

    // Straight-edge fringe: mean transparency per row along the left edge
    // (taken far from the corner) and per column along the top edge.
    let band = 8.min(h / 2).min(w / 2).max(1);
    let mut left_edge = 0.0f32;
    for y in (h - band)..h {
        for x in 0..w {
            left_edge += at(x, y);
        }
    }
    left_edge /= band as f32;
    let mut top_edge = 0.0f32;
    for x in (w - band)..w {
        for y in 0..h {
            top_edge += at(x, y);
        }
    }
    top_edge /= band as f32;

    let corner_area = (total - left_edge * h as f32 - top_edge * w as f32).max(0.0);
    let r = (corner_area / (1.0 - std::f32::consts::FRAC_PI_4)).sqrt();
    if r < MIN_MEASURED_RADIUS_PX {
        0.0
    } else {
        r
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn inputs() -> WindowsCornerInputs {
        WindowsCornerInputs {
            build: 22631,
            remote_session: false,
            maximized: false,
            arranged: false,
            preference: None,
            has_region: false,
            has_frame: true,
        }
    }

    #[test]
    fn windows_10_never_rounds() {
        let i = WindowsCornerInputs {
            build: 19045,
            preference: Some(CornerPreference::Round),
            ..inputs()
        };
        assert_eq!(windows_corner_radius_logical(i), 0.0);
    }

    #[test]
    fn windows_11_framed_window_rounds_by_default() {
        assert_eq!(windows_corner_radius_logical(inputs()), WIN11_ROUND_LOGICAL);
        let default_pref = WindowsCornerInputs {
            preference: Some(CornerPreference::Default),
            ..inputs()
        };
        assert_eq!(windows_corner_radius_logical(default_pref), WIN11_ROUND_LOGICAL);
    }

    #[test]
    fn maximized_snapped_remote_and_regioned_windows_are_square() {
        for i in [
            WindowsCornerInputs {
                maximized: true,
                ..inputs()
            },
            WindowsCornerInputs {
                arranged: true,
                ..inputs()
            },
            WindowsCornerInputs {
                remote_session: true,
                ..inputs()
            },
            WindowsCornerInputs {
                has_region: true,
                ..inputs()
            },
            WindowsCornerInputs {
                has_frame: false,
                ..inputs()
            },
        ] {
            assert_eq!(windows_corner_radius_logical(i), 0.0, "{i:?}");
        }
    }

    #[test]
    fn explicit_dwm_preference_wins_over_the_heuristic() {
        // An unframed popup that opted in (e.g. a custom menu) rounds.
        let small = WindowsCornerInputs {
            has_frame: false,
            preference: Some(CornerPreference::RoundSmall),
            ..inputs()
        };
        assert_eq!(windows_corner_radius_logical(small), WIN11_ROUND_SMALL_LOGICAL);
        let round = WindowsCornerInputs {
            has_frame: false,
            preference: Some(CornerPreference::Round),
            ..inputs()
        };
        assert_eq!(windows_corner_radius_logical(round), WIN11_ROUND_LOGICAL);
        // And a framed window that opted out stays square.
        let none = WindowsCornerInputs {
            preference: Some(CornerPreference::DoNotRound),
            ..inputs()
        };
        assert_eq!(windows_corner_radius_logical(none), 0.0);
    }

    #[test]
    fn macos_table_covers_big_sur_through_tahoe_only() {
        assert_eq!(macos_fallback_radius_points(10), 0.0);
        assert_eq!(macos_fallback_radius_points(11), 10.0);
        assert_eq!(macos_fallback_radius_points(15), 10.0);
        assert_eq!(macos_fallback_radius_points(26), 16.0);
        // Unknown future releases: square rather than a guess.
        assert_eq!(macos_fallback_radius_points(27), 0.0);
        assert_eq!(macos_fallback_radius_points(99), 0.0);
    }

    /// Synthetic window corner: a quarter circle of radius `r` at the
    /// given corner with ~1 px linear anti-aliasing, plus a faint fringe
    /// along both straight edges like the window server produces.
    fn synthetic_alpha(w: usize, h: usize, r: f32, corner: Corner) -> Vec<u8> {
        let mut buf = vec![255u8; w * h];
        for y in 0..h {
            for x in 0..w {
                let (cx, cy) = match corner {
                    Corner::TopLeft => (x, y),
                    Corner::TopRight => (w - 1 - x, y),
                    Corner::BottomLeft => (x, h - 1 - y),
                    Corner::BottomRight => (w - 1 - x, h - 1 - y),
                };
                let px = cx as f32 + 0.5;
                let py = cy as f32 + 0.5;
                let mut cov = 1.0f32;
                if px < r && py < r {
                    let d = ((px - r).powi(2) + (py - r).powi(2)).sqrt() - r;
                    cov = (0.5 - d).clamp(0.0, 1.0);
                }
                // Straight-edge fringe: the outermost row/column is 80 % opaque.
                if cx == 0 || cy == 0 {
                    cov *= 0.8;
                }
                buf[y * w + x] = (cov * 255.0).round() as u8;
            }
        }
        buf
    }

    #[test]
    fn estimator_recovers_synthetic_radii_at_every_corner() {
        for corner in [Corner::TopLeft, Corner::TopRight, Corner::BottomLeft, Corner::BottomRight] {
            for r in [16.0f32, 24.0, 32.0, 52.0] {
                let (w, h) = (96, 96);
                let buf = synthetic_alpha(w, h, r, corner);
                let est = estimate_radius_from_alpha(w, h, corner, |x, y| buf[y * w + x]);
                assert!((est - r).abs() < 1.5, "corner {corner:?} r={r}: estimated {est}");
            }
        }
    }

    #[test]
    fn estimator_reports_square_for_a_square_window() {
        let (w, h) = (96, 96);
        let buf = synthetic_alpha(w, h, 0.0, Corner::TopLeft);
        assert_eq!(estimate_radius_from_alpha(w, h, Corner::TopLeft, |x, y| buf[y * w + x]), 0.0);
        // Fully opaque, no fringe at all.
        assert_eq!(estimate_radius_from_alpha(w, h, Corner::TopLeft, |_, _| 255), 0.0);
    }

    #[test]
    fn estimator_tolerates_a_probe_smaller_than_the_band() {
        // Degenerate sizes must not panic or index out of range.
        assert_eq!(estimate_radius_from_alpha(2, 2, Corner::TopLeft, |_, _| 0), 0.0);
        let _ = estimate_radius_from_alpha(5, 9, Corner::BottomRight, |_, _| 0);
    }
}
