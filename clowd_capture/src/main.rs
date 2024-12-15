//! This example demonstrates Bevy's immediate mode drawing API intended for visual debugging.

mod geometry;
mod plugins;
mod resources;
mod screen;

#[macro_use]
extern crate anyhow;

use crate::geometry::*;
use crate::resources::*;

use bevy::{
    asset::RenderAssetUsages,
    color::palettes::css::*,
    core::FrameCount,
    log::*,
    prelude::*,
    render::camera::RenderTarget,
    render::settings::RenderCreation,
    window::{PresentMode, RawHandleWrapper, WindowCreated, WindowRef, WindowResolution},
};

use bevy_prototype_lyon::prelude::*;
use iyes_perf_ui::prelude::*;
use raw_window_handle::RawWindowHandle;
use screen::{all_monitors, capture_desktop};

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
                }),
        )
        .add_plugins(bevy::diagnostic::LogDiagnosticsPlugin::default())
        .add_plugins(bevy::diagnostic::FrameTimeDiagnosticsPlugin)
        .add_plugins(bevy::diagnostic::SystemInformationDiagnosticsPlugin)
        .add_plugins(PerfUiPlugin)
        .add_plugins(ShapePlugin)
        .init_gizmo_group::<MyOverlayGizmos>()
        .init_resource::<MousePosition>()
        .init_resource::<CaptureState>()
        .add_systems(Startup, setup)
        .add_systems(Update, (update_cursor, handle_keypress, handle_window_created, make_visible))
        .add_systems(Update, toggle_debug.before(iyes_perf_ui::PerfUiSet::Setup))
        .run();
}

fn handle_window_created(mut events: EventReader<WindowCreated>, windows: Query<(&RawHandleWrapper, &Window)>) {
    for w in events.read() {
        let w = windows.get(w.window).unwrap();

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

fn make_visible(mut window: Query<&mut Window>, frames: Res<FrameCount>) {
    if frames.0 == 2 {
        for mut window in window.iter_mut() {
            window.visible = true;
        }
    }
}

#[derive(Default, Reflect, GizmoConfigGroup)]
struct MyOverlayGizmos {}

fn setup(
    mut commands: Commands,
    mut images: ResMut<Assets<Image>>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<ColorMaterial>>,
) {
    let shape = shapes::RegularPolygon {
        sides: 6,
        feature: shapes::RegularPolygonFeature::Radius(200.0),
        ..shapes::RegularPolygon::default()
    };
    commands.spawn((Camera2d, Msaa::Sample4));
    commands.spawn((
        ShapeBuilder::with(&shape)
            .fill(DARK_CYAN)
            .stroke((BLACK, 10.0))
            .build(),
        Transform::from_xyz(0.0, 0.0, 1.0),
    ));

    let monitors = all_monitors().expect("Failed to get all monitors");
    let (desktop_bounds, desktop_color_image, desktop_gray_image) = capture_desktop().expect("Unable to capture desktop");

    println!("Desktop bounds: {:?}", desktop_bounds);
    let image_color = Image::from_dynamic(desktop_color_image, true, RenderAssetUsages::all());
    let image_gray = Image::from_dynamic(desktop_gray_image, true, RenderAssetUsages::all());
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
        let width = monitor.width() as f32 * scale;
        let height = monitor.height() as f32 * scale;
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
                #[cfg(target_os = "windows")]
                present_mode: PresentMode::Mailbox,
                #[cfg(target_os = "macos")]
                present_mode: PresentMode::Immediate,
                desired_maximum_frame_latency: std::num::NonZero::new(1),
                focused: i == 0,
                position: WindowPosition::At(IVec2::new(x, y)),
                decorations: false,
                visible: false,
                ..default()
            })
            .id();

        let half_width = width / 2.0;
        let half_height = height / 2.0;
        let camera = commands
            .spawn((
                Camera2d::default(),
                Msaa::Sample4,
                Transform::IDENTITY.with_translation(Vec3::new(x as f32 + half_width, -y as f32 - half_height as f32, 0.0)),
                Camera {
                    target: RenderTarget::Window(WindowRef::Entity(window)),
                    ..default()
                },
            ))
            .id();

        commands.spawn((Text::new(format!("Window: {}", i + 1)), node.clone(), TargetCamera(camera)));

        if monitor.is_primary() {
            commands.insert_resource(PrimaryCamera(camera));
            commands.spawn((PerfUiDefaultEntries::default(), TargetCamera(camera)));
        }
    }

    // let shapes = [
    //     meshes.add(Rectangle::new(width, height)),
    //     meshes.add(Line2d)
    // ]
}

fn toggle_debug(
    mut commands: Commands,
    q_root: Query<Entity, With<PerfUiRoot>>,
    kbd: Res<ButtonInput<KeyCode>>,
    camera: Res<PrimaryCamera>,
) {
    if kbd.just_pressed(KeyCode::KeyD) {
        if let Ok(e) = q_root.get_single() {
            commands.entity(e).despawn_recursive();
        } else {
            commands.spawn((PerfUiDefaultEntries::default(), camera.get()));
        }
    }
}

fn update_cursor(mouse: Res<MousePosition>, mut gizmos: Gizmos<MyOverlayGizmos>) {
    let pos = mouse.get();
    let pos = IVec2::new(pos.x, -pos.y).as_vec2();
    gizmos.circle_2d(pos, 10.0, GREEN);
}

fn handle_keypress(
    mut exit: EventWriter<AppExit>,
    keyboard: Res<ButtonInput<KeyCode>>,
    time: Res<Time>,
    mut config_store: ResMut<GizmoConfigStore>,
) {
    let (my_config, _) = config_store.config_mut::<MyOverlayGizmos>();

    if keyboard.just_pressed(KeyCode::Escape) {
        exit.send(AppExit::Success);
    } else if keyboard.just_pressed(KeyCode::KeyQ) {
        my_config.enabled ^= true;
        println!("Overlay gizmos enabled: {}", my_config.enabled);
    }
}
