//! Pure region math (DESIGN §2.3) — moved to the shared `prisc_obs_platform` crate
//! (SHARE_REGION_PLAN §4.3); re-exported here so every existing
//! `crate::region::*` path keeps resolving.

pub use prisc_obs_platform::region::*;
