//! Button metadata: the static tables of what the panel can show.
//!
//! Two sets ([`PanelButtonSet`]) of [`ButtonDef`]s in strip order, each
//! carrying its own icon bytes, the shell's opt-out switches
//! ([`PanelFeatures`]) and the accelerator lookup. The tables are the full
//! static truth; the strip on screen is `set.visible_defs(features)`, and
//! both the tray and the accelerator lookup read that one filtered view,
//! so a switched-off button is unreachable by mouse AND by key.
//!
//! The buttons sit in groups ([`ButtonGroup`]): consecutive runs of the
//! table that share one rounded fill, the primary group in the user's
//! accent and the others in the segment grey. A button has no fill of its
//! own; only the hovered one lights up, inside its group. The `below`
//! style sizes each button from its laid-out label, so the label text is
//! geometry as much as it is copy.

use crate::ui::command::Command;

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
    /// VIDEO in the capture strip. Off when the shell has recording switched
    /// off altogether (its Recording settings page), which is the only reason
    /// to hide it: there is no per-button switch for VIDEO on the Capture
    /// page. Like SHARE, this hides the button and its accelerator only —
    /// `--video` never shows the strip, so it is untouched.
    pub video: bool,
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
        video: true,
        ocr: true,
    };

    /// Whether a button emitting `command` may appear at all. Commands
    /// with no switch of their own are always allowed.
    pub fn allows(self, command: Command) -> bool {
        match command {
            Command::Upload | Command::OcrUpload => self.upload,
            Command::Share => self.share,
            Command::ScrollCapture => self.scroll_capture,
            Command::Video => self.video,
            Command::Ocr => self.ocr,
            _ => true,
        }
    }
}

/// Which strip of buttons the panel is showing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PanelButtonSet {
    /// The capture strip: [UPLOAD / EDIT / VIDEO / COPY / SAVE]
    /// [SHARE / SCROLL / OCR] [RESET / EXIT] (SCROLL and OCR are
    /// Windows-only).
    Normal,
    /// The strip shown while the OCR overlay owns the selection.
    Ocr,
    /// The strip shown while a scroll point is being picked: no readout,
    /// the aiming instruction in its place, and the two ways back out.
    ScrollPick,
}

impl PanelButtonSet {
    /// Every set, for the invariants the tests hold across all of them.
    /// The strip's own union fit walks [`Self::UNION`] instead.
    #[cfg(test)]
    pub const ALL: &'static [PanelButtonSet] = &[Self::Normal, Self::Ocr, Self::ScrollPick];

    /// The sets the union fit is taken over: every set EXCEPT the
    /// scroll-pick strip, whose instruction makes it several times wider
    /// than any other. Folding it into the union would widen every column
    /// and push every strip's row length past the fit test, so it is
    /// measured on its own instead (`show::union_fit` adds its own row
    /// footprint when it is the set on screen) and pinned to a row
    /// ([`Self::axis_lock`]).
    pub const UNION: &'static [PanelButtonSet] = &[Self::Normal, Self::Ocr];

    /// The axis this set must run along, or `None` when placement is free
    /// to choose. The scroll-pick strip is a row wherever it lands: its
    /// instruction wraps to two lines at a width no column could hold.
    pub const fn axis_lock(self) -> Option<super::place::Axis> {
        match self {
            Self::ScrollPick => Some(super::place::Axis::Row),
            _ => None,
        }
    }

    /// What sits between the emblem and the buttons.
    pub const fn body(self) -> Body {
        match self {
            Self::Normal | Self::Ocr => Body::Readout,
            Self::ScrollPick => Body::Hint(SCROLL_PICK_HINT),
        }
    }

    /// Every button this set *can* show, in left-to-right (or
    /// top-to-bottom) order — including any the user has switched off.
    /// Callers that draw or dispatch want [`Self::visible_defs`] instead.
    pub const fn defs(self) -> &'static [ButtonDef] {
        match self {
            Self::Normal => NORMAL_DEFS,
            Self::Ocr => OCR_DEFS,
            Self::ScrollPick => SCROLL_PICK_DEFS,
        }
    }

    /// How [`Self::defs`] is cut into groups, in strip order. The lengths
    /// sum to the table's length (a test pins it).
    pub const fn groups(self) -> &'static [ButtonGroup] {
        match self {
            Self::Normal => NORMAL_GROUPS,
            Self::Ocr => OCR_GROUPS,
            Self::ScrollPick => SCROLL_PICK_GROUPS,
        }
    }

    /// The groups this set actually shows under `features`, in strip
    /// order, each with its visible buttons as [`Self::visible_defs`]
    /// yields them. A group every button of which is switched off is
    /// dropped altogether: no empty fill, no gap.
    pub fn visible_groups(self, features: PanelFeatures) -> Vec<(GroupTone, Vec<(usize, &'static ButtonDef)>)> {
        let defs = self.defs();
        let mut start = 0;
        self.groups()
            .iter()
            .filter_map(|g| {
                let end = start + g.len;
                let members: Vec<_> = (start..end)
                    .map(|i| (i, &defs[i]))
                    .filter(|(_, def)| features.allows(def.command))
                    .collect();
                start = end;
                (!members.is_empty()).then_some((g.tone, members))
            })
            .collect()
    }

    /// The buttons this set actually shows under `features`, in the same
    /// order, each with its index into [`Self::defs`] — the single
    /// definition of "the strip on screen", shared by `show` and the
    /// accelerator lookup. The index is the table position, not the
    /// visible position: `show` keys widget ids on it so a switched-off
    /// button does not renumber its neighbours.
    pub fn visible_defs(self, features: PanelFeatures) -> impl Iterator<Item = (usize, &'static ButtonDef)> {
        self.defs()
            .iter()
            .enumerate()
            .filter(move |(_, def)| features.allows(def.command))
    }
}

/// What the tray's readout slot shows beside the emblem.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Readout {
    /// The capture strip: the selection's size, "W / × / H".
    Size { width: i32, height: i32 },
    /// The OCR strip: how many words were lifted, "N / words".
    Words(usize),
}

impl Readout {
    /// The word count of a lifted result: whitespace-separated runs of
    /// the newline-joined `full_text`, which is what COPY hands over.
    pub fn words_in(text: &str) -> Self {
        Self::Words(text.split_whitespace().count())
    }
}

/// Which fill a group of buttons sits on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GroupTone {
    /// The user's capture accent: the actions that finish the capture.
    Primary,
    /// The segment grey (`theme::tokens::SEG_FILL`).
    Secondary,
}

/// A run of consecutive buttons in a set's table that share one rounded
/// fill.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ButtonGroup {
    pub tone: GroupTone,
    /// How many table entries this group covers.
    pub len: usize,
}

/// How a button presents its def. Chosen once per run by `--panel-buttons`.
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, clap::ValueEnum)]
pub enum ButtonStyle {
    /// A 40 px square: the icon with the accelerator letter, dim, beside
    /// it. No label, no underline.
    #[default]
    #[value(name = "key")]
    KeyHint,
    /// A 48 px tall segment: the icon over the Title-case label with the
    /// accelerator glyph underlined.
    Below,
}

/// Static metadata for one button.
#[derive(Debug, Clone, Copy)]
pub struct ButtonDef {
    /// Command this button emits on click.
    pub command: Command,
    /// Display label: Title case, ASCII, no whitespace. ASCII is a hard
    /// requirement: `show` measures labels in a mono font by `chars()`,
    /// `widgets::underlined_label` splits them by byte index and
    /// `accel_key` reads that index as a char index, so bytes, glyphs and
    /// advance columns must all agree. `labels_are_title_case_ascii_and_underline_index_is_in_range`
    /// pins it.
    pub label: &'static str,
    /// BYTE index into `label` of the accelerator glyph:
    /// [`super::widgets::underlined_label`] splits the label there to
    /// underline it, and `accel_key` reads the same index as a `chars()`
    /// index (equal only because labels are ASCII).
    pub underline_idx: usize,
    /// The icon's embedded SVG, from the table in `assets.rs`. egui's
    /// image loader rasterises it per host at the size the button draws
    /// it, keyed by the mark's uri.
    pub icon: &'static super::assets::Svg,
    /// What the button does, as the `key` style's hover tooltip says it:
    /// a short verb phrase, one line, no trailing stop.
    pub tip: &'static str,
}

/// The capture-mode panel buttons in strip order, cut by
/// [`NORMAL_GROUPS`] into three groups.
///
/// The primary group (accent) is the five actions that finish the
/// capture with the image as it is. The second group is the hand-offs:
/// SHARE and SCROLL give the region to a live helper, OCR swaps the strip
/// for a second round of decisions. The last group is the two ways out.
///
/// Accelerator keys (not stored — derived from `underline_idx`):
///   0: Upload — U   (0x55)
///   1: Edit   — E
///   2: Video  — V   (0x56)
///   3: Copy   — C   (0x43)
///   4: Save   — S   (0x53)
///   5: Share  — H   (0x48), underlined on the second char because every
///      other letter of Share is spoken for (S=Save, A/R/E=Edit, Reset).
///      'h' is also the pre-capture color-sampler key, which does not
///      collide: that branch only runs while nothing is captured, and the
///      panel — and therefore this lookup — only exists once something is.
///   6: Scroll — L   (0x4C), underlined on the fifth char because
///      S, C and R already belong to Save, Copy and Reset
///   7: OCR    — O   (0x4F)
///   8: Reset  — R   (0x52)
///   9: Exit   — X   (0x58), underlined on the second char
const NORMAL_DEFS: &[ButtonDef] = &[
    ButtonDef {
        command: Command::Upload,
        label: "Upload",
        underline_idx: 0,
        icon: &super::assets::UPLOAD,
        tip: "Upload to default destination",
    },
    ButtonDef {
        command: Command::Edit,
        label: "Edit",
        underline_idx: 0,
        icon: &super::assets::EDIT,
        tip: "Open image editor",
    },
    ButtonDef {
        command: Command::Video,
        label: "Video",
        underline_idx: 0,
        icon: &super::assets::VIDEO,
        tip: "Record screen video",
    },
    ButtonDef {
        command: Command::Copy,
        label: "Copy",
        underline_idx: 0,
        icon: &super::assets::COPY,
        tip: "Copy to clipboard",
    },
    ButtonDef {
        command: Command::Save,
        label: "Save",
        underline_idx: 0,
        icon: &super::assets::SAVE,
        tip: "Save image to a file",
    },
    ButtonDef {
        command: Command::Share,
        label: "Share",
        underline_idx: 1,
        icon: &super::assets::SHARE,
        tip: "Share region to meeting app",
    },
    ButtonDef {
        command: Command::ScrollCapture,
        label: "Scroll",
        underline_idx: 4,
        icon: &super::assets::SCROLL,
        tip: "Capture scrolling content",
    },
    ButtonDef {
        command: Command::Ocr,
        label: "OCR",
        underline_idx: 0,
        icon: &super::assets::OCR,
        tip: "Recognize text (OCR)",
    },
    ButtonDef {
        command: Command::Reset,
        label: "Reset",
        underline_idx: 0,
        icon: &super::assets::RESET,
        tip: "Reset selection",
    },
    ButtonDef {
        command: Command::Exit,
        label: "Exit",
        underline_idx: 1,
        icon: &super::assets::EXIT,
        tip: "Cancel and exit",
    },
];

/// [Upload Edit Video Copy Save] [Share Scroll OCR] [Reset Exit].
const NORMAL_GROUPS: &[ButtonGroup] = &[
    ButtonGroup {
        tone: GroupTone::Primary,
        len: 5,
    },
    ButtonGroup {
        tone: GroupTone::Secondary,
        len: 3,
    },
    ButtonGroup {
        tone: GroupTone::Secondary,
        len: 2,
    },
];

/// The buttons shown once recognized text has been lifted off the
/// selection: what to *do* with that text, plus the two ways out.
///
/// The accelerators reuse `u`/`s`/`c`/`x` from the capture strip on
/// purpose: only one set is ever on screen, and `lookup_command_by_key`
/// is scoped to that set.
const OCR_DEFS: &[ButtonDef] = &[
    ButtonDef {
        command: Command::OcrUpload,
        label: "Upload",
        underline_idx: 0,
        icon: &super::assets::UPLOAD,
        tip: "Upload text to default destination",
    },
    ButtonDef {
        command: Command::OcrSearch,
        label: "Search",
        underline_idx: 0,
        icon: &super::assets::SEARCH,
        tip: "Search the web for this text",
    },
    ButtonDef {
        command: Command::OcrCopy,
        label: "Copy",
        underline_idx: 0,
        icon: &super::assets::COPY,
        tip: "Copy text to clipboard",
    },
    ButtonDef {
        command: Command::OcrBack,
        label: "Back",
        underline_idx: 0,
        icon: &super::assets::BACK,
        tip: "Back to previous options",
    },
    ButtonDef {
        command: Command::Exit,
        label: "Exit",
        underline_idx: 1,
        icon: &super::assets::EXIT,
        tip: "Cancel and exit",
    },
];

/// [Upload Search Copy] [Back Exit].
const OCR_GROUPS: &[ButtonGroup] = &[
    ButtonGroup {
        tone: GroupTone::Primary,
        len: 3,
    },
    ButtonGroup {
        tone: GroupTone::Secondary,
        len: 2,
    },
];

/// The scroll-picker's instruction, in the readout's place. One
/// sentence, kept short because the strip is as wide as it is: the wrap
/// search cuts it into two balanced lines (`show::hint_wrap`) and the
/// panel is sized from that, so every word costs tray width.
pub const SCROLL_PICK_HINT: &str = "Resize the selection to the scrolling area, then click where mouse should scroll.";

/// What a set puts between the emblem and its buttons.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Body {
    /// The two-line readout: the selection's size, or the word count.
    Readout,
    /// A wrapped instruction, sitting straight on the chassis — no fill
    /// of its own, the way the readout has none.
    Hint(&'static str),
}

/// The buttons shown while a scroll point is being picked: the two ways
/// out, in one grey group. Nothing primary — the accepting gesture is the
/// click on the desktop, not a button.
const SCROLL_PICK_DEFS: &[ButtonDef] = &[
    ButtonDef {
        command: Command::ScrollBack,
        label: "Back",
        underline_idx: 0,
        icon: &super::assets::BACK,
        tip: "Back to previous options",
    },
    ButtonDef {
        command: Command::Exit,
        label: "Exit",
        underline_idx: 1,
        icon: &super::assets::EXIT,
        tip: "Cancel and exit",
    },
];

/// [Back Exit].
const SCROLL_PICK_GROUPS: &[ButtonGroup] = &[ButtonGroup {
    tone: GroupTone::Secondary,
    len: 2,
}];

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
        .find(|(_, def)| def.accel_key() == lower)
        .map(|(_, def)| def.command)
}

/// All 32 on/off combinations of the five switches, so invariants are
/// checked against every strip the shell can ask for rather than just
/// the extremes. Module level so `show`'s tests can reach it too.
#[cfg(test)]
pub const FEATURE_COMBINATIONS: [PanelFeatures; 32] = {
    let mut out = [PanelFeatures::ALL; 32];
    let mut i = 0;
    while i < 32 {
        out[i] = PanelFeatures {
            upload: i & 1 != 0,
            scroll_capture: i & 2 != 0,
            ocr: i & 4 != 0,
            share: i & 8 != 0,
            video: i & 16 != 0,
        };
        i += 1;
    }
    out
};

#[cfg(test)]
mod tests {
    use super::super::assets;
    use super::*;
    use egui::load::{SizeHint, TexturePoll};

    /// The size every icon test rasterises at: the tray's icon box at
    /// 100 %.
    const ICON_PX: u32 = super::super::theme::tokens::ICON as u32;

    fn exact(px: u32) -> SizeHint {
        SizeHint::Size {
            width: px,
            height: px,
            maintain_aspect_ratio: false,
        }
    }

    /// Every mark the tray can show must rasterise. The SVG loader caches
    /// failures as well as successes, so a mark that fails once is a blank
    /// button for the rest of the run; `assets::preload` only logs it. The
    /// emblem rides the same path, so it is in the table too.
    #[test]
    fn every_panel_icon_parses() {
        for svg in assets::ALL {
            let image = egui_extras::image::load_svg_bytes_with_size(svg.bytes, exact(ICON_PX), &Default::default())
                .unwrap_or_else(|err| panic!("{} failed to rasterise: {err}", svg.uri));
            assert_eq!(image.size, [ICON_PX as usize; 2], "{}", svg.uri);
        }
    }

    /// The real path a mark takes on a host: through the installed loader,
    /// at the pixel size a 20 pt icon asks for at that monitor's DPI. The
    /// texture must be allocated at exactly that many pixels, which is
    /// what keeps the tray crisp at 125-200 %.
    #[test]
    fn every_icon_loads_through_the_installed_loader_at_each_dpi() {
        let ctx = egui::Context::default();
        egui_extras::install_image_loaders(&ctx);
        for dpi in [1.0_f32, 1.25, 1.5, 2.0] {
            let px = (super::super::theme::tokens::ICON * dpi).round() as u32;
            for svg in assets::ALL {
                let poll = svg
                    .source()
                    .load(&ctx, egui::TextureOptions::LINEAR, exact(px))
                    .unwrap_or_else(|err| panic!("{} at {dpi}: {err}", svg.uri));
                let TexturePoll::Ready {
                    texture,
                } = poll
                else {
                    panic!("{} at {dpi} is still loading", svg.uri);
                };
                assert!(texture.size.x > 0.0, "{} at {dpi}", svg.uri);
                let manager = ctx.tex_manager();
                let meta = manager.read();
                let meta = meta
                    .meta(texture.id)
                    .expect("the loader allocated the texture");
                assert_eq!(meta.size, [px as usize; 2], "{} at {dpi}", svg.uri);
            }
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
            video: false,
            ocr: false,
        };
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, off, 'u'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, off, 'h'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, off, 'l'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, off, 'v'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, off, 'o'), None);
        // UPLOAD is one switch across both strips — text is still an upload.
        assert_eq!(lookup_command_by_key(PanelButtonSet::Ocr, off, 'u'), None);
        assert_eq!(lookup_command_by_key(PanelButtonSet::Ocr, off, 's'), Some(Command::OcrSearch));

        // The non-configurable core survives every combination.
        for features in FEATURE_COMBINATIONS {
            for (key, cmd) in [
                ('e', Command::Edit),
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
                    .map(|(_, d)| d.command)
                    .collect();
                assert!(!visible.is_empty(), "{set:?} emptied by {features:?}");

                let mut full = set.defs().iter().map(|d| d.command);
                for cmd in &visible {
                    assert!(full.any(|c| c == *cmd), "{cmd:?} out of order in {set:?} under {features:?}");
                }
            }
        }
    }

    /// The index `visible_defs` yields is the def's position in the full
    /// table, so it survives a neighbour being switched off; `show`
    /// derives widget ids from it on that promise.
    #[test]
    fn visible_defs_yields_table_indices() {
        for set in PanelButtonSet::ALL {
            for features in FEATURE_COMBINATIONS {
                for (i, def) in set.visible_defs(features) {
                    assert!(
                        std::ptr::eq(def, &set.defs()[i]),
                        "{set:?} {features:?}: index {i} is not the table position"
                    );
                }
            }
        }
    }

    /// The label text is geometry: the `below` style sizes every button
    /// from its laid-out label, the bundled mono face makes each glyph the
    /// same advance, and `underlined_label` splits the job by BYTE index
    /// while `accel_key` reads a `chars()` index. All of that holds only
    /// for ASCII, whitespace-free labels whose `underline_idx` is inside
    /// the string and names the accelerator glyph.
    #[test]
    fn labels_are_title_case_ascii_and_underline_index_is_in_range() {
        for set in PanelButtonSet::ALL {
            for def in set.defs() {
                let label = def.label;
                assert!(label.is_ascii(), "{label} in {set:?} is not ASCII");
                assert!(!label.chars().any(char::is_whitespace), "{label} in {set:?} contains whitespace");
                assert!(
                    label
                        .chars()
                        .next()
                        .is_some_and(|c| c.is_ascii_uppercase()),
                    "{label} in {set:?} does not start with a capital"
                );
                assert!(
                    def.underline_idx < label.len(),
                    "{label} in {set:?}: underline_idx {} past the label",
                    def.underline_idx
                );
                assert_eq!(
                    label.as_bytes()[def.underline_idx].to_ascii_lowercase() as char,
                    def.accel_key(),
                    "{label} in {set:?}: byte index and chars() index disagree"
                );
            }
        }
    }

    /// The exact spellings from the design workbench's capture profile.
    /// Pinned as literals because a respelling silently changes the button
    /// widths the `below` style measures.
    #[test]
    fn labels_match_the_workbench_capture_table() {
        let normal: Vec<&str> = PanelButtonSet::Normal
            .defs()
            .iter()
            .map(|d| d.label)
            .collect();
        assert_eq!(
            normal,
            ["Upload", "Edit", "Video", "Copy", "Save", "Share", "Scroll", "OCR", "Reset", "Exit"]
        );
        let ocr: Vec<&str> = PanelButtonSet::Ocr
            .defs()
            .iter()
            .map(|d| d.label)
            .collect();
        assert_eq!(ocr, ["Upload", "Search", "Copy", "Back", "Exit"]);
    }

    /// The group lengths are a partition of the table: a length that
    /// drifts from the table would silently drop a button off the strip
    /// (or index past it).
    #[test]
    fn groups_partition_each_table() {
        for set in PanelButtonSet::ALL {
            let total: usize = set.groups().iter().map(|g| g.len).sum();
            assert_eq!(total, set.defs().len(), "{set:?}");
            assert!(set.groups().iter().all(|g| g.len > 0), "{set:?} has an empty group");
        }
    }

    /// The design: the accent group holds the actions that finish the
    /// capture, the grey groups the hand-offs and the ways out.
    #[test]
    fn groups_match_the_design() {
        let labels = |set: PanelButtonSet| -> Vec<(GroupTone, Vec<&str>)> {
            set.visible_groups(PanelFeatures::ALL)
                .into_iter()
                .map(|(tone, members)| {
                    (
                        tone,
                        members
                            .iter()
                            .map(|(_, d)| d.label)
                            .collect(),
                    )
                })
                .collect()
        };
        assert_eq!(
            labels(PanelButtonSet::Normal),
            vec![
                (GroupTone::Primary, vec!["Upload", "Edit", "Video", "Copy", "Save"]),
                (GroupTone::Secondary, vec!["Share", "Scroll", "OCR"]),
                (GroupTone::Secondary, vec!["Reset", "Exit"]),
            ]
        );
        assert_eq!(
            labels(PanelButtonSet::Ocr),
            vec![
                (GroupTone::Primary, vec!["Upload", "Search", "Copy"]),
                (GroupTone::Secondary, vec!["Back", "Exit"]),
            ]
        );
    }

    /// Flattening the visible groups gives exactly `visible_defs`, under
    /// every switch combination, and a group whose every button is off
    /// vanishes rather than leaving an empty fill behind.
    #[test]
    fn visible_groups_flatten_to_visible_defs_and_never_empty() {
        for set in PanelButtonSet::ALL {
            for features in FEATURE_COMBINATIONS {
                let groups = set.visible_groups(features);
                assert!(groups.iter().all(|(_, m)| !m.is_empty()), "{set:?} {features:?}");
                let flat: Vec<usize> = groups
                    .iter()
                    .flat_map(|(_, m)| m.iter().map(|(i, _)| *i))
                    .collect();
                let expected: Vec<usize> = set
                    .visible_defs(features)
                    .map(|(i, _)| i)
                    .collect();
                assert_eq!(flat, expected, "{set:?} {features:?}");
            }
        }
        let no_handoffs = PanelFeatures {
            share: false,
            scroll_capture: false,
            ocr: false,
            ..PanelFeatures::ALL
        };
        assert_eq!(
            PanelButtonSet::Normal
                .visible_groups(no_handoffs)
                .len(),
            2
        );
    }

    /// The count the OCR readout shows is the number of whitespace-
    /// separated runs across every line, since `full_text` joins the
    /// lines with newlines.
    #[test]
    fn readout_counts_words_across_lines() {
        assert_eq!(Readout::words_in(""), Readout::Words(0));
        assert_eq!(Readout::words_in("   \n  "), Readout::Words(0));
        assert_eq!(Readout::words_in("one two\nthree  four\n"), Readout::Words(4));
    }

    /// The tips are one-line verb phrases: a chip long enough to wrap
    /// belongs in a window, not over the desktop (the C# strips' rule).
    #[test]
    fn tips_are_short_single_line_phrases() {
        for set in PanelButtonSet::ALL {
            for def in set.defs() {
                let tip = def.tip;
                assert!(
                    !tip.is_empty() && !tip.lines().nth(1).is_some(),
                    "{} in {set:?}: {tip:?}",
                    def.label
                );
                assert!(tip.len() <= 40, "{} in {set:?}: tip too long: {tip:?}", def.label);
                assert!(!tip.ends_with('.'), "{} in {set:?}: {tip:?}", def.label);
                assert!(
                    tip.chars()
                        .next()
                        .is_some_and(|c| c.is_ascii_uppercase()),
                    "{} in {set:?}: {tip:?}",
                    def.label
                );
            }
        }
    }

    /// Title-casing the labels moved the accelerator glyphs of Share,
    /// Scroll and Exit off an uppercase letter; `accel_key` lowercases, so
    /// the keys must still resolve from either case.
    #[test]
    fn accelerators_survive_title_case() {
        let all = PanelFeatures::ALL;
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, all, 'H'), Some(Command::Share));
        assert_eq!(
            lookup_command_by_key(PanelButtonSet::Normal, all, 'l'),
            Some(Command::ScrollCapture)
        );
        assert_eq!(lookup_command_by_key(PanelButtonSet::Normal, all, 'x'), Some(Command::Exit));
        assert_eq!(lookup_command_by_key(PanelButtonSet::Ocr, all, 'b'), Some(Command::OcrBack));
    }

    /// UPLOAD is a real button with the paper-plane mark; the Clowd logo
    /// is the tray emblem and must never be a button icon — the emblem is
    /// drawn at its own size, from `widgets::emblem`, with no button
    /// around it.
    #[test]
    fn upload_icon_is_the_paper_plane() {
        let upload = PanelButtonSet::Normal
            .defs()
            .iter()
            .find(|d| d.command == Command::Upload)
            .expect("the capture strip has an UPLOAD button");
        assert_eq!(upload.icon.uri, assets::UPLOAD.uri);
        assert_ne!(assets::UPLOAD.uri, assets::CLOWD_LOGO.uri);
        for set in PanelButtonSet::ALL {
            for def in set.defs() {
                assert_ne!(def.icon.uri, assets::CLOWD_LOGO.uri, "the emblem must not be a button icon");
            }
        }
    }

    /// The emblem is rasterised through the same path as the button icons,
    /// so it must parse, and `widgets::emblem` centres a square mark, so
    /// the canvas must be square (16 x 16 as authored — the rasteriser
    /// reports the SVG's own point size as `source_size`).
    #[test]
    fn clowd_logo_parses() {
        let image = egui_extras::image::load_svg_bytes_with_size(assets::CLOWD_LOGO.bytes, exact(ICON_PX), &Default::default())
            .expect("clowd-logo.svg failed to rasterise");
        assert_eq!(image.size, [ICON_PX as usize; 2]);
        assert_eq!(image.source_size, egui::vec2(16.0, 16.0));
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
}
