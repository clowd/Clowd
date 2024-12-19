#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod capture;
mod commands;
mod settings;
mod util;

use std::{collections::HashMap, path::PathBuf, sync::atomic::AtomicBool};

use anyhow::Result;
use global_hotkey::{GlobalHotKeyEvent, HotKeyState};
use lazy_static::lazy_static;
use rfd::MessageDialog;
use settings::{ClowdSettingsMutex, HotkeyManager};
use tauri::{
    image::Image,
    menu::{Menu, MenuItem},
    tray::TrayIconBuilder,
    utils::config::WindowConfig,
    window::Color,
    AppHandle, Manager,
};

#[macro_use]
extern crate anyhow;

#[macro_use]
extern crate log;

lazy_static! {
    static ref EXIT_REQUESTED: AtomicBool = AtomicBool::new(false);
}

fn action_exit_app(app: AppHandle) {
    let settings = app.state::<ClowdSettingsMutex>();
    let settings = settings.read().unwrap();
    settings.save();
    EXIT_REQUESTED.store(true, std::sync::atomic::Ordering::Relaxed);
    app.exit(0);
}

fn action_start_capture(app: AppHandle) {
    let (session_dir, name_template, last_save_dir) = {
        let settings = app.state::<ClowdSettingsMutex>();
        let settings = settings.read().unwrap();
        (
            settings.session_dir.clone(),
            settings.filename_pattern.clone(),
            settings.last_capture_save_dir.clone(),
        )
    };

    std::thread::spawn(move || {
        let app = app.clone();

        match capture::start_capture_blocking(session_dir, name_template, last_save_dir) {
            Ok(res) => match res {
                capture::CaptureResult::UpdateLastSaveDir(last_dir) => {
                    let settings = app.state::<ClowdSettingsMutex>();
                    let mut settings = settings.write().unwrap();
                    settings.last_capture_save_dir = PathBuf::from(last_dir);
                    settings.save()
                }
                capture::CaptureResult::EditImage(image_path) => {
                    action_open_canvas(app, Some(image_path));
                }
                _ => {}
            },
            Err(e) => {
                MessageDialog::new()
                    .set_title("Capture Error")
                    .set_description(&format!("Error starting capture: {}", e))
                    .set_buttons(rfd::MessageButtons::Ok)
                    .set_level(rfd::MessageLevel::Error)
                    .show();
            }
        }
    });
}

fn action_open_colorpick(app: AppHandle, initial_color: Option<Color>) {
    // todo
}

fn action_open_canvas(app: AppHandle, initial_image: Option<PathBuf>) {
    let mut query = HashMap::new();
    let mut width = 800.0;
    let mut height = 600.0;

    if let Some(image) = initial_image {
        query.insert("image", image.to_string_lossy().to_string());
        if let Ok((w, h)) = util::get_image_size(image) {
            width = w as f64 + 100.0;
            height = h as f64 + 100.0;
            query.insert("width", width.to_string());
            query.insert("height", height.to_string());
        }
    }

    if let Err(e) = util::show_window(app, "canvas", "Clowd Canvas", width, height, query) {
        MessageDialog::new()
            .set_title("Canvas Error")
            .set_description(&format!("Error opening canvas: {}", e))
            .set_buttons(rfd::MessageButtons::Ok)
            .set_level(rfd::MessageLevel::Error)
            .show();
    }
}

fn action_reset_hotkeys(app: AppHandle) {
    let hotkey_manger = app.state::<HotkeyManager>();
    let (capture, colorpick) = {
        let settings = app.state::<ClowdSettingsMutex>();
        let settings = settings.read().unwrap();
        (settings.hotkey_capture.clone(), settings.hotkey_colorpick.clone())
    };

    let capture_id = capture
        .map(|h| h.id)
        .unwrap_or(u32::max_value());
    let colorpick_id = colorpick
        .map(|h| h.id)
        .unwrap_or(u32::max_value());

    let to_register = vec![capture, colorpick]
        .iter()
        .filter(|h| h.is_some())
        .map(|h| h.unwrap())
        .collect::<Vec<_>>();

    hotkey_manger.unregister_all();
    hotkey_manger.register_all(to_register.as_slice());

    let closure = move |e: GlobalHotKeyEvent| {
        let app = app.clone();
        if e.state == HotKeyState::Pressed {
            match e.id {
                id if id == capture_id => action_start_capture(app),
                id if id == colorpick_id => action_open_colorpick(app, None),
                _ => {}
            }
        }
    };

    GlobalHotKeyEvent::set_event_handler(Some(closure));
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .manage(HotkeyManager::default())
        .manage(settings::load_settings_or_default())
        .invoke_handler(tauri::generate_handler![commands::greet])
        .setup(|app| {
            let menu_capture = MenuItem::new(app, "Capture Screen", true, Some("PrtScr"))?;
            let menu_canvas = MenuItem::new(app, "Canvas", true, Some("c"))?;
            let menu_colorpick = MenuItem::new(app, "Color Picker", true, Some("p"))?;
            let menu_quit = MenuItem::new(app, "Quit", true, Some("q"))?;
            let menu = Menu::new(app)?;
            menu.append(&menu_capture)?;
            menu.append(&menu_canvas)?;
            menu.append(&menu_colorpick)?;
            menu.append(&menu_quit)?;
            TrayIconBuilder::new()
                .icon(Image::from_bytes(include_bytes!("../../../assets/white/borderless-white.ico"))?)
                .tooltip(env!("CARGO_PKG_DESCRIPTION"))
                .menu(&menu)
                // .on_tray_icon_event(handle_tray_event) TODO: double click should open settings
                .on_menu_event(move |app, event| {
                    let quit_id = menu_quit.id();
                    let capture_id = menu_capture.id();
                    match event.id {
                        id if id == quit_id => action_exit_app(app.clone()),
                        id if id == capture_id => action_start_capture(app.clone()),
                        id if id == menu_canvas.id() => action_open_canvas(app.clone(), None),
                        id if id == menu_colorpick.id() => action_open_colorpick(app.clone(), None),
                        _ => {}
                    }
                })
                .build(app)?;

            action_reset_hotkeys(app.app_handle().clone());
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building tauri application")
        .run(|_app_handle, event| match event {
            tauri::RunEvent::ExitRequested {
                api,
                ..
            } => {
                if !EXIT_REQUESTED.load(std::sync::atomic::Ordering::Relaxed) {
                    api.prevent_exit();
                }
            }
            _ => {}
        });
}
