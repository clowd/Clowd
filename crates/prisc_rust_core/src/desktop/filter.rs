//! The platform-neutral half of the window filter: the parts that are plain
//! data, kept out of the platform modules so their tests run on every host.

/// Minimum width and height for a top-level window to count as one, in
/// physical pixels on Windows. Below this a "window" is a helper surface no
/// one would aim at or could see in a recording. macOS callers apply it in
/// their own space (the overlay after converting to physical pixels, the
/// recorder in points).
pub const MIN_WINDOW_SIZE: i32 = 25;

/// Win32 window classes that are never a real application window: the desktop
/// itself, the shell chrome, and the immersive-shell surfaces (Start, Search,
/// taskbar hover previews). Each entry is here because it showed up as a
/// phantom window in practice.
///
/// Sorted for [`is_shell_window_class`]'s `binary_search` (ASCII,
/// case-sensitive): `Shell_Dialog` sorts before `Shell_Dim` because 'a' < 'm'.
pub const SHELL_WINDOW_CLASSES: &[&str] = &[
    "ApplicationManager_ImmersiveShellWindow",
    "EdgeUiInputWndClass",
    "Immersive Chrome Container",
    "ImmersiveBackgroundWindow",
    "ImmersiveLauncher",
    "LauncherTipWndClass",
    "MetroGhostWindow",
    "ModeInputWnd",
    "NativeHWNDHost",
    "Progman",
    "SearchPane",
    "Shell_Dialog",
    "Shell_Dim",
    "Shell_TrayWnd",
    "Snapped Desktop",
    "TaskListThumbnailWnd",
    "Touch Tooltip Window",
    "Windows.UI.Core.CoreWindow",
    "WorkerW",
];

/// Whether `class` is one of [`SHELL_WINDOW_CLASSES`].
pub fn is_shell_window_class(class: &str) -> bool {
    SHELL_WINDOW_CLASSES
        .binary_search(&class)
        .is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_shell_class_list_is_sorted_for_binary_search() {
        // `binary_search` silently misses on an unsorted slice: a class added
        // in the wrong place would just stop being filtered (the overlay's
        // old copy of this list lost `Shell_Dim` exactly that way).
        let mut sorted = SHELL_WINDOW_CLASSES.to_vec();
        sorted.sort_unstable();
        assert_eq!(sorted, SHELL_WINDOW_CLASSES);
        for class in SHELL_WINDOW_CLASSES {
            assert!(is_shell_window_class(class), "{class}");
        }
    }

    #[test]
    fn ordinary_classes_are_not_shell_classes() {
        for class in ["ApplicationFrameWindow", "Chrome_WidgetWin_1", "Notepad", "", "shell_traywnd"] {
            assert!(!is_shell_window_class(class), "{class}");
        }
    }
}
