//! This example demonstrates Bevy's immediate mode drawing API intended for visual debugging.

mod geometry;
mod plugins;
mod screen;

#[macro_use]
extern crate anyhow;

use crate::geometry::*;

use bevy::{
    asset::RenderAssetUsages,
    color::palettes,
    core::FrameCount,
    image::CompressedImageFormats,
    log::*,
    render::{camera::RenderTarget, view},
    window::{RawHandleWrapper, WindowCreated, WindowRef, WindowResolution},
};

use euclid::Transform2D;
use image::DynamicImage;
use iyes_perf_ui::prelude::PerfUiDefaultEntries;
use raw_window_handle::RawWindowHandle;
use screen::{all_monitors, capture_desktop};

use std::{
    f32::consts::{FRAC_PI_2, PI, TAU},
    num::NonZero,
};

use bevy::{color::palettes::css::*, math::Isometry2d, prelude::*, render::settings::RenderCreation, window::PresentMode};

fn main() {
    App::new()
        .add_plugins((
            DefaultPlugins
                .set(WindowPlugin {
                    primary_window: None,
                    close_when_requested: true,
                    exit_condition: bevy::window::ExitCondition::DontExit,
                })
                .set(bevy::render::RenderPlugin {
                    render_creation: RenderCreation::Automatic(bevy::render::settings::WgpuSettings {
                        backends: Some(bevy::render::settings::Backends::DX12 | bevy::render::settings::Backends::METAL),
                        dx12_shader_compiler: bevy::render::settings::Dx12Compiler::Dxc {
                            dxil_path: None,
                            dxc_path: None,
                        },
                        power_preference: bevy::render::settings::PowerPreference::HighPerformance,
                        ..default()
                    }),
                    ..default()
                }),
            bevy::diagnostic::LogDiagnosticsPlugin::default(),
            bevy::diagnostic::FrameTimeDiagnosticsPlugin::default(),
            // plugins::framepace::FramepacePlugin,
        ))
        // .init_gizmo_group::<MyRoundGizmos>()
        .add_systems(Startup, setup)
        .add_systems(Update, (update_cursor, handle_keypress, handle_window_created, make_visible))
        .run();
}

fn handle_window_created(mut events: EventReader<WindowCreated>, windows: Query<(&RawHandleWrapper, &Window)>) {
    for w in events.read() {
        let w = windows.get(w.window).unwrap();
        if let RawWindowHandle::Win32(handle) = w.0.window_handle {
            use windows::Win32::Graphics::Dwm::*;
            let handle: isize = handle.hwnd.into();
            let hwnd = windows::Win32::Foundation::HWND(handle as *mut std::ffi::c_void);
            let dw_flag: i32 = 1;
            let dw_flag_ptr = &dw_flag as *const i32 as *const std::ffi::c_void;
            unsafe {
                let _ = DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, dw_flag_ptr, 4);
                let _ = DwmSetWindowAttribute(hwnd, DWMWA_EXCLUDED_FROM_PEEK, dw_flag_ptr, 4);
            }
        }
    }
}

fn make_visible(mut window: Query<&mut Window>, frames: Res<FrameCount>) {
    if frames.0 == 2 {
        for mut window in window.iter_mut() {
            window.visible = true;
        }
    }
}

#[derive(Resource)]
struct MouseRes(mouse_rs::Mouse);

fn setup(
    mut commands: Commands,
    mut images: ResMut<Assets<Image>>,
    // mut meshes: ResMut<Assets<Mesh>>,
    // mut materials: ResMut<Assets<ColorMaterial>>,
) {
    let monitors = all_monitors().expect("Failed to get all monitors");
    commands.insert_resource(MouseRes(mouse_rs::Mouse::new()));

    let (desktop_bounds, desktop_color_image, desktop_gray_image) = capture_desktop().expect("Unable to capture desktop");
    // let desktop_virtual_origin = desktop_bounds.top_left();
    // let vd_transform = Transform2D::<i32, ScreenUnit, ScreenUnit>::identity().then_translate(-desktop_virtual_origin.to_vector());

    println!("Desktop bounds: {:?}", desktop_bounds);

    let dynamic_color = DynamicImage::ImageRgba8(desktop_color_image);
    let dynamic_gray = DynamicImage::ImageRgba8(desktop_gray_image);
    let image_color = Image::from_dynamic(dynamic_color, true, RenderAssetUsages::all());
    let image_gray = Image::from_dynamic(dynamic_gray, true, RenderAssetUsages::all());
    let color_handle = images.add(image_color);
    let gray_handle = images.add(image_gray);

    commands.spawn((
        Sprite::from_image(gray_handle.clone()),
        Transform::from_translation(Vec3::new(
            (desktop_bounds.width() as f32 / 2.0) + desktop_bounds.left() as f32,
            (-desktop_bounds.height() as f32 / 2.0) - desktop_bounds.top() as f32,
            0.,
        )),
    ));

    let node = Node {
        position_type: PositionType::Absolute,
        top: Val::Px(12.0),
        left: Val::Px(12.0),
        ..default()
    };

    for (i, monitor) in monitors.iter().enumerate() {
        let scale = monitor.scale_factor();
        let x = monitor.x();
        let y = monitor.y();
        let width = monitor.width() as f32 / scale;
        let height = monitor.height() as f32 / scale;
        info!(
            "Monitor {}: x={}, y={}, width={}, height={}, scale={}",
            i + 1,
            x,
            y,
            width,
            height,
            scale
        );

        let window = commands
            .spawn(Window {
                title: "Clowd Capture".to_owned(),
                resolution: WindowResolution::new(width, height).with_scale_factor_override(1.0),
                present_mode: PresentMode::Mailbox,
                desired_maximum_frame_latency: NonZero::new(1),
                focused: i == 0,
                position: WindowPosition::At(IVec2::new(x, y)),
                decorations: false,
                visible: false,
                ..default()
            })
            .id();

        let half_width = width / 2.0;
        let half_height = height / 2.0;

        // let viewport_bounds = ScreenRect::from_xy_size(x, y, monitor.width() as i32, monitor.height() as i32);
        // let viewport_bounds = vd_transform.outer_transformed_rect(&viewport_bounds);

        // info!("Viewport bounds: {:?}", viewport_bounds);

        let camera = commands
            .spawn((
                Camera2d::default(),
                // Transform::IDENTITY.with_translation(Vec3::new(
                //     half_width + viewport_bounds.min_x() as f32,
                //     -half_height - viewport_bounds.min_y() as f32,
                //     0.0,
                // )),
                Transform::IDENTITY.with_translation(Vec3::new(x as f32 + half_width, -y as f32 - half_height as f32, 0.0)),
                Camera {
                    target: RenderTarget::Window(WindowRef::Entity(window)),
                    ..default()
                },
            ))
            .id();

        commands.spawn((
            Text::new(format!("Window: {}", i + 1)),
            node.clone(),
            // Since we are using multiple cameras, we need to specify which camera UI should be rendered to
            TargetCamera(camera),
        ));
    }

    commands.spawn(PerfUiDefaultEntries::default());
}

fn update_cursor(mouse: Res<MouseRes>, mut gizmos: Gizmos) {
    if let Ok(pos) = mouse.0.get_position() {
        let pos = IVec2::new(pos.x, -pos.y).as_vec2();
        // let pos = Vec2::new(pos.x - windows.single().width() / 2.0, windows.single().height() / 2.0 - pos.y);
        gizmos.circle_2d(pos, 10.0, palettes::basic::GREEN);
        // gizmos.linestrip_2d(positions, color);
    }
}

fn handle_keypress(mut exit: EventWriter<AppExit>, keyboard: Res<ButtonInput<KeyCode>>, time: Res<Time>) {
    if keyboard.just_pressed(KeyCode::Escape) {
        exit.send(AppExit::Success);
    }
}

// // We can create our own gizmo config group!
// #[derive(Default, Reflect, GizmoConfigGroup)]
// struct MyRoundGizmos {}

// fn setup(mut commands: Commands) {
//     commands.spawn(Camera2d);
//     // text
//     commands.spawn((
//         Text::new(
//             "Hold 'Left' or 'Right' to change the line width of straight gizmos\n\
//         Hold 'Up' or 'Down' to change the line width of round gizmos\n\
//         Press '1' / '2' to toggle the visibility of straight / round gizmos\n\
//         Press 'U' / 'I' to cycle through line styles\n\
//         Press 'J' / 'K' to cycle through line joins",
//         ),
//         Node {
//             position_type: PositionType::Absolute,
//             top: Val::Px(12.),
//             left: Val::Px(12.),
//             ..default()
//         },
//     ));
// }

// fn draw_example_collection(mut gizmos: Gizmos, mut my_gizmos: Gizmos<MyRoundGizmos>, time: Res<Time>) {
//     let sin_t_scaled = ops::sin(time.elapsed_secs()) * 50.;
//     gizmos.line_2d(Vec2::Y * -sin_t_scaled, Vec2::splat(-80.), RED);
//     gizmos.ray_2d(Vec2::Y * sin_t_scaled, Vec2::splat(80.), LIME);

//     gizmos
//         .grid_2d(
//             Isometry2d::IDENTITY,
//             UVec2::new(16, 9),
//             Vec2::new(80., 80.),
//             // Dark gray
//             LinearRgba::gray(0.05),
//         )
//         .outer_edges();

//     // Triangle
//     gizmos.linestrip_gradient_2d([
//         (Vec2::Y * 300., BLUE),
//         (Vec2::new(-255., -155.), RED),
//         (Vec2::new(255., -155.), LIME),
//         (Vec2::Y * 300., BLUE),
//     ]);

//     gizmos.rect_2d(Isometry2d::IDENTITY, Vec2::splat(650.), BLACK);

//     gizmos.cross_2d(Vec2::new(-160., 120.), 12., FUCHSIA);

//     let domain = Interval::EVERYWHERE;
//     let curve = FunctionCurve::new(domain, |t| Vec2::new(t, ops::sin(t / 25.0) * 100.0));
//     let resolution = ((ops::sin(time.elapsed_secs()) + 1.0) * 50.0) as usize;
//     let times_and_colors = (0..=resolution)
//         .map(|n| n as f32 / resolution as f32)
//         .map(|t| (t - 0.5) * 600.0)
//         .map(|t| (t, TEAL.mix(&HOT_PINK, (t + 300.0) / 600.0)));
//     gizmos.curve_gradient_2d(curve, times_and_colors);

//     my_gizmos
//         .rounded_rect_2d(Isometry2d::IDENTITY, Vec2::splat(630.), BLACK)
//         .corner_radius(ops::cos(time.elapsed_secs() / 3.) * 100.);

//     // Circles have 32 line-segments by default.
//     // You may want to increase this for larger circles.
//     my_gizmos
//         .circle_2d(Isometry2d::IDENTITY, 300., NAVY)
//         .resolution(64);

//     my_gizmos.ellipse_2d(Rot2::radians(time.elapsed_secs() % TAU), Vec2::new(100., 200.), YELLOW_GREEN);

//     // Arcs default resolution is linearly interpolated between
//     // 1 and 32, using the arc length as scalar.
//     my_gizmos.arc_2d(Rot2::radians(sin_t_scaled / 10.), FRAC_PI_2, 310., ORANGE_RED);
//     my_gizmos.arc_2d(Isometry2d::IDENTITY, FRAC_PI_2, 80.0, ORANGE_RED);
//     my_gizmos.long_arc_2d_between(Vec2::ZERO, Vec2::X * 20.0, Vec2::Y * 20.0, ORANGE_RED);
//     my_gizmos.short_arc_2d_between(Vec2::ZERO, Vec2::X * 40.0, Vec2::Y * 40.0, ORANGE_RED);

//     gizmos.arrow_2d(Vec2::ZERO, Vec2::from_angle(sin_t_scaled / -10. + PI / 2.) * 50., YELLOW);

//     // You can create more complex arrows using the arrow builder.
//     gizmos
//         .arrow_2d(Vec2::ZERO, Vec2::from_angle(sin_t_scaled / -10.) * 50., GREEN)
//         .with_double_end()
//         .with_tip_length(10.);
// }

// fn update_config(mut config_store: ResMut<GizmoConfigStore>, keyboard: Res<ButtonInput<KeyCode>>, time: Res<Time>) {
//     let (config, _) = config_store.config_mut::<DefaultGizmoConfigGroup>();
//     if keyboard.pressed(KeyCode::ArrowRight) {
//         config.line_width += 5. * time.delta_secs();
//         config.line_width = config.line_width.clamp(0., 50.);
//     }
//     if keyboard.pressed(KeyCode::ArrowLeft) {
//         config.line_width -= 5. * time.delta_secs();
//         config.line_width = config.line_width.clamp(0., 50.);
//     }
//     if keyboard.just_pressed(KeyCode::Digit1) {
//         config.enabled ^= true;
//     }
//     if keyboard.just_pressed(KeyCode::KeyU) {
//         config.line_style = match config.line_style {
//             GizmoLineStyle::Solid => GizmoLineStyle::Dotted,
//             _ => GizmoLineStyle::Solid,
//         };
//     }
//     if keyboard.just_pressed(KeyCode::KeyJ) {
//         config.line_joints = match config.line_joints {
//             GizmoLineJoint::Bevel => GizmoLineJoint::Miter,
//             GizmoLineJoint::Miter => GizmoLineJoint::Round(4),
//             GizmoLineJoint::Round(_) => GizmoLineJoint::None,
//             GizmoLineJoint::None => GizmoLineJoint::Bevel,
//         };
//     }

//     let (my_config, _) = config_store.config_mut::<MyRoundGizmos>();
//     if keyboard.pressed(KeyCode::ArrowUp) {
//         my_config.line_width += 5. * time.delta_secs();
//         my_config.line_width = my_config.line_width.clamp(0., 50.);
//     }
//     if keyboard.pressed(KeyCode::ArrowDown) {
//         my_config.line_width -= 5. * time.delta_secs();
//         my_config.line_width = my_config.line_width.clamp(0., 50.);
//     }
//     if keyboard.just_pressed(KeyCode::Digit2) {
//         my_config.enabled ^= true;
//     }
//     if keyboard.just_pressed(KeyCode::KeyI) {
//         my_config.line_style = match my_config.line_style {
//             GizmoLineStyle::Solid => GizmoLineStyle::Dotted,
//             _ => GizmoLineStyle::Solid,
//         };
//     }
//     if keyboard.just_pressed(KeyCode::KeyK) {
//         my_config.line_joints = match my_config.line_joints {
//             GizmoLineJoint::Bevel => GizmoLineJoint::Miter,
//             GizmoLineJoint::Miter => GizmoLineJoint::Round(4),
//             GizmoLineJoint::Round(_) => GizmoLineJoint::None,
//             GizmoLineJoint::None => GizmoLineJoint::Bevel,
//         };
//     }
// }
