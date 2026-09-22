//! The tray as one egui `Area`: measure the strip, place it beside the
//! selection, then lay it out along the chosen axis.
//!
//! Two chassis, one `Area`. [`StripMetrics`] / `single_strip` is the one
//! run every set but the capture one uses: the emblem, the readout and
//! the button groups along the axis, flush inside a group and one `GAP`
//! between them. [`DoubleMetrics`] / `double_strip` is the capture
//! strip's two-row tray — the five finishing actions with full labels on
//! the first row, the hand-offs and the two ways out as accelerator tiles
//! on the second, the latter straight on the chassis with no fill of
//! their own. [`Strip`] is the pair, and everything from the union fit
//! down to the `Area` is written against it.
//!
//! A button hovered for 350 ms grows a tooltip chip saying what it does
//! — as the C# strips do — hung off the strip's far edge (below a row,
//! right of a column) and flipped to the other side when the monitor has
//! no room there. Once a chip is up, the next button's chip follows the
//! pointer without the wait, across the gap between groups too, until the
//! pointer has been off every button for the same 350 ms. The `below`
//! style is the one that raises none: see [`Presentation::wants_tip`].
//!
//! The strip's size is analytic — computed from the same numbers the
//! widgets are laid out with — because placement needs it before the Area
//! is positioned, and because each strip must be centred with its own
//! width for the set-swap guard's premise to hold. A test pins the
//! analytic size against what egui actually lays out at every DPI.

use egui::{pos2, vec2, Rect, Vec2};

use super::assets;
use super::model::{Body, ButtonDef, ButtonStyle, GroupTone, PanelButtonSet, PanelFeatures, PanelLayout, Readout};
use super::place::{self, Axis, Fit, Footprint, Near};
use super::theme::{self, tokens};
use super::widgets;
use crate::ui::egui_host::{PanelInputs, PanelOutcome};
use crate::ui::shared::UiMonitor;
use clowd_rust_core::geometry::RectExt;

/// The Area id. One tray per context, so one id per context.
pub const PANEL_ID: &str = "clowd-tray";
/// The tooltip chip's Area id, and the key its hover clock is kept under.
pub const TIP_ID: &str = "clowd-tray-tip";

/// The tooltip's clock, in egui input time, kept in the context's temp
/// data so it survives between passes: which button the pointer is on and
/// since when, and when a chip was last on screen. The last-shown mark is
/// what makes the chip follow the pointer between buttons without a fresh
/// wait — and outlives the pointer leaving the strip by the grace period.
#[derive(Clone, Copy, Default)]
struct TipClock {
    button: Option<egui::Id>,
    since: f64,
    shown_at: Option<f64>,
}

/// The tooltip chip's rect for a button at `button` inside a strip at
/// `tray`, in points, kept inside `screen`. Below a row / right of a
/// column, flipped to the near side when the far side has no room; then
/// slid along the strip so it stays on the monitor.
fn tip_rect(axis: Axis, tray: Rect, button: Rect, chip: Vec2, screen: Rect) -> Rect {
    let rect = match axis {
        Axis::Row => {
            let top = tray.bottom() + tokens::TIP_GAP_BOTTOM;
            let top = if top + chip.y > screen.bottom() {
                tray.top() - tokens::TIP_GAP_BOTTOM - chip.y
            } else {
                top
            };
            Rect::from_min_size(pos2(button.center().x - chip.x / 2.0, top), chip)
        }
        Axis::Column => {
            let left = tray.right() + tokens::TIP_GAP_RIGHT;
            let left = if left + chip.x > screen.right() {
                tray.left() - tokens::TIP_GAP_RIGHT - chip.x
            } else {
                left
            };
            Rect::from_min_size(pos2(left, button.center().y - chip.y / 2.0), chip)
        }
    };
    let dx = (screen.left() - rect.left()).max(0.0) - (rect.right() - screen.right()).max(0.0);
    let dy = (screen.top() - rect.top()).max(0.0) - (rect.bottom() - screen.bottom()).max(0.0);
    rect.translate(vec2(dx, dy))
}

/// Run the hover clock for this pass and paint the chip once it has
/// waited long enough. `hovered` is the button under the pointer (its id,
/// rect and def), `None` when the pointer is on no button.
///
/// A new button restarts the wait unless a chip was on screen within the
/// last `TIP_DELAY_SECS`: then it shows at once, so a pointer sliding
/// along the strip — through the gap between two groups included — keeps
/// its chip. Only a pause off every button as long as the wait itself
/// brings the wait back.
fn show_tip(ctx: &egui::Context, axis: Axis, tray: Rect, hovered: Option<(egui::Id, Rect, &'static ButtonDef)>, visible: bool) {
    let key = egui::Id::new(TIP_ID);
    let now = ctx.input(|i| i.time);
    let mut clock = ctx.data(|d| {
        d.get_temp::<TipClock>(key)
            .unwrap_or_default()
    });
    let Some((button, rect, def)) = hovered else {
        clock.button = None;
        ctx.data_mut(|d| d.insert_temp(key, clock));
        return;
    };
    if clock.button != Some(button) {
        clock.button = Some(button);
        let in_grace = clock
            .shown_at
            .is_some_and(|t| now - t <= tokens::TIP_DELAY_SECS);
        clock.since = if in_grace { now - tokens::TIP_DELAY_SECS } else { now };
    }
    let waited = now - clock.since;
    if waited < tokens::TIP_DELAY_SECS {
        ctx.data_mut(|d| d.insert_temp(key, clock));
        ctx.request_repaint_after(std::time::Duration::from_secs_f64(tokens::TIP_DELAY_SECS - waited));
        return;
    }
    clock.shown_at = Some(now);
    ctx.data_mut(|d| d.insert_temp(key, clock));
    let text = ctx.fonts_mut(|f| f.layout_job(widgets::tip_job(def)));
    // Whole points, so the chip's edges land where egui pins the Area.
    let chip = (text.size() + 2.0 * vec2(tokens::TIP_PAD_H, tokens::TIP_PAD_V)).ceil();
    let chip_rect = tip_rect(axis, tray, rect, chip, ctx.viewport_rect());
    egui::Area::new(key)
        .order(egui::Order::Tooltip)
        .fixed_pos(chip_rect.min)
        .constrain(false)
        .fade_in(false)
        .interactable(false)
        .show(ctx, |ui| {
            if !visible {
                ui.set_opacity(0.0);
            }
            let (r, _) = ui.allocate_exact_size(chip, egui::Sense::hover());
            widgets::tip(ui.painter(), r, def);
        });
}

/// One visible button as measured: table index, def, and its length
/// along a row.
pub type MeasuredButton = (usize, &'static ButtonDef, f32);

/// The strip's body as measured: which one it is, and — for the
/// instruction — the wrap width it was laid out at, so the pass that
/// paints it uses the width the pass that sized the tray measured.
#[derive(Clone, Copy, PartialEq)]
pub enum MeasuredBody {
    Readout(Readout),
    Hint { text: &'static str, wrap: f32 },
}

/// The narrowest wrap width, in points, that fits `text` in
/// `tokens::HINT_LINES` lines, and the line count it achieved.
///
/// Narrowest is also best-balanced: at the width where the last line is
/// about to spill into a third, the two lines are as near equal as the
/// break points allow. The search starts from the width a perfectly even
/// split would need (half the unwrapped run) and steps up, so it costs a
/// handful of layouts of one short paragraph, once per measured pass.
///
/// A text that will not fit in `HINT_LINES` lines at any width up to its
/// full run returns that full width: one line, never a clipped box.
fn hint_wrap(ctx: &egui::Context, text: &'static str) -> (f32, usize) {
    let full = ctx
        .fonts_mut(|f| f.layout_job(widgets::hint_job(text, f32::INFINITY)))
        .size()
        .x;
    let lines = |wrap: f32| {
        ctx.fonts_mut(|f| f.layout_job(widgets::hint_job(text, wrap)))
            .rows
            .len()
    };
    let start = (full / tokens::HINT_LINES as f32).floor();
    let mut wrap = start;
    while wrap < full {
        let rows = lines(wrap);
        if rows <= tokens::HINT_LINES {
            return (wrap, rows);
        }
        wrap += tokens::HINT_WRAP_STEP;
    }
    (full, lines(full))
}

/// Every number the strip is laid out with, in points, measured before the
/// Area is positioned.
pub struct StripMetrics {
    /// Item thickness across the strip: the button's, and the readout's
    /// square side.
    pub thick: f32,
    /// What sits between the emblem and the buttons, measured: the
    /// readout, or the instruction with the wrap width it was measured
    /// at.
    pub body: MeasuredBody,
    /// The body slot's length along a row.
    pub body_along: f32,
    /// The visible groups in strip order, each with its buttons: table
    /// index, def, and the button's length along a row.
    pub groups: Vec<(GroupTone, Vec<MeasuredButton>)>,
}

impl StripMetrics {
    /// Measure one set under one feature switch set. Only valid inside a
    /// run: `Context::fonts_mut` panics before the first pass.
    pub fn measure(ctx: &egui::Context, set: PanelButtonSet, features: PanelFeatures, style: ButtonStyle, readout: Readout) -> Self {
        let body = match set.body() {
            Body::Readout => MeasuredBody::Readout(readout),
            Body::Hint(text) => {
                let (wrap, _) = hint_wrap(ctx, text);
                MeasuredBody::Hint {
                    text,
                    wrap,
                }
            }
        };
        let thick = match style {
            ButtonStyle::KeyHint => tokens::KEY_TILE,
            ButtonStyle::Below => tokens::BELOW_HEIGHT,
        };
        let groups = set
            .visible_groups(features)
            .into_iter()
            .map(|(tone, members)| {
                let buttons = members
                    .into_iter()
                    .map(|(i, def)| {
                        let along = match style {
                            ButtonStyle::KeyHint => tokens::KEY_TILE,
                            ButtonStyle::Below => {
                                let label = ctx.fonts_mut(|f| f.layout_job(widgets::underlined_label(def)));
                                tokens::BUTTON_MIN_LENGTH.max(label.size().x + 2.0 * tokens::BELOW_PAD_H)
                            }
                        };
                        (i, def, along)
                    })
                    .collect();
                (tone, buttons)
            })
            .collect();
        let body_along = match body {
            MeasuredBody::Readout(readout) => {
                let galley = ctx.fonts_mut(|f| f.layout_job(widgets::readout_job(readout)));
                thick.max(galley.size().x + 2.0 * tokens::READOUT_PAD_H)
            }
            MeasuredBody::Hint {
                text,
                wrap,
            } => {
                let galley = ctx.fonts_mut(|f| f.layout_job(widgets::hint_job(text, wrap)));
                thick.max(galley.size().x + 2.0 * tokens::HINT_PAD_H)
            }
        };
        Self {
            thick,
            body,
            body_along,
            groups,
        }
    }

    /// Every visible button, in strip order, with its length along a row.
    pub fn buttons(&self) -> impl Iterator<Item = &MeasuredButton> {
        self.groups
            .iter()
            .flat_map(|(_, b)| b.iter())
    }

    /// One group's length along `axis`: its buttons flush, no gaps.
    fn group_along(&self, axis: Axis, buttons: &[MeasuredButton]) -> f32 {
        match axis {
            Axis::Row => buttons
                .iter()
                .map(|(_, _, along)| *along)
                .sum(),
            Axis::Column => buttons.len() as f32 * self.thick,
        }
    }

    /// The strip's content size (the tray's padding not included) for one
    /// axis. A column is `col_inner` wide throughout, so a set swap or a
    /// feature change can never change its width.
    pub fn content_size(&self, axis: Axis, col_inner: f32) -> Vec2 {
        // One gap before each group, so `n` groups cost `n` gaps: the
        // first follows the readout.
        let groups_along: f32 = self
            .groups
            .iter()
            .map(|(_, buttons)| tokens::GAP + self.group_along(axis, buttons))
            .sum();
        let body_along = match axis {
            Axis::Row => self.body_along,
            Axis::Column => self.thick,
        };
        let along = tokens::EMBLEM_SLOT + tokens::EMBLEM_GAP + body_along + groups_along;
        match axis {
            Axis::Row => vec2(along, self.thick),
            Axis::Column => vec2(col_inner, along),
        }
    }

    /// The width this strip wants when it stands on its end: the widest
    /// thing it has to hold. Its caller measures with every switch on, so
    /// flipping a feature can never move the tray's edge.
    pub fn col_inner(&self) -> f32 {
        let mut w = tokens::EMBLEM_SLOT.max(self.body_along);
        for (_, _, along) in self.buttons() {
            w = w.max(*along);
        }
        w
    }
}

/// Every number the capture strip's two-row chassis is laid out with.
///
/// The runs are `NORMAL_GROUPS` read by TONE: the accent actions that
/// finish the capture and any grey run attached to them are the labelled
/// first row, and the bare runs are the second — every one but the last
/// is a hand-off, the last is the ways out. A run can empty out under
/// [`PanelFeatures`], and the layout closes the hole rather than leaving a
/// gap where it was.
///
/// A row is the emblem over the readout in one head column, the accent
/// actions beside the emblem with their full labels, and the odds and
/// ends beside the readout as accelerator tiles — the hand-offs left, the
/// ways out flush right. A column is the emblem beside the readout, then
/// the accent actions one per line at the tray's full width, then the odds
/// and ends `DOUBLE_COLS` to a line.
///
/// The emblem and the readout take one button's box each and centre their
/// contents in it, so both sit square over what is under them: in a row
/// that box is `head`, the same width for the two of them; in a column it
/// is one grid cell, so the emblem lands dead centre over the first
/// column of tiles and the readout over the second.
///
/// In a row the emblem is followed by a full `GAP`, not the single
/// strip's tighter `EMBLEM_GAP`: it heads a column of buttons rather than
/// a run, so it has to take exactly one button's room — a tile and a gap
/// — or everything under it would start half a step to its left. In a
/// column the two head boxes are flush, like the grid cells they sit
/// over.
///
/// Switching enough of the accent actions off leaves the first row
/// shorter than the second, which reads as a tray with a bite out of its
/// top right corner (UPLOAD and VIDEO both off is the case that does it
/// with everything else on). The first of the ways out is then lifted onto
/// the first row, flush right, so the two rows end together and RESET sits
/// straight above EXIT. A column never does this: its grid has no ragged
/// row to even out, and it draws a rule between the two clusters instead
/// (`widgets::divider`), which is the separation a row gets for free from
/// pushing its ways out to the far end.
pub struct DoubleMetrics {
    /// What the readout says: the selection's size.
    pub readout: Readout,
    /// The head column of a row-mode tray — the emblem above the readout,
    /// both as wide as the wider of the two, so the accent row and the
    /// second row start at the same x.
    pub head: f32,
    /// The accent run of the first row: labelled buttons with their row
    /// lengths.
    pub primary: Vec<MeasuredButton>,
    /// The grey run flush against `primary`, labelled the same way. One
    /// grey pill is drawn under both runs and the accent one laid over
    /// `primary`, so this reads as the tail of the accent block rather
    /// than as a second block beside it.
    pub attached: Vec<MeasuredButton>,
    /// The second row's left cluster, as `key` tiles.
    pub funcs: Vec<MeasuredButton>,
    /// The second row's right cluster, as `key` tiles.
    pub tail: Vec<MeasuredButton>,
    /// The one way out lifted off `tail` onto the accent row, flush right,
    /// when the accent row would otherwise be the shorter of the two.
    /// Empty otherwise — and irrelevant to a column, whose grid lays the
    /// same buttons out in table order either way.
    pub promoted: Vec<MeasuredButton>,
    /// The width this measure would want in a column: enough for the
    /// widest labelled button, for the emblem beside the readout and for
    /// `DOUBLE_COLS` tiles side by side — and a whole multiple of
    /// `DOUBLE_COLS`, so the grid splits it evenly.
    ///
    /// Only `Strip::col_inner` reads it, and only off a measure taken
    /// with every switch on; the width the tray is laid out at is passed
    /// in, so flipping a feature never moves the tray's edge.
    pub col_inner: f32,
}

impl DoubleMetrics {
    /// Measure the capture strip under one feature switch set. Only valid
    /// inside a run: `Context::fonts_mut` panics before the first pass.
    pub fn measure(ctx: &egui::Context, features: PanelFeatures, readout: Readout) -> Self {
        let mut primary = Vec::new();
        let mut attached = Vec::new();
        let mut bare: Vec<Vec<MeasuredButton>> = Vec::new();
        for (tone, members) in PanelButtonSet::Normal.visible_groups(features) {
            let run: Vec<MeasuredButton> = members
                .into_iter()
                .map(|(i, def)| {
                    // The tone is also the presentation: the finishing run
                    // is labelled and sizes itself from its labels, the
                    // bare runs are square accelerator tiles.
                    let along = match tone {
                        GroupTone::Primary | GroupTone::Secondary => widgets::label_button_length(ctx, def),
                        GroupTone::Bare => tokens::KEY_TILE,
                    };
                    (i, def, along)
                })
                .collect();
            match tone {
                GroupTone::Primary => primary = run,
                GroupTone::Secondary => attached = run,
                GroupTone::Bare => bare.push(run),
            }
        }
        // The last bare run is the ways out; anything before it is a
        // hand-off. `visible_groups` has already dropped the runs whose
        // every button is switched off.
        let mut tail = bare.pop().unwrap_or_default();
        let funcs: Vec<MeasuredButton> = bare.concat();

        let galley = ctx.fonts_mut(|f| f.layout_job(widgets::readout_job(readout)));
        let body_along = galley.size().x + 2.0 * tokens::READOUT_PAD_H;
        let head = tokens::EMBLEM_SLOT.max(body_along);

        // Even the two rows up: with the first row the shorter of the two
        // there is a tile-sized hole above the ways out, so the first of
        // them moves up into it. Never the last one — a row of ways out
        // with nothing left on it below would just move the hole. Both
        // rows share the same head column, so it cancels out of the
        // comparison.
        let finishers_len = Self::run_len(&primary) + Self::run_len(&attached);
        let second_len = Self::run_len(&funcs) + Self::gap_between(&funcs, &tail) + Self::run_len(&tail);
        let promoted: Vec<MeasuredButton> = if tail.len() > 1 && finishers_len < second_len {
            vec![tail.remove(0)]
        } else {
            Vec::new()
        };

        let cols = tokens::DOUBLE_COLS as f32;
        let widest_primary = primary
            .iter()
            .chain(attached.iter())
            .map(|(_, _, along)| *along)
            .fold(tokens::KEY_TILE, f32::max);
        // In a column the emblem and the readout are one grid cell each,
        // flush, so the tray has to be at least `DOUBLE_COLS` of the wider
        // of the two — otherwise one of them would be squeezed narrower
        // than the tiles it sits over and stop looking centred on them.
        let head_row = cols * head;
        // The grid's buttons are left-aligned at the labelled buttons'
        // padding so every icon in a column sits at one x, so a cell has
        // to be wide enough for a tile carrying that padding — wider than
        // the plain `KEY_TILE` the second row of a wide tray uses.
        let grid_floor = cols * widgets::key_button_length(ctx, tokens::LABEL_PAD_H);
        let col_inner = (widest_primary.max(head_row).max(grid_floor) / cols).ceil() * cols;

        Self {
            readout,
            head,
            primary,
            attached,
            funcs,
            tail,
            promoted,
            col_inner,
        }
    }

    /// One run's length along a row: its buttons flush, no gaps.
    fn run_len(buttons: &[MeasuredButton]) -> f32 {
        buttons
            .iter()
            .map(|(_, _, along)| *along)
            .sum()
    }

    /// One row's length: the head column, the gap after the emblem and
    /// the run itself.
    fn row_len(head: f32, buttons: &[MeasuredButton]) -> f32 {
        head + tokens::GAP + Self::run_len(buttons)
    }

    /// One gap between a row's two clusters — none when either of them is
    /// switched off altogether, so a vanished cluster leaves no hole.
    fn gap_between(left: &[MeasuredButton], right: &[MeasuredButton]) -> f32 {
        if left.is_empty() || right.is_empty() {
            0.0
        } else {
            tokens::GAP
        }
    }

    /// The whole finishing run in order: the accent buttons, then the
    /// grey ones attached to them. Flush throughout — they are one block
    /// on screen, drawn in two colours (`show::finishing_run`).
    pub fn finishers(&self) -> impl Iterator<Item = &MeasuredButton> {
        self.primary
            .iter()
            .chain(self.attached.iter())
    }

    /// The ways out in table order, whether or not a row lifted the first
    /// of them up beside the accent actions. A column always shows them
    /// together, under the rule.
    pub fn ways_out(&self) -> impl Iterator<Item = &MeasuredButton> {
        self.promoted.iter().chain(self.tail.iter())
    }

    /// How many lines the column-mode grid takes. The two clusters are
    /// chunked apart, so four hand-offs and two ways out are 2 + 1 lines
    /// whatever the switches did — never a line with one of each on it,
    /// which is what the rule between them would then be cutting through.
    fn secondary_rows(&self) -> usize {
        let lines = |n: usize| n.div_ceil(tokens::DOUBLE_COLS);
        lines(self.funcs.len()) + lines(self.ways_out().count())
    }

    /// Whether the column shows the rule: only with something on either
    /// side of it.
    fn has_divider(&self) -> bool {
        !self.funcs.is_empty() && self.ways_out().count() > 0
    }

    /// The strip's content size (the tray's padding not included).
    /// `col_inner` is the width this set draws a column at.
    pub fn content_size(&self, axis: Axis, col_inner: f32) -> Vec2 {
        match axis {
            Axis::Row => {
                // The gap before a right-aligned cluster is the least it
                // may be; that cluster usually pushes it much wider.
                let finishers: Vec<MeasuredButton> = self.finishers().copied().collect();
                let first =
                    Self::row_len(self.head, &finishers) + Self::gap_between(&finishers, &self.promoted) + Self::run_len(&self.promoted);
                let second = Self::row_len(self.head, &self.funcs) + Self::gap_between(&self.funcs, &self.tail) + Self::run_len(&self.tail);
                vec2(first.max(second), 2.0 * tokens::DOUBLE_ROW + tokens::GAP)
            }
            Axis::Column => {
                let mut len = tokens::DOUBLE_ROW;
                let finishers = self.finishers().count();
                if finishers > 0 {
                    len += tokens::GAP + finishers as f32 * tokens::DOUBLE_ROW;
                }
                let rows = self.secondary_rows();
                if rows > 0 {
                    len += tokens::GAP + rows as f32 * tokens::DOUBLE_ROW;
                }
                if self.has_divider() {
                    len += tokens::DIVIDER_BLOCK;
                }
                vec2(col_inner, len)
            }
        }
    }

    /// The air between one row's two clusters, so the right-hand one ends
    /// flush with the tray's edge whatever the other row is doing.
    fn spacer(&self, content_width: f32, left: f32, right: &[MeasuredButton]) -> f32 {
        let used = self.head + tokens::GAP + left + Self::run_len(right);
        (content_width - used).max(0.0)
    }
}

/// The strip as measured, in whichever chassis its set asked for.
pub enum Strip {
    Single(StripMetrics),
    Double(DoubleMetrics),
}

impl Strip {
    pub fn measure(ctx: &egui::Context, set: PanelButtonSet, features: PanelFeatures, style: ButtonStyle, readout: Readout) -> Self {
        match set.layout() {
            PanelLayout::Single => Self::Single(StripMetrics::measure(ctx, set, features, style, readout)),
            PanelLayout::Double => Self::Double(DoubleMetrics::measure(ctx, features, readout)),
        }
    }

    /// The content size for one axis. `col_inner` is the single strip's
    /// column width; the double chassis carries its own.
    pub fn content_size(&self, axis: Axis, col_inner: f32) -> Vec2 {
        match self {
            Self::Single(m) => m.content_size(axis, col_inner),
            Self::Double(m) => m.content_size(axis, col_inner),
        }
    }

    /// The width this strip draws at when it stands on its end.
    pub fn col_inner(&self) -> f32 {
        match self {
            Self::Single(m) => m.col_inner(),
            Self::Double(m) => m.col_inner,
        }
    }
}

/// The strip box in physical pixels: the content plus the tray's padding
/// on either side, rounded once per axis.
fn outer_px(content: Vec2, ppp: f32) -> (i32, i32) {
    let pad = 2.0 * tokens::PAD;
    (((content.x + pad) * ppp).round() as i32, ((content.y + pad) * ppp).round() as i32)
}

/// The readout each set would show, given the current inputs: the size
/// for the capture strip, the word count for the OCR strip. Before any
/// text is lifted the count is unknown, and zero stands in: the "words"
/// caption is the wide line, so the slot's width does not depend on the
/// number.
fn readout_for(set: PanelButtonSet, p: &PanelInputs) -> Readout {
    match (set, p.readout) {
        // The scroll-pick strip shows an instruction, not a readout; the
        // value is carried along unused so `measure` stays one signature.
        (PanelButtonSet::ScrollPick, r) => r,
        (PanelButtonSet::Normal, _) => Readout::Size {
            width: p.selection.width(),
            height: p.selection.height(),
        },
        (PanelButtonSet::Ocr, words @ Readout::Words(_)) => words,
        (
            PanelButtonSet::Ocr,
            Readout::Size {
                ..
            },
        ) => Readout::Words(0),
    }
}

/// The width one set draws at when the tray stands on its end: its own,
/// measured with every switch on, so flipping a feature can never move
/// the tray's edge. Two sets may want different widths — the capture
/// strip's grid is twice a tile wide and the OCR strip is one — the same
/// way they have always drawn rows of different lengths.
fn col_inner_for(ctx: &egui::Context, p: &PanelInputs, set: PanelButtonSet) -> f32 {
    Strip::measure(ctx, set, PanelFeatures::ALL, p.style, readout_for(set, p)).col_inner()
}

/// The longest box either set can become in each orientation with every
/// feature on, so the side choice depends on neither which set is up nor
/// which switches are on.
fn union_fit(ctx: &egui::Context, p: &PanelInputs, ppp: f32) -> Fit {
    let mut fit = Fit {
        row: Footprint {
            len: 0,
            thick: 0,
        },
        col: Footprint {
            len: 0,
            thick: 0,
        },
    };
    for &set in PanelButtonSet::UNION {
        let m = Strip::measure(ctx, set, PanelFeatures::ALL, p.style, readout_for(set, p));
        let col_inner = m.col_inner();
        let (w, h) = outer_px(m.content_size(Axis::Row, col_inner), ppp);
        fit.row.len = fit.row.len.max(w);
        fit.row.thick = fit.row.thick.max(h);
        let (w, h) = outer_px(m.content_size(Axis::Column, col_inner), ppp);
        fit.col.len = fit.col.len.max(h);
        fit.col.thick = fit.col.thick.max(w);
    }
    fit
}

/// Measure the strip that is up and place its box, in physical pixels —
/// everything `show` needs before the Area exists, and the whole of what
/// the placement tests check.
fn measure_and_place(
    ctx: &egui::Context,
    p: &PanelInputs,
    monitor: UiMonitor,
) -> (Strip, f32, place::Side, clowd_rust_core::geometry::ScreenRect) {
    let ppp = monitor.dpi_scale.max(0.1);
    let mut fit = union_fit(ctx, p, ppp);
    let col_inner = col_inner_for(ctx, p, p.set);
    let m = Strip::measure(ctx, p.set, p.features, p.style, p.readout);
    // A set outside the union (the scroll-picker) is not in `fit`, and the
    // side cascade's "does the strip fit along the monitor" test has to be
    // asked about the strip that is actually up — otherwise a two-line
    // instruction wider than the monitor would still pass as `Below`.
    if !PanelButtonSet::UNION.contains(&p.set) {
        let (w, h) = outer_px(m.content_size(Axis::Row, col_inner), ppp);
        fit.row.len = fit.row.len.max(w);
        fit.row.thick = fit.row.thick.max(h);
    }
    let (side, rect_px) = place::place(p.anchor, monitor.bounds, fit, Near::at_dpi(ppp), p.set.axis_lock(), |axis| {
        outer_px(m.content_size(axis, col_inner), ppp)
    });
    (m, col_inner, side, rect_px)
}

/// How a button says what it is. The single strip picks one for the whole
/// tray from [`ButtonStyle`]; the capture strip's double chassis uses two
/// at once — `Label` on the accent row, `Key` on the row beneath it.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Presentation {
    /// Icon and accelerator letter in a square: needs a hover chip to say
    /// what it does.
    Key,
    /// Icon over the label.
    Below,
    /// Icon beside the full label, one tile tall.
    Label,
}

impl Presentation {
    fn of(style: ButtonStyle) -> Self {
        match style {
            ButtonStyle::KeyHint => Self::Key,
            ButtonStyle::Below => Self::Below,
        }
    }

    /// Whether a button drawn this way needs a hover chip to say what it
    /// does. The accelerator tile has nothing but an icon and a letter,
    /// and a labelled button's label is one verb where the tip is the
    /// whole phrase ("Upload" against "Upload to default destination"),
    /// so both take one. `below` is the exception: it is a whole strip of
    /// labels under icons, and a chip over every one of them was noise.
    fn wants_tip(self) -> bool {
        !matches!(self, Self::Below)
    }

    /// The padding this presentation carries on either side of its
    /// contents when nothing asks for a shared one.
    fn pad_h(self) -> f32 {
        match self {
            Self::Key => tokens::KEY_PAD_H,
            Self::Below => tokens::BELOW_PAD_H,
            Self::Label => tokens::LABEL_PAD_H,
        }
    }
}

/// Everything about how one run of buttons is drawn, apart from the
/// buttons themselves: which presentation, where its contents sit, and
/// the group colours the hover veil works over.
#[derive(Clone, Copy)]
struct ButtonSkin {
    kind: Presentation,
    align: egui::Align2,
    pad_h: f32,
    base: egui::Color32,
    veil: f32,
}

impl ButtonSkin {
    fn new(kind: Presentation, tone: GroupTone, accent: egui::Color32) -> Self {
        Self {
            kind,
            align: egui::Align2::CENTER_CENTER,
            pad_h: kind.pad_h(),
            base: theme::group_fill(tone, accent),
            veil: theme::hover_veil(tone),
        }
    }

    /// Contents against the left edge at a shared padding instead of
    /// centred. A stack of buttons all one width would otherwise put each
    /// icon at its own x, since each carries a different amount of text.
    fn left(self, pad_h: f32) -> Self {
        Self {
            align: egui::Align2::LEFT_CENTER,
            pad_h,
            ..self
        }
    }
}

/// Draw one button at `size`, fold what the pointer did with it into
/// `out`, and hand back the box it took. A button is registered as the
/// hovered one only if its presentation wants a chip
/// ([`Presentation::wants_tip`]).
fn button(
    ui: &mut egui::Ui,
    p: &PanelInputs,
    measured: &MeasuredButton,
    size: Vec2,
    skin: ButtonSkin,
    out: &mut PanelOutcome,
    hovered: &mut Option<(egui::Id, Rect, &'static ButtonDef)>,
) -> Rect {
    let (table_idx, def, _) = *measured;
    // The set is part of the id, so hover state and hit tests can never
    // resolve against another strip; the table index keeps a switched-off
    // button from renumbering its neighbours.
    let id = egui::Id::new(("panel", p.set as u8, table_idx));
    let mut widget = match skin.kind {
        Presentation::Key => widgets::key_hint_button(def, id, size, skin.base, skin.veil),
        Presentation::Below => widgets::below_button(def, id, size, skin.base, skin.veil),
        Presentation::Label => widgets::label_button(def, id, size, skin.base, skin.veil),
    };
    widget.align = skin.align;
    widget.pad_h = skin.pad_h;
    let r = widget.show(ui);
    out.over_button |= r.contains_pointer();
    if r.contains_pointer() && skin.kind.wants_tip() {
        *hovered = Some((id, r.rect, def));
    }
    if r.clicked() {
        out.clicked = Some(def.command);
    }
    r.rect
}

/// Draw the finishing run: one grey pill under the whole of it, the
/// accent pill laid over the buttons that carry the accent, so the grey
/// reads as the tail of the accent block rather than as a second block
/// beside it.
///
/// The accent plate's shape index is reserved before the buttons go down
/// and filled in once their boxes are known — the way `Frame` reserves
/// its own background — so it lands behind them without the layout having
/// to be predicted.
fn finishing_run(
    ui: &mut egui::Ui,
    m: &DoubleMetrics,
    p: &PanelInputs,
    size: impl Fn(&MeasuredButton) -> Vec2,
    // How the accent run and the grey run attached to it are drawn.
    skins: (ButtonSkin, ButtonSkin),
    out: &mut PanelOutcome,
    hovered: &mut Option<(egui::Id, Rect, &'static ButtonDef)>,
) {
    let (accent_skin, grey_skin) = skins;
    theme::group_frame(grey_skin.base).show(ui, |ui| {
        ui.spacing_mut().item_spacing = Vec2::ZERO;
        let plate = ui.painter().add(egui::Shape::Noop);
        let mut accent_box: Option<Rect> = None;
        for measured in &m.primary {
            let r = button(ui, p, measured, size(measured), accent_skin, out, hovered);
            accent_box = Some(accent_box.map_or(r, |b: Rect| b.union(r)));
        }
        for measured in &m.attached {
            button(ui, p, measured, size(measured), grey_skin, out, hovered);
        }
        if let Some(b) = accent_box {
            ui.painter()
                .set(plate, egui::Shape::rect_filled(b, tokens::RADIUS, accent_skin.base));
        }
    });
}

/// The original one-run strip: the emblem, the body and the button groups
/// along `axis`, flush inside each group and one `GAP` between them.
fn single_strip(
    ui: &mut egui::Ui,
    m: &StripMetrics,
    p: &PanelInputs,
    axis: Axis,
    col_inner: f32,
    out: &mut PanelOutcome,
    hovered: &mut Option<(egui::Id, Rect, &'static ButtonDef)>,
) {
    let kind = Presentation::of(p.style);
    let strip = |ui: &mut egui::Ui| {
        let across = match axis {
            Axis::Row => m.thick,
            Axis::Column => col_inner,
        };
        let emblem_slot = match axis {
            Axis::Row => vec2(tokens::EMBLEM_SLOT, across),
            Axis::Column => vec2(across, tokens::EMBLEM_SLOT),
        };
        // The emblem's slot is padded around a smaller mark, so it needs
        // less of a gap than the flush button boxes. egui spends
        // `item_spacing` AFTER a widget, so this is set before the emblem
        // and restored before the readout.
        ui.spacing_mut().item_spacing = Vec2::splat(tokens::EMBLEM_GAP);
        widgets::emblem(ui, emblem_slot);
        ui.spacing_mut().item_spacing = Vec2::splat(tokens::GAP);
        let body_slot = match axis {
            Axis::Row => vec2(m.body_along, across),
            Axis::Column => vec2(across, m.thick),
        };
        match m.body {
            MeasuredBody::Readout(readout) => {
                widgets::readout(ui, readout, body_slot);
            }
            MeasuredBody::Hint {
                text,
                wrap,
            } => {
                widgets::hint(ui, text, wrap, body_slot);
            }
        }
        for (tone, buttons) in &m.groups {
            let skin = ButtonSkin::new(kind, *tone, p.accent);
            let group = |ui: &mut egui::Ui| {
                // Flush inside the group; the tray's gap is between
                // groups only.
                ui.spacing_mut().item_spacing = Vec2::ZERO;
                for measured in buttons {
                    let size = match axis {
                        Axis::Row => vec2(measured.2, across),
                        Axis::Column => vec2(across, m.thick),
                    };
                    button(ui, p, measured, size, skin, out, hovered);
                }
            };
            // The frame's content ui inherits the strip's layout, so the
            // buttons run along the same axis; a nested
            // `horizontal`/`vertical` here would pad the cross axis with
            // its own initial size.
            theme::group_frame(skin.base).show(ui, group);
        }
    };
    match axis {
        Axis::Row => {
            ui.horizontal(strip);
        }
        Axis::Column => {
            ui.vertical(strip);
        }
    }
}

/// The capture strip's two-row chassis.
///
/// A row: the emblem over the readout in one head column, the finishing
/// actions beside the emblem with their full labels (the accent block
/// with its grey tail — see `finishing_run`), and beside the readout the
/// hand-offs left and the ways out flush right, those six straight on the
/// chassis with no fill under them.
///
/// A column: the emblem beside the readout, then the finishing actions
/// one per line at the tray's full width, then the same six two to a
/// line with a rule across the break.
fn double_strip(
    ui: &mut egui::Ui,
    m: &DoubleMetrics,
    p: &PanelInputs,
    axis: Axis,
    col_inner: f32,
    out: &mut PanelOutcome,
    hovered: &mut Option<(egui::Id, Rect, &'static ButtonDef)>,
) {
    let label = ButtonSkin::new(Presentation::Label, GroupTone::Primary, p.accent);
    let grey = ButtonSkin::new(Presentation::Label, GroupTone::Secondary, p.accent);
    let tile = ButtonSkin::new(Presentation::Key, GroupTone::Bare, p.accent);
    let row = tokens::DOUBLE_ROW;
    let finishers_len: f32 = m
        .finishers()
        .map(|(_, _, along)| *along)
        .sum();
    match axis {
        Axis::Row => {
            let width = m.content_size(Axis::Row, col_inner).x;
            ui.vertical(|ui| {
                ui.spacing_mut().item_spacing = vec2(0.0, tokens::GAP);
                ui.horizontal(|ui| {
                    ui.spacing_mut().item_spacing = Vec2::ZERO;
                    widgets::emblem(ui, vec2(m.head, row));
                    ui.add_space(tokens::GAP);
                    if m.finishers().next().is_some() {
                        finishing_run(ui, m, p, |b| vec2(b.2, row), (label, grey), out, hovered);
                    }
                    // The way out lifted up here to even the rows: flush
                    // right, straight above the one left below it.
                    if !m.promoted.is_empty() {
                        ui.add_space(m.spacer(width, finishers_len, &m.promoted));
                        for measured in &m.promoted {
                            button(ui, p, measured, vec2(measured.2, row), tile, out, hovered);
                        }
                    }
                });
                ui.horizontal(|ui| {
                    ui.spacing_mut().item_spacing = Vec2::ZERO;
                    widgets::readout(ui, m.readout, vec2(m.head, row));
                    ui.add_space(tokens::GAP);
                    for measured in &m.funcs {
                        button(ui, p, measured, vec2(measured.2, row), tile, out, hovered);
                    }
                    ui.add_space(m.spacer(width, DoubleMetrics::run_len(&m.funcs), &m.tail));
                    for measured in &m.tail {
                        button(ui, p, measured, vec2(measured.2, row), tile, out, hovered);
                    }
                });
            });
        }
        Axis::Column => {
            let cell = col_inner / tokens::DOUBLE_COLS as f32;
            // A stacked button is as wide as the tray, so centring would
            // put every icon at its own x. Left-aligning both runs at one
            // padding lines the whole first column up — labels included,
            // since each label starts one icon and one gap along.
            let label = label.left(tokens::LABEL_PAD_H);
            let grey = grey.left(tokens::LABEL_PAD_H);
            let tile = tile.left(tokens::LABEL_PAD_H);
            let funcs: Vec<&MeasuredButton> = m.funcs.iter().collect();
            let ways_out: Vec<&MeasuredButton> = m.ways_out().collect();
            ui.vertical(|ui| {
                ui.spacing_mut().item_spacing = vec2(0.0, tokens::GAP);
                ui.horizontal(|ui| {
                    ui.spacing_mut().item_spacing = Vec2::ZERO;
                    // One grid cell each, flush: the emblem is then dead
                    // centre over the first column of tiles and the
                    // readout over the second.
                    widgets::emblem(ui, vec2(cell, row));
                    widgets::readout(ui, m.readout, vec2(cell, row));
                });
                if m.finishers().next().is_some() {
                    finishing_run(ui, m, p, |_| vec2(col_inner, row), (label, grey), out, hovered);
                }
                if !funcs.is_empty() || !ways_out.is_empty() {
                    ui.vertical(|ui| {
                        ui.spacing_mut().item_spacing = Vec2::ZERO;
                        let mut grid = |ui: &mut egui::Ui, run: &[&MeasuredButton]| {
                            for line in run.chunks(tokens::DOUBLE_COLS) {
                                ui.horizontal(|ui| {
                                    ui.spacing_mut().item_spacing = Vec2::ZERO;
                                    for measured in line {
                                        button(ui, p, measured, vec2(cell, row), tile, out, hovered);
                                    }
                                });
                            }
                        };
                        // The two clusters are chunked apart, so the rule
                        // never has to cut through a line holding one of
                        // each.
                        grid(ui, &funcs);
                        if m.has_divider() {
                            widgets::divider(ui, col_inner);
                        }
                        grid(ui, &ways_out);
                    });
                }
            });
        }
    }
}

/// Build the tray for one monitor and report what the pointer found.
/// Called inside a host's run closure, so text measurement is legal here.
///
/// `visible` is the Q toggle. A hidden tray is still laid out, still
/// hit-tested and still clicked — that is the overlay's long-standing
/// behaviour — it simply paints nothing: at opacity 0 every shape the
/// painter is handed becomes a `Shape::Noop`, while the widget rects and
/// the hover animation carry on as they are.
pub fn show(ctx: &egui::Context, p: &PanelInputs, monitor: UiMonitor, visible: bool) -> PanelOutcome {
    // Ahead of any measurement: a mark that cannot be rasterised is logged
    // here rather than drawn as egui's placeholder glyph.
    assets::preload(ctx);
    let ppp = monitor.dpi_scale.max(0.1);
    // The size readout prints the UNCLIPPED selection, so a rect straddling
    // two monitors keeps showing its true size; only placement uses the
    // clipped anchor.
    let (m, col_inner, side, rect_px) = measure_and_place(ctx, p, monitor);
    let axis = side.axis();
    let local = egui::pos2(
        (rect_px.left() - monitor.bounds.left()) as f32 / ppp,
        (rect_px.top() - monitor.bounds.top()) as f32 / ppp,
    );

    let mut out = PanelOutcome::default();
    let mut hovered: Option<(egui::Id, Rect, &'static ButtonDef)> = None;
    let area = egui::Area::new(egui::Id::new(PANEL_ID))
        .order(egui::Order::Foreground)
        .fixed_pos(local)
        // Never `constrain`: egui's symmetric clamp would override the
        // tail-wins rule `place` just applied.
        .constrain(false)
        .fade_in(false)
        .interactable(true)
        // The dead chassis swallows clicks, as the C# strips do.
        .sense(egui::Sense::CLICK);
    let inner = area.show(ctx, |ui| {
        if !visible {
            ui.set_opacity(0.0);
        }
        theme::tray_frame().show(ui, |ui| {
            ui.spacing_mut().item_spacing = Vec2::splat(tokens::GAP);
            match &m {
                Strip::Single(m) => single_strip(ui, m, p, axis, col_inner, &mut out, &mut hovered),
                Strip::Double(m) => double_strip(ui, m, p, axis, col_inner, &mut out, &mut hovered),
            }
        });
    });
    // The whole tray: emblem, readout, padding and gaps included.
    out.over_tray = inner.response.contains_pointer();
    // Only a presentation that wants a chip registers a hovered button,
    // so a `below` strip never raises one.
    show_tip(ctx, axis, inner.response.rect, hovered, visible);
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ui::command::Command;
    use crate::ui::components::panel::model::FEATURE_COMBINATIONS;
    use clowd_rust_core::geometry::ScreenRect;
    use egui::{Pos2, Rect};

    const DPIS: [f32; 4] = [1.0, 1.25, 1.5, 2.0];

    fn rect(x: i32, y: i32, w: i32, h: i32) -> ScreenRect {
        ScreenRect::from_xy_size(x, y, w, h)
    }

    fn monitor(bounds: ScreenRect, dpi: f32) -> UiMonitor {
        UiMonitor {
            bounds,
            dpi_scale: dpi,
            is_primary: true,
        }
    }

    const ACCENT: egui::Color32 = egui::Color32::from_rgb(0x2F, 0x7C, 0xAE);

    fn inputs(set: PanelButtonSet, features: PanelFeatures, style: ButtonStyle, selection: ScreenRect, monitor: UiMonitor) -> PanelInputs {
        let readout = match set {
            // The scroll-pick strip has no readout; the value is carried
            // through unused, as the shell carries it.
            PanelButtonSet::Normal | PanelButtonSet::ScrollPick => Readout::Size {
                width: selection.width(),
                height: selection.height(),
            },
            PanelButtonSet::Ocr => Readout::Words(1234),
        };
        PanelInputs {
            set,
            features,
            style,
            accent: ACCENT,
            readout,
            selection,
            anchor: crate::selection::intersect_rects(monitor.bounds, selection).expect("the selection overlaps the monitor"),
        }
    }

    /// A styled, fonted context that runs the real panel. Real Cascadia
    /// faces are mandatory: `FontDefinitions::empty()` lays out zero-width
    /// glyphs and every size assertion would be wrong.
    struct Harness {
        ctx: egui::Context,
        monitor: UiMonitor,
    }

    impl Harness {
        fn new(monitor: UiMonitor) -> Self {
            let ctx = egui::Context::default();
            egui_extras::install_image_loaders(&ctx);
            ctx.set_fonts(crate::ui::fonts::font_definitions(&[]));
            theme::apply_style(&ctx);
            Self {
                ctx,
                monitor,
            }
        }

        fn raw_input(&self, pointer: Option<Pos2>, extra: &[egui::Event]) -> egui::RawInput {
            let ppp = self.monitor.dpi_scale.max(0.1);
            let b = self.monitor.bounds;
            let mut events = Vec::with_capacity(extra.len() + 1);
            events.push(match pointer {
                Some(p) => egui::Event::PointerMoved(p),
                None => egui::Event::PointerGone,
            });
            events.extend_from_slice(extra);
            egui::RawInput {
                screen_rect: Some(Rect::from_min_size(
                    Pos2::ZERO,
                    egui::vec2(b.width() as f32, b.height() as f32) / ppp,
                )),
                viewports: [(
                    egui::ViewportId::ROOT,
                    egui::ViewportInfo {
                        native_pixels_per_point: Some(ppp),
                        ..Default::default()
                    },
                )]
                .into_iter()
                .collect(),
                events,
                focused: true,
                ..Default::default()
            }
        }

        /// A fixed three passes — enough for the Area's rects to settle,
        /// and for a press to land on the second the way the host feeds
        /// it — with every `FullOutput` dropped without applying its
        /// deltas (epaint debug-asserts a non-empty `TexturesDelta` on
        /// drop). Unlike `MonitorHost::run` this reports the pointer on
        /// every pass: the host gates that on movement so it can tell
        /// when it is owed another run, and a fixed-length loop is not.
        fn run(&mut self, p: &PanelInputs, pointer: Option<Pos2>) -> PanelOutcome {
            self.tick(p, pointer, false)
        }

        /// One tick with the press-and-release pair fed from the second
        /// pass on, exactly as the host does it, and `clicked` ORed over
        /// the passes.
        fn press(&mut self, p: &PanelInputs, pointer: Pos2) -> PanelOutcome {
            self.tick(p, Some(pointer), true)
        }

        fn tick(&mut self, p: &PanelInputs, pointer: Option<Pos2>, press: bool) -> PanelOutcome {
            let press_events: Vec<egui::Event> = match pointer.filter(|_| press) {
                Some(pos) => [true, false]
                    .into_iter()
                    .map(|pressed| egui::Event::PointerButton {
                        pos,
                        button: egui::PointerButton::Primary,
                        pressed,
                        modifiers: egui::Modifiers::NONE,
                    })
                    .collect(),
                None => Vec::new(),
            };
            let mut acc = PanelOutcome::default();
            for pass in 0..3 {
                let extra: &[egui::Event] = if press && pass == 1 { &press_events } else { &[] };
                let raw = self.raw_input(pointer, extra);
                let monitor = self.monitor;
                let mut out = PanelOutcome::default();
                let full = self.ctx.run_ui(raw, |ui| {
                    out = show(ui.ctx(), p, monitor, true);
                });
                full.drop_without_applying_deltas();
                acc = PanelOutcome {
                    clicked: acc.clicked.or(out.clicked),
                    over_button: out.over_button,
                    over_tray: out.over_tray,
                };
            }
            acc
        }

        fn area_rect(&self) -> Rect {
            self.ctx
                .memory(|m| m.area_rect(egui::Id::new(PANEL_ID)))
                .expect("the tray has an area rect after a run")
        }

        fn interactive_rects(&self) -> Vec<Rect> {
            self.ctx.interactive_rects_last_pass()
        }

        /// The analytic placement, computed inside a pass so text
        /// measurement is legal. Call after at least one `run`.
        fn analytic(&mut self, p: &PanelInputs) -> (place::Side, ScreenRect) {
            let monitor = self.monitor;
            let mut got = None;
            let raw = self.raw_input(None, &[]);
            let full = self.ctx.run_ui(raw, |ui| {
                let (_, _, side, rect) = measure_and_place(ui.ctx(), p, monitor);
                got = Some((side, rect));
            });
            full.drop_without_applying_deltas();
            got.expect("the closure ran")
        }
    }

    /// A switch set whose first row is comfortably the longer of the two,
    /// so the ways out stay together on the second row.
    const LONG_FIRST_ROW: PanelFeatures = PanelFeatures {
        share: false,
        scroll_capture: false,
        ocr: false,
        image_search: false,
        ..PanelFeatures::ALL
    };

    /// A switch set whose first row is comfortably the shorter, so RESET
    /// is lifted up beside the accent run.
    const SHORT_FIRST_ROW: PanelFeatures = PanelFeatures {
        video: false,
        ..PanelFeatures::ALL
    };

    const HD: (i32, i32) = (1920, 1080);

    fn hd(dpi: f32) -> UiMonitor {
        monitor(rect(0, 0, HD.0, HD.1), dpi)
    }

    /// A selection comfortably inset, so the row goes beneath it.
    fn row_selection() -> ScreenRect {
        rect(500, 300, 600, 400)
    }

    #[test]
    fn button_count_matches_visible_defs() {
        for &style in &[ButtonStyle::KeyHint, ButtonStyle::Below] {
            for &set in PanelButtonSet::ALL {
                for features in FEATURE_COMBINATIONS {
                    let mon = hd(1.0);
                    let mut h = Harness::new(mon);
                    let p = inputs(set, features, style, row_selection(), mon);
                    h.run(&p, None);
                    let expected = set.visible_defs(features).count();
                    // One extra: the tray body itself senses clicks.
                    assert_eq!(h.interactive_rects().len(), expected + 1, "{set:?} {style:?} {features:?}");
                }
            }
        }
    }

    /// The OCR strip's readout is the word count over a "words" caption,
    /// and the caption is what sets the slot's width, so the count's
    /// digits never change the strip.
    #[test]
    fn ocr_readout_shows_words_and_the_caption_sets_the_width() {
        let job = widgets::readout_job(Readout::Words(1234));
        assert_eq!(job.text, "1234\nwords");
        let size = widgets::readout_job(Readout::Size {
            width: 600,
            height: 400,
        });
        assert_eq!(size.text, "600\n\u{00D7}\n400");

        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let mut widths = Vec::new();
        for n in [0, 7, 1234, 99_999] {
            let p = PanelInputs {
                readout: Readout::Words(n),
                ..inputs(PanelButtonSet::Ocr, PanelFeatures::ALL, ButtonStyle::KeyHint, row_selection(), mon)
            };
            h.run(&p, None);
            widths.push(h.area_rect().width());
        }
        assert!(
            widths
                .iter()
                .all(|w| (w - widths[0]).abs() <= 0.01),
            "{widths:?}"
        );
    }

    /// The chip hangs off the strip's far edge, centred on the button,
    /// and flips to the near edge when the far one is off the monitor.
    #[test]
    fn tip_rect_hangs_off_the_far_edge_and_flips_when_out_of_room() {
        let screen = Rect::from_min_size(Pos2::ZERO, vec2(1920.0, 1080.0));
        let chip = vec2(50.0, 20.0);
        let tray = Rect::from_min_size(pos2(600.0, 700.0), vec2(500.0, 48.0));
        let button = Rect::from_min_size(pos2(700.0, 704.0), vec2(40.0, 40.0));
        let below = tip_rect(Axis::Row, tray, button, chip, screen);
        assert_eq!(below.top(), tray.bottom() + tokens::TIP_GAP_BOTTOM);
        assert_eq!(below.center().x, button.center().x);

        let low_tray = Rect::from_min_size(pos2(600.0, 1030.0), vec2(500.0, 48.0));
        let low_button = Rect::from_min_size(pos2(700.0, 1034.0), vec2(40.0, 40.0));
        let above = tip_rect(Axis::Row, low_tray, low_button, chip, screen);
        assert_eq!(above.bottom(), low_tray.top() - tokens::TIP_GAP_BOTTOM);

        let col = Rect::from_min_size(pos2(1000.0, 100.0), vec2(48.0, 500.0));
        let col_button = Rect::from_min_size(pos2(1004.0, 200.0), vec2(40.0, 40.0));
        let right = tip_rect(Axis::Column, col, col_button, chip, screen);
        assert_eq!(right.left(), col.right() + tokens::TIP_GAP_RIGHT);
        assert_eq!(right.center().y, col_button.center().y);

        let edge_col = Rect::from_min_size(pos2(1870.0, 100.0), vec2(48.0, 500.0));
        let edge_button = Rect::from_min_size(pos2(1874.0, 200.0), vec2(40.0, 40.0));
        let left = tip_rect(Axis::Column, edge_col, edge_button, chip, screen);
        assert_eq!(left.right(), edge_col.left() - tokens::TIP_GAP_RIGHT);

        // Slid back onto the monitor along the strip.
        let corner_button = Rect::from_min_size(pos2(1900.0, 704.0), vec2(40.0, 40.0));
        let slid = tip_rect(Axis::Row, tray, corner_button, chip, screen);
        assert!(slid.right() <= screen.right(), "{slid:?}");
    }

    /// The tooltip is a `key`-style affair only, and it waits: a pointer
    /// resting on a button for a tick shows nothing, one resting for the
    /// delay shows the chip, and moving off the button removes it at
    /// once. Once a chip has been up, the next button gets its chip
    /// straight away — even after a short hop across no button — until
    /// the pointer has been off the buttons for the whole delay. The
    /// harness feeds no clock, so egui steps time by its predicted frame
    /// (1/60 s) per pass.
    #[test]
    fn key_hint_tooltip_appears_after_the_delay_and_names_the_button() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        let first = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.width() <= tokens::KEY_TILE + 0.5)
            .min_by(|a, b| a.left().total_cmp(&b.left()))
            .expect("a button rect");
        let tip_layer = egui::LayerId::new(egui::Order::Tooltip, egui::Id::new(TIP_ID));
        let tip_shown = |h: &Harness| {
            h.ctx
                .memory(|m| m.areas().visible_last_frame(&tip_layer))
        };

        h.run(&p, Some(first.center()));
        assert!(!tip_shown(&h), "the chip must wait out the delay");
        let passes = (tokens::TIP_DELAY_SECS * 60.0).ceil() as usize + 3;
        for _ in 0..passes / 3 {
            h.run(&p, Some(first.center()));
        }
        assert!(tip_shown(&h), "the chip is up after {passes} passes");
        let tip = h
            .ctx
            .memory(|m| m.area_rect(egui::Id::new(TIP_ID)))
            .expect("the chip has a rect");
        let tray = h.area_rect();
        assert!(
            (tip.top() - (tray.bottom() + tokens::TIP_GAP_BOTTOM)).abs() < 0.01,
            "{tip:?} vs {tray:?}"
        );
        // Centred to the pixel the Area was pinned on.
        assert!((tip.center().x - first.center().x).abs() <= 0.5, "{tip:?} vs {first:?}");

        h.run(&p, None);
        assert!(!tip_shown(&h), "the chip goes with the pointer");

        // Within the grace period, the next button shows at once — one
        // tick off every button (a gap between groups) does not reset it.
        let second = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.width() <= tokens::KEY_TILE + 0.5 && r.left() > first.left() + 1.0)
            .min_by(|a, b| a.left().total_cmp(&b.left()))
            .expect("a second button rect");
        h.run(&p, Some(second.center()));
        assert!(tip_shown(&h), "the chip follows the pointer within the grace period");
        let tip = h
            .ctx
            .memory(|m| m.area_rect(egui::Id::new(TIP_ID)))
            .expect("the chip has a rect");
        assert!((tip.center().x - second.center().x).abs() <= 0.5, "{tip:?} vs {second:?}");

        // Off every button for the whole delay: the wait is back.
        for _ in 0..passes / 3 {
            h.run(&p, None);
        }
        h.run(&p, Some(first.center()));
        assert!(!tip_shown(&h), "a long pause brings the wait back");

        // `below` has labels and never shows a chip.
        let below = inputs(PanelButtonSet::Ocr, PanelFeatures::ALL, ButtonStyle::Below, row_selection(), mon);
        let mut h = Harness::new(mon);
        h.run(&below, None);
        let first = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::BELOW_HEIGHT + 0.5 && r.width() < 200.0)
            .min_by(|a, b| a.left().total_cmp(&b.left()))
            .expect("a button rect");
        for _ in 0..passes / 3 {
            h.run(&below, Some(first.center()));
        }
        assert!(!tip_shown(&h));
    }

    #[test]
    fn key_hint_letters_match_accelerators() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        for set in PanelButtonSet::ALL {
            for def in set.defs() {
                let button = widgets::key_hint_button(def, egui::Id::new(def.label), Vec2::ZERO, ACCENT, tokens::HOVER_VEIL_PRIMARY);
                assert_eq!(
                    button.text.text(),
                    def.accel_key()
                        .to_ascii_uppercase()
                        .to_string(),
                    "{}",
                    def.label
                );
            }
        }
    }

    #[test]
    fn below_labels_underline_the_accelerator_byte() {
        for set in PanelButtonSet::ALL {
            for def in set.defs() {
                let job = widgets::underlined_label(def);
                assert_eq!(job.text, def.label);
                assert_eq!(job.sections.len(), 3, "{}", def.label);
                let accel = &job.sections[1];
                assert_eq!(
                    (accel.byte_range.start.0, accel.byte_range.end.0),
                    (def.underline_idx, def.underline_idx + 1),
                    "{}",
                    def.label
                );
                assert!(accel.format.underline.width > 0.0, "{}", def.label);
            }
        }
    }

    /// The single-strip sets keep the one-row chassis the `key` and
    /// `below` styles size: 40 + 2 x 4 and 48 + 2 x 4.
    #[test]
    fn frame_is_48_tall_in_key_style_and_56_in_below_at_100_percent() {
        for (style, height) in [(ButtonStyle::KeyHint, 48.0), (ButtonStyle::Below, 56.0)] {
            let mon = hd(1.0);
            let mut h = Harness::new(mon);
            let p = inputs(PanelButtonSet::Ocr, PanelFeatures::ALL, style, row_selection(), mon);
            h.run(&p, None);
            assert!((h.area_rect().height() - height).abs() <= 0.5, "{style:?}: {:?}", h.area_rect());
        }
    }

    /// The capture strip is the double chassis: two tile rows, one gap
    /// between them and the tray's padding round the outside — whichever
    /// `--panel-buttons` style the run was given, since it draws both
    /// presentations itself.
    #[test]
    fn the_capture_strip_is_two_rows_tall_in_either_style() {
        let want = 2.0 * tokens::DOUBLE_ROW + tokens::GAP + 2.0 * tokens::PAD;
        for &style in &[ButtonStyle::KeyHint, ButtonStyle::Below] {
            let mon = hd(1.0);
            let mut h = Harness::new(mon);
            let p = inputs(PanelButtonSet::Normal, PanelFeatures::ALL, style, row_selection(), mon);
            h.run(&p, None);
            assert!((h.area_rect().height() - want).abs() <= 0.5, "{style:?}: {:?}", h.area_rect());
        }
    }

    /// The design, as a row: the emblem over the readout in one head
    /// column, the five accent actions on the first row with their labels
    /// on, and on the second row the four hand-offs left and the two ways
    /// out flush with the tray's right padding.
    #[test]
    fn capture_row_puts_labels_up_top_and_the_ways_out_bottom_right() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        let area = h.area_rect();
        // Before `analytic`: its pass lays out no widgets, so it would
        // leave `interactive_rects_last_pass` empty.
        let mut rects: Vec<Rect> = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5)
            .collect();
        rects.sort_by(|a, b| {
            a.top()
                .total_cmp(&b.top())
                .then(a.left().total_cmp(&b.left()))
        });
        assert_eq!(h.analytic(&p).0.axis(), Axis::Row);
        let (top, bottom): (Vec<Rect>, Vec<Rect>) = rects
            .iter()
            .partition(|r| r.top() < area.center().y);
        assert_eq!(top.len(), 4, "the accent row holds the four finishing actions");
        assert_eq!(bottom.len(), 7, "the second row holds the five hand-offs and the two ways out");

        // Every accent button is wider than a bare tile: it carries a
        // label, not a letter. Every second-row button is a tile.
        assert!(
            top.iter()
                .all(|r| r.width() > tokens::KEY_TILE + 0.5),
            "{top:?}"
        );
        assert!(
            bottom
                .iter()
                .all(|r| (r.width() - tokens::KEY_TILE).abs() <= 0.5),
            "{bottom:?}"
        );

        // The head column: both rows start past the emblem, at the same x,
        // one whole button's room (a tile and a gap) in from the padding.
        assert!((top[0].left() - bottom[0].left()).abs() <= 0.5, "{:?} {:?}", top[0], bottom[0]);
        let head = area.left() + tokens::PAD + tokens::EMBLEM_SLOT + tokens::GAP;
        assert!((top[0].left() - head).abs() <= 0.5, "{:?} in {area:?}", top[0]);
        // The hand-offs are flush, then a hole, then the two ways out end
        // on the tray's right padding.
        let ways_out = &bottom[5..];
        assert!(
            (ways_out[1].right() - (area.right() - tokens::PAD)).abs() <= 0.5,
            "{:?} in {area:?}",
            ways_out[1]
        );
        assert!(
            ways_out[0].left() - bottom[4].right() > tokens::GAP,
            "the ways out are pushed away from the hand-offs: {bottom:?}"
        );
        // UPLOAD leads the hand-offs, as a tile.
        let upload = bottom[0];
        assert!((upload.width() - tokens::KEY_TILE).abs() <= 0.5, "{upload:?}");
        let out = h.press(&p, upload.center());
        assert_eq!(out.clicked, Some(Command::Upload), "{out:?}");
    }

    /// The design, as a column: the emblem beside the readout, the accent
    /// actions one per line at the tray's full width, then the rest two to
    /// a line.
    #[test]
    fn capture_column_stacks_labels_then_a_two_column_grid() {
        let mon = monitor(rect(0, 0, 1920, 1300), 1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            rect(100, 300, 400, 998),
            mon,
        );
        h.run(&p, None);
        let area = h.area_rect();
        let inner = area.width() - 2.0 * tokens::PAD;
        // Before `analytic`, which lays out no widgets of its own.
        let mut rects: Vec<Rect> = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5)
            .collect();
        rects.sort_by(|a, b| {
            a.top()
                .total_cmp(&b.top())
                .then(a.left().total_cmp(&b.left()))
        });
        assert_eq!(h.analytic(&p).0.axis(), Axis::Column);
        assert_eq!(rects.len(), 11);
        let (primary, secondary) = rects.split_at(4);
        for r in primary {
            assert!((r.width() - inner).abs() <= 0.5, "a primary button spans the tray: {r:?}");
        }
        let cell = inner / tokens::DOUBLE_COLS as f32;
        for r in secondary {
            assert!((r.width() - cell).abs() <= 0.5, "a secondary button is half the tray: {r:?}");
        }
        // Laid out two to a line, the two clusters chunked apart: the five
        // hand-offs make 2 + 2 + 1, the two ways out make one more line.
        let lines: Vec<&[Rect]> = {
            let (funcs, ways) = secondary.split_at(5);
            funcs
                .chunks(2)
                .chain(ways.chunks(2))
                .collect()
        };
        assert_eq!(lines.len(), 4);
        for line in &lines {
            assert!((line[0].left() - (area.left() + tokens::PAD)).abs() <= 0.5, "{line:?}");
            if let [left, right] = line {
                assert!((left.top() - right.top()).abs() <= 0.5, "{line:?}");
                assert!((right.left() - left.right()).abs() <= 0.5, "{line:?}");
            }
        }
    }

    /// The emblem and the readout each take one grid cell in a column, so
    /// the emblem is centred over the first column of tiles and the
    /// readout over the second. A cell narrower than either of them would
    /// squeeze it off centre.
    #[test]
    fn capture_column_heads_are_one_cell_each() {
        let mon = monitor(rect(0, 0, 1920, 1300), 1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            rect(100, 300, 400, 998),
            mon,
        );
        h.run(&p, None);
        let area = h.area_rect();
        let mut rects: Vec<Rect> = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5)
            .collect();
        rects.sort_by(|a, b| {
            a.top()
                .total_cmp(&b.top())
                .then(a.left().total_cmp(&b.left()))
        });
        let cell = (area.width() - 2.0 * tokens::PAD) / tokens::DOUBLE_COLS as f32;

        // The measured head box is a cell, and a cell holds it whole.
        let raw = h.raw_input(None, &[]);
        let mut head = 0.0;
        let full = h.ctx.run_ui(raw, |ui| {
            head = DoubleMetrics::measure(ui.ctx(), PanelFeatures::ALL, p.readout).head;
        });
        full.drop_without_applying_deltas();
        assert!(cell + 0.01 >= head, "a {cell} pt cell cannot hold a {head} pt head box");

        // Which puts the emblem's centre on the first tile column's, since
        // both are the middle of the same cell.
        let first_tile = rects[4];
        let emblem_centre = area.left() + tokens::PAD + cell / 2.0;
        assert!(
            (first_tile.center().x - emblem_centre).abs() <= 0.5,
            "{first_tile:?} is not under the emblem at {emblem_centre} in {area:?}"
        );
    }

    /// A column stacks the ways out flush against the hand-offs, so it
    /// draws a rule between them — the separation a row gets for free from
    /// pushing its ways out to the far end of their row. The rule's block
    /// is the only break in an otherwise flush grid, and the line inside
    /// it stops short of either edge.
    #[test]
    fn capture_column_rules_off_the_ways_out() {
        let mon = monitor(rect(0, 0, 1920, 1300), 1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            rect(100, 300, 400, 998),
            mon,
        );
        h.run(&p, None);
        let mut rects: Vec<Rect> = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5)
            .collect();
        rects.sort_by(|a, b| {
            a.top()
                .total_cmp(&b.top())
                .then(a.left().total_cmp(&b.left()))
        });
        // Four labels, then [Upload Share] [Scroll OCR] [Search], the rule,
        // then [Reset Exit].
        let grid = &rects[4..];
        assert_eq!(grid.len(), 7);
        // Flush inside the hand-offs...
        assert!((grid[2].top() - grid[1].bottom()).abs() <= 0.5, "{grid:?}");
        assert!((grid[4].top() - grid[3].bottom()).abs() <= 0.5, "{grid:?}");
        // ...and one rule block between the clusters.
        let across = grid[5].top() - grid[4].bottom();
        assert!(
            (across - tokens::DIVIDER_BLOCK).abs() <= 0.5,
            "{across} between the clusters, want {}",
            tokens::DIVIDER_BLOCK
        );
        // The line is inset, not a border across the whole tray, and it
        // carries air on either side of it.
        const {
            assert!(tokens::DIVIDER_INSET > 0.0);
            assert!(tokens::DIVIDER_BLOCK > tokens::DIVIDER.width);
        }
    }

    /// Every button says what it does after the hover delay — the
    /// labelled ones included. "Upload" is one verb; "Upload to default
    /// destination" is the sentence, and losing it was a regression.
    #[test]
    fn labelled_buttons_still_raise_their_tooltip() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        let area = h.area_rect();
        // A first-row button: taller than nothing, wider than a tile.
        let label = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5 && r.width() > tokens::KEY_TILE + 0.5 && r.top() < area.center().y)
            .min_by(|a, b| a.left().total_cmp(&b.left()))
            .expect("a labelled button rect");
        let tip_layer = egui::LayerId::new(egui::Order::Tooltip, egui::Id::new(TIP_ID));
        let tip_shown = |h: &Harness| {
            h.ctx
                .memory(|m| m.areas().visible_last_frame(&tip_layer))
        };
        h.run(&p, Some(label.center()));
        assert!(!tip_shown(&h), "the chip waits out the delay here too");
        let passes = (tokens::TIP_DELAY_SECS * 60.0).ceil() as usize + 3;
        for _ in 0..passes / 3 {
            h.run(&p, Some(label.center()));
        }
        assert!(tip_shown(&h), "a labelled button raises its chip");
    }

    /// The column grid is left-aligned at the labelled buttons' padding so
    /// every icon in a column sits at one x. That only works if a cell is
    /// wide enough for a tile carrying that padding — otherwise the
    /// accelerator letter spills out of the button and the grid stops
    /// being two even columns.
    #[test]
    fn capture_column_cells_hold_a_left_padded_tile() {
        let mon = monitor(rect(0, 0, 1920, 1300), 1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            rect(100, 300, 400, 998),
            mon,
        );
        h.run(&p, None);
        let cell = (h.area_rect().width() - 2.0 * tokens::PAD) / tokens::DOUBLE_COLS as f32;
        let raw = h.raw_input(None, &[]);
        let mut want = 0.0;
        let full = h.ctx.run_ui(raw, |ui| {
            want = widgets::key_button_length(ui.ctx(), tokens::LABEL_PAD_H);
        });
        full.drop_without_applying_deltas();
        assert!(cell + 0.01 >= want, "a {cell} pt cell cannot hold a {want} pt tile");
    }

    /// With VIDEO off the accent row has only EDIT, COPY and SAVE on it
    /// and comes up shorter than the row beneath, leaving a bite out of
    /// the tray's top right corner. RESET moves up into it, flush right
    /// and straight above EXIT, and the two rows end together.
    #[test]
    fn a_short_accent_row_lifts_the_first_way_out_up_beside_it() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(PanelButtonSet::Normal, SHORT_FIRST_ROW, ButtonStyle::KeyHint, row_selection(), mon);
        h.run(&p, None);
        let area = h.area_rect();
        let mut rects: Vec<Rect> = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5)
            .collect();
        rects.sort_by(|a, b| {
            a.top()
                .total_cmp(&b.top())
                .then(a.left().total_cmp(&b.left()))
        });
        let (top, bottom): (Vec<Rect>, Vec<Rect>) = rects
            .iter()
            .partition(|r| r.top() < area.center().y);
        // Edit, Copy, Save + the lifted Reset; the five hand-offs + Exit.
        assert_eq!((top.len(), bottom.len()), (4, 6), "{top:?} / {bottom:?}");
        let lifted = top.last().expect("the lifted tile");
        let exit = bottom.last().expect("Exit");
        assert!(
            (lifted.width() - tokens::KEY_TILE).abs() <= 0.5,
            "the lifted button is a tile, not a label: {lifted:?}"
        );
        // Both rows end on the tray's right padding, and the two ways out
        // stack.
        let right = area.right() - tokens::PAD;
        assert!((lifted.right() - right).abs() <= 0.5, "{lifted:?} in {area:?}");
        assert!((exit.right() - right).abs() <= 0.5, "{exit:?} in {area:?}");
        assert!((lifted.left() - exit.left()).abs() <= 0.5, "{lifted:?} above {exit:?}");
        // And it is really RESET that moved, not EXIT.
        let out = h.press(&p, lifted.center());
        assert_eq!(out.clicked, Some(Command::Reset), "{out:?}");
    }

    /// The lift is for a ragged row only. With the hand-offs switched off
    /// the accent row is comfortably the longer of the two, so both ways
    /// out stay together on the second row — and a column never lifts at
    /// all, since its grid lays the same buttons out in table order
    /// whatever a row decided.
    #[test]
    fn a_long_accent_row_keeps_both_ways_out_below_it() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(PanelButtonSet::Normal, LONG_FIRST_ROW, ButtonStyle::KeyHint, row_selection(), mon);
        h.run(&p, None);
        let area = h.area_rect();
        let below = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5 && r.top() >= area.center().y)
            .count();
        assert_eq!(below, 3, "UPLOAD and both ways out");

        // The same switches that lift RESET in a row change nothing in a
        // column.
        let tall = monitor(rect(0, 0, 1920, 1300), 1.0);
        let mut h = Harness::new(tall);
        let p = inputs(
            PanelButtonSet::Normal,
            SHORT_FIRST_ROW,
            ButtonStyle::KeyHint,
            rect(100, 300, 400, 998),
            tall,
        );
        h.run(&p, None);
        let rows = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5)
            .count();
        assert_eq!(h.analytic(&p).0.axis(), Axis::Column);
        assert_eq!(rows, 10, "three labels and the seven-button grid, ungrouped");
    }

    /// Switching every optional button off may not leave a hole where a
    /// cluster was: the ways out stay flush right, and the column grid
    /// closes up.
    #[test]
    fn the_capture_strip_survives_every_switch_combination() {
        for features in FEATURE_COMBINATIONS {
            let mon = hd(1.0);
            let mut h = Harness::new(mon);
            let p = inputs(PanelButtonSet::Normal, features, ButtonStyle::KeyHint, row_selection(), mon);
            h.run(&p, None);
            let area = h.area_rect();
            // The longer row ends on the tray's right padding, whichever
            // it is, and nothing ever sticks out past it.
            let edge = area.right() - tokens::PAD;
            let rights: Vec<f32> = h
                .interactive_rects()
                .into_iter()
                .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5)
                .map(|r| r.right())
                .collect();
            let right = rights
                .iter()
                .copied()
                .fold(f32::MIN, f32::max);
            assert!((right - edge).abs() <= 0.5, "{features:?}: {right} in {area:?}");
        }
    }

    #[test]
    fn area_rect_equals_the_analytic_strip() {
        // A row anchor and a column anchor: a selection hugging the bottom
        // of a tall monitor forces the column for either set.
        let cases: [(UiMonitor, ScreenRect); 2] = [
            (hd(1.0), row_selection()),
            (monitor(rect(0, 0, 1920, 1300), 1.0), rect(100, 300, 400, 998)),
        ];
        for &style in &[ButtonStyle::KeyHint, ButtonStyle::Below] {
            for &set in PanelButtonSet::ALL {
                for (mon, sel) in cases {
                    for dpi in DPIS {
                        let mon = monitor(mon.bounds, dpi);
                        let mut h = Harness::new(mon);
                        let p = inputs(set, PanelFeatures::ALL, style, sel, mon);
                        h.run(&p, None);
                        let (_, expected) = h.analytic(&p);
                        let area = h.area_rect();
                        let tol = 1.0 / dpi + 1e-3;
                        assert!(
                            (area.width() - expected.width() as f32 / dpi).abs() <= tol
                                && (area.height() - expected.height() as f32 / dpi).abs() <= tol,
                            "{set:?} {style:?} at {dpi}: {area:?} vs {expected:?}"
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn side_follows_the_placement_rules() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            rect(500, 300, 100, 100),
            mon,
        );
        h.run(&p, None);
        assert_eq!(h.analytic(&p).0.axis(), Axis::Row);

        let low = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            rect(500, 1000, 100, 60),
            mon,
        );
        h.run(&low, None);
        assert_eq!(h.analytic(&low).0.axis(), Axis::Column);
    }

    #[test]
    fn swap_recentres_with_the_new_width() {
        for dpi in DPIS {
            let mon = hd(dpi);
            let centres: Vec<f32> = PanelButtonSet::ALL
                .iter()
                .map(|&set| {
                    let mut h = Harness::new(mon);
                    let p = inputs(set, PanelFeatures::ALL, ButtonStyle::KeyHint, row_selection(), mon);
                    h.run(&p, None);
                    h.area_rect().center().x
                })
                .collect();
            assert!((centres[0] - centres[1]).abs() <= 1.0 / dpi, "at {dpi}: {centres:?}");
        }
    }

    /// Pinned over the union sets only: the scroll-picker is not one of
    /// them — it is locked to a row, so it has no column width to agree
    /// about, and its instruction would widen every other set's column if
    /// it were folded in.
    /// The instruction is wrapped by the strip, not by hand: two lines,
    /// near enough the same length that the block reads as a paragraph
    /// rather than a long line with a word hanging off it.
    #[test]
    fn the_scroll_pick_instruction_wraps_into_two_balanced_lines() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::ScrollPick,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);

        let raw = h.raw_input(None, &[]);
        let mut rows: Vec<f32> = Vec::new();
        let full = h.ctx.run_ui(raw, |ui| {
            let ctx = ui.ctx();
            let (wrap, lines) = hint_wrap(ctx, crate::ui::components::panel::model::SCROLL_PICK_HINT);
            assert_eq!(lines, 2, "wrapped at {wrap}");
            let galley = ctx.fonts_mut(|f| f.layout_job(widgets::hint_job(crate::ui::components::panel::model::SCROLL_PICK_HINT, wrap)));
            rows = galley
                .rows
                .iter()
                .map(|r| r.rect().width())
                .collect();
        });
        full.drop_without_applying_deltas();

        assert_eq!(rows.len(), 2);
        let (short, long) = (rows[0].min(rows[1]), rows[0].max(rows[1]));
        assert!(short / long >= 0.75, "lines are lopsided: {rows:?}");
    }

    /// The scroll-pick strip is a row wherever it lands: the anchor here
    /// is the one every other set answers with a column.
    #[test]
    fn the_scroll_pick_strip_is_always_a_row() {
        let mon = monitor(rect(0, 0, 1920, 1300), 1.0);
        let sel = rect(100, 300, 400, 998);
        for &set in PanelButtonSet::ALL {
            let mut h = Harness::new(mon);
            let p = inputs(set, PanelFeatures::ALL, ButtonStyle::Below, sel, mon);
            h.run(&p, None);
            let want = if set == PanelButtonSet::ScrollPick {
                Axis::Row
            } else {
                Axis::Column
            };
            assert_eq!(h.analytic(&p).0.axis(), want, "{set:?}");
        }
    }

    /// A set's column width is its own — the capture strip's grid is two
    /// tiles wide where the OCR strip is one — but it may never move when
    /// a switch is flipped, or a user turning UPLOAD off would find the
    /// tray under a different part of the selection.
    #[test]
    fn column_width_is_fixed_per_set_under_every_switch_combination() {
        let mon = monitor(rect(0, 0, 1920, 1300), 1.0);
        let sel = rect(100, 300, 400, 998);
        for &set in PanelButtonSet::UNION {
            let mut widths = Vec::new();
            for features in FEATURE_COMBINATIONS {
                let mut h = Harness::new(mon);
                let p = inputs(set, features, ButtonStyle::Below, sel, mon);
                h.run(&p, None);
                assert_eq!(h.analytic(&p).0.axis(), Axis::Column, "{set:?} {features:?}");
                widths.push(h.area_rect().width());
            }
            let first = widths[0];
            for w in &widths {
                assert!((w - first).abs() <= 0.01, "{set:?}: {widths:?}");
            }
        }
    }

    /// `double_strip` reads `NORMAL_GROUPS` by tone: one accent run, an
    /// optional grey run attached to it, then the bare runs — every one
    /// but the last a hand-off, the last the ways out. The table has to
    /// end on a bare run, or the ways out would be drawn as hand-offs.
    #[test]
    fn capture_groups_end_on_the_bare_run_that_holds_the_ways_out() {
        let groups = PanelButtonSet::Normal.groups();
        let tones: Vec<GroupTone> = groups.iter().map(|g| g.tone).collect();
        assert_eq!(tones, vec![GroupTone::Primary, GroupTone::Bare, GroupTone::Bare]);
        assert_eq!(
            tones
                .iter()
                .filter(|t| **t == GroupTone::Primary)
                .count(),
            1
        );
        assert!(
            tones
                .iter()
                .filter(|t| **t == GroupTone::Secondary)
                .count()
                <= 1
        );
        assert_eq!(tones.last(), Some(&GroupTone::Bare));

        // And the last run really is the ways out.
        let last = groups.last().expect("a last group");
        let ways_out: Vec<Command> = PanelButtonSet::Normal.defs()[PanelButtonSet::Normal.defs().len() - last.len..]
            .iter()
            .map(|d| d.command)
            .collect();
        assert_eq!(ways_out, vec![Command::Reset, Command::Exit]);
    }

    /// The finishing run is one block: its buttons are flush on a single
    /// pill, with no gap anywhere along it. (A grey run attached to the
    /// accent one, when the table has one, is part of that same block —
    /// `finishing_run` lays the accent pill over the grey.)
    #[test]
    fn the_finishing_run_is_one_flush_block() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(PanelButtonSet::Normal, LONG_FIRST_ROW, ButtonStyle::KeyHint, row_selection(), mon);
        h.run(&p, None);
        let area = h.area_rect();
        let mut top: Vec<Rect> = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5 && r.top() < area.center().y)
            .collect();
        top.sort_by(|a, b| a.left().total_cmp(&b.left()));
        assert_eq!(top.len(), 4, "Edit Video Copy Save: {top:?}");
        for pair in top.windows(2) {
            assert!(
                (pair[1].left() - pair[0].right()).abs() <= 0.5,
                "a gap opened in the finishing run: {pair:?}"
            );
        }
        // Every one of them is a labelled button, not a tile.
        assert!(
            top.iter()
                .all(|r| r.width() > tokens::KEY_TILE + 0.5),
            "{top:?}"
        );
        let out = h.press(&p, top[2].center());
        assert_eq!(out.clicked, Some(Command::Copy), "{out:?}");
    }

    #[test]
    fn dead_tray_swallows_and_reports_over_tray() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        let area = h.area_rect();
        let emblem = egui::pos2(area.left() + tokens::PAD + tokens::EMBLEM_SLOT / 2.0, area.center().y);
        let out = h.press(&p, emblem);
        assert!(out.clicked.is_none() && out.over_tray && !out.over_button, "{out:?}");

        let outside = egui::pos2(area.right() + 1.0, area.center().y);
        let out = h.run(&p, Some(outside));
        assert!(!out.over_tray && !out.over_button, "{out:?}");
    }

    /// Decision 3: the overlay fires on the OS press, so the press tick
    /// must answer with the command in the same call that laid the strip
    /// out — no second event, no frame of latency.
    #[test]
    fn press_on_a_button_reports_its_command_synchronously() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        // The first visible def is the accent row's leftmost button.
        let first = h
            .interactive_rects()
            .into_iter()
            .filter(|r| r.height() <= tokens::DOUBLE_ROW + 0.5)
            .min_by(|a, b| {
                a.top()
                    .total_cmp(&b.top())
                    .then(a.left().total_cmp(&b.left()))
            })
            .expect("a button rect");
        let expected = PanelButtonSet::Normal
            .visible_defs(PanelFeatures::ALL)
            .next()
            .expect("a visible button")
            .1
            .command;
        let out = h.press(&p, first.center());
        assert_eq!(out.clicked, Some(expected), "{out:?}");
        assert!(out.over_button && out.over_tray, "{out:?}");
    }

    #[test]
    fn pointer_gone_clears_hover() {
        let mon = hd(1.0);
        let mut h = Harness::new(mon);
        let p = inputs(
            PanelButtonSet::Normal,
            PanelFeatures::ALL,
            ButtonStyle::KeyHint,
            row_selection(),
            mon,
        );
        h.run(&p, None);
        let first = h
            .interactive_rects()
            .into_iter()
            .find(|r| r.width() <= tokens::KEY_TILE + 0.5)
            .expect("a button rect");
        let over = h.run(&p, Some(first.center()));
        assert!(over.over_button && over.over_tray, "{over:?}");
        let gone = h.run(&p, None);
        assert!(!gone.over_button && !gone.over_tray, "{gone:?}");
    }
}
