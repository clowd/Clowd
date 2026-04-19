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
//! `Component::derive_state` on every registered component. The component
//! returns a minimal, hashable `State` describing everything `bake` reads;
//! the host hashes it and only calls `bake` when the hash changes. Because
//! `bake` is an associated function taking only `&Assets` and `&State`,
//! the compiler structurally prevents it from reading anything that
//! isn't in the hashed state.

use std::hash::{Hash, Hasher};

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

/// Where (if anywhere) a component wants to be drawn this frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Placement {
    Hidden,
    Visible { monitor_idx: usize },
}

/// Result of `Component::derive_state`: either the component is hidden,
/// or it's visible on a given monitor with a concrete hashable state.
pub enum DeriveResult<S> {
    Hidden,
    Visible { monitor_idx: usize, state: S },
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
    /// The component's internal state changed in a way that affects its
    /// `derive_state` output. Host re-derives using the cached context
    /// and re-bakes if the state hash changed.
    NeedsRedraw,
    /// The component requests an app-level command be dispatched.
    Command(Command),
}

/// Cursor icon hint from a component's hit-test.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CursorHint {
    Default,
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

/// How a region's pixels should be modified at composite time.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[allow(dead_code)]
pub enum RegionMode {
    Lighten,
    Darken,
    Fade,
}

/// A rectangular region within the baked texture that receives a
/// shader-driven effect. Expressed in texture UV coordinates.
#[derive(Clone, Copy)]
pub struct OverlayRegion {
    pub uv_rect: [f32; 4],
    pub mode: RegionMode,
    pub target_amount: f32,
}

/// The pixmap half of a component snapshot. `Keep` means the render
/// thread should leave its cached texture alone — only overlay regions
/// or opacity moved. This replaces the old "hash the whole pixel buffer
/// on both ends" detection.
#[derive(Clone)]
pub enum SnapshotPixmap {
    Replace(BakedPixmap),
    Keep,
}

/// Type-erased snapshot shipped from the app thread to a render thread.
#[derive(Clone)]
pub struct ComponentSnapshot {
    pub id: ComponentId,
    /// Hash of the component's `State` when this snapshot was produced.
    /// The render thread uses this to detect when a fresh pixmap upload
    /// is needed.
    pub state_hash: u64,
    pub pixmap: SnapshotPixmap,
    pub overlay_regions: Vec<OverlayRegion>,
    pub base_opacity: f32,
}

/// What `ErasedComponent::try_bake` produced this frame.
pub enum BakeOutcome {
    /// Component isn't visible — no state.
    Hidden,
    /// State hash matched last bake; render thread should reuse its cached texture.
    Unchanged { state_hash: u64 },
    /// State hash differed; fresh pixmap attached.
    Fresh {
        state_hash: u64,
        pixmap: BakedPixmap,
    },
}

/// The trait every UI component implements. All methods run on the
/// app thread. The GPU backend never calls anything component-specific.
///
/// `Assets` and `State` are split so `bake` — an associated function,
/// not a method — can only read immutable resources plus the hashed
/// state. The compiler prevents `bake` from reaching for anything else
/// the component shell might be carrying.
pub trait Component: Send {
    /// Immutable-interface render resources (fonts, parsed SVG trees,
    /// static glyph caches). Lives for the component's lifetime and
    /// stays on the app thread — only `Send` is required so the
    /// component itself can cross a thread boundary at startup.
    type Assets: Send;

    /// Hashable snapshot of every ctx-derived input that affects the
    /// bake. The host hashes this and skips `bake` if the hash matches.
    type State: Hash + Eq + Send;

    fn id(&self) -> ComponentId;

    /// Read-only view of the component's render assets.
    fn assets(&self) -> &Self::Assets;

    /// Cheap field reads from AppContext → `State`. Takes `&mut self`
    /// so shells can cache derived data (e.g. layout) for `hit_test`
    /// and `overlay_regions` — these caches are NOT hashed and must
    /// never affect the bake. Returning `Hidden` skips all baking.
    fn derive_state(&mut self, ctx: &AppContext) -> DeriveResult<Self::State>;

    /// Rasterize the pixmap from `Assets` + `State`. Takes no `self` —
    /// the signature structurally guarantees we're not reading anything
    /// outside the hashed state.
    fn bake(assets: &Self::Assets, state: &Self::State) -> Option<BakedPixmap>;

    /// Hit-test a point (virtual-desktop pixels) against the component.
    fn hit_test(&self, pos: ScreenPointF) -> bool;

    /// Cursor hint when the mouse is over this component.
    fn cursor_hint(&self, pos: ScreenPointF) -> CursorHint;

    /// Handle a mouse event.
    fn on_mouse_event(&mut self, event: MouseEvent) -> EventResponse;

    /// Current overlay regions (hover highlights, shadow fade, etc).
    fn overlay_regions(&self) -> Vec<OverlayRegion>;

    /// Component-wide alpha applied in the overlay shader.
    fn base_opacity(&self) -> f32 {
        1.0
    }
}

/// Type-erased component interface consumed by `ComponentHost`. Hides
/// each component's `Assets`/`State` associated types so the host can
/// store `Box<dyn ErasedComponent>`.
pub trait ErasedComponent: Send {
    fn id(&self) -> ComponentId;

    /// Run `derive_state`, stash the resulting state inside the box, and
    /// return the placement decision.
    fn sync_derive(&mut self, ctx: &AppContext) -> Placement;

    /// Bake if the stashed state's hash differs from the last bake.
    fn try_bake(&mut self) -> BakeOutcome;

    /// Invalidate the cached hash, forcing the next `try_bake` to bake.
    /// Called when the host sends a `Remove` (e.g. monitor migration)
    /// so the first snapshot on the new monitor always ships a pixmap.
    fn invalidate_bake_cache(&mut self);

    fn hit_test(&self, pos: ScreenPointF) -> bool;
    fn cursor_hint(&self, pos: ScreenPointF) -> CursorHint;
    fn on_mouse_event(&mut self, event: MouseEvent) -> EventResponse;
    fn overlay_regions(&self) -> Vec<OverlayRegion>;
    fn base_opacity(&self) -> f32;
}

/// Wraps a concrete `Component` and carries the pending state + hash
/// cache so the type-erased host can drive it.
pub struct ComponentBox<C: Component> {
    inner: C,
    pending_state: Option<C::State>,
    last_state_hash: u64,
}

impl<C: Component> ComponentBox<C> {
    pub fn new(inner: C) -> Self {
        Self {
            inner,
            pending_state: None,
            last_state_hash: 0,
        }
    }
}

impl<C: Component> ErasedComponent for ComponentBox<C> {
    fn id(&self) -> ComponentId {
        self.inner.id()
    }

    fn sync_derive(&mut self, ctx: &AppContext) -> Placement {
        match self.inner.derive_state(ctx) {
            DeriveResult::Hidden => {
                self.pending_state = None;
                Placement::Hidden
            }
            DeriveResult::Visible { monitor_idx, state } => {
                self.pending_state = Some(state);
                Placement::Visible { monitor_idx }
            }
        }
    }

    fn try_bake(&mut self) -> BakeOutcome {
        let Some(state) = self.pending_state.as_ref() else {
            return BakeOutcome::Hidden;
        };
        let hash = hash_one(state);
        if hash == self.last_state_hash {
            return BakeOutcome::Unchanged { state_hash: hash };
        }
        match C::bake(self.inner.assets(), state) {
            Some(pixmap) => {
                self.last_state_hash = hash;
                BakeOutcome::Fresh {
                    state_hash: hash,
                    pixmap,
                }
            }
            None => {
                // Force retry on the next sync; keep pending_state so
                // the host's reconcile flow doesn't treat this as Hidden.
                self.last_state_hash = 0;
                BakeOutcome::Hidden
            }
        }
    }

    fn invalidate_bake_cache(&mut self) {
        self.last_state_hash = 0;
    }

    fn hit_test(&self, pos: ScreenPointF) -> bool {
        self.inner.hit_test(pos)
    }

    fn cursor_hint(&self, pos: ScreenPointF) -> CursorHint {
        self.inner.cursor_hint(pos)
    }

    fn on_mouse_event(&mut self, event: MouseEvent) -> EventResponse {
        self.inner.on_mouse_event(event)
    }

    fn overlay_regions(&self) -> Vec<OverlayRegion> {
        self.inner.overlay_regions()
    }

    fn base_opacity(&self) -> f32 {
        self.inner.base_opacity()
    }
}

/// Hash a single `Hash` value with `DefaultHasher`.
pub fn hash_one<T: Hash + ?Sized>(value: &T) -> u64 {
    let mut h = std::collections::hash_map::DefaultHasher::new();
    value.hash(&mut h);
    h.finish()
}

/// Helper: pick the monitor whose bounds contain the center of `rect`.
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
