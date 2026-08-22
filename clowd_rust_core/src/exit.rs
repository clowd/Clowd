//! Process exit codes the shell distinguishes.
//!
//! Every one of these has a named counterpart on the C# side; the comments
//! say which. A code that means one thing in one binary and something else
//! in another would send the shell down the wrong recovery path, so they
//! are defined once here rather than per-binary.
//!
//! 0 is success and 101 is Rust's own panic code — neither needs a name.

/// "The OS has not granted this process permission to capture the screen"
/// (macOS Screen Recording). Distinct from the generic failure exit so the
/// shell can tell a missing permission apart from a crash and offer the
/// user the System Settings route instead of a stack trace — matches
/// `ScreenCaptureService.cs`'s `ExitCodeNoScreenPermission`.
pub const NO_SCREEN_PERMISSION: i32 = 3;

/// "The desktop screenshot itself failed" (e.g. every
/// CGDisplayCreateImage returned null despite the TCC preflight passing).
/// Kept distinct from a panic-driven exit so the shell's crash report
/// carries the reason from stderr/capture.log rather than a stack trace.
/// Also what the scrolling-capture driver exits with on a platform where
/// it is not implemented.
pub const CAPTURE_FAILED: i32 = 4;
