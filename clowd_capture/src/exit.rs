use arboard::{Clipboard, ImageData};
use bevy::prelude::Resource;
use image::DynamicImage;
use rfd::{FileDialog, MessageDialog};

use crate::{ScreenRect, UserAction};

#[derive(Resource)]
pub struct AfterExitAction {
    pub screenshot: DynamicImage,
    pub selection: ScreenRect,
    pub action: UserAction,
}

impl Drop for AfterExitAction {
    fn drop(&mut self) {
        let selection = self.selection.to_u32();
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
                }
                Err(e) => {
                    MessageDialog::new()
                        .set_title("Error: Copy Image")
                        .set_description(&format!("Failed to open clipboard: {}", e))
                        .set_buttons(rfd::MessageButtons::Ok)
                        .set_level(rfd::MessageLevel::Error)
                        .show();
                }
            },
            UserAction::Save => {
                let photos_dir = dirs::picture_dir().unwrap_or_else(|| dirs::home_dir().unwrap());
                let file_opt = FileDialog::new()
                    .set_title("Save Image")
                    .add_filter("PNG", &["png"])
                    .add_filter("JPEG", &["jpg", "jpeg", "jfif"])
                    .add_filter("BMP", &["bmp"])
                    .add_filter("TIFF", &["tiff", "tif"])
                    .add_filter("GIF", &["gif"])
                    .add_filter("WEBP", &["webp"])
                    .add_filter("AVIF", &["avif"])
                    .set_directory(photos_dir)
                    .save_file();

                if let Some(file) = file_opt {
                    if let Err(e) = cropped.save(file) {
                        MessageDialog::new()
                            .set_title("Error: Save Image")
                            .set_description(&format!("Failed to save image: {}", e))
                            .set_buttons(rfd::MessageButtons::Ok)
                            .set_level(rfd::MessageLevel::Error)
                            .show();
                    }
                }
            }
            UserAction::Edit => todo!(),
            UserAction::Video => todo!(),
            _ => (),
        }
    }
}
