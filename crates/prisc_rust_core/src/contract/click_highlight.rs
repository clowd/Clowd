//! The recorded click highlight's animation, shared by the recorder's live tracker and the video
//! editor's composition (C#: `ClickHighlight`).

crate::contract! {
    /// How long one click's animation runs, in milliseconds.
    pub const DURATION_MS: f64 = 400.0;
    /// Peak opacity as a fraction: never fully opaque, so what is under the highlight stays
    /// readable.
    pub const MAX_OPACITY: f64 = 0.85;
    /// Radius at the moment of the click, in density-independent units (scaled by the monitor's
    /// DPI at draw time).
    pub const RADIUS_START_DIP: f64 = 10.0;
    /// How much the radius grows over the animation, in density-independent units.
    pub const RADIUS_GROWTH_DIP: f64 = 30.0;
}
