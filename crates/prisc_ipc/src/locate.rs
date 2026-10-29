//! Rust-side helper lookup (HELPER_PROTOCOL.md §7.1, the part prisc_capture needs to find
//! prisc_ai). The C# `HelperLocator` is the full version.

use std::ffi::OsStr;
use std::path::{Path, PathBuf};

/// A path list (`;` on Windows, `:` elsewhere) of directories probed before the executable's
/// own. The only helper-lookup variable there is.
pub const HELPERS_DIR_VAR: &str = crate::env::HELPERS_DIR;

/// The executable `stem` (plus `.exe` on Windows): each directory of `$PRISC_HELPERS_DIR` in
/// order, then the directory of the running executable. `None` when none of them has it.
pub fn sibling(stem: &str) -> Option<PathBuf> {
    let file = format!("{stem}{}", std::env::consts::EXE_SUFFIX);
    let helpers_dir = std::env::var_os(HELPERS_DIR_VAR);
    let exe = std::env::current_exe().ok();
    probe(&file, helpers_dir.as_deref(), exe.as_deref().and_then(Path::parent))
}

fn probe(file: &str, helpers_dir: Option<&OsStr>, exe_dir: Option<&Path>) -> Option<PathBuf> {
    helpers_dir
        .into_iter()
        .flat_map(std::env::split_paths)
        .filter(|dir| !dir.as_os_str().is_empty())
        .chain(exe_dir.map(Path::to_path_buf))
        .map(|dir| dir.join(file))
        .find(|path| path.is_file())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("prisc_ipc_locate_{}_{name}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn probes_the_path_list_in_order_then_the_exe_dir() {
        let (a, b, exe) = (temp_dir("a"), temp_dir("b"), temp_dir("exe"));
        let list = std::env::join_paths([&a, &b]).unwrap();
        assert_eq!(probe("helper", Some(&list), Some(&exe)), None);

        std::fs::write(exe.join("helper"), b"").unwrap();
        assert_eq!(probe("helper", Some(&list), Some(&exe)), Some(exe.join("helper")));
        assert_eq!(probe("helper", None, Some(&exe)), Some(exe.join("helper")));

        std::fs::write(b.join("helper"), b"").unwrap();
        assert_eq!(probe("helper", Some(&list), Some(&exe)), Some(b.join("helper")));

        std::fs::write(a.join("helper"), b"").unwrap();
        assert_eq!(probe("helper", Some(&list), Some(&exe)), Some(a.join("helper")));

        // A directory named like the helper is not the helper.
        std::fs::create_dir(a.join("other")).unwrap();
        assert_eq!(probe("other", Some(&list), None), None);

        for dir in [a, b, exe] {
            let _ = std::fs::remove_dir_all(dir);
        }
    }
}
