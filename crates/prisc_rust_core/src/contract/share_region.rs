//! The share-region size rules, shared by prisc_share_region and Prisc.Ui's resize drag
//! (C#: `ShareRegionGeometry`).

crate::contract! {
    /// The floor on a shared region's width and height. A degenerate or one-pixel region would give
    /// a canvas the swapchain and the scene maths cannot meaningfully represent (and, on Windows, a
    /// mirror window smaller than its own minimum tracking size). Even, so rounding a size down to
    /// even can never take it below the floor.
    pub const MIN_REGION: i32 = 64;
}
