use anyhow::Result;
use image::ImageReader;
use std::{collections::HashMap, fs, io::Cursor, path::PathBuf};
use tauri::{utils::config::WindowConfig, AppHandle, Manager};

use crate::settings::ClowdSettingsMutex;

pub fn get_image_size(image: PathBuf) -> Result<(u32, u32)> {
    let img_stream = fs::read(image)?;
    let fmt_cursor = Cursor::new(&img_stream);
    let fmt_reader = ImageReader::new(fmt_cursor).with_guessed_format()?;
    let dims = &fmt_reader.into_dimensions()?;
    Ok(*dims)
}

pub fn show_window(app: AppHandle, page: &str, title: &str, width: f64, height: f64, query: HashMap<&str, String>) -> Result<()> {
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
        visible: true,
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
