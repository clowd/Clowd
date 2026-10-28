//! Process exit codes the parent distinguishes. Defined once in `prisc_ipc` (HELPER_PROTOCOL.md
//! §5, mirrored into C# as the generated `HelperExit`) and re-exported here so existing paths keep
//! compiling.

pub use prisc_ipc::exit::*;
