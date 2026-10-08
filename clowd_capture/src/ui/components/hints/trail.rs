//! The accent comet that orbits a chip's border.
//!
//! Built as a mesh — a strip of seven concentric rings around the chip's
//! outline, sampled 128 times around. It shares its look with the floating
//! strips' entrance comet in the shell (clowd_ui/Clowd.Ui/Controls/Tray/
//! TrayComet.cs): a solid core line with a short faint halo rather than a
//! gaussian haze, a tail that tapers instead of going see-through, and a head
//! that runs hot — all in colours derived from the user's own accent pick
//! ([`comet_colors`]). Only the speed and length are the hints' own: a chip is
//! far smaller than a strip, and the comet orbits it forever.
//!
//! Everything below is a pure function of the chip rect and the host's own
//! clock, so nothing is retained between passes.

use egui::epaint::Mesh;
use egui::{pos2, vec2, Color32, Pos2, Rect, Vec2};

use crate::accent;

/// Orbits per second — one lap every 2.5 s, as the shader had it.
pub const SPEED: f32 = 0.4;
/// The comet's length, as a fraction of the perimeter.
pub const TRAIL_LEN: f32 = 0.4;
/// Samples around the perimeter.
pub const N: usize = 128;
/// The band across the rim, as (signed offset from the chip's edge in
/// points, positive outward; opacity) pairs: a solid core three rings wide
/// with a short, faint halo either side.
const BAND: [(f32, f32); 7] = [
    (-4.5, 0.0),
    (-2.5, 0.28),
    (-1.25, 1.0),
    (0.0, 1.0),
    (1.25, 1.0),
    (2.5, 0.28),
    (4.5, 0.0),
];
/// How thin the band gets at the very end of the tail, as a fraction of its
/// full width.
pub const TAIL_WIDTH: f32 = 0.10;

/// The comet's body and head colours from the accent as the user picked it
/// (`CapturerSettings::picked_accent_color`), straight alpha.
pub fn comet_colors(picked: [f32; 4]) -> (Color32, Color32) {
    let q = |v: f32| (v.clamp(0.0, 1.0) * 255.0).round() as u8;
    let (body, head) = accent::comet_colors([q(picked[0]), q(picked[1]), q(picked[2])]);
    (
        Color32::from_rgb(body[0], body[1], body[2]),
        Color32::from_rgb(head[0], head[1], head[2]),
    )
}

/// How a sample of `brightness` (0..1, from [`intensity`]) is drawn: the
/// band's width as a fraction of full, its opacity, and how far its colour has
/// moved from the body towards the head. Opacity saturates early so most of
/// the tail is solid colour; the head tint is confined to the very front.
pub fn look(brightness: f32) -> (f32, f32, f32) {
    let t = brightness.clamp(0.0, 1.0);
    (TAIL_WIDTH + (1.0 - TAIL_WIDTH) * t, (2.5 * t).min(1.0), t * t * t * t)
}

/// The point at normalised arc length `s` around the chip's rounded-rect
/// outline, and the outward unit normal there.
///
/// `s` runs clockwise on screen from where the top-left arc meets the top
/// edge, which is where the shader's own parameterisation started.
pub fn perimeter_point(rect: Rect, radius: f32, s: f32) -> (Pos2, Vec2) {
    use std::f32::consts::{FRAC_PI_2, PI};
    let r = radius.clamp(0.0, rect.width().min(rect.height()) / 2.0);
    // A zero radius still has to divide by something; the arcs are
    // zero-length then, so the value never reaches a result.
    let rd = r.max(1e-3);
    let (sw, sh, arc) = (rect.width() - 2.0 * r, rect.height() - 2.0 * r, FRAC_PI_2 * r);
    let mut d = s.rem_euclid(1.0) * (2.0 * sw + 2.0 * sh + 4.0 * arc);
    let on_arc = |c: Pos2, a0: f32, d: f32| {
        let a = a0 + d / rd;
        let n = vec2(a.cos(), a.sin());
        (c + n * r, n)
    };
    if d < sw {
        return (pos2(rect.left() + r + d, rect.top()), vec2(0.0, -1.0));
    }
    d -= sw;
    if d < arc {
        return on_arc(pos2(rect.right() - r, rect.top() + r), -FRAC_PI_2, d);
    }
    d -= arc;
    if d < sh {
        return (pos2(rect.right(), rect.top() + r + d), vec2(1.0, 0.0));
    }
    d -= sh;
    if d < arc {
        return on_arc(pos2(rect.right() - r, rect.bottom() - r), 0.0, d);
    }
    d -= arc;
    if d < sw {
        return (pos2(rect.right() - r - d, rect.bottom()), vec2(0.0, 1.0));
    }
    d -= sw;
    if d < arc {
        return on_arc(pos2(rect.left() + r, rect.bottom() - r), FRAC_PI_2, d);
    }
    d -= arc;
    if d < sh {
        return (pos2(rect.left(), rect.bottom() - r - d), vec2(-1.0, 0.0));
    }
    d -= sh;
    on_arc(pos2(rect.left() + r, rect.top() + r), PI, d)
}

/// The comet's brightness at arc length `s` with its head at `head`:
/// full at the head, fading over `TRAIL_LEN` behind it, dark elsewhere.
pub fn intensity(s: f32, head: f32) -> f32 {
    (1.0 - (head - s + 1.0).fract() / TRAIL_LEN)
        .max(0.0)
        .powf(1.5)
}

/// The comet as one triangle strip around the chip.
///
/// `border_w` puts the inner half of the band a border's width inside the
/// outline; `alpha_mul` is the chip's own fade; `time` is the host's own
/// clock, which is legal because a chip never straddles a monitor seam.
pub fn mesh(chip: Rect, radius: f32, border_w: f32, colors: (Color32, Color32), alpha_mul: f32, time: f64) -> Mesh {
    let (body, hot) = colors;
    let head = (time * SPEED as f64).fract() as f32;
    let mut m = Mesh::default();
    for i in 0..N {
        let s = i as f32 / N as f32;
        let (pt, n) = perimeter_point(chip, radius, s);
        let (width, opacity, heat) = look(intensity(s, head));
        let lerp = |a: u8, b: u8| (a as f32 + (b as f32 - a as f32) * heat).round() as u8;
        let (r, g, b) = (lerp(body.r(), hot.r()), lerp(body.g(), hot.g()), lerp(body.b(), hot.b()));
        for (d, band) in BAND {
            let mut offset = d * width;
            if offset < 0.0 {
                offset -= border_w;
            }
            let a = (opacity * band * alpha_mul).clamp(0.0, 1.0);
            m.colored_vertex(pt + n * offset, Color32::from_rgba_unmultiplied(r, g, b, (a * 255.0).round() as u8));
        }
    }
    let r = BAND.len() as u32;
    for i in 0..N as u32 {
        let j = (i + 1) % N as u32;
        for k in 0..r - 1 {
            let (a, b, c, d) = (i * r + k, i * r + k + 1, j * r + k, j * r + k + 1);
            m.add_triangle(a, b, c);
            m.add_triangle(b, d, c);
        }
    }
    m
}

#[cfg(test)]
mod tests {
    use super::*;

    fn chip() -> Rect {
        Rect::from_min_size(pos2(100.0, 200.0), vec2(160.0, 28.0))
    }

    /// The parameterisation has to come back to where it started and never
    /// jump: a gap between two samples would show as a notch in the comet.
    #[test]
    fn perimeter_closes_and_is_continuous() {
        let c = chip();
        let (start, _) = perimeter_point(c, 6.0, 0.0);
        let (end, _) = perimeter_point(c, 6.0, 1.0);
        assert!((start - end).length() < 1e-3, "{start:?} vs {end:?}");

        let step = c.width().max(c.height());
        let mut previous = start;
        for i in 1..=N {
            let (p, n) = perimeter_point(c, 6.0, i as f32 / N as f32);
            assert!((p - previous).length() < step / 4.0, "jump at sample {i}");
            assert!((n.length() - 1.0).abs() < 1e-3, "the normal is a unit vector");
            // Every sample is on the chip's outline, within its radius.
            assert!(c.expand(0.01).contains(p), "{p:?} left the chip at sample {i}");
            previous = p;
        }
    }

    #[test]
    fn intensity_is_one_at_the_head_and_zero_past_the_tail() {
        assert!((intensity(0.25, 0.25) - 1.0).abs() < 1e-6);
        // Half a trail behind the head is dimmer but still lit.
        let mid = intensity(0.25 - TRAIL_LEN / 2.0, 0.25);
        assert!(mid > 0.0 && mid < 1.0, "{mid}");
        // Anywhere past the tail is dark.
        assert_eq!(intensity(0.25 - TRAIL_LEN - 0.05, 0.25), 0.0);
        assert_eq!(intensity(0.5, 0.1), 0.0);
        // The tail wraps past zero: a head just past the origin is still
        // lit from the far end of the perimeter.
        assert!(intensity(0.98, 0.02) > 0.0);
        assert!(intensity(0.75, 0.1) > 0.0);
    }

    #[test]
    fn mesh_has_n_times_rings_vertices_and_is_valid() {
        let m = mesh(chip(), 6.0, 1.0, (Color32::RED, Color32::WHITE), 1.0, 1.25);
        assert_eq!(m.vertices.len(), N * BAND.len());
        assert_eq!(m.indices.len(), N * (BAND.len() - 1) * 6);
        assert!(m.is_valid());
    }

    #[test]
    fn tail_tapers_and_stays_solid_while_the_head_runs_hot() {
        assert_eq!(look(1.0), (1.0, 1.0, 1.0));
        let (width, opacity, heat) = look(0.5);
        assert_eq!(opacity, 1.0);
        assert!(width > TAIL_WIDTH && width < 1.0);
        assert!(heat < 0.1);
        assert_eq!(look(0.0), (TAIL_WIDTH, 0.0, 0.0));
    }
}
