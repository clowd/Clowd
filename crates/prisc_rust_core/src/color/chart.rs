//! The colour picker's OKLCH gamut charts and slider tracks, painted per pixel here so the picker
//! crosses into native code once per bitmap rather than once per pixel.
//!
//! A chart is a slice through OKLCH at the current value of one channel, painted wherever sRGB can
//! show the colour and left transparent where it cannot, so the shape of the gamut is the picture
//! (as oklch.com draws them):
//!
//! - [`Channel::Lightness`] held: hue across (0 to 360), chroma up (0 to `OKLCH_CHROMA_MAX`).
//! - [`Channel::Chroma`] held: hue across, lightness up (0 to 1).
//! - [`Channel::Hue`] held: lightness across, chroma up.
//!
//! Pixels are premultiplied BGRA, one `u32` each as `0xAARRGGBB` (Avalonia's `Bgra8888` read as a
//! little-endian word), row-major with the top row first.

use super::OkLch;
use crate::contract::accent::OKLCH_CHROMA_MAX;
use std::sync::LazyLock;

/// One of the three OKLCH channels: the one a chart holds fixed, or the one a track sweeps.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Channel {
    Lightness,
    Chroma,
    Hue,
}

/// An edge pixel is sampled this many times a side to find how much of it the gamut covers.
const EDGE_SAMPLES: u32 = 4;

/// Linear light (0..1 in 4096 steps) to 8-bit sRGB: a table, since a chart is ~100k pixels and the
/// exact curve is a `powf` per channel.
static ENCODE_TABLE: LazyLock<[u8; 4097]> = LazyLock::new(|| {
    let mut table = [0u8; 4097];
    for (i, entry) in table.iter_mut().enumerate() {
        *entry = super::encode_round(i as f64 / 4096.0);
    }
    table
});

fn encoded(linear: f64) -> u32 {
    ENCODE_TABLE[(linear.clamp(0.0, 1.0) * 4096.0 + 0.5) as usize] as u32
}

/// An opaque pixel for a linear-light colour.
fn opaque([r, g, b]: [f64; 3]) -> u32 {
    0xFF00_0000 | encoded(r) << 16 | encoded(g) << 8 | encoded(b)
}

/// The axes of a chart that holds `fixed`: how far each runs, left to right and bottom to top.
fn extents(fixed: Channel) -> (f64, f64) {
    let x_max = if fixed == Channel::Hue { 1.0 } else { 360.0 };
    let y_max = if fixed == Channel::Chroma { 1.0 } else { OKLCH_CHROMA_MAX };
    (x_max, y_max)
}

/// The colour at chart coordinates (`x`, `y`) of a chart through `value` holding `fixed`.
fn at(fixed: Channel, value: OkLch, x: f64, y: f64) -> OkLch {
    match fixed {
        Channel::Lightness => OkLch {
            l: value.l,
            c: y,
            h_deg: x,
        },
        Channel::Chroma => OkLch {
            l: y,
            c: value.c,
            h_deg: x,
        },
        Channel::Hue => OkLch {
            l: x,
            c: y,
            h_deg: value.h_deg,
        },
    }
}

/// Paints the chart through `value` that holds `fixed` into `pixels` (`width * height` of them).
/// Every pixel is first sampled once, at its centre; then the pixels on the gamut's edge (any whose
/// neighbours disagree with it) are sampled again on a grid and given the share of them that is in
/// gamut as their alpha, so the cut-out edge is smooth.
///
/// # Panics
/// When `pixels` does not hold exactly `width * height` pixels.
pub fn paint_slice(fixed: Channel, value: OkLch, width: usize, height: usize, pixels: &mut [u32]) {
    assert_eq!(pixels.len(), width * height, "a {width}x{height} chart needs that many pixels");
    let (x_max, y_max) = extents(fixed);
    let mut inside = vec![false; pixels.len()];

    for py in 0..height {
        let y = (1.0 - (py as f64 + 0.5) / height as f64) * y_max;
        for px in 0..width {
            let x = (px as f64 + 0.5) / width as f64 * x_max;
            let i = py * width + px;
            match at(fixed, value, x, y).to_linear_srgb() {
                Some(rgb) => {
                    inside[i] = true;
                    pixels[i] = opaque(rgb);
                }
                None => pixels[i] = 0,
            }
        }
    }

    for py in 0..height {
        for px in 0..width {
            if on_edge(&inside, px, py, width, height) {
                pixels[py * width + px] = edge_pixel(fixed, value, px, py, width, height);
            }
        }
    }
}

fn on_edge(inside: &[bool], px: usize, py: usize, width: usize, height: usize) -> bool {
    let here = inside[py * width + px];
    let rows = py.saturating_sub(1)..=(py + 1).min(height - 1);
    rows.into_iter()
        .any(|ny| (px.saturating_sub(1)..=(px + 1).min(width - 1)).any(|nx| inside[ny * width + nx] != here))
}

/// A premultiplied pixel: the mean of its in-gamut samples, at the coverage they make up.
fn edge_pixel(fixed: Channel, value: OkLch, px: usize, py: usize, width: usize, height: usize) -> u32 {
    let (x_max, y_max) = extents(fixed);
    let n = EDGE_SAMPLES as f64;
    let mut sum = [0.0f64; 3];
    let mut hits = 0u32;
    for sy in 0..EDGE_SAMPLES {
        let y = (1.0 - (py as f64 + (sy as f64 + 0.5) / n) / height as f64) * y_max;
        for sx in 0..EDGE_SAMPLES {
            let x = (px as f64 + (sx as f64 + 0.5) / n) / width as f64 * x_max;
            if let Some([r, g, b]) = at(fixed, value, x, y).to_linear_srgb() {
                sum = [sum[0] + r, sum[1] + g, sum[2] + b];
                hits += 1;
            }
        }
    }

    if hits == 0 {
        return 0;
    }

    let alpha = (255.0 * hits as f64 / (n * n)).round_ties_even() as u32;
    let premultiplied = |linear: f64| (encoded(linear / hits as f64) * alpha + 127) / 255;
    alpha << 24 | premultiplied(sum[0]) << 16 | premultiplied(sum[1]) << 8 | premultiplied(sum[2])
}

/// Paints a slider track: `channel` swept across its range left to right (`t` = (i + 0.5) / len of
/// lightness 0..1, chroma 0..`OKLCH_CHROMA_MAX` or hue 0..360) at `value`'s other two channels,
/// opaque where sRGB has the colour and transparent where it does not.
pub fn paint_track(channel: Channel, value: OkLch, pixels: &mut [u32]) {
    let n = pixels.len() as f64;
    for (i, pixel) in pixels.iter_mut().enumerate() {
        let t = (i as f64 + 0.5) / n;
        let sample = match channel {
            Channel::Lightness => OkLch {
                l: t,
                ..value
            },
            Channel::Chroma => OkLch {
                c: t * OKLCH_CHROMA_MAX,
                ..value
            },
            Channel::Hue => OkLch {
                h_deg: t * 360.0,
                ..value
            },
        };
        *pixel = sample.to_linear_srgb().map_or(0, opaque);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// One painted bitmap from `chart_vectors.txt`.
    struct Case {
        painter: String,
        channel: Channel,
        value: OkLch,
        width: usize,
        height: usize,
        pixels: Vec<u32>,
    }

    fn cases() -> Vec<Case> {
        let mut cases: Vec<Case> = Vec::new();
        for line in include_str!("chart_vectors.txt").lines() {
            if line.starts_with('#') || line.is_empty() {
                continue;
            }
            if line.starts_with("slice") || line.starts_with("track") {
                let f: Vec<&str> = line.split(' ').collect();
                let channel = match f[1] {
                    "Lightness" => Channel::Lightness,
                    "Chroma" => Channel::Chroma,
                    _ => Channel::Hue,
                };
                let num = |s: &str| s.parse::<f64>().unwrap();
                let (width, height) = if f[0] == "slice" {
                    (f[5].parse().unwrap(), f[6].parse().unwrap())
                } else {
                    (f[5].parse().unwrap(), 1)
                };
                cases.push(Case {
                    painter: f[0].to_string(),
                    channel,
                    value: OkLch {
                        l: num(f[2]),
                        c: num(f[3]),
                        h_deg: num(f[4]),
                    },
                    width,
                    height,
                    pixels: Vec::new(),
                });
                continue;
            }
            let case = cases.last_mut().unwrap();
            for token in line.split(' ') {
                match token.strip_prefix("0*") {
                    Some(run) => case
                        .pixels
                        .extend(std::iter::repeat_n(0, run.parse().unwrap())),
                    None => case
                        .pixels
                        .push(u32::from_str_radix(token, 16).unwrap()),
                }
            }
        }
        for case in &cases {
            assert_eq!(case.pixels.len(), case.width * case.height, "{} {:?}", case.painter, case.channel);
        }
        cases
    }

    /// Per channel, at most one step apart (libm differs between platforms, so painted bitmaps are
    /// never compared exactly).
    fn close(a: u32, b: u32) -> bool {
        (0..4).all(|shift| ((a >> (shift * 8)) & 0xFF).abs_diff((b >> (shift * 8)) & 0xFF) <= 1)
    }

    #[test]
    fn charts_and_tracks_equal_the_csharp_painters() {
        let cases = cases();
        assert_eq!(
            cases
                .iter()
                .filter(|c| c.painter == "slice")
                .count(),
            6
        );
        assert_eq!(
            cases
                .iter()
                .filter(|c| c.painter == "track")
                .count(),
            6
        );
        for case in cases {
            let mut pixels = vec![0xDEAD_BEEFu32; case.width * case.height];
            if case.painter == "slice" {
                paint_slice(case.channel, case.value, case.width, case.height, &mut pixels);
            } else {
                paint_track(case.channel, case.value, &mut pixels);
            }
            let painted = pixels.iter().filter(|p| **p != 0).count();
            assert!(painted > 0, "{} {:?} painted nothing", case.painter, case.channel);
            for (i, (got, want)) in pixels.iter().zip(&case.pixels).enumerate() {
                assert!(
                    close(*got, *want),
                    "{} {:?} {:?}: pixel {} ({}, {}) is {got:08X}, C# painted {want:08X}",
                    case.painter,
                    case.channel,
                    case.value,
                    i,
                    i % case.width,
                    i / case.width
                );
            }
        }
    }

    #[test]
    fn an_achromatic_track_is_opaque_grey_from_black_to_white() {
        let mut pixels = [0u32; 16];
        paint_track(
            Channel::Lightness,
            OkLch {
                l: 0.5,
                c: 0.0,
                h_deg: 0.0,
            },
            &mut pixels,
        );
        for (i, p) in pixels.iter().enumerate() {
            assert_eq!(p >> 24, 0xFF, "pixel {i}");
            let [r, g, b] = [(p >> 16) & 0xFF, (p >> 8) & 0xFF, p & 0xFF];
            assert!(r.abs_diff(g) <= 1 && g.abs_diff(b) <= 1, "pixel {i}: {p:08X}");
        }
        assert!(pixels[0] & 0xFF < pixels[15] & 0xFF);
    }

    #[test]
    #[should_panic(expected = "needs that many pixels")]
    fn a_short_buffer_is_refused() {
        paint_slice(Channel::Hue, OkLch::default(), 4, 4, &mut [0; 15]);
    }
}
