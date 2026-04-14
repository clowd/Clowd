//! Concrete UI components that live on top of the generic framework in
//! `ui::{component, host, backend, animation}`. Each submodule is a
//! drop-in component — to add one, create a new subdir and register it
//! in `App::new`.

pub mod panel;
pub mod tips;
