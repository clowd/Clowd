//! Shader registry for the wgpu backend: the WGSL sources themselves,
//! embedded verbatim (`include_str!` from `shaders/`) and translated at
//! runtime by the naga front end inside wgpu — to SPIR-V on Vulkan, to
//! GLSL on the GL fallback. No precompile step exists for this backend
//! (contrast the d3d11 DXBC blobs and the metal MSL text `build.rs`
//! emits): naga is already in the binary as part of wgpu, and the
//! translation of five small shaders is negligible next to device
//! creation.
//!
//! There is no runtime fallback either: `build.rs` validates every WGSL
//! file against its binding table on Linux, so a module wgpu rejects is a
//! build bug or a driver bug, not a runtime condition — pipeline creation
//! panics instead (see `Device::create_pipeline`).

use crate::gxi::types::ShaderId;

/// WGSL source for one shader program (both entry points, `vs_main` and
/// `fs_main`, live in the one file).
#[derive(Clone, Copy)]
pub(crate) struct ShaderSource {
    pub label: &'static str,
    pub wgsl: &'static str,
}

/// The registry: the WGSL for `id` (labelled by [`ShaderId::name`], which
/// is also the source's file-name stem).
pub(crate) fn source(id: ShaderId) -> ShaderSource {
    macro_rules! s {
        ($path:literal) => {
            ShaderSource {
                label: id.name(),
                wgsl: include_str!(concat!("../../../shaders/", $path)),
            }
        };
    }
    match id {
        ShaderId::Desktop => s!("desktop.wgsl"),
        ShaderId::Peek => s!("peek.wgsl"),
        ShaderId::Selection => s!("selection.wgsl"),
        ShaderId::Crosshair => s!("crosshair.wgsl"),
        ShaderId::Egui => s!("ui_egui.wgsl"),
    }
}
