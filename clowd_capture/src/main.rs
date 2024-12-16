mod geometry;
mod myshapes;
mod plugins;
mod resources;
mod screen;

#[macro_use]
extern crate anyhow;

use std::f64::consts::PI;

use crate::geometry::*;
use crate::resources::*;

use bevy::{
    asset::RenderAssetUsages,
    color::palettes::css::*,
    core::FrameCount,
    input::mouse::MouseWheel,
    log::*,
    prelude::*,
    render::camera::RenderTarget,
    render::settings::RenderCreation,
    window::{PresentMode, RawHandleWrapper, WindowCreated, WindowRef, WindowResolution},
};

use bevy_prototype_lyon::prelude::*;
use iyes_perf_ui::prelude::*;
use raw_window_handle::RawWindowHandle;
use screen::capture_desktop;
use screen::Monitor;

fn main() {
    App::new()
        .add_plugins(
            DefaultPlugins
                .set(WindowPlugin {
                    primary_window: None,
                    close_when_requested: true,
                    exit_condition: bevy::window::ExitCondition::OnAllClosed,
                })
                .set(bevy::render::RenderPlugin {
                    render_creation: RenderCreation::Automatic(bevy::render::settings::WgpuSettings {
                        backends: Some(bevy::render::settings::Backends::VULKAN | bevy::render::settings::Backends::METAL),
                        dx12_shader_compiler: bevy::render::settings::Dx12Compiler::Dxc {
                            dxil_path: None,
                            dxc_path: None,
                        },
                        power_preference: bevy::render::settings::PowerPreference::HighPerformance,
                        ..default()
                    }),
                    ..default()
                })
                .set(ImagePlugin::default_nearest()),
        )
        .add_plugins(bevy::diagnostic::LogDiagnosticsPlugin::default())
        .add_plugins(bevy::diagnostic::FrameTimeDiagnosticsPlugin)
        .add_plugins(bevy::diagnostic::SystemInformationDiagnosticsPlugin)
        .add_plugins(PerfUiPlugin)
        .add_plugins(ShapePlugin)
        .init_resource::<MousePosition>()
        .init_resource::<CaptureState>()
        .init_resource::<AccentColors>()
        .add_systems(Startup, setup)
        .add_systems(Update, startup_animation)
        .add_systems(PreUpdate, handle_mouse_scroll)
        .add_systems(Update, (handle_window_created, handle_mouse_move))
        .add_systems(Update, handle_keypress.before(iyes_perf_ui::PerfUiSet::Setup))
        .run();
}

fn setup(mut commands: Commands, mut images: ResMut<Assets<Image>>, accents: Res<AccentColors>) {
    let monitors = Monitor::all().expect("Failed to get all monitors");
    let (desktop_bounds, desktop_color_image, desktop_gray_image) = capture_desktop().expect("Unable to capture desktop");
    let desktop_bounds = desktop_bounds.to_f32();

    println!("Desktop bounds: {:?}", desktop_bounds);
    let image_color = Image::from_dynamic(desktop_color_image, true, RenderAssetUsages::all());
    let image_gray = Image::from_dynamic(desktop_gray_image, true, RenderAssetUsages::all());
    let color_handle = images.add(image_color);
    let gray_handle = images.add(image_gray);

    let mut camera_entities = Vec::new();

    commands.spawn((
        Sprite::from_image(gray_handle),
        Transform::from_translation(Vec3::new(
            (desktop_bounds.width() / 2.0) + desktop_bounds.left(),
            (-desktop_bounds.height() / 2.0) - desktop_bounds.top(),
            Z_BGGRAY,
        )),
        ImageGrayTag,
    ));

    commands.spawn((
        Sprite::from_image(color_handle),
        Transform::from_translation(Vec3::new(
            (desktop_bounds.width() / 2.0) + desktop_bounds.left(),
            (-desktop_bounds.height() / 2.0) - desktop_bounds.top(),
            Z_BGCOLOR,
        )),
        ImageColorTag,
    ));

    let node = Node {
        position_type: PositionType::Absolute,
        top: Val::Px(12.0),
        left: Val::Px(12.0),
        ..default()
    };

    for (i, monitor) in monitors.iter().enumerate() {
        let scale = monitor.scale_factor();
        let bounds = monitor.bounds();
        let x = bounds.left();
        let y = bounds.top();
        let width = bounds.width() as f32 / scale;
        let height = bounds.height() as f32 / scale;
        info!("Monitor {}: bounds={:?}, scale={}", i + 1, bounds, scale);

        let window = commands
            .spawn(Window {
                title: "Clowd Capture".to_owned(),
                resolution: WindowResolution::new(width, height),
                #[cfg(target_os = "windows")]
                present_mode: PresentMode::Mailbox,
                #[cfg(target_os = "macos")]
                present_mode: PresentMode::Immediate,
                desired_maximum_frame_latency: std::num::NonZero::new(1),
                focused: i == 0,
                position: WindowPosition::At(IVec2::new(x, y)),
                decorations: false,
                window_level: bevy::window::WindowLevel::AlwaysOnTop,
                visible: false,
                ..default()
            })
            .id();

        let half_width = width / 2.0;
        let half_height = height / 2.0;
        let cam_transform = Transform::IDENTITY
            .with_scale(Vec3::splat(scale as f32))
            .with_translation(Vec3::new(
                x as f32 + half_width * scale,
                -y as f32 - half_height as f32 * scale,
                0.0,
            ));
        let camera = commands
            .spawn((
                Camera2d::default(),
                Msaa::Sample4,
                cam_transform,
                Camera {
                    target: RenderTarget::Window(WindowRef::Entity(window)),
                    ..default()
                },
                WindowCameraTag,
            ))
            .id();

        commands.spawn((Text::new(format!("Window: {}", i + 1)), node.clone(), TargetCamera(camera)));

        if monitor.is_primary() {
            commands.insert_resource(PrimaryCamera(camera));
        }

        camera_entities.push((camera, bounds, cam_transform.clone(), scale));
    }

    commands.insert_resource(CameraEntities(camera_entities));

    // create crosshair
    let ch_parent_accent = commands
        .spawn((Transform::default(), GlobalTransform::default(), CrosshairAccentTag))
        .id();

    let ch_parent_horiz = commands
        .spawn((Transform::default(), GlobalTransform::default(), CrosshairHorizTag))
        .id();

    let ch_parent_vert = commands
        .spawn((Transform::default(), GlobalTransform::default(), CrosshairVertTag))
        .id();

    let ch_stroke = 1.0;
    let ch_dashlength = 8.0;
    let ch_offset_x = -0.5;
    let ch_offset_y = 0.5;
    let accent_size = 100.0;
    let accent_width = 5.0;
    let ch_horiz_start = Vec2::new(desktop_bounds.min_x(), ch_offset_x);
    let ch_horiz_end = Vec2::new(desktop_bounds.max_x(), ch_offset_x);
    let ch_vert_start = Vec2::new(ch_offset_y, -desktop_bounds.min_y());
    let ch_vert_end = Vec2::new(ch_offset_y, -desktop_bounds.max_y());

    println!("Desktop bounds: {:?}", desktop_bounds);

    commands
        .spawn((
            ShapeBuilder::with(&shapes::Line(ch_horiz_start, ch_horiz_end))
                .stroke((BLACK, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_BACK),
        ))
        .set_parent(ch_parent_horiz);

    commands
        .spawn((
            ShapeBuilder::with(&shapes::Line(ch_vert_start, ch_vert_end))
                .stroke((BLACK, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_BACK),
        ))
        .set_parent(ch_parent_vert);

    commands
        .spawn((
            ShapeBuilder::with(&myshapes::shape_dashed_line(ch_horiz_start, ch_horiz_end, ch_dashlength))
                .stroke((WHITE, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_DASH),
        ))
        .set_parent(ch_parent_horiz);

    commands
        .spawn((
            ShapeBuilder::with(&myshapes::shape_dashed_line(ch_vert_start, ch_vert_end, ch_dashlength))
                .stroke((WHITE, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_DASH),
        ))
        .set_parent(ch_parent_vert);

    let ch_horiz_start = Vec2::new(-accent_size, ch_offset_x);
    let ch_horiz_end = Vec2::new(accent_size, ch_offset_x);
    let ch_vert_start = Vec2::new(ch_offset_y, -accent_size);
    let ch_vert_end = Vec2::new(ch_offset_y, accent_size);
    commands
        .spawn((
            ShapeBuilder::with(&shapes::Line(ch_horiz_start, ch_horiz_end))
                .stroke((accents.accent_light, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_ACCENT),
        ))
        .set_parent(ch_parent_accent);

    commands
        .spawn((
            ShapeBuilder::with(&shapes::Line(ch_vert_start, ch_vert_end))
                .stroke((accents.accent_light, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_ACCENT),
        ))
        .set_parent(ch_parent_accent);

    let ch_accent_rects = [
        (Vec2::new(-accent_size, ch_offset_x), Vec2::new(-accent_size / 2.0, ch_offset_x)), // left
        (Vec2::new(accent_size / 2.0, ch_offset_x), Vec2::new(accent_size, ch_offset_x)),   // right
        (Vec2::new(ch_offset_y, -accent_size), Vec2::new(ch_offset_y, -accent_size / 2.0)), // bottom
        (Vec2::new(ch_offset_y, accent_size / 2.0), Vec2::new(ch_offset_y, accent_size)),   // top
    ];

    for (start, end) in ch_accent_rects.iter() {
        commands
            .spawn((
                ShapeBuilder::with(&shapes::Line(*start, *end))
                    .stroke((accents.accent_light, accent_width))
                    .build(),
                Transform::from_xyz(0.0, 0.0, Z_CURSOR_ACCENT),
            ))
            .set_parent(ch_parent_accent);
    }
}

fn startup_animation(
    mut commands: Commands,
    mut window: Query<&mut Window>,
    mut mouse: ResMut<MousePosition>,
    frames: Res<FrameCount>,
    first_render: Option<Res<FirstRenderTime>>,
    time: Res<Time>,
    mut query: Query<&mut Sprite, With<ImageColorTag>>,
) {
    if frames.0 == 3 {
        for mut window in window.iter_mut() {
            window.visible = true;
        }
        commands.insert_resource(FirstRenderTime(time.elapsed_secs_f64()));
        mouse.set_anchored(true);
    }

    if let Some(first_render) = first_render {
        let mut overlay = query.single_mut();
        if overlay.color.alpha() > 0.0 {
            let elapsed_seconds = time.elapsed_secs_f64() - first_render.0;
            let fade_duration = 0.2; // 200ms
            let t = (elapsed_seconds / fade_duration).clamp(0.0, 1.0);
            let fade_value = 0.5 * (1.0 + (PI * t).cos());
            overlay.color.set_alpha(fade_value as f32);
        }
    }
}

fn handle_window_created(mut events: EventReader<WindowCreated>, windows: Query<(&RawHandleWrapper, &Window)>) {
    for w in events.read() {
        let w = windows.get(w.window).unwrap();
        // WinitWindows::get_window

        #[cfg(windows)]
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

fn handle_mouse_move(
    mut mouse: ResMut<MousePosition>,
    mut transforms: ParamSet<(
        Query<&mut Transform, With<CrosshairVertTag>>,
        Query<&mut Transform, With<CrosshairHorizTag>>,
        Query<&mut Transform, With<CrosshairAccentTag>>,
        Query<&mut Transform, With<WindowCameraTag>>,
    )>,
    cameras: Res<CameraEntities>,
) {
    mouse.update_position();
    let pos = mouse.get_position().to_nannou();
    if let Ok(mut e) = transforms.p0().get_single_mut() {
        e.translation = Transform::from_xyz(pos.x, 0.0, Z_CURSOR_BACK).translation;
    }
    if let Ok(mut e) = transforms.p1().get_single_mut() {
        e.translation = Transform::from_xyz(0.0, pos.y, Z_CURSOR_BACK).translation;
    }
    if let Ok(mut e) = transforms.p2().get_single_mut() {
        e.translation = Transform::from_xyz(pos.x, pos.y, Z_CURSOR_ACCENT).translation;
    }

    // Update camera positions
    let zoom = mouse.get_zoom();
    for camera in &cameras.0 {
        if let Ok(mut e) = transforms.p3().get_mut(camera.0) {
            if zoom > 1.0 {
                e.scale = Vec3::splat(camera.3 * 1.0 / zoom as f32);
            } else {
                // reset to defaults
                e.translation = camera.2.translation;
                e.scale = Vec3::splat(camera.3);
            }
        }
    }

    // for (_, bounds, mut transform) in cameras.0.iter_mut() {
    //     let half_width = bounds.width() as f32 / 2.0;
    //     let half_height = bounds.height() as f32 / 2.0;
    //     let x = bounds.left() as f32 + half_width;
    //     let y = bounds.top() as f32 + half_height;
    //     transform.translation = Vec3::new(x, -y, 0.0);
    // }
}

fn handle_mouse_scroll(mut mouse: ResMut<MousePosition>, mut scroll: EventReader<MouseWheel>, keyboard: Res<ButtonInput<KeyCode>>) {
    for ev in scroll.read() {
        let delta = ev.y;
        let mut zoom = mouse.get_zoom();

        if keyboard.pressed(KeyCode::ShiftLeft)
            || keyboard.pressed(KeyCode::ShiftRight)
            || keyboard.pressed(KeyCode::ControlLeft)
            || keyboard.pressed(KeyCode::ControlRight)
        {
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

        mouse.set_zoom(zoom.max(1.0).min(256.0));
    }
}

fn handle_keypress(
    mut mouse: ResMut<MousePosition>,
    mut commands: Commands,
    mut exit: EventWriter<AppExit>,
    keyboard: Res<ButtonInput<KeyCode>>,
    q_root: Query<Entity, With<PerfUiRoot>>,
    camera: Res<PrimaryCamera>,
) {
    if keyboard.just_pressed(KeyCode::Escape) {
        mouse.set_anchored(false);
        exit.send(AppExit::Success);
    } else if keyboard.just_pressed(KeyCode::KeyQ) {
        // my_config.enabled ^= true;
        // println!("Overlay gizmos enabled: {}", my_config.enabled);
    } else if keyboard.just_pressed(KeyCode::KeyD) {
        if let Ok(e) = q_root.get_single() {
            commands.entity(e).despawn_recursive();
        } else {
            commands.spawn((PerfUiDefaultEntries::default(), camera.get()));
        }
    }
}
