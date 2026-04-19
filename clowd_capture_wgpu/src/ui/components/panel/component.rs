//! Button panel as a `Component` implementation.
//!
//! Owns its own layout decision: given an `AppContext`, it picks the
//! monitor whose center contains the selection, computes the panel
//! layout, and derives a minimal hashable `State` that drives `bake`.
//! The GPU plumbing is handled entirely by `ui::backend::OverlayBackend`.

use std::sync::Arc;

use swash::FontRef;
use tiny_skia::{Pixmap, Transform};

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};
use crate::ui::component::*;
use crate::ui::draw::{blit_pixmap, fill_rect, TextLine, TextRenderer};

use super::layout::{compute_layout, PanelLayout};
use super::model::{button_defs, NUM_SVG_BUTTONS};

const LABEL_FONT_PX: f32 = 11.0;
const AREA_FONT_PX: f32 = 11.0;
const ICON_UNSCALED_PX: f32 = 26.0;
const GRAY_RGBA: [u8; 4] = [0x37, 0x37, 0x37, 0xFF];
const HOVER_OVERLAY_STRENGTH: f32 = 0.30;
/// Milli-DPI quantization factor for the state hash.
const DPI_Q_SCALE: f32 = 1000.0;

pub struct ButtonPanelAssets {
    pub text: TextRenderer,
    pub svg_trees: [Arc<usvg::Tree>; NUM_SVG_BUTTONS],
}

#[derive(Clone, Hash, PartialEq, Eq)]
pub struct ButtonPanelState {
    monitor_bounds: ScreenRect,
    /// `(monitor.dpi_scale * DPI_Q_SCALE) as u32`.
    dpi_q: u32,
    selection: ScreenRect,
    accent_u8: [u8; 4],
}

pub struct ButtonPanelComponent {
    id: ComponentId,
    assets: ButtonPanelAssets,
    /// Cached layout for hit_test / cursor_hint / overlay_regions.
    /// Set in `derive_state`, cleared when hidden. Not hashed (it's a
    /// pure function of `State`).
    cached_layout: Option<PanelLayout>,
    /// Hover state — shell-only, NOT part of `State` because hover
    /// drives overlay_regions only and never affects the bake.
    hover_idx: Option<usize>,
}

impl ButtonPanelComponent {
    pub fn new() -> Self {
        let usvg_opts = usvg::Options::default();
        let defs = button_defs();
        let svg_trees: [Arc<usvg::Tree>; NUM_SVG_BUTTONS] = std::array::from_fn(|i| {
            match usvg::Tree::from_data(defs[i].svg_bytes, &usvg_opts) {
                Ok(t) => Arc::new(t),
                Err(e) => {
                    error!(
                        "failed to parse SVG for button {i} ({:?}): {e:?}",
                        defs[i].command
                    );
                    Arc::new(
                        usvg::Tree::from_str(
                            "<svg xmlns=\"http://www.w3.org/2000/svg\"/>",
                            &usvg::Options::default(),
                        )
                        .expect("empty SVG parses"),
                    )
                }
            }
        });

        let font = FontRef::from_index(super::assets::FONT_ROBOTO, 0)
            .expect("Roboto is valid TTF at build time");
        let text = TextRenderer::new(font);

        Self {
            id: ComponentId::new(),
            assets: ButtonPanelAssets { text, svg_trees },
            cached_layout: None,
            hover_idx: None,
        }
    }
}

impl Default for ButtonPanelComponent {
    fn default() -> Self {
        Self::new()
    }
}

fn quantize_color(c: [f32; 4]) -> [u8; 4] {
    [
        (c[0].clamp(0.0, 1.0) * 255.0) as u8,
        (c[1].clamp(0.0, 1.0) * 255.0) as u8,
        (c[2].clamp(0.0, 1.0) * 255.0) as u8,
        (c[3].clamp(0.0, 1.0) * 255.0) as u8,
    ]
}

/// Rasterize the panel into a pixmap from assets + state only.
fn bake_pixmap(assets: &ButtonPanelAssets, state: &ButtonPanelState) -> Option<Pixmap> {
    let dpi = (state.dpi_q as f32 / DPI_Q_SCALE).max(0.1);
    let layout = compute_layout(state.monitor_bounds, state.selection, dpi)?;
    let panel = layout.panel_rect;
    let w = panel.width().max(1) as u32;
    let h = panel.height().max(1) as u32;
    let mut pixmap = Pixmap::new(w, h)?;

    let to_local = |r: ScreenRect| -> (f32, f32, f32, f32) {
        let l = (r.left() - panel.left()) as f32;
        let t = (r.top() - panel.top()) as f32;
        let rt = (r.right() - panel.left()) as f32;
        let bt = (r.bottom() - panel.top()) as f32;
        (l, t, rt, bt)
    };

    let accent_u8 = state.accent_u8;

    for (i, b) in layout.buttons.iter().enumerate() {
        let (l, t, r, bt) = to_local(*b);
        let def = &button_defs()[i];
        let fill = if def.primary { accent_u8 } else { GRAY_RGBA };
        fill_rect(&mut pixmap, l, t, r - l, bt - t, fill);
    }

    // Area indicator.
    let (al, at, ar, ab) = to_local(layout.area_rect);
    let aw = ar - al;
    let ah = ab - at;
    fill_rect(&mut pixmap, al, at, aw, ah, GRAY_RGBA);

    let line = 2.0_f32.max(1.0);
    let edge = (aw / 3.0).floor().max(1.0);
    let white_rgba = [0xFF, 0xFF, 0xFF, 0xFF];
    fill_rect(&mut pixmap, al, at, edge, line, white_rgba);
    fill_rect(&mut pixmap, al, at, line, edge, white_rgba);
    fill_rect(&mut pixmap, ar - edge, at, edge, line, white_rgba);
    fill_rect(&mut pixmap, ar - line, at, line, edge, white_rgba);
    fill_rect(&mut pixmap, al, ab - line, edge, line, white_rgba);
    fill_rect(&mut pixmap, al, ab - edge, line, edge, white_rgba);
    fill_rect(&mut pixmap, ar - edge, ab - line, edge, line, white_rgba);
    fill_rect(&mut pixmap, ar - line, ab - edge, line, edge, white_rgba);

    // SVG icons + labels per button.
    let icon_size = (ICON_UNSCALED_PX * dpi).floor();
    let label_px = (LABEL_FONT_PX * dpi).floor();

    for (i, b) in layout.buttons.iter().enumerate() {
        let (l, t, r, bt) = to_local(*b);
        let bw = r - l;
        let bh = bt - t;

        let def = &button_defs()[i];
        let label_metrics = assets.text.measure_line(def.label, label_px);

        let v_gap = ((bh - icon_size - label_metrics.height) / 3.0).max(0.0);
        let icon_left = l + (bw / 2.0) - (icon_size / 2.0);
        let icon_top = t + v_gap;

        let tree = &assets.svg_trees[i];
        let vb = tree.size();
        let sx = icon_size / vb.width();
        let sy = icon_size / vb.height();
        let mut icon_pm = match Pixmap::new(icon_size as u32, icon_size as u32) {
            Some(p) => p,
            None => continue,
        };
        resvg::render(tree, Transform::from_scale(sx, sy), &mut icon_pm.as_mut());
        blit_pixmap(&mut pixmap, &icon_pm, icon_left as i32, icon_top as i32);

        let label_x = l + (bw / 2.0) - (label_metrics.width / 2.0);
        let label_lift = (2.0 * dpi).round();
        let label_y = icon_top + icon_size + v_gap - label_lift;
        let underline_thickness = dpi.round().max(1.0);
        assets.text.draw_text_line(
            &mut pixmap,
            TextLine {
                text: def.label,
                px: label_px,
                x: label_x,
                y: label_y,
                rgba: white_rgba,
                underline: Some(def.underline_idx),
                underline_thickness,
            },
        );
    }

    // Area indicator text.
    let area_px = (AREA_FONT_PX * dpi).floor();
    let width_str = state.selection.width().to_string();
    let height_str = state.selection.height().to_string();
    let x_str = "\u{00D7}";
    let mw = assets.text.measure_line(&width_str, area_px);
    let mh = assets.text.measure_line(&height_str, area_px);
    let mx = assets.text.measure_line(x_str, area_px);

    assets.text.draw_text_line(
        &mut pixmap,
        TextLine {
            text: &width_str,
            px: area_px,
            x: al + (aw / 2.0) - (mw.width / 2.0),
            y: at + (ah / 4.0) - (mw.height / 2.0),
            rgba: white_rgba,
            underline: None,
            underline_thickness: 0.0,
        },
    );
    assets.text.draw_text_line(
        &mut pixmap,
        TextLine {
            text: &height_str,
            px: area_px,
            x: al + (aw / 2.0) - (mh.width / 2.0),
            y: at + (ah / 1.34) - (mh.height / 2.0),
            rgba: white_rgba,
            underline: None,
            underline_thickness: 0.0,
        },
    );
    assets.text.draw_text_line(
        &mut pixmap,
        TextLine {
            text: x_str,
            px: area_px,
            x: al + (aw / 2.0) - (mx.width / 2.0),
            y: at + (ah / 2.0) - (mx.height / 2.0),
            rgba: [0xFF, 0xFF, 0xFF, (0.70 * 255.0) as u8],
            underline: None,
            underline_thickness: 0.0,
        },
    );

    Some(pixmap)
}

impl Component for ButtonPanelComponent {
    type Assets = ButtonPanelAssets;
    type State = ButtonPanelState;

    fn id(&self) -> ComponentId {
        self.id
    }

    fn assets(&self) -> &ButtonPanelAssets {
        &self.assets
    }

    fn derive_state(&mut self, ctx: &AppContext) -> DeriveResult<ButtonPanelState> {
        if !ctx.captured {
            self.cached_layout = None;
            self.hover_idx = None;
            return DeriveResult::Hidden;
        }
        let Some(sel) = ctx.selection else {
            self.cached_layout = None;
            self.hover_idx = None;
            return DeriveResult::Hidden;
        };
        let Some((idx, mon)) = pick_monitor_containing_center(ctx.monitors, sel) else {
            self.cached_layout = None;
            self.hover_idx = None;
            return DeriveResult::Hidden;
        };
        let Some(layout) = compute_layout(mon.bounds, sel, mon.dpi_scale) else {
            self.cached_layout = None;
            self.hover_idx = None;
            return DeriveResult::Hidden;
        };

        self.cached_layout = Some(layout);
        let state = ButtonPanelState {
            monitor_bounds: mon.bounds,
            dpi_q: (mon.dpi_scale.max(0.1) * DPI_Q_SCALE) as u32,
            selection: sel,
            accent_u8: quantize_color(ctx.accent_color),
        };
        DeriveResult::Visible {
            monitor_idx: idx,
            state,
        }
    }

    fn bake(assets: &ButtonPanelAssets, state: &ButtonPanelState) -> Option<BakedPixmap> {
        let dpi = (state.dpi_q as f32 / DPI_Q_SCALE).max(0.1);
        let layout = compute_layout(state.monitor_bounds, state.selection, dpi)?;
        let panel = layout.panel_rect;
        let pixmap = bake_pixmap(assets, state)?;
        Some(BakedPixmap {
            data: pixmap.data().to_vec(),
            width: pixmap.width(),
            height: pixmap.height(),
            dest_vd: panel,
        })
    }

    fn hit_test(&self, pos: ScreenPointF) -> bool {
        let Some(layout) = self.cached_layout else {
            return false;
        };
        let r = layout.panel_rect;
        let px = pos.x.floor() as i32;
        let py = pos.y.floor() as i32;
        px >= r.left() && px < r.right() && py >= r.top() && py < r.bottom()
    }

    fn cursor_hint(&self, pos: ScreenPointF) -> CursorHint {
        let Some(layout) = self.cached_layout else {
            return CursorHint::Default;
        };
        if layout.hit_test(pos.x, pos.y).is_some() {
            CursorHint::Pointer
        } else {
            CursorHint::Default
        }
    }

    fn on_mouse_event(&mut self, event: MouseEvent) -> EventResponse {
        let Some(layout) = self.cached_layout else {
            return EventResponse::Ignored;
        };
        match event {
            MouseEvent::Move { pos } => {
                let new_hover = layout.hit_test(pos.x, pos.y);
                if new_hover != self.hover_idx {
                    self.hover_idx = new_hover;
                    EventResponse::NeedsOverlayUpdate
                } else {
                    EventResponse::Ignored
                }
            }
            MouseEvent::Press { pos } => {
                if let Some(idx) = layout.hit_test(pos.x, pos.y) {
                    EventResponse::Command(button_defs()[idx].command)
                } else {
                    EventResponse::Ignored
                }
            }
            MouseEvent::Release { .. } => EventResponse::Ignored,
        }
    }

    fn overlay_regions(&self) -> Vec<OverlayRegion> {
        let Some(layout) = self.cached_layout else {
            return Vec::new();
        };
        let panel = layout.panel_rect;
        let pw = panel.width() as f32;
        let ph = panel.height() as f32;
        if pw <= 0.0 || ph <= 0.0 {
            return Vec::new();
        }
        layout
            .buttons
            .iter()
            .enumerate()
            .map(|(i, b)| {
                let l = (b.left() - panel.left()) as f32 / pw;
                let t = (b.top() - panel.top()) as f32 / ph;
                let r = (b.right() - panel.left()) as f32 / pw;
                let bt = (b.bottom() - panel.top()) as f32 / ph;
                OverlayRegion {
                    uv_rect: [l, t, r, bt],
                    mode: RegionMode::Lighten,
                    target_amount: if self.hover_idx == Some(i) {
                        HOVER_OVERLAY_STRENGTH
                    } else {
                        0.0
                    },
                }
            })
            .collect()
    }
}
