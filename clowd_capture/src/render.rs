use crate::geometry::*;
use crate::{
    app::{RenderMessage, RendererDto, SharedModel, UserEvent},
    gpu::WindowSurface,
};
use std::sync::mpsc::Receiver;
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

        info!("{:?} Starting render loop", info.window_id);

        loop {
            let texture = surface.begin_draw();
            scene.reset();

            info!("{:?} Drawing scene", info.window_id);
            draw_scene(&mut scene, &model, &info);

            surface.end_draw(texture, &scene, &mut renderer);

            if !first_render {
                info!("{:?} Sending ready event", info.window_id);
                first_render = true;
                info.event_proxy
                    .send_event(UserEvent::RendererReady(info.window_id))
                    .unwrap();
            }

            match bus.recv() {
                Ok(msg) => {
                    debug!("Received render message: {:?}", msg);
                    match msg {
                        RenderMessage::ModelUpdate(new_model) => {
                            model = new_model;
                        }
                        RenderMessage::Resize((width, height)) => {
                            surface.resize_surface(width, height);
                        }
                        RenderMessage::Close => {
                            break;
                        }
                        _ => {}
                    }
                }
                Err(e) => {
                    error!("Error receiving render message: {:?}", e);
                    break;
                }
            }
        }

        let _ = info
            .event_proxy
            .send_event(UserEvent::RendererExited(info.window_id));
    });
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

    let stroke = Stroke::new(1.0);
    let crosshair_color = Color::WHITE;

    let horiz = Line::new((0.0, mouse_pt.y), (bounds.width(), mouse_pt.y));
    let vert = Line::new((mouse_pt.x, 0.0), (mouse_pt.x, bounds.height()));

    scene.stroke(&stroke, Affine::IDENTITY, crosshair_color, None, &horiz);
    scene.stroke(&stroke, Affine::IDENTITY, crosshair_color, None, &vert);

    // Draw a horizontal line
    // let line = Line::new((0.0, 0.0), (100.0, 0.0));
    // scene.stroke(&stroke, Affine::IDENTITY, crosshair_color, None, &line);

    // // Draw a vertical line
    // let line = Line::new((0.0, 0.0), (0.0, 100.0));
    // scene.stroke(&stroke, Affine::IDENTITY, crosshair_color, None, &line);
}
