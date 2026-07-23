//! Concrete UI components. Each subdir holds the pure layout/model logic
//! for one component. Per-monitor rendering lives in [`crate::ui::gpu`];
//! visibility rules in [`crate::ui::shared`].

pub mod debug;
pub mod hints;
pub mod panel;
pub mod tips;
