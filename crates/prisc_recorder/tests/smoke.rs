//! End-to-end smoke test (DESIGN §2.8). Ignored by default — requires a real
//! display and the assembled OBS runtime next to the binary. Run explicitly:
//!
//! ```text
//! cargo test -p obs-express --test smoke -- --ignored
//! ```

use std::io::{BufRead, BufReader, Write};
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;
use std::time::{Duration, Instant};

struct ProtocolReader {
    rx: mpsc::Receiver<serde_json::Value>,
}

impl ProtocolReader {
    fn new(child: &mut Child) -> ProtocolReader {
        let stdout = child.stdout.take().expect("stdout piped");
        let (tx, rx) = mpsc::channel();
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                let Ok(line) = line else { break };
                let trimmed = line.trim();
                // Protocol rule: consumers parse only {...} lines.
                if trimmed.starts_with('{') && trimmed.ends_with('}') {
                    if let Ok(v) = serde_json::from_str::<serde_json::Value>(trimmed) {
                        if tx.send(v).is_err() {
                            break;
                        }
                    }
                } else if !trimmed.is_empty() {
                    // Surface protocol violations to the test thread.
                    let _ = tx.send(serde_json::json!({
                        "type": "__protocol_violation",
                        "line": trimmed,
                    }));
                }
            }
        });
        ProtocolReader { rx }
    }

    /// Waits for a message with the given `type`, collecting everything else.
    fn wait_for(&self, msg_type: &str, timeout: Duration) -> serde_json::Value {
        let deadline = Instant::now() + timeout;
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            match self.rx.recv_timeout(remaining) {
                Ok(v) => {
                    assert_ne!(
                        v["type"], "__protocol_violation",
                        "non-JSON line on stdout (protocol violation): {}",
                        v["line"]
                    );
                    if v["type"] == msg_type {
                        return v;
                    }
                }
                Err(_) => panic!("timed out waiting for '{msg_type}' after {timeout:?}"),
            }
        }
    }

    /// Drains messages for `window`, returning those matching `msg_type`.
    fn collect_for(&self, msg_type: &str, window: Duration) -> Vec<serde_json::Value> {
        let deadline = Instant::now() + window;
        let mut out = Vec::new();
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return out;
            }
            if let Ok(v) = self.rx.recv_timeout(remaining) {
                if v["type"] == msg_type {
                    out.push(v);
                }
            }
        }
    }
}

#[test]
#[ignore]
fn record_three_seconds_and_validate_mp4() {
    let out_dir = std::env::temp_dir().join("obs-express-smoke");
    std::fs::create_dir_all(&out_dir).unwrap();
    let out_mp4 = out_dir.join("smoke.mp4");
    let _ = std::fs::remove_file(&out_mp4);

    let exe = env!("CARGO_BIN_EXE_obs-express");
    let mut child = Command::new(exe)
        .arg("--region")
        .arg("0,0,640,480")
        .arg("--fps")
        .arg("30")
        .arg("--speaker")
        .arg("default")
        .arg("--microphone")
        .arg("default")
        .arg("--pause")
        .arg("--output")
        .arg(&out_mp4)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .expect("spawn obs-express");

    let reader = ProtocolReader::new(&mut child);
    let mut stdin = child.stdin.take().expect("stdin piped");

    reader.wait_for("initialized", Duration::from_secs(30));

    // Levels must flow during the pre-start WAIT phase (100 ms cadence).
    let levels = reader.collect_for("levels", Duration::from_secs(2));
    assert!(
        !levels.is_empty(),
        "expected >= 1 levels message before start"
    );
    for l in &levels {
        for key in ["speaker", "mic"] {
            let arr = l[key]
                .as_array()
                .unwrap_or_else(|| panic!("{key} must be an array: {l}"));
            assert!(!arr.is_empty(), "{key} must be non-empty: {l}");
            assert!(
                arr.iter().all(|v| v.is_f64() || v.is_i64() || v.is_u64()),
                "{key} must be numeric: {l}"
            );
        }
    }

    stdin.write_all(b"start\n").unwrap();
    stdin.flush().unwrap();
    reader.wait_for("started_recording", Duration::from_secs(10));

    // Record ~3 s; expect at least 2 one-second status ticks.
    let statuses = reader.collect_for("status", Duration::from_millis(3500));
    assert!(
        statuses.len() >= 2,
        "expected >= 2 status lines, got {}",
        statuses.len()
    );
    for s in &statuses {
        assert!(
            s["fps"].is_f64() || s["fps"].is_u64(),
            "fps must be numeric: {s}"
        );
        assert!(s["timeMs"].is_u64(), "timeMs must be u64: {s}");
        assert!(s.get("dropped").is_some() && s.get("droppedPerc").is_some());
    }

    stdin.write_all(b"quit\n").unwrap();
    stdin.flush().unwrap();

    let stopped = reader.wait_for("stopped_recording", Duration::from_secs(35));
    assert_eq!(stopped["code"].as_i64(), Some(0), "stop code: {stopped}");
    assert_eq!(stopped["message"].as_str(), Some("Successfully stopped"));

    let status = child.wait().expect("wait for exit");
    assert_eq!(status.code(), Some(0), "exit code");

    validate_mp4(&std::fs::read(&out_mp4).expect("read mp4"));
}

/// `--multi-track`: screen + webcam video tracks and one audio track per
/// device must all land in the file as separate streams — the headline case
/// is 4 (screen, webcam, speaker, mic). Uses the `test` webcam device (a solid
/// color source) so the test runs on machines without a camera.
#[test]
#[ignore]
fn multi_track_records_four_separate_streams() {
    let out_dir = std::env::temp_dir().join("obs-express-smoke");
    std::fs::create_dir_all(&out_dir).unwrap();
    let out_mp4 = out_dir.join("smoke-multi-track.mp4");
    let _ = std::fs::remove_file(&out_mp4);

    let exe = env!("CARGO_BIN_EXE_obs-express");
    let mut child = Command::new(exe)
        .args(["--region", "0,0,640,480"])
        .args(["--fps", "30"])
        .arg("--multi-track")
        .args(["--webcam", "test"])
        .args(["--speaker", "default"])
        .args(["--microphone", "default"])
        .arg("--pause")
        .arg("--output")
        .arg(&out_mp4)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .expect("spawn obs-express");

    let reader = ProtocolReader::new(&mut child);
    let mut stdin = child.stdin.take().expect("stdin piped");

    reader.wait_for("initialized", Duration::from_secs(30));
    stdin.write_all(b"start\n").unwrap();
    stdin.flush().unwrap();

    // The tracks payload must describe every stream before a byte is written.
    let started = reader.wait_for("started_recording", Duration::from_secs(10));
    let tracks = &started["tracks"];
    assert_eq!(tracks["screen"]["index"].as_u64(), Some(0), "{started}");
    assert_eq!(tracks["webcam"]["index"].as_u64(), Some(1), "{started}");
    let audio = tracks["audio"].as_array().expect("audio track array");
    assert_eq!(audio.len(), 2, "expected 2 audio tracks: {started}");
    assert_eq!(audio[0]["kind"].as_str(), Some("speaker"), "{started}");
    assert_eq!(audio[1]["kind"].as_str(), Some("microphone"), "{started}");

    let statuses = reader.collect_for("status", Duration::from_millis(3500));
    assert!(statuses.len() >= 2, "got {} status lines", statuses.len());

    stdin.write_all(b"quit\n").unwrap();
    stdin.flush().unwrap();
    let stopped = reader.wait_for("stopped_recording", Duration::from_secs(35));
    assert_eq!(stopped["code"].as_i64(), Some(0), "stop code: {stopped}");
    assert_eq!(child.wait().expect("wait for exit").code(), Some(0));

    let data = std::fs::read(&out_mp4).expect("read mp4");
    validate_mp4(&data);
    let (video, audio) = count_tracks(&data);
    assert_eq!(video, 2, "expected 2 video tracks (screen + webcam)");
    assert_eq!(audio, 2, "expected 2 audio tracks (speaker + microphone)");
}

/// Single-track (the default): everything is mixed down to one video and one
/// audio stream, and `--webcam` is refused outright.
#[test]
#[ignore]
fn single_track_records_one_stream_per_media_type() {
    let out_dir = std::env::temp_dir().join("obs-express-smoke");
    std::fs::create_dir_all(&out_dir).unwrap();
    let out_mp4 = out_dir.join("smoke-single-track.mp4");
    let _ = std::fs::remove_file(&out_mp4);

    let exe = env!("CARGO_BIN_EXE_obs-express");
    let refused = Command::new(exe)
        .args(["--region", "0,0,640,480"])
        .args(["--webcam", "test"])
        .arg("--output")
        .arg(&out_mp4)
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .expect("spawn obs-express");
    assert_eq!(refused.code(), Some(2), "--webcam without --multi-track");

    let mut child = Command::new(exe)
        .args(["--region", "0,0,640,480"])
        .args(["--fps", "30"])
        .args(["--speaker", "default"])
        .args(["--microphone", "default"])
        .arg("--output")
        .arg(&out_mp4)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .expect("spawn obs-express");

    let reader = ProtocolReader::new(&mut child);
    let mut stdin = child.stdin.take().expect("stdin piped");

    let started = reader.wait_for("started_recording", Duration::from_secs(30));
    assert!(started["tracks"]["webcam"].is_null(), "{started}");
    let audio = started["tracks"]["audio"]
        .as_array()
        .expect("audio track array");
    assert_eq!(audio.len(), 1, "{started}");
    assert_eq!(audio[0]["kind"].as_str(), Some("mixed"), "{started}");

    let statuses = reader.collect_for("status", Duration::from_millis(3500));
    assert!(statuses.len() >= 2, "got {} status lines", statuses.len());

    stdin.write_all(b"quit\n").unwrap();
    stdin.flush().unwrap();
    let stopped = reader.wait_for("stopped_recording", Duration::from_secs(35));
    assert_eq!(stopped["code"].as_i64(), Some(0), "stop code: {stopped}");
    assert_eq!(child.wait().expect("wait for exit").code(), Some(0));

    let data = std::fs::read(&out_mp4).expect("read mp4");
    validate_mp4(&data);
    assert_eq!(count_tracks(&data), (1, 1));
}

/// Counts (video, audio) tracks: every `moov/trak/mdia/hdlr` whose handler
/// type is `vide` / `soun`.
fn count_tracks(data: &[u8]) -> (usize, usize) {
    let boxes = parse_boxes(data);
    let moov = boxes
        .iter()
        .find(|(t, _)| t == b"moov")
        .expect("no moov box");
    let mut video = 0;
    let mut audio = 0;
    for (typ, trak) in parse_boxes(moov.1) {
        if &typ != b"trak" {
            continue;
        }
        let Some((_, mdia)) = parse_boxes(trak).into_iter().find(|(t, _)| t == b"mdia") else {
            continue;
        };
        let Some((_, hdlr)) = parse_boxes(mdia).into_iter().find(|(t, _)| t == b"hdlr") else {
            continue;
        };
        // hdlr payload: 4 version/flags + 4 pre_defined + 4 handler_type.
        match hdlr.get(8..12) {
            Some(b"vide") => video += 1,
            Some(b"soun") => audio += 1,
            _ => {}
        }
    }
    (video, audio)
}

/// Minimal top-level MP4 box validation: `ftyp` first, `moov` present, a size
/// floor, and `moov/mvhd` duration >= 2 s (timescale-normalized).
///
/// Size floor: 20 KB (design said 100 KB, but 3 s of a mostly-static desktop
/// at 640x480 CRF 24 legitimately encodes to ~50 KB; an all-black/broken
/// capture measured ~10 KB, so 20 KB still discriminates).
fn validate_mp4(data: &[u8]) {
    assert!(
        data.len() > 20 * 1024,
        "mp4 too small: {} bytes",
        data.len()
    );

    let boxes = parse_boxes(data);
    assert!(!boxes.is_empty(), "no mp4 boxes found");
    assert_eq!(
        &boxes[0].0,
        b"ftyp",
        "first box must be ftyp, got {:?}",
        fourcc(&boxes[0].0)
    );
    let moov = boxes.iter().find(|(t, _)| t == b"moov").unwrap_or_else(|| {
        panic!(
            "no moov box; boxes: {:?}",
            boxes.iter().map(|(t, _)| fourcc(t)).collect::<Vec<_>>()
        )
    });

    let mvhd = parse_boxes(moov.1)
        .into_iter()
        .find(|(t, _)| t == b"mvhd")
        .expect("no mvhd in moov");
    let duration_secs = mvhd_duration_secs(mvhd.1);
    assert!(
        duration_secs >= 2.0,
        "mvhd duration {duration_secs:.2}s < 2s"
    );
}

/// Iterates `[u32 size][4cc type]` boxes, returning (type, payload) pairs.
fn parse_boxes(data: &[u8]) -> Vec<([u8; 4], &[u8])> {
    let mut boxes = Vec::new();
    let mut off = 0usize;
    while off + 8 <= data.len() {
        let size32 = u32::from_be_bytes(data[off..off + 4].try_into().unwrap()) as u64;
        let mut typ = [0u8; 4];
        typ.copy_from_slice(&data[off + 4..off + 8]);
        let (size, header) = match size32 {
            0 => ((data.len() - off) as u64, 8usize), // to end of file
            1 => {
                if off + 16 > data.len() {
                    break;
                }
                (
                    u64::from_be_bytes(data[off + 8..off + 16].try_into().unwrap()),
                    16usize,
                )
            }
            n => (n, 8usize),
        };
        if size < header as u64 || off as u64 + size > data.len() as u64 {
            break;
        }
        boxes.push((typ, &data[off + header..off + size as usize]));
        off += size as usize;
    }
    boxes
}

fn fourcc(t: &[u8; 4]) -> String {
    String::from_utf8_lossy(t).into_owned()
}

/// mvhd payload → duration in seconds (handles version 0 and 1).
fn mvhd_duration_secs(payload: &[u8]) -> f64 {
    let version = payload[0];
    let (timescale, duration) = if version == 1 {
        // v1: 4 (ver/flags) + 8 ctime + 8 mtime = 20 → timescale u32, duration u64
        let ts = u32::from_be_bytes(payload[20..24].try_into().unwrap());
        let dur = u64::from_be_bytes(payload[24..32].try_into().unwrap());
        (ts, dur)
    } else {
        // v0: 4 (ver/flags) + 4 ctime + 4 mtime = 12 → timescale u32, duration u32
        let ts = u32::from_be_bytes(payload[12..16].try_into().unwrap());
        let dur = u32::from_be_bytes(payload[16..20].try_into().unwrap()) as u64;
        (ts, dur)
    };
    assert!(timescale > 0, "mvhd timescale is 0");
    duration as f64 / timescale as f64
}
