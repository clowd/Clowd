use anyhow::Result;
use chrono::Local;
use rfd::FileDialog;
use std::{fs, path::PathBuf};

use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ProgramResultRect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ProgramResultColor {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub enum ProgramResult {
    Cancelled,
    CopyToClipboard,
    SaveFile,
    Edit,
    SelectColor(ProgramResultColor),
    Video(ProgramResultRect),
}

pub enum CaptureResult {
    NotHandled,
    UpdateLastSaveDir(PathBuf),
    EditImage(PathBuf),
}

pub fn start_capture_blocking(session_dir: PathBuf, name_template: String, initial_save_dir: PathBuf) -> Result<CaptureResult> {
    let mut capture_exe = std::env::current_exe()?;
    capture_exe.pop();
    capture_exe.push("clowd_capture.exe");

    let now = Local::now();
    let session_name = now
        .format("session_%Y%m%d_%H%M%S_%3f")
        .to_string();
    let user_name = now.format(&name_template).to_string();
    let user_file_name = user_name.clone() + ".png";

    fs::create_dir_all(&session_dir)?;
    let capture_path = session_dir.join(session_name.clone() + ".png");
    let result_path = session_dir.join(session_name + ".result");

    let mut cmd = std::process::Command::new(capture_exe)
        .arg("--capture-path")
        .arg(&capture_path)
        .arg("--result-path")
        .arg(&result_path)
        .spawn()?;

    cmd.wait()?;

    if !result_path.exists() {
        warn!("Capture failed, no result found. Check logs for a more detailed error.");
        return Ok(CaptureResult::NotHandled);
    }

    let result = std::fs::read_to_string(&result_path)?;
    let result: ProgramResult = serde_json::from_str(&result)?;
    // let _ = fs::remove_file(&result_path);

    match &result {
        ProgramResult::SaveFile => {
            let file_opt = FileDialog::new()
                .set_title("Save Image")
                .add_filter("PNG", &["png"])
                .add_filter("JPEG", &["jpg", "jpeg", "jfif"])
                .add_filter("BMP", &["bmp"])
                .add_filter("TIFF", &["tiff", "tif"])
                .add_filter("GIF", &["gif"])
                .add_filter("WEBP", &["webp"])
                .add_filter("AVIF", &["avif"])
                .set_directory(initial_save_dir)
                .set_file_name(user_file_name)
                .save_file();

            if let Some(mut file) = file_opt {
                if !capture_path.exists() {
                    bail!("Capture failed, no output image found. Check logs for a more detailed error.");
                }
                fs::copy(capture_path, &file)?;
                file.pop();
                Ok(CaptureResult::UpdateLastSaveDir(file))
            } else {
                Ok(CaptureResult::NotHandled)
            }
        }
        ProgramResult::Edit => {
            if !capture_path.exists() {
                bail!("Capture failed, no output image found. Check logs for a more detailed error.");
            }
            Ok(CaptureResult::EditImage(capture_path))
        }
        ProgramResult::Video(_rect) => {
            // TODO: Implement video
            Ok(CaptureResult::NotHandled)
        }
        ProgramResult::SelectColor(_clr) => {
            // TODO: Implement color picker
            Ok(CaptureResult::NotHandled)
        }
        _ => Ok(CaptureResult::NotHandled),
    }
}
