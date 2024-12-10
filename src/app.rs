use std::time::Duration;

use crate::{
    geometry::*,
    render_context::{RenderContext, RenderSurface},
    screenshot::capture_desktop,
};
use anyhow::Result;
use euclid::Transform2D;
use image::{DynamicImage, ImageBuffer};
use mouse_rs::Mouse;
use vello::wgpu::{self, Backends, Texture};
use winit::{
    application::ApplicationHandler,
    event::{StartCause, WindowEvent},
    event_loop::{ActiveEventLoop, ControlFlow, EventLoop, EventLoopProxy},
    monitor::MonitorHandle,
    window::{Window, WindowAttributes, WindowId},
};
use xcap::Window as XCapWindow;

#[derive(Debug, Clone, Copy, PartialEq)]
enum MouseState {
    Up,
    StartSelection(ScreenPointF),
    MakingSelection(ScreenPointF),
    // SizingSelection(HitTest),
    MovingSelection(ScreenRect, ScreenPointF),
}

enum UserEvent {
    None,
}

struct App<'s> {
    render_context: RenderContext,
    event_proxy: EventLoopProxy<UserEvent>,
    model: Option<Model>,
    present_mode: wgpu::PresentMode,
    renderers: Vec<RendererInfo<'s>>,
}

struct Model {
    desktop_bounds: ScreenRect,
    desktop_virtual_origin: ScreenPoint,
    desktop_color_texture: wgpu::Texture,
    desktop_gray_texture: wgpu::Texture,
    desktop_color_image: DynamicImage,
    desktop_gray_image: DynamicImage,
    windows: Vec<DesktopWindowInfo>,
    shown: bool,
    debug: bool,
    zoom: f64,
    // accent_light: Rgb,
    // accent_dark: Rgb,
    selection: Option<ScreenRect>,
    captured: bool,
    mouse_pt: ScreenPointF,
    mouse_state: MouseState,
    mouse_anchor_pt: ScreenPoint,
    mouse_anchored: bool,
    mouse: Mouse,
    // button_panel: ui::ButtonPanel,
}

#[allow(dead_code)]
struct RendererInfo<'s> {
    // SAFETY: We MUST drop the surface before the `window`,
    // so the fields must be in this order
    surface: RenderSurface<'s>,
    window: Window,
    monitor_handle: MonitorHandle,
    monitor_bounds: ScreenRect,
    transform: TransformUnit,
    is_primary: bool,
    ready: bool,
    scale_factor: f64,
}

struct DesktopWindowInfo {
    title: String,
    window_bounds: ScreenRect,
    capture: Option<DynamicImage>,
}

impl ApplicationHandler for App<'_> {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.model.is_some() {
            return;
        }

        let sw = simple_stopwatch::Stopwatch::start_new();
        info!("[TIME] Start: {:?}", Duration::from_millis(sw.ms() as u64));

        let mut renderers = Vec::new();
        let mut desktop_windows = Vec::new();

        let monitors = event_loop.available_monitors();
        let windows = XCapWindow::all()?;

        let primary = event_loop.primary_monitor().unwrap();
        let primary_position = primary.position();
        let primary_size = primary.size();
        let primary_bounds = ScreenRect::from_xy_size(
            primary_position.x,
            primary_position.y,
            primary_size.width as i32,
            primary_size.height as i32,
        );

        let mouse_anchor_pt = primary_bounds.center();

        info!("[TIME] Capturing: {:?}", Duration::from_millis(sw.ms() as u64));

        let (desktop_bounds, desktop_color_image, desktop_gray_image) = capture_desktop()?;

        info!("[TIME] Captured: {:?}", Duration::from_millis(sw.ms() as u64));

        let desktop_virtual_origin = desktop_bounds.top_left();
        let vd_transform = Transform2D::<i32, ScreenUnit, ScreenUnit>::identity().then_translate(-desktop_virtual_origin.to_vector());

        let desktop_bounds = vd_transform.outer_transformed_rect(&desktop_bounds);

        info!("[TIME] Windows Captured: {:?}", Duration::from_millis(sw.ms() as u64));

        let desktop_color_texture = Texture::from_image(event_loop, &desktop_color_image);
        let desktop_gray_texture = Texture::from_image(event_loop, &desktop_gray_image);

        info!("[TIME] Textures Loaded: {:?}", Duration::from_millis(sw.ms() as u64));
        info!("[TIME] Done: {:?}", Duration::from_millis(sw.ms() as u64));

        // for window in windows {
        //     let window_bounds = ScreenRect::from_xy_size(window.x(), window.y(), window.width() as i32, window.height() as i32);
        //     let window_bounds = vd_transform.outer_transformed_rect(&window_bounds);

        //     desktop_windows.push(DesktopWindowInfo {
        //         title: window.title().to_string(),
        //         window_bounds,
        //         // capture: window.capture_image().ok(),
        //         capture: None,
        //     });
        // }

        self.model = Some(Model {
            desktop_bounds,
            desktop_virtual_origin,
            desktop_color_texture,
            desktop_gray_texture,
            desktop_color_image,
            desktop_gray_image,
            windows: desktop_windows,
            shown: false,
            debug: false,
            zoom: 1.0,
            // accent_light: rgb(0.0, 175.0 / 255.0, 240.0 / 255.0),
            // accent_dark: rgb(0.0, 125.0 / 255.0, 180.0 / 255.0),
            // dash_black_white: bw_tex,
            selection: None,
            captured: false,
            mouse_pt: ScreenPointF::zero(),
            mouse_state: MouseState::Up,
            mouse_anchor_pt,
            mouse_anchored: false,
            mouse: Mouse::new(),
            // button_panel: ui::ButtonPanel::new(),
        });

        for (i, monitor) in monitors.iter().enumerate() {
            let position = monitor.position();
            let size = monitor.size();

            let attributes = WindowAttributes::default()
                .with_decorations(false)
                .with_visible(false)
                .with_title("Clowd Capture");

            let window = event_loop.create_window(attributes).unwrap();

            let surface_future = self
                .context
                .create_surface(window.clone(), size.width, size.height, self.present_mode);

            let surface = pollster::block_on(surface_future).expect("Error creating surface");

            let monitor_bounds = ScreenRect::from_xy_size(position.x, position.y, size.width as i32, size.height as i32);
            let monitor_bounds = vd_transform.outer_transformed_rect(&monitor_bounds);

            renderers.push(RendererInfo {
                surface,
                window,
                monitor_handle: monitor.clone(),
                monitor_bounds,
                transform: TransformUnit::new(monitor_bounds, monitor.scale_factor()),
                ready: false,
                scale_factor: monitor.scale_factor(),
                is_primary: monitor_bounds.contains(mouse_anchor_pt),
            });

            info!("[TIME] Monitor Build: {:?}", Duration::from_millis(sw.ms() as u64));
        }
    }

    fn window_event(&mut self, _event_loop: &ActiveEventLoop, _id: WindowId, _event: WindowEvent) {}
}

pub fn run_app(backends: Option<Backends>, present_mode: wgpu::PresentMode) -> Result<()> {
    info!("Starting application event loop...");
    let context = RenderContext::new(backends);
    let event_loop = EventLoop::<UserEvent>::with_user_event().build()?;

    let mut app = App {
        render_context: context,
        model: None,
        event_proxy: event_loop.create_proxy(),
        present_mode,
        renderers: Vec::new(),
    };

    event_loop.set_control_flow(ControlFlow::Wait);
    event_loop.run_app(&mut app)?;
    info!("Application exited successfully.");
    Ok(())
}
