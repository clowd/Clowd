//! The area indicator: the "W x H" pill that sits at the bottom of the
//! selection while the user is dragging one out.
//!
//! Everything it needs is in [`show`] — the per-host rule that decides
//! whether this monitor draws it, the pure placement function, and the
//! painting — because the placement depends on the measured label, which
//! only exists inside a pass.

pub mod show;
