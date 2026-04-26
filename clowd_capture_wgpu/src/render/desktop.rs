use crate::geometry::{RectExt, ScreenPointF, ScreenRect, WindowPoint};
use crate::gpu::desktop::WindowUniforms;

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
    pub elapsed: f32,
    pub surface_size: (u32, u32),
}

impl SnapshotState {
    pub fn update_uniforms(&mut self, queue: &wgpu::Queue, frame: &FrameState) {
        let FrameState {
            monitor_bounds,
            mouse_pos,
            zoom,
            selection,
            captured,
            overlays_visible,
            elapsed,
            surface_size,
        } = *frame;

        if !overlays_visible {
            self.uniforms.params[0] = 0.0;
            let local = WindowPoint::new(
                mouse_pos.x - monitor_bounds.min_x() as f32,
                mouse_pos.y - monitor_bounds.min_y() as f32,
            );
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
            queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&self.uniforms));
            return;
        }

        let fade = {
            let t = (elapsed / FADE_DURATION_SECS).clamp(0.0, 1.0);
            let inv = 1.0 - t;
            1.0 - inv * inv * inv * inv
        };
        self.uniforms.params[0] = fade;

        let local = WindowPoint::new(
            mouse_pos.x - monitor_bounds.min_x() as f32,
            mouse_pos.y - monitor_bounds.min_y() as f32,
        );
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
            let local_cursor = WindowPoint::new(cx - monitor_bounds.min_x() as f32, cy - monitor_bounds.min_y() as f32);
            let to_local =
                |vd_x: f32, vd_y: f32| -> (f32, f32) { ((vd_x - cx) * zoom + local_cursor.x, (vd_y - cy) * zoom + local_cursor.y) };
            let (l, t) = to_local(sel.left() as f32, sel.top() as f32);
            let (r, b) = to_local(sel.right() as f32, sel.bottom() as f32);
            self.uniforms.selection_rect = [l, t, r, b];
        } else {
            self.uniforms.selection_rect = [0.0, 0.0, -1.0, -1.0];
        }

        self.uniforms.selection_params[0] = elapsed;
        self.uniforms.selection_params[1] = if captured { 1.0 } else { 0.0 };
        self.uniforms.selection_params[2] = zoom;

        queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&self.uniforms));
    }
}
