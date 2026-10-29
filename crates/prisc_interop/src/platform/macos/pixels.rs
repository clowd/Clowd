//! macOS: nothing here. The screen grab and the pixel read are `prisc_rust_core::desktop::grab`
//! (CGWindowListCreateImage), the grab the capture overlay and the scrolling driver use too, and
//! `ffi/pixels.rs` calls it directly.
