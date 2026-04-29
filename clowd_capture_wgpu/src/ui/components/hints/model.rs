use crate::ui::components::tips::model::{truncate_ellipsis, MONITOR_NAME_MAX, WINDOW_TITLE_MAX};

pub struct HintDef {
    pub template: &'static str,
}

pub const HINT_WINDOW: HintDef = HintDef {
    template: "Select {window}",
};
pub const HINT_MONITOR: HintDef = HintDef {
    template: "Select {monitor}",
};
pub fn render_hint_text(template: &str, hovered_window: Option<&str>, hovered_monitor: Option<&str>) -> String {
    let window = hovered_window.map(|s| truncate_ellipsis(s, WINDOW_TITLE_MAX));
    let monitor = hovered_monitor.map(|s| truncate_ellipsis(s, MONITOR_NAME_MAX));
    template
        .replace("{window}", window.as_deref().unwrap_or("n/a"))
        .replace("{monitor}", monitor.as_deref().unwrap_or("n/a"))
}
