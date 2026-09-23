//! The wgpu backend of `gxi`: what Linux ships.
//!
//! Linux has no single native API the way Windows has D3D11 and macOS
//! has Metal: the overlay has to run on Vulkan (Mesa, NVIDIA, AMD) and,
//! on machines with no Vulkan driver at all, on GL — and wgpu is the one
//! reasonable way to cover both behind a single code path. This module
//! descends from the wgpu backend the d3d11 and metal siblings replaced
//! (deleted in 31c099a6), ported to the contract those two have since
//! settled on.
//!
//! Vulkan is the default; GL is an explicit fallback only — `Instance`
//! takes it when no Vulkan adapter exists, or when `CLOWD_GPU_BACKEND=gl`
//! forces it (see `device::create_instance`). The shaders are the WGSL
//! sources themselves, embedded verbatim and translated at runtime by the
//! naga front end inside wgpu (`shaders.rs`); there is no precompile step
//! for this backend, so `build.rs` only validates the WGSL against the
//! binding tables on Linux.
//!
//! Public surface is byte-for-byte the d3d11 and metal backends': the
//! same type names, signatures and documented semantics, selected by
//! `gxi/mod.rs` at compile time.
//!
//! Everything wgpu-typed in the crate lives behind this module — nothing
//! outside `src/gxi/` names a `wgpu::` type.

mod device;
mod frame;
mod pipeline;
mod shaders;
mod surface;
mod timing;

pub use device::{set_display_handle, BindGroup, Buffer, Device, Instance, Queue, Sampler, Texture};
pub use frame::Frame;
pub use pipeline::RenderPipeline;
pub use surface::{BackdropImage, Surface, SurfaceViews};
pub use timing::GpuTimings;

/// Non-sRGB format used by every pipeline and surface — the wgpu spelling
/// of the shared policy const in `gxi/types.rs` (`Bgra8Unorm`), derived
/// through this backend's own translator so it cannot diverge from the
/// other backends'. Every Vulkan WSI driver on X11 offers it; GL's EGL
/// surface reports it too. `Surface::configure` checks rather than
/// assumes.
///
/// Private for the same reason as the other backends' `SURFACE_FORMAT`:
/// the type is backend-specific, and code outside `src/gxi/` must never
/// bind to it.
const SURFACE_FORMAT: wgpu::TextureFormat = device::texture_format(crate::gxi::types::SURFACE_FORMAT);
