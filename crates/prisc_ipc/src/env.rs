//! The environment variables more than one Prisc process reads, named once.
//!
//! prisc_ipc is the lowest crate every reader shares (prisc_ipc itself reads
//! [`HELPERS_DIR`], prisc_rust_core's telemetry reads [`DISABLE_TELEMETRY`]), and the C# side gets
//! the same names generated into `ui/Prisc.Interop/Generated/EnvVars.g.cs` (`cargo codegen`), so a
//! variable is spelled exactly once in the repo.

/// One shared environment variable: the C# constant name, the variable itself and what it does.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct EnvVar {
    /// The PascalCase name of the generated C# constant.
    pub name: &'static str,
    /// The variable's name in the environment.
    pub value: &'static str,
    /// One or two sentences, carried into the generated C# doc comment.
    pub doc: &'static str,
}

/// A path list (`;` on Windows, `:` elsewhere) of directories probed for helpers and native
/// libraries before the application's own directory (HELPER_PROTOCOL.md section 7.1).
pub const HELPERS_DIR: &str = "PRISC_HELPERS_DIR";

/// Any non-empty value turns crash and error reporting off in every process.
pub const DISABLE_TELEMETRY: &str = "PRISC_DISABLE_TELEMETRY";

/// The prisc_interop library to load, authoritative: set but not naming an existing file means
/// the library is unavailable (no fallback to the normal probe).
pub const INTEROP_PATH: &str = "PRISC_INTEROP_PATH";

/// `1` makes every test that needs prisc_interop fail instead of skipping when the library is
/// missing (CI sets it).
pub const INTEROP_REQUIRED: &str = "PRISC_INTEROP_REQUIRED";

/// `1` lets the GPU device pick a software (WARP) adapter on Windows; tests only.
pub const GPU_ALLOW_WARP: &str = "PRISC_GPU_ALLOW_WARP";

/// `cpu` forces the video renderer onto the raster (CPU) composition path.
pub const RENDER_BACKEND: &str = "PRISC_RENDER_BACKEND";

/// Every shared variable, in the order the generated C# lists them.
pub const ALL: &[EnvVar] = &[
    EnvVar {
        name: "HelpersDir",
        value: HELPERS_DIR,
        doc: "A path list (; on Windows, : elsewhere) of directories probed for helpers and native libraries before the \
              application's own directory (HELPER_PROTOCOL.md section 7.1).",
    },
    EnvVar {
        name: "DisableTelemetry",
        value: DISABLE_TELEMETRY,
        doc: "Any non-empty value turns crash and error reporting off in every process.",
    },
    EnvVar {
        name: "InteropPath",
        value: INTEROP_PATH,
        doc: "The prisc_interop library to load, authoritative: set but not naming an existing file means the library is \
              unavailable.",
    },
    EnvVar {
        name: "InteropRequired",
        value: INTEROP_REQUIRED,
        doc: "1 makes every test that needs prisc_interop fail instead of skipping when the library is missing (CI sets it).",
    },
    EnvVar {
        name: "GpuAllowWarp",
        value: GPU_ALLOW_WARP,
        doc: "1 lets the GPU device pick a software (WARP) adapter on Windows; tests only.",
    },
    EnvVar {
        name: "RenderBackend",
        value: RENDER_BACKEND,
        doc: "cpu forces the video renderer onto the raster (CPU) composition path.",
    },
];

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
            assert!(!var.doc.is_empty());
            for other in &ALL[i + 1..] {
                assert_ne!(var.name, other.name);
                assert_ne!(var.value, other.value);
            }
        }
    }

    #[test]
    fn the_locator_reads_the_shared_name() {
        assert_eq!(crate::locate::HELPERS_DIR_VAR, HELPERS_DIR);
    }
}
