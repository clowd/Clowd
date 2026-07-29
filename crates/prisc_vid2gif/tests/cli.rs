//! End-to-end CLI tests: run the real vid2gif binary against the real FFmpeg
//! from the obs-deps bundle (`obs-studio/.deps`), pointed at via
//! VID2GIF_TOOLS_DIR so no assembled profile dir is required. On a checkout
//! where the deps bundle has not been downloaded yet, each test skips with a
//! note instead of failing.

use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

// ---------------------------------------------------------------------------
// Harness
// ---------------------------------------------------------------------------

fn exe(name: &str) -> String {
    format!("{name}{}", std::env::consts::EXE_SUFFIX)
}

/// Locates `obs-studio/.deps/obs-deps-*/bin` by walking up from this crate.
fn deps_bin() -> Option<PathBuf> {
    let mut dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    loop {
        let deps = dir.join("obs-studio").join(".deps");
        if deps.is_dir() {
            for entry in std::fs::read_dir(&deps).ok()?.flatten() {
                let name = entry.file_name().to_string_lossy().into_owned();
                if name.starts_with("obs-deps-") && !name.contains("qt6") {
                    let bin = entry.path().join("bin");
                    if bin.join(exe("ffmpeg")).is_file() && bin.join(exe("ffprobe")).is_file() {
                        return Some(bin);
                    }
                }
            }
        }
        if !dir.pop() {
            return None;
        }
    }
}

macro_rules! require_tools {
    () => {
        match deps_bin() {
            Some(b) => b,
            None => {
                eprintln!("skipping: obs-deps FFmpeg bundle not found");
                return;
            }
        }
    };
}

/// Fresh per-test temp dir (removed up-front so reruns start clean).
fn test_dir(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("vid2gif-test-{name}"));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

/// Generates a short H.264 clip with the bundled ffmpeg. The container is
/// inferred from `out`'s extension (.mp4 / .mkv).
fn gen_clip(tools: &Path, out: &Path, size: &str, seconds: u32) {
    let status = Command::new(tools.join(exe("ffmpeg")))
        .args([
            "-hide_banner",
            "-v",
            "error",
            "-nostdin",
            "-f",
            "lavfi",
            "-i",
        ])
        .arg(format!("testsrc2=duration={seconds}:size={size}:rate=30"))
        .args([
            "-c:v",
            "libx264",
            "-preset",
            "ultrafast",
            "-pix_fmt",
            "yuv420p",
            "-y",
        ])
        .arg(out)
        .status()
        .expect("spawn ffmpeg");
    assert!(status.success(), "failed to generate test clip {out:?}");
}

/// Runs vid2gif and returns (success, parsed stdout protocol messages).
fn run_vid2gif(tools: &Path, args: &[&std::ffi::OsStr]) -> (bool, Vec<Msg>) {
    let out = Command::new(env!("CARGO_BIN_EXE_vid2gif"))
        .env("VID2GIF_TOOLS_DIR", tools)
        .args(args)
        .output()
        .expect("spawn vid2gif");
    let stdout = String::from_utf8_lossy(&out.stdout).into_owned();
    (out.status.success(), parse_protocol(&stdout))
}

#[derive(Debug)]
enum Msg {
    Progress(u32),
    Done(PathBuf, u64),
    Error(String),
    Cancelled,
}

/// Parses the stdout protocol strictly: any unrecognized non-empty line is a
/// protocol violation and fails the test.
fn parse_protocol(stdout: &str) -> Vec<Msg> {
    let mut msgs = Vec::new();
    for line in stdout.lines().map(str::trim).filter(|l| !l.is_empty()) {
        if let Some(v) = line.strip_prefix("progress ") {
            msgs.push(Msg::Progress(
                v.parse()
                    .unwrap_or_else(|_| panic!("bad progress line: {line:?}")),
            ));
        } else if let Some(v) = line.strip_prefix("done ") {
            // Format is `done <path> <bytes>`; the path may contain spaces.
            let (path, bytes) = v.rsplit_once(' ').expect("done line needs bytes");
            msgs.push(Msg::Done(
                PathBuf::from(path),
                bytes
                    .parse()
                    .unwrap_or_else(|_| panic!("bad done line: {line:?}")),
            ));
        } else if let Some(v) = line.strip_prefix("error ") {
            msgs.push(Msg::Error(v.to_string()));
        } else if line == "cancelled" {
            msgs.push(Msg::Cancelled);
        } else {
            panic!("protocol violation on stdout: {line:?}");
        }
    }
    msgs
}

/// Asserts the happy-path protocol shape and returns the `done` payload:
/// progress lines strictly increasing from 0 to exactly 100, then one final
/// `done`, and no `error` lines anywhere.
fn expect_success(msgs: &[Msg]) -> (PathBuf, u64) {
    let mut progress = Vec::new();
    for (i, m) in msgs.iter().enumerate() {
        match m {
            Msg::Progress(p) => {
                assert!(i < msgs.len() - 1, "progress after done: {msgs:?}");
                progress.push(*p);
            }
            Msg::Done(..) => assert_eq!(i, msgs.len() - 1, "done must be last: {msgs:?}"),
            Msg::Error(e) => panic!("unexpected error message: {e}"),
            Msg::Cancelled => panic!("unexpected cancelled message: {msgs:?}"),
        }
    }
    assert_eq!(
        progress.first(),
        Some(&0),
        "must start at progress 0: {msgs:?}"
    );
    assert_eq!(
        progress.last(),
        Some(&100),
        "must end at progress 100: {msgs:?}"
    );
    assert!(
        progress.windows(2).all(|w| w[0] < w[1]),
        "progress must be strictly increasing: {progress:?}"
    );
    assert!(progress.iter().all(|p| *p <= 100));
    match msgs.last() {
        Some(Msg::Done(path, bytes)) => (path.clone(), *bytes),
        other => panic!("expected done as final message, got {other:?}"),
    }
}

/// Minimal GIF header validation: magic + logical screen dimensions.
fn gif_dims(data: &[u8]) -> (u16, u16) {
    assert!(data.len() > 10, "gif too small: {} bytes", data.len());
    assert_eq!(&data[..6], b"GIF89a", "bad gif magic");
    (
        u16::from_le_bytes([data[6], data[7]]),
        u16::from_le_bytes([data[8], data[9]]),
    )
}

/// Decodes the GIF with the bundled ffprobe and counts its frames.
fn gif_frames(tools: &Path, gif: &Path) -> u64 {
    let out = Command::new(tools.join(exe("ffprobe")))
        .args([
            "-v",
            "error",
            "-count_frames",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=nb_read_frames",
            "-of",
            "json",
        ])
        .arg(gif)
        .output()
        .expect("spawn ffprobe");
    assert!(out.status.success(), "ffprobe failed on {gif:?}");
    let v: serde_json::Value = serde_json::from_slice(&out.stdout).unwrap();
    v["streams"][0]["nb_read_frames"]
        .as_str()
        .expect("nb_read_frames")
        .parse()
        .unwrap()
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[test]
fn converts_mp4_with_valid_protocol_and_output() {
    let tools = require_tools!();
    let dir = test_dir("mp4");
    let clip = dir.join("clip.mp4");
    gen_clip(&tools, &clip, "160x120", 2);

    let (ok, msgs) = run_vid2gif(
        &tools,
        &[clip.as_os_str(), "--quality".as_ref(), "fair".as_ref()],
    );
    assert!(ok, "vid2gif failed: {msgs:?}");
    let (path, bytes) = expect_success(&msgs);

    // Default output path: input with .gif extension.
    assert_eq!(path, dir.join("clip.gif"));
    let data = std::fs::read(&path).expect("read output gif");
    assert_eq!(
        bytes,
        data.len() as u64,
        "done line must report the real size"
    );
    assert_eq!(gif_dims(&data), (160, 120), "no scaling requested");

    // 2 s at the `fair` preset's 10 fps ≈ 20 frames.
    let frames = gif_frames(&tools, &path);
    assert!(
        (15..=25).contains(&frames),
        "unexpected frame count {frames}"
    );
}

#[test]
fn max_width_and_height_clamp_most_restrictive() {
    let tools = require_tools!();
    let dir = test_dir("clamp");
    let clip = dir.join("clip.mp4");
    gen_clip(&tools, &clip, "320x240", 1);

    let convert = |extra: &[&str], out: &str| -> (u16, u16) {
        let out_path = dir.join(out);
        let mut args: Vec<&std::ffi::OsStr> = vec![clip.as_os_str(), out_path.as_os_str()];
        args.extend(extra.iter().map(|s| -> &std::ffi::OsStr { s.as_ref() }));
        let (ok, msgs) = run_vid2gif(&tools, &args);
        assert!(ok, "vid2gif failed: {msgs:?}");
        let (path, _) = expect_success(&msgs);
        gif_dims(&std::fs::read(path).unwrap())
    };

    // Width clamp alone.
    assert_eq!(convert(&["--max-width", "120"], "w.gif"), (120, 90));
    // Height clamp alone (factor 0.25 -> 80x60).
    assert_eq!(convert(&["--max-height", "60"], "h.gif"), (80, 60));
    // Both: the height clamp is more restrictive here.
    assert_eq!(
        convert(&["--max-width", "120", "--max-height", "60"], "wh.gif"),
        (80, 60)
    );
    // Clamps larger than the source never upscale.
    assert_eq!(
        convert(&["--max-width", "5000", "--max-height", "5000"], "big.gif"),
        (320, 240)
    );
}

#[test]
fn mkv_input_explicit_output_with_spaces_best_quality() {
    let tools = require_tools!();
    let dir = test_dir("mkv");
    let clip = dir.join("clip.mkv");
    gen_clip(&tools, &clip, "160x120", 2);

    // Exercise the done-line parsing with a space in the output path.
    let out = dir.join("my clips").join("out put.gif");
    let (ok, msgs) = run_vid2gif(
        &tools,
        &[
            clip.as_os_str(),
            out.as_os_str(),
            "--quality".as_ref(),
            "best".as_ref(),
        ],
    );
    assert!(ok, "vid2gif failed: {msgs:?}");
    let (path, bytes) = expect_success(&msgs);
    assert_eq!(path, out);
    assert_eq!(bytes, std::fs::metadata(&out).unwrap().len());
    gif_dims(&std::fs::read(&out).unwrap());

    // 2 s at the `best` preset's 20 fps ≈ 40 frames.
    let frames = gif_frames(&tools, &out);
    assert!(
        (30..=50).contains(&frames),
        "unexpected frame count {frames}"
    );
}

#[test]
fn fps_override_changes_frame_count() {
    let tools = require_tools!();
    let dir = test_dir("fps");
    let clip = dir.join("clip.mp4");
    gen_clip(&tools, &clip, "160x120", 2);

    let (ok, msgs) = run_vid2gif(&tools, &[clip.as_os_str(), "--fps".as_ref(), "5".as_ref()]);
    assert!(ok, "vid2gif failed: {msgs:?}");
    let (path, _) = expect_success(&msgs);
    let frames = gif_frames(&tools, &path);
    assert!(
        (8..=12).contains(&frames),
        "unexpected frame count {frames}"
    );
}

#[test]
fn missing_input_prints_error_line_and_fails() {
    let tools = require_tools!();
    let (ok, msgs) = run_vid2gif(&tools, &["C:/definitely/not/here.mp4".as_ref()]);
    assert!(!ok, "must exit non-zero");
    match msgs.as_slice() {
        [Msg::Error(e)] => assert!(e.contains("not found"), "unexpected message: {e}"),
        other => panic!("expected a single error message, got {other:?}"),
    }
}

#[test]
fn corrupt_input_prints_error_line_and_fails() {
    let tools = require_tools!();
    let dir = test_dir("corrupt");
    let clip = dir.join("clip.mp4");
    std::fs::write(&clip, b"this is not a video file").unwrap();

    let (ok, msgs) = run_vid2gif(&tools, &[clip.as_os_str()]);
    assert!(!ok, "must exit non-zero");
    match msgs.as_slice() {
        [Msg::Error(e)] => assert!(e.contains("could not read"), "unexpected message: {e}"),
        other => panic!("expected a single error message, got {other:?}"),
    }
}

#[test]
fn quit_on_stdin_cancels_and_cleans_up() {
    let tools = require_tools!();
    let dir = test_dir("cancel");
    let clip = dir.join("clip.mp4");
    // Long enough that the conversion cannot finish before `quit` arrives
    // (the pipeline alone spawns three ffmpeg processes).
    gen_clip(&tools, &clip, "1280x720", 60);

    let mut child = Command::new(env!("CARGO_BIN_EXE_vid2gif"))
        .env("VID2GIF_TOOLS_DIR", &tools)
        .arg(&clip)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .expect("spawn vid2gif");
    child
        .stdin
        .take()
        .unwrap()
        .write_all(b"quit\n")
        .expect("write quit");

    let out = child.wait_with_output().expect("wait for vid2gif");
    assert!(out.status.success(), "cancellation must exit 0");

    let msgs = parse_protocol(&String::from_utf8_lossy(&out.stdout));
    assert!(
        matches!(msgs.last(), Some(Msg::Cancelled)),
        "cancelled must be the final message: {msgs:?}"
    );
    assert!(
        !msgs
            .iter()
            .any(|m| matches!(m, Msg::Done(..) | Msg::Error(_))),
        "no done/error after cancellation: {msgs:?}"
    );
    assert!(
        !dir.join("clip.gif").exists(),
        "partial output must be removed"
    );
}

#[test]
fn quit_mid_stage_kills_active_ffmpeg() {
    use std::io::{BufRead, BufReader};
    use std::time::Instant;

    let tools = require_tools!();
    let dir = test_dir("cancel-mid");
    let clip = dir.join("clip.mp4");
    gen_clip(&tools, &clip, "1280x720", 60);

    let mut child = Command::new(env!("CARGO_BIN_EXE_vid2gif"))
        .env("VID2GIF_TOOLS_DIR", &tools)
        .arg(&clip)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .expect("spawn vid2gif");
    let mut stdin = child.stdin.take().unwrap();
    let stdout = BufReader::new(child.stdout.take().unwrap());

    // Wait until a stage is demonstrably in flight (progress strictly between
    // 0 and 100), then cancel and require a prompt exit.
    let mut lines = Vec::new();
    let mut sent_at = None;
    for line in stdout.lines() {
        let line = line.expect("read stdout");
        if sent_at.is_none() {
            if let Some(p) = line.strip_prefix("progress ") {
                let p: u32 = p.parse().unwrap();
                if p > 0 && p < 100 {
                    stdin.write_all(b"quit\n").expect("write quit");
                    stdin.flush().unwrap();
                    sent_at = Some(Instant::now());
                }
            }
        }
        lines.push(line);
    }
    let latency = sent_at.expect("never saw mid-conversion progress").elapsed();

    let status = child.wait().expect("wait for vid2gif");
    assert!(status.success(), "cancellation must exit 0");
    let msgs = parse_protocol(&lines.join("\n"));
    assert!(
        matches!(msgs.last(), Some(Msg::Cancelled)),
        "cancelled must be the final message: {msgs:?}"
    );
    assert!(
        latency.as_secs() < 5,
        "cancel must kill the active stage promptly, took {latency:?}"
    );
    assert!(
        !dir.join("clip.gif").exists(),
        "partial output must be removed"
    );
}

#[test]
fn gif_input_without_explicit_output_is_rejected() {
    let tools = require_tools!();
    let dir = test_dir("gifin");
    let input = dir.join("clip.gif");
    std::fs::write(&input, b"GIF89a").unwrap();

    let (ok, msgs) = run_vid2gif(&tools, &[input.as_os_str()]);
    assert!(!ok, "must exit non-zero");
    match msgs.as_slice() {
        [Msg::Error(e)] => assert!(e.contains("equals input"), "unexpected message: {e}"),
        other => panic!("expected a single error message, got {other:?}"),
    }
}
