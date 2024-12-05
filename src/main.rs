mod event_handler;

use anyhow::{anyhow, Result};
use nannou::{
    color::{self},
    image::{self, DynamicImage, ImageBuffer, RgbaImage},
    prelude::*,
    winit::{
        dpi::{PhysicalPosition, PhysicalSize},
        monitor::MonitorHandle,
        window::WindowBuilder,
    },
};
use wgpu::{SamplerBuilder, Texture, WithDeviceQueuePair};
use xcap::{Monitor as XCapMonitor, Window as XCapWindow};

fn main() {
    nannou::app(model)
        .loop_mode(LoopMode::RefreshSync)
        .update(update)
        .run();
}

struct Model {
    renderers: Vec<RendererInfo>,
    windows: Vec<DesktopWindowInfo>,
    shown: bool,
    debug: bool,
    zoom: f32,
    accent_light: Rgb,
    accent_dark: Rgb,
    dash_black_white: Texture,
    dash_white_accent: Texture,
}

#[allow(dead_code)]
struct RendererInfo {
    pub window: WindowId,
    pub monitor: MonitorHandle,
    pub color_image: DynamicImage,
    pub color_texture: wgpu::Texture,
    pub gray_image: DynamicImage,
    pub gray_texture: wgpu::Texture,
    pub position: PhysicalPosition<i32>,
    pub size: PhysicalSize<u32>,
    pub ready: bool,
}

struct DesktopWindowInfo {
    pub title: String,
    pub position: PhysicalPosition<i32>,
    pub size: PhysicalSize<u32>,
    pub capture: Option<RgbaImage>,
}

impl Model {
    fn is_all_ready(&self) -> bool {
        self.renderers.iter().all(|r| r.ready)
    }

    fn show_all(&mut self, app: &App) {
        self.renderers.iter_mut().for_each(|r| {
            let window = app.window(r.window).unwrap();
            window.set_fullscreen_with(Some(Fullscreen::Borderless(Some(r.monitor.clone()))));
            window.set_visible(true);
        });
    }
}

fn convert_image<T>(buffer: xcap::image::ImageBuffer<xcap::image::Rgba<u8>, T>) -> nannou::image::ImageBuffer<nannou::image::Rgba<u8>, T>
where
    T: std::ops::Deref<Target = [u8]> + std::ops::DerefMut<Target = [u8]> + 'static,
{
    nannou::image::ImageBuffer::from_raw(buffer.width(), buffer.height(), buffer.into_raw()).expect("Conversion failed")
}

fn create_model(app: &App) -> Result<Model> {
    let mut renderers = Vec::new();
    let mut desktop_windows = Vec::new();

    event_handler::init_event_handler(handle_event);

    let monitors = app.available_monitors();
    let windows = XCapWindow::all()?;

    for (i, monitor) in monitors.iter().enumerate() {
        let position = monitor.position();
        let size = monitor.size();

        // Try to create a monitor capturer and handle errors.
        let monitor_capturer = XCapMonitor::from_point(position.x + 10, position.y + 10)?;
        let capture: RgbaImage = convert_image(monitor_capturer.capture_image()?);
        let color_image = DynamicImage::ImageRgba8(capture);

        // TODO optimise this, can we just use a shader?
        let gray_image_intermediate = DynamicImage::ImageLuma8(color_image.to_luma8());
        let gray_image = DynamicImage::ImageRgba8(gray_image_intermediate.to_rgba8());

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

        let window_handle = app.window(window).unwrap();
        let (color_texture, gray_texture) = window_handle.with_device_queue_pair(|device, queue| {
            let usage = wgpu::TextureUsages::COPY_SRC
                | wgpu::TextureUsages::COPY_DST
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::RENDER_ATTACHMENT;
            // let usage = wgpu::TextureUsages::all();
            (
                wgpu::Texture::load_from_image(device, queue, usage, &color_image),
                wgpu::Texture::load_from_image(device, queue, usage, &gray_image),
            )
        });

        // println!("color_texture: {:?}", color_texture.sample_type());
        // println!("gray_texture: {:?}", gray_texture.sample_type());
        // panic!();

        // let color_texture = wgpu::Texture::from_image(&window_handle, &color_image);
        // let gray_texture = wgpu::Texture::load_from_image(&window_handle, &gray_image);

        renderers.push(RendererInfo {
            window,
            monitor: monitor.clone(),
            color_image,
            color_texture,
            gray_image,
            gray_texture,
            position,
            size,
            ready: false,
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

    Ok(Model {
        renderers,
        windows: desktop_windows,
        shown: false,
        debug: false,
        zoom: 1.0,
        accent_light: rgb(0.0, 175.0 / 255.0, 240.0 / 255.0),
        accent_dark: rgb(0.0, 125.0 / 255.0, 180.0 / 255.0),
        dash_black_white: bw_tex,
        dash_white_accent: aw_tex,
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
    println!("update: {:?}", _update);
}

fn handle_event(app: &App, model: &mut Model, event: WindowEvent, idx: usize) {
    println!("window a: {:?}, {:?}", event, idx);
    // let window = _app.window(_model.window).unwrap();
    // window.set_fullscreen(true);
    // window.set_visible(true);

    // app.window(model.)
    // app.main_window()
    // model.show_all(app);

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
    }
}

fn draw_dashed_line_polyline(draw: &Draw, start: Vec2, end: Vec2, weight: f32, dash_length: f32, texture: &Texture, time: f32) {
    let total_distance = start.distance(end);
    let direction = (end - start).normalize();

    let dash_offset = (time * 50.0) % (dash_length * 2.0);

    let mut current_distance = dash_offset;
    let mut points_colored = Vec::new();
    let mut toggle = true;

    while current_distance < total_distance {
        let dash_start = start + direction * current_distance;
        let dash_end = start + direction * (current_distance + dash_length).min(total_distance);

        let texture_coords = if toggle { [0.0, 0.0] } else { [1.0, 1.0] };
        points_colored.push((dash_start, texture_coords));
        points_colored.push((dash_end, texture_coords));

        toggle = !toggle;
        current_distance += dash_length;
    }

    draw.polyline()
        .weight(weight)
        .points_textured(&texture, points_colored);
}

fn draw_dashed_rectangle(draw: &Draw, rect: Rect, weight: f32, dash_length: f32, texture: &Texture, time: f32) {
    let draw = draw.scissor(rect.pad(-1.0));

    // let mut points_colored = Vec::new();
    // let mut toggle = true;

    let x = vec2(dash_length * 4.0, 0.0);
    let y = vec2(0.0, dash_length * 4.0);

    draw_dashed_line_polyline(&draw, rect.top_left() - x, rect.top_right() + x, weight, dash_length, texture, time);
    draw_dashed_line_polyline(&draw, rect.top_right() + y, rect.bottom_right() - y, weight, dash_length, texture, time);
    draw_dashed_line_polyline(&draw, rect.bottom_right() + x, rect.bottom_left() - x, weight, dash_length, texture, time);
    draw_dashed_line_polyline(&draw, rect.bottom_left() - y, rect.top_left() + y, weight, dash_length, texture, time);

    draw.line()
        .start(rect.top_left() + vec2(-1.0, 1.0))
        .end(rect.top_left())
        .weight(weight)
        .color(BLACK);

    draw.line()
        .start(rect.top_right() + vec2(1.0, 1.0))
        .end(rect.top_right())
        .weight(weight)
        .color(BLACK);

    draw.line()
        .start(rect.bottom_right() + vec2(1.0, -1.0))
        .end(rect.bottom_right())
        .weight(weight)
        .color(BLACK);

    draw.line()
        .start(rect.bottom_left() + vec2(-1.0, -1.0))
        .end(rect.bottom_left())
        .weight(weight)
        .color(BLACK);

    // let mut do_side = |start: Vec2, end: Vec2| {
    //     let mut current_distance = dash_offset;
    //     let total_distance = start.distance(end);
    //     let direction = (end - start).normalize();

    //     while current_distance < total_distance {
    //         let dash_start = start + direction * current_distance;
    //         let dash_end = start + direction * (current_distance + dash_length).min(total_distance);

    //         let texture_coords = if toggle { [0.0, 0.0] } else { [1.0, 1.0] };
    //         points_colored.push((dash_start, texture_coords));
    //         points_colored.push((dash_end, texture_coords));

    //         toggle = !toggle;
    //         current_distance += dash_length * 1.5; // Include the gap length
    //     }
    // };

    // do_side(rect.top_left(), rect.top_right());
    // do_side(rect.top_right(), rect.bottom_right());
    // do_side(rect.bottom_right(), rect.bottom_left());
    // do_side(rect.bottom_left(), rect.top_left());

    // draw.polyline()
    //     .weight(weight)
    //     .start_cap_round()
    //     .end_cap_round()
    //     .points_textured(&texture, points_colored);
}

fn view(app: &App, model: &Model, frame: Frame) {
    let desc = SamplerBuilder::new()
        .mag_filter(wgpu::FilterMode::Nearest)
        .min_filter(wgpu::FilterMode::Nearest)
        .mipmap_filter(wgpu::FilterMode::Nearest)
        .into_descriptor();

    let draw = app.draw().sampler(desc);
    let window = app.window(frame.window_id()).unwrap();

    let renderer = model
        .renderers
        .iter()
        .find(|r| r.window == frame.window_id())
        .unwrap();
    let win = window.rect();

    let cursor_pos = app.mouse.position();
    let zoom = model.zoom;

    let texture_draw = draw
        .x_y(-cursor_pos.x * (zoom - 1.0), -cursor_pos.y * (zoom - 1.0))
        .scale(zoom);

    texture_draw.texture(&renderer.gray_texture);
    texture_draw
        .rect()
        .wh(win.wh())
        .rgba(0.0, 0.0, 0.0, 0.5);

    let selection = Rect::from_x_y_w_h(0.0, 0.0, 500.0, 500.0);
    let cropped_draw = draw.scissor(selection);
    // .x_y(-cursor_pos.x * (zoom - 1.0), -cursor_pos.y * (zoom - 1.0))
    // .scale(zoom);

    cropped_draw.texture(&renderer.color_texture);

    // frame.command_encoder()
    // draw.path().stroke().points_textured(view, points)

    draw_dashed_rectangle(&draw, selection, 2.0, 20.0, &model.dash_white_accent, app.time);

    draw_crosshair(app, model, win, &draw);

    if model.debug {
        draw_debug(app, model, &draw, window);
    }

    draw.to_frame(app, &frame).unwrap();
}

fn draw_debug(app: &App, model: &Model, draw: &Draw, window: std::cell::Ref<'_, Window>) {
    let win = window.rect();

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
    if let Some(monitor) = window.current_monitor() {
        let w_scale_factor = window.scale_factor();
        let m_scale_factor = monitor.scale_factor();
        let mon_phys = monitor.size();
        let mon = mon_phys.to_logical(w_scale_factor as f64);
        let mon_w: f32 = mon.width;
        let mon_h: f32 = mon.height;
        let text = format!(
            "
            Window size: [{:.0}, {:.0}]
            Window ratio: {:.2}
            Window scale factor: {:.2}
            Monitor size: [{:.0}, {:.0}]
            Monitor ratio: {:.2}
            Monitor scale factor: {:.2}
            World Zoom: {:.2}
            ",
            win.w(),
            win.h(),
            win.w() / win.h(),
            w_scale_factor,
            mon_w,
            mon_h,
            mon_w / mon_h,
            m_scale_factor,
            model.zoom
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
    }

    // Ellipse at mouse.
    draw.ellipse()
        .wh([5.0; 2].into())
        .xy(app.mouse.position());

    // Mouse position text.
    let mouse = app.mouse.position();
    let pos = format!("[{:.1}, {:.1}]", mouse.x, mouse.y);
    draw.text(&pos)
        .xy(mouse + vec2(0.0, 20.0))
        .font_size(14)
        .color(WHITE);
}

fn draw_crosshair(app: &App, model: &Model, win: Rect, draw: &Draw) {
    let mouse = app.mouse.position() - pt2(-0.5, 0.5);
    let mouse_dashed_horiz = (pt2(win.left(), mouse.y), pt2(win.right(), mouse.y));
    let mouse_dashed_vert = (pt2(mouse.x, win.bottom()), pt2(mouse.x, win.top()));

    draw_dashed_line_polyline(&draw, mouse_dashed_horiz.0, mouse_dashed_horiz.1, 1.0, 8.0, &model.dash_black_white, 0.0);

    draw_dashed_line_polyline(&draw, mouse_dashed_vert.0, mouse_dashed_vert.1, 1.0, 8.0, &model.dash_black_white, 0.0);

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

// fn draw_grid(draw: &Draw, win: &Rect, step: f32, weight: f32) {
//     let step_by = || (0..).map(|i| i as f32 * step);
//     let r_iter = step_by().take_while(|&f| f < win.right());
//     let l_iter = step_by()
//         .map(|f| -f)
//         .take_while(|&f| f > win.left());
//     let x_iter = r_iter.chain(l_iter);
//     for x in x_iter {
//         draw.line()
//             .weight(weight)
//             .points(pt2(x, win.bottom()), pt2(x, win.top()));
//     }
//     let t_iter = step_by().take_while(|&f| f < win.top());
//     let b_iter = step_by()
//         .map(|f| -f)
//         .take_while(|&f| f > win.bottom());
//     let y_iter = t_iter.chain(b_iter);
//     for y in y_iter {
//         draw.line()
//             .weight(weight)
//             .points(pt2(win.left(), y), pt2(win.right(), y));
//     }
// }
