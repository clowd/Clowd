// Shared binding definitions for each shader.
// include!()'d by build.rs for naga HLSL register assignment,
// and used by runtime code for wgpu BindGroupLayout creation.
// This is the single source of truth — update here when shader bindings change.

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
    BindingEntry { binding: 0, kind: ResourceKind::UniformBuffer, vertex: true,  fragment: true  },
    BindingEntry { binding: 1, kind: ResourceKind::Texture2D,     vertex: false, fragment: true  },
    BindingEntry { binding: 2, kind: ResourceKind::Sampler,       vertex: false, fragment: true  },
    BindingEntry { binding: 3, kind: ResourceKind::Texture2D,     vertex: false, fragment: true  },
    BindingEntry { binding: 4, kind: ResourceKind::Texture2D,     vertex: false, fragment: true  },
];

pub const PEEK_BINDINGS: &[BindingEntry] = &[
    BindingEntry { binding: 0, kind: ResourceKind::UniformBuffer, vertex: true,  fragment: true  },
    BindingEntry { binding: 1, kind: ResourceKind::Texture2D,     vertex: false, fragment: true  },
    BindingEntry { binding: 2, kind: ResourceKind::Texture2D,     vertex: false, fragment: true  },
    BindingEntry { binding: 3, kind: ResourceKind::Sampler,       vertex: false, fragment: true  },
];

pub const RECT_BINDINGS: &[BindingEntry] = &[
    BindingEntry { binding: 0, kind: ResourceKind::UniformBuffer, vertex: true,  fragment: true  },
];

pub const ICON_BINDINGS: &[BindingEntry] = &[
    BindingEntry { binding: 0, kind: ResourceKind::UniformBuffer, vertex: true,  fragment: false },
    BindingEntry { binding: 1, kind: ResourceKind::Texture2D,     vertex: false, fragment: true  },
    BindingEntry { binding: 2, kind: ResourceKind::Sampler,       vertex: false, fragment: true  },
];

pub const ALL_SHADERS: &[ShaderDef] = &[
    ShaderDef { name: "desktop", wgsl_path: "shaders/desktop.wgsl", bindings: DESKTOP_BINDINGS },
    ShaderDef { name: "peek",    wgsl_path: "shaders/peek.wgsl",    bindings: PEEK_BINDINGS },
    ShaderDef { name: "ui_rect", wgsl_path: "shaders/ui_rect.wgsl", bindings: RECT_BINDINGS },
    ShaderDef { name: "ui_icon", wgsl_path: "shaders/ui_icon.wgsl", bindings: ICON_BINDINGS },
];
