//! The Metal backend of `gxi`: what macOS ships.
//!
//! Replaces the wgpu backend on macOS for the same reasons the d3d11
//! sibling replaced it on Windows: the overlay's GPU needs are six simple
//! render pipelines, and going native drops the whole wgpu/naga runtime
//! stack from the dependency tree while shortening the path to frame 0
//! (no instance/adapter enumeration, no runtime WGSL translation - the
//! shaders arrive as precompiled MSL source, see `build.rs`).
//!
//! Public surface is byte-for-byte the d3d11 backend's (`gxi/d3d11/`):
//! the same type names, signatures and documented semantics, selected by
//! `gxi/mod.rs` at compile time.
//!
//! Everything Metal-typed in the crate lives behind this module - nothing
//! outside `src/gxi/` names an `objc2_metal::` type.

mod device;
mod frame;
mod pipeline;
mod shaders;
mod surface;
mod timing;

pub use device::{BindGroup, Buffer, Device, Instance, Queue, Sampler, Texture};
pub use frame::Frame;
pub use pipeline::RenderPipeline;
// `BackdropImage` is part of the shared backend contract but only the
// non-macOS arm of `render/window.rs` names it (the macOS arm produces
// the concrete `CGImage` directly), so the re-export is unused here.
#[allow(unused_imports)]
pub use surface::BackdropImage;
pub use surface::{Surface, SurfaceViews};
pub use timing::GpuTimings;

/// Non-sRGB format used by every pipeline and surface - the Metal spelling
/// of the shared policy const in `gxi/types.rs` (`Bgra8Unorm`), derived
/// through this backend's own translator so it cannot diverge from the
/// d3d11 backend's. BGRA8Unorm is one of the three formats CAMetalLayer
/// accepts on every macOS version.
///
/// Private for the same reason as the d3d11 backend's `SURFACE_FORMAT`:
/// the type is backend-specific, and code outside `src/gxi/` must never
/// bind to it.
const SURFACE_FORMAT: objc2_metal::MTLPixelFormat = device::texture_format(crate::gxi::types::SURFACE_FORMAT);

/// Buffer bind slot for the one per-instance vertex buffer. Pinned at the
/// top of Metal's buffer index range (31 slots) so it can never collide
/// with the uniform buffers naga assigns from `buffer(0)` upward (see
/// `build_msl_options` in build.rs): vertex data arrives via
/// `[[stage_in]]` + the runtime `MTLVertexDescriptor`, whose layout is
/// registered at this index, and `Frame::set_vertex_buffer` binds the
/// buffer to the same index.
const VERTEX_BUFFER_INDEX: usize = 30;
