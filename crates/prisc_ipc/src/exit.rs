//! Process exit codes the parent distinguishes (HELPER_PROTOCOL.md §5).
//!
//! A code that means one thing in one helper and something else in another would send the parent
//! down the wrong recovery path, so they are defined once here. The C# mirror is the generated
//! `HelperExit` in `ui/Prisc.Ipc`.

/// Every outcome reported on the protocol, cancel included.
pub const OK: i32 = 0;

/// Fatal failure; a fatal `error` was emitted (best effort).
pub const FAILED: i32 = 1;

/// Bad command line: clap's own rejection (no protocol line), or a post-`hello`
/// `error{code:"usage",fatal:true}`.
pub const USAGE: i32 = 2;

/// "The OS has not granted this process permission to capture the screen" (macOS Screen
/// Recording). Distinct from the generic failure exit so the parent can tell a missing permission
/// apart from a crash and offer the user the System Settings route instead of a stack trace.
pub const NO_SCREEN_PERMISSION: i32 = 3;

/// "The desktop screenshot itself failed" (e.g. every CGDisplayCreateImage returned null despite
/// the TCC preflight passing). Kept distinct from a panic so the parent's crash report carries the
/// reason from stderr/capture.log rather than a stack trace.
pub const CAPTURE_FAILED: i32 = 4;

/// The hard-exit timer fired: the helper was still alive its quit grace after `quit`, stdin EOF,
/// a signal or a dead stdout.
pub const QUIT_TIMEOUT: i32 = 6;

/// "Inference is unavailable on this install" (`prisc_ai`). The binary links ONNX Runtime
/// statically and no longer emits this at startup; the code stays reserved because an older
/// shipped binary may still exit with it, and the parent falls back to raw passthrough on it.
pub const INFERENCE_UNAVAILABLE: i32 = 7;

/// Rust's own exit code for a panic. Never passed to an exit call; named so the parent can
/// recognise it.
pub const RUST_PANIC: i32 = 101;

/// Every code above, by name, for the C# mirror.
pub const ALL: &[(&str, i32)] = &[
    ("OK", OK),
    ("FAILED", FAILED),
    ("USAGE", USAGE),
    ("NO_SCREEN_PERMISSION", NO_SCREEN_PERMISSION),
    ("CAPTURE_FAILED", CAPTURE_FAILED),
    ("QUIT_TIMEOUT", QUIT_TIMEOUT),
    ("INFERENCE_UNAVAILABLE", INFERENCE_UNAVAILABLE),
    ("RUST_PANIC", RUST_PANIC),
];
