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
    kurbo::{Affine, Circle},
    peniko::{Color, Fill},
    wgpu::{self, Backends, Texture},
    AaConfig, RenderParams, Renderer, RendererOptions, Scene,
};
use winit::{
    application::ApplicationHandler,
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
    render_context: RenderContext,
    event_proxy: EventLoopProxy<UserEvent>,
    present_mode: wgpu::PresentMode,
    renderers: Vec<RendererInfo<'s>>,
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
    renderer: Renderer,
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

        for (i, monitor) in monitors.enumerate() {
            let position = monitor.position();
            let size = monitor.size();

            let attributes = WindowAttributes::default()
                .with_decorations(false)
                .with_visible(false)
                .with_no_redirection_bitmap(true)
                .with_title("Clowd Capture");

            let window = Arc::new(event_loop.create_window(attributes).unwrap());

            let surface_future = self
                .render_context
                .create_surface(window.clone(), size.width, size.height, self.present_mode);

            let surface = pollster::block_on(surface_future).expect("Error creating surface");

            // let device_future = self
            //     .render_context
            //     .device(Some(&surface.surface));
            // let device_id = pollster::block_on(device_future).expect("Error creating device");
            let device = self
                .render_context
                .devices
                .get(surface.dev_id)
                .unwrap();

            const AA_CONFIGS: [AaConfig; 3] = [AaConfig::Area, AaConfig::Msaa8, AaConfig::Msaa16];

            fn default_threads() -> usize {
                #[cfg(target_os = "macos")]
                return 1;
                #[cfg(not(target_os = "macos"))]
                return 0;
            }

            let renderer = Renderer::new(
                &device.device,
                RendererOptions {
                    surface_format: Some(surface.format),
                    use_cpu: false,
                    antialiasing_support: AA_CONFIGS.iter().copied().collect(),
                    num_init_threads: NonZeroUsize::new(default_threads()),
                },
            )
            .unwrap();

            let monitor_bounds = ScreenRect::from_xy_size(position.x, position.y, size.width as i32, size.height as i32);
            let monitor_bounds = vd_transform.outer_transformed_rect(&monitor_bounds);

            self.renderers.push(RendererInfo {
                window_id: window.id(),
                surface,
                renderer,
                window: window.clone(),
                monitor_handle: monitor.clone(),
                monitor_bounds,
                transform: TransformUnit::new(monitor_bounds, monitor.scale_factor()),
                ready: false,
                scale_factor: monitor.scale_factor(),
                is_primary: monitor_bounds.contains(mouse_anchor_pt),
            });

            info!("[TIME] Monitor Build: {:?}", Duration::from_millis(sw.ms() as u64));
        }

        info!("[TIME] Done: {:?}", Duration::from_millis(sw.ms() as u64));
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, id: WindowId, event: WindowEvent) {
        println!("Window event: {:?}", event);
        self.input
            .update::<UserEvent>(&Event::WindowEvent {
                window_id: id,
                event: event.clone(),
            });

        self.renderers.get_by_id(id).unwrap().ready = true;

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
                let mut scene = Scene::new();
                scene.fill(
                    Fill::NonZero,
                    Affine::IDENTITY,
                    Color::rgba8(242, 140, 168, 255),
                    None,
                    &Circle::new((420.0, 200.0), 120.0),
                );

                // Render to your window/buffer/etc.
                // let renderer = renderer.renderer;
                let renderer = self.renderers.get_by_id(id).unwrap();
                let device = self
                    .render_context
                    .devices
                    .get(renderer.surface.dev_id)
                    .unwrap();

                let surface_texture = renderer
                    .surface
                    .surface
                    .get_current_texture()
                    .expect("failed to get surface texture");

                renderer
                    .renderer
                    .render_to_surface(
                        &device.device,
                        &device.queue,
                        &scene,
                        &surface_texture,
                        &RenderParams {
                            base_color: Color::BLACK, // Background color
                            width: renderer.surface.config.width,
                            height: renderer.surface.config.height,
                            antialiasing_method: AaConfig::Msaa16,
                        },
                    )
                    .expect("Failed to render to surface");

                surface_texture.present();
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
        self.renderers.iter().all(|r| r.ready)
    }

    fn show_all(&mut self) {
        self.renderers.iter_mut().for_each(|r| {
            let window = &r.window;
            window.set_fullscreen(Some(Fullscreen::Borderless(Some(r.monitor_handle.clone()))));
            window.set_visible(true);
            if r.is_primary {
                window.focus_window();
            }
        });
    }

    fn set_cursor(&mut self, cursor: Option<CursorIcon>) {
        self.renderers.iter_mut().for_each(|r| {
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
        self.renderers
            .iter()
            .find(|r| r.monitor_bounds.to_f64().contains(pt))
            .or_else(|| {
                self.renderers.iter().min_by(|a, b| {
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
        render_context: context,
        model: Default::default(),
        event_proxy: event_loop.create_proxy(),
        present_mode,
        renderers: Vec::new(),
        mouse: Mouse::new(),
        model_initialized: false,
        input: WinitInputHelper::new(),
    };

    event_loop.set_control_flow(ControlFlow::Wait);
    event_loop.run_app(&mut app)?;
    info!("Application exited successfully.");
    Ok(())
}
