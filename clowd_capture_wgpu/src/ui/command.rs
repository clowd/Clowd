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
