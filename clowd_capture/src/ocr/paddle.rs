//! PaddleOCR (PP-OCRv6) backend via the `ocr-rs` crate / MNN runtime.
//! Fully self-contained and platform-independent: models are embedded, MNN
//! runs on the CPU, and recognition covers Chinese/Japanese/Latin scripts
//! without any OS engine or language pack.
//!
//! The pipeline is run manually (det → sort/cap → per-region rec) rather
//! than through `OcrEngine::recognize`, for three reasons the engine's
//! all-in-one path cannot deliver:
//!
//! * **The dense-window cliff.** Recognition cost is ~3.7 ms + ~22 ms per
//!   1000 px of tensor width PER LINE (tensor width = aspect × 48), fully
//!   serialized behind a global mutex in the MNN wrapper and immune to
//!   thread count (measured: 4→24 threads moved a 176-line image from
//!   5.45 s to 5.20 s). A text-dense terminal window therefore took tens
//!   of seconds and read as hung. Running det ourselves lets us PREDICT
//!   that cost from the box geometry and drop to the tiny recognition
//!   model when it blows the budget — measured 3.5× faster (4.8 s → 1.4 s
//!   on the 176-line bench image) at near-identical text.
//! * **The line cap belongs BEFORE recognition.** The engine recognizes
//!   everything and lets us truncate after; a degenerate selection (a
//!   whole spreadsheet) pays for lines that are then thrown away.
//! * Reading-order sorting before the cap, so truncation keeps the top of
//!   the page rather than raw detection order.

use std::sync::{Mutex, OnceLock};

use image::DynamicImage;
use ocr_rs::{DetModel, DetOptions, RecModel};

use super::{OcrError, OcrLine, OcrOutcome, OcrRequest};
use clowd_rust_core::geometry::{RectExt, ScreenRectF};

/// The lift render pass hard-caps its instance buffer at 512 lines; past
/// 256 a selection is degenerate (a whole spreadsheet) where extra lines
/// only add latency. Applied BEFORE recognition (see module docs).
const MAX_LINES: usize = 256;

/// Detection-stage resolution ceiling (the image is downscaled to this long
/// side before the DB detector runs; recognition always crops from the
/// full-resolution input). The crate default of 960 loses small screen text
/// on large selections — 1920 keeps ~12px UI text detectable on a 4K-wide
/// crop. This is THE accuracy-vs-latency knob to revisit if either suffers.
const DET_MAX_SIDE_LEN: u32 = 1920;

/// Every detection box is inflated by this many pixels per side before the
/// recognition crop — the context measurably helps the recognizer (same
/// role as `DetOptions::box_border` in the engine's own pipeline).
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

/// Recognition results below this confidence are dropped. 0.5 (the
/// engine's default) drops too much real UI text; screen captures are
/// clean enough that low-confidence reads are usually right.
const MIN_CONFIDENCE: f32 = 0.35;

/// The recognition model's input height; crops are resized to this,
/// preserving aspect, so a crop's tensor width is aspect × 48. Fixed by
/// the PP-OCRv6 architecture — not tunable.
const REC_TARGET_HEIGHT: f32 = 48.0;

/// Measured per-call cost model of the SMALL recognition model on the dev
/// box (release build): a fixed ~3.7 ms per call plus ~22 ms per 1000 px
/// of tensor width. Used only to CHOOSE a tier, so being off by 2x on
/// other hardware moves the tier threshold, not correctness.
const SMALL_REC_FIXED_MS: f32 = 3.7;
const SMALL_REC_MS_PER_KPX: f32 = 22.0;

/// Predicted small-model recognition time above which the tiny model takes
/// over. At the boundary the small tier finishes in roughly this time;
/// past it the tiny tier runs ~3.5-4x faster than small would have. Dense
/// terminal windows land squarely in the tiny tier (a 176-line bench image
/// predicted ~4.5 s small, ran 1.1 s tiny).
const SMALL_TIER_BUDGET_MS: f32 = 1500.0;

/// Boxes with a width/height ratio beyond this are detector junk (a 1900px
/// wide, 4px tall sliver would alone cost a ~23000px-wide tensor) — skip
/// them rather than pay for garbage.
const MAX_BOX_ASPECT: f32 = 300.0;

// PP-OCRv6, converted to MNN, vendored from
// github.com/zibo-chen/rust-paddle-ocr (models/). ~18 MB embedded — the
// deliberate cost of a zero-install, no-language-pack OCR. The _medium_ set
// (~70 MB, same repo, same charset coverage) measured noticeably beyond the
// size budget for a marginal accuracy gain on screen text; it remains the
// fallback if small's accuracy disappoints in real use.
//
// Two recognition tiers share the one detector: `small` is the default;
// `tiny` (+2.3 MB) exists solely for text-dense selections where small's
// serial per-line cost would run to tens of seconds (see module docs).
// NOTE the tiny charset is a subset (~6.8k glyphs vs small's ~15k — less
// CJK coverage, per upstream no Japanese): on a dense page a rare glyph
// may come back wrong that the small tier would have read. Speed over
// completeness there is deliberate — the alternative was "seemingly hung".
static DET_MODEL: &[u8] = include_bytes!("../../assets/ocr/PP-OCRv6_small_det.mnn");
static REC_MODEL: &[u8] = include_bytes!("../../assets/ocr/PP-OCRv6_small_rec.mnn");
static CHARSET: &[u8] = include_bytes!("../../assets/ocr/ppocr_keys_v6_small.txt");
static TINY_REC_MODEL: &[u8] = include_bytes!("../../assets/ocr/PP-OCRv6_tiny_rec.mnn");
static TINY_CHARSET: &[u8] = include_bytes!("../../assets/ocr/ppocr_keys_v6_tiny.txt");

struct Backend {
    det: DetModel,
    rec_small: RecModel,
    rec_tiny: RecModel,
}

/// Process-global models, created on first use: MNN session setup parses
/// the models and is far too slow to repeat per capture. `Option` caches a
/// failed init too — an MNN that cannot start once will not start later,
/// so every recognize then reports `Unavailable` without retrying.
///
/// `Mutex` (not bare `Backend`): inference takes `&self` but MNN sessions
/// are not documented thread-safe, and the OCR worker threads are spawned
/// per capture cycle — serialize them defensively. Recognitions never
/// overlap in practice (one selection at a time), so the lock is
/// uncontended.
fn backend() -> Option<&'static Mutex<Backend>> {
    static BACKEND: OnceLock<Option<Mutex<Backend>>> = OnceLock::new();
    BACKEND
        .get_or_init(|| match create_backend() {
            Ok(b) => Some(Mutex::new(b)),
            Err(e) => {
                log::error!("PaddleOCR engine init failed: {e}");
                None
            }
        })
        .as_ref()
}

fn create_backend() -> Result<Backend, ocr_rs::OcrError> {
    let det = DetModel::from_bytes(DET_MODEL, None)?.with_options(
        DetOptions::new()
            .with_max_side_len(DET_MAX_SIDE_LEN)
            .with_box_border(BOX_BORDER),
    );
    let rec_small = RecModel::from_bytes_with_charset(REC_MODEL, CHARSET, None)?;
    let rec_tiny = RecModel::from_bytes_with_charset(TINY_REC_MODEL, TINY_CHARSET, None)?;
    Ok(Backend {
        det,
        rec_small,
        rec_tiny,
    })
}

/// Predicted milliseconds for the SMALL model to recognize boxes of the
/// given (width, height) dimensions — the tier-choice input. Pure so the
/// threshold behaviour is testable.
fn predict_small_rec_ms(dims: impl Iterator<Item = (u32, u32)>) -> f32 {
    dims.map(|(w, h)| {
        let aspect = w as f32 / (h as f32).max(1.0);
        SMALL_REC_FIXED_MS + (aspect * REC_TARGET_HEIGHT / 1000.0) * SMALL_REC_MS_PER_KPX
    })
    .sum()
}

pub fn recognize(req: &OcrRequest) -> Result<OcrOutcome, OcrError> {
    let Some(backend) = backend() else {
        return Err(OcrError::Unavailable);
    };

    // BGRA -> RGB up front (alpha dropped: BitBlt'd desktop pixels routinely
    // carry a == 0, and the crate's preprocess calls to_rgb8() on whatever it
    // receives — handing it RGB directly makes that a no-op instead of a
    // second full-image conversion).
    let mut rgb = Vec::with_capacity(req.width as usize * req.height as usize * 3);
    for px in req.bgra.chunks_exact(4) {
        rgb.extend_from_slice(&[px[2], px[1], px[0]]);
    }
    let rgb_img = image::RgbImage::from_raw(req.width, req.height, rgb)
        .expect("OcrRequest.bgra must be width * height * 4 bytes");
    let img = DynamicImage::ImageRgb8(rgb_img);

    let b = backend.lock().expect("OCR backend lock poisoned");

    let t_det = std::time::Instant::now();
    let mut boxes = b
        .det
        .detect(&img)
        .map_err(|e| OcrError::Failed(e.to_string()))?;
    let det_elapsed = t_det.elapsed();

    // Reading order BEFORE the cap, so truncation keeps the top of the
    // page. Boxes on one visual row can differ by a few pixels of top;
    // bucket by half-line-height so columns on the same row sort
    // left-to-right.
    boxes.sort_by(|a, b| {
        let (ar, br) = (&a.rect, &b.rect);
        let tolerance = (ar.height().min(br.height()) / 2) as i32;
        if (ar.top() - br.top()).abs() <= tolerance {
            ar.left().cmp(&br.left())
        } else {
            ar.top().cmp(&br.top())
        }
    });
    boxes.retain(|bx| {
        let aspect = bx.rect.width() as f32 / (bx.rect.height() as f32).max(1.0);
        aspect <= MAX_BOX_ASPECT
    });
    let detected = boxes.len();
    if detected > MAX_LINES {
        log::warn!("OCR truncated to {MAX_LINES} lines before recognition (detector found {detected})");
        boxes.truncate(MAX_LINES);
    }

    // Tier choice — see the cost model constants. Logged with the numbers
    // so a slow-feeling capture can be diagnosed from the log alone.
    let predicted_ms = predict_small_rec_ms(boxes.iter().map(|b| (b.rect.width(), b.rect.height())));
    let use_tiny = predicted_ms > SMALL_TIER_BUDGET_MS;
    let rec = if use_tiny { &b.rec_tiny } else { &b.rec_small };
    log::info!(
        "OCR det {:?} ({} boxes), predicted small-rec {:.0} ms -> {} tier",
        det_elapsed,
        boxes.len(),
        predicted_ms,
        if use_tiny { "tiny" } else { "small" },
    );

    let t_rec = std::time::Instant::now();
    let ox = req.origin.left() as f32;
    let oy = req.origin.top() as f32;
    let mut lines: Vec<OcrLine> = Vec::with_capacity(boxes.len());
    for text_box in &boxes {
        // Same crop the engine's own pipeline would take: the detector box
        // expanded by the context border, clamped to the image.
        let expanded = text_box.expand(BOX_BORDER, req.width, req.height);
        let r = &expanded.rect;
        let crop = img.crop_imm(r.left() as u32, r.top() as u32, r.width(), r.height());
        let result = match rec.recognize(&crop) {
            Ok(r) => r,
            Err(e) => {
                // One unreadable region must not kill the whole page.
                log::warn!("OCR region failed: {e}");
                continue;
            }
        };
        if result.text.trim().is_empty() || result.confidence < MIN_CONFIDENCE {
            continue;
        }

        // Deflate the crop border back out, but never below 1px. (At image
        // edges expand() clamped instead of inflating, so this over-tightens
        // by up to BOX_BORDER px there — invisible in practice and not
        // worth tracking the clamp per side.)
        let bpx = BOX_BORDER as f32;
        let (mut left, mut top) = (r.left() as f32 + bpx, r.top() as f32 + bpx);
        let (mut right, mut bottom) = (
            r.left() as f32 + r.width() as f32 - bpx,
            r.top() as f32 + r.height() as f32 - bpx,
        );
        if right - left < 1.0 {
            left = r.left() as f32;
            right = left + r.width() as f32;
        }
        if bottom - top < 1.0 {
            top = r.top() as f32;
            bottom = top + r.height() as f32;
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
            text: result.text,
            // Boxes are in input-image coordinates (detection's internal
            // downscale is already mapped back by the crate), so only the
            // crop-origin offset applies here.
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
                state = state.wrapping_mul(1664525).wrapping_add(1013904223);
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

    /// Two back-to-back calls through the cached models: pins the OnceLock
    /// reuse and that an inference leaves the sessions reusable.
    #[test]
    fn consecutive_recognize_calls_succeed() {
        let req = request(vec![255u8; 64 * 64 * 4], 64, 64);
        recognize(&req).expect("first call");
        recognize(&req).expect("second call");
    }

    /// The tier predictor: cheap boxes stay under the budget, a dense page
    /// of wide lines goes over. Pins the cost model's shape (fixed +
    /// width-proportional), not its absolute calibration.
    #[test]
    fn tier_prediction_scales_with_count_and_width() {
        // A handful of short lines: small tier.
        assert!(predict_small_rec_ms(std::iter::repeat_n((400u32, 20u32), 8)) < SMALL_TIER_BUDGET_MS);

        // A dense terminal: 176 full-width lines of small text blows the
        // budget (this is the measured 4.8s case).
        assert!(predict_small_rec_ms(std::iter::repeat_n((1600u32, 16u32), 176)) > SMALL_TIER_BUDGET_MS);

        // Degenerate zero-height boxes must not divide by zero.
        assert!(predict_small_rec_ms(std::iter::once((100u32, 0u32))).is_finite());
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
        let img = image::open(&path).expect("CLOWD_OCR_BENCH_IMAGE must decode").to_rgba8();
        let (w, h) = img.dimensions();
        let mut bgra = img.into_raw();
        for px in bgra.chunks_exact_mut(4) {
            px.swap(0, 2);
        }
        let req = request(bgra, w, h);
        // Warm (engine init paid) then timed.
        let _ = recognize(&req).expect("warmup");
        let t = std::time::Instant::now();
        let outcome = recognize(&req).expect("bench");
        eprintln!("recognize(): {:?} ({} lines) — tier choice is in the log above", t.elapsed(), outcome.lines.len());
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
        let img = image::open(&path)
            .expect("CLOWD_OCR_TEST_IMAGE must decode")
            .to_rgba8();
        let (w, h) = img.dimensions();
        // The request wants BGRA; image gives RGBA — swap R and B.
        let mut bgra = img.into_raw();
        for px in bgra.chunks_exact_mut(4) {
            px.swap(0, 2);
        }
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
