//! Button metadata — the static lists of commands the panel exposes.
//!
//! Mirrors `captureButtonDetails` at
//! `clowd_capture_dx/DxScreenCapture.cpp:52-60`. Order matters: the same
//! order is used for layout, rendering, and hit-testing. In the capture
//! set index 0 is UPLOAD and the last index is EXIT; the area indicator
//! is *not* part of these arrays — it lives at
//! `buttonPositions[NUM_SVG_BUTTONS]` in the C++ and as a separate field
//! on `PanelLayout` here.
//!
//! The C++ reference implementation was deleted in 3a5939ac, so the
//! `clowd_capture_dx` paths quoted throughout this module are history,
//! not a live contract: SCROLL and OCR (both Windows only) have no C++
//! counterpart and the button count deliberately no longer matches it.
//!
//! The panel now carries *two* sets — see [`PanelButtonSet`]. They have
//! different lengths, which is why every consumer takes the set as a
//! parameter instead of reaching for one global table.
//!
//! On top of the set, the shell can switch individual buttons off — see
//! [`PanelFeatures`]. The tables below stay the full static truth; the
//! *visible* strip is `set.visible_defs(features)`, and every consumer
//! (layout, hit-testing, rendering, accelerators) works from that filtered
//! view so a switched-off button is unreachable by mouse AND by key.

use crate::ui::command::Command;

/// The deduped union of every icon any button set can show, in the order
/// `PanelRenderer` parses and rasterizes them into the icon atlas.
/// `ButtonDef::icon_id` indexes *this* table, not the button's position
/// in its set, because the two sets share icons (UPLOAD/COPY/EXIT appear
/// in both) and the atlas is built once for all of them.
///
/// The order is load-bearing: an index is baked into every `ButtonDef`
/// at compile time, so inserting an entry anywhere but the end
/// renumbers every icon after it.
pub const PANEL_ICONS: &[&[u8]] = &[
    super::assets::SVG_UPLOAD,
    super::assets::SVG_EDIT,
    super::assets::SVG_VIDEO,
    super::assets::SVG_COPY,
    super::assets::SVG_SAVE,
    super::assets::SVG_RESET,
    super::assets::SVG_EXIT,
    super::assets::SVG_SEARCH,
    super::assets::SVG_BACK,
    super::assets::SVG_OCR,
    super::assets::SVG_SCROLL,
    super::assets::SVG_SHARE,
];

// Named indices into `PANEL_ICONS`. Hand-writing the numbers at each
// `ButtonDef` would make the table order load-bearing in nine places;
// `icon_ids_point_at_their_own_bytes` in the tests below proves these
// still line up with the bytes each def carries.
pub const ICON_UPLOAD: usize = 0;
pub const ICON_EDIT: usize = 1;
pub const ICON_VIDEO: usize = 2;
pub const ICON_COPY: usize = 3;
pub const ICON_SAVE: usize = 4;
pub const ICON_RESET: usize = 5;
pub const ICON_EXIT: usize = 6;
pub const ICON_SEARCH: usize = 7;
pub const ICON_BACK: usize = 8;
pub const ICON_OCR: usize = 9;
pub const ICON_SCROLL: usize = 10;
pub const ICON_SHARE: usize = 11;

/// Which of the optional panel buttons the shell has left switched on.
///
/// The capture strip grew past what fits comfortably under a small
/// selection, so UPLOAD, SHARE, SCROLL and OCR became opt-out
/// (SettingsCapture's "Optional features" section, carried in over
/// `--no-upload` / `--no-share` / `--no-scroll-capture` / `--no-ocr` and
/// the matching `show` fields).
/// EDIT / VIDEO / COPY / SAVE / RESET / EXIT are deliberately NOT
/// configurable — they are the capturer's reason to exist, and a strip
/// that can be emptied is a strip that can strand a captured selection.
///
/// Every field defaults to `true`, so a bare capturer (standalone runs,
/// `{"type":"show"}`) shows the full strip.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PanelFeatures {
    /// UPLOAD in the capture strip, and UPLOAD in the OCR strip: one
    /// switch, because both are "hand this to the upload provider" and a
    /// user who turned uploading off did not mean "except for text".
    pub upload: bool,
    /// SHARE in the capture strip. Switches off the button and its
    /// accelerator only — NOT the action: `--share` still auto-dispatches
    /// it, because that mode never shows the panel and is reached by the
    /// shell's own tray item and hotkey, which the user invoked
    /// deliberately. Same division as UPLOAD, whose switch trims the strip
    /// while the shell's "Upload File…" tray item stays.
    pub share: bool,
    /// SCROLL in the capture strip.
    pub scroll_capture: bool,
    /// OCR in the capture strip. Switching it off makes the OCR strip
    /// unreachable, since OCR mode is the only thing that raises it.
    pub ocr: bool,
}

impl Default for PanelFeatures {
    fn default() -> Self {
        Self::ALL
    }
}

impl PanelFeatures {
    /// Everything on — the default, and what standalone runs use.
    pub const ALL: Self = Self {
        upload: true,
        share: true,
        scroll_capture: true,
        ocr: true,
    };

    /// Whether a button emitting `command` may appear at all. Commands
    /// with no switch of their own are always allowed.
    pub fn allows(self, command: Command) -> bool {
        match command {
            Command::Upload | Command::OcrUpload => self.upload,
            Command::Share => self.share,
            Command::ScrollCapture => self.scroll_capture,
            Command::Ocr => self.ocr,
            _ => true,
        }
    }
}

/// Which strip of buttons the panel is showing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PanelButtonSet {
    /// The capture strip: UPLOAD / EDIT / VIDEO / SHARE / SCROLL /
    /// COPY / SAVE / OCR / RESET / EXIT (SCROLL and OCR are Windows-only).
    Normal,
    /// The strip shown while the OCR overlay owns the selection.
    Ocr,
}

impl PanelButtonSet {
    /// Every set, for tests that must hold an invariant across all of
    /// them. Only the test module iterates it today — the `allow` is
    /// scoped to non-test builds so a genuinely-dead future addition
    /// still warns.
    #[cfg_attr(not(test), allow(dead_code))]
    pub const ALL: &'static [PanelButtonSet] = &[Self::Normal, Self::Ocr];

    /// Every button this set *can* show, in left-to-right (or
    /// top-to-bottom) order — including any the user has switched off.
    /// Callers that draw or dispatch want [`Self::visible_defs`] instead.
    pub const fn defs(self) -> &'static [ButtonDef] {
        match self {
            Self::Normal => NORMAL_DEFS,
            Self::Ocr => OCR_DEFS,
        }
    }

    /// The buttons this set actually shows under `features`, in the same
    /// order — the single definition of "the strip on screen", shared by
    /// layout, hit-testing, rendering and the accelerator lookup.
    ///
    /// Its length is also what the geometry derives from: each strip is
    /// re-centered with its own width (see `layout::compute_layout`), so
    /// the panel moves under the cursor on a set swap. That is deliberate;
    /// the double-click hazard the movement creates is absorbed by
    /// `PanelSwapGuard` in app.rs.
    pub fn visible_defs(self, features: PanelFeatures) -> impl Iterator<Item = &'static ButtonDef> {
        self.defs()
            .iter()
            .filter(move |def| features.allows(def.command))
    }
}

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
    /// True for the accent-colored primary buttons (UPLOAD, EDIT,
    /// VIDEO, SHARE, SCROLL, COPY, SAVE); false for the gray secondary
    /// buttons (OCR, RESET, BACK, EXIT). See `captureButtonDetails[i].primary`
    /// at DxScreenCapture.cpp:52-60.
    pub primary: bool,
    /// Index into [`PANEL_ICONS`] of this button's icon, which is also
    /// its slot in the rasterized icon atlas. Explicit rather than
    /// derived from the button's position because the two sets share
    /// icons and have different lengths — a positional mapping would
    /// index the atlas out of bounds on the render thread.
    pub icon_id: usize,
    /// Raw SVG bytes for the icon, embedded at compile time via
    /// `include_bytes!` in `assets.rs`.
    ///
    /// Nothing *renders* from this any more — the atlas is built from
    /// `PANEL_ICONS` — but it is what makes `icon_id` checkable: it must
    /// be the same bytes `PANEL_ICONS[icon_id]` holds, and
    /// `icon_ids_point_at_their_own_bytes` below proves it. Keeping both
    /// means a mis-numbered `icon_id` fails a unit test instead of
    /// drawing the wrong glyph — or indexing the atlas out of bounds — on
    /// a render thread. The `allow` is scoped to non-test builds because
    /// the test module is its only reader.
    #[cfg_attr(not(test), allow(dead_code))]
    pub svg_bytes: &'static [u8],
}

/// The capture-mode panel buttons in C++ order.
///
/// SHARE and SCROLL sit after VIDEO because they are the other "hand off
/// to a capture driver" actions — SHARE first, since like VIDEO it hands
/// the region to a live helper rather than producing a file. OCR sits
/// last of the actions, immediately left of RESET, and is gray rather
/// than accented: it does not finish the capture the way the accented
/// buttons do, it swaps the strip for a second round of decisions.
///
/// A slice (`&[ButtonDef]`) rather than a fixed-size array so the
/// per-element `#[cfg]` doesn't have to be mirrored in a length
/// constant; `PanelButtonSet::len()` reads the real length instead.
///
/// Accelerator keys (not stored — derived from `underline_idx`):
///   0: UPLOAD — U   (0x55)
///   1: EDIT   — E
///   2: VIDEO  — V   (0x56)
///   3: SHARE  — H   (0x48), underlined on the second char because every
///      other letter of SHARE is spoken for (S=SAVE, A/R/E=EDIT, RESET).
///      'h' is also the pre-capture color-sampler key, which does not
///      collide: that branch only runs while nothing is captured, and the
///      panel — and therefore this lookup — only exists once something is.
///   4: SCROLL — L   (0x4C), underlined on the fifth char because
///      S, C and R already belong to SAVE, COPY and RESET
///   5: COPY   — C   (0x43)
///   6: SAVE   — S   (0x53)
///   7: OCR    — O   (0x4F)
///   8: RESET  — R   (0x52)
///   9: EXIT   — X   (0x58), underlined on the second char
const NORMAL_DEFS: &[ButtonDef] = &[
    ButtonDef {
        command: Command::Upload,
        label: "UPLOAD",
        underline_idx: 0,
        primary: true,
        icon_id: ICON_UPLOAD,
        svg_bytes: super::assets::SVG_UPLOAD,
    },
    ButtonDef {
        command: Command::Edit,
        label: "EDIT",
        underline_idx: 0,
        primary: true,
        icon_id: ICON_EDIT,
        svg_bytes: super::assets::SVG_EDIT,
    },
    ButtonDef {
        command: Command::Video,
        label: "VIDEO",
        underline_idx: 0,
        primary: true,
        icon_id: ICON_VIDEO,
        svg_bytes: super::assets::SVG_VIDEO,
    },
    ButtonDef {
        command: Command::Share,
        label: "SHARE",
        underline_idx: 1,
        primary: true,
        icon_id: ICON_SHARE,
        svg_bytes: super::assets::SVG_SHARE,
    },
    ButtonDef {
        command: Command::ScrollCapture,
        label: "SCROLL",
        underline_idx: 4,
        primary: true,
        icon_id: ICON_SCROLL,
        svg_bytes: super::assets::SVG_SCROLL,
    },
    ButtonDef {
        command: Command::Copy,
        label: "COPY",
        underline_idx: 0,
        primary: true,
        icon_id: ICON_COPY,
        svg_bytes: super::assets::SVG_COPY,
    },
    ButtonDef {
        command: Command::Save,
        label: "SAVE",
        underline_idx: 0,
        primary: true,
        icon_id: ICON_SAVE,
        svg_bytes: super::assets::SVG_SAVE,
    },
    ButtonDef {
        command: Command::Ocr,
        label: "OCR",
        underline_idx: 0,
        primary: false,
        icon_id: ICON_OCR,
        svg_bytes: super::assets::SVG_OCR,
    },
    ButtonDef {
        command: Command::Reset,
        label: "RESET",
        underline_idx: 0,
        primary: false,
        icon_id: ICON_RESET,
        svg_bytes: super::assets::SVG_RESET,
    },
    ButtonDef {
        command: Command::Exit,
        label: "EXIT",
        underline_idx: 1,
        primary: false,
        icon_id: ICON_EXIT,
        svg_bytes: super::assets::SVG_EXIT,
    },
];

/// The buttons shown once recognized text has been lifted off the
/// selection: what to *do* with that text, plus the two ways out.
///
/// BACK and EXIT are gray, mirroring the RESET/EXIT pairing in the
/// capture strip — the destructive/leave actions read as secondary.
/// The accelerators reuse `u`/`s`/`c`/`x` from the capture strip on
/// purpose: only one set is ever on screen, and `lookup_command_by_key`
/// is scoped to that set.
const OCR_DEFS: &[ButtonDef] = &[
    ButtonDef {
        command: Command::OcrUpload,
        label: "UPLOAD",
        underline_idx: 0,
        primary: true,
        icon_id: ICON_UPLOAD,
        svg_bytes: super::assets::SVG_UPLOAD,
    },
    ButtonDef {
        command: Command::OcrSearch,
        label: "SEARCH",
        underline_idx: 0,
        primary: true,
        icon_id: ICON_SEARCH,
        svg_bytes: super::assets::SVG_SEARCH,
    },
    ButtonDef {
        command: Command::OcrCopy,
        label: "COPY",
        underline_idx: 0,
        primary: true,
        icon_id: ICON_COPY,
        svg_bytes: super::assets::SVG_COPY,
    },
    ButtonDef {
        command: Command::OcrBack,
        label: "BACK",
        underline_idx: 0,
        primary: false,
        icon_id: ICON_BACK,
        svg_bytes: super::assets::SVG_BACK,
    },
    ButtonDef {
        command: Command::Exit,
        label: "EXIT",
        underline_idx: 1,
        primary: false,
        icon_id: ICON_EXIT,
        svg_bytes: super::assets::SVG_EXIT,
    },
];

/// Upper bound on how many buttons any one set can have — the size of
/// `PanelLayout`'s fixed rect array and of the renderer's per-button
/// hover state.
///
/// Computed from the tables rather than hand-written (it would be 10 on
/// Windows and 9 on macOS today) so adding a button to either set can
/// never overflow the array.
pub const MAX_PANEL_BUTTONS: usize = const_max(NORMAL_DEFS.len(), OCR_DEFS.len());

/// `usize::max` is not `const fn`, and `MAX_PANEL_BUTTONS` has to be a
/// constant because it sizes arrays.
const fn const_max(a: usize, b: usize) -> usize {
    if a > b {
        a
    } else {
        b
    }
}

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

/// Look up a panel button `Command` by its accelerator key
/// (case-insensitive) **within one set, under one feature switch set**.
/// Returns `None` if no visible button matches.
///
/// Scoping to a set is not an optimization: the two sets deliberately
/// reuse `u`, `s`, `c` and `x`, and a global search would let a key fire
/// a button that is not on screen. The caller must pass the set the user
/// is actually looking at. `features` closes the same hole from the other
/// direction — a switched-off button must not answer to its letter either.
pub fn lookup_command_by_key(set: PanelButtonSet, features: PanelFeatures, c: char) -> Option<Command> {
    let lower = c.to_ascii_lowercase();
    set.visible_defs(features)
        .find(|def| def.accel_key() == lower)
        .map(|def| def.command)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// All eight on/off combinations of the three switches, so the
    /// invariants below are checked against every strip the shell can ask
    /// for rather than just the extremes.
    const FEATURE_COMBINATIONS: [PanelFeatures; 16] = {
        let mut out = [PanelFeatures::ALL; 16];
        let mut i = 0;
        while i < 16 {
            out[i] = PanelFeatures {
                upload: i & 1 != 0,
                scroll_capture: i & 2 != 0,
                ocr: i & 4 != 0,
                share: i & 8 != 0,
            };
            i += 1;
        }
        out
    };

    /// Every icon must survive `usvg` parsing: `PanelRenderer::new`
    /// swallows a parse failure into an empty tree and an `error!` line,
    /// so a malformed icon ships as a blank button rather than a crash.
    ///
    /// Iterates `PANEL_ICONS` rather than the sets so an icon that is in
    /// the atlas but not (yet) on any button is still validated.
    #[test]
    fn every_panel_icon_parses() {
        let opts = usvg::Options::default();
        for (i, bytes) in PANEL_ICONS.iter().enumerate() {
            assert!(usvg::Tree::from_data(bytes, &opts).is_ok(), "PANEL_ICONS[{i}] failed to parse");
        }
    }

    /// `lookup_command_by_key` returns the *first* match, so a duplicate
    /// accelerator would silently make the later button unreachable from
    /// the keyboard while still rendering an underline that promises it
    /// works.
    ///
    /// Uniqueness is per set. Cross-set reuse (`u`, `s`, `c`, `x` appear
    /// in both) is intentional and safe because only one set is on
    /// screen at a time — `lookup_is_scoped_to_its_set` pins that.
    #[test]
    fn accelerator_keys_are_unique_within_each_set() {
        for set in PanelButtonSet::ALL {
            let mut seen: Vec<char> = Vec::new();
            for def in set.defs() {
                let key = def.accel_key();
                assert!(!seen.contains(&key), "duplicate accelerator '{key}' on {} in {set:?}", def.label);
                seen.push(key);
            }
        }
    }

    /// `App::window_event` consumes 'd' (debug overlay) and 'm' (cursor
    /// overlay) *before* it consults the panel, so a button that claimed
    /// either would never see its key.
    #[test]
    fn accelerator_keys_avoid_the_global_toggles() {
        for set in PanelButtonSet::ALL {
            for def in set.defs() {
                let key = def.accel_key();
                assert!(key != 'd' && key != 'm', "{} in {set:?} shadows a global toggle", def.label);
            }
        }
    }

    /// The highest-risk failure mode of a two-set panel: a key firing a
    /// button the user cannot see. 'e' (EDIT) and 'l' (SCROLL) belong to
    /// the capture strip, 'b' (BACK) to the OCR strip — none may leak
    /// across.
    #[test]
    fn lookup_is_scoped_to_its_set() {
        let all = PanelFeatures::ALL;
        assert_eq!(lookup_command_by_key(PanelButtonSet::Ocr, all, 'e'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Ocr, all, 'l'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, all, 'b'), None);
        // The shared letters must still resolve — to *this* set's command.
        assert_eq!(lookup_command_by_key(PanelButtonSet::Ocr, all, 'c'), Some(Command::OcrCopy));
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, all, 'c'), Some(Command::Copy));
    }

    /// The whole point of the feature switches: a button the user turned
    /// off must be unreachable by keyboard too, or the strip would be
    /// missing a button that still fires. The buttons that are NOT
    /// configurable must be untouched by any combination.
    #[test]
    fn switched_off_buttons_lose_their_accelerator() {
        let off = PanelFeatures {
            upload: false,
            share: false,
            scroll_capture: false,
            ocr: false,
        };
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, off, 'u'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, off, 'h'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, off, 'l'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, off, 'o'), None);
        // UPLOAD is one switch across both strips — text is still an upload.
        assert_eq!(lookup_command_by_key(PanelButtonSet::Ocr, off, 'u'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Ocr, off, 's'), Some(Command::OcrSearch));

        // The non-configurable core survives every combination.
        for features in FEATURE_COMBINATIONS {
            for (key, cmd) in [
                ('e', Command::Edit),
                ('v', Command::Video),
                ('c', Command::Copy),
                ('s', Command::Save),
                ('r', Command::Reset),
                ('x', Command::Exit),
            ] {
                assert_eq!(
                    lookup_command_by_key(PanelButtonSet::Normal, features, key),
                    Some(cmd),
                    "{key} under {features:?}"
                );
            }
        }
    }

    /// Switching optional buttons off may only ever *remove* buttons —
    /// never reorder the rest, and never empty a strip (a strip with no
    /// way out would strand a captured selection).
    #[test]
    fn visible_defs_is_a_subsequence_and_never_empty() {
        for set in PanelButtonSet::ALL {
            for features in FEATURE_COMBINATIONS {
                let visible: Vec<_> = set
                    .visible_defs(features)
                    .map(|d| d.command)
                    .collect();
                assert!(!visible.is_empty(), "{set:?} emptied by {features:?}");

                let mut full = set.defs().iter().map(|d| d.command);
                for cmd in &visible {
                    assert!(full.any(|c| c == *cmd), "{cmd:?} out of order in {set:?} under {features:?}");
                }
            }
        }
    }

    /// A wrong `icon_id` is invisible until the render thread draws the
    /// wrong glyph — or, past the end of the table, indexes the atlas out
    /// of bounds and panics on a machine nobody is watching.
    ///
    /// Compared by *content*, not `std::ptr::eq`: the `assets::SVG_*`
    /// entries are `const` items, so each use site gets its own inlined
    /// copy of the `include_bytes!` data and the addresses legitimately
    /// differ (pointer equality was tried and fails on the very first
    /// button). Content equality is exactly as strong here, because
    /// `panel_icons_are_deduped` below proves no two table entries share
    /// bytes.
    #[test]
    fn icon_ids_point_at_their_own_bytes() {
        for set in PanelButtonSet::ALL {
            for def in set.defs() {
                assert!(
                    def.icon_id < PANEL_ICONS.len(),
                    "{} in {set:?} has icon_id past PANEL_ICONS",
                    def.label
                );
                assert_eq!(
                    def.svg_bytes, PANEL_ICONS[def.icon_id],
                    "{} in {set:?} points at the wrong PANEL_ICONS entry",
                    def.label
                );
            }
        }
    }

    /// `PANEL_ICONS` is the *deduped* union — one atlas slot per distinct
    /// icon, shared by every set that uses it (UPLOAD, COPY and EXIT are
    /// in both strips). A duplicate entry would waste an atlas slot and,
    /// more importantly, would leave the content comparison above unable
    /// to tell two icon ids apart.
    #[test]
    fn panel_icons_are_deduped() {
        for (i, a) in PANEL_ICONS.iter().enumerate() {
            for (j, b) in PANEL_ICONS.iter().enumerate().skip(i + 1) {
                assert_ne!(a, b, "PANEL_ICONS[{i}] and PANEL_ICONS[{j}] are the same icon");
            }
        }
    }

    #[test]
    fn scroll_button_answers_to_l() {
        let all = PanelFeatures::ALL;
        assert_eq!(
            lookup_command_by_key(PanelButtonSet::Normal, all, 'l'),
            Some(Command::ScrollCapture)
        );
        assert_eq!(
            lookup_command_by_key(PanelButtonSet::Normal, all, 'L'),
            Some(Command::ScrollCapture)
        );
    }

    #[test]
    fn ocr_button_answers_to_o() {
        let all = PanelFeatures::ALL;
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, all, 'o'), Some(Command::Ocr));
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, all, 'O'), Some(Command::Ocr));
    }

    // (The old `set_swap_reclick_collisions_are_pinned` test is gone with
    // the fixed-footprint anchoring it described: the strips now re-center
    // with their own widths on every swap, so a double-click's second
    // press has no fixed index alignment to pin — it can land on ANY
    // button of the new strip, or none. `PanelSwapGuard` in app.rs blocks
    // every panel-aimed click for one OS double-click interval after any
    // swap, which covers the entire class regardless of geometry, and the
    // `ocr.active()` guard on `Command::ScrollCapture` still covers a
    // stray press outliving the guard window mid-retract.)
}
