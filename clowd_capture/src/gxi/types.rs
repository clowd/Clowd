//! Backend-agnostic plain-data types for the `gxi` GPU abstraction.
//!
//! Everything here is inert description — no GPU handles, no backend
//! imports (the two exceptions, [`BindingRes`] and [`AcquireResult`],
//! reference the cfg-selected backend's resource types through `super`,
//! which every backend re-exports under the same names).
//!
//! The per-pipeline binding tables live in `src/shader_bindings.rs` (the
//! single source of truth shared with `build.rs`); [`ShaderId::bindings`]
//! is the runtime entry point into them.

use crate::shader_bindings::{self, BindingEntry};

use super::{Buffer, Frame, Sampler, Texture};

/// Identifies one of the crate's shader programs. Doubles as the bind
/// layout id: every shader has exactly one bind group layout, described by
/// [`ShaderId::bindings`], so a separate `BindLayoutId` would be a 1:1
/// rename of this enum.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ShaderId {
    Desktop,
    Peek,
    UiRect,
    UiIcon,
    UiLift,
    UiText,
}

impl ShaderId {
    pub const fn name(self) -> &'static str {
        match self {
            ShaderId::Desktop => "desktop",
            ShaderId::Peek => "peek",
            ShaderId::UiRect => "ui_rect",
            ShaderId::UiIcon => "ui_icon",
            ShaderId::UiLift => "ui_lift",
            ShaderId::UiText => "ui_text",
        }
    }

    /// The shader's binding table — binding index, resource kind and stage
    /// visibility per slot. Backends derive their bind layouts from this
    /// (d3d11: b#/t#/s# register slots; metal: buffer/texture/sampler
    /// slot lists), and `build.rs` derives the DXBC register and MSL slot
    /// assignment from the same consts, so the contract cannot drift.
    pub const fn bindings(self) -> &'static [BindingEntry] {
        match self {
            ShaderId::Desktop => shader_bindings::DESKTOP_BINDINGS,
            ShaderId::Peek => shader_bindings::PEEK_BINDINGS,
            ShaderId::UiRect => shader_bindings::RECT_BINDINGS,
            ShaderId::UiIcon => shader_bindings::ICON_BINDINGS,
            ShaderId::UiLift => shader_bindings::LIFT_BINDINGS,
            ShaderId::UiText => shader_bindings::TEXT_BINDINGS,
        }
    }
}

/// Fixed-function blend state, one of the three combinations the overlay
/// actually uses. (Backends translate; d3d11 maps these to the three
/// `ID3D11BlendState` objects.)
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BlendMode {
    /// No blending (desktop and peek passes — they own every pixel they
    /// touch).
    Replace,
    /// Source-over with premultiplied source (`ONE / ONE_MINUS_SRC_ALPHA`
    /// for both color and alpha) — rect, icon and lift pipelines.
    PremultipliedAlpha,
    /// Straight-alpha color (`SRC_ALPHA / ONE_MINUS_SRC_ALPHA`) with
    /// premultiplied alpha channel (`ONE / ONE_MINUS_SRC_ALPHA`) — the
    /// glyph pipeline, kept pixel-identical to glyphon's blend state.
    StraightAlpha,
}

/// Texture formats in use. All non-sRGB except the glyph color atlas
/// (which glyphon also kept sRGB — pixel identity, see ui/gpu/glyph.rs).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TexFormat {
    Bgra8Unorm,
    Rgba8Unorm,
    Rgba8UnormSrgb,
    R8Unorm,
}

/// Shared surface policy: surfaces and every pipeline's color target are
/// BGRA8 non-sRGB. Each backend derives its private, native-typed
/// `SURFACE_FORMAT` from this through its own `TexFormat` translator
/// (the d3d11 and metal `texture_format()` functions), so the two
/// spellings cannot silently diverge.
pub const SURFACE_FORMAT: TexFormat = TexFormat::Bgra8Unorm;

impl TexFormat {
    pub const fn bytes_per_pixel(self) -> u32 {
        match self {
            TexFormat::Bgra8Unorm | TexFormat::Rgba8Unorm | TexFormat::Rgba8UnormSrgb => 4,
            TexFormat::R8Unorm => 1,
        }
    }
}

/// A create-then-write 2D texture (mip 1, sample count 1, bindable +
/// CPU-uploadable — the only kind of texture the overlay uses).
#[derive(Clone, Copy, Debug)]
pub struct TextureDesc<'a> {
    pub label: &'a str,
    pub width: u32,
    pub height: u32,
    pub format: TexFormat,
}

/// Sampler filtering. Address mode is always clamp-to-edge and mip
/// filtering always nearest — no current sampler differs. Every sampler
/// in the crate is currently nearest-filtered; a `Linear` variant joins
/// this enum the day a pipeline wants one.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FilterMode {
    Nearest,
}

/// Vertex attribute formats in use by the instance layouts.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum VertexFormat {
    Float32,
    Float32x4,
    Sint32x2,
    Uint32,
}

#[derive(Clone, Copy, Debug)]
pub struct VertexAttr {
    pub format: VertexFormat,
    pub offset: u64,
    /// `@location(n)` in the WGSL (naga emits matching `LOC{n}` semantics
    /// in the HLSL, which is what the d3d11 input layouts will key on).
    pub location: u32,
}

/// One per-instance vertex buffer layout (every vertex buffer in the crate
/// is per-instance step mode; pipelines without one are fullscreen-triangle
/// passes driven by `@builtin(vertex_index)`).
#[derive(Clone, Copy, Debug)]
pub struct VertexLayout {
    pub stride: u64,
    pub attrs: &'static [VertexAttr],
}

/// Everything needed to build one render pipeline. Topology is always a
/// triangle list, MSAA is always 1, there is no depth/stencil, and the
/// color target is always the surface format — so none of those are
/// parameters.
#[derive(Clone, Copy, Debug)]
pub struct PipelineDesc {
    pub label: &'static str,
    pub shader: ShaderId,
    /// `None` for the fullscreen-triangle passes (desktop, peek).
    pub vertex: Option<VertexLayout>,
    pub blend: BlendMode,
}

/// Swapchain parameters. Format (BGRA8, non-sRGB), present mode (fifo),
/// alpha (opaque) and frame latency (1) are backend policy, not knobs.
#[derive(Clone, Copy, Debug)]
pub struct SurfaceConfig {
    pub width: u32,
    pub height: u32,
    /// Clear color for the render pass `Surface::acquire` opens.
    pub clear_color: [f64; 4],
}

/// One resource to bind, in binding-table order. Kinds are checked against
/// the shader's [`ShaderId::bindings`] table at bind-group creation.
pub enum BindingRes<'a> {
    Uniform(&'a Buffer),
    Texture(&'a Texture),
    Sampler(&'a Sampler),
}

/// What `Surface::acquire` produced.
pub enum AcquireResult {
    /// A frame is open (render pass begun, cleared); draw into it and call
    /// `Frame::present`. Boxed since the wgpu era, when `Frame` was
    /// ~1.2 KB; today's backends are far smaller, but one heap allocation
    /// per presented frame is still noise next to encoder creation, so
    /// the shape stays put.
    Frame(Box<Frame>),
    /// Nothing to draw this iteration (timeout / validation hiccup). Skip
    /// the frame and carry on.
    Skip,
    /// The surface is occluded. Same handling as [`AcquireResult::Skip`]
    /// in the steady-state loop; frame 0 on macOS retries this for a
    /// bounded window (see `render::present_first_frame`) because the
    /// early order-front races the metal backend's occlusion guard.
    #[cfg_attr(windows, allow(dead_code))] // constructed only by the metal backend
    Occluded,
    /// The swapchain was outdated/lost; the backend has already
    /// reconfigured it. Skip this frame — the next acquire should
    /// succeed. Currently never constructed: d3d11's flip-model swapchain
    /// and Metal's CAMetalLayer have no outdated state to report. Kept
    /// because it documents the acquire contract the render loop already
    /// handles, and a future backend condition may need it.
    #[allow(dead_code)]
    Reconfigured,
    /// The device itself is gone. Produced only by the d3d11 backend,
    /// which maps `DXGI_ERROR_DEVICE_REMOVED/RESET` here (in
    /// `Surface::acquire`) so the worker can exit via its fail path;
    /// Metal has no equivalent runtime device-loss signal.
    #[cfg_attr(target_os = "macos", allow(dead_code))] // constructed only by the d3d11 backend
    DeviceLost,
}

/// Progress callbacks out of `Device::create`, so the caller can stamp its
/// startup-telemetry marks without the backend depending on the telemetry
/// types.
///
/// The marks keep their ORDER across backends but not their cost split,
/// so read per-stage deltas in the startup report backend-aware: on
/// d3d11, adapter selection is pure DXGI enumeration (microseconds) and
/// ALL driver work lands in the `AdapterSelected` → `DeviceReady` delta
/// (`prep_device`); on metal, `MTLCreateSystemDefaultDevice` does the
/// driver work *before* the `AdapterSelected` mark, so that same delta
/// is ~0 instead. Likewise `instance_created` is always ~0 (both
/// backends' `Instance` is an empty token; d3d11 creates its DXGI
/// factory inside `Device::create` on the worker thread). Compare totals
/// (`prep_start` → `prep_pipelines`) when A/B-ing across backends, not
/// columns.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CreateMark {
    /// The adapter has been selected (`prep_adapter`).
    AdapterSelected,
    /// The device + queue exist and the error handler is installed
    /// (`prep_device`).
    DeviceReady,
}
