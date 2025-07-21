use bevy::{color::Color, prelude::Resource};
use clap::*;
use serde::{Deserialize, Serialize};

#[derive(Parser, Debug, Clone, Resource)]
#[command(version, about, long_about = None)]
pub struct ProgramArgs {
    #[arg(long)]
    pub capture_path: String,
    #[arg(long)]
    pub result_path: String,
    #[arg(long)]
    pub accent_color: Option<String>,
    #[arg(long)]
    pub low_perf_mode: Option<bool>,
}

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

fn util_string_split(input: &str, delimiter: char) -> Vec<String> {
    input
        .split(delimiter)
        .map(|s| s.to_string())
        .collect()
}

fn adjust_brightness(r: u8, g: u8, b: u8, factor: f32) -> (u8, u8, u8) {
    assert!((0.0..=1.0).contains(&factor), "Factor must be between 0 and 1");

    let lighten = |channel: u8| {
        let adjusted = channel as f32 + (255.0 - channel as f32) * factor;
        adjusted.round() as u8
    };

    let darken = |channel: u8| {
        let adjusted = channel as f32 * (1.0 - factor);
        adjusted.round() as u8
    };

    // Use `factor > 0` to determine lightening or darkening
    if factor >= 0.0 {
        (lighten(r), lighten(g), lighten(b))
    } else {
        (darken(r), darken(g), darken(b))
    }
}

pub fn cli_parse_color(input: &str) -> Result<(Color, Color, Color), String> {
    let parts = util_string_split(input, ',');
    let parts: Vec<&str> = parts.iter().map(|s| s.as_str()).collect();
    if parts.len() != 3 {
        return Err(format!("Not a valid color: {}", input));
    }

    let r = parts[0]
        .parse::<u8>()
        .map_err(|_| format!("Invalid red value: {}", parts[0]))?;
    let g = parts[1]
        .parse::<u8>()
        .map_err(|_| format!("Invalid green value: {}", parts[1]))?;
    let b = parts[2]
        .parse::<u8>()
        .map_err(|_| format!("Invalid blue value: {}", parts[2]))?;

    let darkened = adjust_brightness(r, g, b, 0.2);
    let lightened = adjust_brightness(r, g, b, 0.2);

    let darkened = Color::srgb_u8(darkened.0, darkened.1, darkened.2);
    let lightened = Color::srgb_u8(lightened.0, lightened.1, lightened.2);
    let color = Color::srgb_u8(r, g, b);

    Ok((color, darkened, lightened))
}

pub fn write_result_file(result: ProgramResult, path: &str) -> Result<(), String> {
    let s = serde_json::to_string(&result).map_err(|e| format!("Error serializing result: {}", e))?;
    std::fs::write(path, s).map_err(|e| format!("Error writing result file: {}", e))?;
    Ok(())
}
