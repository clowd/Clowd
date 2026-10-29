//! macOS half of `prisc_process_prepare_ffmpeg_dir`: loads the FFmpeg dylibs and the siblings they
//! link against, by absolute path, before the bindings go looking for them.
//!
//! The recorder payload is linked the way a self-contained app bundle is: every dylib in it has an
//! `@rpath` install name and asks for its siblings the same way (libavformat wants
//! `@rpath/libavcodec.dylib`, `@rpath/libx264.dylib`, `@rpath/libsrt.dylib` and so on). The only
//! `LC_RPATH` in the payload is on the `prisc_recorder` executable (`@executable_path/Frameworks`);
//! no dylib carries one. When another program dlopens one of them by absolute path, dyld expands
//! `@rpath` against the images doing the loading, finds no rpath anywhere in that chain, and fails.
//!
//! `DYLD_LIBRARY_PATH` is read at exec and stripped by the hardened runtime, and re-linking the
//! payload would mean re-signing every dylib in another repository's artifact. What works is
//! loading them first, by a path dyld does not have to resolve: an image is registered under its
//! own install name, so a later `@rpath/libavcodec.dylib` matches the loaded image and is never
//! path-resolved at all.
//!
//! Only the closure over FFmpeg's own dependencies is loaded, never the whole directory: it also
//! holds OBS's runtime, graphics backends and a FreeType that has no business in a process that
//! already has Skia's. The closure is read from each file's load commands, so a payload that picks
//! up a new codec dependency keeps working without a list to edit. (Ported from the C#
//! MacDylibPreloader, whose tests are the ones below.)

use std::collections::{HashSet, VecDeque};
use std::ffi::CString;
use std::os::unix::ffi::OsStrExt;
use std::path::{Path, PathBuf};

/// The libraries FFmpeg.AutoGen opens, the roots of the closure. Unversioned, which is how their
/// install names read and so how their dependents ask for them: the directory holds the same code
/// as `libavcodec.dylib`, `libavcodec.62.dylib` and `libavcodec.62.28.100.dylib` (the zip turns
/// symlinks into copies), and loading two of those would put one library in the process twice.
pub(crate) const ROOTS: [&str; 7] = [
    "libavutil.dylib",
    "libswresample.dylib",
    "libswscale.dylib",
    "libavcodec.dylib",
    "libavformat.dylib",
    "libavfilter.dylib",
    "libavdevice.dylib",
];

/// Loads the closure of [`ROOTS`] in `directory`; the number of images `dlopen` loaded. Order is
/// not computed, only converged on: load what will load and go round again while a pass loaded
/// anything, since a failure means a dependency a later pass supplies. Cheaper than a topological
/// sort and immune to cycles. Best-effort: what never loads is left for the real load to fail on
/// with its own message, and a directory without FFmpeg loads nothing.
pub(crate) fn prepare(directory: &Path) -> u32 {
    if !directory.is_dir() {
        return 0;
    }
    let mut pending = closure(directory);
    let mut loaded = 0u32;
    loop {
        let before = pending.len();
        pending.retain(|path| !load(path));
        loaded += (before - pending.len()) as u32;
        if pending.is_empty() || pending.len() == before {
            return loaded;
        }
    }
}

/// `dlopen(path, RTLD_LAZY)`, as .NET's `NativeLibrary.TryLoad` did. The handle is never closed:
/// the image must stay registered for the rest of the process.
fn load(path: &Path) -> bool {
    let Ok(c_path) = CString::new(path.as_os_str().as_bytes()) else {
        return false;
    };
    // SAFETY: a NUL-terminated path; dlopen runs the image's initialisers, which is the point.
    !unsafe { libc::dlopen(c_path.as_ptr(), libc::RTLD_LAZY) }.is_null()
}

/// Absolute paths of the [`ROOTS`] present in `directory` plus everything they reach through
/// `@rpath`, breadth-first, each file once.
pub(crate) fn closure(directory: &Path) -> Vec<PathBuf> {
    let mut seen: HashSet<String> = HashSet::new();
    let mut queue: VecDeque<String> = VecDeque::new();
    for root in ROOTS {
        if seen.insert(root.to_owned()) {
            queue.push_back(root.to_owned());
        }
    }
    let mut closure = Vec::new();
    while let Some(name) = queue.pop_front() {
        let path = directory.join(&name);
        if !path.is_file() {
            continue; // a root this payload does not ship, or a dependency that lives elsewhere
        }
        for dependency in rpath_dependencies(&path) {
            if seen.insert(dependency.clone()) {
                queue.push_back(dependency);
            }
        }
        closure.push(path);
    }
    closure
}

/// The `@rpath/...` libraries `path` links against, as bare file names. Absolute dependencies
/// (`/usr/lib/...`, system frameworks) are skipped: dyld always resolved those.
pub(crate) fn rpath_dependencies(path: &Path) -> Vec<String> {
    dependencies(path)
        .into_iter()
        .filter_map(|d| d.strip_prefix("@rpath/").map(str::to_owned))
        .collect()
}

/// Every library `path` links against, exactly as its load commands name them. Empty for anything
/// that is not a Mach-O this reads, or cannot be read. `LC_ID_DYLIB` is not a dependency: it is the
/// file's own install name, which for a copy of a versioned library is itself an `@rpath` string.
pub(crate) fn dependencies(path: &Path) -> Vec<String> {
    let Ok(bytes) = std::fs::read(path) else {
        return Vec::new();
    };
    let mut names = Vec::new();
    read_into(&bytes, &mut names);
    names
}

const FAT_MAGIC: u32 = 0xCAFE_BABE;
const FAT_MAGIC_64: u32 = 0xCAFE_BABF;
const MH_MAGIC_64: u32 = 0xFEED_FACF;
const LC_LOAD_DYLIB: u32 = 0x0000_000C;
const LC_LOAD_WEAK_DYLIB: u32 = 0x8000_0018;
const LC_REEXPORT_DYLIB: u32 = 0x8000_001F;
/// `sizeof(mach_header_64)`.
const MACH_HEADER_64: usize = 32;

fn u32_be(bytes: &[u8], at: usize) -> Option<u32> {
    Some(u32::from_be_bytes(
        bytes
            .get(at..at.checked_add(4)?)?
            .try_into()
            .ok()?,
    ))
}

fn u64_be(bytes: &[u8], at: usize) -> Option<u64> {
    Some(u64::from_be_bytes(
        bytes
            .get(at..at.checked_add(8)?)?
            .try_into()
            .ok()?,
    ))
}

fn u32_le(bytes: &[u8], at: usize) -> Option<u32> {
    Some(u32::from_le_bytes(
        bytes
            .get(at..at.checked_add(4)?)?
            .try_into()
            .ok()?,
    ))
}

/// A universal file's first slice (every slice lists the same dependencies), or a thin file.
fn read_into(bytes: &[u8], names: &mut Vec<String>) {
    let Some(magic) = u32_be(bytes, 0) else {
        return;
    };
    if magic == FAT_MAGIC || magic == FAT_MAGIC_64 {
        if u32_be(bytes, 4).unwrap_or(0) == 0 {
            return;
        }
        // fat_header is magic + nfat_arch; fat_arch is cputype, cpusubtype, offset, ... and
        // fat_arch_64 widens the offset to 8 bytes.
        let offset = if magic == FAT_MAGIC {
            u32_be(bytes, 16).map(u64::from)
        } else {
            u64_be(bytes, 16)
        };
        if let Some(slice) = offset
            .and_then(|o| usize::try_from(o).ok())
            .filter(|&o| o > 0)
            .and_then(|o| bytes.get(o..))
        {
            read_thin(slice, names);
        }
        return;
    }
    read_thin(bytes, names);
}

/// A little-endian 64-bit Mach-O (every slice anything here ships as); 32-bit ones are ignored.
fn read_thin(bytes: &[u8], names: &mut Vec<String>) {
    if bytes.len() < MACH_HEADER_64 || u32_le(bytes, 0) != Some(MH_MAGIC_64) {
        return;
    }
    let Some(count) = u32_le(bytes, 16) else {
        return;
    };
    let mut cursor = MACH_HEADER_64;
    for _ in 0..count {
        let (Some(command), Some(size)) = (u32_le(bytes, cursor), u32_le(bytes, cursor + 4)) else {
            return;
        };
        let size = size as usize;
        let Some(end) = cursor
            .checked_add(size)
            .filter(|&end| size >= 8 && end <= bytes.len())
        else {
            return;
        };
        if matches!(command, LC_LOAD_DYLIB | LC_LOAD_WEAK_DYLIB | LC_REEXPORT_DYLIB) {
            // dylib_command: cmd, cmdsize, then a dylib whose first member is an lc_str, the
            // offset from the command's start of a NUL-terminated name in the command's tail.
            if let Some(name_offset) = u32_le(bytes, cursor + 8)
                .map(|o| o as usize)
                .filter(|&o| o > 0 && o < size)
            {
                let tail = &bytes[cursor + name_offset..end];
                let name = tail
                    .split(|&b| b == 0)
                    .next()
                    .unwrap_or(tail);
                names.push(String::from_utf8_lossy(name).into_owned());
            }
        }
        cursor = end;
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use std::process::Command;

    use super::*;

    const LC_ID_DYLIB: u32 = 0x0000_000D;
    const LC_SEGMENT_64: u32 = 0x19;

    /// One load command naming `name` (a dylib_command), padded to 8 bytes.
    fn dylib_command(command: u32, name: &str) -> Vec<u8> {
        let mut size = 24 + name.len() + 1;
        size = size.div_ceil(8) * 8;
        let mut out = Vec::new();
        out.extend(command.to_le_bytes());
        out.extend((size as u32).to_le_bytes());
        out.extend(24u32.to_le_bytes()); // lc_str offset
        out.extend(2u32.to_le_bytes()); // timestamp
        out.extend(0x0001_0000u32.to_le_bytes()); // current_version
        out.extend(0x0001_0000u32.to_le_bytes()); // compatibility_version
        out.extend(name.as_bytes());
        out.resize(size, 0);
        out
    }

    /// A thin arm64 Mach-O dylib: its own `LC_ID_DYLIB`, an unrelated command, then `deps` as
    /// (command, name) load commands.
    pub(crate) fn thin(id: &str, deps: &[(u32, &str)]) -> Vec<u8> {
        let mut commands = vec![dylib_command(LC_ID_DYLIB, id)];
        let mut segment = Vec::new();
        segment.extend(LC_SEGMENT_64.to_le_bytes());
        segment.extend(72u32.to_le_bytes());
        segment.resize(72, 0);
        commands.push(segment);
        for (command, name) in deps {
            commands.push(dylib_command(*command, name));
        }
        let sizeofcmds: usize = commands.iter().map(Vec::len).sum();
        let mut out = Vec::new();
        out.extend(MH_MAGIC_64.to_le_bytes());
        out.extend(0x0100_000Cu32.to_le_bytes()); // CPU_TYPE_ARM64
        out.extend(0u32.to_le_bytes()); // cpusubtype
        out.extend(6u32.to_le_bytes()); // MH_DYLIB
        out.extend((commands.len() as u32).to_le_bytes());
        out.extend((sizeofcmds as u32).to_le_bytes());
        out.extend(0u32.to_le_bytes()); // flags
        out.extend(0u32.to_le_bytes()); // reserved
        for command in commands {
            out.extend(command);
        }
        out
    }

    /// A universal file around one slice, with a 32- or 64-bit fat header.
    fn fat(slice: &[u8], wide: bool) -> Vec<u8> {
        const OFFSET: usize = 4096;
        let mut out = Vec::new();
        out.extend((if wide { FAT_MAGIC_64 } else { FAT_MAGIC }).to_be_bytes());
        out.extend(1u32.to_be_bytes());
        out.extend(0x0100_000Cu32.to_be_bytes());
        out.extend(0u32.to_be_bytes());
        if wide {
            out.extend((OFFSET as u64).to_be_bytes());
            out.extend((slice.len() as u64).to_be_bytes());
            out.extend(14u32.to_be_bytes());
            out.extend(0u32.to_be_bytes());
        } else {
            out.extend((OFFSET as u32).to_be_bytes());
            out.extend((slice.len() as u32).to_be_bytes());
            out.extend(14u32.to_be_bytes());
        }
        out.resize(OFFSET, 0);
        out.extend(slice);
        out
    }

    fn temp_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("prisc_interop_dylib_{}_{name}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn deps() -> [(u32, &'static str); 3] {
        [
            (LC_LOAD_DYLIB, "@rpath/liba.dylib"),
            (LC_LOAD_WEAK_DYLIB, "@rpath/libb.dylib"),
            (LC_REEXPORT_DYLIB, "/usr/lib/libc++.1.dylib"),
        ]
    }

    #[test]
    fn thin_and_fat_files_list_load_weak_and_reexport_commands_but_not_their_own_id() {
        let dir = temp_dir("shapes");
        let slice = thin("@rpath/libself.dylib", &deps());
        let expected = ["@rpath/liba.dylib", "@rpath/libb.dylib", "/usr/lib/libc++.1.dylib"];
        for (name, bytes) in [("thin", slice.clone()), ("fat32", fat(&slice, false)), ("fat64", fat(&slice, true))] {
            let path = dir.join(name);
            std::fs::write(&path, bytes).unwrap();
            assert_eq!(dependencies(&path), expected, "{name}");
            assert_eq!(rpath_dependencies(&path), ["liba.dylib", "libb.dylib"], "{name}");
        }
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn unreadable_truncated_and_non_macho_inputs_report_nothing() {
        let dir = temp_dir("junk");
        let text = dir.join("text");
        std::fs::write(&text, "this is certainly not a Mach-O file").unwrap();
        assert!(dependencies(&text).is_empty());
        assert!(dependencies(&dir.join("missing")).is_empty());
        assert!(dependencies(&dir).is_empty());

        // every prefix of a real file: never a panic, never a name from past the end
        let slice = thin("@rpath/libself.dylib", &deps());
        for len in 0..slice.len() {
            let mut names = Vec::new();
            read_into(&slice[..len], &mut names);
            assert!(names.len() < 3, "a {len}-byte prefix read {names:?}");
        }
        // a fat header whose slice offset points past the end
        let mut broken = fat(&slice, false);
        broken.truncate(4096);
        let mut names = Vec::new();
        read_into(&broken, &mut names);
        assert!(names.is_empty());
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn the_closure_follows_rpath_from_the_roots_once_and_skips_versioned_aliases() {
        let dir = temp_dir("closure");
        let write = |name: &str, deps: &[(u32, &str)]| std::fs::write(dir.join(name), thin(&format!("@rpath/{name}"), deps)).unwrap();
        write("libavutil.dylib", &[(LC_LOAD_DYLIB, "@rpath/libx.dylib")]);
        write(
            "libavcodec.dylib",
            &[
                (LC_LOAD_DYLIB, "@rpath/libavutil.dylib"),
                (LC_LOAD_WEAK_DYLIB, "@rpath/liby.dylib"),
                (LC_LOAD_DYLIB, "/usr/lib/libSystem.B.dylib"),
            ],
        );
        write("libx.dylib", &[(LC_LOAD_DYLIB, "@rpath/libavutil.dylib")]); // a cycle
        write("libavcodec.62.dylib", &[]); // an alias nobody asks for
        write("libfreetype.dylib", &[]); // in the directory, outside the closure

        let names: Vec<String> = closure(&dir)
            .iter()
            .map(|p| {
                p.file_name()
                    .unwrap()
                    .to_string_lossy()
                    .into_owned()
            })
            .collect();
        assert_eq!(names, ["libavutil.dylib", "libavcodec.dylib", "libx.dylib"]);

        let empty = temp_dir("empty");
        assert!(closure(&empty).is_empty());
        assert_eq!(prepare(&empty), 0);
        assert_eq!(prepare(&empty.join("missing")), 0);
        let _ = std::fs::remove_dir_all(dir);
        let _ = std::fs::remove_dir_all(empty);
    }

    /// Every library `otool -l` says the file links against (load, weak and re-export commands),
    /// or None when otool is not usable. `-l`, not `-L`: `-L` prints the file's own install name
    /// first, indistinguishable from a dependency.
    fn otool_dependencies(path: &Path) -> Option<Vec<String>> {
        let output = Command::new("/usr/bin/otool")
            .arg("-l")
            .arg(path)
            .output()
            .ok()?;
        if !output.status.success() {
            return None;
        }
        let mut names = Vec::new();
        let mut in_load = false;
        for line in String::from_utf8_lossy(&output.stdout).lines() {
            let line = line.trim();
            if let Some(cmd) = line.strip_prefix("cmd ") {
                in_load = matches!(cmd.trim(), "LC_LOAD_DYLIB" | "LC_LOAD_WEAK_DYLIB" | "LC_REEXPORT_DYLIB");
            } else if in_load {
                if let Some(value) = line.strip_prefix("name ") {
                    in_load = false;
                    let value = value
                        .rsplit_once(" (offset ")
                        .map_or(value, |(name, _)| name);
                    names.push(value.to_owned());
                }
            }
        }
        Some(names)
    }

    fn distinct_sorted(mut names: Vec<String>) -> Vec<String> {
        names.sort();
        names.dedup();
        names
    }

    /// The reader agrees with otool on real files: this test binary (thin), `/bin/ls` and
    /// `/usr/lib/dyld` (universal), the synthetic fixtures, and the recorder payload's dylibs when
    /// `PRISC_HELPERS_DIR` names it. Compared on the distinct set, since otool prints every slice.
    #[test]
    fn the_reader_agrees_with_otool() {
        let dir = temp_dir("otool");
        let fixture = dir.join("libfixture.dylib");
        std::fs::write(&fixture, fat(&thin("@rpath/libfixture.dylib", &deps()), true)).unwrap();

        let mut corpus = vec![std::env::current_exe().unwrap(), "/bin/ls".into(), "/usr/lib/dyld".into(), fixture];
        if let Some(helpers) = std::env::var_os(prisc_ipc::locate::HELPERS_DIR_VAR) {
            for frameworks in std::env::split_paths(&helpers).map(|d| d.join("recorder/Frameworks")) {
                if let Ok(entries) = std::fs::read_dir(&frameworks) {
                    corpus.extend(
                        entries
                            .flatten()
                            .map(|e| e.path())
                            .filter(|p| p.extension().is_some_and(|e| e == "dylib")),
                    );
                }
            }
        }

        let (mut compared, mut total) = (0, 0);
        for path in &corpus {
            if !path.is_file() {
                continue;
            }
            let Some(expected) = otool_dependencies(path) else {
                continue;
            };
            assert_eq!(
                distinct_sorted(dependencies(path)),
                distinct_sorted(expected.clone()),
                "{}",
                path.display()
            );
            compared += 1;
            total += expected.len();
        }
        let _ = std::fs::remove_dir_all(dir);
        if compared == 0 {
            eprintln!("skipping: otool is not available to compare against");
            return;
        }
        assert!(
            total > 0,
            "compared {compared} files and none had a dependency: the comparison proved nothing"
        );
    }
}
