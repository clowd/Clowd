//! Shader registry for the D3D11 backend: the precompiled SM 5.0 DXBC
//! blobs `build.rs` emits (WGSL → naga → HLSL patched to classic flat
//! registers → FXC), one `{name}_d11_vs.dxbc` / `{name}_d11_ps.dxbc` pair
//! per [`ShaderId`].
//!
//! There is no runtime fallback here (the deleted wgpu backend kept a
//! runtime-WGSL one): this backend ships no shader compiler, and a
//! rejected blob on an FL ≥ 11_0 device is a build bug, not a runtime
//! condition — pipeline creation panics instead (see
//! `Device::create_pipeline`).

use crate::gxi::types::ShaderId;

/// Precompiled SM 5.0 DXBC pair for one shader program.
#[derive(Clone, Copy)]
pub(crate) struct ShaderBlobs {
    pub label: &'static str,
    pub vs_dxbc: &'static [u8],
    pub ps_dxbc: &'static [u8],
}

/// The registry: the blobs for `id` (labelled by [`ShaderId::name`],
/// which is also the blobs' file-name stem).
pub(crate) fn source(id: ShaderId) -> ShaderBlobs {
    macro_rules! s {
        ($vs:literal, $ps:literal) => {
            ShaderBlobs {
                label: id.name(),
                vs_dxbc: include_bytes!(concat!(env!("OUT_DIR"), $vs)),
                ps_dxbc: include_bytes!(concat!(env!("OUT_DIR"), $ps)),
            }
        };
    }
    match id {
        ShaderId::Desktop => s!("/desktop_d11_vs.dxbc", "/desktop_d11_ps.dxbc"),
        ShaderId::Peek => s!("/peek_d11_vs.dxbc", "/peek_d11_ps.dxbc"),
        ShaderId::UiRect => s!("/ui_rect_d11_vs.dxbc", "/ui_rect_d11_ps.dxbc"),
        ShaderId::UiIcon => s!("/ui_icon_d11_vs.dxbc", "/ui_icon_d11_ps.dxbc"),
        ShaderId::UiLift => s!("/ui_lift_d11_vs.dxbc", "/ui_lift_d11_ps.dxbc"),
        ShaderId::UiText => s!("/ui_text_d11_vs.dxbc", "/ui_text_d11_ps.dxbc"),
    }
}
