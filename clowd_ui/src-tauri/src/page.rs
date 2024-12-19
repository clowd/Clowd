use anyhow::Result;
use rfd::MessageDialog;
use std::{collections::HashMap, path::PathBuf};
use tauri::{utils::config::WindowConfig, AppHandle, Manager};

use crate::settings::ClowdSettingsMutex;

pub enum ShowPage {
    CanvasEmpty,
    CanvasImage(PathBuf, u32, u32),
}

pub fn show_window(app: AppHandle, title: &str, page: ShowPage) {
    const CANVAS_PAGE: &str = "canvas";
    if let Err(e) = match page {
        ShowPage::CanvasEmpty => show_window_impl(app, CANVAS_PAGE, title, 800.0, 600.0, HashMap::new()),
        ShowPage::CanvasImage(path, w, h) => {
            let mut query = HashMap::new();
            query.insert("imagePath", path.to_string_lossy().to_string());
            query.insert("width", w.to_string());
            query.insert("height", h.to_string());
            show_window_impl(app, CANVAS_PAGE, title, w as f64 + 100.0, h as f64 + 100.0, query)
        }
    } {
        error!("Error opening window: {}", e);
        MessageDialog::new()
            .set_title("Clowd Error")
            .set_description(&format!("Error opening Window: {}", e))
            .set_buttons(rfd::MessageButtons::Ok)
            .set_level(rfd::MessageLevel::Error)
            .show();
    }
}

fn show_window_impl(app: AppHandle, page: &str, title: &str, width: f64, height: f64, query: HashMap<&str, String>) -> Result<()> {
    let view_data_dir = {
        let settings = app.state::<ClowdSettingsMutex>();
        let settings = settings.read().unwrap();
        settings.webview_data_dir.clone()
    };

    let now = chrono::Local::now();
    let session_name = now
        .format(&format!("{}_%Y%m%d_%H%M%S_%3f", page))
        .to_string();

    // turn query into a query string
    let query_str = query
        .iter()
        .map(|(k, v)| format!("{}={}", k, urlencoding::encode(v)))
        .collect::<Vec<String>>()
        .join("&");

    let url = format!("index.html/{}?{}", page, query_str);
    let url = tauri::WebviewUrl::App(url.into());

    let window = WindowConfig {
        label: session_name,
        create: true,
        url,
        drag_drop_enabled: true,
        center: true,
        x: None,
        y: None,
        width,
        height,
        min_width: Some(200.0),
        min_height: Some(200.0),
        resizable: true,
        maximizable: true,
        minimizable: true,
        closable: true,
        title: title.to_string(),
        fullscreen: false,
        focus: true,
        transparent: false,
        maximized: false,
        visible: false,
        decorations: true,
        hidden_title: true,
        accept_first_mouse: false,
        zoom_hotkeys_enabled: false,
        browser_extensions_enabled: false,
        devtools: Some(true),
        ..Default::default()
    };

    tauri::WebviewWindowBuilder::from_config(&app, &window)?
        .data_directory(view_data_dir)
        .build()?;

    Ok(())
}
