use crate::util;
use std::path::PathBuf;

#[tauri::command]
pub fn greet(name: &str) -> String {
    format!("Hello, {}! You've been greeted from Rust!", name)
}

#[tauri::command]
pub fn get_image_uri(file_path: String) -> Result<String, String> {
    let path: PathBuf = file_path.into();
    let uri = util::get_image_uri(&path).map_err(|e| e.to_string())?;
    Ok(uri)
}

#[tauri::command]
pub async fn show_dialog_error(title: String, body: String, window: tauri::Window) -> Result<(), String> {
    rfd::MessageDialog::new()
        .set_title(&title)
        .set_description(&body)
        .set_buttons(rfd::MessageButtons::Ok)
        .set_level(rfd::MessageLevel::Error)
        .set_parent(&window)
        .show();
    Ok(())
}

#[tauri::command]
pub fn show_current_window(window: tauri::Window) {
    if let Err(e) = window.show() {
        error!("Error showing window: {}", e);
    }
}