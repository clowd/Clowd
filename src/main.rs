mod draw_ex;
mod event_handler;
mod geometry;
mod logging;
mod screenshot;
mod ui;
mod app;
mod render_context;

use std::time::Duration;

use geometry::*;
use ui::*;

use anyhow::{anyhow, Result};
use euclid::{SideOffsets2D, Transform2D};
use mouse_rs::Mouse;
use nannou::{
    color::{self},
    image::{self, DynamicImage, ImageBuffer, RgbaImage},
    prelude::*,
    winit::{
        monitor::MonitorHandle,
        window::{CursorIcon, WindowBuilder},
    },
};
use screenshot::capture_desktop;
use wgpu::{default_device_descriptor, SamplerBuilder, Texture};
use xcap::Window as XCapWindow;

#[macro_use]
extern crate log;

#[macro_use]
extern crate anyhow;

fn main() {
    let _ = logging::setup_logging("capture", None, true, false);
    nannou::app(model)
        .loop_mode(LoopMode::RefreshSync)
        // .backends(wgpu::Backends::DX12)
        .update(update)
        .run();
}

#[derive(Debug, Clone, Copy, PartialEq)]
enum MouseState {
    Up,
    StartSelection(ScreenPointF),
    MakingSelection(ScreenPointF),
    SizingSelection(HitTest),
    MovingSelection(ScreenRect, ScreenPointF),
}

#[allow(dead_code)]
struct Model {
    renderers: Vec<RendererInfo>,
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
    accent_light: Rgb,
    accent_dark: Rgb,
    dash_black_white: Texture,
    selection: Option<ScreenRect>,
    captured: bool,
    mouse_pt: ScreenPointF,
    mouse_state: MouseState,
    mouse_anchor_pt: ScreenPoint,
    mouse_anchored: bool,
    mouse: Mouse,
    button_panel: ui::ButtonPanel,
}

#[allow(dead_code)]
struct RendererInfo {
    window: WindowId,
    monitor_handle: MonitorHandle,
    monitor_bounds: ScreenRect,
    size_vec2: Vec2,
    transform: TransformUnit,
    is_primary: bool,
    ready: bool,
    scale_factor: f64,
}

struct DesktopWindowInfo {
    title: String,
    window_bounds: ScreenRect,
    capture: Option<RgbaImage>,
}

impl Model {
    fn is_all_ready(&self) -> bool {
        self.renderers.iter().all(|r| r.ready)
    }

    fn show_all(&mut self, app: &App) {
        self.renderers.iter_mut().for_each(|r| {
            let window = app.window(r.window).unwrap();
            window.set_fullscreen_with(Some(Fullscreen::Borderless(Some(r.monitor_handle.clone()))));
            window.set_visible(true);
            if r.is_primary {
                window.winit_window().focus_window();
            }
        });
    }

    fn set_cursor(&mut self, app: &App, cursor: Option<CursorIcon>) {
        self.renderers.iter_mut().for_each(|r| {
            let window = app.window(r.window).unwrap();
            if let Some(cur) = cursor {
                window.set_cursor_visible(true);
                window.set_cursor_icon(cur);
            } else {
                window.set_cursor_visible(false);
            }
        });
    }

    fn set_anchored(&mut self, anchored: bool) {
        if anchored && !self.mouse_anchored {
            self.mouse_anchored = true;
            let _ = self
                .mouse
                .move_to(self.mouse_anchor_pt.x, self.mouse_anchor_pt.y);
        } else if !anchored && self.mouse_anchored {
            self.mouse_anchored = false;
            let pt = self.mouse_pt.to_i32();
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

    fn handle_mouse_move(&mut self, app: &App, pt: ScreenPointF) {
        if self.mouse_anchored {
            let relative_anchor = self.mouse_anchor_pt - self.desktop_virtual_origin.to_vector();
            if relative_anchor != pt.to_i32() {
                let anchor_f = relative_anchor.to_f64();
                let x_delta = (pt.x - anchor_f.x) / self.zoom;
                let y_delta = (pt.y - anchor_f.y) / self.zoom;

                let mut mx = self.mouse_pt.x + x_delta;
                let mut my = self.mouse_pt.y + y_delta;

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

                self.mouse_pt = ScreenPointF::new(mx, my);
                let _ = self
                    .mouse
                    .move_to(self.mouse_anchor_pt.x, self.mouse_anchor_pt.y);
            }
        } else {
            self.mouse_pt = pt;
        }

        let pt = self.mouse_pt;
        match self.mouse_state {
            MouseState::Up => (),
            MouseState::StartSelection(start) => {
                let dist = start.distance_to(pt);
                let drag_threshold = 10.0 / self.zoom;
                if dist > drag_threshold {
                    self.mouse_state = MouseState::MakingSelection(start);
                    self.selection = Some(ScreenRect::from_rounded_threshold(start.x, start.y, pt.x, pt.y))
                }
            }
            MouseState::MakingSelection(start) => self.selection = Some(ScreenRect::from_rounded_threshold(start.x, start.y, pt.x, pt.y)),
            MouseState::MovingSelection(orig_rect, orig_point) => {
                let dx = (pt.x - orig_point.x) as i32;
                let dy = (pt.y - orig_point.y) as i32;
                let x1 = orig_rect.min_x() + dx;
                let y1 = orig_rect.min_y() + dy;
                let x2 = orig_rect.max_x() + dx;
                let y2 = orig_rect.max_y() + dy;
                self.selection = Some(ScreenRect::from_exact(x1, y1, x2, y2));
                self.update_buttons();
            }
            MouseState::SizingSelection(hit) => {
                if let Some(selection) = self.selection {
                    let rect = hit.resize_rect(pt, selection);
                    self.selection = Some(rect);
                }
                self.update_buttons();
            }
        }

        if self.captured {
            self.set_cursor(app, Some(self.button_panel.hit_test(pt).to_cursor()));
        }
    }

    fn handle_mouse_down(&mut self, pt: ScreenPointF) {
        if self.captured {
            let hit = self.button_panel.hit_test(pt);
            if hit.is_size_handle() {
                self.mouse_state = MouseState::SizingSelection(hit);
            } else if hit == HitTest::Content {
                self.mouse_state = MouseState::MovingSelection(self.selection.unwrap(), pt);
            }
        } else {
            self.mouse_state = MouseState::StartSelection(pt);
        }
    }

    fn handle_mouse_up(&mut self, pt: ScreenPointF) {
        match self.mouse_state {
            MouseState::StartSelection(_) => {
                self.selection = None;
            }
            MouseState::MakingSelection(start) => {
                self.zoom = 1.0;
                self.set_anchored(false);
                self.captured = true;
                self.selection = Some(ScreenRect::from_rounded_threshold(start.x, start.y, pt.x, pt.y));
                self.update_buttons();
            }
            _ => (),
        }
        self.mouse_state = MouseState::Up;
    }

    fn update_buttons(&mut self) {
        if let Some(selection) = self.selection {
            let renderer = self.get_nearest_renderer(selection.center().to_f64());
            self.button_panel
                .update(renderer.monitor_bounds, renderer.scale_factor, selection);
        }
    }
}

fn create_model(app: &App) -> Result<Model> {
    let sw = simple_stopwatch::Stopwatch::start_new();
    info!("[TIME] Start: {:?}", Duration::from_millis(sw.ms() as u64));

    let mut renderers = Vec::new();
    let mut desktop_windows = Vec::new();

    event_handler::init_event_handler(handle_event);

    let monitors = app.available_monitors();
    let windows = XCapWindow::all()?;

    let primary = app.primary_monitor().unwrap();
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

    for (i, monitor) in monitors.iter().enumerate() {
        let position = monitor.position();
        let size = monitor.size();

        let texture_size_limit = 8192
            .max(desktop_bounds.width() as u32)
            .max(desktop_bounds.height() as u32);

        let mut descriptor = default_device_descriptor();
        descriptor.limits.max_texture_dimension_1d = texture_size_limit;
        descriptor.limits.max_texture_dimension_2d = texture_size_limit;
        // descriptor.limits.max_texture_dimension_3d = texture_size_limit;

        let surface_config = window::SurfaceConfigurationBuilder::new().present_mode(wgpu::PresentMode::Immediate);

        // Try to create a new window and handle errors.
        let window = app
            .new_window()
            .window(WindowBuilder::new().with_visible(false))
            .surface_conf_builder(surface_config)
            .clear_color(color::rgb(0u8, 0u8, 0u8))
            .title("Clowd Capture")
            .event(event_handler::get_event(i))
            .device_descriptor(descriptor)
            .view(view)
            .build()
            .map_err(|e| anyhow!("{:?}", e))?;

        let monitor_bounds = ScreenRect::from_xy_size(position.x, position.y, size.width as i32, size.height as i32);
        let monitor_bounds = vd_transform.outer_transformed_rect(&monitor_bounds);

        renderers.push(RendererInfo {
            window,
            monitor_handle: monitor.clone(),
            monitor_bounds,
            // cartesian_bounds: Rect::from_w_h(monitor_bounds.width() as f32, monitor_bounds.height() as f32),
            size_vec2: Vec2::new(monitor_bounds.width() as f32, monitor_bounds.height() as f32),
            transform: TransformUnit::new(monitor_bounds, monitor.scale_factor()),
            ready: false,
            scale_factor: monitor.scale_factor(),
            is_primary: monitor_bounds.contains(mouse_anchor_pt),
        });

        info!("[TIME] Monitor Build: {:?}", Duration::from_millis(sw.ms() as u64));
    }

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

    info!("[TIME] Windows Captured: {:?}", Duration::from_millis(sw.ms() as u64));

    let bw_buf = ImageBuffer::from_fn(2, 2, |x, _y| {
        if x == 0 {
            image::Rgba([255, 255, 255, 255])
        } else {
            image::Rgba([0, 0, 0, 255])
        }
    });
    let bw_img = image::DynamicImage::ImageRgba8(bw_buf);
    let bw_tex = wgpu::Texture::from_image(app, &bw_img);

    let desktop_color_texture = Texture::from_image(app, &desktop_color_image);
    let desktop_gray_texture = Texture::from_image(app, &desktop_gray_image);

    info!("[TIME] Textures Loaded: {:?}", Duration::from_millis(sw.ms() as u64));
    info!("[TIME] Done: {:?}", Duration::from_millis(sw.ms() as u64));

    Ok(Model {
        renderers,
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
        accent_light: rgb(0.0, 175.0 / 255.0, 240.0 / 255.0),
        accent_dark: rgb(0.0, 125.0 / 255.0, 180.0 / 255.0),
        dash_black_white: bw_tex,
        selection: None,
        captured: false,
        mouse_pt: ScreenPointF::zero(),
        mouse_state: MouseState::Up,
        mouse_anchor_pt,
        mouse_anchored: false,
        mouse: Mouse::new(),
        button_panel: ui::ButtonPanel::new(),
    })
}

fn model(app: &App) -> Model {
    match create_model(app) {
        Ok(model) => model,
        Err(e) => {
            eprintln!("Fatal Error: {:?}", e);
            std::process::exit(1);
        }
    }
}

fn update(_app: &App, _model: &mut Model, _update: Update) {
    // println!("update: {:?}", _update);
}

fn handle_event(app: &App, model: &mut Model, event: WindowEvent, idx: usize) {
    let renderer = &mut model.renderers[idx];
    renderer.ready = true;

    if model.is_all_ready() && !model.shown {
        model.show_all(app);
        model.shown = true;
    }

    if let WindowEvent::KeyPressed(key_pressed) = event {
        match key_pressed {
            Key::D => model.debug = !model.debug,
            Key::R => {
                model.zoom = 1.0;
                model.set_anchored(false);
                model.captured = false;
                model.selection = None;
            }
            Key::Escape => app.quit(),
            _ => (),
        }
    } else if let WindowEvent::MouseMoved(pt) = event {
        let renderer = &model.renderers[idx];
        let transform = renderer.transform.with_logical_units();
        let pt = transform.pt_to_screen(pt.to_window_point());
        model.handle_mouse_move(app, pt);
    } else if let WindowEvent::MousePressed(button) = event {
        if button == MouseButton::Left {
            model.handle_mouse_down(model.mouse_pt);
        }
    } else if let WindowEvent::MouseReleased(button) = event {
        if button == MouseButton::Left {
            model.handle_mouse_up(model.mouse_pt);
        }
    } else if let WindowEvent::MouseWheel(scroll_delta, _) = event {
        if !model.captured {
            let delta = match scroll_delta {
                MouseScrollDelta::LineDelta(_, y) => y,
                MouseScrollDelta::PixelDelta(pt) => pt.y as f32,
            };

            let mut zoom = model.zoom;

            if app.keys.mods.shift() || app.keys.mods.ctrl() {
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
            model.set_anchored(zoom > 1.0);
        }
    }
}

fn view(app: &App, model: &Model, frame: Frame) {
    let desc = SamplerBuilder::new()
        .mag_filter(wgpu::FilterMode::Nearest)
        .min_filter(wgpu::FilterMode::Nearest)
        .mipmap_filter(wgpu::FilterMode::Nearest)
        .into_descriptor();

    let window = app.window(frame.window_id()).unwrap();
    let draw = app
        .draw()
        .scale(1.0 / window.scale_factor())
        .sampler(desc);

    let renderer = model
        .renderers
        .iter()
        .find(|r| r.window == frame.window_id())
        .unwrap();

    draw_texture(model, &draw, renderer, app.time);

    if !model.captured {
        draw_crosshair(model, &draw, renderer);
    }

    if model.debug {
        draw_debug(model, &draw, renderer);
    }

    if let Some(selection) = model.selection {
        if model.captured
            && renderer
                .monitor_bounds
                .contains(selection.center())
        {
            model.button_panel.draw(&draw, renderer);
        }
    }

    draw.to_frame(app, &frame).unwrap();
}

fn draw_texture(model: &Model, draw: &Draw, renderer: &RendererInfo, time: f32) {
    let cursor_pos = renderer
        .transform
        .pt_to_window(model.mouse_pt)
        .to_nannou();

    let zoom_f32 = model.zoom as f32;

    let monitor_center = renderer.monitor_bounds.center().to_f32();
    let desktop_center = model.desktop_bounds.center().to_f32();
    let x_diff = desktop_center.x - monitor_center.x;
    let y_diff = -(desktop_center.y - monitor_center.y);

    let texture_draw = draw
        .x_y(x_diff * zoom_f32, y_diff * zoom_f32)
        .x_y(-cursor_pos.x * (zoom_f32 - 1.0), -cursor_pos.y * (zoom_f32 - 1.0))
        .scale(zoom_f32);

    texture_draw.texture(&model.desktop_gray_texture);

    draw.rect()
        .wh(renderer.size_vec2)
        .rgba(0.0, 0.0, 0.0, 0.5);

    if let Some(screen_selection) = model.selection {
        let zoom_transform = renderer
            .transform
            .with_zoom(model.mouse_pt, model.zoom);

        let scissor_transform = zoom_transform.with_scissor();

        let scissor_rect = scissor_transform
            .rect_to_window(screen_selection.to_f64())
            .to_nannou();

        let cropped_draw = texture_draw.scissor(scissor_rect);

        cropped_draw.texture(&model.desktop_color_texture);

        let pixel_size = renderer.scale_factor.floor() as f32;
        let outline_weight = pixel_size * 2.0;
        let outline_offset = if model.zoom < 1.5 {
            SideOffsets2D::new(1, 1, 1, 1)
        } else {
            SideOffsets2D::new(0, 0, 0, 0)
        };

        let outline_rect = zoom_transform.rect_to_window(
            screen_selection
                .outer_rect(outline_offset)
                .to_f64(),
        );

        draw_ex::draw_dashed_rectangle(
            &draw,
            outline_rect.to_nannou(),
            outline_weight,
            pixel_size * 20.0,
            WHITE,
            model.accent_dark,
            time,
        );

        let min_size_for_handles = (6.0 * pixel_size * 5.0) as i32;
        if model.captured && screen_selection.width() > min_size_for_handles && screen_selection.height() > min_size_for_handles {
            for handle in HitTest::resize_handles() {
                let pos = handle
                    .handle_position(screen_selection.outer_rect(outline_offset))
                    .to_f64();

                let pos = zoom_transform.pt_to_window(pos);

                let rect = pos
                    .to_widened_rect(6.0 * pixel_size as f64)
                    .to_nannou();
                draw.ellipse()
                    .xy(rect.xy())
                    .wh(rect.wh())
                    .color(model.accent_light);

                let rect = pos
                    .to_widened_rect(5.0 * pixel_size as f64)
                    .to_nannou();
                draw.ellipse()
                    .xy(rect.xy())
                    .wh(rect.wh())
                    .color(WHITE);

                let rect = pos
                    .to_widened_rect(4.0 * pixel_size as f64)
                    .to_nannou();
                draw.ellipse()
                    .xy(rect.xy())
                    .wh(rect.wh())
                    .color(model.accent_light);
            }
        }
    }
}

fn draw_crosshair(model: &Model, draw: &Draw, renderer: &RendererInfo) {
    let rc_win = renderer
        .transform
        .rect_to_window(renderer.monitor_bounds)
        .to_nannou();
    let mouse_pos = renderer
        .transform
        .pt_to_window(model.mouse_pt)
        .to_nannou();
    let mouse = mouse_pos - pt2(-0.5, 0.5);
    let mouse_dashed_horiz = (pt2(rc_win.left(), mouse.y), pt2(rc_win.right(), mouse.y));
    let mouse_dashed_vert = (pt2(mouse.x, rc_win.bottom()), pt2(mouse.x, rc_win.top()));

    draw_ex::draw_dashed_line_polyline(&draw, mouse_dashed_horiz.0, mouse_dashed_horiz.1, 1.0, 8.0, &model.dash_black_white);

    draw_ex::draw_dashed_line_polyline(&draw, mouse_dashed_vert.0, mouse_dashed_vert.1, 1.0, 8.0, &model.dash_black_white);

    let accent_size = 100.0;
    let accent_color = model.accent_light;
    let mouse_accent_horiz = (pt2(mouse.x - accent_size, mouse.y), pt2(mouse.x + accent_size, mouse.y));
    let mouse_accent_vert = (pt2(mouse.x, mouse.y - accent_size), pt2(mouse.x, mouse.y + accent_size));

    draw.line()
        .start(mouse_accent_horiz.0)
        .end(mouse_accent_horiz.1)
        .stroke_weight(1.0)
        .color(accent_color);

    draw.line()
        .start(mouse_accent_vert.0)
        .end(mouse_accent_vert.1)
        .stroke_weight(1.0)
        .color(accent_color);

    let handle_size = accent_size / 2.0;
    let handle_weight = 5.0;

    let mouse_handle_left = (pt2(mouse.x - handle_size - 0.5, mouse.y), pt2(mouse.x - accent_size - 0.5, mouse.y));
    let mouse_handle_right = (pt2(mouse.x + handle_size + 0.5, mouse.y), pt2(mouse.x + accent_size + 0.5, mouse.y));
    let mouse_handle_top = (pt2(mouse.x, mouse.y - handle_size - 0.5), pt2(mouse.x, mouse.y - accent_size - 0.5));
    let mouse_handle_bottom = (pt2(mouse.x, mouse.y + handle_size + 0.5), pt2(mouse.x, mouse.y + accent_size + 0.5));

    draw.line()
        .start(mouse_handle_left.0)
        .end(mouse_handle_left.1)
        .stroke_weight(handle_weight)
        .color(accent_color);

    draw.line()
        .start(mouse_handle_right.0)
        .end(mouse_handle_right.1)
        .stroke_weight(handle_weight)
        .color(accent_color);

    draw.line()
        .start(mouse_handle_top.0)
        .end(mouse_handle_top.1)
        .stroke_weight(handle_weight)
        .color(accent_color);

    draw.line()
        .start(mouse_handle_bottom.0)
        .end(mouse_handle_bottom.1)
        .stroke_weight(handle_weight)
        .color(accent_color);
}

fn draw_debug(model: &Model, draw: &Draw, renderer: &RendererInfo) {
    let win = renderer
        .transform
        .rect_to_window(renderer.monitor_bounds)
        .to_nannou();

    // Crosshair at window center
    let crosshair_color = rgba(1.0, 1.0, 1.0, 1.0);
    let ends = [win.mid_top(), win.mid_right(), win.mid_bottom(), win.mid_left()];
    for &end in &ends {
        draw.arrow()
            .weight(0.5)
            .start_cap_round()
            .head_length(16.0)
            .head_width(8.0)
            .color(crosshair_color)
            .end(end - vec2(-0.5, -0.5))
            .start(vec2(0.5, 0.5));
    }

    let top = format!("{:.1}", win.top());
    let bottom = format!("{:.1}", win.bottom());
    let left = format!("{:.1}", win.left());
    let right = format!("{:.1}", win.right());
    let x_off = 30.0;
    let y_off = 20.0;
    draw.text("0.0")
        .x_y(15.0, 15.0)
        .color(crosshair_color)
        .font_size(14);
    draw.text(&top)
        .h(win.h())
        .font_size(14)
        .align_text_top()
        .color(crosshair_color)
        .x(x_off);
    draw.text(&bottom)
        .h(win.h())
        .font_size(14)
        .align_text_bottom()
        .color(crosshair_color)
        .x(x_off);
    draw.text(&left)
        .w(win.w())
        .font_size(14)
        .left_justify()
        .color(crosshair_color)
        .y(y_off);
    draw.text(&right)
        .w(win.w())
        .font_size(14)
        .right_justify()
        .color(crosshair_color)
        .y(y_off);

    let mouse_pos = renderer
        .transform
        .pt_to_window(model.mouse_pt)
        .to_nannou();

    // Debug window and monitor details.
    let m_scale_factor = renderer.scale_factor;
    let mon_phys_size = renderer.monitor_handle.size();
    let mon_log_size = mon_phys_size.to_logical::<f32>(m_scale_factor as f64);
    let mon_phys_pos = renderer.monitor_handle.position();
    let mon_log_pos = mon_phys_pos.to_logical::<f32>(m_scale_factor as f64);
    let text = format!(
        "
        Monitor logical: [{:.0}, {:.0}, {:.0}, {:.0}]
        Monitor phsical: [{:.0}, {:.0}, {:.0}, {:.0}]
        Monitor ratio: {:.2}
        Monitor scale factor: {:.2}
        Monitor primary: {:?}
        World zoom: {:.2}
        World mouse: {:?}
        Mouse relative to window: {:.2}
        Artificial mouse: {:?}
        ",
        mon_log_pos.x,
        mon_log_pos.y,
        mon_log_size.width,
        mon_log_size.height,
        mon_phys_pos.x,
        mon_phys_pos.y,
        mon_phys_size.width,
        mon_phys_size.height,
        mon_log_size.width / mon_log_size.height,
        m_scale_factor,
        renderer.is_primary,
        model.zoom,
        model.mouse_pt,
        mouse_pos,
        model.mouse_anchored,
    );
    let pad = 6.0;
    draw.text(&text)
        .h(win.pad(pad).h())
        .w(win.pad(pad).w())
        .line_spacing(pad)
        .font_size(14)
        .align_text_bottom()
        .color(crosshair_color)
        .left_justify();

    // Ellipse at mouse.
    draw.ellipse()
        .wh([5.0; 2].into())
        .xy(mouse_pos);

    // Mouse position text.
    let pos = format!("[{:.1}, {:.1}]", mouse_pos.x, mouse_pos.y);
    draw.text(&pos)
        .xy(mouse_pos + vec2(0.0, 20.0))
        .font_size(14)
        .color(WHITE);
}
