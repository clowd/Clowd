use vello::wgpu::{Backends, PresentMode};

mod app;
mod geometry;
mod gpu;
mod input;
mod logging;
mod render;
mod screenshot;
mod util;
mod stats;
mod simple_text;

#[macro_use]
extern crate log;

#[macro_use]
extern crate anyhow;

fn main() {
    let _ = logging::setup_logging("capture", None, true, false);

    #[cfg(target_os = "macos")]
    app::run_app(Some(Backends::METAL), PresentMode::Immediate).unwrap();

    #[cfg(target_os = "windows")]
    app::run_app(Some(Backends::VULKAN), PresentMode::Mailbox).unwrap();
}

// fn view(app: &App, model: &Model, frame: Frame) {
//     let desc = SamplerBuilder::new()
//         .mag_filter(wgpu::FilterMode::Nearest)
//         .min_filter(wgpu::FilterMode::Nearest)
//         .mipmap_filter(wgpu::FilterMode::Nearest)
//         .into_descriptor();

//     let window = app.window(frame.window_id()).unwrap();
//     let draw = app
//         .draw()
//         .scale(1.0 / window.scale_factor())
//         .sampler(desc);

//     let renderer = model
//         .renderers
//         .iter()
//         .find(|r| r.window == frame.window_id())
//         .unwrap();

//     draw_texture(model, &draw, renderer, app.time);

//     if !model.captured {
//         draw_crosshair(model, &draw, renderer);
//     }

//     if model.debug {
//         draw_debug(model, &draw, renderer);
//     }

//     if let Some(selection) = model.selection {
//         if model.captured
//             && renderer
//                 .monitor_bounds
//                 .contains(selection.center())
//         {
//             model.button_panel.draw(&draw, renderer);
//         }
//     }

//     draw.to_frame(app, &frame).unwrap();
// }

// fn draw_texture(model: &Model, draw: &Draw, renderer: &RendererInfo, time: f32) {
//     let cursor_pos = renderer
//         .transform
//         .pt_to_window(model.mouse_pt)
//         .to_nannou();

//     let zoom_f32 = model.zoom as f32;

//     let monitor_center = renderer.monitor_bounds.center().to_f32();
//     let desktop_center = model.desktop_bounds.center().to_f32();
//     let x_diff = desktop_center.x - monitor_center.x;
//     let y_diff = -(desktop_center.y - monitor_center.y);

//     let texture_draw = draw
//         .x_y(x_diff * zoom_f32, y_diff * zoom_f32)
//         .x_y(-cursor_pos.x * (zoom_f32 - 1.0), -cursor_pos.y * (zoom_f32 - 1.0))
//         .scale(zoom_f32);

//     texture_draw.texture(&model.desktop_gray_texture);

//     draw.rect()
//         .wh(renderer.size_vec2)
//         .rgba(0.0, 0.0, 0.0, 0.5);

//     if let Some(screen_selection) = model.selection {
//         let zoom_transform = renderer
//             .transform
//             .with_zoom(model.mouse_pt, model.zoom);

//         let scissor_transform = zoom_transform.with_scissor();

//         let scissor_rect = scissor_transform
//             .rect_to_window(screen_selection.to_f64())
//             .to_nannou();

//         let cropped_draw = texture_draw.scissor(scissor_rect);

//         cropped_draw.texture(&model.desktop_color_texture);

//         let pixel_size = renderer.scale_factor.floor() as f32;
//         let outline_weight = pixel_size * 2.0;
//         let outline_offset = if model.zoom < 1.5 {
//             SideOffsets2D::new(1, 1, 1, 1)
//         } else {
//             SideOffsets2D::new(0, 0, 0, 0)
//         };

//         let outline_rect = zoom_transform.rect_to_window(
//             screen_selection
//                 .outer_rect(outline_offset)
//                 .to_f64(),
//         );

//         draw_ex::draw_dashed_rectangle(
//             &draw,
//             outline_rect.to_nannou(),
//             outline_weight,
//             pixel_size * 20.0,
//             WHITE,
//             model.accent_dark,
//             time,
//         );

//         let min_size_for_handles = (6.0 * pixel_size * 5.0) as i32;
//         if model.captured && screen_selection.width() > min_size_for_handles && screen_selection.height() > min_size_for_handles {
//             for handle in HitTest::resize_handles() {
//                 let pos = handle
//                     .handle_position(screen_selection.outer_rect(outline_offset))
//                     .to_f64();

//                 let pos = zoom_transform.pt_to_window(pos);

//                 let rect = pos
//                     .to_widened_rect(6.0 * pixel_size as f64)
//                     .to_nannou();
//                 draw.ellipse()
//                     .xy(rect.xy())
//                     .wh(rect.wh())
//                     .color(model.accent_light);

//                 let rect = pos
//                     .to_widened_rect(5.0 * pixel_size as f64)
//                     .to_nannou();
//                 draw.ellipse()
//                     .xy(rect.xy())
//                     .wh(rect.wh())
//                     .color(WHITE);

//                 let rect = pos
//                     .to_widened_rect(4.0 * pixel_size as f64)
//                     .to_nannou();
//                 draw.ellipse()
//                     .xy(rect.xy())
//                     .wh(rect.wh())
//                     .color(model.accent_light);
//             }
//         }
//     }
// }

// fn draw_crosshair(model: &Model, draw: &Draw, renderer: &RendererInfo) {
//     let rc_win = renderer
//         .transform
//         .rect_to_window(renderer.monitor_bounds)
//         .to_nannou();
//     let mouse_pos = renderer
//         .transform
//         .pt_to_window(model.mouse_pt)
//         .to_nannou();
//     let mouse = mouse_pos - pt2(-0.5, 0.5);
//     let mouse_dashed_horiz = (pt2(rc_win.left(), mouse.y), pt2(rc_win.right(), mouse.y));
//     let mouse_dashed_vert = (pt2(mouse.x, rc_win.bottom()), pt2(mouse.x, rc_win.top()));

//     draw_ex::draw_dashed_line_polyline(&draw, mouse_dashed_horiz.0, mouse_dashed_horiz.1, 1.0, 8.0, &model.dash_black_white);

//     draw_ex::draw_dashed_line_polyline(&draw, mouse_dashed_vert.0, mouse_dashed_vert.1, 1.0, 8.0, &model.dash_black_white);

//     let accent_size = 100.0;
//     let accent_color = model.accent_light;
//     let mouse_accent_horiz = (pt2(mouse.x - accent_size, mouse.y), pt2(mouse.x + accent_size, mouse.y));
//     let mouse_accent_vert = (pt2(mouse.x, mouse.y - accent_size), pt2(mouse.x, mouse.y + accent_size));

//     draw.line()
//         .start(mouse_accent_horiz.0)
//         .end(mouse_accent_horiz.1)
//         .stroke_weight(1.0)
//         .color(accent_color);

//     draw.line()
//         .start(mouse_accent_vert.0)
//         .end(mouse_accent_vert.1)
//         .stroke_weight(1.0)
//         .color(accent_color);

//     let handle_size = accent_size / 2.0;
//     let handle_weight = 5.0;

//     let mouse_handle_left = (pt2(mouse.x - handle_size - 0.5, mouse.y), pt2(mouse.x - accent_size - 0.5, mouse.y));
//     let mouse_handle_right = (pt2(mouse.x + handle_size + 0.5, mouse.y), pt2(mouse.x + accent_size + 0.5, mouse.y));
//     let mouse_handle_top = (pt2(mouse.x, mouse.y - handle_size - 0.5), pt2(mouse.x, mouse.y - accent_size - 0.5));
//     let mouse_handle_bottom = (pt2(mouse.x, mouse.y + handle_size + 0.5), pt2(mouse.x, mouse.y + accent_size + 0.5));

//     draw.line()
//         .start(mouse_handle_left.0)
//         .end(mouse_handle_left.1)
//         .stroke_weight(handle_weight)
//         .color(accent_color);

//     draw.line()
//         .start(mouse_handle_right.0)
//         .end(mouse_handle_right.1)
//         .stroke_weight(handle_weight)
//         .color(accent_color);

//     draw.line()
//         .start(mouse_handle_top.0)
//         .end(mouse_handle_top.1)
//         .stroke_weight(handle_weight)
//         .color(accent_color);

//     draw.line()
//         .start(mouse_handle_bottom.0)
//         .end(mouse_handle_bottom.1)
//         .stroke_weight(handle_weight)
//         .color(accent_color);
// }

// fn draw_debug(model: &Model, draw: &Draw, renderer: &RendererInfo) {
//     let win = renderer
//         .transform
//         .rect_to_window(renderer.monitor_bounds)
//         .to_nannou();

//     // Crosshair at window center
//     let crosshair_color = rgba(1.0, 1.0, 1.0, 1.0);
//     let ends = [win.mid_top(), win.mid_right(), win.mid_bottom(), win.mid_left()];
//     for &end in &ends {
//         draw.arrow()
//             .weight(0.5)
//             .start_cap_round()
//             .head_length(16.0)
//             .head_width(8.0)
//             .color(crosshair_color)
//             .end(end - vec2(-0.5, -0.5))
//             .start(vec2(0.5, 0.5));
//     }

//     let top = format!("{:.1}", win.top());
//     let bottom = format!("{:.1}", win.bottom());
//     let left = format!("{:.1}", win.left());
//     let right = format!("{:.1}", win.right());
//     let x_off = 30.0;
//     let y_off = 20.0;
//     draw.text("0.0")
//         .x_y(15.0, 15.0)
//         .color(crosshair_color)
//         .font_size(14);
//     draw.text(&top)
//         .h(win.h())
//         .font_size(14)
//         .align_text_top()
//         .color(crosshair_color)
//         .x(x_off);
//     draw.text(&bottom)
//         .h(win.h())
//         .font_size(14)
//         .align_text_bottom()
//         .color(crosshair_color)
//         .x(x_off);
//     draw.text(&left)
//         .w(win.w())
//         .font_size(14)
//         .left_justify()
//         .color(crosshair_color)
//         .y(y_off);
//     draw.text(&right)
//         .w(win.w())
//         .font_size(14)
//         .right_justify()
//         .color(crosshair_color)
//         .y(y_off);

//     let mouse_pos = renderer
//         .transform
//         .pt_to_window(model.mouse_pt)
//         .to_nannou();

//     // Debug window and monitor details.
//     let m_scale_factor = renderer.scale_factor;
//     let mon_phys_size = renderer.monitor_handle.size();
//     let mon_log_size = mon_phys_size.to_logical::<f32>(m_scale_factor as f64);
//     let mon_phys_pos = renderer.monitor_handle.position();
//     let mon_log_pos = mon_phys_pos.to_logical::<f32>(m_scale_factor as f64);
//     let text = format!(
//         "
//         Monitor logical: [{:.0}, {:.0}, {:.0}, {:.0}]
//         Monitor phsical: [{:.0}, {:.0}, {:.0}, {:.0}]
//         Monitor ratio: {:.2}
//         Monitor scale factor: {:.2}
//         Monitor primary: {:?}
//         World zoom: {:.2}
//         World mouse: {:?}
//         Mouse relative to window: {:.2}
//         Artificial mouse: {:?}
//         ",
//         mon_log_pos.x,
//         mon_log_pos.y,
//         mon_log_size.width,
//         mon_log_size.height,
//         mon_phys_pos.x,
//         mon_phys_pos.y,
//         mon_phys_size.width,
//         mon_phys_size.height,
//         mon_log_size.width / mon_log_size.height,
//         m_scale_factor,
//         renderer.is_primary,
//         model.zoom,
//         model.mouse_pt,
//         mouse_pos,
//         model.mouse_anchored,
//     );
//     let pad = 6.0;
//     draw.text(&text)
//         .h(win.pad(pad).h())
//         .w(win.pad(pad).w())
//         .line_spacing(pad)
//         .font_size(14)
//         .align_text_bottom()
//         .color(crosshair_color)
//         .left_justify();

//     // Ellipse at mouse.
//     draw.ellipse()
//         .wh([5.0; 2].into())
//         .xy(mouse_pos);

//     // Mouse position text.
//     let pos = format!("[{:.1}, {:.1}]", mouse_pos.x, mouse_pos.y);
//     draw.text(&pos)
//         .xy(mouse_pos + vec2(0.0, 20.0))
//         .font_size(14)
//         .color(WHITE);
// }
