//! Generic CPU drawing operations on `tiny_skia::Pixmap` buffers: rect
//! fills, pixmap compositing, and ClearType-style subpixel text
//! rasterization. Shared by every UI component that bakes a pixmap.

use std::sync::OnceLock;

use swash::scale::image::Content;
use swash::scale::{Render, ScaleContext, Source};
use swash::zeno::Format;
use swash::{FontRef, GlyphId};
use tiny_skia::Pixmap;

// ---------------------------------------------------------------------------
// Rect fills and pixmap compositing
// ---------------------------------------------------------------------------

/// Fill an axis-aligned rect with the given sRGB colour. Premultiplies
/// before writing (tiny-skia stores premultiplied bytes). Clips to
/// pixmap bounds; silently no-ops if the rect is entirely outside.
pub fn fill_rect(pixmap: &mut Pixmap, x: f32, y: f32, w: f32, h: f32, rgba: [u8; 4]) {
    if w <= 0.0 || h <= 0.0 {
        return;
    }
    let pm_w = pixmap.width() as i32;
    let pm_h = pixmap.height() as i32;
    let x0 = x.floor().max(0.0) as i32;
    let y0 = y.floor().max(0.0) as i32;
    let x1 = ((x + w).ceil() as i32).min(pm_w);
    let y1 = ((y + h).ceil() as i32).min(pm_h);
    if x0 >= x1 || y0 >= y1 {
        return;
    }
    let src_a = rgba[3] as u32;
    // Premultiply: (C * A + 127) / 255 keeps rounding in the
    // middle-bin tie-breaker consistent.
    let src_r = ((rgba[0] as u32 * src_a + 127) / 255) as u8;
    let src_g = ((rgba[1] as u32 * src_a + 127) / 255) as u8;
    let src_b = ((rgba[2] as u32 * src_a + 127) / 255) as u8;
    let src_a_u8 = rgba[3];
    let data = pixmap.data_mut();
    let stride = pm_w * 4;
    for yy in y0..y1 {
        let row_start = (yy * stride) as usize;
        for xx in x0..x1 {
            let idx = row_start + (xx as usize) * 4;
            // Source-over blend in premultiplied space.
            let dst_r = data[idx] as u32;
            let dst_g = data[idx + 1] as u32;
            let dst_b = data[idx + 2] as u32;
            let dst_a = data[idx + 3] as u32;
            let inv_a = 255 - src_a;
            data[idx] = (src_r as u32 + (dst_r * inv_a + 127) / 255) as u8;
            data[idx + 1] = (src_g as u32 + (dst_g * inv_a + 127) / 255) as u8;
            data[idx + 2] = (src_b as u32 + (dst_b * inv_a + 127) / 255) as u8;
            data[idx + 3] = (src_a_u8 as u32 + (dst_a * inv_a + 127) / 255) as u8;
        }
    }
}

/// Composite a smaller pre-rendered pixmap onto the main pixmap at a
/// pixel offset. Both are premultiplied sRGBA. Used for stamping
/// resvg output into a component's baked pixmap.
pub fn blit_pixmap(dst: &mut Pixmap, src: &Pixmap, x: i32, y: i32) {
    let dst_w = dst.width() as i32;
    let dst_h = dst.height() as i32;
    let sw = src.width() as i32;
    let sh = src.height() as i32;
    let src_data = src.data();
    let dst_data = dst.data_mut();
    for sy in 0..sh {
        let dy = y + sy;
        if dy < 0 || dy >= dst_h {
            continue;
        }
        for sx in 0..sw {
            let dx = x + sx;
            if dx < 0 || dx >= dst_w {
                continue;
            }
            let si = ((sy * sw + sx) * 4) as usize;
            let di = ((dy * dst_w + dx) * 4) as usize;
            let src_a = src_data[si + 3] as u32;
            if src_a == 0 {
                continue;
            }
            let inv_a = 255 - src_a;
            dst_data[di] =
                (src_data[si] as u32 + (dst_data[di] as u32 * inv_a + 127) / 255) as u8;
            dst_data[di + 1] =
                (src_data[si + 1] as u32 + (dst_data[di + 1] as u32 * inv_a + 127) / 255) as u8;
            dst_data[di + 2] =
                (src_data[si + 2] as u32 + (dst_data[di + 2] as u32 * inv_a + 127) / 255) as u8;
            dst_data[di + 3] =
                (src_data[si + 3] as u32 + (dst_data[di + 3] as u32 * inv_a + 127) / 255) as u8;
        }
    }
}

// ---------------------------------------------------------------------------
// ClearType-style subpixel text compositing
// ---------------------------------------------------------------------------
//
// swash gives us a 4-byte-per-pixel buffer (`Format::Subpixel`) where
// each pixel holds three independently-rasterized coverage values at
// horizontal offsets -0.3, 0, +0.3 px stored in bytes 0, 1, 2.
// (The 4th byte — A — is never written by zeno and is left at zero.)
// Because the offset shifts the *glyph* (not the sampling grid),
// byte 0 (offset -0.3, glyph shifted left) gives the coverage at the
// *rightmost* subpixel position (blue on an RGB LCD), and byte 2
// (offset +0.3, glyph shifted right) gives the *leftmost* (red).
// `blit_glyph_subpixel` swaps R↔B when reading the coverage buffer
// to match the standard RGB subpixel layout.
//
// This is a clean three-pass rasterization, not the supersample-and-LCD-
// filter approach FreeType uses. As a result we don't need an explicit
// LCD filter — the slight overlap between the ±0.3 offsets naturally
// reduces color fringing.
//
// `blit_glyph_subpixel` then composites those per-channel coverages
// onto the pixmap *in linear light* using sRGB <-> linear LUTs. This
// is what makes small text look solid and on-weight instead of thin
// and washed out — gamma-correct compositing matters as much as
// subpixel rendering for the perceived quality.
//
// Precondition: the destination pixmap is opaque underneath the text
// (alpha = 0xFF). Components that draw text on a transparent background
// will render incorrectly. The textured-quad pipeline blends the *whole*
// baked pixmap onto the screen with premultiplied alpha, so we rely on
// we can read the destination RGB as plain sRGB rather than dividing
// out a fractional alpha.

fn srgb_to_linear_lut() -> &'static [f32; 256] {
    static LUT: OnceLock<[f32; 256]> = OnceLock::new();
    LUT.get_or_init(|| {
        let mut lut = [0.0f32; 256];
        for (i, slot) in lut.iter_mut().enumerate() {
            let c = i as f32 / 255.0;
            *slot = if c <= 0.04045 {
                c / 12.92
            } else {
                ((c + 0.055) / 1.055).powf(2.4)
            };
        }
        lut
    })
}

fn linear_to_srgb_lut() -> &'static [u8; 4096] {
    static LUT: OnceLock<[u8; 4096]> = OnceLock::new();
    LUT.get_or_init(|| {
        let mut lut = [0u8; 4096];
        for (i, slot) in lut.iter_mut().enumerate() {
            let lin = i as f32 / 4095.0;
            let srgb = if lin <= 0.003_130_8 {
                lin * 12.92
            } else {
                1.055 * lin.powf(1.0 / 2.4) - 0.055
            };
            *slot = (srgb.clamp(0.0, 1.0) * 255.0 + 0.5) as u8;
        }
        lut
    })
}

fn blit_glyph_subpixel(
    dst: &mut Pixmap,
    coverage_rgba: &[u8],
    w: usize,
    h: usize,
    x: i32,
    y: i32,
    text_rgba: [u8; 4],
) {
    if w == 0 || h == 0 {
        return;
    }
    let dst_w = dst.width() as i32;
    let dst_h = dst.height() as i32;
    let data = dst.data_mut();
    let s2l = srgb_to_linear_lut();
    let l2s = linear_to_srgb_lut();

    let text_alpha = text_rgba[3] as f32 / 255.0;
    let text_lin = [
        s2l[text_rgba[0] as usize],
        s2l[text_rgba[1] as usize],
        s2l[text_rgba[2] as usize],
    ];

    let src_stride = w * 4;
    for gy in 0..(h as i32) {
        let dy = y + gy;
        if dy < 0 || dy >= dst_h {
            continue;
        }
        let row_off = (gy as usize) * src_stride;
        for gx in 0..(w as i32) {
            let dx = x + gx;
            if dx < 0 || dx >= dst_w {
                continue;
            }
            let pix_off = row_off + (gx as usize) * 4;
            // zeno's Format::Subpixel rasterizes at offsets [-0.3, 0, +0.3]
            // and stores coverage in bytes [0, 1, 2]. Offset -0.3 shifts
            // the glyph LEFT, so each pixel samples 0.3 px to the RIGHT of
            // the glyph — that's the BLUE physical subpixel on an RGB LCD.
            // Offset +0.3 shifts RIGHT → pixel samples LEFT → RED subpixel.
            // So byte 0 = blue coverage, byte 2 = red coverage — we must
            // swap R and B to match the standard RGB subpixel layout.
            let cov_r = coverage_rgba[pix_off + 2] as f32 * (1.0 / 255.0) * text_alpha;
            let cov_g = coverage_rgba[pix_off + 1] as f32 * (1.0 / 255.0) * text_alpha;
            let cov_b = coverage_rgba[pix_off] as f32 * (1.0 / 255.0) * text_alpha;
            if cov_r == 0.0 && cov_g == 0.0 && cov_b == 0.0 {
                continue;
            }
            let di = ((dy * dst_w + dx) * 4) as usize;
            // Dst is premultiplied sRGB. Because dst alpha is 0xFF here
            // (opaque-background precondition), the premultiplied bytes
            // equal the straight-sRGB bytes, so we can index the LUT directly.
            let dst_lin = [
                s2l[data[di] as usize],
                s2l[data[di + 1] as usize],
                s2l[data[di + 2] as usize],
            ];
            let out_lin = [
                text_lin[0] * cov_r + dst_lin[0] * (1.0 - cov_r),
                text_lin[1] * cov_g + dst_lin[1] * (1.0 - cov_g),
                text_lin[2] * cov_b + dst_lin[2] * (1.0 - cov_b),
            ];
            // Quantize linear -> 12-bit LUT index, then look up the
            // sRGB-encoded byte. clamp() handles tiny float overshoot
            // from rounding in the lerp.
            data[di] = l2s[(out_lin[0].clamp(0.0, 1.0) * 4095.0) as usize];
            data[di + 1] = l2s[(out_lin[1].clamp(0.0, 1.0) * 4095.0) as usize];
            data[di + 2] = l2s[(out_lin[2].clamp(0.0, 1.0) * 4095.0) as usize];
            // Leave alpha alone — dst stays opaque so the textured quad
            // fully replaces what's underneath when it's blended onto
            // the swapchain.
        }
    }
}

// ---------------------------------------------------------------------------
// Text measurement and rendering
// ---------------------------------------------------------------------------

/// Parameters for drawing a single line of text into a pixmap.
pub struct TextLine<'a> {
    pub text: &'a str,
    pub px: f32,
    pub x: f32,
    pub y: f32,
    pub rgba: [u8; 4],
    pub underline: Option<usize>,
    pub underline_thickness: f32,
}

pub struct LineMetrics {
    pub width: f32,
    pub height: f32,
}

/// Owns font data and a reusable scaler context for glyph rasterization.
pub struct TextRenderer {
    /// Roboto font reference. Borrows `FONT_ROBOTO` (a `&'static [u8]`)
    /// so the lifetime is `'static`. `FontRef` is `Copy`.
    pub font: FontRef<'static>,
    /// Reusable scaler context. Holds caches and scratch buffers for
    /// glyph rasterization — keep it across renders.
    scale_ctx: ScaleContext,
}

impl TextRenderer {
    pub fn new(font: FontRef<'static>) -> Self {
        Self {
            font,
            scale_ctx: ScaleContext::new(),
        }
    }

    /// Sum of advance widths for the string at the given px size, plus
    /// the font's cap-height as a representative "visual" line height.
    pub fn measure_line(&self, s: &str, px: f32) -> LineMetrics {
        let charmap = self.font.charmap();
        let gm = self.font.glyph_metrics(&[]).scale(px);
        let total_w: f32 = s
            .chars()
            .map(|c| gm.advance_width(charmap.map(c)))
            .sum();
        let m = self.font.metrics(&[]).scale(px);
        let height = if m.cap_height > 0.0 {
            m.cap_height
        } else {
            m.ascent
        };
        LineMetrics {
            width: total_w,
            height,
        }
    }

    /// Rasterize a string with swash and stamp each glyph onto the
    /// pixmap with gamma-correct subpixel compositing.
    ///
    /// `y` is the top of the **visible cap-height box** — i.e. the y
    /// where the top of capital letters / digits will land. We
    /// deliberately don't use the top of the ascent box: the gap
    /// between ascent and cap-height (space reserved for diacriticals
    /// over capitals) is ~2-3 px at 12px Roboto and would make every
    /// centered string sit visually low.
    pub fn draw_text_line(&mut self, pixmap: &mut Pixmap, tl: TextLine<'_>) {
        let TextLine {
            text,
            px,
            mut x,
            y,
            rgba,
            underline,
            underline_thickness,
        } = tl;
        let font_metrics = self.font.metrics(&[]).scale(px);
        let baseline_y: i32 = (y + font_metrics.cap_height).round() as i32;

        let charmap = self.font.charmap();
        let gm = self.font.glyph_metrics(&[]).scale(px);
        let mut scaler = self
            .scale_ctx
            .builder(self.font)
            .size(px)
            .hint(true)
            .build();

        let mut underline_x_start = 0.0_f32;
        let mut underline_x_end = 0.0_f32;
        for (i, c) in text.chars().enumerate() {
            let gid: GlyphId = charmap.map(c);
            let advance = gm.advance_width(gid);

            // Pin the pen to the nearest integer pixel column (standard
            // ClearType-style rendering). Hinted TrueType outlines +
            // zeno's `Format::Subpixel` artifact badly when mixed with
            // non-integer X offsets.
            let pen_x = x.round() as i32;

            let image = Render::new(&[Source::Outline])
                .format(Format::Subpixel)
                .render(&mut scaler, gid);
            if let Some(image) = image {
                if image.content == Content::SubpixelMask
                    && image.placement.width > 0
                    && image.placement.height > 0
                {
                    let blit_x = pen_x + image.placement.left;
                    let blit_y = baseline_y - image.placement.top;
                    blit_glyph_subpixel(
                        pixmap,
                        &image.data,
                        image.placement.width as usize,
                        image.placement.height as usize,
                        blit_x,
                        blit_y,
                        rgba,
                    );
                    if underline == Some(i) {
                        underline_x_start = blit_x as f32;
                        underline_x_end = underline_x_start + image.placement.width as f32;
                    }
                } else if underline == Some(i) {
                    underline_x_start = x;
                    underline_x_end = x + advance;
                }
            }
            x += advance;
        }
        if underline.is_some() && underline_thickness > 0.0 {
            let uy = (baseline_y + 1) as f32;
            fill_rect(
                pixmap,
                underline_x_start,
                uy,
                underline_x_end - underline_x_start,
                underline_thickness,
                rgba,
            );
        }
    }
}
