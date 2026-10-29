//! Code shared by Prisc's Rust binaries (the capture overlay
//! `prisc_capture`, the scrolling-capture driver `prisc_scroll_driver`, the AI
//! inference binary `prisc_ai` and, through `prisc_obs_platform`, the recorder
//! stack) and by Prisc.Ui and Prisc.VideoRender through `prisc_interop`, which
//! links it without the `telemetry` feature.
//!
//! The bar for living here is that **two processes must agree**, so a change
//! made in one place and not the other would be a bug:
//!
//! - [`geometry`] — the virtual-desktop coordinate space every capture
//!   coordinate, CLI flag and Win32 call is expressed in.
//! - [`session`] — the `session.json` contract, which `Prisc.Ui` reads
//!   (`SessionInfo`) and both binaries write.
//! - [`ocr`] — the recognition request/response contract the overlay and
//!   `prisc_ai ocr` speak across their process boundary.
//! - [`exit`] — the process exit codes the shell distinguishes.
//! - [`telemetry`] — one Sentry project, one release name, one opt-out
//!   variable across every process.
//! - [`desktop`]: the window list, monitors (`screens`), screen grab
//!   (`grab`) and cursor, which the overlay, the scrolling driver, the
//!   recorder and Prisc.Ui must all read the same way, plus the macOS overlay
//!   window level and collection behaviour (`overlay`).
//! - [`color`], [`share_region`], [`encoding`]: pure logic that a Rust
//!   helper and Prisc.Ui (through prisc_interop) must compute identically.
//! - [`contract`]: typed constant tables both sides read; prisc_codegen
//!   renders them into C# (`cargo codegen`).
//!
//! Anything that only one binary needs stays in that binary. `app.manifest`
//! lives in this crate's directory for the same reason its Rust neighbors
//! do; each binary's `build.rs` embeds it.

pub mod color;
pub mod contract;
#[cfg(feature = "desktop")]
pub mod desktop;
pub mod encoding;
pub mod exit;
pub mod geometry;
pub mod ocr;
pub mod session;
pub mod share_region;
#[cfg(feature = "telemetry")]
pub mod telemetry;
