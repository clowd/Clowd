use std::{num::NonZeroUsize, sync::Arc, time::Duration};

use crate::{
    geometry::*,
    input_helper::WinitInputHelper,
    render_context::{RenderContext, RenderSurface},
    screenshot::capture_desktop,
};
use anyhow::Result;
use euclid::Transform2D;
use image::{DynamicImage, ImageBuffer};
use mouse_rs::Mouse;
use vello::{
    kurbo::{Affine, Circle, Ellipse, Line, RoundedRect, Stroke},
    peniko::{Color, Fill},
    wgpu::{self, Backends, Texture},
    AaConfig, RenderParams, Renderer, RendererOptions, Scene,
};
use winit::{
    application::ApplicationHandler,
    dpi::LogicalSize,
    event::{ElementState, Event, MouseButton, MouseScrollDelta, StartCause, WindowEvent},
    event_loop::{ActiveEventLoop, ControlFlow, EventLoop, EventLoopProxy},
    keyboard::KeyCode,
    monitor::MonitorHandle,
    platform::windows::WindowAttributesExtWindows,
    raw_window_handle::HasWindowHandle,
    window::{CursorIcon, Fullscreen, Window, WindowAttributes, WindowId},
};
use xcap::Window as XCapWindow;

#[derive(Debug, Clone, Copy, PartialEq, Default)]
enum MouseState {
    #[default]
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
    context: RenderContext,
    event_proxy: EventLoopProxy<UserEvent>,
    present_mode: wgpu::PresentMode,
    renderer_contexts: Vec<RendererInfo<'s>>,
    // An array of renderers, one per wgpu device
    renderers: Vec<Option<Renderer>>,
    mouse: Mouse,
    model: Model,
    model_initialized: bool,
    input: WinitInputHelper,
}

#[derive(derivative::Derivative)]
#[derivative(Default)]
struct Model {
    desktop_bounds: ScreenRect,
    desktop_virtual_origin: ScreenPoint,
    // desktop_color_texture: wgpu::Texture,
    // desktop_gray_texture: wgpu::Texture,
    #[derivative(Default(value = "DynamicImage::new_bgra8(1, 1)"))]
    desktop_color_image: DynamicImage,
    #[derivative(Default(value = "DynamicImage::new_bgra8(1, 1)"))]
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
    // button_panel: ui::ButtonPanel,
}

#[allow(dead_code)]
struct RendererInfo<'s> {
    // SAFETY: We MUST drop the surface before the `window`,
    // so the fields must be in this order
    window_id: WindowId,
    surface: RenderSurface<'s>,
    window: Arc<Window>,
    monitor_handle: MonitorHandle,
    monitor_bounds: ScreenRect,
    transform: TransformUnit,
    is_primary: bool,
    ready: bool,
    scale_factor: f64,
}

trait RendererInfoImpl<'s> {
    fn get_by_id(&mut self, id: WindowId) -> Option<&mut RendererInfo<'s>>;
}

impl<'s> RendererInfoImpl<'s> for Vec<RendererInfo<'s>> {
    fn get_by_id(&mut self, id: WindowId) -> Option<&mut RendererInfo<'s>> {
        self.iter_mut().find(|r| r.window_id == id)
    }
}

struct DesktopWindowInfo {
    title: String,
    window_bounds: ScreenRect,
    capture: Option<DynamicImage>,
}

/// Helper function that creates a Winit window and returns it (wrapped in an Arc for sharing between threads)
fn create_winit_window(event_loop: &ActiveEventLoop) -> Arc<Window> {
    let attr = Window::default_attributes()
        .with_inner_size(LogicalSize::new(1044, 800))
        .with_resizable(true)
        .with_fullscreen(Some(Fullscreen::Borderless(None)))
        .with_visible(false)
        .with_title("Vello Shapes");
    Arc::new(event_loop.create_window(attr).unwrap())
}

/// Helper function that creates a vello `Renderer` for a given `RenderContext` and `RenderSurface`
fn create_vello_renderer(render_cx: &RenderContext, surface: &RenderSurface<'_>) -> Renderer {
    Renderer::new(
        &render_cx.devices[surface.dev_id].device,
        RendererOptions {
            surface_format: Some(surface.format),
            use_cpu: false,
            antialiasing_support: vello::AaSupport {
                area: true,
                msaa8: false,
                msaa16: false,
            },
            num_init_threads: NonZeroUsize::new(1),
        },
    )
    .expect("Couldn't create renderer")
}

/// Add shapes to a vello scene. This does not actually render the shapes, but adds them
/// to the Scene data structure which represents a set of objects to draw.
fn add_shapes_to_scene(scene: &mut Scene) {
    // Draw an outlined rectangle
    let stroke = Stroke::new(6.0);
    let rect = RoundedRect::new(10.0, 10.0, 240.0, 240.0, 20.0);
    let rect_stroke_color = Color::rgba(0.9804, 0.702, 0.5294, 1.);
    scene.stroke(&stroke, Affine::IDENTITY, rect_stroke_color, None, &rect);

    // Draw a filled circle
    let circle = Circle::new((420.0, 200.0), 120.0);
    let circle_fill_color = Color::rgba(0.9529, 0.5451, 0.6588, 1.);
    scene.fill(vello::peniko::Fill::NonZero, Affine::IDENTITY, circle_fill_color, None, &circle);

    // Draw a filled ellipse
    let ellipse = Ellipse::new((250.0, 420.0), (100.0, 160.0), -90.0);
    let ellipse_fill_color = Color::rgba(0.7961, 0.651, 0.9686, 1.);
    scene.fill(vello::peniko::Fill::NonZero, Affine::IDENTITY, ellipse_fill_color, None, &ellipse);

    // Draw a straight line
    let line = Line::new((260.0, 20.0), (620.0, 100.0));
    let line_stroke_color = Color::rgba(0.5373, 0.7059, 0.9804, 1.);
    scene.stroke(&stroke, Affine::IDENTITY, line_stroke_color, None, &line);
}

impl ApplicationHandler<UserEvent> for App<'_> {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.model_initialized {
            return;
        }

        let sw = simple_stopwatch::Stopwatch::start_new();
        info!("[TIME] Start: {:?}", Duration::from_millis(sw.ms() as u64));

        let monitors = event_loop.available_monitors();
        // let windows = XCapWindow::all().ok();

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

        let (desktop_bounds, desktop_color_image, desktop_gray_image) = capture_desktop().unwrap();

        info!("[TIME] Captured: {:?}", Duration::from_millis(sw.ms() as u64));

        let desktop_virtual_origin = desktop_bounds.top_left();
        let vd_transform = Transform2D::<i32, ScreenUnit, ScreenUnit>::identity().then_translate(-desktop_virtual_origin.to_vector());

        let desktop_bounds = vd_transform.outer_transformed_rect(&desktop_bounds);

        info!("[TIME] Windows Captured: {:?}", Duration::from_millis(sw.ms() as u64));

        // let desktop_color_texture = Texture::from_image(event_loop, &desktop_color_image);
        // let desktop_gray_texture = Texture::from_image(event_loop, &desktop_gray_image);

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

        self.model = Model {
            desktop_bounds,
            desktop_virtual_origin,
            // desktop_color_texture,
            // desktop_gray_texture,
            desktop_color_image,
            desktop_gray_image,
            // windows: desktop_windows,
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
            ..Default::default() // button_panel: ui::ButtonPanel::new(),
        };

        let window = create_winit_window(event_loop);

        let size = window.inner_size();
        let surface_future = self
            .context
            .create_surface(window.clone(), size.width, size.height, wgpu::PresentMode::AutoVsync);
        let surface = pollster::block_on(surface_future).expect("Error creating surface");

        // Create a vello Renderer for the surface (using its device id)
        self.renderers
            .resize_with(self.context.devices.len(), || None);
        self.renderers[surface.dev_id].get_or_insert_with(|| create_vello_renderer(&self.context, &surface));

        let scale = primary.scale_factor();

        self.renderer_contexts.push(RendererInfo {
            window_id: window.id(),
            surface,
            window,
            monitor_handle: primary,
            monitor_bounds: primary_bounds,
            transform: TransformUnit::new(primary_bounds, scale),
            ready: false,
            scale_factor: scale,
            is_primary: true,
        });

        // for (i, monitor) in monitors.enumerate() {
        //     let position = monitor.position();
        //     let size = monitor.size();

        //     let attributes = WindowAttributes::default()
        //         .with_decorations(false)
        //         .with_visible(false)
        //         .with_no_redirection_bitmap(true)
        //         .with_title("Clowd Capture");

        //     let window = Arc::new(event_loop.create_window(attributes).unwrap());

        //     let surface_future = self
        //         .context
        //         .create_surface(window.clone(), size.width, size.height, self.present_mode);

        //     let surface = pollster::block_on(surface_future).expect("Error creating surface");

        //     // let device_future = self
        //     //     .render_context
        //     //     .device(Some(&surface.surface));
        //     // let device_id = pollster::block_on(device_future).expect("Error creating device");
        //     let device = self
        //         .context
        //         .devices
        //         .get(surface.dev_id)
        //         .unwrap();

        //     const AA_CONFIGS: [AaConfig; 3] = [AaConfig::Area, AaConfig::Msaa8, AaConfig::Msaa16];

        //     fn default_threads() -> usize {
        //         #[cfg(target_os = "macos")]
        //         return 1;
        //         #[cfg(not(target_os = "macos"))]
        //         return 0;
        //     }

        //     let renderer = Renderer::new(
        //         &device.device,
        //         RendererOptions {
        //             surface_format: Some(surface.format),
        //             use_cpu: false,
        //             antialiasing_support: AA_CONFIGS.iter().copied().collect(),
        //             num_init_threads: NonZeroUsize::new(default_threads()),
        //         },
        //     )
        //     .unwrap();

        //     let monitor_bounds = ScreenRect::from_xy_size(position.x, position.y, size.width as i32, size.height as i32);
        //     let monitor_bounds = vd_transform.outer_transformed_rect(&monitor_bounds);

        //     self.renderer_contexts.push(RendererInfo {
        //         window_id: window.id(),
        //         surface,
        //         renderer,
        //         window: window.clone(),
        //         monitor_handle: monitor.clone(),
        //         monitor_bounds,
        //         transform: TransformUnit::new(monitor_bounds, monitor.scale_factor()),
        //         ready: false,
        //         scale_factor: monitor.scale_factor(),
        //         is_primary: monitor_bounds.contains(mouse_anchor_pt),
        //     });

        //     info!("[TIME] Monitor Build: {:?}", Duration::from_millis(sw.ms() as u64));
        // }

        info!("[TIME] Done: {:?}", Duration::from_millis(sw.ms() as u64));
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, id: WindowId, event: WindowEvent) {
        println!("Window event: {:?}", event);
        self.input
            .update::<UserEvent>(&Event::WindowEvent {
                window_id: id,
                event: event.clone(),
            });

        self.renderer_contexts
            .get_by_id(id)
            .unwrap()
            .ready = true;

        if !self.model.shown && self.is_all_ready() {
            self.show_all();
            self.model.shown = true;
            info!("All windows ready and shown.");
        }

        if self.input.key_pressed(KeyCode::KeyD) {
            self.model.debug = !self.model.debug;
        } else if self.input.key_pressed(KeyCode::KeyR) {
            self.model.zoom = 1.0;
            self.set_anchored(false);
            self.model.captured = false;
            self.model.selection = None;
        } else if self.input.key_pressed(KeyCode::Escape) {
            event_loop.exit();
        }

        match event {
            WindowEvent::RedrawRequested => {
                let render_state = self.renderer_contexts.get_by_id(id).unwrap();
                let mut scene = Scene::new();

                // Re-add the objects to draw to the scene.
                add_shapes_to_scene(&mut scene);

                // Get the RenderSurface (surface + config)
                let surface = &render_state.surface;
                let window = &render_state.window;

                // Get the window size
                let width = surface.config.width;
                let height = surface.config.height;

                // Get a handle to the device
                let device_handle = &self.context.devices[surface.dev_id];

                // Get the surface's texture
                let surface_texture = surface
                    .surface
                    .get_current_texture()
                    .expect("failed to get surface texture");

                // Render to the surface's texture
                self.renderers[surface.dev_id]
                    .as_mut()
                    .unwrap()
                    .render_to_surface(
                        &device_handle.device,
                        &device_handle.queue,
                        &scene,
                        &surface_texture,
                        &vello::RenderParams {
                            base_color: Color::BLACK, // Background color
                            width,
                            height,
                            antialiasing_method: AaConfig::Msaa16,
                        },
                    )
                    .expect("failed to render to surface");

                // Queue the texture to be presented on the surface
                surface_texture.present();

                device_handle
                    .device
                    .poll(wgpu::Maintain::Poll);
                window.request_redraw();
                println!("Redraw requested");

                // scene.fill(
                //     Fill::NonZero,
                //     Affine::IDENTITY,
                //     Color::rgba8(242, 140, 168, 255),
                //     None,
                //     &Circle::new((420.0, 200.0), 120.0),
                // );

                // // Render to your window/buffer/etc.
                // // let renderer = renderer.renderer;
                // let renderer = self.renderer_contexts.get_by_id(id).unwrap();
                // let device = self
                //     .context
                //     .devices
                //     .get(renderer.surface.dev_id)
                //     .unwrap();

                // let surface_texture = renderer
                //     .surface
                //     .surface
                //     .get_current_texture()
                //     .expect("failed to get surface texture");

                // renderer
                //     .renderer
                //     .render_to_surface(
                //         &device.device,
                //         &device.queue,
                //         &scene,
                //         &surface_texture,
                //         &RenderParams {
                //             base_color: Color::BLACK, // Background color
                //             width: renderer.surface.config.width,
                //             height: renderer.surface.config.height,
                //             antialiasing_method: AaConfig::Area,
                //         },
                //     )
                //     .expect("Failed to render to surface");

                // surface_texture.present();
                // device.device.poll(wgpu::Maintain::Poll);

                // renderer.window.request_redraw();
                // info!("Window redrawn.");
            }
            WindowEvent::CloseRequested => {
                event_loop.exit();
            }
            WindowEvent::MouseInput {
                device_id: _,
                state,
                button,
            } => {
                if state == ElementState::Pressed && button == MouseButton::Left {
                    let pt = self.model.mouse_pt;
                    self.handle_mouse_down(pt);
                } else if state == ElementState::Released && button == MouseButton::Left {
                    let pt = self.model.mouse_pt;
                    self.handle_mouse_up(pt);
                }
            }
            WindowEvent::CursorMoved {
                device_id: _,
                position,
            } => {
                //     let transform = renderer.transform.with_logical_units();
                //     let pt = transform.pt_to_screen(pt.to_window_point());
                //     model.handle_mouse_move(app, pt);
                let pt = ScreenPointF::new(position.x, position.y);
                self.handle_mouse_move(pt);
            }
            WindowEvent::MouseWheel {
                device_id: _,
                delta,
                phase: _,
            } => {
                if !self.model.captured {
                    let delta = match delta {
                        MouseScrollDelta::LineDelta(_, y) => y,
                        MouseScrollDelta::PixelDelta(pt) => pt.y as f32,
                    };

                    let mut zoom = self.model.zoom;

                    if self.input.held_shift() || self.input.held_control() {
                        if delta > 0.0 {
                            zoom *= 1.05;
                        } else {
                            zoom /= 1.05;
                        }
                    } else {
                        if delta > 0.0 {
                            zoom *= 2.0;
                        } else {
                            zoom /= 2.0;
                        }
                    }

                    self.model.zoom = zoom.max(1.0).min(256.0);
                    self.set_anchored(zoom > 1.0);
                }
            }
            _ => (),
        }
    }
}

impl App<'_> {
    fn is_all_ready(&self) -> bool {
        self.renderer_contexts
            .iter()
            .all(|r| r.ready)
    }

    fn show_all(&mut self) {
        self.renderer_contexts
            .iter_mut()
            .for_each(|r| {
                let window = &r.window;
                // window.set_fullscreen(Some(Fullscreen::Borderless(Some(r.monitor_handle.clone()))));
                window.set_visible(true);
                if r.is_primary {
                    window.focus_window();
                }
            });
    }

    fn set_cursor(&mut self, cursor: Option<CursorIcon>) {
        self.renderer_contexts
            .iter_mut()
            .for_each(|r| {
                let window = &r.window;
                if let Some(cur) = cursor {
                    window.set_cursor_visible(true);
                    window.set_cursor(cur);
                } else {
                    window.set_cursor_visible(false);
                }
            });
    }

    fn set_anchored(&mut self, anchored: bool) {
        if anchored && !self.model.mouse_anchored {
            self.model.mouse_anchored = true;
            let _ = self
                .mouse
                .move_to(self.model.mouse_anchor_pt.x, self.model.mouse_anchor_pt.y);
        } else if !anchored && self.model.mouse_anchored {
            self.model.mouse_anchored = false;
            let pt = self.model.mouse_pt.to_i32();
            let relative = pt + self.model.desktop_virtual_origin.to_vector();
            let _ = self.mouse.move_to(relative.x, relative.y);
        }
    }

    fn get_nearest_renderer(&self, pt: ScreenPointF) -> &RendererInfo {
        self.renderer_contexts
            .iter()
            .find(|r| r.monitor_bounds.to_f64().contains(pt))
            .or_else(|| {
                self.renderer_contexts.iter().min_by(|a, b| {
                    let a_dist = a
                        .monitor_bounds
                        .center()
                        .to_f64()
                        .distance_to(pt);
                    let b_dist = b
                        .monitor_bounds
                        .center()
                        .to_f64()
                        .distance_to(pt);
                    a_dist.partial_cmp(&b_dist).unwrap()
                })
            })
            .unwrap()
    }

    fn handle_mouse_move(&mut self, pt: ScreenPointF) {
        if self.model.mouse_anchored {
            let relative_anchor = self.model.mouse_anchor_pt - self.model.desktop_virtual_origin.to_vector();
            if relative_anchor != pt.to_i32() {
                let anchor_f = relative_anchor.to_f64();
                let x_delta = (pt.x - anchor_f.x) / self.model.zoom;
                let y_delta = (pt.y - anchor_f.y) / self.model.zoom;

                let mut mx = self.model.mouse_pt.x + x_delta;
                let mut my = self.model.mouse_pt.y + y_delta;

                let bounds = self
                    .get_nearest_renderer(ScreenPointF::new(mx, my))
                    .monitor_bounds
                    .to_f64();

                // clip cursor to nearest monitor
                let left = bounds.left();
                let right = bounds.right();
                let top = bounds.top();
                let bottom = bounds.bottom();

                mx = mx.max(left).min(right - 0.001);
                my = my.max(top).min(bottom - 0.001);

                self.model.mouse_pt = ScreenPointF::new(mx, my);
                let _ = self
                    .mouse
                    .move_to(self.model.mouse_anchor_pt.x, self.model.mouse_anchor_pt.y);
            }
        } else {
            self.model.mouse_pt = pt;
        }

        let pt = self.model.mouse_pt;
        match self.model.mouse_state {
            MouseState::Up => (),
            MouseState::StartSelection(start) => {
                let dist = start.distance_to(pt);
                let drag_threshold = 10.0 / self.model.zoom;
                if dist > drag_threshold {
                    self.model.mouse_state = MouseState::MakingSelection(start);
                    self.model.selection = Some(ScreenRect::from_rounded_threshold(start.x, start.y, pt.x, pt.y))
                }
            }
            MouseState::MakingSelection(start) => {
                self.model.selection = Some(ScreenRect::from_rounded_threshold(start.x, start.y, pt.x, pt.y))
            }
            MouseState::MovingSelection(orig_rect, orig_point) => {
                let dx = (pt.x - orig_point.x) as i32;
                let dy = (pt.y - orig_point.y) as i32;
                let x1 = orig_rect.min_x() + dx;
                let y1 = orig_rect.min_y() + dy;
                let x2 = orig_rect.max_x() + dx;
                let y2 = orig_rect.max_y() + dy;
                self.model.selection = Some(ScreenRect::from_exact(x1, y1, x2, y2));
                // self.update_buttons();
            } // MouseState::SizingSelection(hit) => {
              //     if let Some(selection) = model.selection {
              //         let rect = hit.resize_rect(pt, selection);
              //         model.selection = Some(rect);
              //     }
              //     self.update_buttons();
              // }
        }

        // if model.captured {
        //     self.set_cursor(app, Some(self.button_panel.hit_test(pt).to_cursor()));
        // }
    }

    fn handle_mouse_down(&mut self, pt: ScreenPointF) {
        if self.model.captured {
            // let hit = model.button_panel.hit_test(pt);
            // if hit.is_size_handle() {
            //     model.mouse_state = MouseState::SizingSelection(hit);
            // } else if hit == HitTest::Content {
            //     model.mouse_state = MouseState::MovingSelection(self.selection.unwrap(), pt);
            // }
        } else {
            self.model.mouse_state = MouseState::StartSelection(pt);
        }
    }

    fn handle_mouse_up(&mut self, pt: ScreenPointF) {
        match self.model.mouse_state {
            MouseState::StartSelection(_) => {
                self.model.selection = None;
            }
            MouseState::MakingSelection(start) => {
                self.model.zoom = 1.0;
                self.model.captured = true;
                self.model.selection = Some(ScreenRect::from_rounded_threshold(start.x, start.y, pt.x, pt.y));
                self.set_anchored(false);
                // self.update_buttons();
            }
            _ => (),
        }
        self.model.mouse_state = MouseState::Up;
    }

    // fn update_buttons(&mut self) {
    //     if let Some(selection) = self.selection {
    //         let renderer = self.get_nearest_renderer(selection.center().to_f64());
    //         self.button_panel
    //             .update(renderer.monitor_bounds, renderer.scale_factor, selection);
    //     }
    // }
}

pub fn run_app(backends: Option<Backends>, present_mode: wgpu::PresentMode) -> Result<()> {
    info!("Starting application event loop...");
    let context = RenderContext::new(backends);
    let event_loop = EventLoop::<UserEvent>::with_user_event().build()?;

    let mut app = App {
        context,
        model: Default::default(),
        event_proxy: event_loop.create_proxy(),
        present_mode,
        renderer_contexts: Vec::new(),
        mouse: Mouse::new(),
        model_initialized: false,
        input: WinitInputHelper::new(),
        renderers: Vec::new(),
    };

    event_loop.set_control_flow(ControlFlow::Wait);
    event_loop.run_app(&mut app)?;
    info!("Application exited successfully.");
    Ok(())
}
