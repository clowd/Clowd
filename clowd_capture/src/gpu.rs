#![allow(dead_code)]

use anyhow::Result;
use std::{num::NonZeroUsize, path::PathBuf};
use vello::{
    wgpu::{
        self, Adapter, Backends, Device, Instance, Limits, Queue, Surface, SurfaceConfiguration, SurfaceError, SurfaceTarget,
        SurfaceTexture, Texture, TextureFormat, TextureView,
    },
    AaConfig, Renderer, RendererOptions, Scene,
};

pub struct RenderContext {
    instance: Instance,
}

pub struct DeviceHandle {
    pub adapter: Adapter,
    pub device: Device,
    pub queue: Queue,
}

pub struct WindowSurface<'w> {
    surface: Surface<'w>,
    config: SurfaceConfiguration,
    format: TextureFormat,
    device: DeviceHandle,
}

fn default_threads() -> usize {
    #[cfg(target_os = "macos")]
    return 1;
    #[cfg(not(target_os = "macos"))]
    return 0;
}

impl WindowSurface<'_> {
    pub fn begin_draw(&self) -> Result<SurfaceTexture, SurfaceError> {
        self.surface.get_current_texture()
    }

    pub fn upload_image(&self, image: &image::RgbaImage) -> Texture {
        let image_width = image.width();
        let image_height = image.height();
        let texture = self
            .device
            .device
            .create_texture(&wgpu::TextureDescriptor {
                label: Some("Image Texture"),
                size: wgpu::Extent3d {
                    width: image_width,
                    height: image_height,
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format: wgpu::TextureFormat::Rgba8UnormSrgb, // match your image data format if possible
                usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST, // required to upload data
                view_formats: &[],
            });

        let data = image.as_raw(); // This is &[u8] containing RGBA data
        let texture_size = wgpu::Extent3d {
            width: image_width,
            height: image_height,
            depth_or_array_layers: 1,
        };

        self.device.queue.write_texture(
            wgpu::ImageCopyTexture {
                texture: &texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            data,
            wgpu::ImageDataLayout {
                offset: 0,
                bytes_per_row: Some(4 * image_width), // 4 bytes per pixel * width
                rows_per_image: None,
            },
            texture_size,
        );
        texture
    }

    pub fn submit(&self) {
        self.device.queue.submit(Vec::new());
    }

    pub fn draw_scene(&self, scene: &Scene, texture: &TextureView, renderer: &mut Renderer) -> Result<()> {
        renderer.render_to_texture(
            &self.device.device,
            &self.device.queue,
            &scene,
            &texture,
            &vello::RenderParams {
                base_color: vello::peniko::Color::TRANSPARENT, // Background color
                width: self.config.width,
                height: self.config.height,
                antialiasing_method: AaConfig::Area,
            },
        )?;
        Ok(())
    }

    pub fn get_device(&self) -> &DeviceHandle {
        &self.device
    }

    pub fn end_draw(&self, texture: SurfaceTexture) -> Result<()> {
        // renderer.render_to_surface(
        //     &self.device.device,
        //     &self.device.queue,
        //     scene,
        //     &texture,
        // &vello::RenderParams {
        //     base_color: vello::peniko::Color::BLACK, // Background color
        //     width: self.config.width,
        //     height: self.config.height,
        //     antialiasing_method: AaConfig::Area,
        // },
        // )?;

        texture.present();

        self.device.device.poll(wgpu::Maintain::Poll);
        Ok(())
    }

    pub fn create_renderer(&mut self) -> Renderer {
        Renderer::new(
            &self.device.device,
            RendererOptions {
                surface_format: Some(self.format),
                use_cpu: false,
                antialiasing_support: vello::AaSupport {
                    area: true,
                    msaa8: false,
                    msaa16: false,
                },
                num_init_threads: NonZeroUsize::new(default_threads()),
            },
        )
        .expect("Couldn't create renderer")
    }

    pub fn resize_surface(&mut self, width: u32, height: u32) {
        self.config.width = width;
        self.config.height = height;
        self.configure_surface();
    }

    pub fn set_present_mode(&mut self, present_mode: wgpu::PresentMode) {
        self.config.present_mode = present_mode;
        self.configure_surface();
    }

    fn configure_surface(&self) {
        self.surface
            .configure(&self.device.device, &self.config);
    }
}

impl RenderContext {
    pub fn new(backends: Option<Backends>, dxil_path: Option<PathBuf>, dxc_path: Option<PathBuf>) -> Self {
        let instance = Instance::new(wgpu::InstanceDescriptor {
            backends: backends.unwrap_or(wgpu::Backends::PRIMARY),
            dx12_shader_compiler: wgpu::Dx12Compiler::Dxc {
                dxil_path,
                dxc_path,
            },
            ..Default::default()
        });
        Self {
            instance: instance,
        }
    }

    pub fn create_surface<'w>(
        &mut self,
        window: impl Into<SurfaceTarget<'w>>,
        width: u32,
        height: u32,
        present_mode: wgpu::PresentMode,
    ) -> Result<WindowSurface<'w>> {
        let surface_future = self.create_surface_async(window, width, height, present_mode);
        Ok(pollster::block_on(surface_future)?)
    }

    pub async fn create_surface_async<'w>(
        &mut self,
        window: impl Into<SurfaceTarget<'w>>,
        width: u32,
        height: u32,
        present_mode: wgpu::PresentMode,
    ) -> Result<WindowSurface<'w>> {
        let surface = self
            .create_render_surface_async(self.instance.create_surface(window.into())?, width, height, present_mode)
            .await?;
        // self.get_or_create_renderer(&surface);
        Ok(surface)
    }

    async fn create_render_surface_async<'w>(
        &mut self,
        surface: Surface<'w>,
        width: u32,
        height: u32,
        present_mode: wgpu::PresentMode,
    ) -> Result<WindowSurface<'w>> {
        // let dev_id = self
        //     .device(Some(&surface))
        //     .await
        //     .ok_or(anyhow!("Error::NoCompatibleDevice"))?;

        // let devices = self.devices;
        let device_handle = self
            .new_device(Some(&surface))
            .await
            .ok_or(anyhow!("Error::NoCompatibleDevice"))?;
        let capabilities = surface.get_capabilities(&device_handle.adapter);
        let format = capabilities
            .formats
            .into_iter()
            .find(|it| matches!(it, TextureFormat::Rgba8Unorm | TextureFormat::Bgra8Unorm))
            .ok_or(anyhow!("Error::UnsupportedSurfaceFormat"))?;

        let config = SurfaceConfiguration {
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            format,
            width,
            height,
            present_mode,
            desired_maximum_frame_latency: 1,
            alpha_mode: wgpu::CompositeAlphaMode::Auto,
            view_formats: vec![],
        };
        let surface = WindowSurface {
            surface,
            config,
            format,
            device: device_handle,
        };
        surface.configure_surface();
        Ok(surface)
    }

    // async fn device(&mut self, compatible_surface: Option<&Surface<'_>>) -> Option<usize> {
    //     let compatible = {
    //         let devices = self.devices.clone();
    //         let devices = devices.read().unwrap();
    //         match compatible_surface {
    //             Some(s) => devices
    //                 .iter()
    //                 .enumerate()
    //                 .find(|(_, d)| d.adapter.is_surface_supported(s))
    //                 .map(|(i, _)| i),
    //             None => (!devices.is_empty()).then_some(0),
    //         }
    //     };
    //     if compatible.is_none() {
    //         return self.new_device(compatible_surface).await;
    //     }
    //     compatible
    // }

    async fn new_device(&mut self, compatible_surface: Option<&Surface<'_>>) -> Option<DeviceHandle> {
        let adapter = wgpu::util::initialize_adapter_from_env_or_default(&self.instance, compatible_surface).await?;
        let features = adapter.features();
        let limits = Limits::default();
        let maybe_features = wgpu::Features::CLEAR_TEXTURE;

        let (device, queue) = adapter
            .request_device(
                &wgpu::DeviceDescriptor {
                    label: None,
                    required_features: features & maybe_features,
                    required_limits: limits,
                    memory_hints: Default::default(),
                },
                None,
            )
            .await
            .ok()?;

        let device_handle = DeviceHandle {
            adapter,
            device,
            queue,
        };

        Some(device_handle)

        // let mut devices = self.devices.write().unwrap();
        // devices.push(device_handle);
        // Some(devices.len() - 1)
    }
}
