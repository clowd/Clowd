//! Every export this library has, one file per area. Every area file is registered here from the
//! first commit, so the generated bindings are complete and the area work packages fill bodies
//! against fixed signatures.
//!
//! No `#[cfg]` gates an item under this directory (README D1): csbindgen and prisc_codegen's lint
//! parse these files with syn and never evaluate cfg. Platform switches are cfg'd blocks inside an
//! export body, whose `#[cfg(not(...))]` arm answers `PRISC_E_UNSUPPORTED` (or the truthful off-OS
//! answer). prisc_codegen finds the files by listing this directory, so adding a file here is all
//! a new area needs.

// infra (README section 4.1)
pub mod error;
pub mod log;
pub mod selftest;
pub mod status;
pub mod string;
pub mod types;
pub mod version;

// desktop (4.2)
pub mod idle;
pub mod pixels;
pub mod screens;
pub mod system;

// window chrome and cursor (4.3)
pub mod cursor;
pub mod window;

// IPC child and process (4.4)
pub mod ipc;
pub mod process;

// macOS app integration and permissions (4.5)
pub mod app;
pub mod permissions;
pub mod services;

// devices and audio (4.6)
pub mod audio_input;
pub mod audio_output;
pub mod devices;

// GPU (4.7)
pub mod gpu;

// hotkey hook (4.8)
pub mod hotkeys;

// gestures and keyboard (4.9)
pub mod gesture;
pub mod keyboard;

// pure shared logic (4.10)
pub mod color;
pub mod encoding;
pub mod share_region;
