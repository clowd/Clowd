//! Generic per-region overlay animation.
//!
//! Generalization of `panel::hover::HoverAnimator` for N regions instead
//! of a fixed 7. Each region has an independent fade value that animates
//! smoothly toward its target using exponential ease-out.

use super::component::OverlayRegion;

/// Animation speed factor. Higher = faster animation.
/// At 60 FPS with factor 12.0, ~90% of the transition completes in ~200ms.
const ANIM_SPEED: f32 = 12.0;

/// Threshold below which we snap to target (avoids asymptotic crawl).
const SNAP_THRESHOLD: f32 = 0.01;

#[derive(Clone, Copy, Default)]
struct FadeState {
    current: f32,
    target: f32,
}

impl FadeState {
    fn advance(&mut self, dt: f32) {
        if (self.current - self.target).abs() < SNAP_THRESHOLD {
            self.current = self.target;
            return;
        }
        let factor = 1.0 - (-ANIM_SPEED * dt).exp();
        self.current += (self.target - self.current) * factor;
    }

    fn is_animating(&self) -> bool {
        (self.current - self.target).abs() >= SNAP_THRESHOLD
    }
}

/// Manages fade animations for an arbitrary number of overlay regions.
///
/// The app thread sets targets via `update_targets()`; the render thread
/// calls `advance()` each frame and reads `fades()` for shader uniforms.
pub struct OverlayAnimator {
    fades: Vec<FadeState>,
}

impl OverlayAnimator {
    pub fn new() -> Self {
        Self { fades: Vec::new() }
    }

    /// Update targets from a new set of overlay regions. Regions are
    /// matched by index. If the region count changed, new regions start
    /// at fade 0.
    pub fn update_targets(&mut self, regions: &[OverlayRegion]) {
        self.fades.resize(regions.len(), FadeState::default());
        for (i, region) in regions.iter().enumerate() {
            self.fades[i].target = region.target_opacity;
        }
    }

    /// Advance all fades by `dt` seconds. Returns `true` if any fade
    /// is still animating.
    pub fn advance(&mut self, dt: f32) -> bool {
        let mut any = false;
        for f in &mut self.fades {
            f.advance(dt);
            if f.is_animating() {
                any = true;
            }
        }
        any
    }

    /// Current fade value at index `i`, or 0.0 if out of range.
    pub fn fade_at(&self, i: usize) -> f32 {
        self.fades.get(i).map(|f| f.current).unwrap_or(0.0)
    }
}

impl Default for OverlayAnimator {
    fn default() -> Self {
        Self::new()
    }
}
