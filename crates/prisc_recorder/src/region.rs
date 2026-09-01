//! Pure region math (DESIGN §2.3) — moved to the shared `obs-platform` crate
//! (SHARE_REGION_PLAN §4.3); re-exported here so every existing
//! `crate::region::*` path keeps resolving.

pub use obs_platform::region::*;
