//! The D3D11 backend of `gxi`: what Windows ships.
//!
//! Exists because wgpu's DX12 backend crashes inside old Intel iGPU
//! drivers (issue #74, `STATUS_ACCESS_VIOLATION` during device/pipeline
//! init) and D3D11 — created with a NULL feature-level array so the
//! runtime walks its own default ladder — is the compatibility target
//! those machines actually support well. It is also markedly cheaper to
//! initialize (no descriptor-heap/allocator warm-up), which shortens the
//! path to frame 0.
//!
//! Public surface is byte-for-byte the metal backend's (`gxi/metal/`,
//! what macOS ships): the same type names, signatures and documented
//! semantics, selected by `gxi/mod.rs` at compile time. Both descend
//! from the deleted wgpu backend, whose API they preserved.
//!
//! Everything windows/d3d11-typed in the crate lives behind this module —
//! nothing outside `src/gxi/` names a `windows::` graphics type.

mod device;
mod frame;
mod pipeline;
mod shaders;
mod surface;
mod timing;

pub use device::{BindGroup, Buffer, Device, Instance, Queue, Sampler, Texture};
pub use frame::Frame;
pub use pipeline::RenderPipeline;
pub use shaders::precompiled_in_use;
pub use surface::{BackdropImage, Surface, SurfaceViews};
pub use timing::GpuTimings;

/// Non-sRGB format used by every pipeline and surface — the DXGI spelling
/// of the shared policy const in `gxi/types.rs` (`Bgra8Unorm`), derived
/// through this backend's own translator so it cannot diverge from the
/// metal backend's. Universally supported as a flip-model swapchain format
/// on every D3D11 runtime.
///
/// Private for the same reason as the metal backend's `SURFACE_FORMAT`:
/// the type is backend-specific, and code outside `src/gxi/` must never
/// bind to it.
const SURFACE_FORMAT: windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT = device::texture_format(crate::gxi::types::SURFACE_FORMAT);
