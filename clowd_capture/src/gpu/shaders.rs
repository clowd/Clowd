use std::borrow::Cow;
use std::sync::Arc;

/// Install a non-fatal uncaptured-error handler on `device`. Must be called
/// once, right after the device is created and before any shader is loaded.
/// Without it, wgpu's default handler panics on the first shader/pipeline
/// validation error, which killed the render worker thread and left the
/// overlay invisible while the event loop spun forever.
pub fn install_error_handler(device: &wgpu::Device) {
    device.on_uncaptured_error(Arc::new(|err| {
        log::error!("wgpu uncaptured error (non-fatal): {err}");
    }));
}

fn wgsl(device: &wgpu::Device, label: &str, wgsl_source: &'static str) -> wgpu::ShaderModule {
    device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some(label),
        source: wgpu::ShaderSource::Wgsl(Cow::Borrowed(wgsl_source)),
    })
}

pub fn desktop(device: &wgpu::Device) -> wgpu::ShaderModule {
    wgsl(device, "desktop", include_str!("../../shaders/desktop.wgsl"))
}

pub fn peek(device: &wgpu::Device) -> wgpu::ShaderModule {
    wgsl(device, "peek", include_str!("../../shaders/peek.wgsl"))
}

pub fn ui_rect(device: &wgpu::Device) -> wgpu::ShaderModule {
    wgsl(device, "ui_rect", include_str!("../../shaders/ui_rect.wgsl"))
}

pub fn ui_icon(device: &wgpu::Device) -> wgpu::ShaderModule {
    wgsl(device, "ui_icon", include_str!("../../shaders/ui_icon.wgsl"))
}

pub fn ui_lift(device: &wgpu::Device) -> wgpu::ShaderModule {
    wgsl(device, "ui_lift", include_str!("../../shaders/ui_lift.wgsl"))
}
