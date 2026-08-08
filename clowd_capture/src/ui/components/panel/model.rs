//! Button metadata — the static list of commands the panel exposes.
//!
//! Mirrors `captureButtonDetails` at
//! `clowd_capture_dx/DxScreenCapture.cpp:52-60`. Order matters: the same
//! order is used for layout, rendering, and hit-testing. Index 0 is
//! UPLOAD; the last index is EXIT; the area indicator is *not* part of
//! this array — it lives at `buttonPositions[NUM_SVG_BUTTONS]` in the
//! C++ and as a separate field on `PanelLayout` here.
//!
//! The C++ reference implementation was deleted in 3a5939ac, so the
//! `clowd_capture_dx` paths quoted throughout this module are history,
//! not a live contract: SCROLL (Windows only) has no C++ counterpart
//! and the button count deliberately no longer matches it.

use crate::ui::command::Command;

/// Number of SVG buttons in the panel — 8 on Windows, 7 elsewhere,
/// because SCROLL is Windows-only. Must equal the length of
/// `BUTTON_DEFS`; the layout code multiplies it to size the button row
/// and `PanelLayout::buttons` is an array of exactly this many rects.
#[cfg(windows)]
pub const NUM_SVG_BUTTONS: usize = 8;
#[cfg(not(windows))]
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
    /// VIDEO, SCROLL, COPY, SAVE); false for the gray secondary buttons
    /// (RESET, EXIT). See `captureButtonDetails[i].primary` at
    /// DxScreenCapture.cpp:52-60.
    pub primary: bool,
    /// Raw SVG bytes for the icon, embedded at compile time via
    /// `include_bytes!` in `assets.rs`. Passed to `usvg::Tree::from_data`
    /// once at backend construction time.
    pub svg_bytes: &'static [u8],
}

/// The panel buttons in C++ order. Consumers should use `button_defs()`
/// rather than this constant directly so the `NUM_SVG_BUTTONS`
/// invariant is enforced by the return type.
///
/// SCROLL carries `#[cfg(windows)]` on its array element rather than
/// duplicating the whole table behind two cfg'd copies — the element
/// vanishes on macOS exactly as `NUM_SVG_BUTTONS` shrinks, and every
/// other button stays defined once. It sits after VIDEO because it is
/// the other "hand off to a capture driver" action; the indices below
/// are therefore the Windows ones.
///
/// Accelerator keys (not stored — derived from `underline_idx`):
///   0: UPLOAD — U   (0x55)
///   1: EDIT   — E
///   2: VIDEO  — V   (0x56)
///   3: SCROLL — L   (0x4C), underlined on the fifth char because
///      S, C and R already belong to SAVE, COPY and RESET
///   4: COPY   — C   (0x43)
///   5: SAVE   — S   (0x53)
///   6: RESET  — R   (0x52)
///   7: EXIT   — X   (0x58), underlined on the second char
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
    #[cfg(windows)]
    ButtonDef {
        command: Command::ScrollCapture,
        label: "SCROLL",
        underline_idx: 4,
        primary: true,
        svg_bytes: super::assets::SVG_SCROLL,
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

#[cfg(test)]
mod tests {
    use super::*;

    /// Every button's SVG must survive `usvg` parsing: `PanelRenderer::new`
    /// swallows a parse failure into an empty tree and an `error!` line,
    /// so a malformed icon ships as a blank button rather than a crash.
    #[test]
    fn every_button_icon_parses() {
        let opts = usvg::Options::default();
        for def in button_defs() {
            assert!(
                usvg::Tree::from_data(def.svg_bytes, &opts).is_ok(),
                "icon for {} failed to parse",
                def.label
            );
        }
    }

    /// `lookup_command_by_key` returns the *first* match, so a duplicate
    /// accelerator would silently make the later button unreachable from
    /// the keyboard while still rendering an underline that promises it
    /// works.
    #[test]
    fn accelerator_keys_are_unique() {
        let mut seen: Vec<char> = Vec::new();
        for def in button_defs() {
            let key = def.accel_key();
            assert!(!seen.contains(&key), "duplicate panel accelerator '{key}' on {}", def.label);
            seen.push(key);
        }
    }

    /// `App::window_event` consumes 'd' (debug overlay) and 'm' (cursor
    /// overlay) *before* it consults the panel, so a button that claimed
    /// either would never see its key.
    #[test]
    fn accelerator_keys_avoid_the_global_toggles() {
        for def in button_defs() {
            let key = def.accel_key();
            assert!(key != 'd' && key != 'm', "{} shadows a global toggle", def.label);
        }
    }

    #[cfg(windows)]
    #[test]
    fn scroll_button_answers_to_l() {
        assert_eq!(lookup_command_by_key('l'), Some(Command::ScrollCapture));
        assert_eq!(lookup_command_by_key('L'), Some(Command::ScrollCapture));
    }

    /// The scrolling-capture driver is Win32-only, so macOS must not
    /// render a button that leads nowhere.
    #[cfg(not(windows))]
    #[test]
    fn scroll_button_is_absent_off_windows() {
        assert_eq!(lookup_command_by_key('l'), None);
        for def in button_defs() {
            assert_ne!(def.command, Command::ScrollCapture);
        }
    }
}
