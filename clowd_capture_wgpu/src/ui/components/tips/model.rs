//! Static content for the Tips & Hotkeys panel.
//!
//! Everything except the three runtime-populated strings (hovered
//! window title, hovered monitor name, hovered pixel RGB) is declared
//! here as compile-time constants. Layout math reads this table to
//! measure the longest row and size the panel accordingly.

/// The accent-colored title bar text.
pub const TITLE: &str = "Tips & Hotkeys";

/// A single body row: a one-character hotkey label followed by the
/// description text. Rendered as "<hotkey>   <description>" with three
/// spaces between, matching the mono-font column layout from
/// `DxScreenCapture.cpp:746-752`.
pub struct TipRow {
    pub hotkey: &'static str,
    /// `{window}` is substituted with `AppContext.hovered_window_title`,
    /// `{monitor}` with `AppContext.hovered_monitor_name`. No substitution
    /// for rows that don't contain a placeholder.
    pub description_template: &'static str,
}

impl TipRow {
    const fn new(hotkey: &'static str, description_template: &'static str) -> Self {
        Self {
            hotkey,
            description_template,
        }
    }
}

/// The top block of body rows, above the color-sampler row.
/// `DxScreenCapture.cpp:746-749`.
pub const TIPS_TOP: &[TipRow] = &[
    TipRow::new("-", "Scroll to zoom!"),
    TipRow::new("W", "Select {window}"),
    TipRow::new("F", "Select monitor {monitor}"),
    TipRow::new("A", "Select all monitors"),
];

/// The bottom block of body rows, below the color-sampler row.
/// `DxScreenCapture.cpp:750,752`.
pub const TIPS_BOTTOM: &[TipRow] = &[
    TipRow::new("D", "Toggle debug stats"),
    TipRow::new("T", "Toggle this panel"),
    TipRow::new("Q", "Toggle cursor/crosshair"),
];

/// Hotkey label for the color-sampler row. The description is computed
/// from `AppContext.hovered_pixel_bgra` at bake time.
pub const COLOR_ROW_HOTKEY: &str = "H";

/// Number of spaces between hotkey and description (matches the visual
/// column from the old panel, where `paddingHalf = 10px` created roughly
/// 3 space-widths in Consolas at 12px).
pub const HOTKEY_GAP: &str = "   ";

/// Fallback string when no window / monitor is under the cursor, matching
/// `DxScreenCapture.cpp` which uses the string "n/a" in the same case.
pub const FALLBACK: &str = "n/a";

/// Render the body of a single tip row (description only, without hotkey
/// or gap) with runtime substitutions applied.
pub fn render_description(
    template: &str,
    hovered_window: Option<&str>,
    hovered_monitor: Option<&str>,
) -> String {
    template
        .replace("{window}", hovered_window.unwrap_or(FALLBACK))
        .replace("{monitor}", hovered_monitor.unwrap_or(FALLBACK))
}
