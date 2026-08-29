//! Render pipeline construction from [`PipelineDesc`] + the
//! `shader_bindings.rs` slot tables, for the D3D11 backend.
//!
//! A D3D11 "pipeline" is the bundle of objects `Frame::set_pipeline`
//! binds in one go: the two shader objects, the (optional) input layout,
//! and the blend state. The rasterizer state and topology are identical
//! across every pipeline in the crate, so they are set once per frame by
//! `Surface::acquire` instead.

use anyhow::Result;
use windows::core::s;
use windows::Win32::Graphics::Direct3D11::{
    ID3D11BlendState, ID3D11Device, ID3D11InputLayout, ID3D11PixelShader, ID3D11RasterizerState, ID3D11VertexShader, D3D11_BLEND_DESC,
    D3D11_BLEND_INV_SRC_ALPHA, D3D11_BLEND_ONE, D3D11_BLEND_OP_ADD, D3D11_BLEND_SRC_ALPHA, D3D11_COLOR_WRITE_ENABLE_ALL, D3D11_CULL_NONE,
    D3D11_FILL_SOLID, D3D11_INPUT_ELEMENT_DESC, D3D11_INPUT_PER_INSTANCE_DATA, D3D11_RASTERIZER_DESC, D3D11_RENDER_TARGET_BLEND_DESC,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT, DXGI_FORMAT_R32G32B32A32_FLOAT, DXGI_FORMAT_R32G32_SINT, DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT_R32_UINT,
};

use crate::gxi::types::{BlendMode, PipelineDesc, VertexFormat};

use super::device::Device;
use super::shaders;

/// A compiled render pipeline (immutable; shareable by reference).
pub struct RenderPipeline {
    pub(super) vs: ID3D11VertexShader,
    pub(super) ps: ID3D11PixelShader,
    /// `None` for the fullscreen-triangle passes (desktop, peek), which
    /// bind a null input layout and are driven by `SV_VertexID` alone.
    pub(super) input_layout: Option<ID3D11InputLayout>,
    pub(super) blend: ID3D11BlendState,
    /// Per-instance stride of the pipeline's one vertex buffer (0 when
    /// `input_layout` is `None`). `Frame::set_vertex_buffer` needs it —
    /// D3D11 passes strides at bind time where wgpu bakes them into the
    /// pipeline.
    pub(super) stride: u32,
}

// SAFETY: shader objects, input layouts and blend states are immutable
// D3D11 device children — the same argument as the resource wrappers in
// `device.rs` (no thread affinity; use is via free-threaded device
// methods or the mutex-guarded context; refcounting is atomic). Pipelines
// are built on the deferred build threads and then moved to / shared with
// the render worker, which is exactly this bound.
unsafe impl Send for RenderPipeline {}
unsafe impl Sync for RenderPipeline {}

impl Device {
    /// Build one render pipeline: shaders from the precompiled SM 5.0
    /// blobs (`build.rs`), input layout from the `PipelineDesc`'s vertex
    /// layout validated against the VS blob's input signature, blend state
    /// from the shared trio.
    ///
    /// Failure PANICS in every build: unlike the wgpu backend there is no
    /// runtime-WGSL fallback (this backend ships no shader compiler), and
    /// a rejected blob can only mean a broken build or a drifted register
    /// contract — never a legitimate runtime condition, because `create`
    /// guarantees FL ≥ 11_0 and SM 5.0 is core there. Containment depends
    /// on the call site: the desktop pipeline (worker Stage A) panics with
    /// the fail guard armed and rides `ReadyGuard` → `failed_count` → show
    /// gate → the shell's error dialog; the other five pipelines (peek +
    /// the UI stack) are built on the deferred builder thread, whose panic
    /// is absorbed in `render.rs` — that monitor keeps a desktop-only
    /// overlay, no failure is counted, and the only evidence is the log.
    pub fn create_pipeline(&self, desc: &PipelineDesc) -> RenderPipeline {
        let blobs = shaders::source(desc.shader);
        let device = self.raw();

        let mut vs: Option<ID3D11VertexShader> = None;
        unsafe { device.CreateVertexShader(blobs.vs_dxbc, None, Some(&mut vs)) }
            .unwrap_or_else(|e| panic!("pipeline '{}': precompiled VS '{}' rejected: {e}", desc.label, blobs.label));
        let mut ps: Option<ID3D11PixelShader> = None;
        unsafe { device.CreatePixelShader(blobs.ps_dxbc, None, Some(&mut ps)) }
            .unwrap_or_else(|e| panic!("pipeline '{}': precompiled PS '{}' rejected: {e}", desc.label, blobs.label));

        let input_layout = desc.vertex.map(|v| {
            let elements: Vec<D3D11_INPUT_ELEMENT_DESC> = v
                .attrs
                .iter()
                .map(|a| D3D11_INPUT_ELEMENT_DESC {
                    // naga emits `LOC{n}` semantics for `@location(n)`;
                    // FXC splits that into name "LOC" + index n, which is
                    // what CreateInputLayout matches against the blob's
                    // input signature (so a drifted layout fails loudly
                    // right here, not as garbage geometry).
                    SemanticName: s!("LOC"),
                    SemanticIndex: a.location,
                    Format: vertex_format(a.format),
                    InputSlot: 0,
                    AlignedByteOffset: a.offset as u32,
                    InputSlotClass: D3D11_INPUT_PER_INSTANCE_DATA,
                    InstanceDataStepRate: 1,
                })
                .collect();
            let mut layout: Option<ID3D11InputLayout> = None;
            unsafe { device.CreateInputLayout(&elements, blobs.vs_dxbc, Some(&mut layout)) }
                .unwrap_or_else(|e| panic!("pipeline '{}': input layout rejected against VS '{}': {e}", desc.label, blobs.label));
            layout.expect("CreateInputLayout succeeded without an object")
        });

        shaders::note_pipeline_built();

        RenderPipeline {
            vs: vs.expect("CreateVertexShader succeeded without an object"),
            ps: ps.expect("CreatePixelShader succeeded without an object"),
            input_layout,
            blend: self.states().blend(desc.blend).clone(),
            stride: desc
                .vertex
                .map(|v| v.stride as u32)
                .unwrap_or(0),
        }
    }
}

// ── Shared fixed-function state ─────────────────────────────────────

/// The blend + rasterizer state objects every pipeline shares, built once
/// per device (they are tiny and immutable, and D3D11 dedupes identical
/// state objects internally anyway).
pub(super) struct SharedStates {
    pub(super) rasterizer: ID3D11RasterizerState,
    blend_replace: ID3D11BlendState,
    blend_premul: ID3D11BlendState,
    blend_straight: ID3D11BlendState,
}

// SAFETY: immutable device-child state objects; same argument as
// `RenderPipeline` above. Held inside `Device` (via `Arc`), which is
// cloned across threads.
unsafe impl Send for SharedStates {}
unsafe impl Sync for SharedStates {}

impl SharedStates {
    pub(super) fn create(device: &ID3D11Device) -> Result<Self> {
        // EXPLICITLY cull-none + counter-clockwise front: D3D11 defaults
        // to CULL_BACK / clockwise, which differs from the wgpu defaults
        // (cull none, Ccw) every pipeline in the crate relies on.
        let raster_desc = D3D11_RASTERIZER_DESC {
            FillMode: D3D11_FILL_SOLID,
            CullMode: D3D11_CULL_NONE,
            FrontCounterClockwise: true.into(),
            DepthBias: 0,
            DepthBiasClamp: 0.0,
            SlopeScaledDepthBias: 0.0,
            DepthClipEnable: true.into(),
            ScissorEnable: false.into(),
            MultisampleEnable: false.into(),
            AntialiasedLineEnable: false.into(),
        };
        let mut rasterizer: Option<ID3D11RasterizerState> = None;
        unsafe { device.CreateRasterizerState(&raster_desc, Some(&mut rasterizer)) }?;

        // Replace: blending disabled (desktop and peek own every pixel).
        let blend_replace = create_blend(device, None)?;
        // Source-over with premultiplied source, both channels.
        let blend_premul = create_blend(device, Some((D3D11_BLEND_ONE, D3D11_BLEND_ONE)))?;
        // Straight-alpha color + premultiplied alpha channel (the glyph
        // pipeline, pixel-identical to glyphon's blend state).
        let blend_straight = create_blend(device, Some((D3D11_BLEND_SRC_ALPHA, D3D11_BLEND_ONE)))?;

        Ok(Self {
            rasterizer: rasterizer.expect("CreateRasterizerState succeeded without an object"),
            blend_replace,
            blend_premul,
            blend_straight,
        })
    }

    pub(super) fn blend(&self, mode: BlendMode) -> &ID3D11BlendState {
        match mode {
            BlendMode::Replace => &self.blend_replace,
            BlendMode::PremultipliedAlpha => &self.blend_premul,
            BlendMode::StraightAlpha => &self.blend_straight,
        }
    }
}

/// One blend state: `None` disables blending; `Some((color_src,
/// alpha_src))` enables `src / INV_SRC_ALPHA` add on both channels with
/// the given source factors (destination factor is `INV_SRC_ALPHA` in
/// every mode the crate uses).
fn create_blend(
    device: &ID3D11Device,
    enabled: Option<(
        windows::Win32::Graphics::Direct3D11::D3D11_BLEND,
        windows::Win32::Graphics::Direct3D11::D3D11_BLEND,
    )>,
) -> Result<ID3D11BlendState> {
    let mut rt = D3D11_RENDER_TARGET_BLEND_DESC {
        BlendEnable: enabled.is_some().into(),
        SrcBlend: D3D11_BLEND_ONE,
        DestBlend: D3D11_BLEND_INV_SRC_ALPHA,
        BlendOp: D3D11_BLEND_OP_ADD,
        SrcBlendAlpha: D3D11_BLEND_ONE,
        DestBlendAlpha: D3D11_BLEND_INV_SRC_ALPHA,
        BlendOpAlpha: D3D11_BLEND_OP_ADD,
        RenderTargetWriteMask: D3D11_COLOR_WRITE_ENABLE_ALL.0 as u8,
    };
    if let Some((color_src, alpha_src)) = enabled {
        rt.SrcBlend = color_src;
        rt.SrcBlendAlpha = alpha_src;
    }
    let desc = D3D11_BLEND_DESC {
        AlphaToCoverageEnable: false.into(),
        IndependentBlendEnable: false.into(),
        RenderTarget: [rt; 8],
    };
    let mut state: Option<ID3D11BlendState> = None;
    unsafe { device.CreateBlendState(&desc, Some(&mut state)) }?;
    Ok(state.expect("CreateBlendState succeeded without an object"))
}

fn vertex_format(f: VertexFormat) -> DXGI_FORMAT {
    match f {
        VertexFormat::Float32 => DXGI_FORMAT_R32_FLOAT,
        VertexFormat::Float32x4 => DXGI_FORMAT_R32G32B32A32_FLOAT,
        VertexFormat::Sint32x2 => DXGI_FORMAT_R32G32_SINT,
        VertexFormat::Uint32 => DXGI_FORMAT_R32_UINT,
    }
}
