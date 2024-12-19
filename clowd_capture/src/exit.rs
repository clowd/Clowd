use arboard::{Clipboard, ImageData};
use bevy::prelude::Resource;
use image::{DynamicImage, GenericImageView};
use rfd::MessageDialog;

use crate::{
    cli::{self, ProgramResultRect},
    ScreenPoint, ScreenRect, UserAction,
};

#[derive(Resource)]
pub struct AfterExitAction {
    pub screenshot: DynamicImage,
    pub screenshot_selection: ScreenRect,
    pub raw_selection: ScreenRect,
    pub screenshot_mouse_pt: ScreenPoint,
    pub action: UserAction,
    pub capture_path: String,
    pub result_path: String,
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
            UserAction::SelectColor => {
                let pt = self.screenshot_mouse_pt.to_u32();
                let color = cropped.get_pixel(pt.x, pt.y);
                let color = cli::ProgramResultColor {
                    r: color[0],
                    g: color[1],
                    b: color[2],
                };

                if let Err(e) = cli::write_result_file(cli::ProgramResult::SelectColor(color), &self.result_path) {
                    MessageDialog::new()
                        .set_title("Error: Pick Color")
                        .set_description(&format!("Failed to write result: {}", e))
                        .set_buttons(rfd::MessageButtons::Ok)
                        .set_level(rfd::MessageLevel::Error)
                        .show();
                }
            }
            UserAction::Edit | UserAction::Save => {
                let action = if self.action == UserAction::Edit {
                    cli::ProgramResult::Edit
                } else {
                    cli::ProgramResult::SaveFile
                };

                if let Err(e) = cropped.save(&self.capture_path) {
                    MessageDialog::new()
                        .set_title("Error: Save Image")
                        .set_description(&format!("Failed to save image: {}", e))
                        .set_buttons(rfd::MessageButtons::Ok)
                        .set_level(rfd::MessageLevel::Error)
                        .show();
                } else {
                    if let Err(e) = cli::write_result_file(action, &self.result_path) {
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
