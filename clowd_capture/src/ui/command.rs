//! Shared vocabulary of commands a component can emit to request
//! app-level behavior.
//!
//! This is the *only* type-specific coupling between components and the
//! app: a component turns a user interaction (button click, key press,
//! …) into a `Command` variant; the app matches on it and performs the
//! effect. Any component can emit any command — they're not owned by
//! a specific component.
//!
//! Adding a new command = add a variant here + one arm in the app's
//! command dispatcher.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Command {
    /// Upload the current selection.
    Upload,
    /// Open the selection in the editor.
    Edit,
    /// Start video capture on the current selection.
    Video,
    /// Enter scroll-point pick mode for the current selection: the next
    /// click inside it hands the region + point off to the scrolling
    /// capture driver.
    ///
    /// The variant itself is platform-independent — the dispatcher arm
    /// compiles everywhere — but the only thing that *emits* it is the
    /// SCROLL panel button, which is `#[cfg(windows)]` because the driver
    /// is. On macOS nothing constructs it, and `-D warnings` would call
    /// that dead code; the arm is kept rather than cfg'd out so the two
    /// platforms share one dispatcher.
    #[cfg_attr(not(windows), allow(dead_code))]
    ScrollCapture,
    /// Run OCR over the current selection and lift the recognised lines
    /// off the desktop. The PaddleOCR backend is platform-independent, so
    /// unlike `ScrollCapture` this is emitted on every platform.
    Ocr,
    /// Copy the recognised text to the clipboard.
    OcrCopy,
    /// Open a web search for the recognised text.
    OcrSearch,
    /// Upload the recognised text as a paste.
    OcrUpload,
    /// Leave OCR mode and return to the capture panel, keeping the
    /// selection.
    OcrBack,
    /// Copy the selection to the clipboard.
    Copy,
    /// Save the selection to a file.
    Save,
    /// Report the pixel colour under the cursor to the shell (H in
    /// crosshair mode, before a selection exists).
    SelectColor,
    /// Reset the current selection (return to draw mode).
    Reset,
    /// Exit the capture tool.
    Exit,
}
