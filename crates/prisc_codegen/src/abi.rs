//! The prisc_interop C ABI as C# (DESIGN 3.2):
//!
//! - `ui/Prisc.Interop/Generated/NativeMethods.g.cs`: csbindgen's DllImports, `#[repr(C)]` structs
//!   and opaque handle types, `internal`. Enums are kept out of this pass (signatures carry
//!   `i32`/`u32`, README B2) and no const passes the filter.
//! - `ui/Prisc.Interop/Generated/InteropTypes.g.cs`: every `#[repr] pub enum Prisc*` in `src/ffi`,
//!   public, `Prisc` prefix stripped, rendered by [`cs::enum_decl`] (not csbindgen), `[Flags]` when
//!   the name ends in `Flags`.
//! - `ui/Prisc.Interop/Generated/InteropAbi.g.cs`: the ABI fingerprint (README E1), the number the
//!   loader compares with the library's `prisc_interop_abi_hash`.
//!
//! The ffi files are found by listing the directory (sorted), never by a hand list. prisc_codegen
//! reads them as text and never depends on prisc_interop.

use std::path::{Path, PathBuf};

use crate::{abi_fingerprint, cs, Output};

/// prisc_interop's ABI directory, repo-relative.
pub const FFI_DIR: &str = "crates/prisc_interop/src/ffi";
/// The hotkey key table the fingerprint covers, repo-relative.
pub const KEY_RS: &str = "crates/prisc_hotkeys/src/key.rs";

const NATIVE_SOURCE: &str = "crates/prisc_interop/src/ffi/*.rs (csbindgen)";
const TYPES_SOURCE: &str = "the #[repr] enums in crates/prisc_interop/src/ffi/*.rs";
const ABI_SOURCE: &str = "crates/prisc_interop/src/ffi/*.rs and crates/prisc_hotkeys/src/key.rs";

/// The three interop outputs.
pub static OUTPUTS: &[Output] = &[
    Output {
        name: "interop-native",
        path: "ui/Prisc.Interop/Generated/NativeMethods.g.cs",
        source: NATIVE_SOURCE,
        render: render_native,
    },
    Output {
        name: "interop-types",
        path: "ui/Prisc.Interop/Generated/InteropTypes.g.cs",
        source: TYPES_SOURCE,
        render: render_types,
    },
    Output {
        name: "interop-abi",
        path: "ui/Prisc.Interop/Generated/InteropAbi.g.cs",
        source: ABI_SOURCE,
        render: render_abi,
    },
];

/// Every `*.rs` in the ffi directory, sorted.
pub fn ffi_files(root: &Path) -> anyhow::Result<Vec<PathBuf>> {
    let mut files: Vec<PathBuf> = std::fs::read_dir(root.join(FFI_DIR))?
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| {
            path.extension()
                .is_some_and(|ext| ext == "rs")
        })
        .collect();
    files.sort();
    Ok(files)
}

fn parse(path: &Path) -> anyhow::Result<syn::File> {
    let text = std::fs::read_to_string(path)?;
    syn::parse_file(&text).map_err(|e| anyhow::anyhow!("{}: {e}", path.display()))
}

/// The ABI fingerprint of the tree at `root`.
pub fn fingerprint(root: &Path) -> anyhow::Result<u64> {
    abi_fingerprint::compute_from_paths(&root.join(FFI_DIR), &root.join(KEY_RS))
        .map(|f| f.hash)
        .map_err(|e| anyhow::anyhow!(e))
}

fn render_native(root: &Path) -> anyhow::Result<String> {
    let files = ffi_files(root)?;
    // Every struct (repr(C) and opaque handle) is always included, so one reached only through a
    // callback signature is still emitted; enums are never named, so csbindgen leaves them out.
    let mut structs = Vec::new();
    for path in &files {
        for item in parse(path)?.items {
            if let syn::Item::Struct(s) = item {
                if matches!(s.vis, syn::Visibility::Public(_)) {
                    structs.push(s.ident.to_string());
                }
            }
        }
    }
    let mut builder = csbindgen::Builder::default();
    for path in &files {
        builder = builder.input_extern_file(path);
    }
    let dir = std::env::temp_dir().join(format!("prisc_codegen-{}", std::process::id()));
    std::fs::create_dir_all(&dir)?;
    let staged = dir.join("NativeMethods.g.cs");
    builder
        .csharp_dll_name("prisc_interop")
        .csharp_namespace("Prisc.Interop")
        .csharp_class_name("NativeMethods")
        .csharp_class_accessibility("internal")
        .csharp_use_function_pointer(true)
        .csharp_use_nint_types(true)
        .always_included_types(structs)
        // The `pub const PRISC_*` limits C# checks before it calls (README D6). csbindgen reads
        // every const whatever its visibility, so the lint keeps that name for the pub ones.
        .csharp_generate_const_filter(|name| name.starts_with("PRISC_"))
        .csharp_file_header(cs::header("abi", NATIVE_SOURCE).trim_end())
        .generate_csharp_file(&staged)
        .map_err(|e| anyhow::anyhow!("csbindgen: {e}"))?;
    let text = std::fs::read_to_string(&staged)?.replace("\r\n", "\n");
    let _ = std::fs::remove_dir_all(&dir);
    Ok(text)
}

/// The C# underlying type of a `#[repr]`.
fn underlying(attrs: &[syn::Attribute]) -> Option<&'static str> {
    let mut found = None;
    for attr in attrs {
        if attr.path().is_ident("repr") {
            let _ = attr.parse_nested_meta(|meta| {
                found = match meta
                    .path
                    .get_ident()
                    .map(|i| i.to_string())
                    .as_deref()
                {
                    Some("i32") => Some("int"),
                    Some("u32") => Some("uint"),
                    Some("u8") => Some("byte"),
                    Some("i8") => Some("sbyte"),
                    Some("i16") => Some("short"),
                    Some("u16") => Some("ushort"),
                    Some("i64") => Some("long"),
                    Some("u64") => Some("ulong"),
                    _ => found,
                };
                Ok(())
            });
        }
    }
    found
}

fn render_types(root: &Path) -> anyhow::Result<String> {
    let mut out = cs::header("abi", TYPES_SOURCE);
    out.push_str("using System;\n\nnamespace Prisc.Interop\n{\n");
    let mut first = true;
    for path in ffi_files(root)? {
        for item in parse(&path)?.items {
            let syn::Item::Enum(e) = item else {
                continue;
            };
            let name = e.ident.to_string();
            let Some(public) = name.strip_prefix("Prisc") else {
                continue;
            };
            let members = e
                .variants
                .iter()
                .map(|variant| cs::Member {
                    name: variant.ident.to_string(),
                    value: variant
                        .discriminant
                        .as_ref()
                        .map(|(_, expr)| {
                            quote::ToTokens::to_token_stream(expr)
                                .to_string()
                                .replace(' ', "")
                        })
                        .unwrap_or_default(),
                    doc: cs::doc_of(&variant.attrs),
                })
                .collect();
            let decl = cs::EnumDecl {
                name: public.to_string(),
                underlying: underlying(&e.attrs).map(str::to_string),
                flags: name.ends_with("Flags"),
                access: "public",
                doc: cs::Doc::Summary(cs::doc_of(&e.attrs)),
                members,
            };
            if !first {
                out.push('\n');
            }
            first = false;
            out.push_str(&cs::enum_decl(&decl, "    "));
        }
    }
    out.push_str("}\n");
    Ok(out)
}

fn render_abi(root: &Path) -> anyhow::Result<String> {
    let hash = fingerprint(root)?;
    let mut out = cs::header("abi", ABI_SOURCE);
    out.push_str(&format!(
        r#"namespace Prisc.Interop
{{
    /// <summary>The ABI fingerprint these bindings were generated for (crates/prisc_interop/README.md, section E).
    /// The loader refuses a library whose <c>prisc_interop_abi_hash</c> differs.</summary>
    internal static class InteropAbi
    {{
        public const ulong Hash = 0x{hash:016X}UL;
    }}
}}
"#
    ));
    Ok(out)
}
