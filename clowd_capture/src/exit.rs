use std::path::PathBuf;

use arboard::{Clipboard, ImageData};
use bevy::prelude::Resource;
use image::DynamicImage;
use rfd::{FileDialog, MessageDialog};

use crate::{
    cli::{self, ProgramResultRect},
    ScreenRect, UserAction,
};

#[derive(Resource)]
pub struct AfterExitAction {
    pub screenshot: DynamicImage,
    pub screenshot_selection: ScreenRect,
    pub raw_selection: ScreenRect,
    pub action: UserAction,
    pub capture_path: String,
    pub result_path: String,
    pub last_save_dir: Option<String>,
}

impl Drop for AfterExitAction {
    fn drop(&mut self) {
        let selection = self.screenshot_selection.to_u32();
        let cropped = self
            .screenshot
            .crop_imm(selection.min_x(), selection.min_y(), selection.max_x(), selection.max_y());

        match self.action {
            UserAction::Copy => match Clipboard::new() {
                Ok(mut clipboard) => {
                    let width = cropped.width() as usize;
                    let height = cropped.height() as usize;
                    let cropped_bytes = cropped.into_bytes();
                    let image_data = ImageData {
                        width,
                        height,
                        bytes: cropped_bytes.into(),
                    };
                    if let Err(e) = clipboard.set_image(image_data) {
                        MessageDialog::new()
                            .set_title("Error: Copy Image")
                            .set_description(&format!("Failed to copy image to clipboard: {}", e))
                            .set_buttons(rfd::MessageButtons::Ok)
                            .set_level(rfd::MessageLevel::Error)
                            .show();
                    }
                    let _ = cli::write_result_file(cli::ProgramResult::CopyToClipboard, &self.result_path);
                }
                Err(e) => {
                    MessageDialog::new()
                        .set_title("Error: Copy Image")
                        .set_description(&format!("Failed to open clipboard: {}", e))
                        .set_buttons(rfd::MessageButtons::Ok)
                        .set_level(rfd::MessageLevel::Error)
                        .show();
                    let _ = cli::write_result_file(cli::ProgramResult::CopyToClipboard, &self.result_path);
                }
            },
            UserAction::Save => {
                let initial_dir = self
                    .last_save_dir
                    .clone()
                    .map(|f| PathBuf::from(f))
                    .unwrap_or_else(|| dirs::picture_dir().unwrap_or_else(|| dirs::home_dir().unwrap()));

                let file_opt = FileDialog::new()
                    .set_title("Save Image")
                    .add_filter("PNG", &["png"])
                    .add_filter("JPEG", &["jpg", "jpeg", "jfif"])
                    .add_filter("BMP", &["bmp"])
                    .add_filter("TIFF", &["tiff", "tif"])
                    .add_filter("GIF", &["gif"])
                    .add_filter("WEBP", &["webp"])
                    .add_filter("AVIF", &["avif"])
                    .set_directory(initial_dir)
                    .save_file();

                if let Some(file) = file_opt {
                    if let Err(e) = cropped.save(&file) {
                        MessageDialog::new()
                            .set_title("Error: Save Image")
                            .set_description(&format!("Failed to save image: {}", e))
                            .set_buttons(rfd::MessageButtons::Ok)
                            .set_level(rfd::MessageLevel::Error)
                            .show();
                    }
                    let _ = cli::write_result_file(cli::ProgramResult::SaveFile(file.to_string_lossy().to_string()), &self.result_path);
                } else {
                    let _ = cli::write_result_file(cli::ProgramResult::Cancelled, &self.result_path);
                }
            }
            UserAction::Edit => {
                if let Err(e) = cropped.save(&self.capture_path) {
                    MessageDialog::new()
                        .set_title("Error: Save Image")
                        .set_description(&format!("Failed to save image: {}", e))
                        .set_buttons(rfd::MessageButtons::Ok)
                        .set_level(rfd::MessageLevel::Error)
                        .show();
                } else {
                    if let Err(e) = cli::write_result_file(cli::ProgramResult::Edit, &self.result_path) {
                        MessageDialog::new()
                            .set_title("Error: Save Image")
                            .set_description(&format!("Failed to save image: {}", e))
                            .set_buttons(rfd::MessageButtons::Ok)
                            .set_level(rfd::MessageLevel::Error)
                            .show();
                    }
                }
            }
            UserAction::Video => {
                let rect = ProgramResultRect {
                    x: self.raw_selection.min_x(),
                    y: self.raw_selection.min_y(),
                    width: self.raw_selection.width(),
                    height: self.raw_selection.height(),
                };
                if let Err(e) = cli::write_result_file(cli::ProgramResult::Video(rect), &self.result_path) {
                    MessageDialog::new()
                        .set_title("Error: Write Result")
                        .set_description(&format!("Failed to write result: {}", e))
                        .set_buttons(rfd::MessageButtons::Ok)
                        .set_level(rfd::MessageLevel::Error)
                        .show();
                }
            }
            _ => {}
        }
    }
}
