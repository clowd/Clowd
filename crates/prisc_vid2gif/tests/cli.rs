//! End-to-end CLI tests. vid2gif links the bundled FFmpeg libraries directly,
//! so these tests are fully self-contained: container inputs come from small
//! committed fixtures (tests/fixtures/), raw inputs are generated on the fly
//! as Y4M, and outputs are validated by parsing the GIF byte stream — no
//! external tools involved.

use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

// ---------------------------------------------------------------------------
// Harness
// ---------------------------------------------------------------------------

fn fixture(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join(name)
}

/// Fresh per-test temp dir (removed up-front so reruns start clean).
fn test_dir(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("vid2gif-test-{name}"));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

/// Writes a raw Y4M clip (moving gradient bands) — decodable by FFmpeg on
/// every platform with no codec involved.
fn write_y4m(path: &Path, w: usize, h: usize, fps: u32, seconds: u32) {
    use std::io::BufWriter;
    let mut f = BufWriter::new(std::fs::File::create(path).unwrap());
    write!(f, "YUV4MPEG2 W{w} H{h} F{fps}:1 Ip A1:1 C420mpeg2\n").unwrap();
    let (cw, ch) = (w / 2, h / 2);
    let mut y_plane = vec![0u8; w * h];
    let mut u_plane = vec![0u8; cw * ch];
    let mut v_plane = vec![0u8; cw * ch];
    for t in 0..(fps as usize * seconds as usize) {
        for yy in 0..h {
            for xx in 0..w {
                y_plane[yy * w + xx] = ((xx / 2 + yy / 2 + t * 7) & 0xFF) as u8;
            }
        }
        for yy in 0..ch {
            for xx in 0..cw {
                u_plane[yy * cw + xx] = ((xx * 2 + t * 5) & 0xFF) as u8;
                v_plane[yy * cw + xx] = ((yy * 2 + t * 3) & 0xFF) as u8;
            }
        }
        f.write_all(b"FRAME\n").unwrap();
        f.write_all(&y_plane).unwrap();
        f.write_all(&u_plane).unwrap();
        f.write_all(&v_plane).unwrap();
    }
}

/// Runs vid2gif and returns (success, parsed stdout protocol messages).
fn run_vid2gif(args: &[&std::ffi::OsStr]) -> (bool, Vec<Msg>) {
    let out = Command::new(env!("CARGO_BIN_EXE_vid2gif"))
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
/// `done`, and no `error`/`cancelled` lines anywhere.
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

// ---------------------------------------------------------------------------
// GIF validation (byte-level, no external tools)
// ---------------------------------------------------------------------------

/// GIF header magic + logical screen dimensions.
fn gif_dims(data: &[u8]) -> (u16, u16) {
    assert!(data.len() > 13, "gif too small: {} bytes", data.len());
    assert_eq!(&data[..6], b"GIF89a", "bad gif magic");
    (
        u16::from_le_bytes([data[6], data[7]]),
        u16::from_le_bytes([data[8], data[9]]),
    )
}

/// Walks the GIF block structure and counts image frames. Panics on any
/// malformed block, so it doubles as a structural integrity check down to the
/// trailer byte.
fn gif_frame_count(data: &[u8]) -> usize {
    gif_dims(data);
    let mut pos = 6;
    let flags = data[pos + 4];
    pos += 7; // logical screen descriptor
    if flags & 0x80 != 0 {
        pos += 3 << ((flags & 0x07) as usize + 1); // global color table
    }
    let mut frames = 0;
    loop {
        match data[pos] {
            0x21 => {
                // extension: introducer + label, then length-prefixed sub-blocks
                pos += 2;
                while data[pos] != 0 {
                    pos += data[pos] as usize + 1;
                }
                pos += 1;
            }
            0x2C => {
                // image descriptor
                let f = data[pos + 9];
                pos += 10;
                if f & 0x80 != 0 {
                    pos += 3 << ((f & 0x07) as usize + 1); // local color table
                }
                pos += 1; // LZW minimum code size
                while data[pos] != 0 {
                    pos += data[pos] as usize + 1;
                }
                pos += 1;
                frames += 1;
            }
            0x3B => break, // trailer
            other => panic!("unexpected GIF block 0x{other:02X} at offset {pos}"),
        }
    }
    frames
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[test]
fn converts_mp4_with_valid_protocol_and_output() {
    let dir = test_dir("mp4");
    let clip = dir.join("clip.mp4");
    std::fs::copy(fixture("clip.mp4"), &clip).unwrap();

    let (ok, msgs) = run_vid2gif(&[clip.as_os_str(), "--quality".as_ref(), "fair".as_ref()]);
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
    let frames = gif_frame_count(&data);
    assert!(
        (15..=25).contains(&frames),
        "unexpected frame count {frames}"
    );
}

#[test]
fn mkv_input_explicit_output_with_spaces_best_quality() {
    let dir = test_dir("mkv");

    // Exercise the done-line parsing with a space in the output path.
    let out = dir.join("my clips").join("out put.gif");
    let input = fixture("clip.mkv");
    let (ok, msgs) = run_vid2gif(&[
        input.as_os_str(),
        out.as_os_str(),
        "--quality".as_ref(),
        "best".as_ref(),
    ]);
    assert!(ok, "vid2gif failed: {msgs:?}");
    let (path, bytes) = expect_success(&msgs);
    assert_eq!(path, out);
    assert_eq!(bytes, std::fs::metadata(&out).unwrap().len());

    // 2 s at the `best` preset's 20 fps ≈ 40 frames.
    let data = std::fs::read(&out).unwrap();
    let frames = gif_frame_count(&data);
    assert!(
        (30..=50).contains(&frames),
        "unexpected frame count {frames}"
    );
}

#[test]
fn max_width_and_height_clamp_most_restrictive() {
    let dir = test_dir("clamp");
    let clip = dir.join("clip.y4m");
    write_y4m(&clip, 320, 240, 15, 1);

    let convert = |extra: &[&str], out: &str| -> (u16, u16) {
        let out_path = dir.join(out);
        let mut args: Vec<&std::ffi::OsStr> = vec![clip.as_os_str(), out_path.as_os_str()];
        args.extend(extra.iter().map(|s| -> &std::ffi::OsStr { s.as_ref() }));
        let (ok, msgs) = run_vid2gif(&args);
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
fn fps_override_changes_frame_count() {
    let dir = test_dir("fps");
    let out = dir.join("out.gif");
    let input = fixture("clip.mp4");
    let (ok, msgs) = run_vid2gif(&[
        input.as_os_str(),
        out.as_os_str(),
        "--fps".as_ref(),
        "5".as_ref(),
    ]);
    assert!(ok, "vid2gif failed: {msgs:?}");
    expect_success(&msgs);
    let frames = gif_frame_count(&std::fs::read(&out).unwrap());
    assert!(
        (8..=12).contains(&frames),
        "unexpected frame count {frames}"
    );
}

#[test]
fn missing_input_prints_error_line_and_fails() {
    let (ok, msgs) = run_vid2gif(&["C:/definitely/not/here.mp4".as_ref()]);
    assert!(!ok, "must exit non-zero");
    match msgs.as_slice() {
        [Msg::Error(e)] => assert!(e.contains("not found"), "unexpected message: {e}"),
        other => panic!("expected a single error message, got {other:?}"),
    }
}

#[test]
fn corrupt_input_prints_error_line_and_fails() {
    let dir = test_dir("corrupt");
    let clip = dir.join("clip.mp4");
    std::fs::write(&clip, b"this is not a video file").unwrap();

    let (ok, msgs) = run_vid2gif(&[clip.as_os_str()]);
    assert!(!ok, "must exit non-zero");
    match msgs.as_slice() {
        [Msg::Error(e)] => assert!(e.contains("could not read"), "unexpected message: {e}"),
        other => panic!("expected a single error message, got {other:?}"),
    }
}

#[test]
fn gif_input_without_explicit_output_is_rejected() {
    let dir = test_dir("gifin");
    let input = dir.join("clip.gif");
    std::fs::write(&input, b"GIF89a").unwrap();

    let (ok, msgs) = run_vid2gif(&[input.as_os_str()]);
    assert!(!ok, "must exit non-zero");
    match msgs.as_slice() {
        [Msg::Error(e)] => assert!(e.contains("equals input"), "unexpected message: {e}"),
        other => panic!("expected a single error message, got {other:?}"),
    }
}

#[test]
fn quit_mid_conversion_cancels_promptly_and_cleans_up() {
    use std::io::{BufRead, BufReader};
    use std::time::Instant;

    let dir = test_dir("cancel");
    let clip = dir.join("clip.y4m");
    // Long enough that the conversion cannot finish before `quit` lands.
    write_y4m(&clip, 320, 240, 15, 90);

    let mut child = Command::new(env!("CARGO_BIN_EXE_vid2gif"))
        .arg(&clip)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .expect("spawn vid2gif");
    let mut stdin = child.stdin.take().unwrap();
    let stdout = BufReader::new(child.stdout.take().unwrap());

    // Wait until the conversion is demonstrably in flight (progress strictly
    // between 0 and 100), then cancel and require a prompt exit.
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
    let latency = sent_at
        .expect("never saw mid-conversion progress")
        .elapsed();

    let status = child.wait().expect("wait for vid2gif");
    assert!(status.success(), "cancellation must exit 0");
    let msgs = parse_protocol(&lines.join("\n"));
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
        latency.as_secs() < 5,
        "cancel must stop the conversion promptly, took {latency:?}"
    );
    assert!(
        !dir.join("clip.gif").exists(),
        "partial output must be removed"
    );
}
