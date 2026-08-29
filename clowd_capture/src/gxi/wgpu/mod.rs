//! The wgpu backend of `gxi`: macOS always (Metal); Windows for now (DX12),
//! until the d3d11 backend lands in a later phase.
//!
//! Everything wgpu-typed in the crate lives behind this module (plus the
//! `d3d11` sibling when it exists) — nothing outside `src/gxi/` names
//! `wgpu::` types.

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

/// Non-sRGB format used by every pipeline and surface. On DX12 and Metal
/// this is universally supported as a swapchain format; verified at
/// `Surface::configure` time via an assertion.
///
/// Private (not `pub(crate)`): the type is wgpu's, and `gxi/mod.rs` glob
/// re-exports carry `pub(crate)` items — a wider visibility would let
/// code outside `src/gxi/` name `gxi::SURFACE_FORMAT` and silently bind
/// to a backend-specific type (the d3d11 backend's equivalent is a
/// `DXGI_FORMAT`). Child modules see private parent items, which is all
/// the reach this needs.
const SURFACE_FORMAT: ::wgpu::TextureFormat = ::wgpu::TextureFormat::Bgra8Unorm;

/// MSAA sample count applied to every render pipeline. Always 1 (no
/// multisampling): all UI geometry is axis-aligned so MSAA adds cost
/// without visual benefit. Private for the same reason as
/// [`SURFACE_FORMAT`].
const MSAA_SAMPLES: u32 = 1;
