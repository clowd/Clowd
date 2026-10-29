//! The small OS primitives several Prisc crates need and must answer the same way.
//!
//! - [`permissions`]: the macOS Screen Recording and Accessibility checks, with one rule for
//!   Accessibility everywhere (`AXIsProcessTrusted && CGPreflightPostEventAccess`) and an
//!   idempotent request that re-reads with the same rule.
//! - [`input`]: whether a key or mouse button is physically down (Windows VK, mouse-button VKs
//!   included, or macOS CGKeyCode), and whether Secure Event Input is on.
//! - [`foreground`]: the Windows foreground grant (`AllowSetForegroundWindow`).
//!
//! Every function exists on every OS (Linux included) and never prompts unless its name says
//! `request`. Where a concept does not exist on an OS the answer is the truthful one
//! ([`permissions::State::NotApplicable`], `None`, `false`), so callers need no cfg of their own.

pub mod foreground;
pub mod input;
pub mod permissions;
