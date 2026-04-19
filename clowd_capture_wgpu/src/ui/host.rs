//! App-thread component manager.
//!
//! `ComponentHost` owns all registered UI components, routes mouse events,
//! and ships snapshots to render threads based on each component's current
//! `Placement`. The app code never calls into a specific component — it
//! only pushes `AppContext` via `sync()` and hands off events via
//! `route_mouse_event()`.

use std::collections::HashMap;
use std::hash::{Hash, Hasher};

use winit::window::WindowId;

use crate::geometry::ScreenPointF;
use crate::render::{ComponentUpdate, WindowHandle};

use super::command::Command;
use super::component::*;

/// Per-component tracked state.
struct Entry {
    component: Box<dyn ErasedComponent>,
    /// Monitor currently displaying this component, or `None` if hidden.
    current_monitor: Option<usize>,
    /// Hash of the `State` on the last snapshot we shipped (pixmap OR
    /// overlay-only). The render thread owns its own `cached_state_hash`
    /// and trusts this value.
    last_state_hash: u64,
    /// Hash of the overlay regions on the last snapshot shipped. Hover
    /// changes overlay regions without touching `State`, so we track it
    /// separately and ship a `Keep` pixmap when only overlay changed.
    last_overlay_hash: u64,
}

/// Manages all active UI components on the app thread.
pub struct ComponentHost {
    entries: Vec<Entry>,
    index_by_id: HashMap<ComponentId, usize>,
}

impl ComponentHost {
    pub fn new() -> Self {
        Self {
            entries: Vec::new(),
            index_by_id: HashMap::new(),
        }
    }

    /// Register a component. The concrete type is wrapped in a
    /// `ComponentBox` so the host can store `Box<dyn ErasedComponent>`
    /// despite `Component` carrying associated types.
    pub fn add<C: Component + 'static>(&mut self, component: C) -> ComponentId {
        let id = component.id();
        let idx = self.entries.len();
        self.entries.push(Entry {
            component: Box::new(ComponentBox::new(component)),
            current_monitor: None,
            last_state_hash: 0,
            last_overlay_hash: 0,
        });
        self.index_by_id.insert(id, idx);
        id
    }

    /// Push the latest app state to every component, handle placement
    /// transitions, and ship snapshots to the owning monitor's render
    /// thread whenever the bake output (or overlay regions) changed.
    pub fn sync(
        &mut self,
        ctx: &AppContext,
        windows: &HashMap<WindowId, WindowHandle>,
        monitor_window_ids: &[WindowId],
    ) {
        for entry in &mut self.entries {
            let placement = entry.component.sync_derive(ctx);
            Self::reconcile(entry, placement, windows, monitor_window_ids);
        }
    }

    /// Hit-test all components in reverse registration order (topmost first).
    pub fn hit_test(&self, pos: ScreenPointF) -> Option<(ComponentId, CursorHint)> {
        for entry in self.entries.iter().rev() {
            if entry.current_monitor.is_none() {
                continue;
            }
            if entry.component.hit_test(pos) {
                return Some((entry.component.id(), entry.component.cursor_hint(pos)));
            }
        }
        None
    }

    /// Route a mouse event to the appropriate visible component(s) and
    /// self-refresh any component whose state changed.
    pub fn route_mouse_event(
        &mut self,
        event: MouseEvent,
        windows: &HashMap<WindowId, WindowHandle>,
        monitor_window_ids: &[WindowId],
    ) -> Option<Command> {
        let pos = match event {
            MouseEvent::Move { pos }
            | MouseEvent::Press { pos }
            | MouseEvent::Release { pos } => pos,
        };

        let targets: Vec<usize> = match event {
            MouseEvent::Move { .. } => (0..self.entries.len())
                .rev()
                .filter(|&i| self.entries[i].current_monitor.is_some())
                .collect(),
            _ => {
                let hit = (0..self.entries.len()).rev().find(|&i| {
                    let e = &self.entries[i];
                    e.current_monitor.is_some() && e.component.hit_test(pos)
                });
                hit.into_iter().collect()
            }
        };

        let mut emitted: Option<Command> = None;
        for i in targets {
            let response = self.entries[i].component.on_mouse_event(event);

            match response {
                EventResponse::NeedsOverlayUpdate | EventResponse::NeedsRedraw => {
                    let entry = &mut self.entries[i];
                    if let Some(monitor_idx) = entry.current_monitor {
                        Self::emit_snapshot(entry, monitor_idx, windows, monitor_window_ids);
                    }
                }
                EventResponse::Command(cmd) => {
                    if emitted.is_none() {
                        emitted = Some(cmd);
                    }
                }
                EventResponse::Ignored => {}
            }
        }
        emitted
    }

    /// Handle a placement result from `sync_derive()`: send `Remove` to
    /// an outgoing monitor, then bake+ship if visible.
    fn reconcile(
        entry: &mut Entry,
        placement: Placement,
        windows: &HashMap<WindowId, WindowHandle>,
        monitor_window_ids: &[WindowId],
    ) {
        let desired = match placement {
            Placement::Hidden => None,
            Placement::Visible { monitor_idx } => Some(monitor_idx),
        };

        if entry.current_monitor != desired {
            if let Some(old) = entry.current_monitor {
                if let Some(wid) = monitor_window_ids.get(old) {
                    if let Some(h) = windows.get(wid) {
                        h.send_component_update(ComponentUpdate::Remove(
                            entry.component.id(),
                        ));
                    }
                }
            }
            entry.current_monitor = desired;
            // Force a bake on the new monitor — the component's bake
            // cache is keyed by state hash, not by destination, so we
            // invalidate explicitly to guarantee the new render thread
            // gets a fresh pixmap.
            entry.component.invalidate_bake_cache();
            entry.last_state_hash = 0;
            entry.last_overlay_hash = 0;
        }

        if let Some(monitor_idx) = desired {
            Self::emit_snapshot(entry, monitor_idx, windows, monitor_window_ids);
        }
    }

    /// Ask the component for a bake + overlay, ship a snapshot if
    /// anything changed. Pixmap is shipped as `Replace` on a fresh
    /// bake, `Keep` when only the overlay regions moved.
    fn emit_snapshot(
        entry: &mut Entry,
        monitor_idx: usize,
        windows: &HashMap<WindowId, WindowHandle>,
        monitor_window_ids: &[WindowId],
    ) {
        let outcome = entry.component.try_bake();
        let overlay = entry.component.overlay_regions();
        let overlay_hash = hash_overlay(&overlay);
        let base_opacity = entry.component.base_opacity();

        let (state_hash, pixmap) = match outcome {
            BakeOutcome::Hidden => return,
            BakeOutcome::Fresh { state_hash, pixmap } => {
                (state_hash, SnapshotPixmap::Replace(pixmap))
            }
            BakeOutcome::Unchanged { state_hash } => (state_hash, SnapshotPixmap::Keep),
        };

        let pixmap_changed = matches!(pixmap, SnapshotPixmap::Replace(_));
        let overlay_changed = overlay_hash != entry.last_overlay_hash;
        let state_hash_changed = state_hash != entry.last_state_hash;

        if !pixmap_changed && !overlay_changed && !state_hash_changed {
            return;
        }
        entry.last_state_hash = state_hash;
        entry.last_overlay_hash = overlay_hash;

        let snapshot = ComponentSnapshot {
            id: entry.component.id(),
            state_hash,
            pixmap,
            overlay_regions: overlay,
            base_opacity,
        };
        if let Some(wid) = monitor_window_ids.get(monitor_idx) {
            if let Some(h) = windows.get(wid) {
                h.send_component_update(ComponentUpdate::Snapshot(snapshot));
            }
        }
    }
}

impl Default for ComponentHost {
    fn default() -> Self {
        Self::new()
    }
}

/// Hash the overlay-region targets so we can detect hover animation
/// changes that don't touch the baked pixmap.
fn hash_overlay(regions: &[OverlayRegion]) -> u64 {
    let mut h = std::collections::hash_map::DefaultHasher::new();
    regions.len().hash(&mut h);
    for r in regions {
        for f in r.uv_rect.iter().chain(std::iter::once(&r.target_amount)) {
            f.to_bits().hash(&mut h);
        }
        (r.mode as u32).hash(&mut h);
    }
    h.finish()
}
