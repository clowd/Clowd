//! Code shared by Clowd's Rust binaries — the capture overlay
//! (`clowd_capture_wgpu`), the scrolling-capture driver
//! (`clowd_scroll_driver`) and the text recognizer (`clowd_ocr`).
//!
//! The bar for living here is that **two processes must agree**, so a change
//! made in one place and not the other would be a bug:
//!
//! - [`geometry`] — the virtual-desktop coordinate space every capture
//!   coordinate, CLI flag and Win32 call is expressed in.
//! - [`session`] — the `session.json` contract, which `Clowd.Ui` reads
//!   (`SessionInfo`, MIGRATION.md §2.11) and both binaries write.
//! - [`ocr`] — the recognition request/response contract the overlay and
//!   `clowd_ocr` speak across their process boundary.
//! - [`exit`] — the process exit codes the shell distinguishes.
//! - [`telemetry`] — one Sentry project, one release name, one opt-out
//!   variable across every process.
//!
//! Anything that only one binary needs stays in that binary. `app.manifest`
//! lives in this crate's directory for the same reason its Rust neighbours
//! do; each binary's `build.rs` embeds it.

pub mod exit;
pub mod geometry;
pub mod ocr;
pub mod session;
pub mod telemetry;
