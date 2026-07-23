use std::borrow::Cow;

pub struct ShaderPair {
    #[cfg(windows)]
    vs: wgpu::ShaderModule,
    #[cfg(windows)]
    fs: wgpu::ShaderModule,
    #[cfg(target_os = "macos")]
    module: wgpu::ShaderModule,
}

impl ShaderPair {
    pub fn vs(&self) -> &wgpu::ShaderModule {
        #[cfg(windows)]
        return &self.vs;
        #[cfg(target_os = "macos")]
        return &self.module;
    }

    pub fn fs(&self) -> &wgpu::ShaderModule {
        #[cfg(windows)]
        return &self.fs;
        #[cfg(target_os = "macos")]
        return &self.module;
    }
}

unsafe fn passthrough(device: &wgpu::Device, label: &str, bytes: &'static [u8]) -> wgpu::ShaderModule {
    unsafe {
        device.create_shader_module_passthrough(wgpu::ShaderModuleDescriptorPassthrough {
            label: Some(label),
            #[cfg(windows)]
            dxil: Some(Cow::Borrowed(bytes)),
            #[cfg(target_os = "macos")]
            metallib: Some(Cow::Borrowed(bytes)),
            ..Default::default()
        })
    }
}

#[cfg(windows)]
fn load(device: &wgpu::Device, label: &str, vs_bytes: &'static [u8], fs_bytes: &'static [u8]) -> ShaderPair {
    unsafe {
        ShaderPair {
            vs: passthrough(device, &format!("{label} VS"), vs_bytes),
            fs: passthrough(device, &format!("{label} FS"), fs_bytes),
        }
    }
}

#[cfg(target_os = "macos")]
fn load(device: &wgpu::Device, label: &str, metallib_bytes: &'static [u8]) -> ShaderPair {
    ShaderPair {
        module: unsafe { passthrough(device, label, metallib_bytes) },
    }
}

pub fn desktop(device: &wgpu::Device) -> ShaderPair {
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/desktop_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/desktop_ps.dxbc"));
        load(device, "desktop", VS, FS)
    }
    #[cfg(target_os = "macos")]
    {
        const METALLIB: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/desktop.metallib"));
        load(device, "desktop", METALLIB)
    }
}

pub fn peek(device: &wgpu::Device) -> ShaderPair {
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/peek_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/peek_ps.dxbc"));
        load(device, "peek", VS, FS)
    }
    #[cfg(target_os = "macos")]
    {
        const METALLIB: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/peek.metallib"));
        load(device, "peek", METALLIB)
    }
}

pub fn ui_rect(device: &wgpu::Device) -> ShaderPair {
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_rect_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_rect_ps.dxbc"));
        load(device, "ui_rect", VS, FS)
    }
    #[cfg(target_os = "macos")]
    {
        const METALLIB: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_rect.metallib"));
        load(device, "ui_rect", METALLIB)
    }
}

pub fn ui_icon(device: &wgpu::Device) -> ShaderPair {
    #[cfg(windows)]
    {
        const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_icon_vs.dxbc"));
        const FS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_icon_ps.dxbc"));
        load(device, "ui_icon", VS, FS)
    }
    #[cfg(target_os = "macos")]
    {
        const METALLIB: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ui_icon.metallib"));
        load(device, "ui_icon", METALLIB)
    }
}
