use crate::gpu::desktop::{CursorTextures, WindowUniforms};
use clowd_rust_core::geometry::{screen_to_window, RectExt, ScreenPointF, ScreenRect};

/// Duration of the colour to grayscale fade after the window first becomes visible.
const FADE_DURATION_SECS: f32 = 0.3;

pub(crate) struct SnapshotState {
    pub ubo: wgpu::Buffer,
    pub bind_group: wgpu::BindGroup,
    pub uniforms: WindowUniforms,
    pub base_uv_offset_scale: [f32; 4],
}

pub(crate) struct FrameState {
    pub monitor_bounds: ScreenRect,
    pub mouse_pos: ScreenPointF,
    pub zoom: f32,
    pub selection: Option<ScreenRect>,
    pub captured: bool,
    pub overlays_visible: bool,
    pub cursor_overlay_visible: bool,
    /// Mirrors [`crate::ui::shared::UiSharedState::scroll_pick_mode`].
    /// Suppresses the resize handles (see `desktop.wgsl`) and the frozen
    /// cursor composited from the snapshot.
    pub scroll_pick_mode: bool,
    pub elapsed: f32,
    pub surface_size: (u32, u32),
}

impl SnapshotState {
    pub fn update_uniforms(&mut self, queue: &wgpu::Queue, frame: &FrameState, cursor_textures: Option<&CursorTextures>) {
        let FrameState {
            monitor_bounds,
            mouse_pos,
            zoom,
            selection,
            captured,
            overlays_visible,
            cursor_overlay_visible,
            scroll_pick_mode,
            elapsed,
            surface_size,
        } = *frame;

        self.uniforms.selection_params[3] = if scroll_pick_mode { 1.0 } else { 0.0 };

        // The picker draws its own reticle at the live cursor. The
        // snapshot's frozen cursor sits wherever the pointer happened to
        // be when the screenshot was taken, so it reads as a second,
        // stuck pointer right where the user is aiming — hide it for the
        // duration whatever the M toggle says. Display only: the frozen
        // cursor is not part of what the scroll driver captures, and the
        // user's setting is untouched when they back out.
        let show_frozen_cursor = cursor_overlay_visible && !scroll_pick_mode;

        if !overlays_visible {
            self.uniforms.params[0] = 0.0;
            let local = screen_to_window(monitor_bounds, mouse_pos);
            self.uniforms.params[1] = -1.0;
            self.uniforms.params[2] = -1.0;
            if zoom <= 1.0 {
                self.uniforms.uv_offset_scale = self.base_uv_offset_scale;
            } else {
                let w = surface_size.0 as f32;
                let h = surface_size.1 as f32;
                let cu = local.x / w;
                let cv = local.y / h;
                let k = 1.0 - 1.0 / zoom;
                let base = self.base_uv_offset_scale;
                self.uniforms.uv_offset_scale = [
                    base[0] + base[2] * cu * k,
                    base[1] + base[3] * cv * k,
                    base[2] / zoom,
                    base[3] / zoom,
                ];
            }
            self.uniforms.selection_rect = [0.0, 0.0, -1.0, -1.0];
            self.uniforms.selection_params[0] = elapsed;
            self.uniforms.selection_params[1] = 0.0;
            self.uniforms.selection_params[2] = zoom;
            self.set_cursor_uniforms(cursor_textures, show_frozen_cursor, monitor_bounds, mouse_pos, zoom);
            queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&self.uniforms));
            return;
        }

        let fade = {
            let t = (elapsed / FADE_DURATION_SECS).clamp(0.0, 1.0);
            let inv = 1.0 - t;
            1.0 - inv * inv * inv * inv
        };
        self.uniforms.params[0] = fade;

        let local = screen_to_window(monitor_bounds, mouse_pos);
        self.uniforms.params[1] = local.x;
        self.uniforms.params[2] = local.y;

        if zoom <= 1.0 {
            self.uniforms.uv_offset_scale = self.base_uv_offset_scale;
        } else {
            let w = surface_size.0 as f32;
            let h = surface_size.1 as f32;
            let cu = local.x / w;
            let cv = local.y / h;
            let k = 1.0 - 1.0 / zoom;
            let base = self.base_uv_offset_scale;
            self.uniforms.uv_offset_scale = [
                base[0] + base[2] * cu * k,
                base[1] + base[3] * cv * k,
                base[2] / zoom,
                base[3] / zoom,
            ];
        }

        if let Some(sel) = selection {
            let cx = mouse_pos.x;
            let cy = mouse_pos.y;
            let local_cursor = screen_to_window(monitor_bounds, mouse_pos);
            let sel_f = sel.to_f32();
            let to_local =
                |vd_x: f32, vd_y: f32| -> (f32, f32) { ((vd_x - cx) * zoom + local_cursor.x, (vd_y - cy) * zoom + local_cursor.y) };
            let (l, t) = to_local(sel_f.left(), sel_f.top());
            let (r, b) = to_local(sel_f.right(), sel_f.bottom());
            self.uniforms.selection_rect = [l, t, r, b];
        } else {
            self.uniforms.selection_rect = [0.0, 0.0, -1.0, -1.0];
        }

        self.uniforms.selection_params[0] = elapsed;
        self.uniforms.selection_params[1] = if captured { 1.0 } else { 0.0 };
        self.uniforms.selection_params[2] = zoom;
        self.set_cursor_uniforms(cursor_textures, show_frozen_cursor, monitor_bounds, mouse_pos, zoom);

        queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&self.uniforms));
    }

    fn set_cursor_uniforms(
        &mut self,
        cursor_textures: Option<&CursorTextures>,
        visible: bool,
        monitor_bounds: ScreenRect,
        mouse_pos: ScreenPointF,
        zoom: f32,
    ) {
        let ct = match cursor_textures {
            Some(ct) if visible && ct.visible => ct,
            _ => {
                self.uniforms.cursor_rect = [0.0, 0.0, -1.0, -1.0];
                self.uniforms.cursor_params = [0.0, 0.0, 0.0, 0.0];
                return;
            }
        };

        let vd_left = (ct.position.x - ct.hotspot_x) as f32;
        let vd_top = (ct.position.y - ct.hotspot_y) as f32;
        let vd_right = vd_left + ct.width as f32;
        let vd_bottom = vd_top + ct.height as f32;

        let cx = mouse_pos.x;
        let cy = mouse_pos.y;
        let local_cursor = screen_to_window(monitor_bounds, mouse_pos);

        let to_local = |vd_x: f32, vd_y: f32| -> (f32, f32) { ((vd_x - cx) * zoom + local_cursor.x, (vd_y - cy) * zoom + local_cursor.y) };

        let (l, t) = to_local(vd_left, vd_top);
        let (r, b) = to_local(vd_right, vd_bottom);
        self.uniforms.cursor_rect = [l, t, r, b];
        self.uniforms.cursor_params = [ct.cursor_type as f32, 0.0, 0.0, 0.0];
    }
}
