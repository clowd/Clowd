//! macOS: system preferences (`prisc_system_animations_enabled`).

use objc2_app_kit::NSWorkspace;

/// The inverse of Reduce Motion (System Settings, Accessibility, Display). `NSWorkspace` is safe to
/// ask from any thread.
pub(crate) fn animations_enabled() -> bool {
    !NSWorkspace::sharedWorkspace().accessibilityDisplayShouldReduceMotion()
}
