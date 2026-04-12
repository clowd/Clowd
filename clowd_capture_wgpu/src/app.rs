use std::collections::HashMap;
use std::sync::{Arc, Barrier};

use winit::application::ApplicationHandler;
use winit::dpi::{PhysicalPosition, PhysicalSize};
use winit::event::{ElementState, KeyEvent, MouseButton, MouseScrollDelta, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::{Key, NamedKey};
#[cfg(windows)]
use winit::platform::windows::WindowAttributesExtWindows;
use winit::window::{CursorIcon, Window, WindowId};

use crate::geometry::{RectExt, ScreenPoint, ScreenPointF, ScreenRect, ScreenRectRounded};
use crate::gpu::bootstrap_window_gpu;
use crate::panel::{self, PanelState};
use crate::platform;
use crate::settings::CapturerSettings;
use crate::system::{CapturedDesktop, SystemInterop, WindowWalker};
use crate::window_state::{spawn_render_thread, WindowHandle};

/// Minimum zoom. The magnifier only ever enlarges the source.
const ZOOM_MIN: f32 = 1.0;
/// Maximum zoom. Matches the original C++ capturer (Screens.cpp /
/// DxScreenCapture.cpp — `min(max(zoom, 1), 256)`).
const ZOOM_MAX: f32 = 256.0;
/// Multiplicative step per wheel tick. Coarse by design — no modifier-key
/// fine-grained step in v1.
const ZOOM_STEP: f32 = 2.0;

/// Pre-DPI radius (in virtual-desktop pixels) of the resize-handle hit
/// boxes used once a selection is captured. Matches
/// `UNSCALED_DRAG_HANDLE_SIZE` at DxScreenCapture.cpp:23. The C++ scales
/// it by `dpizoom = monitor.dpi / BASE_DPI`; we do the same per-monitor
/// using `monitor_dpi`.
const UNSCALED_DRAG_HANDLE_SIZE: f32 = 10.0;

/// Result of hit-testing the cursor against a captured selection rect.
/// Drives both the cursor-icon swap (one-to-one with `IDC_*` cursors in
/// `FrameSetCursor` at DxScreenCapture.cpp:1732-1798) and the resize/move
/// drag mode that mouse-down promotes it to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Hittest {
    /// Cursor is outside the selection (and not on any handle). Default
    /// arrow cursor; mouse-down does nothing in this state.
    Outside,
    /// Cursor is inside the selection's interior. Move cursor;
    /// mouse-down enters MoveDrag.
    Inside,
    Top,
    Right,
    Bottom,
    Left,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

impl Hittest {
    /// Map this hittest to the cursor icon that should be displayed
    /// while hovering. Mirrors `FrameSetCursor`'s switch at
    /// DxScreenCapture.cpp:1736.
    fn cursor(self) -> CursorIcon {
        match self {
            Hittest::Outside => CursorIcon::Default,
            Hittest::Inside => CursorIcon::Move,
            Hittest::Top | Hittest::Bottom => CursorIcon::NsResize,
            Hittest::Left | Hittest::Right => CursorIcon::EwResize,
            Hittest::TopLeft | Hittest::BottomRight => CursorIcon::NwseResize,
            Hittest::TopRight | Hittest::BottomLeft => CursorIcon::NeswResize,
        }
    }
}

/// Result of a Copy or Save action.
enum ActionResult {
    /// Operation completed successfully.
    Success,
    /// User cancelled the operation (e.g. dismissed save dialog).
    Cancelled,
    /// Operation failed with an error message.
    Failed(String),
}

/// What kind of drag is currently in progress. Set on mouse-down,
/// consumed by every CursorMoved until mouse-up. The pre-capture
/// "drawing a new selection" path doesn't use this enum (it's still
/// driven by `mouse_down` + `dragging`); these variants only fire
/// once `captured` is true.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DragMode {
    /// Translating the whole rect. Each frame the rect is shifted by
    /// `(virtual_cursor - mouse_down_pt)` from the snapshotted anchor
    /// selection, then soft-clamped to keep it on-screen *without*
    /// changing its size.
    Move,
    /// Resizing via one of the eight handles. The handle's edges are
    /// dragged to the cursor each frame; the un-touched edges keep
    /// their value from the snapshotted anchor selection. Hard-clamp
    /// each moved edge to vd bounds, then normalise via min/max so
    /// the rect stays well-formed even when the user drags past the
    /// opposite edge (matches the C++ `Xy12Rect` normalisation).
    Resize(Hittest),
}

/// Virtual-cursor + magnifier + selection state owned by the event-loop
/// thread.
///
/// When `anchored` is false (the zoom=1 case) the OS cursor is authoritative
/// and `virtual_cursor` mirrors it exactly. When `anchored` is true (zoom>1)
/// the real OS cursor is pinned to `anchor` via SetCursorPos; each
/// CursorMoved event instead produces a `(os - anchor) / zoom` delta that
/// advances the virtual cursor in fractional world pixels. See the
/// reference C++ in clowd_capture_dx/Screens.cpp:MouseAnchorStart /
/// MouseAnchorUpdate / MouseAnchorStop for the original design.
///
/// The selection state machine mirrors the C++ `mc_frame_data` flags from
/// DxScreenCapture.cpp directly (mouse_down / dragging / captured) rather
/// than collapsing them into an enum. Visible states:
///   Idle:           !mouse_down && !dragging && !captured
///   Pending-drag:    mouse_down && !dragging && !captured
///   Dragging:        mouse_down &&  dragging && !captured
///   Captured:       !mouse_down && !dragging &&  captured
struct InputState {
    /// The logical cursor in virtual-desktop pixels. Always-live: even at
    /// zoom=1 we keep it updated so the zoom-in transition doesn't need
    /// a special "sample the OS cursor" step.
    virtual_cursor: ScreenPointF,
    /// Magnifier scale in [ZOOM_MIN, ZOOM_MAX]. 1.0 = unzoomed (no anchor).
    zoom: f32,
    /// Whether the OS cursor is currently pinned to `anchor`. Implied by
    /// `zoom > 1`, but tracked explicitly so the start/stop transitions
    /// (which need a single SetCursorPos warp) are driven by the wheel
    /// handler and not by every CursorMoved event.
    anchored: bool,
    /// Fixed point in virtual-desktop pixels (== real screen coords, since
    /// the origin of the virtual desktop *is* where primary monitor origin
    /// plus screen = real coords match). Computed once at startup as the
    /// centre of the primary monitor, per Screens.cpp:111-114.
    anchor: ScreenPoint,
    /// Left mouse button currently held down. Set on Pressed, cleared on
    /// Released. Cleared regardless of whether a drag was actually
    /// promoted, so a click that never crossed the drag threshold is a
    /// no-op overall.
    mouse_down: bool,
    /// `Some(virtual_cursor)` captured at the moment of mouse-down, in
    /// virtual-desktop pixels. The drag rectangle is computed against
    /// this point on every subsequent CursorMoved.
    mouse_down_pt: Option<ScreenPointF>,
    /// DPI scale of the monitor that contained `mouse_down_pt` at the
    /// moment of mouse-down, captured **once** so the drag-distance
    /// threshold doesn't flicker as the cursor crosses display
    /// boundaries during a drag. Falls back to 1.0 if no monitor
    /// contained the press point.
    mouse_down_dpi: f32,
    /// Promoted from `false` to `true` once the rounded selection
    /// width OR height exceeds `6 / (mouse_down_dpi * zoom)` virtual-
    /// desktop pixels — the same threshold as
    /// DxScreenCapture.cpp:1497.
    dragging: bool,
    /// Current selection rectangle in virtual-desktop pixel coordinates,
    /// or `None` if there is no selection (idle, pre-threshold, or
    /// dragged back to a degenerate rect). Updated continuously while
    /// `dragging`; preserved verbatim once `captured`.
    selection: Option<ScreenRect>,
    /// Becomes `true` once the user releases the mouse with a non-empty
    /// selection. While captured the wheel handler is a no-op (matches
    /// DxScreenCapture.cpp:1527), the rendered crosshair is suppressed,
    /// and the cursor switches between resize/move icons based on the
    /// per-frame `hittest`.
    captured: bool,
    /// Latest hit test result against the captured selection rect.
    /// Refreshed on every CursorMoved while `captured`. Determines the
    /// cursor icon (via `Hittest::cursor`) and is the seed for the
    /// drag mode entered on the next mouse-down. Always `Outside`
    /// before capture and reset on un-capture.
    hittest: Hittest,
    /// What kind of drag is currently in progress (only meaningful
    /// while `captured && mouse_down`). `None` between drags.
    drag_mode: Option<DragMode>,
    /// The selection rect at the moment the current drag started, in
    /// virtual-desktop pixel coords. Both `Move` and `Resize` use this
    /// as the anchor against which `(cursor - mouse_down_pt)` deltas
    /// are applied — using a frozen anchor instead of incrementally
    /// updating the rect avoids drift and gives the soft-clamp
    /// "snap back when cursor returns into bounds" behaviour the user
    /// asked for.
    drag_anchor_selection: Option<ScreenRect>,
    /// Cached panel state for the currently-finalised selection, or
    /// `None` when no panel is visible. Computed on capture and
    /// refreshed whenever the selection changes. The app thread's
    /// own copy is used for hit-testing; a clone is shipped to the
    /// relevant render thread via `RenderMsg::PanelState`.
    panel_state: Option<PanelState>,
    /// Index of the monitor (into `monitor_bounds`) whose window
    /// currently owns the panel, or `None` if no panel is live. Used
    /// to route panel state updates to only that thread.
    panel_monitor_idx: Option<usize>,
}

pub struct App {
    settings: Arc<CapturerSettings>,
    windows: HashMap<WindowId, WindowHandle>,
    /// Stable, index-order list of window IDs parallel to
    /// `monitor_bounds` / `monitor_dpi`. Populated in `resumed()` in
    /// the same order windows are created; used by the panel plumbing
    /// to look up the `WindowHandle` for a given monitor index.
    monitor_window_ids: Vec<WindowId>,
    /// Populated once in `resumed()`. Used by `clamp_to_nearest_monitor`
    /// so the virtual cursor can't escape all physical screens while the
    /// OS cursor is pinned to the anchor, and by the mouse-down handler
    /// to find the monitor under the press point for the drag-threshold
    /// DPI lookup.
    monitor_bounds: Vec<ScreenRect>,
    /// Per-monitor DPI scale, parallel-indexed with `monitor_bounds`.
    /// Read at mouse-down to seed `InputState::mouse_down_dpi` so the
    /// drag-distance threshold matches the C++ `dpizoom` factor at
    /// DxScreenCapture.cpp:1497.
    monitor_dpi: Vec<f32>,
    /// Union rect of the virtual desktop in physical pixels — same value
    /// as `captured.bounds` from the startup snapshot. Used to soft-
    /// clamp the selection during move/resize so it can't be pushed off
    /// screen (matches `WorkspaceBounds()` + `ClipRectBy` at
    /// DxScreenCapture.cpp:1844-1845).
    vd_bounds: ScreenRect,
    input: InputState,
    /// Retained desktop pixel data for Copy/Save operations. Shared
    /// with render threads via Arc (read-only after capture).
    desktop_buffer: Option<Arc<CapturedDesktop>>,
    /// Window walker: pre-detected window rects for click-to-select.
    walker: Option<WindowWalker>,
}

impl App {
    pub fn new(settings: Arc<CapturerSettings>) -> Self {
        Self {
            settings,
            windows: HashMap::new(),
            monitor_window_ids: Vec::new(),
            monitor_bounds: Vec::new(),
            monitor_dpi: Vec::new(),
            vd_bounds: ScreenRect::zero(),
            desktop_buffer: None,
            walker: None,
            // Real values are written in `resumed()` once we know where
            // the primary monitor is and where the cursor currently sits.
            // Zero here is a placeholder that never gets broadcast.
            input: InputState {
                virtual_cursor: ScreenPointF::new(0.0, 0.0),
                zoom: 1.0,
                anchored: false,
                anchor: ScreenPoint::new(0, 0),
                mouse_down: false,
                mouse_down_pt: None,
                mouse_down_dpi: 1.0,
                dragging: false,
                selection: None,
                captured: false,
                hittest: Hittest::Outside,
                drag_mode: None,
                drag_anchor_selection: None,
                panel_state: None,
                panel_monitor_idx: None,
            },
        }
    }

    /// Push the current `(virtual_cursor, zoom, selection, captured)` to
    /// every render thread. Monitors that don't contain the cursor still
    /// need the message so they can apply the zoom transform uniformly
    /// (their crosshair vanishes via the shader's integer-equality miss),
    /// and so each render thread can run its own VD→window-local pixel
    /// transform on the selection rect.
    fn broadcast_mouse_state(&self) {
        for h in self.windows.values() {
            h.update_mouse_state(
                self.input.virtual_cursor,
                self.input.zoom,
                self.input.selection,
                self.input.captured,
            );
        }
    }

    /// Hide all capture windows.
    fn hide_all_windows(&self) {
        for h in self.windows.values() {
            h.window.set_visible(false);
        }
    }

    /// Show all capture windows.
    fn show_all_windows(&self) {
        for h in self.windows.values() {
            h.window.set_visible(true);
        }
    }

    /// Recompute the panel layout from the currently-captured selection
    /// and ship the new state to every render thread (`None` to
    /// non-panel monitors, `Some(...)` to the one whose monitor owns
    /// the panel). Called on initial capture, after move/resize
    /// drags, and on hover changes.
    ///
    /// The panel lives on **one** monitor at a time: whichever one
    /// contains the selection's centre. That matches the C++ behaviour
    /// — `SetButtonPanelPositions` is called per-monitor with the same
    /// selection, and only the monitor whose clipped selection rect
    /// overlaps produces a visible panel.
    fn recompute_panel(&mut self) {
        let (sel, dpi, monitor_idx, monitor_bounds) = match (
            self.input.selection,
            self.monitor_bounds.is_empty(),
        ) {
            (Some(sel), false) => {
                // Pick the monitor containing the selection's centre.
                // Matches the "intersect the selection with the
                // monitor's workspace" logic in the C++ — the centre
                // lookup is a simpler equivalent for the common case.
                let cx = (sel.left() + sel.right()) / 2;
                let cy = (sel.top() + sel.bottom()) / 2;
                let found = self
                    .monitor_bounds
                    .iter()
                    .enumerate()
                    .find(|(_, m)| {
                        cx >= m.left() && cx < m.right() && cy >= m.top() && cy < m.bottom()
                    })
                    .map(|(i, m)| (i, *m));
                match found {
                    Some((i, mb)) => {
                        let d = self.monitor_dpi.get(i).copied().unwrap_or(1.0);
                        (sel, d, i, mb)
                    }
                    None => {
                        self.clear_panel();
                        return;
                    }
                }
            }
            _ => {
                self.clear_panel();
                return;
            }
        };

        let Some(layout) = panel::compute_layout(monitor_bounds, sel, dpi) else {
            self.clear_panel();
            return;
        };

        // Fold the existing hover index forward if still valid —
        // otherwise clear it. The caller updates hover after this
        // returns when the user is actively moving the cursor.
        let hover_idx = self.input.panel_state.as_ref().and_then(|p| p.hover_idx);

        let new_state = PanelState {
            layout,
            hover_idx,
            selection_size: (sel.width(), sel.height()),
            monitor_bounds,
            dpi_scale: dpi,
            accent_color: self.settings.crosshair_color,
        };

        self.input.panel_state = Some(new_state.clone());
        self.input.panel_monitor_idx = Some(monitor_idx);

        // Broadcast: Some(...) to the owning monitor, None to the rest.
        for (i, wid) in self.monitor_window_ids.iter().enumerate() {
            if let Some(h) = self.windows.get(wid) {
                if i == monitor_idx {
                    h.update_panel_state(Some(new_state.clone()));
                } else {
                    h.update_panel_state(None);
                }
            }
        }
    }

    /// Drop the current panel state and tell every render thread to
    /// clear its backend. Called when capture is reset, when the
    /// selection is dragged off-screen, or when a selection doesn't
    /// overlap any monitor.
    fn clear_panel(&mut self) {
        if self.input.panel_state.is_none() && self.input.panel_monitor_idx.is_none() {
            return;
        }
        self.input.panel_state = None;
        self.input.panel_monitor_idx = None;
        for h in self.windows.values() {
            h.update_panel_state(None);
        }
    }

    /// Extract the selected region from the desktop buffer, converting BGRA to RGBA.
    /// Returns `None` if selection is outside bounds or buffer is missing.
    fn extract_selection_rgba(&self) -> Option<(Vec<u8>, u32, u32)> {
        let selection = self.input.selection?;
        let buffer = self.desktop_buffer.as_ref()?;

        // Convert selection from virtual-desktop coords to buffer-local coords
        let buf_x = (selection.left() - buffer.bounds.left()).max(0) as u32;
        let buf_y = (selection.top() - buffer.bounds.top()).max(0) as u32;
        let sel_w = selection.width() as u32;
        let sel_h = selection.height() as u32;

        // Clamp to buffer bounds
        if buf_x >= buffer.width || buf_y >= buffer.height {
            return None;
        }
        let copy_w = sel_w.min(buffer.width - buf_x);
        let copy_h = sel_h.min(buffer.height - buf_y);

        if copy_w == 0 || copy_h == 0 {
            return None;
        }

        // Extract sub-region with BGRA→RGBA conversion
        let stride = (buffer.width * 4) as usize;
        let mut rgba = Vec::with_capacity((copy_w * copy_h * 4) as usize);

        for row in 0..copy_h {
            let src_start = ((buf_y + row) as usize * stride) + (buf_x as usize * 4);
            let src_end = src_start + (copy_w as usize * 4);
            let src_row = &buffer.bgra[src_start..src_end];

            for chunk in src_row.chunks_exact(4) {
                // BGRA → RGBA: swap B and R
                rgba.push(chunk[2]); // R
                rgba.push(chunk[1]); // G
                rgba.push(chunk[0]); // B
                rgba.push(chunk[3]); // A
            }
        }

        Some((rgba, copy_w, copy_h))
    }

    /// Copy the selected region to the clipboard.
    fn handle_copy(&self) -> ActionResult {
        let Some((rgba, width, height)) = self.extract_selection_rgba() else {
            log::warn!("copy: no selection or failed to extract");
            return ActionResult::Failed("No selection to copy".to_string());
        };

        match arboard::Clipboard::new() {
            Ok(mut clipboard) => {
                let img = arboard::ImageData {
                    width: width as usize,
                    height: height as usize,
                    bytes: std::borrow::Cow::Owned(rgba),
                };
                if let Err(e) = clipboard.set_image(img) {
                    log::error!("copy: clipboard set_image failed: {e}");
                    ActionResult::Failed(format!("Failed to copy to clipboard: {e}"))
                } else {
                    log::info!("copied {}x{} image to clipboard", width, height);
                    ActionResult::Success
                }
            }
            Err(e) => {
                log::error!("copy: failed to open clipboard: {e}");
                ActionResult::Failed(format!("Failed to open clipboard: {e}"))
            }
        }
    }

    /// Save the selected region to a file via save dialog.
    /// Save the selected region to a file via save dialog. Returns true on success.
    /// Save the selected region to a file via save dialog.
    fn handle_save(&self, window: &Window) -> ActionResult {
        let Some((rgba, width, height)) = self.extract_selection_rgba() else {
            log::warn!("save: no selection or failed to extract");
            return ActionResult::Failed("No selection to save".to_string());
        };

        // Show save dialog (parented to capture window so it can't fall behind)
        let path = rfd::FileDialog::new()
            .add_filter("PNG Image", &["png"])
            .add_filter("JPEG Image", &["jpg", "jpeg"])
            .set_file_name("screenshot.png")
            .set_parent(window)
            .save_file();

        let Some(mut path) = path else {
            log::info!("save: dialog cancelled");
            return ActionResult::Cancelled;
        };

        // Auto-append extension if missing
        let ext = path
            .extension()
            .and_then(|e| e.to_str())
            .map(|s| s.to_lowercase());

        let format = match ext.as_deref() {
            Some("png") => image::ImageFormat::Png,
            Some("jpg") | Some("jpeg") => image::ImageFormat::Jpeg,
            _ => {
                // Default to PNG if no/unknown extension
                path.set_extension("png");
                image::ImageFormat::Png
            }
        };

        // Create image and save
        let img: image::RgbaImage =
            image::ImageBuffer::from_raw(width, height, rgba).expect("buffer size matches");

        if let Err(e) = img.save_with_format(&path, format) {
            log::error!("save: failed to write {:?}: {e}", path);
            ActionResult::Failed(format!("Failed to save file: {e}"))
        } else {
            log::info!("saved {}x{} image to {:?}", width, height, path);
            ActionResult::Success
        }
    }

    /// Reset the selection and return to draw mode.
    fn handle_reset(&mut self, window: &Window) {
        // Clear selection state
        self.input.selection = None;
        self.input.captured = false;
        self.input.hittest = Hittest::Outside;
        self.input.drag_mode = None;
        self.input.drag_anchor_selection = None;

        // Clear panel
        self.clear_panel();

        // Reset cursor
        window.set_cursor(CursorIcon::Default);

        // Re-query the walker so the highlight reappears immediately
        // under the cursor without waiting for the next mouse-move.
        let pt = ScreenPoint::new(
            self.input.virtual_cursor.x.round() as i32,
            self.input.virtual_cursor.y.round() as i32,
        );
        self.input.selection = self.walker.as_ref().and_then(|w| w.hit_test(pt));

        // Broadcast cleared state to render threads
        self.broadcast_mouse_state();

        log::info!("selection reset");
    }
}

/// Hit-test the cursor (in virtual-desktop pixels) against a captured
/// selection rect. The handle radius is `UNSCALED_DRAG_HANDLE_SIZE *
/// dpi_scale` rounded down, mirroring DxScreenCapture.cpp:1676. Corners
/// are tested before edges so they win at the corner squares (same
/// loop order as DxScreenCapture.cpp:1695-1721).
fn hit_test(cursor: ScreenPointF, sel: ScreenRect, dpi_scale: f32) -> Hittest {
    let r = (UNSCALED_DRAG_HANDLE_SIZE * dpi_scale).floor() as i32;
    let cx = cursor.x.floor() as i32;
    let cy = cursor.y.floor() as i32;

    // Corner: a `(2r+1) × (2r+1)` square centred on the corner pixel,
    // matching `PtToWidenedRect` at rectex.h:119-127.
    let in_corner = |x: i32, y: i32| -> bool {
        cx >= x - r && cx <= x + r && cy >= y - r && cy <= y + r
    };
    // Edge: a rect widened by `r` on every side around the line
    // segment. Same shape as the corners on the perpendicular axis,
    // but stretched along the edge — matching `LineToWidenedRect` at
    // rectex.h:129-137.
    let in_edge = |x1: i32, y1: i32, x2: i32, y2: i32| -> bool {
        let lx = x1.min(x2) - r;
        let rx = x1.max(x2) + r;
        let ty = y1.min(y2) - r;
        let by = y1.max(y2) + r;
        cx >= lx && cx <= rx && cy >= ty && cy <= by
    };

    let l = sel.left();
    let t = sel.top();
    let r_edge = sel.right();
    let b = sel.bottom();

    // Order matches the C++ handles[] array at DxScreenCapture.cpp:1695:
    // four corners first, then four edges, with first-hit-wins.
    if in_corner(l, t) {
        return Hittest::TopLeft;
    }
    if in_corner(r_edge, t) {
        return Hittest::TopRight;
    }
    if in_corner(r_edge, b) {
        return Hittest::BottomRight;
    }
    if in_corner(l, b) {
        return Hittest::BottomLeft;
    }
    if in_edge(l, t, r_edge, t) {
        return Hittest::Top;
    }
    if in_edge(r_edge, t, r_edge, b) {
        return Hittest::Right;
    }
    if in_edge(r_edge, b, l, b) {
        return Hittest::Bottom;
    }
    if in_edge(l, t, l, b) {
        return Hittest::Left;
    }

    // Fall through: inside the rect (move) or fully outside.
    if cx >= l && cx < r_edge && cy >= t && cy < b {
        Hittest::Inside
    } else {
        Hittest::Outside
    }
}

/// Translate the anchor rect by `(delta_x, delta_y)` and return its
/// intersection with the virtual desktop bounds, or `None` if the
/// translated rect is fully outside the vd. This is the "move" path's
/// core: the underlying logical position is free-form (whatever the
/// cursor points at), and what gets drawn + eventually saved is the
/// *cropped* rect so the selection can't "run off" a display — it
/// visually shrinks against the boundary and reappears when the
/// cursor comes back, all without mutating the anchor.
fn move_and_crop(
    anchor: ScreenRect,
    delta_x: i32,
    delta_y: i32,
    vd: ScreenRect,
) -> Option<ScreenRect> {
    let moved = ScreenRect::from_xy_size(
        anchor.left() + delta_x,
        anchor.top() + delta_y,
        anchor.width(),
        anchor.height(),
    );
    intersect_rects(moved, vd)
}

/// Intersection of two rects, or `None` if they don't overlap. Used by
/// the move-and-crop path to clip the translated rect to vd bounds
/// each frame.
fn intersect_rects(a: ScreenRect, b: ScreenRect) -> Option<ScreenRect> {
    let l = a.left().max(b.left());
    let t = a.top().max(b.top());
    let r = a.right().min(b.right());
    let bot = a.bottom().min(b.bottom());
    if r > l && bot > t {
        Some(ScreenRect::from_exact(l, t, r, bot))
    } else {
        None
    }
}

/// Apply a `Resize` drag to an anchor selection rect via the cursor's
/// current virtual-desktop position. Each handle pulls a different
/// subset of the four edges:
///   * corners drag two edges (the corner-opposite stays fixed)
///   * edges drag one edge (the other three stay fixed)
///   * inside / outside don't enter resize mode
/// Each moved edge is hard-clamped to the vd bounds before the rect is
/// re-normalised, so dragging the right edge past the left flips the
/// rect (matches the C++ `Xy12Rect` normalisation at rectex.h:111-117
/// and DxScreenCapture.cpp:1419-1458).
fn resize_with_clamp(
    anchor: ScreenRect,
    handle: Hittest,
    cursor_x: i32,
    cursor_y: i32,
    vd: ScreenRect,
) -> ScreenRect {
    let mut left = anchor.left();
    let mut top = anchor.top();
    let mut right = anchor.right();
    let mut bottom = anchor.bottom();
    // Each handle pins the opposite corner and drags the named one(s).
    match handle {
        Hittest::Top => {
            top = cursor_y;
        }
        Hittest::Bottom => {
            bottom = cursor_y;
        }
        Hittest::Left => {
            left = cursor_x;
        }
        Hittest::Right => {
            right = cursor_x;
        }
        Hittest::TopLeft => {
            left = cursor_x;
            top = cursor_y;
        }
        Hittest::TopRight => {
            right = cursor_x;
            top = cursor_y;
        }
        Hittest::BottomLeft => {
            left = cursor_x;
            bottom = cursor_y;
        }
        Hittest::BottomRight => {
            right = cursor_x;
            bottom = cursor_y;
        }
        // The Inside/Outside hittests aren't valid resize handles —
        // mouse-down dispatch routes them to Move / no-op respectively,
        // so we should never see them here. Defensive no-op.
        Hittest::Inside | Hittest::Outside => {}
    }
    // Clamp every edge — even ones we didn't move — so an anchor that
    // was already partly off-screen can't escape further. The C++
    // `ClipRectBy` only runs at finalisation, but here we want the
    // soft-clamp behaviour live on every frame.
    left = left.clamp(vd.left(), vd.right());
    right = right.clamp(vd.left(), vd.right());
    top = top.clamp(vd.top(), vd.bottom());
    bottom = bottom.clamp(vd.top(), vd.bottom());
    // Normalise into a min/max rect (mirrors `Xy12Rect`).
    let nl = left.min(right);
    let nr = left.max(right);
    let nt = top.min(bottom);
    let nb = top.max(bottom);
    ScreenRect::from_exact(nl, nt, nr, nb)
}

/// Look up the DPI scale of whichever monitor currently contains `p`,
/// returning `1.0` if the point falls in the multi-monitor dead zone.
/// Used at mouse-down to seed the drag-distance threshold so that
/// dragging across a monitor boundary doesn't change the threshold mid-
/// gesture (matches the C++ behaviour at DxScreenCapture.cpp:1497, which
/// reads `dpizoom` once from the monitor under `mouseDownPt`).
fn dpi_at_point(p: ScreenPointF, monitors: &[ScreenRect], dpis: &[f32]) -> f32 {
    for (i, m) in monitors.iter().enumerate() {
        let mx = m.min_x() as f32;
        let my = m.min_y() as f32;
        let mw = m.width() as f32;
        let mh = m.height() as f32;
        if p.x >= mx && p.x < mx + mw && p.y >= my && p.y < my + mh {
            return dpis.get(i).copied().unwrap_or(1.0);
        }
    }
    1.0
}

/// Clamp the point to whichever monitor currently contains it, or to the
/// nearest monitor if it's in the multi-monitor dead zone. A tiny epsilon
/// is subtracted from the max edge so the point never lands on the
/// exclusive right/bottom of a rect (matches Screens.cpp:147-153 which
/// subtracts 0.001 from the right/bottom bounds).
fn clamp_to_nearest_monitor(p: &mut ScreenPointF, monitors: &[ScreenRect]) {
    if monitors.is_empty() {
        return;
    }
    // First try: already inside a monitor? Leave it alone.
    for m in monitors {
        let mx = m.min_x() as f32;
        let my = m.min_y() as f32;
        let mw = m.width() as f32;
        let mh = m.height() as f32;
        if p.x >= mx && p.x < mx + mw && p.y >= my && p.y < my + mh {
            return;
        }
    }
    // Not inside any monitor — pick the one whose centre is closest and
    // clamp into its bounds. Distance-to-centre is good enough as a
    // "nearest monitor" heuristic for a cursor that's just crossed a
    // boundary.
    let (best_ix, _) = monitors
        .iter()
        .enumerate()
        .map(|(i, m)| {
            let cx = m.min_x() as f32 + m.width() as f32 * 0.5;
            let cy = m.min_y() as f32 + m.height() as f32 * 0.5;
            let dx = p.x - cx;
            let dy = p.y - cy;
            (i, dx * dx + dy * dy)
        })
        .fold((0usize, f32::INFINITY), |(bi, bd), (i, d)| {
            if d < bd {
                (i, d)
            } else {
                (bi, bd)
            }
        });
    let m = &monitors[best_ix];
    let min_x = m.min_x() as f32;
    let min_y = m.min_y() as f32;
    let max_x = (m.min_x() + m.width()) as f32 - 0.001;
    let max_y = (m.min_y() + m.height()) as f32 - 0.001;
    p.x = p.x.clamp(min_x, max_x);
    p.y = p.y.clamp(min_y, max_y);
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        // `resumed` can fire more than once on some platforms; only bootstrap once.
        if !self.windows.is_empty() {
            return;
        }

        // 1. Capture the virtual desktop FIRST, before any winit window
        //    exists. Hidden windows are not normally composited by DWM, but
        //    capturing before any window creation eliminates the possibility
        //    entirely. The capture is a synchronous Win32 BitBlt; it must
        //    happen on the event loop thread. The returned bytes are raw
        //    BGRA — no CPU swizzle — so this call is essentially BitBlt +
        //    GetDIBits + a single Vec allocation. The bundled `monitors`
        //    field is the topology snapshot taken at the same instant.
        let captured = SystemInterop::capture_desktop();

        if captured.monitors.is_empty() {
            error!("no monitors detected; nothing to render to");
            event_loop.exit();
            return;
        }

        // Snapshot the cursor position before any of our windows exist, so
        // each render thread can seed its frame-0 crosshair uniform without
        // ever having to query the OS itself. After the windows are up the
        // main thread keeps every render thread in sync by translating
        // WindowEvent::CursorMoved into RenderMsg::MouseState.
        let initial_mouse = SystemInterop::get_mouse_position();
        let initial_mouse_f =
            ScreenPointF::new(initial_mouse.x as f32, initial_mouse.y as f32);

        // Populate the InputState we drive the zoom + virtual cursor from.
        // The anchor is the real-screen centre of the primary monitor (per
        // Screens.cpp:111-114) — the fixed point we warp the OS cursor to
        // while anchored. Falls back to the first monitor if no primary
        // is flagged, which should never happen but is cheap to guard.
        let primary = captured
            .monitors
            .iter()
            .find(|m| m.is_primary)
            .or_else(|| captured.monitors.first())
            .expect("at least one monitor present");
        let anchor = ScreenPoint::new(
            primary.bounds.min_x() + (primary.bounds.width() / 2),
            primary.bounds.min_y() + (primary.bounds.height() / 2),
        );
        self.input = InputState {
            virtual_cursor: initial_mouse_f,
            zoom: 1.0,
            anchored: false,
            anchor,
            mouse_down: false,
            mouse_down_pt: None,
            mouse_down_dpi: 1.0,
            dragging: false,
            selection: None,
            captured: false,
            hittest: Hittest::Outside,
            drag_mode: None,
            drag_anchor_selection: None,
            panel_state: None,
            panel_monitor_idx: None,
        };
        self.monitor_bounds = captured.monitors.iter().map(|m| m.bounds).collect();
        self.monitor_dpi = captured.monitors.iter().map(|m| m.scale_factor).collect();
        self.vd_bounds = captured.bounds;
        self.monitor_window_ids.clear();

        // Snapshot visible windows for click-to-select. Done after the
        // desktop capture but before our overlay windows are created so
        // our own windows aren't in the enumeration.
        let walker = SystemInterop::snapshot_windows();
        self.input.selection = walker.hit_test(initial_mouse);
        self.walker = Some(walker);

        // 2. Create one hidden, borderless window per monitor.
        let mut created: Vec<(Arc<Window>, f32)> = Vec::with_capacity(captured.monitors.len());
        for (i, m) in captured.monitors.iter().enumerate() {
            let width = m.bounds.size.width.max(1) as u32;
            let height = m.bounds.size.height.max(1) as u32;
            #[allow(unused_mut)]
            let mut attrs = Window::default_attributes()
                .with_title("clowd capture")
                .with_decorations(false)
                .with_resizable(false)
                .with_visible(false)
                .with_transparent(false)
                .with_active(i == 0)
                // .with_window_level(WindowLevel::AlwaysOnTop)
                .with_position(PhysicalPosition::new(m.bounds.origin.x, m.bounds.origin.y))
                .with_inner_size(PhysicalSize::new(width, height));
            // WS_EX_NOREDIRECTIONBITMAP: tells DWM not to create a redirection
            // surface. Required for proper DXGI flip-model swap chain timing.
            #[cfg(windows)]
            {
                attrs = attrs.with_no_redirection_bitmap(true);
            }
            match event_loop.create_window(attrs) {
                Ok(w) => {
                    let w = Arc::new(w);
                    platform::apply_capture_window_tweaks(&w);
                    created.push((w, m.refresh_hz));
                }
                Err(e) => error!("failed to create window for monitor {i}: {e:?}"),
            }
        }

        if created.is_empty() {
            error!("no windows created; exiting");
            event_loop.exit();
            return;
        }

        // 3. Wrap the captured desktop in Arc — shared read-only with
        //    GPU bootstrap (for texture upload) and retained by the main
        //    thread for Copy/Save operations. No copies; each consumer
        //    just bumps the refcount.
        let first_window = created[0].0.clone();
        let captured = Arc::new(captured);
        self.desktop_buffer = Some(captured.clone());

        // 4. Bootstrap a separate wgpu Instance / Adapter / Device /
        //    Queue per window **on the main thread** (winit's window
        //    handle is only available here). Each window gets its own
        //    DX12 command queue so swap chain presents are fully
        //    independent — the prerequisite for Hardware: Independent
        //    Flip on multi-monitor setups.
        let monitors = &captured.monitors;
        let mut gpu_setups: Vec<_> = Vec::with_capacity(created.len());
        for ((w, _hz), m) in created.iter().zip(monitors.iter()) {
            match bootstrap_window_gpu(w.clone(), &captured, m.adapter_id) {
                Ok(pair) => gpu_setups.push(Some(pair)),
                Err(e) => {
                    error!("failed to bootstrap GPU for monitor {:?}: {e:?}", m.bounds);
                    gpu_setups.push(None);
                }
            }
        }

        // 5. Spawn render threads behind a Barrier so the main thread
        //    waits until every swapchain has a valid first frame before
        //    any window is flipped visible.
        let ok_count = gpu_setups.iter().filter(|s| s.is_some()).count();
        if ok_count == 0 {
            error!("no GPU could be bootstrapped; exiting");
            event_loop.exit();
            return;
        }
        let barrier = Arc::new(Barrier::new(ok_count + 1));
        let mut handles: HashMap<WindowId, WindowHandle> = HashMap::with_capacity(ok_count);
        for (((w, hz), m), gpu_setup) in created
            .into_iter()
            .zip(monitors.iter())
            .zip(gpu_setups.into_iter())
        {
            let (gpu, surface) = match gpu_setup {
                Some(pair) => pair,
                None => continue,
            };
            let id = w.id();
            let handle = spawn_render_thread(
                w,
                surface,
                gpu,
                self.settings.clone(),
                m.bounds,
                m.scale_factor,
                hz,
                initial_mouse_f,
                barrier.clone(),
            );
            handles.insert(id, handle);
            self.monitor_window_ids.push(id);
        }

        // 6. Wait until every render thread reports "frame 0 done".
        barrier.wait();

        // 7. Flip every window visible in one pass, then focus the first.
        for handle in handles.values() {
            handle.window.set_visible(true);
        }
        first_window.focus_window();

        self.windows = handles;
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        id: WindowId,
        event: WindowEvent,
    ) {
        // Extract the bits of the window handle we need upfront and
        // drop the `self.windows` borrow immediately. Keeping a live
        // `&WindowHandle` across the match blocks all the panel
        // mutators (`recompute_panel`, `clear_panel`) from running
        // because they need `&mut self`. `Arc<Window>` is cheap to
        // clone and `ScreenRect` is Copy, so this is effectively free.
        let (window, this_monitor_bounds) = match self.windows.get(&id) {
            Some(h) => (h.window.clone(), h.monitor_bounds),
            None => return,
        };
        // Alias to keep the downstream code readable without touching
        // every call site that used `handle.window` and
        // `handle.monitor_bounds`.
        let handle_window = &window;
        let handle_monitor_bounds = this_monitor_bounds;

        match event {
            WindowEvent::CloseRequested => {
                event_loop.exit();
            }
            WindowEvent::KeyboardInput {
                event:
                    KeyEvent {
                        state: ElementState::Pressed,
                        logical_key: Key::Named(NamedKey::Escape),
                        ..
                    },
                ..
            } => {
                event_loop.exit();
            }
            WindowEvent::Resized(new_size) => {
                // Re-borrow `self.windows` briefly. No other mutation is
                // in flight here, so this is safe.
                if let Some(h) = self.windows.get(&id) {
                    h.resize(new_size);
                }
            }
            WindowEvent::CursorMoved { position, .. } => {
                // winit hands us a position in this window's local physical
                // pixels. Reconstruct the OS cursor in virtual-desktop
                // coords so we can compare against the anchor (itself in
                // virtual-desktop coords).
                let bounds = handle_monitor_bounds;
                let os_vd = ScreenPoint::new(
                    bounds.min_x() + position.x.round() as i32,
                    bounds.min_y() + position.y.round() as i32,
                );

                if self.input.anchored {
                    // Feedback-loop guard: our own SetCursorPos(anchor)
                    // below will trigger a CursorMoved event back at the
                    // anchor. Skip it so we don't re-apply a zero delta
                    // and (worse) re-warp mid-frame. Matches
                    // Screens.cpp:IsAnchorPt + DxScreenCapture.cpp:1389.
                    if os_vd == self.input.anchor {
                        return;
                    }
                    let zoom = self.input.zoom;
                    let dx = (os_vd.x - self.input.anchor.x) as f32 / zoom;
                    let dy = (os_vd.y - self.input.anchor.y) as f32 / zoom;
                    self.input.virtual_cursor.x += dx;
                    self.input.virtual_cursor.y += dy;
                    clamp_to_nearest_monitor(
                        &mut self.input.virtual_cursor,
                        &self.monitor_bounds,
                    );
                    SystemInterop::set_mouse_position(self.input.anchor);
                } else {
                    // Unanchored (zoom == 1): the OS cursor is truth. We
                    // still keep `virtual_cursor` updated so a subsequent
                    // zoom-in transition doesn't need a GetCursorPos.
                    self.input.virtual_cursor =
                        ScreenPointF::new(os_vd.x as f32, os_vd.y as f32);
                }

                // Walker hover: when idle (no button held, no finalised
                // selection) ask the window walker for the best capture
                // target under the cursor. The result becomes the
                // pre-highlight selection that a single click finalises.
                if !self.input.mouse_down && !self.input.captured {
                    let pt = ScreenPoint::new(
                        self.input.virtual_cursor.x.round() as i32,
                        self.input.virtual_cursor.y.round() as i32,
                    );
                    self.input.selection = self
                        .walker
                        .as_ref()
                        .and_then(|w| w.hit_test(pt));
                }

                // Drag tracking: if the user is mid-press and hasn't yet
                // finalised a selection, recompute the rounded selection
                // rect against the start point. The drag is "promoted"
                // from pending to active once the rounded width or height
                // exceeds 6 / (dpi * zoom) virtual-desktop pixels — same
                // threshold as DxScreenCapture.cpp:1493-1499. Once active,
                // every cursor move overwrites `selection`. Note that
                // `from_rounded_threshold` returns `None` if the user
                // drags back onto the start pixel — propagating that
                // `None` briefly hides the rect, matching the C++ feel.
                if self.input.mouse_down && !self.input.captured {
                    if let Some(start) = self.input.mouse_down_pt {
                        let psel = ScreenRect::from_rounded_threshold(
                            start.x,
                            start.y,
                            self.input.virtual_cursor.x,
                            self.input.virtual_cursor.y,
                        );
                        if !self.input.dragging {
                            let threshold = 6.0
                                / (self.input.mouse_down_dpi * self.input.zoom);
                            let crossed = psel.map_or(false, |r| {
                                (r.width() as f32) > threshold
                                    || (r.height() as f32) > threshold
                            });
                            if crossed {
                                self.input.dragging = true;
                            }
                        }
                        if self.input.dragging {
                            self.input.selection = psel;
                        }
                    }
                }

                // Captured-state input. Two distinct sub-modes:
                //   * No active drag → hit-test the cursor against
                //     the selection and swap the OS cursor icon.
                //   * Active drag → apply the move/resize math
                //     against the snapshotted anchor selection,
                //     soft-clamping to virtual desktop bounds.
                // Mirrors `FrameUpdateHitTest`/`FrameSetCursor` and
                // the WM_MOUSEMOVE handlers at
                // DxScreenCapture.cpp:1402-1490/1670/1732.
                if self.input.captured {
                    if let (Some(mode), Some(anchor), Some(start)) = (
                        self.input.drag_mode,
                        self.input.drag_anchor_selection,
                        self.input.mouse_down_pt,
                    ) {
                        let cur_x = self.input.virtual_cursor.x.floor() as i32;
                        let cur_y = self.input.virtual_cursor.y.floor() as i32;
                        let new_sel = match mode {
                            DragMode::Move => {
                                // No soft-clamp: the logical rect
                                // follows the cursor freely via
                                // `anchor + delta`, and the *displayed*
                                // rect is the intersection with vd
                                // bounds. Dragging the selection
                                // fully off-screen produces `None`
                                // and makes the selection disappear;
                                // dragging back brings it back.
                                let dx = (self.input.virtual_cursor.x
                                    - start.x)
                                    .round()
                                    as i32;
                                let dy = (self.input.virtual_cursor.y
                                    - start.y)
                                    .round()
                                    as i32;
                                move_and_crop(
                                    anchor,
                                    dx,
                                    dy,
                                    self.vd_bounds,
                                )
                            }
                            DragMode::Resize(handle) => Some(resize_with_clamp(
                                anchor,
                                handle,
                                cur_x,
                                cur_y,
                                self.vd_bounds,
                            )),
                        };
                        self.input.selection = new_sel;
                        // Selection geometry changed during a drag
                        // after capture → re-layout the panel so it
                        // rides with the selection and the area
                        // indicator text updates.
                        self.recompute_panel();
                    } else if let Some(sel) = self.input.selection {
                        // Hover hit-test only when no drag is active.
                        // The cursor is determined by the hover state;
                        // during a drag the OS cursor stays whatever
                        // it was at mouse-down (matches Windows native
                        // resize-drag feel).
                        let dpi = dpi_at_point(
                            self.input.virtual_cursor,
                            &self.monitor_bounds,
                            &self.monitor_dpi,
                        );
                        let ht = hit_test(self.input.virtual_cursor, sel, dpi);
                        if ht != self.input.hittest {
                            self.input.hittest = ht;
                            handle_window.set_cursor(ht.cursor());
                        }

                        // Panel hover tracking: if the cursor is over
                        // one of the panel buttons, swap the cursor to
                        // a click pointer and update the panel state
                        // so the bake backend redraws the hovered
                        // button with the 30% white overlay. A hover
                        // change always rebroadcasts the panel state.
                        let new_hover =
                            self.input.panel_state.as_ref().and_then(|p| {
                                p.layout.hit_test(
                                    self.input.virtual_cursor.x,
                                    self.input.virtual_cursor.y,
                                )
                            });
                        let old_hover =
                            self.input.panel_state.as_ref().and_then(|p| p.hover_idx);
                        if new_hover != old_hover {
                            if let Some(p) = self.input.panel_state.as_mut() {
                                p.hover_idx = new_hover;
                            }
                            // Rebroadcast the panel state to the owning
                            // monitor only (other monitors already
                            // have their panel cleared).
                            if let (Some(idx), Some(state)) =
                                (self.input.panel_monitor_idx, self.input.panel_state.clone())
                            {
                                if let Some(wid) = self.monitor_window_ids.get(idx) {
                                    if let Some(h) = self.windows.get(wid) {
                                        h.update_panel_state(Some(state));
                                    }
                                }
                            }
                            if new_hover.is_some() {
                                handle_window.set_cursor(CursorIcon::Pointer);
                            } else {
                                // Fall back to whichever cursor the
                                // selection hit-test wanted.
                                handle_window.set_cursor(self.input.hittest.cursor());
                            }
                        }
                    }
                }

                self.broadcast_mouse_state();
            }
            WindowEvent::MouseInput {
                state,
                button: MouseButton::Left,
                ..
            } => {
                match state {
                    ElementState::Pressed => {
                        if self.input.captured {
                            // Panel click: if the cursor is over a
                            // button, dispatch the action and exit.
                            // Matches the v1 scope — real handlers
                            // for save/copy/upload/etc. are deferred.
                            if let Some(idx) =
                                self.input.panel_state.as_ref().and_then(|p| {
                                    p.layout.hit_test(
                                        self.input.virtual_cursor.x,
                                        self.input.virtual_cursor.y,
                                    )
                                })
                            {
                                let def = panel::button_defs()[idx];
                                info!("panel click: {:?}", def.action);
                                match def.action {
                                    panel::ButtonAction::Copy => {
                                        match self.handle_copy() {
                                            ActionResult::Success => {
                                                event_loop.exit();
                                            }
                                            ActionResult::Cancelled => {}
                                            ActionResult::Failed(msg) => {
                                                self.hide_all_windows();
                                                if SystemInterop::show_error_retry_cancel("Copy Failed", &msg) {
                                                    // Retry: just re-show windows, user can try again
                                                    self.show_all_windows();
                                                } else {
                                                    // Cancel: exit app
                                                    event_loop.exit();
                                                }
                                            }
                                        }
                                    }
                                    panel::ButtonAction::Save => {
                                        match self.handle_save(handle_window) {
                                            ActionResult::Success => {
                                                event_loop.exit();
                                            }
                                            ActionResult::Cancelled => {}
                                            ActionResult::Failed(msg) => {
                                                self.hide_all_windows();
                                                if SystemInterop::show_error_retry_cancel("Save Failed", &msg) {
                                                    // Retry: just re-show windows, user can try again
                                                    self.show_all_windows();
                                                } else {
                                                    // Cancel: exit app
                                                    event_loop.exit();
                                                }
                                            }
                                        }
                                    }
                                    panel::ButtonAction::Reset => {
                                        self.handle_reset(handle_window);
                                    }
                                    panel::ButtonAction::Exit => {
                                        event_loop.exit();
                                    }
                                    // Upload, Edit, Video not yet implemented
                                    _ => {
                                        info!("action {:?} not yet implemented", def.action);
                                    }
                                }
                                return;
                            }
                            // Captured: this mouse-down enters either
                            // Move (clicked inside the rect) or
                            // Resize (clicked on a handle). Anywhere
                            // else is a no-op — clicking outside the
                            // selection doesn't deselect in v1.
                            let drag_mode = match self.input.hittest {
                                Hittest::Inside => Some(DragMode::Move),
                                Hittest::Outside => None,
                                handle => Some(DragMode::Resize(handle)),
                            };
                            if drag_mode.is_some() {
                                self.input.mouse_down = true;
                                self.input.mouse_down_pt =
                                    Some(self.input.virtual_cursor);
                                self.input.drag_mode = drag_mode;
                                self.input.drag_anchor_selection =
                                    self.input.selection;
                            }
                            return;
                        }
                        // Pre-capture: starting a fresh draw-selection
                        // gesture. The pending-drag state is promoted
                        // to active dragging by the threshold check in
                        // CursorMoved.
                        self.input.mouse_down = true;
                        self.input.mouse_down_pt = Some(self.input.virtual_cursor);
                        self.input.mouse_down_dpi = dpi_at_point(
                            self.input.virtual_cursor,
                            &self.monitor_bounds,
                            &self.monitor_dpi,
                        );
                        self.input.dragging = false;
                        // Selection itself is left alone here so a single
                        // click on a walker-highlighted window keeps the
                        // walker rect visible during the pending-drag
                        // state. If the drag threshold is crossed the
                        // selection switches to freeform; if the user
                        // releases without crossing it the walker rect
                        // is finalised as-is.
                    }
                    ElementState::Released => {
                        // Finalise if we have a selection, haven't
                        // captured yet, and a real press was tracked.
                        // The `mouse_down` guard prevents panel button
                        // clicks (which `return` before setting
                        // `mouse_down`) from accidentally finalising
                        // the walker rect that reset just restored.
                        let finalising = self.input.mouse_down
                            && !self.input.captured
                            && self.input.selection.is_some();
                        let was_move_drag = matches!(
                            self.input.drag_mode,
                            Some(DragMode::Move),
                        );
                        self.input.mouse_down = false;
                        self.input.mouse_down_pt = None;
                        self.input.dragging = false;
                        self.input.drag_mode = None;
                        self.input.drag_anchor_selection = None;
                        // A Move drag that ended with the selection
                        // fully off-screen means the user effectively
                        // cancelled the selection by shoving it into
                        // the void — un-capture so the wheel handler
                        // re-enables zoom and the next mouse-down
                        // starts a fresh draw instead of an impossible
                        // move/resize. Mirrors the C++ `rEmpty`
                        // branch at DxScreenCapture.cpp:1820-1830.
                        if was_move_drag
                            && self.input.captured
                            && self.input.selection.is_none()
                        {
                            self.input.captured = false;
                            self.input.hittest = Hittest::Outside;
                            handle_window.set_cursor(CursorIcon::Default);
                            // Selection dragged off-screen: tear down
                            // the panel so we don't leave a ghost on
                            // the previous monitor.
                            self.clear_panel();
                        }
                        if finalising {
                            self.input.captured = true;
                            // Snap zoom back to 1 and tear down the
                            // anchor when a selection is finalised
                            // (matches FrameMakeSelection at
                            // DxScreenCapture.cpp:1816). The unanchor
                            // sequence is the mirror of the wheel
                            // handler's MouseAnchorStop branch below:
                            // warp the OS cursor to the virtual cursor
                            // so there's no visual jump.
                            if self.input.anchored {
                                self.input.anchored = false;
                                let restore = ScreenPoint::new(
                                    self.input.virtual_cursor.x.floor() as i32,
                                    self.input.virtual_cursor.y.floor() as i32,
                                );
                                SystemInterop::set_mouse_position(restore);
                            }
                            self.input.zoom = 1.0;
                            // Immediately hit-test against the just-
                            // finalised selection so the cursor flips
                            // to the right resize/move icon without
                            // having to wiggle the mouse.
                            if let Some(sel) = self.input.selection {
                                let dpi = dpi_at_point(
                                    self.input.virtual_cursor,
                                    &self.monitor_bounds,
                                    &self.monitor_dpi,
                                );
                                let ht = hit_test(
                                    self.input.virtual_cursor,
                                    sel,
                                    dpi,
                                );
                                self.input.hittest = ht;
                                handle_window.set_cursor(ht.cursor());
                            }
                            // Compute the panel layout for the new
                            // selection and broadcast it to the
                            // render thread of the monitor that owns
                            // the panel.
                            self.recompute_panel();
                        } else if self.input.captured
                            && self.input.selection.is_some()
                        {
                            // A move/resize drag just ended with the
                            // selection still alive — update the panel
                            // layout for the settled selection.
                            self.recompute_panel();
                        }
                        self.broadcast_mouse_state();
                    }
                }
            }
            WindowEvent::MouseWheel { delta, .. } => {
                // After a selection has been finalised the wheel is a
                // no-op — matches DxScreenCapture.cpp:1527's `if
                // (data.captured) return 0;`. While a drag is *in
                // progress* the wheel is allowed: rough-drag, zoom in
                // for precision, refine is a deliberate workflow.
                // The selection lives in virtual-desktop coords, so
                // zooming during a drag re-renders it under the new
                // transform without moving the underlying selected
                // pixels — and the next CursorMoved naturally
                // refreshes the rounded rect against the new zoom.
                if self.input.captured {
                    return;
                }
                // Normalise the two winit delta variants into a single
                // scalar "step" whose sign is all we care about for a
                // coarse ×2/÷2 zoom. LineDelta is the desktop-mouse case;
                // PixelDelta comes from touchpads in physical pixels and
                // needs taming so one scroll gesture isn't twenty zoom
                // steps.
                let step = match delta {
                    MouseScrollDelta::LineDelta(_, y) => y,
                    MouseScrollDelta::PixelDelta(p) => (p.y / 50.0) as f32,
                };
                if step == 0.0 {
                    return;
                }

                let new_zoom = if step > 0.0 {
                    self.input.zoom * ZOOM_STEP
                } else {
                    self.input.zoom / ZOOM_STEP
                };
                let new_zoom = new_zoom.clamp(ZOOM_MIN, ZOOM_MAX);
                if (new_zoom - self.input.zoom).abs() < f32::EPSILON {
                    return;
                }

                let was_anchored = self.input.anchored;
                let will_anchor = new_zoom > 1.0;

                if !was_anchored && will_anchor {
                    // MouseAnchorStart (Screens.cpp:130-137): pin the OS
                    // cursor to the anchor. `virtual_cursor` already tracks
                    // the current cursor position, so the zoom appears
                    // centered on wherever the user was pointing.
                    self.input.anchored = true;
                    SystemInterop::set_mouse_position(self.input.anchor);
                } else if was_anchored && !will_anchor {
                    // MouseAnchorStop (Screens.cpp:161-167): un-pin by
                    // moving the real OS cursor to where the virtual
                    // cursor currently sits — no visual jump because the
                    // virtual cursor was what the user saw.
                    self.input.anchored = false;
                    let restore = ScreenPoint::new(
                        self.input.virtual_cursor.x.floor() as i32,
                        self.input.virtual_cursor.y.floor() as i32,
                    );
                    SystemInterop::set_mouse_position(restore);
                }

                self.input.zoom = new_zoom;
                self.broadcast_mouse_state();
            }
            _ => {}
        }
    }
}
