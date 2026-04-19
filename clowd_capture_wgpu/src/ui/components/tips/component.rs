//! Tips & Hotkeys help panel.
//!
//! Port of the C++ panel at `DxScreenCapture.cpp:741-828`. Stays visible
//! before the user commits to a selection, anchored to the bottom of
//! the primary monitor, and disappears while the mouse is being dragged
//! or once a selection has been captured. The `T` key toggles it on
//! and off entirely.
//!
//! All pixmap content is rasterized fully opaque; the shader applies
//! `base_opacity = 0.70` per-component at composite time (see
//! `overlay_quad.wgsl` and `Component::base_opacity`), keeping text
//! crisp while the panel composites at 70%. The drop shadow uses the
//! per-region alpha override (`OverlayRegion::alpha_override`) to
//! composite the right/bottom strips at `SHADOW_ALPHA = 0.30` instead,
//! matching the C++ panel's `brushOverlay30` shadow at
//! DxScreenCapture.cpp:784-787.

use swash::FontRef;
use tiny_skia::Pixmap;

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};
use crate::ui::component::*;
use crate::ui::draw::{fill_rect, LineMetrics, TextLine, TextRenderer};

use super::assets::{FONT_MONO_BOLD, FONT_MONO_REGULAR};
use super::layout::{compute_layout, TipsLayout, BODY_FONT_PX, TITLE_FONT_PX};
use super::model::{
    render_description, COLOR_ROW_HOTKEY, HOTKEY_GAP, TIPS_BOTTOM, TIPS_TOP, TITLE,
};

const BASE_OPACITY: f32 = 0.70;
const SHADOW_ALPHA: f32 = 0.30;
const WHITE_RGBA: [u8; 4] = [0xFF, 0xFF, 0xFF, 0xFF];
const BLACK_RGBA: [u8; 4] = [0x00, 0x00, 0x00, 0xFF];

/// DPI quantization factor. `dpi * DPI_Q_SCALE` is stored as u32 for
/// hashing; 1000 = milli-DPI resolution, enough granularity that
/// integer-pixel layout values are identical for any two sub-1/1000
/// deltas while keeping the state hash stable under meaningless jitter.
const DPI_Q_SCALE: f32 = 1000.0;

/// Immutable render resources for the Tips panel. Contract: `Component::bake`
/// only reads `&TipsPanelAssets` + `&TipsPanelState`, so nothing in here
/// may change during a component's lifetime.
pub struct TipsPanelAssets {
    pub text_mono: TextRenderer,
    pub text_bold: TextRenderer,
}

/// Hashable inputs to `bake`. The cursor itself is NOT stored —
/// everything the cursor affects collapses to a single bool (did we
/// fall back from the default right-anchor to the left-anchor?), so
/// mouse moves inside the same anchor region don't bust the hash.
#[derive(Clone, Hash, PartialEq, Eq)]
pub struct TipsPanelState {
    primary_bounds: ScreenRect,
    /// `(primary.dpi_scale * DPI_Q_SCALE) as u32`.
    dpi_q: u32,
    /// Collapsed cursor → panel-anchor decision. Derived once per sync
    /// from the real cursor; bake synthesizes a cursor from this bool
    /// when it recomputes the layout.
    use_left_fallback: bool,
    /// Accent RGBA already quantized to the same `(f*255) as u8` rule
    /// the rasterizer uses, so hash identity ≡ bake-output identity.
    accent_u8: [u8; 4],
    hovered_window: Option<String>,
    hovered_monitor: Option<String>,
    hovered_pixel_bgra: Option<[u8; 4]>,
}

pub struct TipsPanelComponent {
    id: ComponentId,
    assets: TipsPanelAssets,
    /// Cached layout from the latest `derive_state`. Used by
    /// `overlay_regions`; NOT hashed (it's a pure function of state).
    cached_layout: Option<TipsLayout>,
}

impl TipsPanelComponent {
    pub fn new() -> Self {
        let mono = FontRef::from_index(FONT_MONO_REGULAR, 0)
            .expect("Cascadia Mono Regular is valid TTF at build time");
        let bold = FontRef::from_index(FONT_MONO_BOLD, 0)
            .expect("Cascadia Mono Bold is valid TTF at build time");
        Self {
            id: ComponentId::new(),
            assets: TipsPanelAssets {
                text_mono: TextRenderer::new(mono),
                text_bold: TextRenderer::new(bold),
            },
            cached_layout: None,
        }
    }
}

impl Default for TipsPanelComponent {
    fn default() -> Self {
        Self::new()
    }
}

/// Measure the widest body row at the target font size, including
/// hotkey, gap, and description columns.
fn measure_widest_body(
    font: &TextRenderer,
    body_px: f32,
    hovered_window: Option<&str>,
    hovered_monitor: Option<&str>,
) -> f32 {
    let longest_hotkey_w = font.measure_line(&format!("H{}", HOTKEY_GAP), body_px).width;
    let mut widest_desc = 0.0_f32;

    let to_measure: Vec<String> = TIPS_TOP
        .iter()
        .chain(TIPS_BOTTOM.iter())
        .map(|r| render_description(r.description_template, hovered_window, hovered_monitor))
        .collect();

    for d in &to_measure {
        let w = font.measure_line(d, body_px).width;
        if w > widest_desc {
            widest_desc = w;
        }
    }

    let color_line_w = font
        .measure_line("#FFFFFF", body_px)
        .width
        .max(font.measure_line("rgb(255, 255, 255)", body_px).width);

    longest_hotkey_w + widest_desc.max(color_line_w)
}

/// Build the TipsLayout from state + assets. Deterministic in state,
/// so bake and overlay_regions produce identical results for the same
/// hashed state. The cursor isn't in state — we synthesize one that
/// reproduces the stored `use_left_fallback` decision.
fn compute_layout_from_state(
    assets: &TipsPanelAssets,
    state: &TipsPanelState,
) -> Option<TipsLayout> {
    // Extreme coordinates guarantee the cursor/threshold comparison
    // in `compute_layout` falls on the intended side regardless of
    // panel dimensions.
    let cursor = if state.use_left_fallback {
        ScreenPointF::new(f32::MAX, f32::MAX)
    } else {
        ScreenPointF::new(f32::MIN, f32::MIN)
    };
    compute_layout_from_cursor(
        assets,
        state.primary_bounds,
        (state.dpi_q as f32 / DPI_Q_SCALE).max(0.1),
        state.hovered_window.as_deref(),
        state.hovered_monitor.as_deref(),
        cursor,
    )
}

/// Compute the layout for a given real cursor — used once in
/// `derive_state` to learn the `use_left_fallback` bool, and again in
/// `bake` via `compute_layout_from_state` with a synthesized cursor.
fn compute_layout_from_cursor(
    assets: &TipsPanelAssets,
    primary_bounds: ScreenRect,
    dpi: f32,
    hovered_window: Option<&str>,
    hovered_monitor: Option<&str>,
    cursor: ScreenPointF,
) -> Option<TipsLayout> {
    let body_px = (BODY_FONT_PX * dpi).floor();
    let title_px = (TITLE_FONT_PX * dpi).floor();
    let longest_body =
        measure_widest_body(&assets.text_mono, body_px, hovered_window, hovered_monitor);
    let title_m: LineMetrics = assets.text_bold.measure_line(TITLE, title_px);
    let body_m: LineMetrics = assets.text_mono.measure_line("Hg", body_px);
    compute_layout(
        primary_bounds,
        cursor,
        dpi,
        longest_body,
        title_m.width,
        body_m.height * 1.4,
        title_m.height,
    )
}

fn quantize_color(c: [f32; 4]) -> [u8; 4] {
    [
        (c[0].clamp(0.0, 1.0) * 255.0) as u8,
        (c[1].clamp(0.0, 1.0) * 255.0) as u8,
        (c[2].clamp(0.0, 1.0) * 255.0) as u8,
        (c[3].clamp(0.0, 1.0) * 255.0) as u8,
    ]
}

/// Rasterize the panel into a pixmap, using ONLY assets + state.
fn bake_pixmap(assets: &TipsPanelAssets, state: &TipsPanelState) -> Option<Pixmap> {
    let layout = compute_layout_from_state(assets, state)?;
    let body_w = layout.panel_rect.width().max(1) as u32;
    let body_h = layout.panel_rect.height().max(1) as u32;
    let shadow_ext = layout.shadow_extension_px.round().max(0.0) as u32;
    let pixmap_w = body_w + shadow_ext;
    let pixmap_h = body_h + shadow_ext;
    let mut pixmap = Pixmap::new(pixmap_w, pixmap_h)?;

    let body_px = (BODY_FONT_PX * layout.dpi_scale).floor();
    let title_px = (TITLE_FONT_PX * layout.dpi_scale).floor();
    let body_w_f = body_w as f32;
    let body_h_f = body_h as f32;
    let shadow_f = shadow_ext as f32;

    // Body background (white, opaque — shader applies 0.7).
    fill_rect(&mut pixmap, 0.0, 0.0, body_w_f, body_h_f, WHITE_RGBA);

    // Title bar (accent color, opaque).
    let title_h = layout.title_rect.height() as f32;
    let accent_u8 = [
        state.accent_u8[0],
        state.accent_u8[1],
        state.accent_u8[2],
        0xFF,
    ];
    fill_rect(&mut pixmap, 0.0, 0.0, body_w_f, title_h, accent_u8);

    // Drop shadow.
    if shadow_ext > 0 {
        fill_rect(
            &mut pixmap,
            body_w_f,
            shadow_f,
            shadow_f,
            body_h_f,
            BLACK_RGBA,
        );
        fill_rect(
            &mut pixmap,
            shadow_f,
            body_h_f,
            body_w_f - shadow_f,
            shadow_f,
            BLACK_RGBA,
        );
    }

    // Title text — bold mono, white, centered in the title bar.
    let title_metrics = assets.text_bold.measure_line(TITLE, title_px);
    let title_x = (body_w_f - title_metrics.width) * 0.5;
    let title_y = (title_h - title_metrics.height) * 0.5;
    assets.text_bold.draw_text_line(
        &mut pixmap,
        TextLine {
            text: TITLE,
            px: title_px,
            x: title_x,
            y: title_y,
            rgba: WHITE_RGBA,
            underline: None,
            underline_thickness: 0.0,
        },
    );

    // Top tip block.
    let mut y = layout.top_block_y;
    for row in TIPS_TOP {
        let desc = render_description(
            row.description_template,
            state.hovered_window.as_deref(),
            state.hovered_monitor.as_deref(),
        );
        draw_row(
            &assets.text_mono,
            &mut pixmap,
            body_px,
            &layout,
            y,
            row.hotkey,
            &desc,
        );
        y += layout.row_height;
    }

    // Color sampler row.
    draw_color_row(
        &assets.text_mono,
        &mut pixmap,
        body_px,
        &layout,
        state.hovered_pixel_bgra,
    );

    // Bottom tip block.
    let mut y = layout.bottom_block_y;
    for row in TIPS_BOTTOM {
        draw_row(
            &assets.text_mono,
            &mut pixmap,
            body_px,
            &layout,
            y,
            row.hotkey,
            row.description_template,
        );
        y += layout.row_height;
    }

    Some(pixmap)
}

fn draw_row(
    font: &TextRenderer,
    pixmap: &mut Pixmap,
    body_px: f32,
    layout: &TipsLayout,
    y: f32,
    hotkey: &str,
    description: &str,
) {
    font.draw_text_line(
        pixmap,
        TextLine {
            text: hotkey,
            px: body_px,
            x: layout.col_hotkey_x,
            y,
            rgba: BLACK_RGBA,
            underline: None,
            underline_thickness: 0.0,
        },
    );
    font.draw_text_line(
        pixmap,
        TextLine {
            text: description,
            px: body_px,
            x: layout.col_desc_x,
            y,
            rgba: BLACK_RGBA,
            underline: None,
            underline_thickness: 0.0,
        },
    );
}

fn draw_color_row(
    font: &TextRenderer,
    pixmap: &mut Pixmap,
    body_px: f32,
    layout: &TipsLayout,
    hovered_pixel_bgra: Option<[u8; 4]>,
) {
    font.draw_text_line(
        pixmap,
        TextLine {
            text: COLOR_ROW_HOTKEY,
            px: body_px,
            x: layout.col_hotkey_x,
            y: layout.color_row_y,
            rgba: BLACK_RGBA,
            underline: None,
            underline_thickness: 0.0,
        },
    );

    let box_x = layout.col_desc_x;
    let box_y = layout.color_row_y;
    let box_size = layout.color_box_size;
    fill_rect(pixmap, box_x, box_y, box_size, box_size, BLACK_RGBA);
    let inset = layout.dpi_scale.max(1.0).round();
    let fill_rgba = hovered_pixel_bgra
        .map(|[b, g, r, _]| [r, g, b, 0xFFu8])
        .unwrap_or(BLACK_RGBA);
    fill_rect(
        pixmap,
        box_x + inset,
        box_y + inset,
        (box_size - inset * 2.0).max(0.0),
        (box_size - inset * 2.0).max(0.0),
        fill_rgba,
    );

    let (hex_text, rgb_text) = match hovered_pixel_bgra {
        Some([b, g, r, _]) => (
            format!("#{:02X}{:02X}{:02X}", r, g, b),
            format!("rgb({}, {}, {})", r, g, b),
        ),
        None => ("#------".to_string(), String::new()),
    };
    font.draw_text_line(
        pixmap,
        TextLine {
            text: &hex_text,
            px: body_px,
            x: layout.color_hex_x,
            y: layout.color_hex_y,
            rgba: BLACK_RGBA,
            underline: None,
            underline_thickness: 0.0,
        },
    );
    if !rgb_text.is_empty() {
        font.draw_text_line(
            pixmap,
            TextLine {
                text: &rgb_text,
                px: body_px,
                x: layout.color_hex_x,
                y: layout.color_rgb_y,
                rgba: BLACK_RGBA,
                underline: None,
                underline_thickness: 0.0,
            },
        );
    }
}

impl Component for TipsPanelComponent {
    type Assets = TipsPanelAssets;
    type State = TipsPanelState;

    fn id(&self) -> ComponentId {
        self.id
    }

    fn assets(&self) -> &TipsPanelAssets {
        &self.assets
    }

    fn derive_state(&mut self, ctx: &AppContext) -> DeriveResult<TipsPanelState> {
        if ctx.captured || ctx.mouse_down || !ctx.tips_visible {
            self.cached_layout = None;
            return DeriveResult::Hidden;
        }
        let Some(primary_idx) = ctx.primary_monitor_idx else {
            self.cached_layout = None;
            return DeriveResult::Hidden;
        };
        let Some(primary) = ctx.monitors.get(primary_idx) else {
            self.cached_layout = None;
            return DeriveResult::Hidden;
        };

        let dpi = primary.dpi_scale.max(0.1);
        let hovered_window = ctx.hovered_window_title.map(str::to_string);
        let hovered_monitor = ctx.hovered_monitor_name.map(str::to_string);

        // Compute the layout once with the real cursor so we can learn
        // which anchor branch the fallback check picked. The returned
        // bool goes into the hashed state; bake reproduces the same
        // layout via a synthesized cursor.
        let Some(layout) = compute_layout_from_cursor(
            &self.assets,
            primary.bounds,
            dpi,
            hovered_window.as_deref(),
            hovered_monitor.as_deref(),
            ctx.virtual_cursor,
        ) else {
            self.cached_layout = None;
            return DeriveResult::Hidden;
        };

        let state = TipsPanelState {
            primary_bounds: primary.bounds,
            dpi_q: (dpi * DPI_Q_SCALE) as u32,
            use_left_fallback: layout.use_left_fallback,
            accent_u8: quantize_color(ctx.accent_color),
            hovered_window,
            hovered_monitor,
            hovered_pixel_bgra: ctx.hovered_pixel_bgra,
        };
        self.cached_layout = Some(layout);

        DeriveResult::Visible {
            monitor_idx: primary_idx,
            state,
        }
    }

    fn bake(assets: &TipsPanelAssets, state: &TipsPanelState) -> Option<BakedPixmap> {
        let layout = compute_layout_from_state(assets, state)?;
        let pixmap = bake_pixmap(assets, state)?;
        let shadow_ext = layout.shadow_extension_px.round().max(0.0) as i32;
        let dest_vd = ScreenRect::from_xy_size(
            layout.panel_rect.left(),
            layout.panel_rect.top(),
            layout.panel_rect.width() + shadow_ext,
            layout.panel_rect.height() + shadow_ext,
        );
        Some(BakedPixmap {
            data: pixmap.data().to_vec(),
            width: pixmap.width(),
            height: pixmap.height(),
            dest_vd,
        })
    }

    fn hit_test(&self, _pos: ScreenPointF) -> bool {
        false
    }

    fn cursor_hint(&self, _pos: ScreenPointF) -> CursorHint {
        CursorHint::Default
    }

    fn on_mouse_event(&mut self, _event: MouseEvent) -> EventResponse {
        EventResponse::Ignored
    }

    fn overlay_regions(&self) -> Vec<OverlayRegion> {
        let Some(layout) = self.cached_layout else {
            return Vec::new();
        };
        let body_w = layout.panel_rect.width() as f32;
        let body_h = layout.panel_rect.height() as f32;
        let shadow = layout.shadow_extension_px.round();
        if body_w <= 0.0 || body_h <= 0.0 || shadow <= 0.0 {
            return Vec::new();
        }
        let pw = body_w + shadow;
        let ph = body_h + shadow;
        vec![
            OverlayRegion {
                uv_rect: [body_w / pw, shadow / ph, 1.0, 1.0],
                mode: RegionMode::Fade,
                target_amount: SHADOW_ALPHA,
            },
            OverlayRegion {
                uv_rect: [shadow / pw, body_h / ph, body_w / pw, 1.0],
                mode: RegionMode::Fade,
                target_amount: SHADOW_ALPHA,
            },
        ]
    }

    fn base_opacity(&self) -> f32 {
        BASE_OPACITY
    }
}
