// Win11 "Upload with Clowd" modern context-menu handler: an IExplorerCommand COM
// server registered through a sparse MSIX package (see clowd_shell_ext/msix). The
// DLL is loaded into dllhost.exe, so it does the absolute minimum — resolve
// Clowd.Ui.exe relative to its own location and spawn it with the selected paths.
// No logging, telemetry, or network in here.

// invoke's callers are all behind cfg(windows); elsewhere the module only exists
// so its unit tests still build and run on any platform
#![cfg_attr(not(windows), allow(dead_code))]

mod invoke;

#[cfg(windows)]
mod com_server;
