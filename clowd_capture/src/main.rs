mod cli;
mod entities;
mod exit;
mod geometry;
mod resources;
mod system;

#[macro_use]
extern crate anyhow;

use crate::entities::*;
use crate::geometry::*;
use crate::resources::*;

use bevy::{
    asset::RenderAssetUsages,
    core::FrameCount,
    input::mouse::MouseWheel,
    log::*,
    prelude::*,
    render::camera::RenderTarget,
    render::settings::RenderCreation,
    window::{PresentMode, RawHandleWrapper, WindowCreated, WindowRef, WindowResolution},
    winit::cursor::CursorIcon,
};

use bevy_prototype_lyon::prelude::*;
use clap::Parser;
use euclid::Transform2D;
use euclid::Vector2D;
use iyes_perf_ui::prelude::*;
use raw_window_handle::RawWindowHandle;
use system::SystemInterop;

fn main() {
    let args = cli::ProgramArgs::parse();
    let mut accents = AccentColors::default();

    if let Some(accent) = &args.accent_color {
        match cli::cli_parse_color(accent) {
            Ok((color, dark, light)) => {
                accents.accent = color;
                accents.accent_dark = dark;
                accents.accent_light = light;
            }
            Err(e) => {
                error!("Error parsing accent color: {}", e);
            }
        }
    }

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
        .insert_resource(args)
        .insert_resource(accents)
        .init_resource::<MousePosition>()
        .init_resource::<CaptureState>()
        .init_resource::<VirtualDesktop>()
        .add_systems(PreStartup, buttonpanel::buttonpanel_init)
        .add_systems(Startup, setup)
        .add_systems(Update, startup_animation)
        .add_systems(PreUpdate, mouse_update)
        .add_systems(Update, (window_created, background_update, buttonpanel::buttonpanel_update))
        .add_systems(Update, (selection::selection_update, crosshair::crosshair_update))
        .add_systems(Update, handle_actions.before(iyes_perf_ui::PerfUiSet::Setup))
        .run();
}

fn setup(mut commands: Commands, mut images: ResMut<Assets<Image>>) {
    let monitors = SystemInterop::all_monitor_bounds();
    let desktop_bounds = SystemInterop::virtual_desktop_bounds().to_f32();
    let (desktop_color_image, desktop_gray_image) = SystemInterop::capture_desktop();

    commands.insert_resource(RawScreenshotData(desktop_color_image.clone()));

    info!("Desktop bounds: {:?}", desktop_bounds);
    let image_color = Image::from_dynamic(desktop_color_image, true, RenderAssetUsages::all());
    let image_gray = Image::from_dynamic(desktop_gray_image, true, RenderAssetUsages::all());
    let color_handle = images.add(image_color);
    let gray_handle = images.add(image_gray);

    let mut camera_entities = Vec::new();

    // spawn background images
    let img_tx = (desktop_bounds.width() / 2.0) + desktop_bounds.left();
    let img_ty = (-desktop_bounds.height() / 2.0) - desktop_bounds.top();
    commands.spawn((
        Sprite::from_image(gray_handle),
        Transform::from_translation(Vec3::new(img_tx, img_ty, Z_BGGRAY)),
        ImageGrayTag,
    ));
    commands.spawn((
        Sprite::from_image(color_handle.clone()),
        Transform::from_translation(Vec3::new(img_tx, img_ty, Z_BGCOLOR)),
        ImageColorTag,
    ));

    for (i, (bounds, scale, primary)) in monitors.iter().enumerate() {
        let x = bounds.left();
        let y = bounds.top();
        let width = bounds.width() as f32 / scale;
        let height = bounds.height() as f32 / scale;
        info!("Monitor {}: bounds={:?}, scale={}", i + 1, bounds, scale);

        let window = commands
            .spawn(Window {
                title: "Clowd Capture".to_owned(),
                resolution: WindowResolution::new(width, height),
                present_mode: PresentMode::AutoNoVsync,
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
        let cam_transform = Transform::IDENTITY
            .with_scale(Vec3::splat(*scale))
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

        // let node = Node {
        //     position_type: PositionType::Absolute,
        //     top: Val::Px(12.0),
        //     left: Val::Px(12.0),
        //     ..default()
        // };
        // commands.spawn((Text::new(format!("Window: {}", i + 1)), node, TargetCamera(camera)));

        if *primary {
            commands.insert_resource(PrimaryCamera(camera));
        }

        camera_entities.push((camera, bounds.clone(), cam_transform.clone(), *scale, *primary));
    }

    commands.insert_resource(CameraEntities(camera_entities));
}

fn startup_animation(mut commands: Commands, mut window: Query<&mut Window>, frames: Res<FrameCount>, time: Res<Time>) {
    if frames.0 == 10 {
        for mut window in window.iter_mut() {
            window.visible = true;
            // window.window_level = bevy::window::WindowLevel::AlwaysOnTop;
        }
        commands.insert_resource(FirstRenderTime(time.elapsed_secs_f64()));
    }
}

fn window_created(mut events: EventReader<WindowCreated>, windows: Query<(&RawHandleWrapper, &Window)>) {
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

fn background_update(
    mut queries: ParamSet<(
        Query<&mut Transform, With<ImageGrayTag>>,
        Query<(&mut Sprite, &mut Transform), With<ImageColorTag>>,
    )>,
    mouse: Res<MousePosition>,
    desktop: Res<VirtualDesktop>,
    capture: Res<CaptureState>,
) {
    // update background images
    let pos = mouse.get_position();
    let zoom = mouse.get_zoom();
    let desktop_bounds = desktop.0.to_f32();
    let image_transform = Transform2D::<f32, ScreenUnit, ScreenUnit>::identity()
        // top-left align the image itself
        .then_translate(Vector2D::new(desktop_bounds.width() / 2.0, desktop_bounds.height() / 2.0))
        // align the image to the top-left corner of the desktop
        .then_translate(Vector2D::new(desktop_bounds.left(), desktop_bounds.top()))
        // zoom around the mouse cursor
        .then_translate(-pos.to_vector())
        .then_scale(zoom, zoom)
        .then_translate(pos.to_vector())
        // flip Y into bevy space
        .then_scale(1.0, -1.0);

    if let Ok(mut e) = queries.p0().get_single_mut() {
        let new_origin = image_transform.transform_point(ScreenPointF::new(0.0, 0.0));
        let transform = Transform::from_xyz(new_origin.x, new_origin.y, Z_BGGRAY).with_scale(Vec3::new(zoom, zoom, 1.0));
        e.translation = transform.translation;
        e.scale = transform.scale;
        e.rotation = transform.rotation;
    }

    if let Ok(mut e) = queries.p1().get_single_mut() {
        let selection_rect = mouse
            .get_selection_in_progress()
            .map_or_else(|| capture.selection, |v| Some(v));

        let mut transform = None;
        if let Some(capture_rect) = selection_rect {
            if capture_rect.width() > 0 && capture_rect.height() > 0 {
                let capture_transform =
                    Transform2D::<f32, ScreenUnit, ScreenUnit>::identity().then_translate(-desktop.0.to_f32().top_left().to_vector());
                let capture_rect = capture_transform.outer_transformed_rect(&capture_rect.to_f32());
                e.0.color.set_alpha(1.0);
                e.0.rect = Some(capture_rect.to_bevy());
                transform = Some(
                    Transform2D::<f32, ScreenUnit, ScreenUnit>::identity()
                        .then_translate(Vector2D::new(capture_rect.width() / 2.0, capture_rect.height() / 2.0))
                        .then_translate(Vector2D::new(desktop_bounds.left(), desktop_bounds.top()))
                        .then_translate(capture_rect.top_left().to_vector())
                        .then_translate(-pos.to_vector())
                        .then_scale(zoom, zoom)
                        .then_translate(pos.to_vector())
                        .then_scale(1.0, -1.0),
                );
            }
        }

        if let Some(transform) = transform {
            let new_origin = transform.transform_point(ScreenPointF::new(0.0, 0.0));
            let transform = Transform::from_xyz(new_origin.x, new_origin.y, Z_BGCOLOR).with_scale(Vec3::new(zoom, zoom, 1.0));
            e.1.translation = transform.translation;
            e.1.scale = transform.scale;
            e.1.rotation = transform.rotation;
        } else {
            e.0.color.set_alpha(0.0);
            e.0.rect = None;
        }
    }
}

fn mouse_update(
    mut commands: Commands,
    mut mouse: ResMut<MousePosition>,
    mut scroll: EventReader<MouseWheel>,
    mut capture: ResMut<CaptureState>,
    mut window: Query<(Entity, &mut Window)>,
    desktop: Res<VirtualDesktop>,
    keyboard: Res<ButtonInput<KeyCode>>,
    buttons: Res<ButtonInput<MouseButton>>,
    first_render: Option<Res<FirstRenderTime>>,
) {
    let mut set_cursor = |cursor: CursorIcon, visible: bool| {
        for mut window in window.iter_mut() {
            window.1.cursor_options.visible = visible;
            commands
                .entity(window.0)
                .insert(cursor.clone());
        }
    };

    if capture.selection.is_none() {
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
    } else {
        mouse.set_zoom(1.0);
    }

    let pos = mouse.update_position();

    if first_render.is_some() {
        mouse.set_anchored(capture.selection.is_none());
    }

    if capture.selection.is_none() {
        set_cursor(CursorIcon::System(bevy::window::SystemCursorIcon::Default), false);
        if buttons.just_pressed(MouseButton::Left) {
            mouse.start_selection();
        }
    } else {
        if let MouseState::SizingSel(hit, _) = mouse.get_button_state() {
            set_cursor(hit.to_cursor(), true);
        } else {
            let hit = HitTest::hit_test_rect(pos, capture.selection);
            set_cursor(hit.to_cursor(), true);
            if buttons.just_pressed(MouseButton::Left) {
                mouse.start_sizing(hit, capture.selection.unwrap());
            }
        }
    }

    if buttons.just_released(MouseButton::Left) {
        if let Some(selection) = mouse.get_selection_in_progress() {
            capture.selection = selection.intersection(&desktop.0);
        }
        mouse.button_up();
    }
}

fn handle_actions(
    mut commands: Commands,
    mut exit: EventWriter<AppExit>,
    mut capture: ResMut<CaptureState>,
    mut window: Query<&mut Window>,
    keyboard: Res<ButtonInput<KeyCode>>,
    buttons: Res<ButtonInput<MouseButton>>,
    debug_query: Query<Entity, With<PerfUiRoot>>,
    button_query: Query<(&Interaction, &buttonpanel::ButtonPanelButtonTag)>,
    camera: Res<PrimaryCamera>,
    screenshot: Res<RawScreenshotData>,
    desktop: Res<VirtualDesktop>,
    args: Res<cli::ProgramArgs>,
    mouse: Res<MousePosition>,
) {
    let mut exit_app = || {
        for mut window in window.iter_mut() {
            window.visible = false;
        }
        exit.send(AppExit::Success);
    };

    let actions: Vec<(&[KeyCode], UserAction)> = vec![
        (&[KeyCode::KeyS], UserAction::Save),
        (&[KeyCode::KeyC, KeyCode::Insert], UserAction::Copy),
        (&[KeyCode::KeyX, KeyCode::Escape, KeyCode::F4], UserAction::Exit),
        (&[KeyCode::KeyD], UserAction::ToggleDebug),
        (&[KeyCode::KeyB, KeyCode::KeyH], UserAction::SelectColor),
        (&[KeyCode::KeyR, KeyCode::Delete], UserAction::Reset),
        (&[KeyCode::KeyV], UserAction::Video),
        (&[KeyCode::KeyE, KeyCode::Enter, KeyCode::KeyP], UserAction::Edit),
    ];

    let pressed_buttons: Vec<UserAction> = if buttons.just_pressed(MouseButton::Left) {
        button_query
            .iter()
            .filter(|(interaction, _)| **interaction == Interaction::Pressed)
            .map(|(_, tag)| tag.action)
            .collect()
    } else {
        Vec::new()
    };

    let current_action = actions.iter().find(|(keys, action)| {
        keys.iter()
            .any(|k| keyboard.just_pressed(*k) || pressed_buttons.iter().any(|b| *b == *action))
    });

    if let Some((_, action)) = current_action {
        match action {
            UserAction::Save | UserAction::Copy | UserAction::Video | UserAction::Edit | UserAction::SelectColor => {
                if let Some(capture) = capture.selection {
                    let capture_transform =
                        Transform2D::<i32, ScreenUnit, ScreenUnit>::identity().then_translate(-desktop.0.top_left().to_vector());
                    let mouse = capture_transform.transform_point(mouse.get_position().to_i32());
                    let selection = capture_transform.outer_transformed_rect(&capture);
                    commands.insert_resource(exit::AfterExitAction {
                        screenshot: screenshot.0.clone(),
                        screenshot_selection: selection,
                        screenshot_mouse_pt: mouse,
                        raw_selection: capture,
                        capture_path: args.capture_path.clone(),
                        result_path: args.result_path.clone(),
                        action: action.clone(),
                    });
                    exit_app();
                }
            }
            UserAction::Exit => {
                exit_app();
            }
            UserAction::ToggleDebug => {
                if let Ok(e) = debug_query.get_single() {
                    commands.entity(e).despawn_recursive();
                } else {
                    commands.spawn((PerfUiDefaultEntries::default(), camera.get()));
                }
            }
            UserAction::Reset => {
                capture.selection = None;
            }
        }
    }
}
