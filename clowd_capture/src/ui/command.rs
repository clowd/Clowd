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
    ScrollCapture,
    /// Run OCR over the current selection and lift the recognized lines
    /// off the desktop.
    Ocr,
    /// Copy the recognized text to the clipboard.
    OcrCopy,
    /// Open a web search for the recognized text.
    OcrSearch,
    /// Upload the recognized text as a paste.
    OcrUpload,
    /// Leave OCR mode and return to the capture panel, keeping the
    /// selection.
    OcrBack,
    /// Copy the selection to the clipboard.
    Copy,
    /// Save the selection to a file.
    Save,
    /// Report the pixel color under the cursor to the shell (H in
    /// crosshair mode, before a selection exists).
    SelectColor,
    /// Reset the current selection (return to draw mode).
    Reset,
    /// Exit the capture tool.
    Exit,
}
