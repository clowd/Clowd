//! Launching Clowd.Ui.exe through Explorer instead of from this DLL's host process.
//!
//! This DLL runs inside a dllhost.exe surrogate that carries the sparse package's
//! identity (Clowd.ShellExtension). On the Windows 11 builds this was measured on, a
//! process that a packaged process creates from inside the package's external location —
//! the whole Clowd install root, so Clowd.Ui.exe included — gets that identity too, and
//! neither the desktop-app breakaway policy nor which exe the manifest declares changes
//! that (both were tried and shipped, clowd/Clowd#83). What does work is having an
//! unpackaged process do the launching: the desktop's shell view lives in explorer.exe,
//! and its automation object's `IShellDispatch2::ShellExecute` runs the CreateProcess over
//! there, so the app comes up exactly as it does from a shortcut. Raymond Chen documents
//! the walk from `IShellWindows` to that object (oldnewthing, 2013-11-18).

use std::path::Path;

use windows::core::{Interface, BSTR};
use windows::Win32::System::Com::{CoCreateInstance, IDispatch, IServiceProvider, CLSCTX_LOCAL_SERVER};
use windows::Win32::System::Variant::VARIANT;
use windows::Win32::UI::Shell::{
    IShellBrowser, IShellDispatch2, IShellFolderViewDual, IShellView, IShellWindows, SID_STopLevelBrowser, ShellWindows, SVGIO_BACKGROUND,
    SWC_DESKTOP, SWFO_NEEDDISPATCH,
};

// CSIDL_DESKTOP: the location FindWindowSW matches the desktop window against
const CSIDL_DESKTOP: i32 = 0;

/// The desktop's `IShellDispatch2`, i.e. a proxy to an object inside explorer.exe.
///
/// Connecting and executing are deliberately separate steps: a failure to *reach*
/// Explorer means nothing was launched and the caller may fall back to launching itself,
/// whereas a failure reported by `ShellExecute` may have come back after Explorer already
/// created the process, and launching again would upload the same selection twice.
pub struct ExplorerShell {
    dispatch: IShellDispatch2,
}

impl ExplorerShell {
    /// Fails when there is no desktop shell view to talk to — Explorer not running (or
    /// restarting), or running as a different user.
    pub fn connect() -> windows::core::Result<Self> {
        unsafe {
            let shell_windows: IShellWindows = CoCreateInstance(&ShellWindows, None, CLSCTX_LOCAL_SERVER)?;
            let mut hwnd = 0i32;
            let desktop: IDispatch = shell_windows.FindWindowSW(
                &VARIANT::from(CSIDL_DESKTOP),
                &VARIANT::default(),
                SWC_DESKTOP,
                &mut hwnd,
                SWFO_NEEDDISPATCH,
            )?;
            let provider: IServiceProvider = desktop.cast()?;
            let browser: IShellBrowser = provider.QueryService(&SID_STopLevelBrowser)?;
            let view: IShellView = browser.QueryActiveShellView()?;
            let folder_view: IDispatch = view.GetItemObject(SVGIO_BACKGROUND)?;
            let folder_view: IShellFolderViewDual = folder_view.cast()?;
            let dispatch: IShellDispatch2 = folder_view.Application()?.cast()?;
            Ok(Self {
                dispatch,
            })
        }
    }

    /// Ask explorer.exe to run `exe` with `arguments` (the command-line tail, already
    /// quoted) from `cwd`. Returns as soon as Explorer has accepted the request; there is
    /// no handle to the child.
    pub fn execute(&self, exe: &Path, arguments: &str, cwd: &Path) -> windows::core::Result<()> {
        unsafe {
            self.dispatch.ShellExecute(
                &BSTR::from(exe.to_string_lossy().as_ref()),
                &VARIANT::from(arguments),
                &VARIANT::from(cwd.to_string_lossy().as_ref()),
                &VARIANT::from("open"),
                // SW_SHOWNORMAL
                &VARIANT::from(1i32),
            )
        }
    }
}
