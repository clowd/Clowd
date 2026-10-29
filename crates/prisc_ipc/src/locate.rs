//! Rust-side helper lookup (HELPER_PROTOCOL.md §7.1, the part prisc_capture needs to find
//! prisc_ai). The C# `HelperLocator` is the full version.

use std::ffi::OsStr;
use std::path::{Path, PathBuf};

/// A path list (`;` on Windows, `:` elsewhere) of directories probed before the executable's
/// own. The only helper-lookup variable there is.
pub const HELPERS_DIR_VAR: &str = crate::env::HELPERS_DIR;

/// The AI inference helper (OCR, mattes, denoise). It speaks no line catalog
/// (HELPER_PROTOCOL.md section 4.6), so no catalog names it.
pub const AI_HELPER: &str = "prisc_ai";

/// Every helper executable stem the apps locate and spawn, as its C# constant name and stem:
/// each catalog's helpers in catalog order, then [`AI_HELPER`]. Generated into the C#
/// `HelperNames` class, so a stem is spelled once.
pub fn helper_names() -> Vec<(String, &'static str)> {
    let stems = crate::catalog::ALL
        .iter()
        .flat_map(|spec| spec.helpers.iter().copied())
        .chain([AI_HELPER]);
    let mut names: Vec<(String, &'static str)> = Vec::new();
    for stem in stems {
        if names.iter().all(|(_, known)| *known != stem) {
            names.push((const_name(stem), stem));
        }
    }
    names
}

/// `prisc_scroll_driver` -> `ScrollDriver`, `Prisc.VideoRender` -> `VideoRender`.
fn const_name(stem: &str) -> String {
    let bare = stem
        .strip_prefix("prisc_")
        .or_else(|| stem.strip_prefix("Prisc."))
        .unwrap_or(stem);
    bare.split('_')
        .map(|word| {
            let mut chars = word.chars();
            chars
                .next()
                .map(|first| first.to_ascii_uppercase().to_string() + chars.as_str())
                .unwrap_or_default()
        })
        .collect()
}

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

    #[test]
    fn helper_names_cover_every_catalog_helper_and_the_ai_helper_once() {
        let names = helper_names();
        let stems: Vec<&str> = names.iter().map(|(_, stem)| *stem).collect();
        assert_eq!(
            stems,
            [
                "prisc_recorder",
                "prisc_share_region",
                "prisc_vid2gif",
                "Prisc.VideoRender",
                "prisc_scroll_driver",
                "prisc_capture",
                "prisc_ai"
            ]
        );
        let consts: Vec<&str> = names
            .iter()
            .map(|(name, _)| name.as_str())
            .collect();
        assert_eq!(
            consts,
            ["Recorder", "ShareRegion", "Vid2gif", "VideoRender", "ScrollDriver", "Capture", "Ai"]
        );
    }

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
