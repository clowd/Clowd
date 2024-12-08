mod event_handler;
mod screenshot;
mod util;

use anyhow::{anyhow, Result};
use bracket_geometry::prelude::{Point as BgPoint, PointF as BgPointF, Rect as BgRect, RectF as BgRectF};
use mouse_rs::Mouse;
use nannou::{
    color::{self},
    image::{self, DynamicImage, ImageBuffer, RgbaImage},
    prelude::*,
    winit::{
        dpi::{LogicalPosition, PhysicalPosition, PhysicalSize},
        monitor::MonitorHandle,
        window::WindowBuilder,
    },
};
use screenshot::capture_desktop;
use util::*;
use wgpu::{SamplerBuilder, Texture};
use xcap::Window as XCapWindow;

#[macro_use]
extern crate log;

#[macro_use]
extern crate anyhow;

fn main() {
    nannou::app(model)
        .loop_mode(LoopMode::RefreshSync)
        .update(update)
        .run();
}

enum MouseState {
    Up,
    StartSelection(BgPointF),
    MakingSelection(BgPointF),
}

#[allow(dead_code)]
struct Model {
    renderers: Vec<RendererInfo>,
    desktop_bounds: BgRect,
    desktop_color_texture: wgpu::Texture,
    desktop_gray_texture: wgpu::Texture,
    desktop_color_image: DynamicImage,
    desktop_gray_image: DynamicImage,
    windows: Vec<DesktopWindowInfo>,
    shown: bool,
    debug: bool,
    zoom: f32,
    accent_light: Rgb,
    accent_dark: Rgb,
    dash_black_white: Texture,
    dash_white_accent: Texture,
    selection: Option<BgRect>,
    captured: bool,
    mouse_pt: BgPointF,
    mouse_state: MouseState,
    mouse_anchor_pt: BgPoint,
    mouse_anchored: bool,
    mouse: Mouse,
}

#[allow(dead_code)]
struct RendererInfo {
    window: WindowId,
    monitor_handle: MonitorHandle,
    monitor_bounds: BgRectF,
    is_primary: bool,
    ready: bool,
    scale_factor: f64,
}

struct DesktopWindowInfo {
    title: String,
    position: PhysicalPosition<i32>,
    size: PhysicalSize<u32>,
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

    fn set_anchored(&mut self, anchored: bool) {
        if anchored && !self.mouse_anchored {
            self.mouse_anchored = true;
            let _ = self
                .mouse
                .move_to(self.mouse_anchor_pt.x, self.mouse_anchor_pt.y);
        } else if !anchored && self.mouse_anchored {
            self.mouse_anchored = false;
            let _ = self
                .mouse
                .move_to(self.mouse_pt.x as i32, self.mouse_pt.y as i32);
        }
    }

    fn get_nearest_renderer(&self, pt: BgPointF) -> &RendererInfo {
        self.renderers
            .iter()
            .find(|r| r.monitor_bounds.point_in_rect(pt))
            .or_else(|| {
                self.renderers.iter().min_by(|a, b| {
                    let a_dist = distance2d_pythagoras_squared_f(a.monitor_bounds.center(), pt);
                    let b_dist = distance2d_pythagoras_squared_f(b.monitor_bounds.center(), pt);
                    a_dist.partial_cmp(&b_dist).unwrap()
                })
            })
            .unwrap()
    }

    fn handle_mouse_move(&mut self, pt: BgPointF) {
        if self.mouse_anchored {
            if self.mouse_anchor_pt != BgPoint::new(pt.x as i32, pt.y as i32) {
                let x_delta = (pt.x - self.mouse_anchor_pt.x as f32) / self.zoom;
                let y_delta = (pt.y - self.mouse_anchor_pt.y as f32) / self.zoom;

                let mut mx = self.mouse_pt.x + x_delta;
                let mut my = self.mouse_pt.y + y_delta;

                let bounds = self
                    .get_nearest_renderer(BgPointF::new(mx, my))
                    .monitor_bounds;

                // clip cursor to nearest monitor
                let left = bounds.x1;
                let right = bounds.x2;
                let top = bounds.y1;
                let bottom = bounds.y2;

                mx = mx.max(left).min(right - 0.001);
                my = my.max(top).min(bottom - 0.001);

                self.mouse_pt = BgPointF::new(mx, my);
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
                let dist = distance2d_diagonal_f(start, pt);
                let drag_threshold = 10.0 / self.zoom;
                if dist > drag_threshold {
                    self.mouse_state = MouseState::MakingSelection(start);
                    let (x1, y1, x2, y2) = round_px_selection(start.x as f64, start.y as f64, pt.x as f64, pt.y as f64);
                    self.selection = Some(BgRect::with_exact(x1, y1, x2, y2))
                }
            }
            MouseState::MakingSelection(start) => {
                let (x1, y1, x2, y2) = round_px_selection(start.x as f64, start.y as f64, pt.x as f64, pt.y as f64);
                self.selection = Some(BgRect::with_exact(x1, y1, x2, y2))
            }
            _ => (),
        }
    }

    fn handle_mouse_down(&mut self, pt: BgPointF) {
        if self.captured {
            // TODO
        } else {
            self.mouse_state = MouseState::StartSelection(pt);
        }
    }

    fn handle_mouse_up(&mut self, pt: BgPointF) {
        match self.mouse_state {
            MouseState::StartSelection(_) => {
                self.selection = None;
            }
            MouseState::MakingSelection(start) => {
                self.zoom = 1.0;
                self.set_anchored(false);
                self.captured = true;
                let (x1, y1, x2, y2) = round_px_selection(start.x as f64, start.y as f64, pt.x as f64, pt.y as f64);
                self.selection = Some(BgRect::with_exact(x1, y1, x2, y2))
            }
            _ => (),
        }
        self.mouse_state = MouseState::Up;
    }
}

impl RendererInfo {
    fn cartesian_bounds(&self) -> Rect {
        Rect::from_w_h(self.monitor_bounds.width() as f32, self.monitor_bounds.height() as f32)
    }

    // fn window_pt_to_screen(&self, app: &App, pt: Vec2) -> Vec2 {
    //     let win = app.window(self.window).unwrap().rect();
    //     let win_w = win.w() as f64;
    //     let win_h = win.h() as f64;
    //     let reverse_tx = |x: f32| x as f64 + win_w / 2.0;
    //     let reverse_ty = |y: f32| -(y as f64) + win_h / 2.0;
    //     let monitor_pos = self.monitor.position();
    //     let x = reverse_tx(pt.x) + monitor_pos.x as f64;
    //     let y = reverse_ty(pt.y) + monitor_pos.y as f64;
    //     vec2(x as f32, y as f32)
    // }

    fn logical_pt_to_screen(&self, app: &App, pt: Vec2) -> BgPointF {
        let win = app.window(self.window).unwrap().rect();
        let win_w = win.w() as f64;
        let win_h = win.h() as f64;
        let reverse_tx = |x: f32| x as f64 + win_w / 2.0;
        let reverse_ty = |y: f32| -(y as f64) + win_h / 2.0;
        let x = reverse_tx(pt.x);
        let y = reverse_ty(pt.y);
        let logical = LogicalPosition::new(x, y);
        let physical: PhysicalPosition<f32> = logical.to_physical(self.scale_factor);
        let monitor_pos = self.monitor_bounds;
        let x = physical.x + monitor_pos.x1 as f32;
        let y = physical.y + monitor_pos.y1 as f32;
        BgPointF::new(x, y)
    }

    fn screen_pt_to_window(&self, pt: BgPointF) -> Vec2 {
        let monitor_pos = self.monitor_bounds;
        let (win_w, win_h) = (self.monitor_bounds.width() as f32, self.monitor_bounds.height() as f32);
        let x = pt.x - monitor_pos.x1 as f32;
        let y = pt.y - monitor_pos.y1 as f32;
        let tx = |x: f32| (x - win_w / 2.0) as f32;
        let ty = |y: f32| (-(y - win_h / 2.0)) as f32;
        let x = tx(x);
        let y = ty(y);
        vec2(x as f32, y as f32)
    }

    // fn screen_pt_to_logical(&self, app: &App, pt: PhysicalPosition<i32>) -> Vec2 {
    //     let window = app.window(self.window).unwrap();
    //     let win = window.rect();
    //     let win_w = win.w() as f64;
    //     let win_h = win.h() as f64;
    //     let tx = |x: f64| (x - win_w / 2.0) as f32;
    //     let ty = |y: f64| (-(y - win_h / 2.0)) as f32;
    //     let (new_x, new_y) = pt
    //         .to_logical::<f64>(window.scale_factor().into())
    //         .into();
    //     let x = tx(new_x);
    //     let y = ty(new_y);
    //     vec2(x as f32, y as f32)
    // }
}

fn create_model(app: &App) -> Result<Model> {
    let mut renderers = Vec::new();
    let mut desktop_windows = Vec::new();

    event_handler::init_event_handler(handle_event);

    let monitors = app.available_monitors();
    let windows = XCapWindow::all()?;

    let primary = app.primary_monitor().unwrap();
    let primary_position = primary.position();
    let primary_size = primary.size();

    let mouse_anchor_pt = BgPoint::new(
        (primary_position.x as f64 + (primary_size.width as f64 / 2.0)) as i32,
        (primary_position.y as f64 + (primary_size.height as f64 / 2.0)) as i32,
    );

    let (desktop_bounds, desktop_capture) = capture_desktop()?;
    let desktop_color_image = DynamicImage::ImageRgba8(desktop_capture);

    // TODO optimise this, can we just use a shader?
    let gray_image_intermediate = DynamicImage::ImageLuma8(desktop_color_image.to_luma8());
    let desktop_gray_image = DynamicImage::ImageRgba8(gray_image_intermediate.to_rgba8());

    for (i, monitor) in monitors.iter().enumerate() {
        let position = monitor.position();
        let size = monitor.size();

        // Try to create a new window and handle errors.
        let window = app
            .new_window()
            .window(WindowBuilder::new().with_visible(false))
            .surface_conf_builder(window::SurfaceConfigurationBuilder::new().present_mode(wgpu::PresentMode::AutoNoVsync))
            .clear_color(color::rgb(0u8, 0u8, 0u8))
            .title("Clowd Capture")
            .event(event_handler::get_event(i))
            .view(view)
            .build()
            .map_err(|e| anyhow!("{:?}", e))?;

        let monitor_bounds = BgRectF::with_size(position.x as f32, position.y as f32, size.width as f32, size.height as f32);

        renderers.push(RendererInfo {
            window,
            monitor_handle: monitor.clone(),
            monitor_bounds,
            ready: false,
            scale_factor: monitor.scale_factor(),
            is_primary: monitor_bounds.point_in_rect(mouse_anchor_pt.to_vec2()),
        });
    }

    for window in windows {
        desktop_windows.push(DesktopWindowInfo {
            title: window.title().to_string(),
            position: PhysicalPosition { x: window.x(), y: window.y() },
            size: PhysicalSize { width: window.width(), height: window.height() },
            // capture: window.capture_image().ok(),
            capture: None,
        });
    }

    let bw_buf = ImageBuffer::from_fn(2, 2, |x, _y| if x == 0 { image::Rgba([255, 255, 255, 255]) } else { image::Rgba([0, 0, 0, 255]) });
    let bw_img = image::DynamicImage::ImageRgba8(bw_buf);
    let bw_tex = wgpu::Texture::from_image(app, &bw_img);

    let aw_buf =
        ImageBuffer::from_fn(2, 2, |x, _y| if x == 0 { image::Rgba([255, 255, 255, 255]) } else { image::Rgba([0, 125, 180, 255]) });
    let aw_img = image::DynamicImage::ImageRgba8(aw_buf);
    let aw_tex = wgpu::Texture::from_image(app, &aw_img);

    let desktop_color_texture = Texture::from_image(app, &desktop_color_image);
    let desktop_gray_texture = Texture::from_image(app, &desktop_gray_image);

    Ok(Model {
        renderers,
        desktop_bounds,
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
        dash_white_accent: aw_tex,
        selection: None,
        captured: false,
        mouse_pt: BgPointF::zero(),
        mouse_state: MouseState::Up,
        mouse_anchor_pt,
        mouse_anchored: false,
        mouse: Mouse::new(),
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
            Key::Escape => app.quit(),
            _ => (),
        }
    } else if let WindowEvent::MouseMoved(pt) = event {
        let renderer = &model.renderers[idx];
        let pt = renderer.logical_pt_to_screen(app, pt);
        model.handle_mouse_move(pt);
    } else if let WindowEvent::MousePressed(button) = event {
        if button == MouseButton::Left {
            model.handle_mouse_down(model.mouse_pt);
        }
    } else if let WindowEvent::MouseReleased(button) = event {
        if button == MouseButton::Left {
            model.handle_mouse_up(model.mouse_pt);
        }
    } else if let WindowEvent::MouseWheel(scroll_delta, _) = event {
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

    draw.to_frame(app, &frame).unwrap();
}

fn draw_texture(model: &Model, draw: &Draw, renderer: &RendererInfo, time: f32) {
    fn zoom_point(original_point: BgPointF, zoom_point: BgPointF, scale: f32) -> BgPointF {
        // Calculate the new point after applying the zoom transformation
        BgPointF::new(zoom_point.x + (original_point.x - zoom_point.x) * scale, zoom_point.y + (original_point.y - zoom_point.y) * scale)
    }

    let win = renderer.cartesian_bounds();
    let cursor_pos = renderer.screen_pt_to_window(model.mouse_pt);
    let zoom = model.zoom;

    let monitor_center = renderer.monitor_bounds.center();
    let desktop_center = model.desktop_bounds.center().to_vec2();
    let x_diff = desktop_center.x - monitor_center.x;
    let y_diff = desktop_center.y - monitor_center.y;

    let texture_draw = draw
        .x_y(x_diff * zoom, y_diff * zoom)
        .x_y(-cursor_pos.x * (zoom - 1.0), -cursor_pos.y * (zoom - 1.0))
        .scale(zoom);

    texture_draw.texture(&model.desktop_gray_texture);

    draw.rect()
        .wh(win.wh())
        .rgba(0.0, 0.0, 0.0, 0.5);

    if let Some(selection) = model.selection {
        let top_left = BgPointF::new(selection.x1 as f32, selection.y1 as f32);
        let bottom_right = BgPointF::new(selection.x2 as f32, selection.y2 as f32);
        let top_left = zoom_point(top_left, model.mouse_pt, zoom);
        let bottom_right = zoom_point(bottom_right, model.mouse_pt, zoom);

        let top_left = renderer.screen_pt_to_window(top_left);
        let bottom_right = renderer.screen_pt_to_window(bottom_right);

        let selection = Rect::from_corners(top_left, bottom_right);

        // not sure why I have to do this, but it works
        let flipped_top_left = pt2(top_left.x, -top_left.y);
        let flipped_bottom_right = pt2(bottom_right.x, -bottom_right.y);
        let flipped_selection = Rect::from_corners(flipped_top_left, flipped_bottom_right);

        let cropped_draw = texture_draw.scissor(flipped_selection);
        cropped_draw.texture(&model.desktop_color_texture);

        let outline_draw = draw.scissor(flipped_selection.pad(-2.0));
        util::draw_dashed_rectangle(&outline_draw, selection.pad(-2.0), 4.0, 20.0, &model.dash_white_accent, time);
    }
}

fn draw_crosshair(model: &Model, draw: &Draw, renderer: &RendererInfo) {
    let win = renderer.cartesian_bounds();
    let mouse_pos = renderer.screen_pt_to_window(model.mouse_pt);
    let mouse = mouse_pos - pt2(-0.5, 0.5);
    let mouse_dashed_horiz = (pt2(win.left(), mouse.y), pt2(win.right(), mouse.y));
    let mouse_dashed_vert = (pt2(mouse.x, win.bottom()), pt2(mouse.x, win.top()));

    util::draw_dashed_line_polyline(&draw, mouse_dashed_horiz.0, mouse_dashed_horiz.1, 1.0, 8.0, &model.dash_black_white, 0.0);

    util::draw_dashed_line_polyline(&draw, mouse_dashed_vert.0, mouse_dashed_vert.1, 1.0, 8.0, &model.dash_black_white, 0.0);

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
    let win = renderer.cartesian_bounds();

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
        renderer.screen_pt_to_window(model.mouse_pt),
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
    let mouse_pos = renderer.screen_pt_to_window(model.mouse_pt);
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
