//! Render pipeline construction from [`PipelineDesc`] + the
//! `shader_bindings.rs` slot tables, for the Metal backend.
//!
//! A Metal pipeline state object bakes in the shader pair, the vertex
//! descriptor and the blend state, so unlike d3d11 there is no bundle of
//! loose objects - [`RenderPipeline`] wraps the one
//! `MTLRenderPipelineState`. Cull mode and winding are encoder state,
//! identical across every pipeline in the crate, so they are set once per
//! frame by `Surface::acquire` instead.

use objc2::rc::Retained;
use objc2::runtime::ProtocolObject;
use objc2_foundation::{ns_string, NSString};
use objc2_metal::{
    MTLBlendFactor, MTLBlendOperation, MTLDevice as _, MTLLibrary as _, MTLRenderPipelineDescriptor, MTLRenderPipelineState,
    MTLVertexDescriptor, MTLVertexFormat, MTLVertexStepFunction,
};

use crate::gxi::types::{BlendMode, PipelineDesc, VertexFormat, VertexLayout};

use super::device::Device;
use super::{shaders, SURFACE_FORMAT, VERTEX_BUFFER_INDEX};

/// A compiled render pipeline (immutable; shareable by reference).
pub struct RenderPipeline {
    pub(super) raw: Retained<ProtocolObject<dyn MTLRenderPipelineState>>,
}

// SAFETY: `MTLRenderPipelineState` is an immutable, thread-safe Metal
// object (Apple's Metal docs list pipeline states among the objects safe
// to use from multiple threads; objc2-metal just does not translate that
// into `Send`/`Sync` impls). Pipelines are built on the deferred build
// threads and then moved to / shared with the render worker, which is
// exactly this bound. Refcounting is atomic.
unsafe impl Send for RenderPipeline {}
unsafe impl Sync for RenderPipeline {}

impl Device {
    /// Build one render pipeline: MSL library from the precompiled source
    /// (`build.rs`), entry points looked up by name, vertex descriptor
    /// from the `PipelineDesc`'s vertex layout, blend baked into the
    /// pipeline state.
    ///
    /// Failure PANICS in every build: like d3d11 there is no runtime-WGSL
    /// fallback (naga never runs on user machines), and MSL source the
    /// driver rejects can only mean a broken build or a drifted slot
    /// contract - never a legitimate runtime condition. Containment
    /// depends on the call site: the desktop pipeline (worker Stage A)
    /// panics with the fail guard armed and rides `ReadyGuard` →
    /// `failed_count` → show gate → the shell's error dialog; the other
    /// five pipelines (peek + the UI stack) are built on the deferred
    /// builder thread, whose panic is absorbed in `render.rs` - that
    /// monitor keeps a desktop-only overlay, no failure is counted, and
    /// the only evidence is the log.
    pub fn create_pipeline(&self, desc: &PipelineDesc) -> RenderPipeline {
        let src = shaders::source(desc.shader);
        let device = self.raw();

        let library = device
            .newLibraryWithSource_options_error(&NSString::from_str(src.msl), None)
            .unwrap_or_else(|e| panic!("pipeline '{}': precompiled MSL '{}' rejected: {e}", desc.label, src.label));
        let vs = library
            .newFunctionWithName(ns_string!("vs_main"))
            .unwrap_or_else(|| panic!("pipeline '{}': vs_main missing from MSL library '{}'", desc.label, src.label));
        let fs = library
            .newFunctionWithName(ns_string!("fs_main"))
            .unwrap_or_else(|| panic!("pipeline '{}': fs_main missing from MSL library '{}'", desc.label, src.label));

        let rp_desc = MTLRenderPipelineDescriptor::new();
        rp_desc.setLabel(Some(&NSString::from_str(desc.label)));
        rp_desc.setVertexFunction(Some(&vs));
        rp_desc.setFragmentFunction(Some(&fs));
        // `None` for the fullscreen-triangle passes (desktop, peek),
        // which are driven by `[[vertex_id]]` alone.
        if let Some(v) = &desc.vertex {
            rp_desc.setVertexDescriptor(Some(&vertex_descriptor(v)));
        }

        // SAFETY (objectAtIndexedSubscript): index 0 always exists - the
        // attachment array is a fixed 8-slot table.
        let attachment = unsafe {
            rp_desc
                .colorAttachments()
                .objectAtIndexedSubscript(0)
        };
        attachment.setPixelFormat(SURFACE_FORMAT);
        match desc.blend {
            // No blending (desktop and peek own every pixel).
            BlendMode::Replace => attachment.setBlendingEnabled(false),
            // Source-over with premultiplied source, both channels.
            BlendMode::PremultipliedAlpha => {
                attachment.setBlendingEnabled(true);
                attachment.setSourceRGBBlendFactor(MTLBlendFactor::One);
                attachment.setDestinationRGBBlendFactor(MTLBlendFactor::OneMinusSourceAlpha);
                attachment.setRgbBlendOperation(MTLBlendOperation::Add);
                attachment.setSourceAlphaBlendFactor(MTLBlendFactor::One);
                attachment.setDestinationAlphaBlendFactor(MTLBlendFactor::OneMinusSourceAlpha);
                attachment.setAlphaBlendOperation(MTLBlendOperation::Add);
            }
            // Straight-alpha color + premultiplied alpha channel (the
            // glyph pipeline, pixel-identical to glyphon's blend state).
            // The asymmetry is deliberate - do not simplify.
            BlendMode::StraightAlpha => {
                attachment.setBlendingEnabled(true);
                attachment.setSourceRGBBlendFactor(MTLBlendFactor::SourceAlpha);
                attachment.setDestinationRGBBlendFactor(MTLBlendFactor::OneMinusSourceAlpha);
                attachment.setRgbBlendOperation(MTLBlendOperation::Add);
                attachment.setSourceAlphaBlendFactor(MTLBlendFactor::One);
                attachment.setDestinationAlphaBlendFactor(MTLBlendFactor::OneMinusSourceAlpha);
                attachment.setAlphaBlendOperation(MTLBlendOperation::Add);
            }
        }

        let raw = device
            .newRenderPipelineStateWithDescriptor_error(&rp_desc)
            .unwrap_or_else(|e| panic!("pipeline '{}': pipeline state rejected: {e}", desc.label));

        shaders::note_pipeline_built();

        RenderPipeline {
            raw,
        }
    }
}

/// Translate a [`VertexLayout`] into the `MTLVertexDescriptor` the
/// pipeline state validates the MSL `[[stage_in]]` signature against
/// (a drifted layout fails loudly at pipeline creation, not as garbage
/// geometry). naga maps `@location(n)` to attribute index `n`, so
/// locations index the attribute array directly; the buffer layout lives
/// at [`VERTEX_BUFFER_INDEX`], per-instance step, matching where
/// `Frame::set_vertex_buffer` binds the buffer.
fn vertex_descriptor(v: &VertexLayout) -> Retained<MTLVertexDescriptor> {
    let vd = MTLVertexDescriptor::vertexDescriptor();
    let attributes = vd.attributes();
    for a in v.attrs {
        // SAFETY (objectAtIndexedSubscript / setOffset / setBufferIndex):
        // attribute indices are the WGSL locations (all < 31, Metal's
        // fixed attribute table size), offsets come from the crate's own
        // layout consts, and the buffer index is the pinned slot 30.
        unsafe {
            let attr = attributes.objectAtIndexedSubscript(a.location as usize);
            attr.setFormat(vertex_format(a.format));
            attr.setOffset(a.offset as usize);
            attr.setBufferIndex(VERTEX_BUFFER_INDEX);
        }
    }
    // SAFETY: same bounds argument; layout index 30 is within Metal's
    // 31-slot layout table.
    unsafe {
        let layout = vd
            .layouts()
            .objectAtIndexedSubscript(VERTEX_BUFFER_INDEX);
        layout.setStride(v.stride as usize);
        layout.setStepFunction(MTLVertexStepFunction::PerInstance);
        layout.setStepRate(1);
    }
    vd
}

fn vertex_format(f: VertexFormat) -> MTLVertexFormat {
    match f {
        VertexFormat::Float32 => MTLVertexFormat::Float,
        VertexFormat::Float32x4 => MTLVertexFormat::Float4,
        VertexFormat::Sint32x2 => MTLVertexFormat::Int2,
        VertexFormat::Uint32 => MTLVertexFormat::UInt,
    }
}
