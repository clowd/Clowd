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

use crate::geometry::{RectExt, ScreenPointF};
use crate::render::{ComponentUpdate, WindowHandle};

use super::command::Command;
use super::component::*;

/// Per-component tracked state.
struct Entry {
    component: Box<dyn Component>,
    /// Monitor currently displaying this component, or `None` if hidden.
    current_monitor: Option<usize>,
    /// Hash of the last-baked pixmap shipped to a render thread.
    /// Used to skip redundant snapshots when the bake output hasn't changed.
    last_bake_hash: u64,
    /// Hash of the last overlay regions shipped. Changes in overlay regions
    /// (e.g. hover moved between buttons) must ship even if the pixmap is
    /// identical, so the render thread's animator gets new targets.
    last_overlay_hash: u64,
}

/// Manages all active UI components on the app thread.
pub struct ComponentHost {
    entries: Vec<Entry>,
    /// Cached `ComponentId → index` map for quick lookup.
    index_by_id: HashMap<ComponentId, usize>,
}

impl ComponentHost {
    pub fn new() -> Self {
        Self {
            entries: Vec::new(),
            index_by_id: HashMap::new(),
        }
    }

    /// Register a component. Components start `Hidden` — no snapshot is
    /// sent to any render thread until the next `sync()` where the
    /// component's `update()` returns `Placement::Visible`.
    pub fn add(&mut self, component: Box<dyn Component>) -> ComponentId {
        let id = component.id();
        let idx = self.entries.len();
        self.entries.push(Entry {
            component,
            current_monitor: None,
            last_bake_hash: 0,
            last_overlay_hash: 0,
        });
        self.index_by_id.insert(id, idx);
        id
    }

    /// Push the latest app state to every component, handle placement
    /// transitions, and ship snapshots to the owning monitor's render
    /// thread whenever the bake output has changed.
    pub fn sync(
        &mut self,
        ctx: &AppContext,
        windows: &HashMap<WindowId, WindowHandle>,
        monitor_window_ids: &[WindowId],
    ) {
        for entry in &mut self.entries {
            let placement = entry.component.update(ctx);
            Self::reconcile(
                entry,
                placement,
                windows,
                monitor_window_ids,
            );
        }
    }

    /// Hit-test all components in reverse registration order (topmost first).
    /// Returns the component ID and cursor hint if a component claims
    /// the point.
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
    ///
    /// For `Press`/`Release`: delivers to the topmost visible component
    /// under the cursor. For `Move`: delivers to ALL visible components
    /// so that components the cursor has left can clear their hover state
    /// and fade out.
    ///
    /// Returns a `Command` if a component emitted one; the app should
    /// match on it and act. `None` means the event was consumed
    /// internally (hover updates, fade-outs, ignored) — nothing for the
    /// app to do beyond its own non-UI logic.
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

        // Collect which entries to deliver to (by index) so we can iterate
        // with exclusive borrows on self.entries[i].
        let targets: Vec<usize> = match event {
            MouseEvent::Move { .. } => (0..self.entries.len())
                .rev()
                .filter(|&i| self.entries[i].current_monitor.is_some())
                .collect(),
            _ => {
                // Press/Release: topmost visible hit only.
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
                    // Self-refresh: ship an updated snapshot so the render
                    // thread's animator/texture picks up the change.
                    let entry = &mut self.entries[i];
                    if let Some(monitor_idx) = entry.current_monitor {
                        Self::emit_snapshot(
                            entry,
                            monitor_idx,
                            windows,
                            monitor_window_ids,
                        );
                    }
                }
                EventResponse::Command(cmd) => {
                    // Surface the first emitted command; later components
                    // still get the event (they may need to clear hover
                    // state) but their commands are dropped — only one
                    // command can be dispatched per click.
                    if emitted.is_none() {
                        emitted = Some(cmd);
                    }
                }
                EventResponse::Ignored => {}
            }
        }
        emitted
    }

    /// Handle a placement result from `update()`: send `Remove` to an
    /// outgoing monitor, then bake+ship if visible.
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

        // Migration / dismissal: if the monitor changed or component is
        // going hidden, send `Remove` to the old render thread first.
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
            // Force a snapshot ship on the new monitor by resetting hashes.
            entry.last_bake_hash = 0;
            entry.last_overlay_hash = 0;
        }

        if let Some(monitor_idx) = desired {
            Self::emit_snapshot(entry, monitor_idx, windows, monitor_window_ids);
        }
    }

    /// Bake the component, hash the result, and ship a `Snapshot` only if
    /// the pixmap OR overlay regions changed since the last ship.
    fn emit_snapshot(
        entry: &mut Entry,
        monitor_idx: usize,
        windows: &HashMap<WindowId, WindowHandle>,
        monitor_window_ids: &[WindowId],
    ) {
        let baked = entry.component.bake();
        let overlay = entry.component.overlay_regions();
        let base_opacity = entry.component.base_opacity();

        let bake_hash = hash_pixmap(baked.as_ref());
        let overlay_hash = hash_overlay(&overlay);

        if bake_hash == entry.last_bake_hash && overlay_hash == entry.last_overlay_hash {
            return;
        }
        entry.last_bake_hash = bake_hash;
        entry.last_overlay_hash = overlay_hash;

        let snapshot = ComponentSnapshot {
            id: entry.component.id(),
            pixmap: baked,
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

/// Hash the pixmap-affecting parts of a bake. Mirrors the
/// `OverlayBackend::snapshot_hash` logic — dest rect + size + sampled
/// bytes. Overlay regions are hashed separately.
fn hash_pixmap(baked: Option<&BakedPixmap>) -> u64 {
    let mut h = std::collections::hash_map::DefaultHasher::new();
    match baked {
        None => 0u8.hash(&mut h),
        Some(b) => {
            1u8.hash(&mut h);
            b.width.hash(&mut h);
            b.height.hash(&mut h);
            b.dest_vd.left().hash(&mut h);
            b.dest_vd.top().hash(&mut h);
            b.dest_vd.right().hash(&mut h);
            b.dest_vd.bottom().hash(&mut h);
            // Hash the full pixel data. Earlier we only sampled the
            // first/last 8 bytes, but any component whose visible content
            // changes in the interior (e.g. the Tips panel's color
            // sampler, which sits in the middle of the pixmap) would
            // miss re-ships. Hashing the whole thing is cheap compared
            // to the texture upload it gates.
            b.data.hash(&mut h);
        }
    }
    h.finish()
}

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
