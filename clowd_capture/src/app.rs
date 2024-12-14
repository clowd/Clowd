use std::{
    num::NonZeroUsize,
    sync::{atomic::AtomicBool, mpsc, Arc, RwLock},
    thread::sleep_ms,
    time::Duration,
};

use crate::{geometry::*, gpu::RenderContext, input::WinitInputHelper, render, screenshot::capture_desktop};
use anyhow::Result;
use euclid::Transform2D;
use image::{DynamicImage, ImageBuffer, RgbaImage};
use mouse_rs::Mouse;
use simple_stopwatch::Stopwatch;

use vello::{
    peniko::Color,
    wgpu::{self, Backends},
};

#[cfg(target_os = "macos")]
use winit::platform::macos::WindowAttributesExtMacOS;
#[cfg(target_os = "macos")]
use winit::platform::macos::WindowExtMacOS;

#[cfg(windows)]
use winit::platform::windows::WindowAttributesExtWindows;

use winit::{
    application::ApplicationHandler,
    dpi::LogicalSize,
    event::{ElementState, Event, MouseButton, MouseScrollDelta, StartCause, WindowEvent},
    event_loop::{ActiveEventLoop, ControlFlow, EventLoop, EventLoopProxy},
    keyboard::KeyCode,
    monitor::MonitorHandle,
    raw_window_handle::{HasRawWindowHandle, HasWindowHandle, RawWindowHandle},
    window::{CursorIcon, Fullscreen, Window, WindowAttributes, WindowId},
};
use xcap::{Monitor as XCapMonitor, Window as XCapWindow};

#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub enum MouseState {
    #[default]
    Up,
    StartSelection(ScreenPointF),
    MakingSelection(ScreenPointF),
    // SizingSelection(HitTest),
    MovingSelection(ScreenRect, ScreenPointF),
}

#[derive(Debug, Clone)]
pub enum UserEvent {
    None,
    RendererReady(WindowId),
    RendererExited(WindowId),
}

#[derive(Debug, Clone)]
pub enum RenderMessage {
    None,
    ModelUpdate(SharedModel),
    Resize((u32, u32)),
    Close,
}

#[derive(Debug, Clone)]
pub struct SharedModel {
    pub zoom: f64,
    pub mouse_pt: ScreenPointF,
    pub mouse_state: MouseState,
    pub selection: Option<ScreenRect>,
    pub captured: bool,
    pub debug: bool,
    pub close_requested: bool,
}

struct App {
    gpu_device: RenderContext,
    present_mode: wgpu::PresentMode,
    renderers: Vec<RendererInfo>,
    initialized: bool,
    shown: bool,
    time: Stopwatch,
    desktop_bounds: ScreenRect,
    desktop_virtual_origin: ScreenPoint,
    desktop_color_image: RgbaImage,
    desktop_gray_image: RgbaImage,
    windows: Vec<DesktopWindowInfo>,
    mouse: Mouse,
    mouse_anchor_pt: ScreenPoint,
    mouse_anchored: bool,
    input: WinitInputHelper,
    model: Arc<RwLock<SharedModel>>,
    event_proxy: EventLoopProxy<UserEvent>,
    accent_light: Color,
    accent_dark: Color,
    vd_transform: Transform2D<i32, ScreenUnit, ScreenUnit>,
    // button_panel: ui::ButtonPanel,
}

struct RendererInfo {
    window_id: WindowId,
    monitor_handle: MonitorHandle,
    monitor_bounds: ScreenRect,
    transform: TransformUnit,
    is_primary: bool,
    scale_factor: f64,
    window: Arc<Window>,
    ready: bool,
    sender: mpsc::Sender<RenderMessage>,
}

#[derive(Clone)]
pub struct RendererDto {
    pub window_id: WindowId,
    pub monitor_handle: MonitorHandle,
    pub monitor_bounds: ScreenRect,
    pub transform: TransformUnit,
    pub is_primary: bool,
    pub scale_factor: f64,
    pub desktop_bounds: ScreenRect,
    pub desktop_virtual_origin: ScreenPoint,
    pub desktop_color_image: RgbaImage,
    pub desktop_gray_image: RgbaImage,
    pub event_proxy: EventLoopProxy<UserEvent>,
    pub accent_light: Color,
    pub accent_dark: Color,
    pub vd_transform: Transform2D<i32, ScreenUnit, ScreenUnit>,
}

trait GetById<T> {
    fn getid_mut(&mut self, id: WindowId) -> Option<&mut T>;
    fn getid(&self, id: WindowId) -> Option<&T>;
}

impl GetById<RendererInfo> for Vec<RendererInfo> {
    fn getid_mut(&mut self, id: WindowId) -> Option<&mut RendererInfo> {
        self.iter_mut().find(|r| r.window_id == id)
    }
    fn getid(&self, id: WindowId) -> Option<&RendererInfo> {
        self.iter().find(|r| r.window_id == id)
    }
}

struct DesktopWindowInfo {
    title: String,
    window_bounds: ScreenRect,
    capture: Option<DynamicImage>,
}

impl ApplicationHandler<UserEvent> for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.initialized {
            return;
        } else {
            self.initialized = true;
        }

        let monitors: Vec<_> = event_loop.available_monitors().collect();

        for (i, monitor) in monitors.iter().enumerate() {
            let position = monitor.position();
            let size = monitor.size();
            println!("Monitor {}: {:?}, {:?},{:?}", i, monitor.name(), position, size);

            let attributes = WindowAttributes::default()
                .with_decorations(false)
                .with_blur(true)
                .with_visible(false)
                .with_inner_size(size)
                .with_position(position)
                // .with_disallow_hidpi(true)
                .with_title("Clowd Capture");

            let window = event_loop.create_window(attributes).unwrap();

            let monitor_bounds = ScreenRect::from_xy_size(position.x, position.y, size.width as i32, size.height as i32);
            let monitor_bounds = self
                .vd_transform
                .outer_transformed_rect(&monitor_bounds);

            let (sender, receiver) = mpsc::channel();

            let window_id = window.id();
            let info = RendererInfo {
                window_id,
                window: Arc::new(window),
                monitor_handle: monitor.clone(),
                monitor_bounds,
                transform: TransformUnit::new(monitor_bounds, monitor.scale_factor()),
                ready: false,
                scale_factor: monitor.scale_factor(),
                is_primary: monitor_bounds.contains(self.mouse_anchor_pt),
                sender,
            };

            let dto = RendererDto {
                accent_dark: self.accent_dark,
                accent_light: self.accent_light,
                desktop_bounds: self.desktop_bounds,
                desktop_color_image: self.desktop_color_image.clone(),
                desktop_gray_image: self.desktop_gray_image.clone(),
                desktop_virtual_origin: self.desktop_virtual_origin,
                event_proxy: self.event_proxy.clone(),
                is_primary: info.is_primary,
                monitor_bounds: info.monitor_bounds,
                monitor_handle: info.monitor_handle.clone(),
                scale_factor: info.scale_factor,
                transform: info.transform.clone(),
                vd_transform: self.vd_transform.clone(),
                window_id: info.window_id,
            };

            let surface = self
                .gpu_device
                .create_surface(info.window.clone(), size.width, size.height, self.present_mode)
                .unwrap();

            self.renderers.push(info);
            self.print_time(format!("Window {}/{} created.", i + 1, monitors.len()).as_str());

            let model = self.model.read().unwrap().clone();
            render::begin_render_loop(surface, model, dto, receiver);
        }

        self.print_time("Initialization complete, waiting for renderers to be ready...");
    }

    fn user_event(&mut self, _event_loop: &ActiveEventLoop, event: UserEvent) {
        match event {
            UserEvent::RendererReady(id) => {
                if let Some(renderer) = self.renderers.getid_mut(id) {
                    renderer.ready = true;
                    self.print_time(format!("Renderer {:?} ready.", id).as_str());
                }

                if !self.shown && self.renderers.iter().all(|r| r.ready) {
                    self.renderers.iter_mut().for_each(|r| {
                        let window = &r.window;
                        #[cfg(target_os = "macos")]
                        window.set_simple_fullscreen(true);
                        window.set_visible(true);
                        if r.is_primary {
                            window.focus_window();
                        }
                    });
                    self.shown = true;
                    self.print_time("All renderers ready, shown all windows...");
                }
            }
            _ => (),
        }
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, id: WindowId, event: WindowEvent) {
        println!("Window {:?} event: {:?}", id, event);

        let model = self.model.clone();
        let mut model = model.write().unwrap();
        self.input
            .update::<UserEvent>(&Event::WindowEvent {
                window_id: id,
                event: event.clone(),
            });

        if self.input.key_pressed(KeyCode::KeyD) {
            model.debug = !model.debug;
        } else if self.input.key_pressed(KeyCode::KeyR) {
            model.zoom = 1.0;
            self.set_anchored(false, model.mouse_pt);
            model.captured = false;
            model.selection = None;
        } else if self.input.key_pressed(KeyCode::Escape) {
            model.close_requested = true;
            event_loop.exit();
        }

        match event {
            WindowEvent::Resized(size) => {
                // #[cfg(target_os = "macos")]
                // self.draw_targets
                //     .getid_mut(id)
                //     .unwrap()
                //     .1
                //     .resize(size.width, size.height);
                // self.renderers
                //     .getid_mut(id)
                //     .unwrap()
                //     .window
                //     .request_redraw();
            }
            WindowEvent::CloseRequested => {
                model.close_requested = true;
                event_loop.exit();
            }
            WindowEvent::MouseInput {
                device_id: _,
                state,
                button,
            } => {
                if state == ElementState::Pressed && button == MouseButton::Left {
                    if model.captured {
                        // let hit = model.button_panel.hit_test(pt);
                        // if hit.is_size_handle() {
                        //     model.mouse_state = MouseState::SizingSelection(hit);
                        // } else if hit == HitTest::Content {
                        //     model.mouse_state = MouseState::MovingSelection(self.selection.unwrap(), pt);
                        // }
                    } else {
                        model.mouse_state = MouseState::StartSelection(model.mouse_pt);
                    }
                } else if state == ElementState::Released && button == MouseButton::Left {
                    match model.mouse_state {
                        MouseState::StartSelection(_) => {
                            model.selection = None;
                        }
                        MouseState::MakingSelection(start) => {
                            model.zoom = 1.0;
                            model.captured = true;
                            model.selection = Some(ScreenRect::from_rounded_threshold(
                                start.x,
                                start.y,
                                model.mouse_pt.x,
                                model.mouse_pt.y,
                            ));
                            self.set_anchored(false, model.mouse_pt);
                            // self.update_buttons();
                        }
                        _ => (),
                    }
                    model.mouse_state = MouseState::Up;
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

                if self.mouse_anchored {
                    let relative_anchor = self.mouse_anchor_pt - self.desktop_virtual_origin.to_vector();
                    if relative_anchor != pt.to_i32() {
                        let anchor_f = relative_anchor.to_f64();
                        let x_delta = (pt.x - anchor_f.x) / model.zoom;
                        let y_delta = (pt.y - anchor_f.y) / model.zoom;

                        let mut mx = model.mouse_pt.x + x_delta;
                        let mut my = model.mouse_pt.y + y_delta;

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

                        model.mouse_pt = ScreenPointF::new(mx, my);
                        let _ = self
                            .mouse
                            .move_to(self.mouse_anchor_pt.x, self.mouse_anchor_pt.y);
                    }
                } else {
                    model.mouse_pt = pt;
                }

                let pt = model.mouse_pt;
                match model.mouse_state {
                    MouseState::Up => (),
                    MouseState::StartSelection(start) => {
                        let dist = start.distance_to(pt);
                        let drag_threshold = 10.0 / model.zoom;
                        if dist > drag_threshold {
                            model.mouse_state = MouseState::MakingSelection(start);
                            model.selection = Some(ScreenRect::from_rounded_threshold(start.x, start.y, pt.x, pt.y))
                        }
                    }
                    MouseState::MakingSelection(start) => {
                        model.selection = Some(ScreenRect::from_rounded_threshold(start.x, start.y, pt.x, pt.y))
                    }
                    MouseState::MovingSelection(orig_rect, orig_point) => {
                        let dx = (pt.x - orig_point.x) as i32;
                        let dy = (pt.y - orig_point.y) as i32;
                        let x1 = orig_rect.min_x() + dx;
                        let y1 = orig_rect.min_y() + dy;
                        let x2 = orig_rect.max_x() + dx;
                        let y2 = orig_rect.max_y() + dy;
                        model.selection = Some(ScreenRect::from_exact(x1, y1, x2, y2));
                        // self.update_buttons();
                    } // MouseState::SizingSelection(hit) => {
                      //     if let Some(selection) = model.selection {
                      //         let rect = hit.resize_rect(pt, selection);
                      //         model.selection = Some(rect);
                      //     }
                      //     self.update_buttons();
                      // }
                }

                self.send_message(RenderMessage::ModelUpdate(model.clone()));

                // if model.captured {
                //     self.set_cursor(app, Some(self.button_panel.hit_test(pt).to_cursor()));
                // }
            }
            WindowEvent::MouseWheel {
                device_id: _,
                delta,
                phase: _,
            } => {
                if !model.captured {
                    let delta = match delta {
                        MouseScrollDelta::LineDelta(_, y) => y,
                        MouseScrollDelta::PixelDelta(pt) => pt.y as f32,
                    };

                    let mut zoom = model.zoom;

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

                    model.zoom = zoom.max(1.0).min(256.0);
                    self.set_anchored(zoom > 1.0, model.mouse_pt);
                }
            }
            _ => (),
        }
    }
}

impl App {
    fn print_time(&self, msg: &str) {
        info!("[TIME {:?}] {}", Duration::from_millis(self.time.ms() as u64), msg);
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

    fn send_message(&self, message: RenderMessage) {
        self.renderers.iter().for_each(|r| {
            let _ = r.sender.send(message.clone());
        });
    }

    fn set_anchored(&mut self, anchored: bool, pt: ScreenPointF) {
        if anchored && !self.mouse_anchored {
            self.mouse_anchored = true;
            let _ = self
                .mouse
                .move_to(self.mouse_anchor_pt.x, self.mouse_anchor_pt.y);
        } else if !anchored && self.mouse_anchored {
            self.mouse_anchored = false;
            let pt = pt.to_i32();
            let relative = pt + self.desktop_virtual_origin.to_vector();
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
}

pub fn run_app(backends: Option<Backends>, present_mode: wgpu::PresentMode) -> Result<()> {
    info!("Application Starting...");

    let gpu_device = RenderContext::new(backends, None, None);
    let event_loop = EventLoop::<UserEvent>::with_user_event().build()?;
    let time = Stopwatch::start_new();

    info!("[TIME] Capturing: {:?}", Duration::from_millis(time.ms() as u64));

    let (desktop_bounds, desktop_color_image, desktop_gray_image) = capture_desktop()?;
    let desktop_virtual_origin = desktop_bounds.top_left();
    let vd_transform = Transform2D::<i32, ScreenUnit, ScreenUnit>::identity().then_translate(-desktop_virtual_origin.to_vector());
    let desktop_bounds = vd_transform.outer_transformed_rect(&desktop_bounds);

    // gpu_device.

    // let desktop_color_image = Image::new(
    //     desktop_color_image.into_raw().into(),
    //     vello::peniko::Format::Rgba8,
    //     desktop_bounds.width() as u32,
    //     desktop_bounds.height() as u32,
    // );

    // let desktop_gray_image = Image::new(
    //     desktop_gray_image.into_raw().into(),
    //     vello::peniko::Format::Rgba8,
    //     desktop_bounds.width() as u32,
    //     desktop_bounds.height() as u32,
    // );

    info!("[TIME] Captured: {:?}", Duration::from_millis(time.ms() as u64));

    let windows = XCapWindow::all()?;
    let mut desktop_windows = Vec::new();

    for window in windows {
        let window_bounds = ScreenRect::from_xy_size(window.x(), window.y(), window.width() as i32, window.height() as i32);
        let window_bounds = vd_transform.outer_transformed_rect(&window_bounds);
        desktop_windows.push(DesktopWindowInfo {
            title: window.title().to_string(),
            window_bounds,
            // capture: window.capture_image().ok(),
            capture: None,
        });
    }

    info!("[TIME] Windows Captured: {:?}", Duration::from_millis(time.ms() as u64));

    let mouse = Mouse::new();
    let mouse_pt = mouse
        .get_position()
        .map_err(|e| anyhow!("Error getting mouse position: {:?}", e))?;
    let mouse_pt = ScreenPointF::new(mouse_pt.x as f64, mouse_pt.y as f64);

    let monitors = XCapMonitor::all()?;
    let primary = monitors
        .iter()
        .find(|m| m.is_primary())
        .unwrap();

    let primary_bounds = ScreenRect::from_exact(primary.x(), primary.y(), primary.width() as i32, primary.height() as i32);
    let mouse_anchor_pt = primary_bounds.center();

    let shared = SharedModel {
        zoom: 1.0,
        mouse_pt,
        selection: None,
        captured: false,
        debug: false,
        mouse_state: MouseState::Up,
        close_requested: false,
    };

    let mut app = App {
        model: Arc::new(RwLock::new(shared)),
        present_mode,
        gpu_device,
        time,
        event_proxy: event_loop.create_proxy(),
        renderers: Vec::new(),
        mouse,
        shown: false,
        desktop_color_image,
        desktop_gray_image,
        mouse_anchor_pt,
        mouse_anchored: false,
        windows: desktop_windows,
        initialized: false,
        input: WinitInputHelper::new(),
        desktop_bounds,
        accent_dark: Color::rgb8(0, 125, 180),
        accent_light: Color::rgb8(0, 175, 240),
        desktop_virtual_origin,
        vd_transform,
    };

    app.print_time("Begin EventLoop...");

    let control_flow = if cfg!(windows) { ControlFlow::Wait } else { ControlFlow::Poll };

    event_loop.set_control_flow(control_flow);
    event_loop.run_app(&mut app)?;
    info!("Application exited successfully.");
    Ok(())
}
