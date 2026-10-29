//! `cargo codegen` (write), `cargo codegen --check` (report, exit 1 when anything is off),
//! `cargo codegen --list` (every output path, for `git add`).

use std::path::Path;
use std::process::ExitCode;

use prisc_codegen::{check, lint, orphans, outputs, repo_root, write, State};

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let root = repo_root();
    match args.first().map(String::as_str) {
        Some("--list") if args.len() == 1 => {
            for output in outputs() {
                println!("{}", output.path);
            }
            ExitCode::SUCCESS
        }
        Some("--check") if args.len() == 1 => run(|| {
            let mut bad = report_lint(&root)?;
            for (output, state) in check(&root)? {
                if state != State::Current {
                    println!("{}: {}", output.path, if state == State::Stale { "stale" } else { "missing" });
                    bad = true;
                }
            }
            for path in orphans(&root)? {
                println!("{}: orphaned (no output generates it any more)", path.display());
                bad = true;
            }
            if bad {
                println!("run `cargo codegen`");
            }
            Ok(!bad)
        }),
        None => run(|| {
            if report_lint(&root)? {
                println!("fix the lint errors above; nothing was written");
                return Ok(false);
            }
            for output in write(&root)? {
                println!("wrote {}", output.path);
            }
            for path in orphans(&root)? {
                println!("{}: orphaned (no output generates it any more); delete it", path.display());
            }
            Ok(true)
        }),
        _ => {
            eprintln!("usage: cargo codegen [--check | --list]");
            ExitCode::from(2)
        }
    }
}

/// Prints every lint violation; true when there was one.
fn report_lint(root: &Path) -> anyhow::Result<bool> {
    let violations = lint::run(root)?;
    for violation in &violations {
        println!("{violation}");
    }
    Ok(!violations.is_empty())
}

fn run(body: impl FnOnce() -> anyhow::Result<bool>) -> ExitCode {
    match body() {
        Ok(true) => ExitCode::SUCCESS,
        Ok(false) => ExitCode::FAILURE,
        Err(e) => {
            eprintln!("prisc_codegen: {e:#}");
            ExitCode::FAILURE
        }
    }
}
