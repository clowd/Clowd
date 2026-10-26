//! The OCR overlay: the scanning sweep band, the reveal wave it turns
//! into, and the text bubbles that rise under it.
//!
//! Everything lives in [`show`] — the per-host rule, the pure geometry
//! that turns a band position into strips, and the painting — because the
//! bubbles' pills are sized from measured galleys, which only exist
//! inside a pass.

pub mod show;
