//! Hover animation state for panel buttons.
//!
//! Each button has an independent fade value (0.0 = idle, 1.0 = fully
//! hovered). When the cursor enters a button, its fade animates toward
//! 1.0; when it leaves, the fade animates back to 0.0. Multiple buttons
//! can animate concurrently (crossfade), giving smooth visual feedback
//! without re-baking the panel texture.
//!
//! Animation uses a simple exponential ease-out approach: each frame,
//! the fade value moves toward its target by a fraction of the remaining
//! distance. This gives natural-feeling deceleration without complex
//! easing math.

use super::model::NUM_SVG_BUTTONS;

/// Animation speed factor. Higher = faster animation.
/// At 60 FPS with factor 12.0, ~90% of the transition completes in ~200ms.
const ANIM_SPEED: f32 = 12.0;

/// Threshold below which we snap to target (avoids asymptotic crawl).
const SNAP_THRESHOLD: f32 = 0.01;

/// Per-button animation state.
#[derive(Clone, Copy, Default)]
struct ButtonAnim {
    /// Current fade value in [0.0, 1.0].
    current: f32,
    /// Target fade value (0.0 = idle, 1.0 = hovered).
    target: f32,
}

impl ButtonAnim {
    /// Advance the animation by `dt` seconds.
    fn advance(&mut self, dt: f32) {
        if (self.current - self.target).abs() < SNAP_THRESHOLD {
            self.current = self.target;
            return;
        }
        // Exponential ease: move a fraction of the remaining distance.
        // factor = 1 - e^(-speed * dt) ≈ speed * dt for small dt
        let factor = 1.0 - (-ANIM_SPEED * dt).exp();
        self.current += (self.target - self.current) * factor;
    }

    /// True if the animation is still in progress.
    fn is_animating(&self) -> bool {
        (self.current - self.target).abs() >= SNAP_THRESHOLD
    }
}

/// Manages hover animations for all panel buttons.
///
/// Call `set_hover` when the hovered button changes, `advance` each
/// frame, and read `fades` to get the current fade values for the shader.
pub struct HoverAnimator {
    buttons: [ButtonAnim; NUM_SVG_BUTTONS],
    current_hover: Option<usize>,
}

impl HoverAnimator {
    pub fn new() -> Self {
        Self {
            buttons: [ButtonAnim::default(); NUM_SVG_BUTTONS],
            current_hover: None,
        }
    }

    /// Update the hovered button. Starts fade-out on the old button (if
    /// any) and fade-in on the new button (if any). Safe to call every
    /// frame — no-ops if the hover hasn't changed.
    pub fn set_hover(&mut self, idx: Option<usize>) {
        if idx == self.current_hover {
            return;
        }
        // Fade out old
        if let Some(old) = self.current_hover {
            self.buttons[old].target = 0.0;
        }
        // Fade in new
        if let Some(new) = idx {
            self.buttons[new].target = 1.0;
        }
        self.current_hover = idx;
    }

    /// Advance all animations by `dt` seconds. Call once per frame.
    pub fn advance(&mut self, dt: f32) {
        for b in &mut self.buttons {
            b.advance(dt);
        }
    }

    /// Current fade values for all buttons, in [0.0, 1.0].
    pub fn fades(&self) -> [f32; NUM_SVG_BUTTONS] {
        std::array::from_fn(|i| self.buttons[i].current)
    }

    /// True if any button is currently animating.
    #[allow(dead_code)]
    pub fn is_animating(&self) -> bool {
        self.buttons.iter().any(|b| b.is_animating())
    }

    /// The currently hovered button index, if any.
    #[allow(dead_code)]
    pub fn current_hover(&self) -> Option<usize> {
        self.current_hover
    }
}

impl Default for HoverAnimator {
    fn default() -> Self {
        Self::new()
    }
}
