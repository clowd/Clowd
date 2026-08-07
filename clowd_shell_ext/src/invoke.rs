//! Pure logic for the shell extension: locating Clowd.Ui.exe relative to the DLL,
//! splitting a selection across command lines and building each one. Kept free of
//! Win32 calls so the functions can be unit-tested on any platform.

use std::path::{Path, PathBuf};

pub const EXE_NAME: &str = "Clowd.Ui.exe";

/// The app's explicit CLI command for uploads (`Clowd.Ui.exe upload "path" ...`).
/// Bare paths still work over there as a legacy fallback, but every launch from
/// here names the command so the surface can grow more commands and options.
pub const UPLOAD_COMMAND: &str = "upload";

/// CreateProcessW rejects a command line of 32,767 UTF-16 units or more, so a big
/// enough selection has to be spread over several launches. Leave headroom below
/// the hard cap rather than sitting on it.
const COMMAND_LINE_BUDGET: usize = 32_000;

/// Locate Clowd.Ui.exe relative to the shell extension DLL. The installed layout
/// places the DLL at the Velopack root with the app under `current\`; the plain
/// sibling probe covers dev trees where everything sits in one directory.
pub fn resolve_exe(dll_path: &Path, exists: &dyn Fn(&Path) -> bool) -> Option<PathBuf> {
    let dir = dll_path.parent()?;
    let installed = dir.join("current").join(EXE_NAME);
    if exists(&installed) {
        return Some(installed);
    }
    let sibling = dir.join(EXE_NAME);
    if exists(&sibling) {
        return Some(sibling);
    }
    None
}

/// Split the selection into groups that each fit in one command line, preserving
/// order. The app coalesces the resulting launches back into a single batch, so the
/// only visible effect is that a huge selection still works. A path so long that it
/// cannot fit on a command line of its own is dropped — nothing can carry it.
pub fn chunk_paths(exe: &str, paths: &[String]) -> Vec<Vec<String>> {
    let base = quoted_len_utf16(exe) + 1 + quoted_len_utf16(UPLOAD_COMMAND);
    let mut chunks = Vec::new();
    let mut current: Vec<String> = Vec::new();
    let mut used = base;

    for path in paths {
        let cost = 1 + quoted_len_utf16(path); // separating space + the quoted path
        if base + cost > COMMAND_LINE_BUDGET {
            continue;
        }
        if used + cost > COMMAND_LINE_BUDGET {
            chunks.push(std::mem::take(&mut current));
            used = base;
        }
        used += cost;
        current.push(path.clone());
    }

    if !current.is_empty() {
        chunks.push(current);
    }
    chunks
}

/// Length in UTF-16 units of what `build_command_line` would emit for one argument,
/// measured by running the same quoting so the two can never disagree.
fn quoted_len_utf16(arg: &str) -> usize {
    let mut quoted = String::new();
    append_quoted(arg, &mut quoted);
    quoted.encode_utf16().count()
}

/// Build a CreateProcessW command line: the exe, the `upload` command, then one
/// argument per path, each quoted so that CommandLineToArgvW reproduces the
/// argument exactly.
pub fn build_command_line(exe: &str, args: &[String]) -> String {
    let mut line = String::new();
    append_quoted(exe, &mut line);
    line.push(' ');
    append_quoted(UPLOAD_COMMAND, &mut line);
    for arg in args {
        line.push(' ');
        append_quoted(arg, &mut line);
    }
    line
}

// CommandLineToArgvW rules: inside quotes a backslash run is literal unless it
// precedes a quote, in which case 2n backslashes yield n literal ones (quote is a
// delimiter) and 2n+1 yield n plus a literal quote.
fn append_quoted(arg: &str, line: &mut String) {
    line.push('"');
    let mut backslashes = 0;
    for ch in arg.chars() {
        match ch {
            '\\' => backslashes += 1,
            '"' => {
                line.extend(std::iter::repeat_n('\\', backslashes * 2 + 1));
                line.push('"');
                backslashes = 0;
            }
            _ => {
                line.extend(std::iter::repeat_n('\\', backslashes));
                line.push(ch);
                backslashes = 0;
            }
        }
    }
    // double a trailing backslash run so it cannot escape the closing quote
    line.extend(std::iter::repeat_n('\\', backslashes * 2));
    line.push('"');
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn names_the_upload_command_first() {
        let line = build_command_line(r"C:\app\Clowd.Ui.exe", &args(&[r"C:\files\a.txt"]));
        assert_eq!(line, r#""C:\app\Clowd.Ui.exe" "upload" "C:\files\a.txt""#);
    }

    #[test]
    fn quotes_paths_with_spaces() {
        let line = build_command_line(r"C:\my apps\Clowd.Ui.exe", &args(&[r"C:\my files\a b.txt", r"C:\other\c.png"]));
        assert_eq!(line, r#""C:\my apps\Clowd.Ui.exe" "upload" "C:\my files\a b.txt" "C:\other\c.png""#);
    }

    #[test]
    fn doubles_trailing_backslashes() {
        let line = build_command_line("exe", &args(&[r"C:\dir\"]));
        assert_eq!(line, r#""exe" "upload" "C:\dir\\""#);
        let line = build_command_line("exe", &args(&[r"C:\dir\\"]));
        assert_eq!(line, r#""exe" "upload" "C:\dir\\\\""#);
    }

    #[test]
    fn escapes_embedded_quotes() {
        let line = build_command_line("exe", &args(&[r#"he"llo"#]));
        assert_eq!(line, r#""exe" "upload" "he\"llo""#);
    }

    #[test]
    fn escapes_backslashes_before_quotes() {
        // one backslash + quote -> 2n+1 = 3 backslashes + quote
        let line = build_command_line("exe", &args(&[r#"a\"b"#]));
        assert_eq!(line, r#""exe" "upload" "a\\\"b""#);
        // two backslashes + quote -> 5 backslashes + quote
        let line = build_command_line("exe", &args(&[r#"a\\"b"#]));
        assert_eq!(line, r#""exe" "upload" "a\\\\\"b""#);
    }

    #[test]
    fn quotes_empty_argument() {
        let line = build_command_line("exe", &args(&[""]));
        assert_eq!(line, r#""exe" "upload" """#);
    }

    const EXE: &str = r"C:\Program Files\Clowd\current\Clowd.Ui.exe";

    fn path_of_len(len: usize) -> String {
        format!(r"C:\{}", "a".repeat(len - 3))
    }

    fn command_line_len(chunk: &[String]) -> usize {
        build_command_line(EXE, chunk)
            .encode_utf16()
            .count()
    }

    #[test]
    fn keeps_a_small_selection_in_one_chunk() {
        let paths = args(&[r"C:\files\a.txt", r"C:\files\b.txt", r"C:\files\c.txt"]);
        let chunks = chunk_paths(EXE, &paths);
        assert_eq!(chunks, vec![paths]);
    }

    #[test]
    fn returns_no_chunks_for_an_empty_selection() {
        assert!(chunk_paths(EXE, &[]).is_empty());
    }

    #[test]
    fn splits_a_large_selection_within_budget_and_in_order() {
        let paths: Vec<String> = (0..100)
            .map(|i| format!(r"C:\files\{i:0>995}.txt"))
            .collect();
        let chunks = chunk_paths(EXE, &paths);

        assert!(chunks.len() > 1, "a 100 KB selection must span several command lines");
        for chunk in &chunks {
            assert!(!chunk.is_empty());
            assert!(command_line_len(chunk) <= COMMAND_LINE_BUDGET);
        }
        let flattened: Vec<String> = chunks.into_iter().flatten().collect();
        assert_eq!(flattened, paths);
    }

    #[test]
    fn packs_each_chunk_up_to_the_budget() {
        // one more path would not fit, so every chunk but the last must be full
        let paths: Vec<String> = (0..20).map(|_| path_of_len(4000)).collect();
        let chunks = chunk_paths(EXE, &paths);
        let per_path = 1 + quoted_len_utf16(&paths[0]);

        assert!(chunks.len() > 1);
        for chunk in &chunks[..chunks.len() - 1] {
            assert!(command_line_len(chunk) + per_path > COMMAND_LINE_BUDGET);
        }
    }

    #[test]
    fn skips_a_path_that_cannot_fit_on_its_own() {
        let huge = path_of_len(COMMAND_LINE_BUDGET);
        let paths = args(&[r"C:\files\a.txt", huge.as_str(), r"C:\files\b.txt"]);
        let chunks = chunk_paths(EXE, &paths);
        assert_eq!(chunks, vec![args(&[r"C:\files\a.txt", r"C:\files\b.txt"])]);
    }

    #[test]
    fn measures_lengths_in_utf16_units() {
        // a surrogate pair is one char but two command-line units
        assert_eq!(quoted_len_utf16("\u{1D11E}"), 4);
    }

    #[test]
    fn resolves_installed_layout_first() {
        // installed layout: DLL at the Velopack root, exe under current\
        let dll = Path::new("root").join("ClowdShellExt.dll");
        let installed = Path::new("root")
            .join("current")
            .join(EXE_NAME);
        let resolved = resolve_exe(&dll, &|p| p == installed);
        assert_eq!(resolved, Some(installed));
    }

    #[test]
    fn falls_back_to_sibling_exe() {
        let dll = Path::new("root").join("clowd_shell_ext.dll");
        let sibling = Path::new("root").join(EXE_NAME);
        let resolved = resolve_exe(&dll, &|p| p == sibling);
        assert_eq!(resolved, Some(sibling));
    }

    #[test]
    fn prefers_installed_over_sibling() {
        let dll = Path::new("root").join("ClowdShellExt.dll");
        let installed = Path::new("root")
            .join("current")
            .join(EXE_NAME);
        let resolved = resolve_exe(&dll, &|_| true);
        assert_eq!(resolved, Some(installed));
    }

    #[test]
    fn resolves_to_none_when_exe_missing() {
        let dll = Path::new("root").join("ClowdShellExt.dll");
        assert_eq!(resolve_exe(&dll, &|_| false), None);
    }
}
