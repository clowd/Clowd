// Copyright 2020 the Piet Authors
// SPDX-License-Identifier: Apache-2.0 OR MIT

// allows e.g. raw_data[dst_off + x * 4 + 2] = buf[src_off + x * 4 + 0];
#![allow(clippy::identity_op)]

//! Support for piet CoreGraphics back-end.

use core_graphics::sys::CGContext as SysCGContext;
// use foreign_types_shared::{ForeignType, ForeignTypeRef};
use sb_impl::CGImpl;
// use objc::{
//     class,
//     declare::ClassDecl,
//     msg_send,
//     rc::WeakPtr,
//     runtime::{Class, Object, Protocol, Sel},
//     sel, sel_impl,
// };
use std::path::Path;
use std::{ffi::c_void, marker::PhantomData};
#[cfg(feature = "png")]
use std::{fs::File, io::BufWriter};

// use cocoa::base::{id, nil};
// use cocoa::foundation::NSAutoreleasePool;
// use cocoa::{appkit::NSImage, base::YES};
use core_graphics::context::{self, CGContextRef};
use core_graphics::{color_space::CGColorSpace, context::CGContext};
// use objc2::rc::Id;
// use objc2::runtime::Object;
// use objc2::{class, msg_send, msg_send_id};
// use objc2_app_kit::NSGraphicsContext;
#[cfg(feature = "png")]
use png::{ColorType, Encoder};

#[cfg(feature = "png")]
use piet::util;
use piet::{Error, ImageBuf, ImageFormat};
#[doc(hidden)]
pub use piet_coregraphics::*;
use raw_window_handle::{HasWindowHandle, RawWindowHandle};

/// The `RenderContext` for the CoreGraphics backend, which is selected.
pub type Piet<'a> = CoreGraphicsContext<'a>;

/// The associated brush type for this backend.
///
/// This type matches `RenderContext::Brush`
pub type Brush = piet_coregraphics::Brush;

/// The associated text factory for this backend.
///
/// This type matches `RenderContext::Text`
pub type PietText = CoreGraphicsText;

/// The associated text layout type for this backend.
///
/// This type matches `RenderContext::Text::TextLayout`
pub type PietTextLayout = CoreGraphicsTextLayout;

/// The associated text layout builder for this backend.
///
/// This type matches `RenderContext::Text::TextLayoutBuilder`
pub type PietTextLayoutBuilder = CoreGraphicsTextLayoutBuilder;

/// The associated image type for this backend.
///
/// This type matches `RenderContext::Image`
pub type PietImage = CoreGraphicsImage;

/// A struct that can be used to create bitmap render contexts.
pub struct Device {
    // Since not all backends can support `Device: Sync`, make it non-Sync here to, for fewer
    // portability surprises.
    marker: std::marker::PhantomData<*const ()>,
}

unsafe impl Send for Device {}

/// A struct provides a `RenderContext` and then can have its bitmap extracted.
pub struct BitmapTarget<'a> {
    ctx: CGContext,
    height: f64,
    phantom: PhantomData<&'a ()>,
}

pub struct WindowTarget {
    // ns_view: id,
    sb: CGImpl,
    ctx: Option<CGContext>,
}

impl Device {
    /// Create a new device.
    pub fn new() -> Result<Device, piet::Error> {
        Ok(Device {
            marker: std::marker::PhantomData,
        })
    }

    pub fn window_target(&mut self, raw_handle: RawWindowHandle, pix_scale: f64) -> Result<WindowTarget, piet::Error> {
        let sb: CGImpl = crate_layer_for_window(raw_handle).map_err(|_e| piet::Error::NotSupported)?;

        // let ns_view = match raw_handle {
        //     RawWindowHandle::AppKit(handle) => handle.ns_view.as_ptr() as id,
        //     _ => panic!("unsupported handle"),
        // };

        Ok(WindowTarget {
            // ns_view,
            sb,
            ctx: None,
        })
    }

    // pub fn lock_view<T: Into<RawWindowHandle>>(&self, view: T) {
    //     use cocoa::appkit::NSView;
    //     unsafe {
    //         let handle: RawWindowHandle = view.into();
    //         match handle {
    //             RawWindowHandle::AppKit(handle) => {
    //                 let ns_view = handle.ns_view.as_ptr() as id;
    //                 ns_view.lockFocus();
    //             }
    //             _ => panic!("unsupported handle"),
    //         }
    //     }
    // }

    // pub fn unlock_view<T: Into<RawWindowHandle>>(&self, view: T) {
    //     unsafe {
    //         let handle: RawWindowHandle = view.into();
    //         match handle {
    //             RawWindowHandle::AppKit(handle) => {
    //                 let ns_view = handle.ns_view.as_ptr() as id;
    //                 ns_view.unlockFocus();
    //             }
    //             _ => panic!("unsupported handle"),
    //         }
    //     }
    // }

    // pub fn create_render_context(&mut self) -> Result<CoreGraphicsContext, piet::Error> {
    //     let context: cocoa::base::id = unsafe { msg_send![class!(NSGraphicsContext), currentContext] };
    //     let cgcontext_ptr: *mut <CGContextRef as ForeignTypeRef>::CType = unsafe { msg_send![context, CGContext] };

    //     if cgcontext_ptr.is_null() {
    //         return Err(Error::MissingFeature("NSGraphicsContext::currentContext"));
    //     }

    //     let cgcontext = unsafe { CGContextRef::from_ptr_mut(cgcontext_ptr) };

    //     let rect = CGRectMake(50.0, 50.0, 100.0, 100.0);
    //     let color = CGColor::rgb(1.0, 0.0, 0.0); // Red color
    //     CGContextSetFillColorWithColor(cgcontext, color);
    //     CGContextFillRect(cgcontext, rect);

    //     Ok(CoreGraphicsContext::new_y_down(cgcontext, None))
    // }

    /// Create a new bitmap target.
    pub fn bitmap_target(&mut self, width: usize, height: usize, pix_scale: f64) -> Result<BitmapTarget, piet::Error> {
        let ctx = CGContext::create_bitmap_context(
            None,
            width,
            height,
            8,
            0,
            &CGColorSpace::create_device_rgb(),
            core_graphics::base::kCGImageAlphaPremultipliedLast,
        );
        ctx.scale(pix_scale, pix_scale);
        let height = height as f64 * pix_scale.recip();
        Ok(BitmapTarget {
            ctx,
            height,
            phantom: PhantomData,
        })
    }
}

impl WindowTarget {
    pub fn resize(&mut self, width: u32, height: u32) {
        let _ = self.sb.resize(width, height);
    }

    pub fn begin_draw(&mut self) -> Result<CoreGraphicsContext, piet::Error> {
        if !self.ctx.is_none() {
            return Err(Error::InvalidInput);
        }

        self.ctx = Some(
            self.sb
                .create_context()
                .map_err(|e| Error::BackendError(Box::new(e)))?,
        );

        let ctx = self.ctx.as_mut().unwrap();
        Ok(CoreGraphicsContext::new_y_down(ctx, None))
    }

    pub fn end_draw(&mut self) {
        if self.ctx.is_none() {
            return;
        }

        let ctx = self.ctx.take().unwrap();
        self.sb.present_context(ctx).unwrap();
    }
}

impl<'a> BitmapTarget<'a> {
    /// Get a piet `RenderContext` for the bitmap.
    ///
    /// Note: caller is responsible for calling `finish` on the render
    /// context at the end of rendering.
    pub fn render_context(&mut self) -> CoreGraphicsContext {
        CoreGraphicsContext::new_y_up(&mut self.ctx, self.height, None)
    }

    /// Get an in-memory pixel buffer from the bitmap.
    ///
    /// Note: caller is responsible for making sure the requested `ImageFormat` is supported.
    // Clippy complains about a to_xxx method taking &mut self. Semantically speaking, this is not
    // really a mutation, so we'll keep the name. Consider using interior mutability in the future.
    #[allow(clippy::wrong_self_convention)]
    pub fn to_image_buf(&mut self, fmt: ImageFormat) -> Result<ImageBuf, piet::Error> {
        let width = self.ctx.width();
        let height = self.ctx.height();
        let mut buf = vec![0; width * height * 4];
        self.copy_raw_pixels(fmt, &mut buf)?;
        Ok(ImageBuf::from_raw(buf, fmt, width, height))
    }

    /// Get raw RGBA pixels from the bitmap by copying them into `buf`. If all the pixels were
    /// copied, returns the number of bytes written. If `buf` wasn't big enough, returns an error
    /// and doesn't write anything.
    ///
    /// Note: caller is responsible for making sure the requested `ImageFormat` is supported.
    pub fn copy_raw_pixels(&mut self, fmt: ImageFormat, buf: &mut [u8]) -> Result<usize, piet::Error> {
        // TODO: convert other formats.
        if fmt != ImageFormat::RgbaPremul {
            return Err(Error::NotSupported);
        }

        let width = self.ctx.width();
        let height = self.ctx.height();
        let stride = self.ctx.bytes_per_row();
        let data = self.ctx.data();
        let size = width * height * 4;
        if buf.len() < size {
            return Err(piet::Error::InvalidInput);
        }
        let used_stride = width * 4;
        if stride != used_stride {
            for y in 0..height {
                let src_start = y * stride;
                let src_end = src_start + used_stride;
                let dst_start = y * used_stride;
                let dst_end = dst_start + used_stride;
                buf[dst_start..dst_end].copy_from_slice(&data[src_start..src_end])
            }
        } else {
            buf.copy_from_slice(data);
        }
        Ok(size)
    }

    /// Save bitmap to RGBA PNG file
    #[cfg(feature = "png")]
    pub fn save_to_file<P: AsRef<Path>>(mut self, path: P) -> Result<(), piet::Error> {
        let width = self.ctx.width();
        let height = self.ctx.height();
        let mut data = vec![0; width * height * 4];
        self.copy_raw_pixels(ImageFormat::RgbaPremul, &mut data)?;
        util::unpremultiply_rgba(&mut data);
        let file = BufWriter::new(File::create(path).map_err(Into::<Box<_>>::into)?);
        let mut encoder = Encoder::new(file, width as u32, height as u32);
        encoder.set_color(ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        encoder
            .write_header()
            .map_err(Into::<Box<_>>::into)?
            .write_image_data(&data)
            .map_err(Into::<Box<_>>::into)?;
        Ok(())
    }

    /// Stub for feature is missing
    #[cfg(not(feature = "png"))]
    pub fn save_to_file<P: AsRef<Path>>(self, _path: P) -> Result<(), piet::Error> {
        Err(Error::MissingFeature("png"))
    }
}
