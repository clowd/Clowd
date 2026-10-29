//! The OS halves of the exports. Every `#[cfg]` that gates an item lives here or below, never
//! under `src/ffi/` (csbindgen parses `src/ffi` and never evaluates cfg). A platform without the
//! concept is handled by the `#[cfg(not(...))]` block inside the export body, so there is no
//! "unsupported" tree here; Linux compiles this whole directory away.

#[cfg(target_os = "macos")]
pub(crate) mod macos;
#[cfg(windows)]
pub(crate) mod windows;
