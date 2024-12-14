use crate::{
    app::{RenderMessage, RendererDto, SharedModel, UserEvent},
    gpu::WindowSurface,
    simple_text::SimpleText,
    stats::{Sample, Stats},
};
use crate::{geometry::*, util};
use anyhow::Result;
use std::{
    sync::mpsc::Receiver,
    thread,
    time::{Duration, Instant},
};
use vello::{kurbo::*, peniko::*, Scene};

pub fn begin_render_loop<'s>(surface: WindowSurface<'s>, initial_model: SharedModel, info: RendererDto, bus: Receiver<RenderMessage>) {
    // This is incredibly unsafe, since if the window is closed, the surface will also be made invalid.
    let static_surface: WindowSurface<'static> = unsafe { std::mem::transmute(surface) };
    std::thread::spawn(move || {
        let mut surface = static_surface;
        let mut model = initial_model;
        let mut first_render = false;
        let mut renderer = surface.create_renderer();
        let mut scene = Scene::new();
        let mut stats = Stats::new();
        let mut text = SimpleText::new();
        let mut frame_start_time = Instant::now();
        let mut should_continue = true;

        info!("{:?} Starting render loop", info.window_id);

        while should_continue {
            loop {
                let msg = bus
                    .recv_timeout(Duration::from_millis(0))
                    .unwrap_or(RenderMessage::None);

                match msg {
                    RenderMessage::ModelUpdate(new_model) => {
                        model = new_model;
                    }
                    RenderMessage::Resize((width, height)) => {
                        surface.resize_surface(width, height);
                    }
                    RenderMessage::Close => {
                        should_continue = false;
                        break;
                    }
                    RenderMessage::None => {
                        break;
                    }
                }
            }

            if let Err(e) = render_frame(&surface, &mut scene, &model, &info, &mut text, &stats, &mut renderer) {
                error!("{:?} Error rendering frame: {:?}", info.window_id, e);
                thread::sleep(Duration::from_millis(16));
            }

            let new_time = Instant::now();
            stats.add_sample(Sample {
                frame_time_us: (new_time - frame_start_time).as_micros() as u64,
            });
            frame_start_time = new_time;

            if !first_render {
                info!("{:?} Sending ready event", info.window_id);
                first_render = true;
                info.event_proxy
                    .send_event(UserEvent::RendererReady(info.window_id))
                    .unwrap();
            }
        }

        let _ = info
            .event_proxy
            .send_event(UserEvent::RendererExited(info.window_id));
    });
}

fn render_frame(
    surface: &WindowSurface<'_>,
    scene: &mut Scene,
    model: &SharedModel,
    info: &RendererDto,
    text: &mut SimpleText,
    stats: &Stats,
    renderer: &mut vello::Renderer,
) -> Result<()> {
    let texture = surface.begin_draw()?;
    scene.reset();

    draw_scene(scene, model, info);

    if model.debug {
        draw_debug(scene, model, info, text, stats);
    }

    surface.end_draw(texture, &*scene, renderer)?;
    Ok(())
}

pub fn draw_scene(scene: &mut Scene, model: &SharedModel, renderer: &RendererDto) {
    draw_background(scene, model, renderer);

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

    draw_crosshair(scene, model, renderer);
}

pub fn draw_background(scene: &mut Scene, model: &SharedModel, renderer: &RendererDto) {
    let bounds = renderer.monitor_bounds.to_f64();
    scene.draw_image(&renderer.desktop_gray_image, Affine::IDENTITY);
}

pub fn draw_crosshair(scene: &mut Scene, model: &SharedModel, renderer: &RendererDto) {
    let mouse_pt = model.mouse_pt - ScreenPointF::new(-0.5, -0.5).to_vector();
    let bounds = renderer.monitor_bounds.to_f64();

    let accent_length = util::round_to_even_f(100.0 * renderer.scale_factor);
    let accent_thick_length = accent_length / 2.0;
    let accent_width = util::round_to_odd_f(5.0 * renderer.scale_factor);
    let dash_length = util::round_to_even_f(8.0 * renderer.scale_factor);

    let thin = Stroke::new(1.0);
    let dashed = Stroke::new(1.0).with_dashes(0.0, &[dash_length, dash_length]);
    let thick = Stroke::new(accent_width);

    let bg = Color::WHITE;
    let fg = Color::BLACK;
    let accent = renderer.accent_light;

    let horiz = Line::new((0.0, mouse_pt.y), (bounds.width(), mouse_pt.y));
    let vert = Line::new((mouse_pt.x, 0.0), (mouse_pt.x, bounds.height()));
    scene.stroke(&thin, Affine::IDENTITY, bg, None, &horiz);
    scene.stroke(&thin, Affine::IDENTITY, bg, None, &vert);
    scene.stroke(&dashed, Affine::IDENTITY, fg, None, &horiz);
    scene.stroke(&dashed, Affine::IDENTITY, fg, None, &vert);

    let intercept = horiz.crossing_point(vert).unwrap();
    let horiz = Line::new(
        (intercept.x - accent_length, intercept.y),
        (intercept.x + accent_length, intercept.y),
    );
    let vert = Line::new(
        (intercept.x, intercept.y - accent_length),
        (intercept.x, intercept.y + accent_length),
    );
    scene.stroke(&thin, Affine::IDENTITY, accent, None, &horiz);
    scene.stroke(&thin, Affine::IDENTITY, accent, None, &vert);

    let top = Line::new(
        (intercept.x, intercept.y + accent_length),
        (intercept.x, intercept.y + accent_thick_length),
    );
    scene.stroke(&thick, Affine::IDENTITY, accent, None, &top);

    let bottom = Line::new(
        (intercept.x, intercept.y - accent_length),
        (intercept.x, intercept.y - accent_thick_length),
    );
    scene.stroke(&thick, Affine::IDENTITY, accent, None, &bottom);

    let left = Line::new(
        (intercept.x - accent_length, intercept.y),
        (intercept.x - accent_thick_length, intercept.y),
    );
    scene.stroke(&thick, Affine::IDENTITY, accent, None, &left);

    let right = Line::new(
        (intercept.x + accent_length, intercept.y),
        (intercept.x + accent_thick_length, intercept.y),
    );
    scene.stroke(&thick, Affine::IDENTITY, accent, None, &right);
}

pub fn draw_debug(scene: &mut Scene, model: &SharedModel, renderer: &RendererDto, text: &mut SimpleText, stats: &Stats) {
    let snapshot = stats.snapshot();
    let bounds = renderer.monitor_bounds.to_f64();
    let width = bounds.width();
    let height = bounds.height();

    snapshot.draw_layer(scene, text, width, height, stats.samples(), None, false, vello::AaConfig::Area);
}
