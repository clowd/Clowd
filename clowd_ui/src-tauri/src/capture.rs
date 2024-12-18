use anyhow::Result;
use chrono::Local;
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
enum ProgramResult {
    Cancelled,
    CopyToClipboard,
    SaveFile(String),
    Edit,
    Video(ProgramResultRect),
}

pub enum CaptureResult {
    NotHandled,
    SaveFile(String),
    Edit(String),
}

pub fn start_capture_blocking(session_dir: PathBuf, name_template: String) -> Result<CaptureResult> {
    let mut capture_exe = std::env::current_exe()?;
    capture_exe.pop();
    capture_exe.push("clowd_capture.exe");

    let now = Local::now();
    let name = now.format(&name_template).to_string();

    fs::create_dir_all(&session_dir)?;
    let capture_path = session_dir.join(name.clone() + ".png");
    let result_path = session_dir.join(name + ".result");

    let mut cmd = std::process::Command::new(capture_exe)
        .arg("--capture-path")
        .arg(&capture_path)
        .arg("--result-path")
        .arg(&result_path)
        .spawn()?;

    cmd.wait()?;

    if !result_path.exists() {
        bail!("Capture failed, no result found. Check logs for a more detailed error.");
    }

    let result = std::fs::read_to_string(&result_path)?;
    let result: ProgramResult = serde_json::from_str(&result)?;

    match &result {
        ProgramResult::SaveFile(path) => {
            let mut path = PathBuf::from(path);
            path.pop();
            let last_save_dir = path.to_string_lossy().to_string();
            Ok(CaptureResult::SaveFile(last_save_dir))
        }
        ProgramResult::Edit => {
            if !capture_path.exists() {
                bail!("Capture failed, no output image found. Check logs for a more detailed error.");
            }
            Ok(CaptureResult::Edit(capture_path.to_string_lossy().to_string()))
        }
        _ => Ok(CaptureResult::NotHandled),
    }
}
