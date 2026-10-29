// The ABI fingerprint of prisc_interop (crates/prisc_interop/README.md, section E).
//
// One 64-bit FNV-1a over a canonical rendering of everything the C# bindings depend on:
// - every `#[no_mangle]` fn in crates/prisc_interop/src/ffi/*.rs: its name and full signature;
// - every `#[repr(C)]` struct with its field names and types, and every opaque handle struct;
// - every enum with its `#[repr]` and discriminants;
// - every type alias (the callback types);
// - every `pub const` (the `PRISC_*` bounds generated into NativeMethods.g.cs);
// - the hotkey `Key` names and discriminants from crates/prisc_hotkeys/src/key.rs.
// Doc comments, bodies, formatting, key labels and aliases do not count: the rendering goes
// through syn and token streams, never through the source text.
//
// Shared by two consumers: crates/prisc_interop/build.rs `include!`s this file to stamp the hash
// into the library (OUT_DIR), and prisc_codegen writes the same number to
// ui/Prisc.Interop/Generated/InteropAbi.g.cs. That is why it is std + syn + quote + proc-macro2 only and carries
// no inner attributes or `//!` docs (an `include!`d file cannot).

use std::fmt::Write as _;
use std::path::{Path, PathBuf};

use quote::ToTokens;

/// The fingerprint and what it was computed from.
pub struct Fingerprint {
    pub hash: u64,
    /// The canonical text that was hashed, one line per item, for diagnostics and tests.
    pub canonical: String,
    /// Every file read, for `rerun-if-changed`.
    pub inputs: Vec<PathBuf>,
}

/// Reads every `*.rs` in `ffi_dir` (sorted by file name) and `key_rs`, and fingerprints them.
pub fn compute_from_paths(ffi_dir: &Path, key_rs: &Path) -> Result<Fingerprint, String> {
    let mut files: Vec<PathBuf> = std::fs::read_dir(ffi_dir)
        .map_err(|e| format!("{}: {e}", ffi_dir.display()))?
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| {
            path.extension()
                .is_some_and(|ext| ext == "rs")
        })
        .collect();
    files.sort();
    let mut sources = Vec::with_capacity(files.len());
    for path in &files {
        let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
        let name = path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default();
        sources.push((name, text));
    }
    let key = std::fs::read_to_string(key_rs).map_err(|e| format!("{}: {e}", key_rs.display()))?;
    let canonical = canonical(&sources, &key)?;
    let mut inputs = files;
    inputs.push(key_rs.to_path_buf());
    Ok(Fingerprint {
        hash: fnv1a64(canonical.as_bytes()),
        canonical,
        inputs,
    })
}

/// The canonical rendering of `files` (`(file name, source)`, already sorted) and the key table.
pub fn canonical(files: &[(String, String)], key_rs: &str) -> Result<String, String> {
    let mut out = String::new();
    for (name, source) in files {
        let file = syn::parse_file(source).map_err(|e| format!("{name}: {e}"))?;
        let _ = writeln!(out, "file {name}");
        render_items(&file.items, &mut out);
    }
    let keys = syn::parse_file(key_rs).map_err(|e| format!("key.rs: {e}"))?;
    let _ = writeln!(out, "keys");
    for item in &keys.items {
        if let syn::Item::Macro(mac) = item {
            if mac.mac.path.is_ident("keys") {
                render_key_rows(mac.mac.tokens.clone(), &mut out);
            }
        }
    }
    Ok(out)
}

/// 64-bit FNV-1a.
pub fn fnv1a64(bytes: &[u8]) -> u64 {
    bytes
        .iter()
        .fold(0xCBF2_9CE4_8422_2325u64, |hash, byte| {
            (hash ^ u64::from(*byte)).wrapping_mul(0x0000_0100_0000_01B3)
        })
}

/// The token text of `node` with every space removed and trailing commas dropped, so that rustfmt
/// wrapping a list onto several lines (which adds a trailing comma) does not change it.
fn tokens(node: &impl ToTokens) -> String {
    node.to_token_stream()
        .to_string()
        .replace(' ', "")
        .replace(",)", ")")
        .replace(",>", ">")
        .replace(",]", "]")
        .replace(",}", "}")
}

fn has_attr(attrs: &[syn::Attribute], name: &str) -> bool {
    attrs.iter().any(|attr| {
        if attr.path().is_ident(name) {
            return true;
        }
        // `#[unsafe(no_mangle)]`
        attr.path().is_ident("unsafe") && tokens(&attr.meta).contains(name)
    })
}

fn repr_of(attrs: &[syn::Attribute]) -> Option<String> {
    attrs
        .iter()
        .find(|attr| attr.path().is_ident("repr"))
        .map(|attr| tokens(&attr.meta))
}

fn is_cfg_test(attrs: &[syn::Attribute]) -> bool {
    attrs
        .iter()
        .any(|attr| attr.path().is_ident("cfg") && tokens(&attr.meta).replace(' ', "") == "cfg(test)")
}

fn render_items(items: &[syn::Item], out: &mut String) {
    for item in items {
        match item {
            syn::Item::Fn(f) if has_attr(&f.attrs, "no_mangle") => {
                let _ = writeln!(out, "fn {}", tokens(&f.sig));
            }
            syn::Item::Struct(s) => match repr_of(&s.attrs) {
                Some(repr) => {
                    let _ = write!(out, "struct {} {repr} {{", s.ident);
                    for (i, field) in s.fields.iter().enumerate() {
                        let name = field
                            .ident
                            .as_ref()
                            .map_or_else(|| i.to_string(), |ident| ident.to_string());
                        let _ = write!(out, " {name}: {};", tokens(&field.ty));
                    }
                    let _ = writeln!(out, " }}");
                }
                None if matches!(s.vis, syn::Visibility::Public(_)) => {
                    let _ = writeln!(out, "opaque {}", s.ident);
                }
                None => {}
            },
            syn::Item::Enum(e) if matches!(e.vis, syn::Visibility::Public(_)) => {
                let _ = write!(out, "enum {} {} {{", e.ident, repr_of(&e.attrs).unwrap_or_default());
                for variant in &e.variants {
                    let value = variant
                        .discriminant
                        .as_ref()
                        .map(|(_, expr)| tokens(expr))
                        .unwrap_or_default();
                    let _ = write!(out, " {} = {value};", variant.ident);
                }
                let _ = writeln!(out, " }}");
            }
            syn::Item::Type(t) if matches!(t.vis, syn::Visibility::Public(_)) => {
                let _ = writeln!(out, "type {} = {}", t.ident, tokens(&t.ty));
            }
            syn::Item::Const(c) if matches!(c.vis, syn::Visibility::Public(_)) => {
                let _ = writeln!(out, "const {}: {} = {}", c.ident, tokens(&c.ty), tokens(&c.expr));
            }
            syn::Item::Mod(m) if !is_cfg_test(&m.attrs) => {
                if let Some((_, items)) = &m.content {
                    render_items(items, out);
                }
            }
            _ => {}
        }
    }
}

/// The `Name = value` pairs of the `keys!` invocation, in order. Aliases, platform codes and the
/// label table are not part of the ABI and are skipped.
fn render_key_rows(stream: proc_macro2::TokenStream, out: &mut String) {
    let trees: Vec<proc_macro2::TokenTree> = stream.into_iter().collect();
    for window in trees.windows(3) {
        if let [proc_macro2::TokenTree::Ident(name), proc_macro2::TokenTree::Punct(eq), proc_macro2::TokenTree::Literal(value)] = window {
            if eq.as_char() == '=' {
                let _ = writeln!(out, "key {name} = {value}");
            }
        }
    }
}
