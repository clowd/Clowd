use anyhow::Result;
use base64::{engine::general_purpose, Engine};
use image::{ImageFormat, ImageReader};
use std::{
    fs,
    io::Cursor,
    path::{Path, PathBuf},
};

fn get_image_size_and_uri(image: PathBuf) -> Result<(u32, u32, String)> {
    let img_stream = fs::read(image)?;
    let fmt_cursor = Cursor::new(&img_stream);
    let fmt_reader = ImageReader::new(fmt_cursor).with_guessed_format()?;

    let image_format = fmt_reader
        .format()
        .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::InvalidData, "Could not determine image format"))?;

    let dims = &fmt_reader.into_dimensions()?;
    let format_str = match image_format {
        ImageFormat::Jpeg => "jpeg",
        ImageFormat::Png => "png",
        ImageFormat::Gif => "gif",
        ImageFormat::Bmp => "bmp",
        ImageFormat::Ico => "ico",
        ImageFormat::Tiff => "tiff",
        ImageFormat::WebP => "webp",
        ImageFormat::Avif => "avif",
        _ => bail!("Unsupported image format: {:?}", image_format),
    };
    let b64 = general_purpose::URL_SAFE_NO_PAD.encode(img_stream);
    let uri = format!("data:image/{};base64,{}", format_str, b64);

    Ok((dims.0, dims.1, uri))
}

pub fn get_image_size<P: AsRef<Path>>(image: P) -> Result<(u32, u32)> {
    let (w, h, _) = get_image_size_and_uri(image.as_ref().to_path_buf())?;
    Ok((w, h))
}

pub fn get_image_uri<P: AsRef<Path>>(image: P) -> Result<String> {
    let (_, _, uri) = get_image_size_and_uri(image.as_ref().to_path_buf())?;
    Ok(uri)
}
