// Shared binding definitions for each shader — the single source of truth
// for the binding/register contract:
//   * include!()'d by build.rs (Windows and macOS) for naga register/slot
//     assignment, so the precompiled DXBC and MSL land on the same slots
//     the runtime backends recompute. This context is why the file must
//     stay self-contained (no `use` of crate items).
//   * consumed at runtime as a crate module by `gxi` (via
//     `gxi::ShaderId::bindings`), which derives each backend's bind
//     layouts from these same tables.
// Update here when shader bindings change.
//
// D3D11 register contract: the precompiled `{name}_d11_vs.dxbc` /
// `{name}_d11_ps.dxbc` blobs (SM 5.0, built by build.rs) assign
// registers by walking each shader's table IN ORDER with three
// independent counters, all space0:
//   UniformBuffer → b0, b1, ..   Texture2D → t0, t1, ..   Sampler → s0, s1, ..
// The d3d11 backend must recompute slots with this exact walk at runtime
// (no extra metadata is emitted) so its Set*ShaderResources /
// Set*ConstantBuffers / Set*Samplers calls land where the blobs expect.
//
// MSL slot contract (macOS): the precompiled `{name}.metal` sources
// (built by build.rs via `build_msl_options`) assign `[[buffer/texture/
// sampler(n)]]` slots with the SAME in-order three-counter walk:
//   UniformBuffer → buffer(0..)   Texture2D → texture(0..)   Sampler → sampler(0..)
// The metal backend recomputes it at runtime in `create_bind_group`,
// exactly like d3d11 (the per-instance vertex buffer is out of range at
// the pinned index 30, see `gxi/metal/mod.rs`).

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResourceKind {
    UniformBuffer,
    Texture2D,
    Sampler,
}

#[derive(Clone, Copy, Debug)]
pub struct BindingEntry {
    pub binding: u32,
    pub kind: ResourceKind,
    pub vertex: bool,
    pub fragment: bool,
}

pub struct ShaderDef {
    pub name: &'static str,
    pub wgsl_path: &'static str,
    pub bindings: &'static [BindingEntry],
}

pub const DESKTOP_BINDINGS: &[BindingEntry] = &[
    BindingEntry {
        binding: 0,
        kind: ResourceKind::UniformBuffer,
        vertex: true,
        fragment: true,
    },
    BindingEntry {
        binding: 1,
        kind: ResourceKind::Texture2D,
        vertex: false,
        fragment: true,
    },
    BindingEntry {
        binding: 2,
        kind: ResourceKind::Sampler,
        vertex: false,
        fragment: true,
    },
    BindingEntry {
        binding: 3,
        kind: ResourceKind::Texture2D,
        vertex: false,
        fragment: true,
    },
    BindingEntry {
        binding: 4,
        kind: ResourceKind::Texture2D,
        vertex: false,
        fragment: true,
    },
];

pub const PEEK_BINDINGS: &[BindingEntry] = &[
    BindingEntry {
        binding: 0,
        kind: ResourceKind::UniformBuffer,
        vertex: true,
        fragment: true,
    },
    BindingEntry {
        binding: 1,
        kind: ResourceKind::Texture2D,
        vertex: false,
        fragment: true,
    },
    BindingEntry {
        binding: 2,
        kind: ResourceKind::Texture2D,
        vertex: false,
        fragment: true,
    },
    BindingEntry {
        binding: 3,
        kind: ResourceKind::Sampler,
        vertex: false,
        fragment: true,
    },
];

pub const RECT_BINDINGS: &[BindingEntry] = &[BindingEntry {
    binding: 0,
    kind: ResourceKind::UniformBuffer,
    vertex: true,
    fragment: true,
}];

pub const ICON_BINDINGS: &[BindingEntry] = &[
    BindingEntry {
        binding: 0,
        kind: ResourceKind::UniformBuffer,
        vertex: true,
        fragment: false,
    },
    BindingEntry {
        binding: 1,
        kind: ResourceKind::Texture2D,
        vertex: false,
        fragment: true,
    },
    BindingEntry {
        binding: 2,
        kind: ResourceKind::Sampler,
        vertex: false,
        fragment: true,
    },
];

// ui_lift.wgsl: one uniform buffer, bound in the VERTEX-only BGL
// (`LiftPipeline::new` in ui/gpu/lift.rs).
pub const LIFT_BINDINGS: &[BindingEntry] = &[BindingEntry {
    binding: 0,
    kind: ResourceKind::UniformBuffer,
    vertex: true,
    fragment: false,
}];

// ui_text.wgsl: Params uniform (vertex), color + mask glyph atlases (the
// VS calls textureDimensions on them, the FS samples them), nearest
// sampler. See `GlyphAtlas` in ui/gpu/glyph.rs.
pub const TEXT_BINDINGS: &[BindingEntry] = &[
    BindingEntry {
        binding: 0,
        kind: ResourceKind::UniformBuffer,
        vertex: true,
        fragment: false,
    },
    BindingEntry {
        binding: 1,
        kind: ResourceKind::Texture2D,
        vertex: true,
        fragment: true,
    },
    BindingEntry {
        binding: 2,
        kind: ResourceKind::Texture2D,
        vertex: true,
        fragment: true,
    },
    BindingEntry {
        binding: 3,
        kind: ResourceKind::Sampler,
        vertex: false,
        fragment: true,
    },
];

pub const ALL_SHADERS: &[ShaderDef] = &[
    ShaderDef {
        name: "desktop",
        wgsl_path: "shaders/desktop.wgsl",
        bindings: DESKTOP_BINDINGS,
    },
    ShaderDef {
        name: "peek",
        wgsl_path: "shaders/peek.wgsl",
        bindings: PEEK_BINDINGS,
    },
    ShaderDef {
        name: "ui_rect",
        wgsl_path: "shaders/ui_rect.wgsl",
        bindings: RECT_BINDINGS,
    },
    ShaderDef {
        name: "ui_icon",
        wgsl_path: "shaders/ui_icon.wgsl",
        bindings: ICON_BINDINGS,
    },
    ShaderDef {
        name: "ui_lift",
        wgsl_path: "shaders/ui_lift.wgsl",
        bindings: LIFT_BINDINGS,
    },
    ShaderDef {
        name: "ui_text",
        wgsl_path: "shaders/ui_text.wgsl",
        bindings: TEXT_BINDINGS,
    },
];
