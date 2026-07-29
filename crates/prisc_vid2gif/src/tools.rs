//! Locates and spawns the bundled ffmpeg/ffprobe binaries.

use std::env;
use std::ffi::OsStr;
use std::io::{BufRead, BufReader, Read};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use anyhow::{bail, Context, Result};

use crate::cancel::{CancelToken, Cancelled};
use crate::probe::{self, ProbeInfo};
use crate::progress::StageProgress;

/// Overrides the directory searched for ffmpeg/ffprobe (normally the
/// directory containing vid2gif itself). Used by the integration tests to run
/// against the obs-deps bundle without assembling a profile dir.
pub const TOOLS_DIR_ENV: &str = "VID2GIF_TOOLS_DIR";

pub struct Tools {
    ffmpeg: PathBuf,
    ffprobe: PathBuf,
}

impl Tools {
    pub fn locate() -> Result<Tools> {
        let mut dirs = Vec::new();
        if let Some(d) = env::var_os(TOOLS_DIR_ENV) {
            dirs.push(PathBuf::from(d));
        }
        if let Ok(exe) = env::current_exe() {
            if let Some(d) = exe.parent() {
                dirs.push(d.to_path_buf());
            }
        }
        for dir in &dirs {
            let ffmpeg = dir.join(format!("ffmpeg{}", env::consts::EXE_SUFFIX));
            let ffprobe = dir.join(format!("ffprobe{}", env::consts::EXE_SUFFIX));
            if ffmpeg.is_file() && ffprobe.is_file() {
                return Ok(Tools { ffmpeg, ffprobe });
            }
        }
        bail!(
            "ffmpeg/ffprobe not found next to vid2gif (searched {}); set {TOOLS_DIR_ENV} to override",
            dirs.iter()
                .map(|d| d.display().to_string())
                .collect::<Vec<_>>()
                .join(", ")
        );
    }

    pub fn probe(&self, input: &Path) -> Result<ProbeInfo> {
        let out = command(&self.ffprobe)
            .args([
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=width,height:format=duration",
                "-of",
                "json",
            ])
            .arg(input)
            .stdin(Stdio::null())
            .output()
            .context("failed to run ffprobe")?;
        if !out.status.success() {
            bail!(
                "ffprobe failed on {}: {}",
                input.display(),
                String::from_utf8_lossy(&out.stderr).trim()
            );
        }
        probe::parse(&String::from_utf8_lossy(&out.stdout))
    }

    /// Runs one ffmpeg stage. The fixed preamble routes machine-readable
    /// progress to stdout and errors to stderr; `args` supplies inputs,
    /// filters and outputs. Progress blocks are folded through `stage` and
    /// reported via `emit`. The child is registered with `cancel` so a
    /// stdin `quit` kills it mid-stage; cancellation surfaces as a
    /// [`Cancelled`] error.
    pub fn run_ffmpeg<I, S>(
        &self,
        args: I,
        stage: &mut StageProgress,
        emit: &mut dyn FnMut(u32),
        cancel: &CancelToken,
    ) -> Result<()>
    where
        I: IntoIterator<Item = S>,
        S: AsRef<OsStr>,
    {
        if cancel.is_cancelled() {
            return Err(anyhow::Error::new(Cancelled));
        }

        let mut child = command(&self.ffmpeg)
            .args([
                "-hide_banner",
                "-v",
                "error",
                "-nostdin",
                "-stats_period",
                "0.2",
                "-progress",
                "pipe:1",
            ])
            .args(args)
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .context("failed to run ffmpeg")?;

        // Drain stderr on a thread so a chatty ffmpeg can never fill the pipe
        // and deadlock against our stdout read loop.
        let mut stderr = child.stderr.take().expect("stderr piped");
        let stderr_thread = std::thread::spawn(move || {
            let mut buf = String::new();
            let _ = stderr.read_to_string(&mut buf);
            buf
        });

        let stdout = child.stdout.take().expect("stdout piped");
        // Hand the child to the token so cancel() can kill it; killing closes
        // stdout and unblocks this loop.
        cancel.register(child);

        for line in BufReader::new(stdout).lines() {
            let Ok(line) = line else { break };
            if let Some(percent) = stage.feed_line(&line) {
                emit(percent);
            }
        }

        // Only this function removes the child, so it is always still there.
        let mut child = cancel.take().expect("child registered above");
        let status = child.wait().context("failed to wait for ffmpeg")?;
        let errtext = stderr_thread.join().unwrap_or_default();

        if cancel.is_cancelled() {
            return Err(anyhow::Error::new(Cancelled));
        }
        if !status.success() {
            let errtext = errtext.trim();
            if errtext.is_empty() {
                bail!("ffmpeg exited with {status}");
            }
            bail!("ffmpeg exited with {status}: {errtext}");
        }
        Ok(())
    }
}

/// Builds a `Command` that never pops up a console window when vid2gif is
/// driven from a GUI parent process.
fn command(path: &Path) -> Command {
    let cmd = Command::new(path);
    #[cfg(windows)]
    let cmd = {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        let mut cmd = cmd;
        cmd.creation_flags(CREATE_NO_WINDOW);
        cmd
    };
    cmd
}
