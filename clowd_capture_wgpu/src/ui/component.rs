//! The `Component` trait and supporting types for the generic UI system.
//!
//! Every UI overlay (button panel, tooltip, color picker, etc.) implements
//! `Component`. The trait methods run on the app thread (CPU only); the
//! GPU backend (`OverlayBackend`) is completely generic and never calls
//! anything component-specific.
//!
//! State flows from the app to components in exactly one direction: the
//! app builds an `AppContext` snapshotting the current input/selection/
//! monitor state and passes it to `ComponentHost::sync`, which calls
//! `Component::update` on every registered component. The component
//! decides for itself whether (and on which monitor) to be visible, and
//! internally caches any data it needs for `bake()` later in the same
//! sync pass.

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};
use super::command::Command;

/// Unique identifier for a component instance. Used for event routing,
/// render-thread state matching, and lifecycle management.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ComponentId(u64);

static NEXT_ID: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);

impl ComponentId {
    pub fn new() -> Self {
        Self(NEXT_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed))
    }
}

/// Per-monitor info the host hands to components so they can decide which
/// monitor they want to live on.
#[derive(Debug, Clone, Copy)]
pub struct MonitorInfo {
    pub bounds: ScreenRect,
    pub dpi_scale: f32,
}

/// App-wide state snapshot passed to every component on every sync.
///
/// Adding a new piece of state components need = add a field here once;
/// components that don't care simply ignore it.
#[derive(Debug, Clone, Copy)]
pub struct AppContext<'a> {
    pub monitors: &'a [MonitorInfo],
    /// Current selection rect in virtual-desktop pixels, or `None`.
    pub selection: Option<ScreenRect>,
    /// `true` once the user has finalised a selection (post mouse-up).
    pub captured: bool,
    /// Left mouse button currently held (set on press, cleared on release).
    /// Components that want to hide while the user is actively dragging
    /// (e.g. the Tips & Hotkeys panel) read this.
    pub mouse_down: bool,
    /// Virtual cursor in virtual-desktop pixels.
    pub virtual_cursor: ScreenPointF,
    /// Accent color for components that want to match the capture overlay.
    pub accent_color: [f32; 4],
    /// Index into `monitors` of the primary display, or `None` if no
    /// monitor reports as primary (defensive fallback).
    pub primary_monitor_idx: Option<usize>,
    /// Whether the user has toggled the Tips & Hotkeys panel on. Only
    /// the tips panel reads this.
    pub tips_visible: bool,
    /// Human-readable display name of the monitor currently under the
    /// virtual cursor, or `None` if the cursor is off-screen.
    pub hovered_monitor_name: Option<&'a str>,
    /// Title of the top-level window under the virtual cursor, or `None`
    /// if the cursor is over the desktop background.
    pub hovered_window_title: Option<&'a str>,
    /// BGRA sample of the captured-desktop pixel directly under the
    /// virtual cursor, or `None` if the cursor is outside the captured
    /// bounds. Used by the Tips & Hotkeys color-sampler row.
    pub hovered_pixel_bgra: Option<[u8; 4]>,
}

/// Return value of `Component::update` — where (if anywhere) the
/// component wants to be drawn this frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Placement {
    /// Not visible this frame; host drops any snapshot on the previously
    /// assigned monitor.
    Hidden,
    /// Visible on `monitor_idx` — the component has already cached whatever
    /// per-monitor state (DPI, bounds) it needs for `bake()`.
    Visible { monitor_idx: usize },
}

/// What a component wants to happen after processing a mouse event.
#[allow(dead_code)]
pub enum EventResponse {
    /// Nothing changed; no redraw needed.
    Ignored,
    /// The component's overlay regions changed (e.g. hover moved between
    /// sub-regions). Ships updated overlay targets to the render thread
    /// but does NOT re-bake the pixmap.
    NeedsOverlayUpdate,
    /// The component's visual content changed; re-bake the pixmap.
    NeedsRedraw,
    /// The component requests an app-level command be dispatched.
    /// The host extracts the command from `route_mouse_event` and returns
    /// it to the app; everything else is handled internally.
    Command(Command),
}

/// Cursor icon hint from a component's hit-test.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CursorHint {
    /// Component doesn't care -- use whatever the app would normally use.
    Default,
    /// Component wants a pointer cursor (e.g. for clickable elements).
    Pointer,
}

/// Mouse event delivered to a component. Coordinates are in
/// virtual-desktop pixels.
#[derive(Debug, Clone, Copy)]
#[allow(dead_code)]
pub enum MouseEvent {
    Move { pos: ScreenPointF },
    Press { pos: ScreenPointF },
    Release { pos: ScreenPointF },
}

/// Baked RGBA pixel data + destination rectangle.
#[derive(Clone)]
pub struct BakedPixmap {
    /// RGBA bytes, width * height * 4.
    pub data: Vec<u8>,
    pub width: u32,
    pub height: u32,
    /// Destination in virtual-desktop pixel coordinates.
    pub dest_vd: ScreenRect,
}

/// A rectangular region within the baked texture that receives a
/// shader-driven hover overlay. Expressed in texture UV coordinates.
#[derive(Clone, Copy)]
pub struct OverlayRegion {
    /// UV rect within the texture: (u_min, v_min, u_max, v_max).
    pub uv_rect: [f32; 4],
    /// Target overlay opacity in [0.0, 1.0]. The render thread's
    /// `OverlayAnimator` smoothly interpolates toward this value.
    pub target_opacity: f32,
}

/// Type-erased snapshot shipped from the app thread to a render thread.
/// Contains everything the render thread needs: pixmap bytes, destination
/// rect, and overlay region targets.
#[derive(Clone)]
pub struct ComponentSnapshot {
    pub id: ComponentId,
    /// Baked pixel data, or `None` to hide (render thread drops its cache).
    pub pixmap: Option<BakedPixmap>,
    /// Overlay regions for shader-driven animation.
    pub overlay_regions: Vec<OverlayRegion>,
    /// Multiplier applied to the sampled pixmap at composite time.
    /// 1.0 = fully opaque (default). Values in [0.0, 1.0] let a component
    /// bake fully opaque content and have the shader fade it during
    /// compositing, keeping text/edges crisp.
    pub base_opacity: f32,
}

/// The trait every UI component implements. All methods run on the
/// app thread (CPU only). The GPU backend is completely generic and
/// never calls anything component-specific.
pub trait Component: Send {
    /// Unique ID for this component instance.
    fn id(&self) -> ComponentId;

    /// Fold new app state into this component. Returns where (if anywhere)
    /// the component wants to be drawn this frame. The component is
    /// expected to cache the chosen monitor's DPI/bounds internally so
    /// `bake()` is parameterless.
    fn update(&mut self, ctx: &AppContext) -> Placement;

    /// Hit-test a point (virtual-desktop pixels) against the component.
    /// Returns `true` if the component considers the point "inside" and
    /// wants to receive mouse events for it.
    fn hit_test(&self, pos: ScreenPointF) -> bool;

    /// Cursor hint when the mouse is over this component.
    fn cursor_hint(&self, pos: ScreenPointF) -> CursorHint;

    /// Handle a mouse event. The component updates its internal state
    /// and returns what should happen next.
    fn on_mouse_event(&mut self, event: MouseEvent) -> EventResponse;

    /// Rasterize the component's current visual state into a pixmap.
    /// Called by the host after a successful `update(..) → Placement::Visible`.
    fn bake(&mut self) -> Option<BakedPixmap>;

    /// Produce the current overlay regions for shader-driven animation.
    fn overlay_regions(&self) -> Vec<OverlayRegion>;

    /// Component-wide alpha applied in the overlay shader. Lets a
    /// component bake fully opaque content and composite at a constant
    /// opacity without muddying its interior (e.g. the Tips & Hotkeys
    /// panel at 0.7). Default is fully opaque.
    fn base_opacity(&self) -> f32 {
        1.0
    }
}

/// Helper: pick the monitor whose bounds contain the center of `rect`.
/// Returns `(index, MonitorInfo)` or `None` if no monitor contains the
/// center (e.g. a selection dragged between monitors into a gap).
pub fn pick_monitor_containing_center(
    monitors: &[MonitorInfo],
    rect: ScreenRect,
) -> Option<(usize, MonitorInfo)> {
    let cx = (rect.left() + rect.right()) / 2;
    let cy = (rect.top() + rect.bottom()) / 2;
    monitors.iter().enumerate().find_map(|(i, m)| {
        let b = m.bounds;
        if cx >= b.left() && cx < b.right() && cy >= b.top() && cy < b.bottom() {
            Some((i, *m))
        } else {
            None
        }
    })
}
