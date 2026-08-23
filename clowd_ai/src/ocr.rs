//! Text recognition — PaddleOCR (PP-OCRv6) on ONNX Runtime, the `ocr`
//! subcommand.
//!
//! By the time this runs the overlay (`clowd_capture_wgpu`) has already done
//! its part: the user captured a region, pressed OCR, and the overlay extracted
//! that region's pixels — compositing a click-locked peek if one is up, so what
//! is recognized is what the user can actually see. It spawns this process per
//! request, writes a [`RequestHeader`] line and the raw BGRA down our stdin,
//! and waits for us to exit. One request, one process, one answer, then gone:
//! canceling a request the user backed out of is the capturer killing us,
//! which is both instant and cannot leave a superseded job holding the engine
//! while the next request queues behind it. Nothing here polls for
//! cancellation.
//!
//! Fully self-contained and platform-independent: the models are embedded,
//! inference runs on the CPU, and recognition covers Chinese/Japanese/Latin
//! scripts without any OS engine or language pack.
//!
//! # Pipeline
//!
//! Detection (a DB text detector) runs on the whole image, then recognition (a
//! CTC recognizer) runs on width-sorted batches of line crops. The pipeline is
//! run manually — det → sort/cap → tier choice → batched rec — rather than as
//! one all-in-one call, for three reasons:
//!
//! * **The dense-window cliff.** Recognition cost scales with the total tensor
//!   width of the crops (tensor width = aspect × 48), so a text-dense terminal
//!   window can run to seconds and read as hung. Running det ourselves lets us
//!   PREDICT that cost from the box geometry and drop to the tiny recognition
//!   model when it blows the budget (see [`SMALL_TIER_BUDGET_MS`]).
//!   Recognition additionally runs in width-sorted BATCHES (see
//!   [`REC_BATCH`]), which amortizes the fixed per-call cost.
//! * **The line cap belongs BEFORE recognition.** A degenerate selection (a
//!   whole spreadsheet) must not pay for lines that are then thrown away.
//! * Reading-order sorting before the cap, so truncation keeps the top of
//!   the page rather than raw detection order.
//!
//! # Why the CPU
//!
//! The video/audio effects register DirectML/CoreML (`ep_session_builder`);
//! OCR deliberately does not. A hardware provider pays for session creation
//! and per-shape graph compilation up front, and this process lives for about
//! a second, runs each model a handful of times on shapes that differ with
//! every selection, and is spawned fresh on every key press — the warm-up
//! would cost more than it saved, and on DirectML the dynamic-width
//! recognition batches would recompile on nearly every call. CPU inference
//! measures well under a second on ordinary selections, which is the budget.
//!
//! # Output
//!
//! Nothing is written to stdout; the result goes to the `--out` file, which
//! doubles as the session's `ocr.json` artifact. There is no Sentry client
//! here (see `main.rs`): a panic leaves its message in the response file via
//! [`install_panic_reporter`] for the capturer to report on our behalf.

use std::io::{BufRead, BufReader, Read};
use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};
use std::time::Instant;

use anyhow::{bail, Context};
use image::RgbaImage;
use ort::session::Session;
use ort::value::Tensor;

use clowd_rust_core::geometry::{RectExt, ScreenRectF};
use clowd_rust_core::ocr::{OcrError, OcrLine, OcrOutcome, OcrRequest, OcrResponse, RequestHeader};

/// Line cap, applied BEFORE recognition (see module docs) purely to bound
/// worst-case latency — the render side (bubble rects, glyphon buffers)
/// grows dynamically and needs no cap. 512 because real pages get there:
/// a full-screen 3440x1440 page of dense book text measured 361 genuine
/// lines, and the previous cap of 256 silently dropped the bottom third.
const MAX_LINES: usize = 512;

/// Detection-stage resolution ceiling (the image is downscaled to this long
/// side before the DB detector runs; recognition always crops from the
/// full-resolution input). MEASURED CLIFF — do not lower casually: on a
/// 3440x1440 dense-text page, det at native res found 361 clean line boxes;
/// at 1920 the same page shattered into 522 fragments whose crops recognized
/// as garbage, and at 960 it found NOTHING. The DB detector simply cannot see
/// ~7px downscaled text, so the ceiling exists only to bound det latency on
/// inputs beyond any single monitor (det cost scales with area). Selections
/// spanning multiple 4K monitors will start to degrade past this — revisit
/// with tiled detection if that ever becomes a real complaint.
const DET_MAX_SIDE_LEN: u32 = 4096;

/// DB binarization threshold: probability-map pixels above this are text.
/// PaddleOCR's own default.
const DET_THRESH: f32 = 0.3;

/// Minimum mean probability over a candidate box for it to count as text.
/// PaddleOCR's default is 0.6; 0.5 keeps faint small UI text that the
/// detector is only moderately sure about, which on screen captures is
/// usually real text (the recognizer's own confidence gate,
/// [`MIN_CONFIDENCE`], catches what is not).
const BOX_THRESH: f32 = 0.5;

/// DB unclip ratio — how far a detected text core is inflated back out to
/// the full glyph extent (the detector is trained on shrunk polygons).
/// PaddleOCR's default.
const UNCLIP_RATIO: f32 = 1.5;

/// Candidate boxes whose shorter side is below this many detector pixels
/// are noise, not text. PaddleOCR's `min_size`.
const MIN_BOX_SIDE: f32 = 3.0;

/// Every detection box is inflated by this many pixels per side before the
/// recognition crop — the context measurably helps the recognizer.
const BOX_BORDER: u32 = 5;

/// Fraction of a box's height shaved off EACH side (vertically and
/// horizontally) after the border deflation, to approximate the glyph-ink
/// rect the bubble renderer sizes its text from. The DB detector's unclip
/// expansion (`unclip_ratio` 1.5) reports boxes ~1.4x taller than the ink:
/// MEASURED on 7-11pt renders — 14px-ink Segoe lines came back h=20,
/// 9px-ink Consolas lines h=13 — and the Windows.Media.Ocr word rects the
/// bubble font sizing was originally tuned against were ink-tight, so
/// without this the bubbles render ~40% oversized and overlap on densely
/// spaced small text. 0.14 per side keeps 72% of the height: 20 -> 14.4.
/// Horizontal uses the same ABSOLUTE amount because unclip pads uniformly.
const UNCLIP_TIGHTEN: f32 = 0.14;

/// Recognition results below this confidence are dropped. 0.5 (PaddleOCR's
/// default) drops too much real UI text; screen captures are clean enough
/// that low-confidence reads are usually right.
const MIN_CONFIDENCE: f32 = 0.35;

/// The recognition model's input height; crops are resized to this,
/// preserving aspect, so a crop's tensor width is aspect × 48. Fixed by
/// the PP-OCRv6 architecture — not tunable.
const REC_TARGET_HEIGHT: u32 = 48;

/// Hard ceiling on one crop's tensor width. Boxes this wide are not text
/// lines that can be read (a 48px-tall line 8192px wide is 170 aspect), and
/// the cap bounds the batch tensor a single absurd box would otherwise
/// force on its seven neighbors. Crops beyond it are squashed to fit.
const REC_MAX_WIDTH: u32 = 8192;

/// Measured per-crop cost model of the SMALL recognition model on the dev
/// box (28-core, release build, ONNX Runtime CPU), in the width-sorted
/// batches of [`REC_BATCH`] recognition actually runs in: a fixed ~0.5 ms
/// per crop plus ~10.6 ms per 1000 px of tensor width (8 × 320 px batch =
/// 30 ms, 8 × 1600 px = 138 ms; the 82-line 3440x1440 terminal reference
/// page, 2.04 kpx mean crop width, predicted 1814 ms and measured 1854 ms).
/// Used only to CHOOSE a tier, so being off by 2x on other hardware moves
/// the tier threshold, not correctness. Re-measure with the
/// `env_det_ceiling_probe` test after a model or runtime change.
const SMALL_REC_FIXED_MS: f32 = 0.5;
const SMALL_REC_MS_PER_KPX: f32 = 10.6;

/// Predicted small-model recognition time above which the tiny model takes
/// over: ~1.5 s of recognition is where a capture starts to feel hung under
/// the scanning sweep. The prediction is in real batched milliseconds (see
/// the constants above), so this is the small tier's wall-clock ceiling. On
/// the reference page tiny ran 4.2× faster (436 ms vs 1854 ms) at a mean
/// confidence of 0.96 against small's 0.98 — a few dropped/substituted
/// glyphs on 1000px-wide lines, which is the trade the cliff is worth.
const SMALL_TIER_BUDGET_MS: f32 = 1500.0;

/// Boxes with a width/height ratio beyond this are detector junk (a 1900px
/// wide, 4px tall sliver would alone cost a ~23000px-wide tensor) — skip
/// them rather than pay for garbage.
const MAX_BOX_ASPECT: f32 = 300.0;

/// Crops recognized per batched inference call. The batch tensor is padded
/// to the chunk's WIDEST sample, so crops are width-sorted before chunking —
/// a chunk of near-equal widths pays almost nothing for padding, while
/// reading-order chunks would routinely pair one full-width line with seven
/// short ones and pay 8× the widest. Results are indexed back to reading
/// order afterwards.
const REC_BATCH: usize = 8;

// PP-OCRv6, the official PaddleOCR ONNX inference exports (fp32), mirrored
// verbatim from GreatV/oar-ocr's v0.7.0 release — see assets/models/README.md.
// ~35.5 MB embedded — the deliberate cost of a zero-install, no-language-pack
// OCR. The _medium_ set (~130 MB, same charset coverage) is far beyond the
// size budget for a marginal accuracy gain on screen text.
//
// Two recognition tiers share the one detector: `small` is the default;
// `tiny` (+4.5 MB, ~5x faster) exists for text-dense selections where small
// would run to seconds (see module docs). NOTE the tiny charset is a subset
// (6904 glyphs vs small's 18708 — less CJK coverage, per upstream no
// Japanese): on a dense page a rare glyph may come back wrong that the small
// tier would have read. Speed over completeness there is deliberate — the
// alternative was "seemingly hung".
static DET_MODEL: &[u8] = include_bytes!("../assets/models/pp-ocrv6_small_det.onnx");
static REC_MODEL: &[u8] = include_bytes!("../assets/models/pp-ocrv6_small_rec.onnx");
static CHARSET: &str = include_str!("../assets/models/ppocrv6_dict.txt");
static TINY_REC_MODEL: &[u8] = include_bytes!("../assets/models/pp-ocrv6_tiny_rec.onnx");
static TINY_CHARSET: &str = include_str!("../assets/models/ppocrv6_tiny_dict.txt");

/// Where a panic should leave its explanation. Set once, from [`run`].
static OUT_PATH: OnceLock<PathBuf> = OnceLock::new();

/// The `ocr` subcommand: one request from stdin, one response file.
pub fn run(out: PathBuf) -> anyhow::Result<()> {
    // Before anything that could panic, and it needs the path, so it cannot
    // move above the argument parse.
    let _ = OUT_PATH.set(out.clone());
    install_panic_reporter();

    let request = read_request().context("reading the OCR request from stdin")?;
    log::info!("recognizing {}x{} at {:?}", request.width, request.height, request.origin);

    // A recognition that runs and fails is part of the answer, not a failure
    // of this process: it goes in the response file and we still exit 0, so
    // the capturer can tell "the engine is unavailable on this machine" apart
    // from "the child died", which is all a non-zero exit can mean.
    let response: OcrResponse = recognize(&request);
    if let Err(e) = &response {
        log::warn!("recognition failed: {e:?}");
    }

    let json = serde_json::to_vec(&response).context("serializing the OCR response")?;
    std::fs::write(&out, &json).with_context(|| format!("writing the OCR response to {}", out.display()))?;
    Ok(())
}

/// Leave a panicking run's message in the response file on the way down.
///
/// The process still dies with Rust's exit code 101, so the capturer still
/// treats it as an abnormal exit and does not use the file as a result — it
/// reads it only for this message, and reports that instead of a bare exit
/// code. Without it, the most likely failure in this subcommand (a Rust
/// panic, far more likely than a native crash inside ONNX Runtime) would
/// reach Sentry as "exited with 101" and nothing else.
pub fn install_panic_reporter() {
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        if let Some(path) = OUT_PATH.get() {
            // `info` Displays as "panicked at src/ocr.rs:279:14:\n<msg>" —
            // location and message, which is what identifies the bug. No
            // backtrace: this is an LTO'd release build, where the frames are
            // largely inlined away, and the response file is not the place for
            // a page of them.
            let response: OcrResponse = Err(OcrError::Failed(format!("recognizer {info}")));
            if let Ok(json) = serde_json::to_vec(&response) {
                let _ = std::fs::write(path, json);
            }
        }
        previous(info);
    }));
}

/// Read one request: a single JSON header line, then exactly
/// `header.payload_len()` bytes of tightly packed BGRA through to EOF.
///
/// The length is checked rather than trusted. A short payload would otherwise
/// reach the detector as a mis-sized buffer, and a long one means the two
/// sides disagree about the format — both are bugs on our own side of a
/// private protocol, so they fail loudly here.
fn read_request() -> anyhow::Result<OcrRequest> {
    let mut stdin = BufReader::new(std::io::stdin().lock());

    let mut line = String::new();
    let read = stdin
        .read_line(&mut line)
        .context("reading the request header line")?;
    if read == 0 {
        bail!("stdin closed before the request header arrived");
    }
    let header: RequestHeader = serde_json::from_str(line.trim_end()).with_context(|| format!("parsing the request header {line:?}"))?;

    // read_to_end on the same BufReader, which drains what the header read
    // already buffered before touching the pipe again.
    let mut bgra = Vec::with_capacity(header.payload_len());
    stdin
        .read_to_end(&mut bgra)
        .context("reading the pixel payload")?;
    if bgra.len() != header.payload_len() {
        bail!(
            "pixel payload is {} bytes, expected {} for {}x{}",
            bgra.len(),
            header.payload_len(),
            header.width,
            header.height
        );
    }

    Ok(OcrRequest {
        bgra,
        width: header.width,
        height: header.height,
        origin: header.origin,
    })
}

/// A CPU-only session over an embedded model. Plain `Session::builder()`, not
/// `ep_session_builder()` — see the module docs for why OCR stays off the
/// hardware providers.
fn cpu_session(model: &'static [u8]) -> ort::Result<Session> {
    Session::builder()?.commit_from_memory(model)
}

/// The detector, created on first use. `Option` caches a failed init too — a
/// runtime that cannot start once will not start later, so a second
/// recognize in the same process reports `Unavailable` without retrying.
///
/// `Mutex` because `Session::run` takes `&mut self` and a `static` must be
/// shared. It is not concurrency control — a one-shot child recognizes
/// exactly once, so the lock is uncontended by construction.
fn detector() -> Option<&'static Mutex<Session>> {
    static DET: OnceLock<Option<Mutex<Session>>> = OnceLock::new();
    DET.get_or_init(|| {
        let t = Instant::now();
        match cpu_session(DET_MODEL) {
            Ok(session) => {
                log::info!("OCR detector init {:?}", t.elapsed());
                Some(Mutex::new(session))
            }
            Err(e) => {
                log::error!("PaddleOCR detector init failed: {e}");
                None
            }
        }
    })
    .as_ref()
}

/// A recognition model plus its decoder charset.
struct Recognizer {
    session: Session,
    /// Index → text. Index 0 is the CTC blank; the model's last class is the
    /// space character, which the dictionary file does not list.
    charset: Vec<&'static str>,
}

impl Recognizer {
    /// Build the ONE recognition model the tier choice landed on.
    ///
    /// Deliberately after detection and deliberately not cached: parsing both
    /// tiers (21 MB + 4.5 MB of weights) would be a tax on every OCR press for
    /// a model that is never used. The tier is not known until det has
    /// produced its boxes, so this cannot be hoisted; it can only be made to
    /// load one model instead of two.
    fn load(use_tiny: bool) -> Option<Self> {
        let (model, dict, name) = if use_tiny {
            (TINY_REC_MODEL, TINY_CHARSET, "tiny")
        } else {
            (REC_MODEL, CHARSET, "small")
        };
        let t = Instant::now();
        match cpu_session(model) {
            Ok(session) => {
                log::info!("OCR {name} recognizer init {:?}", t.elapsed());
                Some(Self {
                    session,
                    charset: charset_from_dict(dict),
                })
            }
            Err(e) => {
                log::error!("PaddleOCR {name} recognizer init failed: {e}");
                None
            }
        }
    }
}

/// PaddleOCR's CTC label layout: `[blank] + dictionary lines + [space]`.
/// Line order is the class order; lines are trimmed of the line ending only
/// (a dictionary entry can legitimately be a non-ASCII space).
fn charset_from_dict(dict: &'static str) -> Vec<&'static str> {
    let mut charset = Vec::with_capacity(dict.lines().count() + 2);
    charset.push("\0");
    charset.extend(
        dict.lines()
            .map(|l| l.trim_end_matches('\r')),
    );
    charset.push(" ");
    charset
}

/// An axis-aligned detection box in input-image pixel coordinates.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct DetBox {
    left: u32,
    top: u32,
    width: u32,
    height: u32,
}

impl DetBox {
    /// Inflate by `border` on every side, clamped to the image; never
    /// smaller than 1x1.
    fn expand(&self, border: u32, max_width: u32, max_height: u32) -> Self {
        let left = self.left.saturating_sub(border);
        let top = self.top.saturating_sub(border);
        let right = (self.left + self.width + border).min(max_width);
        let bottom = (self.top + self.height + border).min(max_height);
        Self {
            left,
            top,
            width: right.saturating_sub(left).max(1),
            height: bottom.saturating_sub(top).max(1),
        }
    }
}

/// Reading-order permutation of detection boxes given as (top, left,
/// height) keys: rows are clustered top-down with a half-line tolerance,
/// then each row runs left-to-right.
///
/// Two passes (cluster, then sort by the fixed row key) rather than one
/// comparator with the tolerance inline: a per-pair tolerance is not
/// transitive — three boxes each within tolerance of their neighbor but
/// not of the ends compare A<B, B<C by left yet A<C by top, an ordering
/// cycle — and std's sort is allowed to PANIC when it detects a
/// comparator that is not a total order. Row ids assigned once are a
/// total order by construction.
fn reading_order(keys: &[(i32, i32, u32)]) -> Vec<usize> {
    let mut idx: Vec<usize> = (0..keys.len()).collect();
    idx.sort_by_key(|&i| keys[i].0);
    // Cluster: a box joins the current row while its top is within half
    // the shorter line height of the row's FIRST box (the anchor —
    // comparing against the anchor rather than the previous box stops a
    // gentle staircase from chaining into one giant "row").
    let mut row_of = vec![0u32; keys.len()];
    let mut row = 0u32;
    let mut anchor = 0usize;
    for k in 1..idx.len() {
        let (top, _, h) = keys[idx[k]];
        let (anchor_top, _, anchor_h) = keys[idx[anchor]];
        let tolerance = (h.min(anchor_h) / 2) as i32;
        if top - anchor_top > tolerance {
            row += 1;
            anchor = k;
        }
        row_of[idx[k]] = row;
    }
    // Stable, so within-row ties keep their top-order from the first sort.
    idx.sort_by_key(|&i| (row_of[i], keys[i].1));
    idx
}

/// Predicted milliseconds for the SMALL model to recognize boxes of the
/// given (width, height) dimensions — the tier-choice input. Pure so the
/// threshold behavior is testable.
fn predict_small_rec_ms(dims: impl Iterator<Item = (u32, u32)>) -> f32 {
    dims.map(|(w, h)| {
        let aspect = w as f32 / (h as f32).max(1.0);
        SMALL_REC_FIXED_MS + (aspect * REC_TARGET_HEIGHT as f32 / 1000.0) * SMALL_REC_MS_PER_KPX
    })
    .sum()
}

/// Recognize every line of text in `req`. Blocking, and the only thing this
/// process does; a request the user abandons is canceled by the capturer
/// killing us mid-call (see the module docs).
pub fn recognize(req: &OcrRequest) -> Result<OcrOutcome, OcrError> {
    let Some(det) = detector() else {
        return Err(OcrError::Unavailable);
    };
    let img = Bgra {
        data: &req.bgra,
        width: req.width,
        height: req.height,
    };

    let t_det = Instant::now();
    let boxes = {
        let mut d = det
            .lock()
            .expect("OCR detector lock poisoned");
        detect(&mut d, &img).map_err(|e| OcrError::Failed(format!("detection: {e:#}")))?
        // lock released here: recognition uses its own model, so holding the
        // detector through it would pin memory nothing is reading.
    };
    let det_elapsed = t_det.elapsed();

    // Reading order BEFORE the cap, so truncation keeps the top of the
    // page. Row clustering + fixed-key sort — see `reading_order` for why
    // this is not one comparator.
    let keys: Vec<(i32, i32, u32)> = boxes
        .iter()
        .map(|b| (b.top as i32, b.left as i32, b.height))
        .collect();
    let order = reading_order(&keys);
    let mut boxes: Vec<DetBox> = order.iter().map(|&i| boxes[i]).collect();
    boxes.retain(|bx| {
        let aspect = bx.width as f32 / (bx.height as f32).max(1.0);
        aspect <= MAX_BOX_ASPECT
    });
    let detected = boxes.len();
    if detected > MAX_LINES {
        log::warn!("OCR truncated to {MAX_LINES} lines before recognition (detector found {detected})");
        boxes.truncate(MAX_LINES);
    }

    // Tier choice — see the cost model constants. Logged with the numbers
    // so a slow-feeling capture can be diagnosed from the log alone.
    let predicted_ms = predict_small_rec_ms(boxes.iter().map(|b| (b.width, b.height)));
    let use_tiny = predicted_ms > SMALL_TIER_BUDGET_MS;
    log::info!(
        "OCR det {:?} ({} boxes), predicted small-rec {:.0} ms -> {} tier",
        det_elapsed,
        boxes.len(),
        predicted_ms,
        if use_tiny { "tiny" } else { "small" },
    );
    let Some(mut rec) = Recognizer::load(use_tiny) else {
        return Err(OcrError::Unavailable);
    };

    let t_rec = Instant::now();

    // The detector box expanded by the context border, clamped to the image.
    // Kept in reading order; `expanded` doubles as the geometry source below.
    let expanded: Vec<DetBox> = boxes
        .iter()
        .map(|b| b.expand(BOX_BORDER, req.width, req.height))
        .collect();
    let crops: Vec<RgbaImage> = expanded
        .iter()
        .map(|b| img.crop_for_rec(b))
        .collect();
    let results = recognize_crops(&mut rec, &crops);

    let ox = req.origin.left() as f32;
    let oy = req.origin.top() as f32;
    let mut lines: Vec<OcrLine> = Vec::with_capacity(boxes.len());
    for (r, result) in expanded.iter().zip(results) {
        let Some((text, confidence)) = result else {
            continue;
        };
        if text.trim().is_empty() || confidence < MIN_CONFIDENCE {
            continue;
        }

        // Deflate the crop border back out, but never below 1px. (At image
        // edges expand() clamped instead of inflating, so this over-tightens
        // by up to BOX_BORDER px there — invisible in practice and not
        // worth tracking the clamp per side.)
        let bpx = BOX_BORDER as f32;
        let (mut left, mut top) = (r.left as f32 + bpx, r.top as f32 + bpx);
        let (mut right, mut bottom) = (r.left as f32 + r.width as f32 - bpx, r.top as f32 + r.height as f32 - bpx);
        if right - left < 1.0 {
            left = r.left as f32;
            right = left + r.width as f32;
        }
        if bottom - top < 1.0 {
            top = r.top as f32;
            bottom = top + r.height as f32;
        }
        // Then the unclip tighten (see UNCLIP_TIGHTEN) — skipped per axis
        // if it would collapse the rect.
        let d = (bottom - top) * UNCLIP_TIGHTEN;
        if bottom - top - 2.0 * d >= 1.0 {
            top += d;
            bottom -= d;
        }
        if right - left - 2.0 * d >= 1.0 {
            left += d;
            right -= d;
        }
        lines.push(OcrLine {
            text,
            // Boxes are in input-image coordinates (detection's internal
            // downscale is mapped back in `detect`), so only the crop-origin
            // offset applies here.
            rect: ScreenRectF::from_exact(ox + left, oy + top, ox + right, oy + bottom),
        });
    }
    log::info!(
        "OCR rec {:?}: {} of {} regions produced text",
        t_rec.elapsed(),
        lines.len(),
        boxes.len()
    );

    let full_text = lines
        .iter()
        .map(|l| l.text.as_str())
        .collect::<Vec<_>>()
        .join("\n");

    Ok(OcrOutcome {
        lines,
        full_text,
        // PaddleOCR reports no skew estimate; the field is diagnostic-only.
        text_angle: 0.0,
    })
}

/// A borrowed, tightly packed BGRA8 image.
struct Bgra<'a> {
    data: &'a [u8],
    width: u32,
    height: u32,
}

impl Bgra<'_> {
    /// Crop `b` and resize it to the recognizer's input height, preserving
    /// aspect (bilinear, like PaddleOCR's cv2 INTER_LINEAR). The result keeps
    /// the source's B,G,R,A byte order — `RgbaImage` is just the container,
    /// the resize filter is per-channel — which is exactly the channel order
    /// the recognizer wants (see [`recognize_crops`]).
    fn crop_for_rec(&self, b: &DetBox) -> RgbaImage {
        let w = b.width.min(self.width - b.left).max(1) as usize;
        let h = b.height.min(self.height - b.top).max(1) as usize;
        let mut crop = vec![0u8; w * h * 4];
        let stride = self.width as usize * 4;
        for y in 0..h {
            let src = (b.top as usize + y) * stride + b.left as usize * 4;
            crop[y * w * 4..(y + 1) * w * 4].copy_from_slice(&self.data[src..src + w * 4]);
        }
        let crop = RgbaImage::from_raw(w as u32, h as u32, crop).expect("crop buffer is w*h*4");
        let target_w = ((w as f32 * REC_TARGET_HEIGHT as f32 / h as f32).ceil() as u32).clamp(1, REC_MAX_WIDTH);
        if h as u32 == REC_TARGET_HEIGHT && w as u32 == target_w {
            return crop;
        }
        image::imageops::resize(&crop, target_w, REC_TARGET_HEIGHT, image::imageops::FilterType::Triangle)
    }
}

/// ImageNet normalization applied in B,G,R channel order — PaddleOCR's
/// detector is trained on cv2 (BGR) input, with these stats in that same
/// channel order. `scale[c]`/`offset[c]` for channel c of a B,G,R tensor.
const DET_NORM: [(f32, f32); 3] = [
    (1.0 / (255.0 * 0.229), -0.485 / 0.229),
    (1.0 / (255.0 * 0.224), -0.456 / 0.224),
    (1.0 / (255.0 * 0.225), -0.406 / 0.225),
];

/// Run the DB detector over the whole image and return axis-aligned text
/// boxes in input-image coordinates, in raw detection order.
fn detect(session: &mut Session, img: &Bgra<'_>) -> anyhow::Result<Vec<DetBox>> {
    // Downscale only beyond the ceiling (see DET_MAX_SIDE_LEN); otherwise the
    // detector sees native pixels. `scaled` keeps the B,G,R,A byte order.
    let max_side = img.width.max(img.height);
    let (scaled, ratio): (std::borrow::Cow<'_, [u8]>, f32) = if max_side > DET_MAX_SIDE_LEN {
        let ratio = DET_MAX_SIDE_LEN as f32 / max_side as f32;
        let w = ((img.width as f32 * ratio).round() as u32).max(1);
        let h = ((img.height as f32 * ratio).round() as u32).max(1);
        let src = RgbaImage::from_raw(img.width, img.height, img.data.to_vec()).expect("request payload is w*h*4");
        let resized = image::imageops::resize(&src, w, h, image::imageops::FilterType::Triangle);
        (std::borrow::Cow::Owned(resized.into_raw()), ratio)
    } else {
        (std::borrow::Cow::Borrowed(img.data), 1.0)
    };
    let (sw, sh) = (
        ((img.width as f32 * ratio).round() as u32).max(1) as usize,
        ((img.height as f32 * ratio).round() as u32).max(1) as usize,
    );
    debug_assert_eq!(scaled.len(), sw * sh * 4);

    // Pad (never resample) right/bottom to the 32-multiple the network's
    // strides need. Zero padding in normalized space reads as a dark border
    // — harmless, and it keeps small text at native resolution.
    let pw = sw.div_ceil(32).max(1) * 32;
    let ph = sh.div_ceil(32).max(1) * 32;
    let plane = pw * ph;
    let mut input = vec![0f32; 3 * plane];
    {
        let (bp, rest) = input.split_at_mut(plane);
        let (gp, rp) = rest.split_at_mut(plane);
        for y in 0..sh {
            let row = &scaled[y * sw * 4..(y + 1) * sw * 4];
            let dst = y * pw;
            // `row` is sliced to exactly `sw` BGRA pixels, so `as_chunks`
            // never has a remainder to drop.
            for (x, px) in row.as_chunks::<4>().0.iter().enumerate() {
                bp[dst + x] = px[0] as f32 * DET_NORM[0].0 + DET_NORM[0].1;
                gp[dst + x] = px[1] as f32 * DET_NORM[1].0 + DET_NORM[1].1;
                rp[dst + x] = px[2] as f32 * DET_NORM[2].0 + DET_NORM[2].1;
            }
        }
    }

    let outputs = session
        .run(ort::inputs!["x" => Tensor::from_array((vec![1usize, 3, ph, pw], input))?])
        .context("running the detector")?;
    let (shape, prob) = outputs["fetch_name_0"]
        .try_extract_tensor::<f32>()
        .context("extracting the detector output")?;
    let dims: Vec<usize> = shape.iter().map(|&d| d as usize).collect();
    anyhow::ensure!(
        dims.len() == 4 && dims[0] == 1 && dims[1] == 1 && dims[2] * dims[3] == prob.len(),
        "unexpected detector output shape {dims:?}"
    );
    let (out_h, out_w) = (dims[2], dims[3]);
    // The DB head is full-resolution; tolerate a re-export that is not by
    // mapping boxes through the output→padded-input scale.
    let (sx, sy) = (pw as f32 / out_w as f32, ph as f32 / out_h as f32);
    // The valid (unpadded) part of the output.
    let valid_w = ((sw as f32 / sx).round() as usize).clamp(1, out_w);
    let valid_h = ((sh as f32 / sy).round() as usize).clamp(1, out_h);

    let raw = db_boxes(prob, out_w, out_h, valid_w, valid_h);
    Ok(raw
        .into_iter()
        .filter_map(|[l, t, r, b]| {
            // Output → padded-input → input coordinates.
            let l = (l * sx / ratio)
                .floor()
                .max(0.0)
                .min(img.width as f32 - 1.0);
            let t = (t * sy / ratio)
                .floor()
                .max(0.0)
                .min(img.height as f32 - 1.0);
            let r = (r * sx / ratio).ceil().min(img.width as f32);
            let b = (b * sy / ratio)
                .ceil()
                .min(img.height as f32);
            let (w, h) = (r - l, b - t);
            (w >= 1.0 && h >= 1.0).then_some(DetBox {
                left: l as u32,
                top: t as u32,
                width: w as u32,
                height: h as u32,
            })
        })
        .collect())
}

/// DB post-processing: binarize the probability map, take each connected
/// text blob's minimum-area rectangle, score it, unclip it, and return its
/// axis-aligned bounds `[left, top, right, bottom]` in output-map pixels.
///
/// Only the `valid_w × valid_h` corner is real image; the rest is padding.
fn db_boxes(prob: &[f32], out_w: usize, out_h: usize, valid_w: usize, valid_h: usize) -> Vec<[f32; 4]> {
    // Binary mask over the valid region only — a blob leaking into the
    // padding is clamped back below anyway.
    let mut mask = vec![false; out_w * out_h];
    for y in 0..valid_h {
        for x in 0..valid_w {
            mask[y * out_w + x] = prob[y * out_w + x] > DET_THRESH;
        }
    }

    let mut boxes = Vec::new();
    let mut visited = vec![false; out_w * out_h];
    let mut stack: Vec<(usize, usize)> = Vec::new();
    let mut boundary: Vec<(f32, f32)> = Vec::new();
    for sy in 0..valid_h {
        for sx in 0..valid_w {
            let seed = sy * out_w + sx;
            if !mask[seed] || visited[seed] {
                continue;
            }
            // Flood-fill one 8-connected blob, keeping only its boundary
            // pixels: the convex hull of the boundary is the hull of the
            // blob, and a 1000x20 text line has ~2000 boundary pixels
            // against 20000 interior ones.
            boundary.clear();
            visited[seed] = true;
            stack.push((sx, sy));
            while let Some((x, y)) = stack.pop() {
                let mut on_boundary = false;
                for (dx, dy) in [(-1i32, 0i32), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1)] {
                    let (nx, ny) = (x as i32 + dx, y as i32 + dy);
                    if nx < 0 || ny < 0 || nx >= valid_w as i32 || ny >= valid_h as i32 {
                        on_boundary |= dx == 0 || dy == 0;
                        continue;
                    }
                    let n = ny as usize * out_w + nx as usize;
                    if !mask[n] {
                        on_boundary |= dx == 0 || dy == 0;
                    } else if !visited[n] {
                        visited[n] = true;
                        stack.push((nx as usize, ny as usize));
                    }
                }
                if on_boundary {
                    boundary.push((x as f32, y as f32));
                }
            }

            let Some(rect) = min_area_rect(&boundary) else {
                continue;
            };
            if rect.width.min(rect.height) < MIN_BOX_SIDE {
                continue;
            }
            // Fast box score: mean probability over the box's axis-aligned
            // bounds (PaddleOCR's `box_score_fast` on a quad).
            let [l, t, r, b] = rect.aabb();
            let (x0, x1) = ((l.floor().max(0.0)) as usize, (r.ceil() as usize).min(valid_w - 1));
            let (y0, y1) = ((t.floor().max(0.0)) as usize, (b.ceil() as usize).min(valid_h - 1));
            let mut sum = 0f32;
            let mut n = 0usize;
            for y in y0..=y1 {
                for x in x0..=x1 {
                    sum += prob[y * out_w + x];
                    n += 1;
                }
            }
            if n == 0 || sum / (n as f32) < BOX_THRESH {
                continue;
            }
            // Unclip: inflate the text core back out to the glyph extent by
            // the DB offset distance (area × ratio / perimeter), never less
            // than a pixel (pixel centers under-measure the blob by one).
            let distance = (rect.width * rect.height * UNCLIP_RATIO / (2.0 * (rect.width + rect.height))).max(1.0);
            let inflated = RotatedRect {
                width: rect.width + 2.0 * distance,
                height: rect.height + 2.0 * distance,
                ..rect
            };
            let [l, t, r, b] = inflated.aabb();
            boxes.push([
                l.clamp(0.0, valid_w as f32),
                t.clamp(0.0, valid_h as f32),
                r.clamp(0.0, valid_w as f32),
                b.clamp(0.0, valid_h as f32),
            ]);
        }
    }
    boxes
}

/// A rectangle of `width × height` rotated by `angle` radians about `center`.
#[derive(Debug, Clone, Copy)]
struct RotatedRect {
    center: (f32, f32),
    width: f32,
    height: f32,
    angle: f32,
}

impl RotatedRect {
    /// Axis-aligned bounds `[left, top, right, bottom]` of the four corners.
    fn aabb(&self) -> [f32; 4] {
        let (cos, sin) = (self.angle.cos(), self.angle.sin());
        let (hw, hh) = (self.width * 0.5, self.height * 0.5);
        let mut out = [f32::INFINITY, f32::INFINITY, f32::NEG_INFINITY, f32::NEG_INFINITY];
        for (x, y) in [(-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh)] {
            let px = self.center.0 + x * cos - y * sin;
            let py = self.center.1 + x * sin + y * cos;
            out[0] = out[0].min(px);
            out[1] = out[1].min(py);
            out[2] = out[2].max(px);
            out[3] = out[3].max(py);
        }
        out
    }
}

/// Minimum-area enclosing rectangle of a point set (rotating calipers over
/// the convex hull: the optimum has an edge collinear with a hull edge).
/// `None` for fewer than three distinct points.
fn min_area_rect(points: &[(f32, f32)]) -> Option<RotatedRect> {
    let hull = convex_hull(points);
    if hull.len() < 3 {
        return None;
    }
    let mut best: Option<RotatedRect> = None;
    let mut best_area = f32::INFINITY;
    for i in 0..hull.len() {
        let (x1, y1) = hull[i];
        let (x2, y2) = hull[(i + 1) % hull.len()];
        let (dx, dy) = (x2 - x1, y2 - y1);
        if dx.abs() < f32::EPSILON && dy.abs() < f32::EPSILON {
            continue;
        }
        let angle = dy.atan2(dx);
        let (cos, sin) = (angle.cos(), angle.sin());
        let (mut min_x, mut max_x, mut min_y, mut max_y) = (f32::INFINITY, f32::NEG_INFINITY, f32::INFINITY, f32::NEG_INFINITY);
        for &(px, py) in &hull {
            let x = px * cos + py * sin;
            let y = -px * sin + py * cos;
            min_x = min_x.min(x);
            max_x = max_x.max(x);
            min_y = min_y.min(y);
            max_y = max_y.max(y);
        }
        let (width, height) = (max_x - min_x, max_y - min_y);
        let area = width * height;
        if width <= 0.0 || height <= 0.0 || area >= best_area {
            continue;
        }
        let (cx, cy) = ((min_x + max_x) * 0.5, (min_y + max_y) * 0.5);
        best_area = area;
        best = Some(RotatedRect {
            center: (cx * cos - cy * sin, cx * sin + cy * cos),
            width,
            height,
            angle,
        });
    }
    best
}

/// Andrew's monotone chain; returns the hull counter-clockwise without the
/// closing point (collinear points dropped).
fn convex_hull(points: &[(f32, f32)]) -> Vec<(f32, f32)> {
    let mut sorted = points.to_vec();
    sorted.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.total_cmp(&b.1)));
    sorted.dedup();
    if sorted.len() <= 2 {
        return sorted;
    }
    let cross = |o: (f32, f32), a: (f32, f32), b: (f32, f32)| (a.0 - o.0) * (b.1 - o.1) - (a.1 - o.1) * (b.0 - o.0);
    let mut lower: Vec<(f32, f32)> = Vec::new();
    for &p in &sorted {
        while lower.len() >= 2 && cross(lower[lower.len() - 2], lower[lower.len() - 1], p) <= 0.0 {
            lower.pop();
        }
        lower.push(p);
    }
    let mut upper: Vec<(f32, f32)> = Vec::new();
    for &p in sorted.iter().rev() {
        while upper.len() >= 2 && cross(upper[upper.len() - 2], upper[upper.len() - 1], p) <= 0.0 {
            upper.pop();
        }
        upper.push(p);
    }
    lower.pop();
    upper.pop();
    lower.extend(upper);
    lower
}

/// Recognize every crop, batched in width-sorted chunks of [`REC_BATCH`]
/// (see there). Returns `(text, mean confidence)` per crop, in the crops'
/// own order; `None` where inference failed for that crop.
fn recognize_crops(rec: &mut Recognizer, crops: &[RgbaImage]) -> Vec<Option<(String, f32)>> {
    let mut results: Vec<Option<(String, f32)>> = vec![None; crops.len()];
    if crops.is_empty() {
        return results;
    }
    let mut rec_order: Vec<usize> = (0..crops.len()).collect();
    rec_order.sort_by_key(|&i| crops[i].width());
    for chunk in rec_order.chunks(REC_BATCH) {
        let refs: Vec<&RgbaImage> = chunk.iter().map(|&i| &crops[i]).collect();
        match infer_batch(rec, &refs) {
            Ok(rs) => {
                for (&i, r) in chunk.iter().zip(rs) {
                    results[i] = Some(r);
                }
            }
            Err(e) => {
                // One unreadable region must not kill the whole page: a
                // failed batch falls back to its members individually so
                // only the truly bad crop is lost.
                log::warn!("OCR batch failed ({e:#}); retrying its regions individually");
                for &i in chunk {
                    match infer_batch(rec, &[&crops[i]]) {
                        Ok(mut r) => results[i] = r.pop(),
                        Err(e) => log::warn!("OCR region failed: {e:#}"),
                    }
                }
            }
        }
    }
    results
}

/// One recognizer call over `crops` (all 48 px tall, B,G,R,A bytes): a
/// `[N,3,48,Wmax]` tensor, each crop left-aligned and zero-padded on the
/// right in normalized space (`v/127.5 - 1`, B,G,R channel order — PaddleOCR
/// trains its recognizer on cv2 BGR input), then greedy CTC decoding of the
/// `[N,T,C]` per-step softmax.
fn infer_batch(rec: &mut Recognizer, crops: &[&RgbaImage]) -> anyhow::Result<Vec<(String, f32)>> {
    let h = REC_TARGET_HEIGHT as usize;
    let wmax = crops
        .iter()
        .map(|c| c.width() as usize)
        .max()
        .unwrap_or(1)
        .max(1);
    let plane = h * wmax;
    let mut input = vec![0f32; crops.len() * 3 * plane];
    for (n, crop) in crops.iter().enumerate() {
        anyhow::ensure!(crop.height() as usize == h, "crop is {} px tall, not {h}", crop.height());
        let w = crop.width() as usize;
        let sample = &mut input[n * 3 * plane..(n + 1) * 3 * plane];
        let (bp, rest) = sample.split_at_mut(plane);
        let (gp, rp) = rest.split_at_mut(plane);
        let raw = crop.as_raw();
        for y in 0..h {
            let row = &raw[y * w * 4..(y + 1) * w * 4];
            let dst = y * wmax;
            // `row` is sliced to exactly `w` BGRA pixels, so `as_chunks`
            // never has a remainder to drop.
            for (x, px) in row.as_chunks::<4>().0.iter().enumerate() {
                bp[dst + x] = px[0] as f32 / 127.5 - 1.0;
                gp[dst + x] = px[1] as f32 / 127.5 - 1.0;
                rp[dst + x] = px[2] as f32 / 127.5 - 1.0;
            }
        }
    }

    let outputs = rec
        .session
        .run(ort::inputs!["x" => Tensor::from_array((vec![crops.len(), 3, h, wmax], input))?])
        .context("running the recognizer")?;
    let (shape, probs) = outputs["fetch_name_0"]
        .try_extract_tensor::<f32>()
        .context("extracting the recognizer output")?;
    let dims: Vec<usize> = shape.iter().map(|&d| d as usize).collect();
    anyhow::ensure!(
        dims.len() == 3 && dims[0] == crops.len() && dims[2] == rec.charset.len() && dims[0] * dims[1] * dims[2] == probs.len(),
        "unexpected recognizer output shape {dims:?} for {} crops and a {}-class charset",
        crops.len(),
        rec.charset.len()
    );
    let (steps, classes) = (dims[1], dims[2]);
    Ok((0..crops.len())
        .map(|n| ctc_decode(&probs[n * steps * classes..(n + 1) * steps * classes], classes, &rec.charset))
        .collect())
}

/// Greedy CTC decode of one sample's `[T,C]` softmax: per-step argmax, drop
/// blanks (class 0) and repeats of the previous step's class, and average
/// the kept steps' probabilities as the confidence (0.0 when nothing was
/// kept).
fn ctc_decode(probs: &[f32], classes: usize, charset: &[&str]) -> (String, f32) {
    let mut text = String::new();
    let mut conf_sum = 0f32;
    let mut kept = 0usize;
    let mut prev = 0usize;
    for step in probs.chunks_exact(classes) {
        let (idx, &p) = step
            .iter()
            .enumerate()
            .fold((0usize, &f32::NEG_INFINITY), |best, (i, v)| if v > best.1 { (i, v) } else { best });
        if idx != 0 && idx != prev {
            if let Some(s) = charset.get(idx) {
                text.push_str(s);
                conf_sum += p;
                kept += 1;
            }
        }
        prev = idx;
    }
    let confidence = if kept == 0 { 0.0 } else { conf_sum / kept as f32 };
    (text, confidence)
}

#[cfg(test)]
mod tests {
    use super::*;
    use clowd_rust_core::geometry::ScreenRect;

    fn request(bgra: Vec<u8>, w: u32, h: u32) -> OcrRequest {
        OcrRequest {
            bgra,
            width: w,
            height: h,
            origin: ScreenRect::from_xy_size(0, 0, w as i32, h as i32),
        }
    }

    /// Decode an RGBA PNG/JPEG into the request's BGRA layout.
    fn load_bgra(path: &str) -> (Vec<u8>, u32, u32) {
        let img = image::open(path)
            .unwrap_or_else(|e| panic!("{path}: {e}"))
            .to_rgba8();
        let (w, h) = img.dimensions();
        let mut bgra = img.into_raw();
        for px in bgra.as_chunks_mut::<4>().0.iter_mut() {
            px.swap(0, 2);
        }
        (bgra, w, h)
    }

    /// The panic path is the recognizer's only channel for explaining itself —
    /// it has no Sentry client, and the capturer reads this file for a message
    /// when we exit abnormally. Verified rather than assumed, because a hook
    /// that silently failed would degrade every future panic to "exited with
    /// code 101" with nothing to go on.
    #[test]
    fn panic_reporter_leaves_its_message_in_the_response_file() {
        let path = std::env::temp_dir().join(format!("clowd_ai_ocr_panic_test_{}.json", std::process::id()));
        let _ = std::fs::remove_file(&path);
        OUT_PATH
            .set(path.clone())
            .expect("no other test sets the out path");
        install_panic_reporter();

        // The chained hook still prints to stderr, so the deliberate panic is
        // noisy in the test output. That is the real behavior.
        let panicked = std::panic::catch_unwind(|| panic!("deliberate test panic"));
        assert!(panicked.is_err(), "the closure must actually panic");

        let bytes = std::fs::read(&path).expect("the hook wrote a response file");
        let response: OcrResponse = serde_json::from_slice(&bytes).expect("the response parses");
        let OcrError::Failed(message) = response.expect_err("a panic is reported as an error") else {
            panic!("a panic must report as Failed, not Unavailable");
        };
        // Message AND location — the location is what identifies the bug.
        assert!(message.contains("deliberate test panic"), "message lost: {message}");
        assert!(message.contains("ocr.rs"), "panic location lost: {message}");

        let _ = std::fs::remove_file(&path);
    }

    /// A blank image must come back as a clean empty success.
    #[test]
    fn blank_image_recognizes_to_empty() {
        let req = request(vec![255u8; 200 * 100 * 4], 200, 100);
        let outcome = recognize(&req).expect("blank image must not error");
        assert!(outcome.lines.is_empty());
        assert_eq!(outcome.full_text, "");
    }

    /// Whatever the engine hallucinates out of noise, every returned rect
    /// must land inside the request's origin rect — pins the crop-origin
    /// offset (zero lines passes trivially; the property is geometry).
    #[test]
    fn noise_line_rects_stay_within_origin() {
        let (w, h) = (320u32, 240u32);
        // Deterministic LCG so failures reproduce; no rand dependency.
        let mut state = 0x12345678u32;
        let bgra: Vec<u8> = (0..w * h * 4)
            .map(|_| {
                state = state
                    .wrapping_mul(1664525)
                    .wrapping_add(1013904223);
                (state >> 24) as u8
            })
            .collect();
        let mut req = request(bgra, w, h);
        // Negative origin: the offset math must hold on multi-monitor
        // layouts where the virtual desktop starts left of zero.
        req.origin = ScreenRect::from_xy_size(-500, -300, w as i32, h as i32);
        let outcome = recognize(&req).expect("noise must not error");
        let bounds = req.origin.to_f32();
        for line in &outcome.lines {
            assert!(
                line.rect.left() >= bounds.left() - 1.0
                    && line.rect.top() >= bounds.top() - 1.0
                    && line.rect.right() <= bounds.right() + 1.0
                    && line.rect.bottom() <= bounds.bottom() + 1.0,
                "line {:?} outside origin {:?}",
                line.rect,
                bounds
            );
        }
    }

    /// Two back-to-back calls through the cached detector: pins the OnceLock
    /// reuse and that an inference leaves the session reusable.
    #[test]
    fn consecutive_recognize_calls_succeed() {
        let req = request(vec![255u8; 64 * 64 * 4], 64, 64);
        recognize(&req).expect("first call");
        recognize(&req).expect("second call");
    }

    /// Degenerate sizes the overlay can legitimately send (the warm-up is a
    /// 1x1 image) must not panic anywhere in the padding/scaling math.
    #[test]
    fn tiny_and_thin_images_do_not_panic() {
        for (w, h) in [(1u32, 1u32), (1, 200), (200, 1), (33, 31)] {
            recognize(&request(vec![0x80; (w * h * 4) as usize], w, h)).unwrap_or_else(|e| panic!("{w}x{h}: {e:?}"));
        }
    }

    /// Reading order: rows cluster within a half-line tolerance and run
    /// left-to-right; distinct rows keep their top-down order.
    #[test]
    fn reading_order_rows_then_columns() {
        // (top, left, height): two visual rows with jittered tops, the
        // second box of row one further left than the first.
        let keys = [(0, 300, 20), (4, 10, 20), (60, 50, 20), (57, 200, 20)];
        assert_eq!(reading_order(&keys), vec![1, 0, 2, 3]);
        // Degenerate inputs.
        assert_eq!(reading_order(&[]), Vec::<usize>::new());
        assert_eq!(reading_order(&[(5, 5, 10)]), vec![0]);
    }

    /// The historical failure mode this replaced: a chain of boxes each
    /// within tolerance of its neighbor but not of the chain's ends is
    /// INTRANSITIVE under a pairwise comparator (A<B and B<C by left, but
    /// A<C by top — a cycle std's sort may panic on). The clustered order
    /// must simply produce a valid permutation, deterministically.
    #[test]
    fn reading_order_survives_tolerance_chains() {
        // Tops 0, 8, 16 with height 20: each neighbor pair is "same row"
        // (tolerance 10) but the ends are not; lefts descend so the pair
        // comparisons used to disagree with the top comparison.
        let keys = [(0, 30, 20), (8, 20, 20), (16, 10, 20)];
        let order = reading_order(&keys);
        let mut sorted = order.clone();
        sorted.sort_unstable();
        assert_eq!(sorted, vec![0, 1, 2], "not a permutation: {order:?}");
        // Anchor-based clustering: 8 joins 0's row, 16 starts a new row.
        assert_eq!(order, vec![1, 0, 2]);

        // A long staircase must not chain into one giant row: each step is
        // within tolerance of the previous but far from the first.
        let stairs: Vec<(i32, i32, u32)> = (0..10)
            .map(|i| (i * 8, 100 - i * 10, 20))
            .collect();
        let order = reading_order(&stairs);
        let mut sorted = order.clone();
        sorted.sort_unstable();
        assert_eq!(sorted, (0..10).collect::<Vec<_>>());
    }

    /// The tier predictor: cheap boxes stay under the budget, a dense page
    /// of wide lines goes over. Pins the cost model's shape (fixed +
    /// width-proportional), not its absolute calibration.
    #[test]
    fn tier_prediction_scales_with_count_and_width() {
        // A handful of short lines: small tier.
        assert!(predict_small_rec_ms(std::iter::repeat_n((400u32, 20u32), 8)) < SMALL_TIER_BUDGET_MS);

        // A dense terminal: 176 full-width lines of small text blows the
        // budget (the measured multi-second case: 100 aspect = 4.8 kpx per
        // line, ~51 ms each on the small tier).
        assert!(predict_small_rec_ms(std::iter::repeat_n((1600u32, 16u32), 176)) > SMALL_TIER_BUDGET_MS);

        // Degenerate zero-height boxes must not divide by zero.
        assert!(predict_small_rec_ms(std::iter::once((100u32, 0u32))).is_finite());
    }

    /// The geometry helpers: an axis-aligned blob's minimum-area rect is the
    /// blob itself, and a rotated one comes back with the right size.
    #[test]
    fn min_area_rect_recovers_axis_aligned_and_rotated_boxes() {
        let pts: Vec<(f32, f32)> = (0..40)
            .flat_map(|x| [(x as f32, 0.0), (x as f32, 9.0)])
            .chain((0..10).flat_map(|y| [(0.0, y as f32), (39.0, y as f32)]))
            .collect();
        let r = min_area_rect(&pts).expect("rect");
        assert!(
            (r.width.max(r.height) - 39.0).abs() < 1e-3 && (r.width.min(r.height) - 9.0).abs() < 1e-3,
            "{r:?}"
        );
        let [l, t, rt, b] = r.aabb();
        assert!((l - 0.0).abs() < 1e-3 && (t - 0.0).abs() < 1e-3 && (rt - 39.0).abs() < 1e-3 && (b - 9.0).abs() < 1e-3);

        // The same box rotated 30°: area and sides survive, the AABB grows.
        let (c, s) = (30f32.to_radians().cos(), 30f32.to_radians().sin());
        let rot: Vec<(f32, f32)> = pts
            .iter()
            .map(|&(x, y)| (x * c - y * s + 100.0, x * s + y * c + 100.0))
            .collect();
        let r = min_area_rect(&rot).expect("rect");
        assert!(
            (r.width.max(r.height) - 39.0).abs() < 0.05 && (r.width.min(r.height) - 9.0).abs() < 0.05,
            "{r:?}"
        );
        // (39 cos30 + 9 sin30 = 38.3 wide by 39 sin30 + 9 cos30 = 27.3 tall.)
        let [l, t, rt, b] = r.aabb();
        assert!((rt - l - 38.3).abs() < 0.1 && (b - t - 27.3).abs() < 0.1, "{:?}", r.aabb());

        assert!(min_area_rect(&[]).is_none());
        assert!(min_area_rect(&[(1.0, 1.0), (2.0, 2.0)]).is_none());
    }

    /// Greedy CTC: blanks and repeats collapse, a repeat across a blank does
    /// not, and confidence averages only the kept steps.
    #[test]
    fn ctc_decode_collapses_blanks_and_repeats() {
        let charset = ["\0", "a", "b", " "];
        // steps: a a _ a b b _ (4 classes each)
        let mut probs = Vec::new();
        for (cls, p) in [(1, 0.9f32), (1, 0.8), (0, 0.99), (1, 0.7), (2, 0.6), (2, 0.5), (0, 0.9)] {
            let mut step = vec![0.0f32; 4];
            step[cls] = p;
            probs.extend(step);
        }
        let (text, conf) = ctc_decode(&probs, 4, &charset);
        assert_eq!(text, "aab");
        assert!((conf - (0.9 + 0.7 + 0.6) / 3.0).abs() < 1e-6, "{conf}");

        let (text, conf) = ctc_decode(&[1.0, 0.0, 0.0, 0.0], 4, &charset);
        assert_eq!(text, "");
        assert_eq!(conf, 0.0);
    }

    /// The charset layout the models were exported with: blank first, the
    /// dictionary in file order, space last — and the class counts match the
    /// models' output dimensions (18710 small, 6906 tiny).
    #[test]
    fn charsets_match_model_class_counts() {
        let small = charset_from_dict(CHARSET);
        let tiny = charset_from_dict(TINY_CHARSET);
        assert_eq!(small.len(), 18710);
        assert_eq!(tiny.len(), 6906);
        assert_eq!(small[0], "\0");
        assert_eq!(small[1], "!");
        assert_eq!(*small.last().unwrap(), " ");
        assert!(small.iter().skip(1).all(|s| !s.is_empty()), "empty dictionary entry");
    }

    /// Opt-in diagnostic for detection-resolution and tier-quality
    /// regressions (this probe found the DET_MAX_SIDE_LEN cliff on the MNN
    /// build: 0 boxes at 960, 522 fragments at 1920, 361 clean lines at
    /// native on the same 3440x1440 page). Runs det at native resolution,
    /// reports box count + det time, then compares small/tiny recognition on
    /// the same boxes — batched AND solo, so a batching-quality regression
    /// shows up as a batched/solo mismatch — and prints the per-batch timings
    /// the cost model constants are calibrated from. Set
    /// CLOWD_OCR_BENCH_IMAGE and run with --release --nocapture.
    #[test]
    fn env_det_ceiling_probe() {
        let Ok(path) = std::env::var("CLOWD_OCR_BENCH_IMAGE") else {
            eprintln!("SKIP {}: CLOWD_OCR_BENCH_IMAGE not set", module_path!());
            return;
        };
        let (bgra, w, h) = load_bgra(&path);
        eprintln!("image {w}x{h}");
        let img = Bgra {
            data: &bgra,
            width: w,
            height: h,
        };

        let det = detector().expect("det model");
        let mut det = det.lock().unwrap();
        let t = Instant::now();
        let _ = detect(&mut det, &img).expect("detect warmup");
        eprintln!("det warmup {:?}", t.elapsed());
        let t = Instant::now();
        let boxes = detect(&mut det, &img).expect("detect");
        eprintln!("det native: {} boxes in {:?}", boxes.len(), t.elapsed());
        drop(det);

        let predicted = predict_small_rec_ms(boxes.iter().map(|b| (b.width, b.height)));
        eprintln!("predicted small-rec: {predicted:.0} ms");

        let crops: Vec<RgbaImage> = boxes
            .iter()
            .map(|b| img.crop_for_rec(&b.expand(BOX_BORDER, w, h)))
            .collect();
        let mut order: Vec<usize> = (0..crops.len()).collect();
        order.sort_by_key(|&i| crops[i].width());
        let widest: Vec<usize> = order.iter().rev().take(5).copied().collect();
        let total_kpx: f32 = crops
            .iter()
            .map(|c| c.width() as f32)
            .sum::<f32>()
            / 1000.0;
        for use_tiny in [false, true] {
            let name = if use_tiny { "tiny" } else { "small" };
            let mut rec = Recognizer::load(use_tiny).expect("rec model");
            // Warm, then timed.
            let _ = recognize_crops(&mut rec, &crops[..crops.len().min(REC_BATCH)]);
            let t = Instant::now();
            let results = recognize_crops(&mut rec, &crops);
            let elapsed = t.elapsed();
            let kept: Vec<&(String, f32)> = results
                .iter()
                .flatten()
                .filter(|(s, c)| !s.trim().is_empty() && *c >= MIN_CONFIDENCE)
                .collect();
            let mean_conf = kept.iter().map(|(_, c)| c).sum::<f32>() / kept.len().max(1) as f32;
            eprintln!(
                "rec {name}: {}/{} lines in {:?} ({:.1} ms/crop, {:.1} ms/kpx over {total_kpx:.0} kpx), mean conf {mean_conf:.3}",
                kept.len(),
                crops.len(),
                elapsed,
                elapsed.as_secs_f32() * 1000.0 / crops.len().max(1) as f32,
                elapsed.as_secs_f32() * 1000.0 / total_kpx.max(1.0),
            );
            for &i in &widest {
                if let Some((s, c)) = &results[i] {
                    eprintln!("  [{i}] {c:.2} {s}");
                }
            }
            // The batching suspect: the same widest crops recognized
            // INDIVIDUALLY — if these come back better than the batched
            // rows above, batch padding is degrading recognition.
            for &i in &widest {
                if let Ok(mut r) = infer_batch(&mut rec, &[&crops[i]]) {
                    let (s, c) = r.pop().unwrap();
                    eprintln!("  solo[{i}] {c:.2} {s}");
                }
            }
            // Per-batch cost at two widths, for the cost-model constants.
            for width in [320u32, 1600] {
                let blank = RgbaImage::from_pixel(width, REC_TARGET_HEIGHT, image::Rgba([255, 255, 255, 255]));
                let refs: Vec<&RgbaImage> = (0..REC_BATCH).map(|_| &blank).collect();
                let _ = infer_batch(&mut rec, &refs);
                let t = Instant::now();
                let _ = infer_batch(&mut rec, &refs);
                let batch = t.elapsed();
                let t = Instant::now();
                let _ = infer_batch(&mut rec, &refs[..1]);
                eprintln!("  {name} batch {REC_BATCH}x{width}: {batch:?}; solo 1x{width}: {:?}", t.elapsed());
            }
        }
    }

    /// Opt-in perf probe: set CLOWD_OCR_BENCH_IMAGE to a file path and run
    /// with --release --nocapture. Times the real recognize() pipeline so
    /// regressions are diagnosed with numbers, not vibes (the dense-window
    /// cliff was found this way).
    #[test]
    fn env_bench_pipeline() {
        let Ok(path) = std::env::var("CLOWD_OCR_BENCH_IMAGE") else {
            eprintln!("SKIP {}: CLOWD_OCR_BENCH_IMAGE not set", module_path!());
            return;
        };
        let (bgra, w, h) = load_bgra(&path);
        let req = request(bgra, w, h);
        // Warm (engine init paid) then timed.
        let _ = recognize(&req).expect("warmup");
        let t = Instant::now();
        let outcome = recognize(&req).expect("bench");
        eprintln!(
            "recognize(): {:?} ({} lines) — tier choice is in the log above",
            t.elapsed(),
            outcome.lines.len()
        );
        for line in outcome.lines.iter().take(4) {
            eprintln!("  {}", line.text);
        }
    }

    /// Opt-in end-to-end check against a real screenshot: set
    /// CLOWD_OCR_TEST_IMAGE to a file path and CLOWD_OCR_TEST_EXPECT to a
    /// substring the recognized text must contain.
    #[test]
    fn env_image_contains_expected_text() {
        let (Ok(path), Ok(expect)) = (std::env::var("CLOWD_OCR_TEST_IMAGE"), std::env::var("CLOWD_OCR_TEST_EXPECT")) else {
            eprintln!("SKIP {}: CLOWD_OCR_TEST_IMAGE/CLOWD_OCR_TEST_EXPECT not set", module_path!());
            return;
        };
        let (bgra, w, h) = load_bgra(&path);
        let outcome = recognize(&request(bgra, w, h)).expect("test image must recognize");
        // Geometry log (visible with --nocapture): the rect heights are how
        // bubble font sizing is tuned against known-size source text.
        for line in &outcome.lines {
            eprintln!(
                "  rect x={:.1} y={:.1} w={:.1} h={:.1} :: {}",
                line.rect.left(),
                line.rect.top(),
                line.rect.width(),
                line.rect.height(),
                line.text
            );
        }
        assert!(
            outcome.full_text.contains(&expect),
            "expected {:?} within recognized text:\n{}",
            expect,
            outcome.full_text
        );
    }
}
