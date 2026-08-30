//! Shader registry for the Metal backend: the precompiled MSL source
//! files `build.rs` emits (WGSL → naga → MSL with explicit
//! buffer/texture/sampler slots), one `{name}.metal` per [`ShaderId`],
//! compiled at runtime with `newLibraryWithSource` (source rather than a
//! metallib because a metallib pins the Metal language version - see the
//! rationale in build.rs).
//!
//! There is no runtime fallback here (contrast the old wgpu backend's
//! shaders.rs): naga never runs on user machines, and MSL source the
//! driver's compiler rejects is a build bug, not a runtime condition -
//! pipeline creation panics instead (see `Device::create_pipeline`).

use crate::gxi::types::ShaderId;

/// Precompiled MSL source for one shader program (both entry points,
/// `vs_main` and `fs_main`, live in the one translated file).
#[derive(Clone, Copy)]
pub(crate) struct ShaderSource {
    pub label: &'static str,
    pub msl: &'static str,
}

/// The registry: the MSL source for `id` (labelled by [`ShaderId::name`],
/// which is also the source's file-name stem).
pub(crate) fn source(id: ShaderId) -> ShaderSource {
    macro_rules! s {
        ($path:literal) => {
            ShaderSource {
                label: id.name(),
                msl: include_str!(concat!(env!("OUT_DIR"), $path)),
            }
        };
    }
    match id {
        ShaderId::Desktop => s!("/desktop.metal"),
        ShaderId::Peek => s!("/peek.metal"),
        ShaderId::UiRect => s!("/ui_rect.metal"),
        ShaderId::UiIcon => s!("/ui_icon.metal"),
        ShaderId::UiLift => s!("/ui_lift.metal"),
        ShaderId::UiText => s!("/ui_text.metal"),
    }
}
