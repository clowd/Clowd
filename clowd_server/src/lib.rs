//! clwd.app streaming upload relay — Cloudflare Workers (Rust/WASM).
//!
//! One Worker script: a `fetch` handler (router + control plane + live tail) and
//! a `queue` handler (destination relay). One Durable Object class,
//! `UploadSession`, holds per-upload state. See `REFACTOR.md` for the full spec.
//!
//! ## Module layout
//! Pure, host-testable logic (no `worker` dependency) lives in the modules below
//! and runs under a plain `cargo test`. Everything that touches the Workers
//! runtime is gated behind `#[cfg(target_arch = "wasm32")]`.

// Pure logic — compiled on every target, unit-tested natively.
pub mod azure;
pub mod chunkplan;
pub mod consts;
pub mod ids;
pub mod manifest;
pub mod model;
pub mod s3;
pub mod sanitize;

// Workers runtime glue — wasm only.
#[cfg(target_arch = "wasm32")]
mod dest;
#[cfg(target_arch = "wasm32")]
mod relay;
#[cfg(target_arch = "wasm32")]
mod router;
#[cfg(target_arch = "wasm32")]
mod session;
#[cfg(target_arch = "wasm32")]
mod wasm_util;

#[cfg(target_arch = "wasm32")]
pub use session::UploadSession;
