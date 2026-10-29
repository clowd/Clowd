//! The gate: every generated file current, nothing orphaned, every crate rule kept. Plus the
//! lint's and the fingerprint's own behaviour on small in-memory sources.

use std::collections::HashSet;
use std::path::PathBuf;

use prisc_codegen::{abi_fingerprint, check, lint, orphans, outputs, repo_root, State};

#[test]
fn generated_files_are_current() {
    let root = repo_root();
    let bad: Vec<String> = check(&root)
        .expect("render")
        .into_iter()
        .filter(|(_, state)| *state != State::Current)
        .map(|(output, state)| format!("{} ({state:?})", output.path))
        .collect();
    assert!(
        bad.is_empty(),
        "stale or missing generated files, run `cargo codegen`:\n{}",
        bad.join("\n")
    );
}

#[test]
fn no_orphaned_outputs() {
    let orphans = orphans(&repo_root()).expect("scan");
    assert!(
        orphans.is_empty(),
        "generated files no output produces any more (delete them): {orphans:?}"
    );
}

#[test]
fn output_names_and_paths_are_unique() {
    let all = outputs();
    let names: HashSet<&str> = all.iter().map(|o| o.name).collect();
    let paths: HashSet<&str> = all.iter().map(|o| o.path).collect();
    assert_eq!(names.len(), all.len());
    assert_eq!(paths.len(), all.len());
}

#[test]
fn lint_passes() {
    let violations = lint::run(&repo_root()).expect("lint");
    let text: Vec<String> = violations
        .iter()
        .map(ToString::to_string)
        .collect();
    assert!(violations.is_empty(), "prisc_interop crate rules broken:\n{}", text.join("\n"));
}

/// `HelperNames` (generated into HelperMessages.g.cs) must match the shipped package list, which
/// is shell and cannot consume C#. Vacuous until the IPC work package generates `HelperNames`.
#[test]
fn helper_names_match_shipped_sh() {
    let root = repo_root();
    let messages = std::fs::read_to_string(root.join("ui/Prisc.Ipc/Generated/HelperMessages.g.cs")).unwrap_or_default();
    let shipped = std::fs::read_to_string(root.join("tools/staging/shipped.sh")).unwrap_or_default();
    let Some(start) = messages.find("class HelperNames") else {
        return;
    };
    let block = &messages[start
        ..messages[start..]
            .find("\n    }")
            .map_or(messages.len(), |end| start + end)];
    for line in block.lines() {
        if let Some((_, value)) = line.split_once("= \"") {
            let name = value.trim_end_matches("\";");
            assert!(
                shipped.contains(name),
                "HelperNames has {name}, which tools/staging/shipped.sh does not ship"
            );
        }
    }
}

fn file(name: &str, text: &str) -> (PathBuf, String) {
    (PathBuf::from(format!("crates/prisc_interop/src/ffi/{name}")), text.to_string())
}

const GOOD: &str = r#"
use crate::panic;

#[repr(C)]
pub struct PriscRect { pub x: i32, pub y: i32 }

pub struct PriscThing { _private: [u8; 0] }

#[repr(i32)]
pub enum PriscKind { A = 0, B = 1 }

crate::convert::ffi_enum!(PriscKind: i32 { A, B });

pub type PriscCallback = Option<unsafe extern "C" fn(user: *mut c_void, rect: *const PriscRect)>;

/// Does it.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_thing_do(rect: *const PriscRect, kind: i32, cb: PriscCallback, out: *mut *mut PriscThing) -> i32 {
    panic::guard("prisc_thing_do", || Ok(0))
}
"#;

fn rules(files: &[(PathBuf, String)]) -> Vec<(&'static str, usize)> {
    lint::lint_ffi(files)
        .into_iter()
        .map(|v| (v.rule, v.line))
        .collect()
}

#[test]
fn lint_accepts_a_well_formed_file() {
    assert_eq!(rules(&[file("thing.rs", GOOD)]), []);
}

#[test]
fn lint_rejects_a_cfg_on_an_item_with_its_line() {
    let text = GOOD.replace("/// Does it.", "#[cfg(windows)]\n/// Does it.");
    let line = text
        .lines()
        .position(|l| l == "#[cfg(windows)]")
        .unwrap()
        + 1;
    let found = lint::lint_ffi(&[file("thing.rs", &text)]);
    assert_eq!(found.len(), 1, "{found:?}");
    assert_eq!((found[0].rule, found[0].line), ("1-no-cfg", line));
    assert!(found[0]
        .to_string()
        .starts_with(&format!("crates/prisc_interop/src/ffi/thing.rs:{line}: ")));
}

#[test]
fn lint_allows_cfg_on_use_and_a_bare_test_module_only() {
    let text = format!("#[cfg(windows)]\nuse std::ffi::c_void;\n{GOOD}\n#[cfg(test)]\nmod tests {{\n    #[test]\n    fn t() {{}}\n}}\n");
    assert_eq!(rules(&[file("thing.rs", &text)]), []);
    let bad = format!("{GOOD}\n#[cfg(test)]\nmod tests {{\n    #[repr(i32)]\n    pub enum E {{ A = 0 }}\n}}\n#[cfg_attr(test, derive(Debug))]\npub struct PriscOther {{ _private: [u8; 0] }}\n");
    let found: Vec<&str> = rules(&[file("thing.rs", &bad)])
        .into_iter()
        .map(|(r, _)| r)
        .collect();
    assert_eq!(found, ["1-no-cfg", "1-no-cfg"]);
}

#[test]
fn lint_rejects_struct_by_value_bool_enum_and_unknown_types() {
    let text = GOOD
        .replace("rect: *const PriscRect, kind: i32", "rect: PriscRect, kind: PriscKind")
        .replace("pub y: i32", "pub y: bool, pub z: String");
    let mut found: Vec<&str> = rules(&[file("thing.rs", &text)])
        .into_iter()
        .map(|(r, _)| r)
        .collect();
    found.sort();
    assert_eq!(found, ["2-types", "3-no-enum", "4-no-bool", "5-no-struct-by-value"]);
}

#[test]
fn lint_rejects_bad_exports() {
    let unguarded = GOOD.replace(
        "panic::guard(\"prisc_thing_do\", || Ok(0))",
        "panic::guard(\"prisc_other\", || Ok(0))",
    );
    assert_eq!(rules(&[file("thing.rs", &unguarded)])[0].0, "7-guard");
    let no_thread = GOOD.replace("/// Thread: any thread.\n", "");
    assert_eq!(rules(&[file("thing.rs", &no_thread)])[0].0, "7-thread-line");
    let odd_thread = GOOD.replace("Thread: any thread.", "Thread: whichever.");
    assert_eq!(rules(&[file("thing.rs", &odd_thread)])[0].0, "7-thread-line");
    let bad_return = GOOD.replace("out: *mut *mut PriscThing) -> i32", "out: *mut *mut PriscThing) -> u32");
    assert_eq!(rules(&[file("thing.rs", &bad_return)])[0].0, "7-export");
    let duplicate = rules(&[
        file("a.rs", GOOD),
        file(
            "b.rs",
            &GOOD
                .replace("pub struct", "struct")
                .replace("pub enum", "enum")
                .replace("pub type", "type"),
        ),
    ]);
    assert!(
        duplicate
            .iter()
            .any(|(r, _)| *r == "7-export"),
        "{duplicate:?}"
    );
}

#[test]
fn lint_rejects_negative_consts_and_implicit_discriminants() {
    let text = format!("{GOOD}\npub const PRISC_E_X: i32 = -1;\n#[repr(i32)]\npub enum PriscImplicit {{ A, B }}\n");
    let mut found: Vec<&str> = rules(&[file("thing.rs", &text)])
        .into_iter()
        .map(|(r, _)| r)
        .collect();
    found.sort();
    assert_eq!(found, ["13-ffi-enum", "6-const-literal", "8-enum-repr", "8-enum-repr"]);
}

#[test]
fn lint_keeps_the_generated_consts_exactly_the_pub_prisc_literals() {
    let text = format!(
        "{GOOD}\npub const PRISC_THING_MAX: u32 = 4;\npub const LIMIT: u32 = 4;\nconst PRISC_HIDDEN: u32 = 1;\nconst PRISC_DERIVED: \
         i32 = PriscKind::A as i32;\nconst LOCAL: u32 = 2;\n"
    );
    let found: Vec<(&str, usize)> = rules(&[file("thing.rs", &text)]);
    let base = GOOD.lines().count();
    assert_eq!(found, [("6-const-literal", base + 3), ("6-const-literal", base + 4)]);
}

#[test]
fn lint_keeps_ffi_enum_lists_in_step_with_the_enums() {
    let added_variant = GOOD.replace("pub enum PriscKind { A = 0, B = 1 }", "pub enum PriscKind { A = 0, B = 1, C = 2 }");
    let found = lint::lint_ffi(&[file("thing.rs", &added_variant)]);
    assert_eq!(found.len(), 1, "{found:?}");
    assert_eq!(found[0].rule, "13-ffi-enum");
    assert!(found[0].message.contains("missing: [C]"), "{}", found[0].message);

    let no_call = GOOD.replace("crate::convert::ffi_enum!(PriscKind: i32 { A, B });", "");
    let line = GOOD
        .lines()
        .position(|l| l.starts_with("pub enum PriscKind"))
        .unwrap()
        + 1;
    assert_eq!(rules(&[file("thing.rs", &no_call)]), [("13-ffi-enum", line)]);

    let wrong_raw = GOOD.replace("ffi_enum!(PriscKind: i32", "ffi_enum!(PriscKind: u32");
    assert_eq!(rules(&[file("thing.rs", &wrong_raw)])[0].0, "13-ffi-enum");

    let flags = format!("{GOOD}\n#[repr(u32)]\npub enum PriscThingFlags {{ None = 0, A = 1 }}\n");
    assert_eq!(rules(&[file("thing.rs", &flags)]), []);
    let flags_call = format!("{flags}\ncrate::convert::ffi_enum!(PriscThingFlags: u32 {{ None, A }});\n");
    assert_eq!(rules(&[file("thing.rs", &flags_call)])[0].0, "13-ffi-enum");

    let unknown = format!("{GOOD}\ncrate::convert::ffi_enum!(PriscNothing: i32 {{ A }});\n");
    assert_eq!(rules(&[file("thing.rs", &unknown)])[0].0, "13-ffi-enum");
}

#[test]
fn lint_rejects_threads_export_calls_and_unguarded_trampolines() {
    let exports: HashSet<String> = ["prisc_thing_do".to_string()].into();
    let util = (
        PathBuf::from("crates/prisc_interop/src/util.rs"),
        "fn f() {\n    std::thread::spawn(|| {});\n    unsafe { crate::ffi::thing::prisc_thing_do() };\n}\n#[cfg(test)]\nmod tests {\n    fn t() { \
         std::thread::spawn(|| {}); }\n}\n"
            .to_string(),
    );
    let platform = (
        PathBuf::from("crates/prisc_interop/src/platform/macos/audio_output.rs"),
        "unsafe extern \"C\" fn render(user: *mut u8) -> i32 {\n    0\n}\nunsafe extern \"C\" fn ok(user: *mut u8) -> i32 {\n    \
         crate::panic::callback_guard(&STATE.panic, 0, || 1)\n}\n"
            .to_string(),
    );
    let found: Vec<(&str, usize)> = lint::lint_crate(&[util, platform], &exports)
        .into_iter()
        .map(|v| (v.rule, v.line))
        .collect();
    assert_eq!(found, [("10-no-threads", 2), ("7-no-export-calls", 3), ("11-callback-guard", 1)]);
}

#[test]
fn lint_rejects_foreign_exports() {
    let hotkeys = (
        PathBuf::from("crates/prisc_hotkeys/src/macos.rs"),
        "#[no_mangle]\npub extern \"C\" fn prisc_hotkeys_start() {}\nfn f() {}\n#[export_name = \"x\"]\nstatic X: u8 = 0;\n".to_string(),
    );
    let platform = (
        PathBuf::from("crates/prisc_platform/src/input.rs"),
        "\n#[unsafe(no_mangle)]\npub extern \"C\" fn prisc_screens_list() {}\nobjc2::define_class!(struct Foo;);\n".to_string(),
    );
    let found: Vec<(String, usize)> = lint::lint_foreign_exports(&[hotkeys, platform])
        .into_iter()
        .map(|v| (v.file.display().to_string(), v.line))
        .collect();
    assert_eq!(
        found,
        [
            ("crates/prisc_hotkeys/src/macos.rs".to_string(), 2),
            ("crates/prisc_hotkeys/src/macos.rs".to_string(), 5),
            ("crates/prisc_platform/src/input.rs".to_string(), 3),
            ("crates/prisc_platform/src/input.rs".to_string(), 4)
        ]
    );
}

#[test]
fn lint_rejects_exported_statics_in_the_crate() {
    let services = (
        PathBuf::from("crates/prisc_interop/src/platform/macos/services.rs"),
        "define_class!(\n    struct Foo;\n);\n#[unsafe(export_name = \"__x\")]\nstatic X: u8 = 0;\nfn f() { let _ = 1; }\n".to_string(),
    );
    let found: Vec<(&str, usize)> = lint::lint_crate(&[services], &HashSet::new())
        .into_iter()
        .map(|v| (v.rule, v.line))
        .collect();
    assert_eq!(found, [("14-exported-statics", 1), ("14-exported-statics", 4)]);
}

const KEYS: &str = "keys! {\n    None = 0, [], win[], mac[], nx[];\n    A = 44, [\"a\"], win[0x41], mac[0x00], nx[];\n}\n";

fn hash(ffi: &str, keys: &str) -> u64 {
    abi_fingerprint::fnv1a64(
        abi_fingerprint::canonical(&[("thing.rs".into(), ffi.into())], keys)
            .unwrap()
            .as_bytes(),
    )
}

#[test]
fn fingerprint_ignores_docs_bodies_formatting_and_key_extras() {
    let base = hash(GOOD, KEYS);
    assert_eq!(base, hash(&GOOD.replace("/// Does it.", "/// Does something else entirely."), KEYS));
    assert_eq!(base, hash(&GOOD.replace("Ok(0)", "Ok(1)"), KEYS));
    assert_eq!(
        base,
        hash(
            &GOOD.replace(
                "pub struct PriscRect { pub x: i32, pub y: i32 }",
                "pub struct PriscRect {\n    pub x: i32,\n    pub y: i32,\n}"
            ),
            KEYS
        )
    );
    assert_eq!(
        base,
        hash(
            &GOOD.replace("out: *mut *mut PriscThing) -> i32", "\n    out: *mut *mut PriscThing,\n) -> i32"),
            KEYS
        ),
        "a trailing comma from rustfmt wrapping"
    );
    assert_eq!(
        base,
        hash(
            GOOD,
            &KEYS
                .replace("[\"a\"]", "[\"a\", \"KeyA\"]")
                .replace("0x41", "0x42")
        )
    );
}

#[test]
fn fingerprint_changes_with_the_abi() {
    let base = hash(GOOD, KEYS);
    assert_ne!(base, hash(&GOOD.replace("B = 1", "B = 2"), KEYS), "an enum discriminant");
    assert_ne!(base, hash(&GOOD.replace("kind: i32", "kind: u32"), KEYS), "a parameter type");
    assert_ne!(base, hash(&GOOD.replace("pub y: i32", "pub y: i64"), KEYS), "a field type");
    assert_ne!(
        base,
        hash(&GOOD.replace("prisc_thing_do(", "prisc_thing_go("), KEYS),
        "an export name"
    );
    assert_ne!(base, hash(GOOD, &KEYS.replace("A = 44", "A = 45")), "a key value");
    let with_const = format!("{GOOD}\n/// The most things.\npub const PRISC_THING_MAX: u32 = 8;\n");
    let const_base = hash(&with_const, KEYS);
    assert_ne!(base, const_base, "a new const");
    assert_ne!(const_base, hash(&with_const.replace("= 8;", "= 9;"), KEYS), "a const value");
    assert_eq!(
        const_base,
        hash(&with_const.replace("/// The most things.", "/// Any other words."), KEYS),
        "a const's doc comment"
    );
}

#[test]
fn fingerprint_of_the_tree_matches_the_generated_bindings() {
    let root = repo_root();
    let hash = prisc_codegen::abi::fingerprint(&root).unwrap();
    let generated = std::fs::read_to_string(root.join("ui/Prisc.Interop/Generated/InteropAbi.g.cs")).unwrap_or_default();
    assert!(
        generated.contains(&format!("0x{hash:016X}UL")),
        "InteropAbi.g.cs is stale, run `cargo codegen`"
    );
}

#[test]
fn lint_finds_an_em_dash_and_skips_build_output() {
    let dir = std::env::temp_dir().join(format!("prisc_codegen_em_dash_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(dir.join("src")).unwrap();
    std::fs::create_dir_all(dir.join("obj")).unwrap();
    std::fs::write(dir.join("src/lib.rs"), "// fine\n// not \u{2014} fine\n").unwrap();
    std::fs::write(dir.join("obj/skipped.cs"), "\u{2014}\n").unwrap();
    let found: Vec<(std::path::PathBuf, usize, &str)> = lint::lint_em_dash(&dir, &dir)
        .expect("scan")
        .into_iter()
        .map(|v| (v.file, v.line, v.rule))
        .collect();
    let _ = std::fs::remove_dir_all(&dir);
    // Paths compare by component, so this holds with either separator.
    assert_eq!(found, [(std::path::PathBuf::from("src/lib.rs"), 2, "9-no-em-dash")]);
}

/// Rule 12 checks what this walk returns, so an empty walk would pass vacuously.
#[test]
fn the_dependency_walk_reaches_the_path_crates_and_not_the_crate_itself() {
    let files = lint::dependency_sources(&repo_root()).expect("cargo metadata");
    let under = |prefix: &str| {
        files
            .iter()
            .any(|(path, _)| path.starts_with(prefix))
    };
    for dependency in [
        "crates/prisc_platform/src",
        "crates/prisc_rust_core/src",
        "crates/prisc_hotkeys/src",
    ] {
        assert!(under(dependency), "no file under {dependency}");
    }
    assert!(!under("crates/prisc_interop/src"), "the walk includes prisc_interop itself");
}
