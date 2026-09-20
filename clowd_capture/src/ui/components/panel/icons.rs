//! The panel's SVG marks as egui textures.
//!
//! One set per host, so one set per monitor DPI: the icons are rasterised
//! at that host's physical pixel size and drawn at their logical size, so
//! egui samples them 1:1 instead of resampling a shared bitmap. Building
//! them is lazy — the first panel run on a host pays for it, never
//! startup.

use std::collections::HashMap;

use egui::load::SizedTexture;
use egui::Vec2;

use super::assets;
use super::model::PanelButtonSet;
use super::theme::tokens;

/// Every mark one host can draw, keyed by the address of the SVG bytes the
/// button def carries. The handles keep the textures alive for the host's
/// life; dropping this frees them all.
pub struct IconTextures {
    buttons: HashMap<usize, egui::TextureHandle>,
    emblem: egui::TextureHandle,
}

impl IconTextures {
    /// Rasterise every button icon and the emblem at `ppp`. Sizes follow
    /// the retired kit's rounding rules: an icon rounds to a whole cell,
    /// the emblem mark floors like any other size.
    pub fn new(ctx: &egui::Context, ppp: f32) -> Self {
        let icon_px = (tokens::ICON * ppp).round().max(1.0) as u32;
        let emblem_px = (tokens::EMBLEM_MARK * ppp).floor().max(1.0) as u32;
        let load = |name: String, svg: &[u8], px: u32| ctx.load_texture(name, rasterise(svg, px), egui::TextureOptions::LINEAR);
        // Deduped by address first: the two sets share UPLOAD, COPY and
        // EXIT, and a second rasterisation of the same bytes would be a
        // second texture for the same picture.
        let distinct: HashMap<usize, &'static [u8]> = PanelButtonSet::ALL
            .iter()
            .flat_map(|s| s.defs())
            .map(|d| (d.svg_bytes.as_ptr() as usize, d.svg_bytes))
            .collect();
        let buttons = distinct
            .into_iter()
            .map(|(key, svg)| (key, load(format!("icon-{key:x}"), svg, icon_px)))
            .collect();
        Self {
            buttons,
            emblem: load("emblem".into(), assets::SVG_CLOWD_LOGO, emblem_px),
        }
    }

    /// The texture for a def's icon, sized in points. `Image::paint_at`
    /// rounds its rect to whole pixels, so a 20 pt icon at 125 % samples
    /// the 25 px texture 1:1.
    pub fn button(&self, svg: &'static [u8]) -> SizedTexture {
        SizedTexture::new(self.buttons[&(svg.as_ptr() as usize)].id(), tokens::ICON_SIZE)
    }

    pub fn emblem(&self) -> SizedTexture {
        SizedTexture::new(self.emblem.id(), Vec2::splat(tokens::EMBLEM_MARK))
    }
}

/// One SVG stretched to a `px` square (the C# canvas-fit rule, never
/// ink-fit). A malformed icon becomes an empty picture and an `error!`
/// line, as the retired atlas did: a blank button, not a crash.
fn rasterise(svg: &[u8], px: u32) -> egui::ColorImage {
    let opts = usvg::Options::default();
    let tree = match usvg::Tree::from_data(svg, &opts) {
        Ok(tree) => tree,
        Err(err) => {
            log::error!("panel icon failed to parse: {err}");
            usvg::Tree::from_str("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", &opts).expect("empty SVG parses")
        }
    };
    let Some(mut pixmap) = resvg::tiny_skia::Pixmap::new(px, px) else {
        return egui::ColorImage::filled([1, 1], egui::Color32::TRANSPARENT);
    };
    let vb = tree.size();
    let transform = resvg::tiny_skia::Transform::from_scale(px as f32 / vb.width(), px as f32 / vb.height());
    resvg::render(&tree, transform, &mut pixmap.as_mut());
    // tiny-skia writes premultiplied RGBA, which is `Color32`'s own
    // encoding, so the bytes go across untouched.
    egui::ColorImage::from_rgba_premultiplied([px as usize; 2], pixmap.data())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Every mark a button can ask for must be in the map, at every DPI
    /// the app runs at: a miss would panic on a render-adjacent path.
    #[test]
    fn every_button_icon_is_registered_at_each_dpi() {
        for dpi in [1.0, 1.25, 1.5, 2.0] {
            let ctx = egui::Context::default();
            let icons = IconTextures::new(&ctx, dpi);
            for set in PanelButtonSet::ALL {
                for def in set.defs() {
                    let tex = icons.button(def.svg_bytes);
                    assert_eq!(tex.size, tokens::ICON_SIZE, "{} at {dpi}", def.label);
                }
            }
            assert_eq!(icons.emblem().size, Vec2::splat(tokens::EMBLEM_MARK));
        }
    }
}
