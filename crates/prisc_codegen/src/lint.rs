//! Every crate rule of prisc_interop (README section D and B2, B3, B8, A6, C5), in one gate:
//! `cargo test -p prisc_codegen`. Each violation reports `file:line`.
//!
//! Over `crates/prisc_interop/src/ffi/*.rs`:
//! 1. no `#[cfg]` on any item (a `use` may carry one; a bare `#[cfg(test)]` inline `mod` may, and
//!    holds no export, `#[repr(C)]` struct, `pub const` or enum); `#[cfg_attr]` never (D1);
//! 2. every type in a signature, `#[repr(C)]` field or type alias resolves to a primitive, a
//!    pointer, an array, a callback, or a struct or alias declared in `src/ffi`;
//! 3. no enum type in a signature or field (B2);
//! 4. no `bool` (B3);
//! 5. no struct by value in a parameter (B8);
//! 6. every `pub const` is a non-negative literal named `PRISC_*`, and no other const is a
//!    `PRISC_*` literal: those are exactly the consts generated into C# (D6);
//! 7. every export starts with `prisc_`, is unique, is `unsafe extern "C"`, returns `i32` (the two
//!    last-error readers excepted), has exactly one `Thread:` line from the vocabulary (D5), and
//!    its body's first statement is `panic::guard("<its own name>", ...)` (A6);
//! 8. every enum has an explicit `#[repr(i32|u32|u8)]` and explicit discriminants;
//! 13. every enum not named `*Flags` has exactly one `convert::ffi_enum!` invocation under src/ffi,
//!     with the enum's repr type and exactly its variants, so `from_raw` cannot fall behind the
//!     declaration; a `*Flags` enum has none (it goes through `convert::flags`) (B2).
//!
//! Over `crates/prisc_interop/src/**` (test modules excluded):
//! - no function body calls an export (A6);
//! 10. no `thread::spawn`, `thread::Builder` or `thread::scope` (D4);
//! 11. every `extern` fn under `src/platform` (an OS-called trampoline) starts with
//!     `panic::callback_guard` (C5);
//! 14. no `#[export_name]` item and no objc2 `define_class!` (which gives every class
//!     `#[export_name]` statics): the cdylib would export those beside the `prisc_*` surface (A1).
//!
//! Elsewhere:
//! 9. no U+2014 in prisc_interop, prisc_platform, prisc_codegen or ui/Prisc.Interop (D7);
//! 12. no `#[no_mangle]` or `#[export_name]` item and no `define_class!` in any path dependency
//!     of prisc_interop (`cargo metadata`).

use std::collections::{BTreeMap, HashMap, HashSet};
use std::fmt;
use std::path::{Path, PathBuf};

use quote::ToTokens;
use syn::spanned::Spanned;
use syn::visit::Visit;

use crate::abi::FFI_DIR;

/// One broken rule.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Violation {
    /// Repo-relative.
    pub file: PathBuf,
    pub line: usize,
    pub rule: &'static str,
    pub message: String,
}

impl fmt::Display for Violation {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}:{}: [{}] {}", self.file.display(), self.line, self.rule, self.message)
    }
}

/// The exact `Thread:` lines an export may carry (README D5).
pub const THREAD_LINES: &[&str] = &[
    "Thread: any thread.",
    "Thread: any thread; the crate scopes its own COM apartment for the duration of the call.",
    "Thread: must be the macOS main thread.",
    "Thread: must be the macOS main thread; any thread elsewhere.",
    "Thread: any thread, never concurrently with another call on the same handle.",
    "Thread: the thread that owns the window (inside its window procedure).",
    "Thread: a thread that pumps window messages; the callback runs on that thread.",
];

/// The two exports that are not status-returning and not guarded (README A1, A5).
const LAST_ERROR_READERS: &[(&str, &str)] = &[("prisc_last_error_message", "usize"), ("prisc_last_error_code", "i64")];

const EM_DASH: char = '\u{2014}';

/// Every rule over the tree at `root`.
pub fn run(root: &Path) -> anyhow::Result<Vec<Violation>> {
    let ffi = read_rs(root, &root.join(FFI_DIR), false)?;
    let mut violations = lint_ffi(&ffi);
    let exports = export_names(&ffi);
    let crate_src = read_rs(root, &root.join("crates/prisc_interop/src"), true)?;
    violations.extend(lint_crate(&crate_src, &exports));
    for dir in [
        "crates/prisc_interop",
        "crates/prisc_platform",
        "crates/prisc_codegen",
        "ui/Prisc.Interop",
    ] {
        violations.extend(lint_em_dash(root, &root.join(dir))?);
    }
    let deps = dependency_sources(root)?;
    violations.extend(lint_foreign_exports(&deps));
    Ok(violations)
}

/// Reads every `*.rs` under `dir` (recursively when asked) as `(repo-relative path, text)`.
pub fn read_rs(root: &Path, dir: &Path, recursive: bool) -> anyhow::Result<Vec<(PathBuf, String)>> {
    let mut files = Vec::new();
    let mut stack = vec![dir.to_path_buf()];
    while let Some(dir) = stack.pop() {
        for entry in std::fs::read_dir(&dir)?.flatten() {
            let path = entry.path();
            if path.is_dir() {
                if recursive {
                    stack.push(path);
                }
            } else if path.extension().is_some_and(|e| e == "rs") {
                let text = std::fs::read_to_string(&path)?;
                files.push((relative(root, &path), text));
            }
        }
    }
    files.sort();
    Ok(files)
}

fn relative(root: &Path, path: &Path) -> PathBuf {
    path.strip_prefix(root)
        .map(Path::to_path_buf)
        .unwrap_or_else(|_| path.to_path_buf())
}

fn line_of(span: proc_macro2::Span) -> usize {
    span.start().line
}

fn tokens(node: &impl ToTokens) -> String {
    node.to_token_stream().to_string()
}

fn has_attr(attrs: &[syn::Attribute], name: &str) -> bool {
    attrs
        .iter()
        .any(|attr| attr.path().is_ident(name) || (attr.path().is_ident("unsafe") && tokens(&attr.meta).contains(name)))
}

fn is_bare_cfg_test(attr: &syn::Attribute) -> bool {
    attr.path().is_ident("cfg") && tokens(&attr.meta).replace(' ', "") == "cfg(test)"
}

fn in_test_mod(attrs: &[syn::Attribute]) -> bool {
    attrs.iter().any(is_bare_cfg_test)
}

fn doc_lines(attrs: &[syn::Attribute]) -> Vec<String> {
    attrs
        .iter()
        .filter(|attr| attr.path().is_ident("doc"))
        .filter_map(|attr| match &attr.meta {
            syn::Meta::NameValue(nv) => match &nv.value {
                syn::Expr::Lit(syn::ExprLit {
                    lit: syn::Lit::Str(s),
                    ..
                }) => Some(s.value().trim().to_string()),
                _ => None,
            },
            _ => None,
        })
        .collect()
}

fn repr_of(attrs: &[syn::Attribute]) -> Option<String> {
    attrs
        .iter()
        .find(|attr| attr.path().is_ident("repr"))
        .map(|attr| tokens(&attr.meta).replace(' ', ""))
}

/// The export names declared in the ffi files.
pub fn export_names(ffi: &[(PathBuf, String)]) -> HashSet<String> {
    let mut names = HashSet::new();
    for (_, text) in ffi {
        if let Ok(file) = syn::parse_file(text) {
            collect_exports(&file.items, &mut names);
        }
    }
    names
}

fn collect_exports(items: &[syn::Item], names: &mut HashSet<String>) {
    for item in items {
        match item {
            syn::Item::Fn(f) if has_attr(&f.attrs, "no_mangle") => {
                names.insert(f.sig.ident.to_string());
            }
            syn::Item::Mod(m) if !in_test_mod(&m.attrs) => {
                if let Some((_, items)) = &m.content {
                    collect_exports(items, names);
                }
            }
            _ => {}
        }
    }
}

/// What the ffi files declare, for type resolution.
#[derive(Default)]
struct Decls {
    structs: HashSet<String>,
    enums: HashSet<String>,
    aliases: HashSet<String>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Ctx {
    /// A parameter of an export or a callback: a struct by value is B8.
    Param,
    Return,
    Field,
    /// Behind a pointer or inside an array or alias.
    Inner,
}

struct FfiLint<'a> {
    file: &'a Path,
    decls: &'a Decls,
    out: &'a mut Vec<Violation>,
}

impl FfiLint<'_> {
    fn push(&mut self, span: proc_macro2::Span, rule: &'static str, message: impl Into<String>) {
        self.out.push(Violation {
            file: self.file.to_path_buf(),
            line: line_of(span),
            rule,
            message: message.into(),
        });
    }

    fn check_type(&mut self, ty: &syn::Type, ctx: Ctx, what: &str) {
        match ty {
            syn::Type::Ptr(p) => self.check_type(&p.elem, Ctx::Inner, what),
            syn::Type::Array(a) => self.check_type(&a.elem, Ctx::Inner, what),
            syn::Type::Paren(p) => self.check_type(&p.elem, ctx, what),
            syn::Type::BareFn(f) => self.check_fn_type(f, what),
            syn::Type::Path(p) if p.qself.is_none() => {
                let Some(last) = p.path.segments.last() else {
                    return;
                };
                let name = last.ident.to_string();
                if name == "Option" {
                    if let syn::PathArguments::AngleBracketed(args) = &last.arguments {
                        if let Some(syn::GenericArgument::Type(syn::Type::BareFn(f))) = args.args.first() {
                            self.check_fn_type(f, what);
                            return;
                        }
                    }
                    self.push(
                        ty.span(),
                        "2-types",
                        format!("{what}: Option is allowed only around an extern fn (a callback)"),
                    );
                    return;
                }
                match name.as_str() {
                    "i8" | "i16" | "i32" | "i64" | "u8" | "u16" | "u32" | "u64" | "isize" | "usize" | "f32" | "f64" | "c_void" => {}
                    "bool" => self.push(
                        ty.span(),
                        "4-no-bool",
                        format!("{what}: bool never crosses the boundary; use u8 (0/1)"),
                    ),
                    _ if self.decls.enums.contains(&name) => self.push(
                        ty.span(),
                        "3-no-enum",
                        format!("{what}: the enum {name} cannot cross the boundary; use its i32/u32 and convert with from_raw"),
                    ),
                    _ if self.decls.structs.contains(&name) => {
                        if ctx == Ctx::Param {
                            self.push(
                                ty.span(),
                                "5-no-struct-by-value",
                                format!("{what}: {name} is passed by value; pass *const {name}"),
                            );
                        }
                    }
                    _ if self.decls.aliases.contains(&name) => {}
                    _ => self.push(
                        ty.span(),
                        "2-types",
                        format!("{what}: {name} is not a primitive, a pointer or a type declared in src/ffi"),
                    ),
                }
            }
            syn::Type::Tuple(t) if t.elems.is_empty() && ctx == Ctx::Inner => {}
            _ => self.push(ty.span(), "2-types", format!("{what}: {} cannot cross the boundary", tokens(ty))),
        }
    }

    fn check_fn_type(&mut self, f: &syn::TypeBareFn, what: &str) {
        for input in &f.inputs {
            self.check_type(&input.ty, Ctx::Param, &format!("{what} (callback parameter)"));
        }
        if let syn::ReturnType::Type(_, ty) = &f.output {
            self.check_type(ty, Ctx::Return, &format!("{what} (callback return)"));
        }
    }

    fn check_cfg(&mut self, attrs: &[syn::Attribute], what: &str, allow_cfg: bool) {
        for attr in attrs {
            if attr.path().is_ident("cfg_attr") {
                self.push(attr.span(), "1-no-cfg", format!("{what}: #[cfg_attr] is not allowed under src/ffi"));
            } else if attr.path().is_ident("cfg") && !allow_cfg {
                self.push(
                    attr.span(),
                    "1-no-cfg",
                    format!("{what}: #[cfg] is not allowed on an item under src/ffi (switch inside the body)"),
                );
            }
        }
    }

    fn items(&mut self, items: &[syn::Item], seen: &mut HashMap<String, PathBuf>) {
        for item in items {
            self.item(item, seen);
        }
    }

    fn item(&mut self, item: &syn::Item, seen: &mut HashMap<String, PathBuf>) {
        match item {
            syn::Item::Use(u) => self.check_cfg(&u.attrs, "use", true),
            syn::Item::Mod(m) => {
                let test = m.attrs.len()
                    == m.attrs
                        .iter()
                        .filter(|a| !a.path().is_ident("cfg") || is_bare_cfg_test(a))
                        .count()
                    && in_test_mod(&m.attrs);
                if test {
                    self.check_cfg(&m.attrs, "mod", true);
                    if let Some((_, items)) = &m.content {
                        self.test_mod_contents(items);
                    }
                } else {
                    self.check_cfg(&m.attrs, &format!("mod {}", m.ident), false);
                    if let Some((_, items)) = &m.content {
                        self.items(items, seen);
                    }
                }
            }
            syn::Item::Fn(f) => {
                let name = f.sig.ident.to_string();
                self.check_cfg(&f.attrs, &format!("fn {name}"), false);
                if has_attr(&f.attrs, "no_mangle") {
                    self.export(f, seen);
                }
            }
            syn::Item::Struct(s) => {
                let name = s.ident.to_string();
                self.check_cfg(&s.attrs, &format!("struct {name}"), false);
                for field in &s.fields {
                    self.check_cfg(&field.attrs, &format!("a field of {name}"), false);
                }
                if repr_of(&s.attrs).is_some_and(|r| r.contains('C')) {
                    for field in &s.fields {
                        let field_name = field
                            .ident
                            .as_ref()
                            .map(ToString::to_string)
                            .unwrap_or_default();
                        self.check_type(&field.ty, Ctx::Field, &format!("{name}.{field_name}"));
                    }
                }
            }
            syn::Item::Enum(e) => {
                let name = e.ident.to_string();
                self.check_cfg(&e.attrs, &format!("enum {name}"), false);
                match repr_of(&e.attrs).as_deref() {
                    Some("repr(i32)") | Some("repr(u32)") | Some("repr(u8)") => {}
                    _ => self.push(
                        e.ident.span(),
                        "8-enum-repr",
                        format!("enum {name} needs #[repr(i32)], #[repr(u32)] or #[repr(u8)]"),
                    ),
                }
                for variant in &e.variants {
                    self.check_cfg(&variant.attrs, &format!("{name}::{}", variant.ident), false);
                    if variant.discriminant.is_none() || !matches!(variant.fields, syn::Fields::Unit) {
                        self.push(
                            variant.ident.span(),
                            "8-enum-repr",
                            format!("{name}::{} needs an explicit discriminant", variant.ident),
                        );
                    }
                }
            }
            syn::Item::Const(c) => {
                self.check_cfg(&c.attrs, &format!("const {}", c.ident), false);
                let public = matches!(c.vis, syn::Visibility::Public(_));
                let literal = matches!(&*c.expr, syn::Expr::Lit(_));
                let generated_name = c.ident.to_string().starts_with("PRISC_");
                if public && !literal {
                    self.push(
                        c.ident.span(),
                        "6-const-literal",
                        format!(
                            "pub const {} must be a non-negative literal (csbindgen drops anything else)",
                            c.ident
                        ),
                    );
                }
                // C# gets exactly the `pub const PRISC_*` literals (abi.rs filters csbindgen by name,
                // since csbindgen reads every const whatever its visibility)
                if public != generated_name && (public || literal) {
                    self.push(
                        c.ident.span(),
                        "6-const-literal",
                        format!(
                            "const {}: a pub const is named PRISC_* (it is generated into C#), and only a pub const may \
                             be a PRISC_* literal",
                            c.ident
                        ),
                    );
                }
            }
            syn::Item::Type(t) => {
                self.check_cfg(&t.attrs, &format!("type {}", t.ident), false);
                self.check_type(&t.ty, Ctx::Inner, &format!("type {}", t.ident));
            }
            syn::Item::Static(s) => self.check_cfg(&s.attrs, &format!("static {}", s.ident), false),
            syn::Item::Impl(i) => {
                self.check_cfg(&i.attrs, "impl", false);
                for item in &i.items {
                    if let syn::ImplItem::Fn(f) = item {
                        self.check_cfg(&f.attrs, &format!("fn {}", f.sig.ident), false);
                    }
                }
            }
            syn::Item::Macro(m) => self.check_cfg(&m.attrs, "macro", false),
            syn::Item::Trait(t) => self.check_cfg(&t.attrs, &format!("trait {}", t.ident), false),
            _ => {}
        }
    }

    /// A `#[cfg(test)]` module may hold tests, never something csbindgen would collect.
    fn test_mod_contents(&mut self, items: &[syn::Item]) {
        for item in items {
            match item {
                syn::Item::Fn(f) if has_attr(&f.attrs, "no_mangle") => self.push(
                    f.sig.ident.span(),
                    "1-no-cfg",
                    "a #[cfg(test)] module may not hold a #[no_mangle] fn",
                ),
                syn::Item::Struct(s) if repr_of(&s.attrs).is_some_and(|r| r.contains('C')) => {
                    self.push(s.ident.span(), "1-no-cfg", "a #[cfg(test)] module may not hold a #[repr(C)] struct")
                }
                syn::Item::Const(c) if matches!(c.vis, syn::Visibility::Public(_)) || c.ident.to_string().starts_with("PRISC_") => self
                    .push(
                        c.ident.span(),
                        "1-no-cfg",
                        "a #[cfg(test)] module may not hold a pub or PRISC_* const",
                    ),
                syn::Item::Enum(e) => self.push(e.ident.span(), "1-no-cfg", "a #[cfg(test)] module may not hold an enum"),
                syn::Item::Mod(m) => {
                    if let Some((_, items)) = &m.content {
                        self.test_mod_contents(items);
                    }
                }
                _ => {}
            }
        }
    }

    fn export(&mut self, f: &syn::ItemFn, seen: &mut HashMap<String, PathBuf>) {
        let name = f.sig.ident.to_string();
        let span = f.sig.ident.span();
        if !name.starts_with("prisc_") {
            self.push(span, "7-export", format!("{name} must start with prisc_"));
        }
        if let Some(other) = seen.insert(name.clone(), self.file.to_path_buf()) {
            self.push(span, "7-export", format!("{name} is also exported from {}", other.display()));
        }
        let abi_c = f
            .sig
            .abi
            .as_ref()
            .and_then(|abi| abi.name.as_ref())
            .is_some_and(|n| n.value() == "C");
        if f.sig.unsafety.is_none() || !abi_c {
            self.push(span, "7-export", format!("{name} must be `pub unsafe extern \"C\" fn`"));
        }
        let reader = LAST_ERROR_READERS
            .iter()
            .find(|(n, _)| *n == name);
        let expected = reader.map_or("i32", |(_, ret)| *ret);
        let returns = match &f.sig.output {
            syn::ReturnType::Type(_, ty) => tokens(ty),
            syn::ReturnType::Default => "()".into(),
        };
        if returns != expected {
            self.push(span, "7-export", format!("{name} must return {expected} (returns {returns})"));
        }
        let threads: Vec<String> = doc_lines(&f.attrs)
            .into_iter()
            .filter(|line| line.starts_with("Thread:"))
            .collect();
        match threads.as_slice() {
            [line] if THREAD_LINES.contains(&line.as_str()) => {}
            [line] => self.push(
                span,
                "7-thread-line",
                format!("{name}: `{line}` is not one of the README D5 Thread lines"),
            ),
            lines => self.push(
                span,
                "7-thread-line",
                format!("{name} needs exactly one `Thread:` doc line (has {})", lines.len()),
            ),
        }
        for input in &f.sig.inputs {
            if let syn::FnArg::Typed(pat) = input {
                let arg = tokens(&pat.pat);
                self.check_type(&pat.ty, Ctx::Param, &format!("{name}({arg})"));
            }
        }
        if reader.is_none() && !first_stmt_calls(&f.block, "guard", Some(&name)) {
            self.push(
                span,
                "7-guard",
                format!("{name}: the body's first statement must be panic::guard(\"{name}\", ...)"),
            );
        }
    }
}

/// Whether the block's first statement is a call to `panic::<function>` (optionally with the
/// string literal `name` as its first argument).
fn first_stmt_calls(block: &syn::Block, function: &str, name: Option<&str>) -> bool {
    let Some(first) = block.stmts.first() else {
        return false;
    };
    let expr = match first {
        syn::Stmt::Expr(expr, _) => expr,
        syn::Stmt::Local(local) => match &local.init {
            Some(init) => &*init.expr,
            None => return false,
        },
        _ => return false,
    };
    let syn::Expr::Call(call) = expr else {
        return false;
    };
    let syn::Expr::Path(path) = &*call.func else {
        return false;
    };
    let segments: Vec<String> = path
        .path
        .segments
        .iter()
        .map(|s| s.ident.to_string())
        .collect();
    let ok_path = segments.len() >= 2 && segments[segments.len() - 1] == function && segments[segments.len() - 2] == "panic";
    if !ok_path {
        return false;
    }
    match name {
        None => true,
        Some(name) => matches!(
            call.args.first(),
            Some(syn::Expr::Lit(syn::ExprLit { lit: syn::Lit::Str(s), .. })) if s.value() == name
        ),
    }
}

/// Rules 1 to 8 over the ffi files.
pub fn lint_ffi(files: &[(PathBuf, String)]) -> Vec<Violation> {
    let mut out = Vec::new();
    let mut parsed = Vec::new();
    for (path, text) in files {
        match syn::parse_file(text) {
            Ok(file) => parsed.push((path, file)),
            Err(e) => out.push(Violation {
                file: path.clone(),
                line: line_of(e.span()),
                rule: "parse",
                message: e.to_string(),
            }),
        }
    }
    let mut decls = Decls::default();
    for (_, file) in &parsed {
        collect_decls(&file.items, &mut decls);
    }
    let mut seen = HashMap::new();
    for (path, file) in &parsed {
        let mut lint = FfiLint {
            file: path,
            decls: &decls,
            out: &mut out,
        };
        lint.items(&file.items, &mut seen);
    }
    lint_ffi_enum_calls(&parsed, &mut out);
    out
}

/// One enum declared under src/ffi, for rule 13.
struct EnumDecl {
    file: PathBuf,
    line: usize,
    repr: Option<String>,
    variants: Vec<String>,
}

/// The arguments of `ffi_enum!(Name: raw { A, B })`.
struct FfiEnumCall {
    name: syn::Ident,
    raw: syn::Ident,
    variants: Vec<syn::Ident>,
}

impl syn::parse::Parse for FfiEnumCall {
    fn parse(input: syn::parse::ParseStream) -> syn::Result<Self> {
        let name = input.parse()?;
        input.parse::<syn::Token![:]>()?;
        let raw = input.parse()?;
        let content;
        syn::braced!(content in input);
        let variants = syn::punctuated::Punctuated::<syn::Ident, syn::Token![,]>::parse_terminated(&content)?
            .into_iter()
            .collect();
        Ok(Self {
            name,
            raw,
            variants,
        })
    }
}

fn collect_enum_decls(items: &[syn::Item], file: &Path, out: &mut BTreeMap<String, EnumDecl>) {
    for item in items {
        match item {
            syn::Item::Enum(e) => {
                out.insert(
                    e.ident.to_string(),
                    EnumDecl {
                        file: file.to_path_buf(),
                        line: line_of(e.ident.span()),
                        repr: repr_of(&e.attrs).map(|r| {
                            r.trim_start_matches("repr(")
                                .trim_end_matches(')')
                                .to_string()
                        }),
                        variants: e
                            .variants
                            .iter()
                            .map(|v| v.ident.to_string())
                            .collect(),
                    },
                );
            }
            syn::Item::Mod(m) if !in_test_mod(&m.attrs) => {
                if let Some((_, items)) = &m.content {
                    collect_enum_decls(items, file, out);
                }
            }
            _ => {}
        }
    }
}

fn collect_ffi_enum_calls<'a>(items: &'a [syn::Item], file: &'a Path, out: &mut Vec<(&'a Path, &'a syn::ItemMacro)>) {
    for item in items {
        match item {
            syn::Item::Macro(m)
                if m.mac
                    .path
                    .segments
                    .last()
                    .is_some_and(|s| s.ident == "ffi_enum") =>
            {
                out.push((file, m));
            }
            syn::Item::Mod(m) if !in_test_mod(&m.attrs) => {
                if let Some((_, items)) = &m.content {
                    collect_ffi_enum_calls(items, file, out);
                }
            }
            _ => {}
        }
    }
}

/// Rule 13: the `ffi_enum!` lists match the enum declarations one for one.
fn lint_ffi_enum_calls(parsed: &[(&PathBuf, syn::File)], out: &mut Vec<Violation>) {
    let mut enums = BTreeMap::new();
    let mut calls = Vec::new();
    for (path, file) in parsed {
        collect_enum_decls(&file.items, path, &mut enums);
        collect_ffi_enum_calls(&file.items, path, &mut calls);
    }
    let mut covered = HashSet::new();
    for (file, mac) in calls {
        let mut push = |line: usize, message: String| {
            out.push(Violation {
                file: file.to_path_buf(),
                line,
                rule: "13-ffi-enum",
                message,
            });
        };
        let call: FfiEnumCall = match mac.mac.parse_body() {
            Ok(call) => call,
            Err(e) => {
                push(line_of(e.span()), format!("ffi_enum! needs `Name: raw {{ A, B }}`: {e}"));
                continue;
            }
        };
        let name = call.name.to_string();
        let line = line_of(call.name.span());
        let Some(decl) = enums.get(&name) else {
            push(line, format!("ffi_enum!({name}) names no enum declared under src/ffi"));
            continue;
        };
        if name.ends_with("Flags") {
            push(
                line,
                format!("{name} is a bit-flag enum; check it with convert::flags, not ffi_enum!"),
            );
            continue;
        }
        if !covered.insert(name.clone()) {
            push(line, format!("{name} has a second ffi_enum! invocation"));
            continue;
        }
        let raw = call.raw.to_string();
        if decl.repr.as_deref() != Some(raw.as_str()) {
            push(
                line,
                format!(
                    "ffi_enum!({name}: {raw}) does not match the enum's #[repr({})]",
                    decl.repr.as_deref().unwrap_or("?")
                ),
            );
        }
        let listed: Vec<String> = call
            .variants
            .iter()
            .map(ToString::to_string)
            .collect();
        let missing: Vec<&str> = decl
            .variants
            .iter()
            .filter(|v| !listed.contains(v))
            .map(String::as_str)
            .collect();
        let extra: Vec<&str> = listed
            .iter()
            .filter(|v| !decl.variants.contains(v))
            .map(String::as_str)
            .collect();
        let mut sorted = listed.clone();
        sorted.sort();
        sorted.dedup();
        if !missing.is_empty() || !extra.is_empty() || sorted.len() != listed.len() {
            push(
                line,
                format!(
                    "ffi_enum!({name}) must list exactly the enum's variants (missing: [{}], unknown: [{}]{})",
                    missing.join(", "),
                    extra.join(", "),
                    if sorted.len() != listed.len() { ", with duplicates" } else { "" }
                ),
            );
        }
    }
    for (name, decl) in &enums {
        if !name.ends_with("Flags") && !covered.contains(name) {
            out.push(Violation {
                file: decl.file.clone(),
                line: decl.line,
                rule: "13-ffi-enum",
                message: format!("enum {name} needs a crate::convert::ffi_enum! invocation next to it (README B2)"),
            });
        }
    }
}

fn collect_decls(items: &[syn::Item], decls: &mut Decls) {
    for item in items {
        match item {
            syn::Item::Struct(s) => {
                decls.structs.insert(s.ident.to_string());
            }
            syn::Item::Enum(e) => {
                decls.enums.insert(e.ident.to_string());
            }
            syn::Item::Type(t) => {
                decls.aliases.insert(t.ident.to_string());
            }
            syn::Item::Mod(m) if !in_test_mod(&m.attrs) => {
                if let Some((_, items)) = &m.content {
                    collect_decls(items, decls);
                }
            }
            _ => {}
        }
    }
}

/// The crate-wide rules over `crates/prisc_interop/src/**`: no export called from Rust, no thread
/// spawned (D4), every platform trampoline guarded (C5).
pub fn lint_crate(files: &[(PathBuf, String)], exports: &HashSet<String>) -> Vec<Violation> {
    let mut out = Vec::new();
    for (path, text) in files {
        let Ok(file) = syn::parse_file(text) else {
            continue;
        };
        let platform = path
            .to_string_lossy()
            .replace('\\', "/")
            .contains("/src/platform/");
        let mut visitor = CrateVisitor {
            file: path,
            exports,
            platform,
            out: &mut out,
        };
        visitor.visit_file(&file);
    }
    out
}

struct CrateVisitor<'a> {
    file: &'a Path,
    exports: &'a HashSet<String>,
    platform: bool,
    out: &'a mut Vec<Violation>,
}

impl CrateVisitor<'_> {
    fn push(&mut self, span: proc_macro2::Span, rule: &'static str, message: String) {
        self.out.push(Violation {
            file: self.file.to_path_buf(),
            line: line_of(span),
            rule,
            message,
        });
    }

    fn check_path(&mut self, path: &syn::Path) {
        let segments: Vec<String> = path
            .segments
            .iter()
            .map(|s| s.ident.to_string())
            .collect();
        for pair in segments.windows(2) {
            if pair[0] == "thread" && matches!(pair[1].as_str(), "spawn" | "Builder" | "scope") {
                self.push(
                    path.span(),
                    "10-no-threads",
                    format!("prisc_interop spawns no thread (thread::{}); README D4", pair[1]),
                );
            }
        }
    }

    fn check_trampoline(&mut self, sig: &syn::Signature, block: &syn::Block, attrs: &[syn::Attribute]) {
        if self.platform && sig.abi.is_some() && !has_attr(attrs, "no_mangle") && !first_stmt_calls(block, "callback_guard", None) {
            self.push(
                sig.ident.span(),
                "11-callback-guard",
                format!(
                    "{}: an OS-called extern fn must start with panic::callback_guard (README C5)",
                    sig.ident
                ),
            );
        }
    }
}

impl<'ast> Visit<'ast> for CrateVisitor<'_> {
    fn visit_item_mod(&mut self, m: &'ast syn::ItemMod) {
        if !in_test_mod(&m.attrs) {
            syn::visit::visit_item_mod(self, m);
        }
    }

    fn visit_item_fn(&mut self, f: &'ast syn::ItemFn) {
        if in_test_mod(&f.attrs) {
            return;
        }
        self.check_trampoline(&f.sig, &f.block, &f.attrs);
        syn::visit::visit_item_fn(self, f);
    }

    fn visit_impl_item_fn(&mut self, f: &'ast syn::ImplItemFn) {
        self.check_trampoline(&f.sig, &f.block, &f.attrs);
        syn::visit::visit_impl_item_fn(self, f);
    }

    fn visit_expr_call(&mut self, call: &'ast syn::ExprCall) {
        if let syn::Expr::Path(path) = &*call.func {
            if let Some(last) = path.path.segments.last() {
                let name = last.ident.to_string();
                if self.exports.contains(&name) {
                    self.push(
                        path.span(),
                        "7-no-export-calls",
                        format!("Rust code calls the export {name}; call its implementation instead"),
                    );
                }
            }
        }
        syn::visit::visit_expr_call(self, call);
    }

    fn visit_path(&mut self, path: &'ast syn::Path) {
        self.check_path(path);
        syn::visit::visit_path(self, path);
    }

    fn visit_macro(&mut self, mac: &'ast syn::Macro) {
        if is_define_class(mac) {
            self.push(
                mac.path.span(),
                "14-exported-statics",
                "define_class! gives the class #[export_name] statics the dylib would export; build it at run time with \
                 objc2's ClassBuilder (README A1)"
                    .into(),
            );
        }
        syn::visit::visit_macro(self, mac);
    }

    fn visit_attribute(&mut self, attr: &'ast syn::Attribute) {
        if has_attr(std::slice::from_ref(attr), "export_name") {
            self.push(
                attr.span(),
                "14-exported-statics",
                "#[export_name] would add a symbol to the dylib's exports beside prisc_* (README A1)".into(),
            );
        }
        syn::visit::visit_attribute(self, attr);
    }

    fn visit_item_use(&mut self, u: &'ast syn::ItemUse) {
        let text = tokens(&u.tree).replace(' ', "");
        for bad in ["thread::spawn", "thread::Builder", "thread::scope", "thread::{"] {
            if text.contains(bad)
                && (bad != "thread::{"
                    || ["spawn", "Builder", "scope"]
                        .iter()
                        .any(|n| text.contains(n)))
            {
                self.push(
                    u.span(),
                    "10-no-threads",
                    format!("prisc_interop spawns no thread (use {text}); README D4"),
                );
                break;
            }
        }
    }
}

/// Rule 9: no U+2014 in the text files under `dir`.
pub fn lint_em_dash(root: &Path, dir: &Path) -> anyhow::Result<Vec<Violation>> {
    let mut out = Vec::new();
    let mut stack = vec![dir.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let name = entry
                .file_name()
                .to_string_lossy()
                .into_owned();
            if path.is_dir() {
                if !matches!(name.as_str(), "target" | "bin" | "obj") && !name.starts_with('.') {
                    stack.push(path);
                }
                continue;
            }
            let Ok(text) = std::fs::read_to_string(&path) else {
                continue;
            };
            for (i, line) in text.lines().enumerate() {
                if line.contains(EM_DASH) {
                    out.push(Violation {
                        file: relative(root, &path),
                        line: i + 1,
                        rule: "9-no-em-dash",
                        message: "U+2014 (em-dash) is not allowed (README D7)".into(),
                    });
                }
            }
        }
    }
    Ok(out)
}

/// Every `*.rs` under `src/` of every path dependency of prisc_interop (transitively, normal
/// dependencies only), from `cargo metadata`.
pub fn dependency_sources(root: &Path) -> anyhow::Result<Vec<(PathBuf, String)>> {
    let cargo = std::env::var("CARGO").unwrap_or_else(|_| "cargo".into());
    let output = std::process::Command::new(cargo)
        .args(["metadata", "--format-version", "1"])
        .current_dir(root)
        .output()?;
    anyhow::ensure!(
        output.status.success(),
        "cargo metadata failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let meta: serde_json::Value = serde_json::from_slice(&output.stdout)?;
    let packages: BTreeMap<String, &serde_json::Value> = meta["packages"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|p| Some((p["id"].as_str()?.to_string(), p)))
        .collect();
    let nodes: HashMap<String, &serde_json::Value> = meta["resolve"]["nodes"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|n| Some((n["id"].as_str()?.to_string(), n)))
        .collect();
    let start = packages
        .iter()
        .find(|(_, p)| p["name"] == "prisc_interop" && p["source"].is_null())
        .map(|(id, _)| id.clone())
        .ok_or_else(|| anyhow::anyhow!("cargo metadata has no prisc_interop"))?;
    let mut seen = HashSet::new();
    let mut stack = vec![start.clone()];
    let mut path_deps = Vec::new();
    while let Some(id) = stack.pop() {
        if !seen.insert(id.clone()) {
            continue;
        }
        if id != start {
            if let Some(p) = packages.get(&id) {
                if !p["source"].is_null() {
                    continue;
                }
                path_deps.push(*p);
            }
        }
        let Some(node) = nodes.get(&id) else {
            continue;
        };
        for dep in node["deps"].as_array().into_iter().flatten() {
            let normal = dep["dep_kinds"]
                .as_array()
                .into_iter()
                .flatten()
                .any(|k| k["kind"].is_null());
            if normal {
                if let Some(pkg) = dep["pkg"].as_str() {
                    stack.push(pkg.to_string());
                }
            }
        }
    }
    let mut files = Vec::new();
    for p in path_deps {
        let manifest = PathBuf::from(
            p["manifest_path"]
                .as_str()
                .unwrap_or_default(),
        );
        if let Some(dir) = manifest.parent() {
            let src = dir.join("src");
            if src.is_dir() {
                files.extend(read_rs(root, &src, true)?);
            }
        }
    }
    files.sort();
    Ok(files)
}

/// Rule 12: no `#[no_mangle]` or `#[export_name]` item and no `define_class!` in a dependency
/// (ld64 keeps one of two duplicate symbols silently, and the cdylib would export the rest).
pub fn lint_foreign_exports(files: &[(PathBuf, String)]) -> Vec<Violation> {
    let mut out = Vec::new();
    for (path, text) in files {
        let Ok(file) = syn::parse_file(text) else {
            continue;
        };
        foreign(&file.items, path, &mut out);
    }
    out
}

/// `define_class!` or `objc2::define_class!`.
fn is_define_class(mac: &syn::Macro) -> bool {
    mac.path
        .segments
        .last()
        .is_some_and(|segment| segment.ident == "define_class")
}

fn foreign(items: &[syn::Item], path: &Path, out: &mut Vec<Violation>) {
    for item in items {
        let (attrs, name, span) = match item {
            syn::Item::Fn(f) => (&f.attrs, f.sig.ident.to_string(), f.sig.ident.span()),
            syn::Item::Static(s) => (&s.attrs, s.ident.to_string(), s.ident.span()),
            syn::Item::Macro(m) if is_define_class(&m.mac) => {
                out.push(Violation {
                    file: path.to_path_buf(),
                    line: line_of(m.mac.path.span()),
                    rule: "12-foreign-export",
                    message: "define_class! in a dependency of prisc_interop would export its class statics".into(),
                });
                continue;
            }
            syn::Item::Mod(m) => {
                if !in_test_mod(&m.attrs) {
                    if let Some((_, items)) = &m.content {
                        foreign(items, path, out);
                    }
                }
                continue;
            }
            _ => continue,
        };
        if has_attr(attrs, "no_mangle") || has_attr(attrs, "export_name") {
            out.push(Violation {
                file: path.to_path_buf(),
                line: line_of(span),
                rule: "12-foreign-export",
                message: format!(
                    "#[no_mangle] or #[export_name] {name} in a dependency of prisc_interop would collide with or shadow its \
                     exports"
                ),
            });
        }
    }
}
