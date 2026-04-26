use std::sync::Arc;

use crate::system::CapturedDesktop;

#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct WindowUniforms {
    pub uv_offset_scale: [f32; 4],
    pub params: [f32; 4],
    pub accent_color: [f32; 4],
    pub selection_rect: [f32; 4],
    pub selection_params: [f32; 4],
}

pub const WINDOW_UNIFORMS_SIZE: u64 = std::mem::size_of::<WindowUniforms>() as u64;

pub struct DesktopSnapshot {
    #[allow(dead_code)]
    pub texture: wgpu::Texture,
    pub view: wgpu::TextureView,
    pub sampler: wgpu::Sampler,
    pub bind_group_layout: wgpu::BindGroupLayout,
    pub vdesktop_origin: [f32; 2],
    pub vdesktop_size: [f32; 2],
}

pub fn upload_snapshot(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    captured: &CapturedDesktop,
    bgl: &wgpu::BindGroupLayout,
    sampler: &wgpu::Sampler,
) -> Option<Arc<DesktopSnapshot>> {
    let width = captured.width;
    let height = captured.height;
    let max = device.limits().max_texture_dimension_2d;
    if width > max || height > max {
        error!(
            "virtual desktop {}x{} exceeds max texture dimension {}; skipping snapshot",
            width, height, max
        );
        return None;
    }
    if width == 0 || height == 0 {
        error!("virtual desktop has zero dimension; skipping snapshot");
        return None;
    }

    let size = wgpu::Extent3d {
        width,
        height,
        depth_or_array_layers: 1,
    };
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("desktop snapshot"),
        size,
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Bgra8Unorm,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    queue.write_texture(
        wgpu::TexelCopyTextureInfo {
            texture: &texture,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        &captured.bgra,
        wgpu::TexelCopyBufferLayout {
            offset: 0,
            bytes_per_row: Some(4 * width),
            rows_per_image: Some(height),
        },
        size,
    );
    queue.submit(std::iter::empty());

    let view = texture.create_view(&wgpu::TextureViewDescriptor::default());

    Some(Arc::new(DesktopSnapshot {
        texture,
        view,
        sampler: sampler.clone(),
        bind_group_layout: bgl.clone(),
        vdesktop_origin: [captured.bounds.min_x() as f32, captured.bounds.min_y() as f32],
        vdesktop_size: [captured.bounds.width() as f32, captured.bounds.height() as f32],
    }))
}
