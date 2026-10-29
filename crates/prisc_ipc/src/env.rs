//! The environment variables more than one Prisc process reads, named once.
//!
//! prisc_ipc is the lowest crate every reader shares, and the C# side gets the same names
//! generated into `ui/Prisc.Interop/Generated/EnvVars.g.cs` (`cargo codegen`), so a reader in
//! either language takes the name from here. prisc_ipc's helper lookup (`locate.rs`) and
//! prisc_rust_core's telemetry already do. Still spelled out by hand, each until the work package
//! that owns the file switches it: `PRISC_DISABLE_TELEMETRY` in `ui/Prisc.Ui/Util/SentryConfig.cs`
//! (WP-PURE, through the generated telemetry contract) and `PRISC_RENDER_BACKEND` in
//! `ui/Prisc.VideoRender/Program.cs` (WP-IPC).
//!
//! Each variable is declared once through `env_vars!`: its doc comment is both the constant's
//! rustdoc and the `doc` the generated C# carries, so the two cannot drift. Write the doc as plain
//! sentences (no Markdown), since it lands in a C# `<summary>` verbatim.

/// One shared environment variable: the C# constant name, the variable itself and what it does.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct EnvVar {
    /// The PascalCase name of the generated C# constant.
    pub name: &'static str,
    /// The variable's name in the environment.
    pub value: &'static str,
    /// The constant's doc comment, its lines joined (each starts with the space `///` leaves; the
    /// generator trims). Carried into the generated C# doc comment.
    pub doc: &'static str,
}

/// Declares each variable's `pub const` and its [`ALL`] entry from one doc comment.
macro_rules! env_vars {
    ($( $(#[doc = $doc:literal])+ $const:ident = $value:literal as $name:literal; )+) => {
        $(
            $(#[doc = $doc])+
            pub const $const: &str = $value;
        )+

        /// Every shared variable, in the order the generated C# lists them.
        pub const ALL: &[EnvVar] = &[
            $( EnvVar { name: $name, value: $value, doc: concat!($($doc),+) }, )+
        ];
    };
}

env_vars! {
    /// A path list (; on Windows, : elsewhere) of directories probed for helpers and native
    /// libraries before the application's own directory (HELPER_PROTOCOL.md section 7.1).
    HELPERS_DIR = "PRISC_HELPERS_DIR" as "HelpersDir";

    /// Any non-empty value turns crash and error reporting off in every process.
    DISABLE_TELEMETRY = "PRISC_DISABLE_TELEMETRY" as "DisableTelemetry";

    /// The prisc_interop library to load, authoritative: set but not naming an existing file means
    /// the library is unavailable (no fallback to the normal probe).
    INTEROP_PATH = "PRISC_INTEROP_PATH" as "InteropPath";

    /// 1 makes every test that needs prisc_interop fail instead of skipping when the library is
    /// missing (CI sets it).
    INTEROP_REQUIRED = "PRISC_INTEROP_REQUIRED" as "InteropRequired";

    /// 1 lets the GPU device pick a software (WARP) adapter on Windows; tests only.
    GPU_ALLOW_WARP = "PRISC_GPU_ALLOW_WARP" as "GpuAllowWarp";

    /// cpu forces the video renderer onto the raster (CPU) composition path.
    RENDER_BACKEND = "PRISC_RENDER_BACKEND" as "RenderBackend";
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_and_values_are_unique_and_prefixed() {
        for (i, var) in ALL.iter().enumerate() {
            assert!(var.value.starts_with("PRISC_"), "{}", var.value);
            assert!(
                var.name
                    .chars()
                    .next()
                    .is_some_and(|c| c.is_ascii_uppercase()),
                "{}",
                var.name
            );
            assert!(!var.doc.trim().is_empty());
            assert!(!var.doc.contains("  "), "a doc line break left a double space: {}", var.doc);
            for other in &ALL[i + 1..] {
                assert_ne!(var.name, other.name);
                assert_ne!(var.value, other.value);
            }
        }
    }

    #[test]
    fn a_doc_reads_as_one_sentence_run() {
        assert_eq!(
            HELPERS_DIR_DOC.trim(),
            "A path list (; on Windows, : elsewhere) of directories probed for helpers and native libraries before the \
             application's own directory (HELPER_PROTOCOL.md section 7.1)."
        );
    }

    const HELPERS_DIR_DOC: &str = ALL[0].doc;
}
