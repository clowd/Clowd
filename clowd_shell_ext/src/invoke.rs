//! Pure logic for the shell extension: locating Clowd.Ui.exe relative to the DLL
//! and building a CreateProcessW command line. Kept free of Win32 calls so the
//! functions can be unit-tested on any platform.

use std::path::{Path, PathBuf};

pub const EXE_NAME: &str = "Clowd.Ui.exe";

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

/// Build a CreateProcessW command line: the exe followed by one argument per path,
/// each quoted so that CommandLineToArgvW reproduces the argument exactly.
pub fn build_command_line(exe: &str, args: &[String]) -> String {
    let mut line = String::new();
    append_quoted(exe, &mut line);
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
    fn quotes_plain_paths() {
        let line = build_command_line(r"C:\app\Clowd.Ui.exe", &args(&[r"C:\files\a.txt"]));
        assert_eq!(line, r#""C:\app\Clowd.Ui.exe" "C:\files\a.txt""#);
    }

    #[test]
    fn quotes_paths_with_spaces() {
        let line = build_command_line(r"C:\my apps\Clowd.Ui.exe", &args(&[r"C:\my files\a b.txt", r"C:\other\c.png"]));
        assert_eq!(line, r#""C:\my apps\Clowd.Ui.exe" "C:\my files\a b.txt" "C:\other\c.png""#);
    }

    #[test]
    fn doubles_trailing_backslashes() {
        let line = build_command_line("exe", &args(&[r"C:\dir\"]));
        assert_eq!(line, r#""exe" "C:\dir\\""#);
        let line = build_command_line("exe", &args(&[r"C:\dir\\"]));
        assert_eq!(line, r#""exe" "C:\dir\\\\""#);
    }

    #[test]
    fn escapes_embedded_quotes() {
        let line = build_command_line("exe", &args(&[r#"he"llo"#]));
        assert_eq!(line, r#""exe" "he\"llo""#);
    }

    #[test]
    fn escapes_backslashes_before_quotes() {
        // one backslash + quote -> 2n+1 = 3 backslashes + quote
        let line = build_command_line("exe", &args(&[r#"a\"b"#]));
        assert_eq!(line, r#""exe" "a\\\"b""#);
        // two backslashes + quote -> 5 backslashes + quote
        let line = build_command_line("exe", &args(&[r#"a\\"b"#]));
        assert_eq!(line, r#""exe" "a\\\\\"b""#);
    }

    #[test]
    fn quotes_empty_argument() {
        let line = build_command_line("exe", &args(&[""]));
        assert_eq!(line, r#""exe" """#);
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
