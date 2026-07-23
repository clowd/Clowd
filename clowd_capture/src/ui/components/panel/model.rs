//! Button metadata — the static list of commands the panel exposes.
//!
//! Mirrors `captureButtonDetails` at
//! `clowd_capture_dx/DxScreenCapture.cpp:52-60`. Order matters: the same
//! order is used for layout, rendering, and hit-testing. Index 0 is
//! UPLOAD; index 6 is EXIT; the area indicator is *not* part of this
//! array — it lives at `buttonPositions[NUM_SVG_BUTTONS]` in the C++
//! and as a separate field on `PanelLayout` here.

use crate::ui::command::Command;

/// Number of SVG buttons in the panel. Must match `NUM_SVG_BUTTONS` at
/// `clowd_capture_dx/DxScreenCapture.h:9` and the length of
/// `BUTTON_DEFS`. Used by the layout code to size the button row.
pub const NUM_SVG_BUTTONS: usize = 7;

/// Static metadata for one button. Fields mirror the C++
/// `captureButtonDetail` struct at `DxScreenCapture.h:24-33`, plus the
/// SVG byte slice embedded directly so backends don't have to go
/// through `assets.rs` for lookups.
#[derive(Debug, Clone, Copy)]
pub struct ButtonDef {
    /// Command this button emits on click.
    pub command: Command,
    /// Display label. The C++ uses wide strings; we store UTF-8 and
    /// rasterize ASCII-only — no glyph in the labels needs shaping.
    pub label: &'static str,
    /// Index (into `label.chars()`) of the character that should be
    /// underlined as the keyboard accelerator hint. Matches
    /// `captureButtonDetail::underlineIndex`.
    pub underline_idx: usize,
    /// True for the accent-coloured primary buttons (UPLOAD, EDIT,
    /// VIDEO, COPY, SAVE); false for the gray secondary buttons
    /// (RESET, EXIT). See `captureButtonDetails[i].primary` at
    /// DxScreenCapture.cpp:52-60.
    pub primary: bool,
    /// Raw SVG bytes for the icon, embedded at compile time via
    /// `include_bytes!` in `assets.rs`. Passed to `usvg::Tree::from_data`
    /// once at backend construction time.
    pub svg_bytes: &'static [u8],
}

/// The seven panel buttons in C++ order. Same indices as
/// `captureButtonDetails[0..7]`. Consumers should use `button_defs()`
/// rather than this constant directly so the `NUM_SVG_BUTTONS`
/// invariant is enforced by the return type.
///
/// Accelerator keys (not stored — derived from `underline_idx`):
///   0: UPLOAD — U   (0x55)
///   1: EDIT   — E
///   2: VIDEO  — V   (0x56)
///   3: COPY   — C   (0x43)
///   4: SAVE   — S   (0x53)
///   5: RESET  — R   (0x52)
///   6: EXIT   — X   (0x58), underlined on the second char
const BUTTON_DEFS: [ButtonDef; NUM_SVG_BUTTONS] = [
    ButtonDef {
        command: Command::Upload,
        label: "UPLOAD",
        underline_idx: 0,
        primary: true,
        svg_bytes: super::assets::SVG_UPLOAD,
    },
    ButtonDef {
        command: Command::Edit,
        label: "EDIT",
        underline_idx: 0,
        primary: true,
        svg_bytes: super::assets::SVG_EDIT,
    },
    ButtonDef {
        command: Command::Video,
        label: "VIDEO",
        underline_idx: 0,
        primary: true,
        svg_bytes: super::assets::SVG_VIDEO,
    },
    ButtonDef {
        command: Command::Copy,
        label: "COPY",
        underline_idx: 0,
        primary: true,
        svg_bytes: super::assets::SVG_COPY,
    },
    ButtonDef {
        command: Command::Save,
        label: "SAVE",
        underline_idx: 0,
        primary: true,
        svg_bytes: super::assets::SVG_SAVE,
    },
    ButtonDef {
        command: Command::Reset,
        label: "RESET",
        underline_idx: 0,
        primary: false,
        svg_bytes: super::assets::SVG_RESET,
    },
    ButtonDef {
        command: Command::Exit,
        label: "EXIT",
        underline_idx: 1,
        primary: false,
        svg_bytes: super::assets::SVG_EXIT,
    },
];

impl ButtonDef {
    /// The keyboard accelerator character for this button, derived from
    /// the underlined position in the label. Always lowercase.
    pub fn accel_key(&self) -> char {
        self.label
            .chars()
            .nth(self.underline_idx)
            .expect("underline_idx out of bounds")
            .to_ascii_lowercase()
    }
}

/// The static list of buttons, as a fixed-size array reference so the
/// invariant `len == NUM_SVG_BUTTONS` is guaranteed at the type level.
pub const fn button_defs() -> &'static [ButtonDef; NUM_SVG_BUTTONS] {
    &BUTTON_DEFS
}

/// Look up a panel button `Command` by its accelerator key
/// (case-insensitive). Returns `None` if no button matches.
pub fn lookup_command_by_key(c: char) -> Option<Command> {
    let lower = c.to_ascii_lowercase();
    button_defs()
        .iter()
        .find(|def| def.accel_key() == lower)
        .map(|def| def.command)
}
