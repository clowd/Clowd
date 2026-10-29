//! The background matte analysis sizes, shared by the editor that scales the frames and prisc_ai
//! that infers on them (C#: `MatteGenerator`).

crate::contract! {
    /// The analysis resolution's cap on the shorter side: large enough for a clean silhouette,
    /// small enough that inference keeps up with a screen recording's frame count.
    pub const MAX_ANALYSIS_SIDE: i32 = 540;
    /// The short side the matting model works at internally: its downsample ratio is this over the
    /// frame's short side, capped at 1 (0.5 at the analysis cap).
    pub const MODEL_SHORT_SIDE: i32 = 270;
}
