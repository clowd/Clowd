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

/// Component-wide alpha — the shader multiplies the baked pixels by
/// this, matching the 70% opacity of the old panel's bg fills and
/// keeping text/edges crisp because the bake itself is fully opaque.
const BASE_OPACITY: f32 = 0.70;

/// Drop-shadow composite alpha. Mirrors `brushOverlay30` in the old
/// C++ code. Applied via per-region alpha override so the shadow
/// composites at this value while the rest of the panel stays at
/// `BASE_OPACITY`.
const SHADOW_ALPHA: f32 = 0.30;

/// Premultiplied white (body background and title text).
const WHITE_RGBA: [u8; 4] = [0xFF, 0xFF, 0xFF, 0xFF];
/// Premultiplied black (body text, color-sampler outline, drop shadow).
const BLACK_RGBA: [u8; 4] = [0x00, 0x00, 0x00, 0xFF];

pub struct TipsPanelComponent {
    id: ComponentId,
    /// Cached layout from the latest `update()` — `None` while hidden.
    layout: Option<TipsLayout>,
    /// Accent color (RGBA floats in 0..1) — painted into the title bar.
    accent_color: [f32; 4],
    /// Copy of the runtime strings sampled in the latest `update()`.
    /// Cached so `bake()` can format the body without re-reading ctx.
    hovered_window: Option<String>,
    hovered_monitor: Option<String>,
    hovered_pixel_bgra: Option<[u8; 4]>,
    text_mono: TextRenderer,
    text_bold: TextRenderer,
}

impl TipsPanelComponent {
    pub fn new() -> Self {
        let mono = FontRef::from_index(FONT_MONO_REGULAR, 0)
            .expect("Cascadia Mono Regular is valid TTF at build time");
        let bold = FontRef::from_index(FONT_MONO_BOLD, 0)
            .expect("Cascadia Mono Bold is valid TTF at build time");
        Self {
            id: ComponentId::new(),
            layout: None,
            accent_color: [1.0, 1.0, 1.0, 1.0],
            hovered_window: None,
            hovered_monitor: None,
            hovered_pixel_bgra: None,
            text_mono: TextRenderer::new(mono),
            text_bold: TextRenderer::new(bold),
        }
    }

    /// Measure the widest body row at the target font size, including
    /// hotkey, gap, and description columns — so the layout can size
    /// the panel wide enough to contain everything.
    fn measure_widest_body(&self, body_px: f32) -> f32 {
        let longest_hotkey_w = self
            .text_mono
            .measure_line(&format!("H{}", HOTKEY_GAP), body_px)
            .width;
        let mut widest_desc = 0.0_f32;

        let to_measure: Vec<String> = TIPS_TOP
            .iter()
            .chain(TIPS_BOTTOM.iter())
            .map(|r| {
                render_description(
                    r.description_template,
                    self.hovered_window.as_deref(),
                    self.hovered_monitor.as_deref(),
                )
            })
            .collect();

        for d in &to_measure {
            let w = self.text_mono.measure_line(d, body_px).width;
            if w > widest_desc {
                widest_desc = w;
            }
        }

        // Include the widest possible color-sampler description
        // ("  #RRGGBB     rgb(255, 255, 255)") so it doesn't get clipped
        // even though it's split across two rows.
        let color_line_w = self
            .text_mono
            .measure_line("#FFFFFF", body_px)
            .width
            .max(
                self.text_mono
                    .measure_line("rgb(255, 255, 255)", body_px)
                    .width,
            );

        longest_hotkey_w + widest_desc.max(color_line_w)
    }

    /// Rasterize the panel into a pixmap. Called from `bake()`.
    ///
    /// The pixmap is `body_w + shadow` wide and `body_h + shadow` tall:
    /// the body+title cover (0..body_w, 0..body_h), and the right /
    /// bottom strips of the extension carry the drop shadow. Pixels
    /// outside both stay transparent.
    fn bake_pixmap(&mut self) -> Option<Pixmap> {
        let layout = self.layout?;
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

        // --- Body background (white, opaque — shader applies 0.7) ---
        fill_rect(&mut pixmap, 0.0, 0.0, body_w_f, body_h_f, WHITE_RGBA);

        // --- Title bar (accent color, opaque) ---
        let title_h = layout.title_rect.height() as f32;
        let accent_u8 = [
            (self.accent_color[0].clamp(0.0, 1.0) * 255.0) as u8,
            (self.accent_color[1].clamp(0.0, 1.0) * 255.0) as u8,
            (self.accent_color[2].clamp(0.0, 1.0) * 255.0) as u8,
            0xFF,
        ];
        fill_rect(&mut pixmap, 0.0, 0.0, body_w_f, title_h, accent_u8);

        // --- Drop shadow (opaque black; shader composites at
        // SHADOW_ALPHA via per-region alpha override). Two strips form
        // an "L" hugging the bottom-right; top-right and bottom-left
        // corners stay clear so the shadow reads as a directional drop.
        // Mirrors DxScreenCapture.cpp:784-787.
        if shadow_ext > 0 {
            // Right strip: x ∈ [body_w, body_w + shadow], y ∈ [shadow, body_h + shadow]
            fill_rect(
                &mut pixmap,
                body_w_f,
                shadow_f,
                shadow_f,
                body_h_f,
                BLACK_RGBA,
            );
            // Bottom strip: x ∈ [shadow, body_w], y ∈ [body_h, body_h + shadow]
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
        let title_metrics = self.text_bold.measure_line(TITLE, title_px);
        let title_x = (body_w_f - title_metrics.width) * 0.5;
        let title_y = (title_h - title_metrics.height) * 0.5;
        self.text_bold.draw_text_line(
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

        // --- Top tip block ---
        let mut y = layout.top_block_y;
        for row in TIPS_TOP {
            let desc = render_description(
                row.description_template,
                self.hovered_window.as_deref(),
                self.hovered_monitor.as_deref(),
            );
            self.draw_row(&mut pixmap, body_px, &layout, y, row.hotkey, &desc);
            y += layout.row_height;
        }

        // --- Color sampler row ---
        self.draw_color_row(&mut pixmap, body_px, &layout);

        // --- Bottom tip block ---
        let mut y = layout.bottom_block_y;
        for row in TIPS_BOTTOM {
            self.draw_row(
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
        &mut self,
        pixmap: &mut Pixmap,
        body_px: f32,
        layout: &TipsLayout,
        y: f32,
        hotkey: &str,
        description: &str,
    ) {
        self.text_mono.draw_text_line(
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
        self.text_mono.draw_text_line(
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
        &mut self,
        pixmap: &mut Pixmap,
        body_px: f32,
        layout: &TipsLayout,
    ) {
        // Hotkey letter.
        self.text_mono.draw_text_line(
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

        // Color-sampler square: 1 px black outline around the sampled
        // fill. Position the square so its top aligns with the hex row.
        // `dest_vd` uses virtual-desktop pixels but everything here is
        // panel-local and `fill_rect` takes f32 pixel coords, so we
        // draw directly in the pixmap.
        let box_x = layout.col_desc_x;
        let box_y = layout.color_row_y;
        let box_size = layout.color_box_size;
        fill_rect(pixmap, box_x, box_y, box_size, box_size, BLACK_RGBA);
        let inset = layout.dpi_scale.max(1.0).round();
        let fill_rgba = self
            .hovered_pixel_bgra
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

        // Hex + rgb text, stacked on two lines to the right of the box.
        let (hex_text, rgb_text) = match self.hovered_pixel_bgra {
            Some([b, g, r, _]) => (
                format!("#{:02X}{:02X}{:02X}", r, g, b),
                format!("rgb({}, {}, {})", r, g, b),
            ),
            None => ("#------".to_string(), String::new()),
        };
        self.text_mono.draw_text_line(
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
            self.text_mono.draw_text_line(
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
}

impl Default for TipsPanelComponent {
    fn default() -> Self {
        Self::new()
    }
}

impl Component for TipsPanelComponent {
    fn id(&self) -> ComponentId {
        self.id
    }

    fn update(&mut self, ctx: &AppContext) -> Placement {
        // Visibility gates — all three must be clear to draw anything.
        //   * user hasn't finalised a selection yet
        //   * user isn't currently mid-drag
        //   * user hasn't turned the panel off with T
        if ctx.captured || ctx.mouse_down || !ctx.tips_visible {
            self.layout = None;
            return Placement::Hidden;
        }

        let Some(primary_idx) = ctx.primary_monitor_idx else {
            self.layout = None;
            return Placement::Hidden;
        };
        let Some(primary) = ctx.monitors.get(primary_idx) else {
            self.layout = None;
            return Placement::Hidden;
        };

        // Cache runtime strings so bake() can format without needing
        // AppContext — bake() runs against whatever we captured here.
        self.hovered_window = ctx.hovered_window_title.map(|s| s.to_string());
        self.hovered_monitor = ctx.hovered_monitor_name.map(|s| s.to_string());
        self.hovered_pixel_bgra = ctx.hovered_pixel_bgra;
        self.accent_color = ctx.accent_color;

        let dpi = primary.dpi_scale.max(0.1);
        let body_px = (BODY_FONT_PX * dpi).floor();
        let title_px = (TITLE_FONT_PX * dpi).floor();

        let longest_body = self.measure_widest_body(body_px);
        let title_m: LineMetrics = self.text_bold.measure_line(TITLE, title_px);
        let body_m: LineMetrics = self
            .text_mono
            .measure_line("Hg", body_px);

        let Some(layout) = compute_layout(
            primary.bounds,
            ctx.virtual_cursor,
            dpi,
            longest_body,
            title_m.width,
            body_m.height * 1.4, // Line height — cap-height plus ~40% leading.
            title_m.height,
        ) else {
            self.layout = None;
            return Placement::Hidden;
        };

        self.layout = Some(layout);
        Placement::Visible { monitor_idx: primary_idx }
    }

    fn hit_test(&self, _pos: ScreenPointF) -> bool {
        // The tips panel is non-interactive — never claim any input.
        false
    }

    fn cursor_hint(&self, _pos: ScreenPointF) -> CursorHint {
        CursorHint::Default
    }

    fn on_mouse_event(&mut self, _event: MouseEvent) -> EventResponse {
        EventResponse::Ignored
    }

    fn bake(&mut self) -> Option<BakedPixmap> {
        let layout = self.layout?;
        let pixmap = self.bake_pixmap()?;
        // Extend the destination rect right & down by the shadow strip
        // so the L-shaped shadow has somewhere to land. The body+title
        // still occupy the original panel_rect at the top-left of the
        // bake.
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

    fn overlay_regions(&self) -> Vec<OverlayRegion> {
        // Two regions for the drop shadow's right & bottom strips.
        // Fade mode replaces the panel's base alpha (0.70) with
        // SHADOW_ALPHA (0.30) inside these strips. The shadow texels
        // are baked opaque black; the shader takes care of compositing.
        let Some(layout) = self.layout else { return Vec::new() };
        let body_w = layout.panel_rect.width() as f32;
        let body_h = layout.panel_rect.height() as f32;
        let shadow = layout.shadow_extension_px.round();
        if body_w <= 0.0 || body_h <= 0.0 || shadow <= 0.0 {
            return Vec::new();
        }
        let pw = body_w + shadow;
        let ph = body_h + shadow;
        vec![
            // Right strip
            OverlayRegion {
                uv_rect: [body_w / pw, shadow / ph, 1.0, 1.0],
                mode: RegionMode::Fade,
                target_amount: SHADOW_ALPHA,
            },
            // Bottom strip
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

