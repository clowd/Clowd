//! What one egui pass hands the render workers, plus the app-thread CPU
//! shadow of egui's textures that makes it self-contained.
//!
//! egui reports texture changes as a [`TexturesDelta`] per pass, which only
//! makes sense to a backend that has seen every previous delta. Workers do
//! not: a broadcast can be dropped, and a worker's UI stack can be built
//! after several passes have already run. So the app thread applies every
//! delta to a whole-texture shadow here and ships a version-stamped
//! snapshot instead; a worker whose applied version differs replaces all of
//! its GPU textures. No `TexturesDelta` ever crosses a thread.

use std::collections::HashMap;
use std::sync::Arc;

use egui::epaint::textures::{TextureOptions, TexturesDelta};
use egui::epaint::{ClippedPrimitive, ColorImage, ImageData, ImageDelta, TextureId};

/// One egui texture as the app thread last saw it whole. `pixels` are
/// premultiplied gamma sRGBA (`Color32` byte order == `Rgba8Unorm` byte
/// order), so the painter uploads them untouched.
#[derive(Clone, Debug)]
pub struct TextureImage {
    pub pixels: Arc<ColorImage>,
    pub options: TextureOptions,
}

/// Every live texture of one context, whole. A worker replaces ALL its GPU
/// textures with this when `version` differs from the one it applied last.
#[derive(Debug)]
pub struct TextureSnapshot {
    pub version: u64,
    pub textures: Vec<(TextureId, TextureImage)>,
}

/// What one monitor's worker paints this broadcast. `primitives` are `Send`
/// (meshes are plain data; Clowd never emits paint callbacks).
#[derive(Debug)]
pub struct EguiFrame {
    pub pixels_per_point: f32,
    pub primitives: Vec<ClippedPrimitive>,
    pub textures: Arc<TextureSnapshot>,
}

/// App-thread CPU shadow of one context's textures. Applies whole and
/// partial deltas, defers frees by one call, and hands out a snapshot only
/// when something changed.
#[derive(Default)]
pub struct TextureShadow {
    images: HashMap<TextureId, TextureImage>,
    pending_free: Vec<TextureId>,
    version: u64,
    snapshot: Option<Arc<TextureSnapshot>>,
}

impl TextureShadow {
    /// Empties `delta` (epaint debug-asserts a non-empty [`TexturesDelta`]
    /// on drop).
    ///
    /// Order: the previous call's frees first (the pass that queued them
    /// may still have drawn the id), then this delta's sets; this delta's
    /// frees are parked for the next call.
    pub fn apply(&mut self, delta: &mut TexturesDelta) {
        let mut changed = false;
        for id in self.pending_free.drain(..) {
            changed |= self.images.remove(&id).is_some();
        }
        for (id, deltas) in std::mem::take(&mut delta.set) {
            for d in deltas {
                changed |= self.apply_one(id, d);
            }
        }
        self.pending_free
            .extend(std::mem::take(&mut delta.free));
        if changed {
            self.version += 1;
            self.snapshot = None;
        }
    }

    fn apply_one(&mut self, id: TextureId, d: ImageDelta) -> bool {
        let ImageData::Color(src) = d.image;
        match d.pos {
            None => {
                self.images.insert(
                    id,
                    TextureImage {
                        pixels: src,
                        options: d.options,
                    },
                );
                true
            }
            Some([x, y]) => {
                let Some(dst) = self.images.get_mut(&id) else {
                    log::warn!("egui: partial delta for unknown texture {id:?}, skipped");
                    return false;
                };
                // Copy on write: a snapshot already in flight keeps the
                // pixels it was taken with.
                let img = Arc::make_mut(&mut dst.pixels);
                if x + src.width() > img.width() || y + src.height() > img.height() {
                    log::warn!("egui: partial delta out of bounds for {id:?}, skipped");
                    return false;
                }
                for row in 0..src.height() {
                    let from = row * src.width();
                    let to = (y + row) * img.width() + x;
                    img.pixels[to..to + src.width()].copy_from_slice(&src.pixels[from..from + src.width()]);
                }
                dst.options = d.options;
                true
            }
        }
    }

    /// The current textures as one shared snapshot. Rebuilt only after a
    /// change, so an unchanged pass hands every worker the same `Arc`.
    pub fn snapshot(&mut self) -> Arc<TextureSnapshot> {
        self.snapshot
            .get_or_insert_with(|| {
                Arc::new(TextureSnapshot {
                    version: self.version,
                    textures: self
                        .images
                        .iter()
                        .map(|(id, t)| (*id, t.clone()))
                        .collect(),
                })
            })
            .clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use egui::Color32;

    fn image(w: usize, h: usize, fill: Color32) -> ColorImage {
        ColorImage::new([w, h], vec![fill; w * h])
    }

    fn whole(w: usize, h: usize, fill: Color32) -> ImageDelta {
        ImageDelta::full(image(w, h, fill), TextureOptions::LINEAR)
    }

    fn partial(pos: [usize; 2], w: usize, h: usize, fill: Color32) -> ImageDelta {
        ImageDelta::partial(pos, image(w, h, fill), TextureOptions::LINEAR)
    }

    fn delta(sets: Vec<(TextureId, ImageDelta)>, frees: Vec<TextureId>) -> TexturesDelta {
        let mut d = TexturesDelta::default();
        for (id, one) in sets {
            d.push(id, one);
        }
        d.free.extend(frees);
        d
    }

    const ID: TextureId = TextureId::Managed(0);

    #[test]
    fn whole_delta_allocates_at_exact_size() {
        let mut shadow = TextureShadow::default();
        shadow.apply(&mut delta(vec![(ID, whole(4, 3, Color32::RED))], vec![]));
        let snap = shadow.snapshot();
        let (id, img) = &snap.textures[0];
        assert_eq!(*id, ID);
        assert_eq!((img.pixels.width(), img.pixels.height()), (4, 3));
        assert_eq!(img.pixels.pixels[0], Color32::RED);
    }

    #[test]
    fn partial_delta_blits_into_place() {
        let mut shadow = TextureShadow::default();
        shadow.apply(&mut delta(vec![(ID, whole(4, 4, Color32::BLACK))], vec![]));
        shadow.apply(&mut delta(vec![(ID, partial([1, 1], 2, 2, Color32::WHITE))], vec![]));
        let snap = shadow.snapshot();
        let img = &snap.textures[0].1.pixels;
        for y in 0..4 {
            for x in 0..4 {
                let inside = (1..3).contains(&x) && (1..3).contains(&y);
                let want = if inside { Color32::WHITE } else { Color32::BLACK };
                assert_eq!(img.pixels[y * 4 + x], want, "at ({x}, {y})");
            }
        }
    }

    #[test]
    fn partial_before_whole_is_skipped_with_no_change() {
        let mut shadow = TextureShadow::default();
        shadow.apply(&mut delta(vec![(ID, partial([0, 0], 1, 1, Color32::WHITE))], vec![]));
        let snap = shadow.snapshot();
        assert_eq!(snap.version, 0);
        assert!(snap.textures.is_empty());
    }

    #[test]
    fn partial_out_of_bounds_is_skipped() {
        let mut shadow = TextureShadow::default();
        shadow.apply(&mut delta(vec![(ID, whole(2, 2, Color32::BLACK))], vec![]));
        let after_whole = shadow.snapshot().version;
        shadow.apply(&mut delta(vec![(ID, partial([1, 1], 2, 2, Color32::WHITE))], vec![]));
        let snap = shadow.snapshot();
        assert_eq!(snap.version, after_whole);
        assert!(snap.textures[0]
            .1
            .pixels
            .pixels
            .iter()
            .all(|p| *p == Color32::BLACK));
    }

    #[test]
    fn free_is_applied_on_the_next_apply() {
        let mut shadow = TextureShadow::default();
        shadow.apply(&mut delta(vec![(ID, whole(2, 2, Color32::BLACK))], vec![]));
        // The pass that queued the free may still be drawing that id.
        shadow.apply(&mut delta(vec![], vec![ID]));
        assert_eq!(shadow.snapshot().textures.len(), 1);
        shadow.apply(&mut delta(vec![], vec![]));
        assert!(shadow.snapshot().textures.is_empty());
    }

    #[test]
    fn snapshot_version_bumps_only_on_change() {
        let mut shadow = TextureShadow::default();
        shadow.apply(&mut delta(vec![(ID, whole(2, 2, Color32::BLACK))], vec![]));
        let first = shadow.snapshot();
        shadow.apply(&mut delta(vec![], vec![]));
        let second = shadow.snapshot();
        assert!(Arc::ptr_eq(&first, &second));
        shadow.apply(&mut delta(vec![(ID, whole(2, 2, Color32::WHITE))], vec![]));
        let third = shadow.snapshot();
        assert_eq!(third.version, first.version + 1);
    }

    #[test]
    fn snapshot_pixels_are_isolated() {
        let mut shadow = TextureShadow::default();
        shadow.apply(&mut delta(vec![(ID, whole(2, 2, Color32::BLACK))], vec![]));
        let old = shadow.snapshot();
        shadow.apply(&mut delta(vec![(ID, partial([0, 0], 1, 1, Color32::WHITE))], vec![]));
        assert_eq!(old.textures[0].1.pixels.pixels[0], Color32::BLACK);
        assert_eq!(shadow.snapshot().textures[0].1.pixels.pixels[0], Color32::WHITE);
    }
}
