//! PaddleOCR (PP-OCRv6) backend via the `ocr-rs` crate / MNN runtime.
//! Fully self-contained and platform-independent: models are embedded, MNN
//! runs on the CPU, and recognition covers Chinese/Japanese/Latin scripts
//! without any OS engine or language pack.
//!
//! The pipeline is run manually (det → sort/cap → batched rec) rather
//! than through `OcrEngine::recognize`, for three reasons the engine's
//! all-in-one path cannot deliver:
//!
//! * **The dense-window cliff.** Recognition cost was measured at ~3.7 ms
//!   plus ~22 ms per 1000 px of tensor width PER serial CALL (tensor width
//!   = aspect × 48) and immune to thread count (4→24 threads moved a
//!   176-line image from 5.45 s to 5.20 s — per-line tensors are too
//!   small for threads to help). A text-dense terminal window therefore
//!   took tens of seconds and read as hung. Running det ourselves lets us
//!   PREDICT that cost from the box geometry and drop to the tiny
//!   recognition model when it blows the budget — measured 3.5× faster
//!   (4.8 s → 1.4 s on the 176-line bench image) at near-identical text.
//!   Recognition additionally runs in width-sorted BATCHES (see
//!   [`REC_BATCH`]), which amortizes the fixed per-call cost and hands
//!   MNN tensors big enough for its threads to matter; the serial-call
//!   cost model above is therefore conservative for the tier choice
//!   until re-measured.
//! * **The line cap belongs BEFORE recognition.** The engine recognizes
//!   everything and lets us truncate after; a degenerate selection (a
//!   whole spreadsheet) pays for lines that are then thrown away.
//! * Reading-order sorting before the cap, so truncation keeps the top of
//!   the page rather than raw detection order.
//!
//! Nothing here polls for cancellation. This is a one-shot child process
//! (see `main`), so a request the user backed out of is cancelled by the
//! capturer killing us — which is both instant and, unlike the in-band
//! polling this replaced, cannot leave a superseded job holding the engine
//! while the next request queues behind it.

use std::sync::{Mutex, OnceLock};

use image::DynamicImage;
use ocr_rs::{DetModel, DetOptions, RecModel, RecognitionResult, TextBox};

use clowd_rust_core::geometry::{RectExt, ScreenRectF};
use clowd_rust_core::ocr::{OcrError, OcrLine, OcrOutcome, OcrRequest};

/// Line cap, applied BEFORE recognition (see module docs) purely to bound
/// worst-case latency — the render side (bubble rects, glyphon buffers)
/// grows dynamically and needs no cap. 512 because real pages get there:
/// a full-screen 3440x1440 page of dense book text measured 361 genuine
/// lines, and the previous cap of 256 silently dropped the bottom third.
/// At 512 the worst-case tiny-tier recognition stays around ~3 s.
const MAX_LINES: usize = 512;

/// Detection-stage resolution ceiling (the image is downscaled to this long
/// side before the DB detector runs; recognition always crops from the
/// full-resolution input). MEASURED CLIFF — do not lower casually: on a
/// 3440x1440 dense-text page, det at native res found 361 clean line boxes
/// (627 ms); at 1920 the same page shattered into 522 fragments (208 ms)
/// whose crops recognized as garbage, and at 960 it found NOTHING. The DB
/// detector simply cannot see ~7px downscaled text, so the ceiling exists
/// only to bound det latency on inputs beyond any single monitor (det cost
/// scales with area; a 4096-long-side page runs det in well under a
/// second, hidden beneath the scanning sweep). Selections spanning
/// multiple 4K monitors will start to degrade past this — revisit with
/// tiled detection if that ever becomes a real complaint.
const DET_MAX_SIDE_LEN: u32 = 4096;

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

/// Predicted-serial small-model time above which the tiny model takes
/// over. The prediction is in SERIAL units while recognition actually
/// runs batched (measured 8454 ms predicted → 5350 ms batched on the
/// 361-line reference page, a ~0.63 correction), so 2400 here targets a
/// real batched small-tier ceiling of ~1.5 s — the same wall-clock cutoff
/// the tier was designed around before batching.
///
/// Past the cutoff, tiny is not merely the fast fallback: on the dense
/// reference page it was BOTH 3.7× faster than small (1.44 s vs 5.35 s)
/// and clearly better on the ultra-wide small-text lines such pages are
/// made of (coherent text at conf ~0.78 where small emitted fragments at
/// ~0.53 — verified identical solo vs batched, so it is the model, not
/// the batching).
const SMALL_TIER_BUDGET_MS: f32 = 2400.0;

/// Boxes with a width/height ratio beyond this are detector junk (a 1900px
/// wide, 4px tall sliver would alone cost a ~23000px-wide tensor) — skip
/// them rather than pay for garbage.
const MAX_BOX_ASPECT: f32 = 300.0;

/// Crops recognized per batched MNN call (matches ocr-rs's own default
/// chunk size). The batch tensor is padded to the chunk's WIDEST sample,
/// so crops are width-sorted before chunking — a chunk of near-equal
/// widths pays almost nothing for padding, while reading-order chunks
/// would routinely pair one full-width line with seven short ones and pay
/// 8× the widest. Results are indexed back to reading order afterwards.
const REC_BATCH: usize = 8;

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
static DET_MODEL: &[u8] = include_bytes!("../assets/ocr/PP-OCRv6_small_det.mnn");
static REC_MODEL: &[u8] = include_bytes!("../assets/ocr/PP-OCRv6_small_rec.mnn");
static CHARSET: &[u8] = include_bytes!("../assets/ocr/ppocr_keys_v6_small.txt");
static TINY_REC_MODEL: &[u8] = include_bytes!("../assets/ocr/PP-OCRv6_tiny_rec.mnn");
static TINY_CHARSET: &[u8] = include_bytes!("../assets/ocr/ppocr_keys_v6_tiny.txt");

/// The detector, created on first use. `Option` caches a failed init too — an
/// MNN that cannot start once will not start later, so a second recognize in
/// the same process reports `Unavailable` without retrying.
///
/// `Mutex` (not a bare `DetModel`) because a `static` must be `Sync` and MNN
/// sessions are not: inference takes `&self`, but nothing documents them as
/// thread-safe, so the lock is what makes this static legal. It is not
/// concurrency control — a one-shot child recognizes exactly once, so the
/// lock is uncontended by construction.
fn detector() -> Option<&'static Mutex<DetModel>> {
    static DET: OnceLock<Option<Mutex<DetModel>>> = OnceLock::new();
    DET.get_or_init(|| {
        let t = std::time::Instant::now();
        match DetModel::from_bytes(DET_MODEL, None) {
            Ok(det) => {
                log::info!("OCR detector init {:?}", t.elapsed());
                Some(Mutex::new(
                    det.with_options(
                        DetOptions::new()
                            .with_max_side_len(DET_MAX_SIDE_LEN)
                            .with_box_border(BOX_BORDER),
                    ),
                ))
            }
            Err(e) => {
                log::error!("PaddleOCR detector init failed: {e}");
                None
            }
        }
    })
    .as_ref()
}

/// Build the ONE recognition model the tier choice landed on.
///
/// Deliberately after detection and deliberately not cached: the two tiers are
/// 10.6 MB and 2.3 MB of embedded weights, and parsing them both cost ~90 ms
/// of pure latency on every request once recognition moved out-of-process —
/// in-process that was a one-off per capturer, here it would be a tax on every
/// OCR press. The tier is not known until det has produced its boxes, so this
/// cannot be hoisted; it can only be made to load one model instead of two.
fn load_rec(use_tiny: bool) -> Option<RecModel> {
    let (model, charset, name) = if use_tiny {
        (TINY_REC_MODEL, TINY_CHARSET, "tiny")
    } else {
        (REC_MODEL, CHARSET, "small")
    };
    let t = std::time::Instant::now();
    match RecModel::from_bytes_with_charset(model, charset, None) {
        Ok(rec) => {
            log::info!("OCR {name} recognizer init {:?}", t.elapsed());
            Some(rec)
        }
        Err(e) => {
            log::error!("PaddleOCR {name} recognizer init failed: {e}");
            None
        }
    }
}

/// Reading-order permutation of detection boxes given as (top, left,
/// height) keys: rows are clustered top-down with a half-line tolerance,
/// then each row runs left-to-right.
///
/// Two passes (cluster, then sort by the fixed row key) rather than one
/// comparator with the tolerance inline: a per-pair tolerance is not
/// transitive — three boxes each within tolerance of their neighbour but
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
/// threshold behaviour is testable. Measured on serial per-call
/// recognition; batching (see [`REC_BATCH`]) only makes it an
/// overestimate, which errs toward the fast tier.
fn predict_small_rec_ms(dims: impl Iterator<Item = (u32, u32)>) -> f32 {
    dims.map(|(w, h)| {
        let aspect = w as f32 / (h as f32).max(1.0);
        SMALL_REC_FIXED_MS + (aspect * REC_TARGET_HEIGHT / 1000.0) * SMALL_REC_MS_PER_KPX
    })
    .sum()
}

/// Recognize every line of text in `req`. Blocking, and the only thing this
/// process does; a request the user abandons is cancelled by the capturer
/// killing us mid-call (see the module docs).
pub fn recognize(req: &OcrRequest) -> Result<OcrOutcome, OcrError> {
    let Some(det) = detector() else {
        return Err(OcrError::Unavailable);
    };

    // BGRA -> RGB up front (alpha dropped: BitBlt'd desktop pixels routinely
    // carry a == 0, and the crate's preprocess calls to_rgb8() on whatever it
    // receives — handing it RGB directly makes that a no-op instead of a
    // second full-image conversion). Indexed writes into a pre-sized
    // buffer, not per-pixel pushes: the push's length bookkeeping defeats
    // vectorization, and a 4K-area selection is ~8M pixels on the
    // latency-critical path before det even starts.
    let mut rgb = vec![0u8; req.width as usize * req.height as usize * 3];
    for (dst, px) in rgb
        .chunks_exact_mut(3)
        .zip(req.bgra.chunks_exact(4))
    {
        dst[0] = px[2];
        dst[1] = px[1];
        dst[2] = px[0];
    }
    let rgb_img = image::RgbImage::from_raw(req.width, req.height, rgb).expect("OcrRequest.bgra must be width * height * 4 bytes");
    let img = DynamicImage::ImageRgb8(rgb_img);

    let t_det = std::time::Instant::now();
    let boxes = {
        let d = det
            .lock()
            .expect("OCR detector lock poisoned");
        d.detect(&img)
            .map_err(|e| OcrError::Failed(e.to_string()))?
        // lock released here: recognition uses its own model, so holding the
        // detector through it would pin memory nothing is reading.
    };
    let det_elapsed = t_det.elapsed();

    // Reading order BEFORE the cap, so truncation keeps the top of the
    // page. Row clustering + fixed-key sort — see `reading_order` for why
    // this is not one comparator.
    let keys: Vec<(i32, i32, u32)> = boxes
        .iter()
        .map(|b| (b.rect.top(), b.rect.left(), b.rect.height()))
        .collect();
    let order = reading_order(&keys);
    let mut boxes: Vec<TextBox> = {
        let mut src: Vec<Option<TextBox>> = boxes.into_iter().map(Some).collect();
        order
            .iter()
            .map(|&i| {
                src[i]
                    .take()
                    .expect("reading_order returns a permutation")
            })
            .collect()
    };
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
    let predicted_ms = predict_small_rec_ms(
        boxes
            .iter()
            .map(|b| (b.rect.width(), b.rect.height())),
    );
    let use_tiny = predicted_ms > SMALL_TIER_BUDGET_MS;
    log::info!(
        "OCR det {:?} ({} boxes), predicted small-rec {:.0} ms -> {} tier",
        det_elapsed,
        boxes.len(),
        predicted_ms,
        if use_tiny { "tiny" } else { "small" },
    );
    let Some(rec) = load_rec(use_tiny) else {
        return Err(OcrError::Unavailable);
    };
    let rec = &rec;

    let t_rec = std::time::Instant::now();

    // Same crops the engine's own pipeline would take: the detector box
    // expanded by the context border, clamped to the image. Kept in
    // reading order; `expanded` doubles as the geometry source below.
    let expanded: Vec<TextBox> = boxes
        .iter()
        .map(|tb| tb.expand(BOX_BORDER, req.width, req.height))
        .collect();
    let crops: Vec<DynamicImage> = expanded
        .iter()
        .map(|e| {
            let r = &e.rect;
            img.crop_imm(r.left() as u32, r.top() as u32, r.width(), r.height())
        })
        .collect();

    // Batched recognition, width-sorted (see REC_BATCH for why), results
    // indexed straight back to reading-order slots.
    let mut rec_order: Vec<usize> = (0..crops.len()).collect();
    rec_order.sort_by_key(|&i| {
        // Tensor width the batch pads to: aspect × the model input height.
        let (w, h) = (crops[i].width() as u64, crops[i].height().max(1) as u64);
        w * REC_TARGET_HEIGHT as u64 / h
    });
    let mut results: Vec<Option<RecognitionResult>> = std::iter::repeat_with(|| None)
        .take(crops.len())
        .collect();
    for chunk in rec_order.chunks(REC_BATCH) {
        let refs: Vec<&DynamicImage> = chunk.iter().map(|&i| &crops[i]).collect();
        match rec.recognize_batch_ref(&refs) {
            Ok(rs) => {
                for (&i, r) in chunk.iter().zip(rs) {
                    results[i] = Some(r);
                }
            }
            Err(e) => {
                // One unreadable region must not kill the whole page: a
                // failed batch falls back to its members individually so
                // only the truly bad crop is lost.
                log::warn!("OCR batch failed ({e}); retrying its regions individually");
                for &i in chunk {
                    match rec.recognize(&crops[i]) {
                        Ok(r) => results[i] = Some(r),
                        Err(e) => log::warn!("OCR region failed: {e}"),
                    }
                }
            }
        }
    }

    let ox = req.origin.left() as f32;
    let oy = req.origin.top() as f32;
    let mut lines: Vec<OcrLine> = Vec::with_capacity(boxes.len());
    for (expanded_box, result) in expanded.iter().zip(results) {
        let Some(result) = result else {
            continue;
        };
        let r = &expanded_box.rect;
        if result.text.trim().is_empty() || result.confidence < MIN_CONFIDENCE {
            continue;
        }

        // Deflate the crop border back out, but never below 1px. (At image
        // edges expand() clamped instead of inflating, so this over-tightens
        // by up to BOX_BORDER px there — invisible in practice and not
        // worth tracking the clamp per side.)
        let bpx = BOX_BORDER as f32;
        let (mut left, mut top) = (r.left() as f32 + bpx, r.top() as f32 + bpx);
        let (mut right, mut bottom) = (r.left() as f32 + r.width() as f32 - bpx, r.top() as f32 + r.height() as f32 - bpx);
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

    /// Two back-to-back calls through the cached models: pins the OnceLock
    /// reuse and that an inference leaves the sessions reusable.
    #[test]
    fn consecutive_recognize_calls_succeed() {
        let req = request(vec![255u8; 64 * 64 * 4], 64, 64);
        recognize(&req).expect("first call");
        recognize(&req).expect("second call");
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
    /// within tolerance of its neighbour but not of the chain's ends is
    /// INTRANSITIVE under a pairwise comparator (A<B and B<C by left, but
    /// A<C by top — a cycle std's sort may panic on). The clustered order
    /// must simply produce a valid permutation, deterministically.
    #[test]
    fn reading_order_survives_tolerance_chains() {
        // Tops 0, 8, 16 with height 20: each neighbour pair is "same row"
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
        // budget (this is the measured 4.8s case).
        assert!(predict_small_rec_ms(std::iter::repeat_n((1600u32, 16u32), 176)) > SMALL_TIER_BUDGET_MS);

        // Degenerate zero-height boxes must not divide by zero.
        assert!(predict_small_rec_ms(std::iter::once((100u32, 0u32))).is_finite());
    }

    /// Opt-in diagnostic for detection-resolution and tier-quality
    /// regressions (this probe found the DET_MAX_SIDE_LEN cliff: 0 boxes
    /// at 960, 522 fragments at 1920, 361 clean lines at native on the
    /// same 3440x1440 page). For each det ceiling it reports box count +
    /// det time, then compares small/tiny recognition on the native-res
    /// boxes — batched AND solo, so a batching-quality regression shows
    /// up as a batched/solo mismatch. Set CLOWD_OCR_BENCH_IMAGE and run
    /// with --release --nocapture.
    #[test]
    fn env_det_ceiling_probe() {
        let Ok(path) = std::env::var("CLOWD_OCR_BENCH_IMAGE") else {
            eprintln!("SKIP {}: CLOWD_OCR_BENCH_IMAGE not set", module_path!());
            return;
        };
        let img = image::open(&path).expect("image must decode");
        let img = DynamicImage::ImageRgb8(img.to_rgb8());
        let (w, h) = (img.width(), img.height());
        eprintln!("image {w}x{h}");

        let mut native_boxes = Vec::new();
        for ceiling in [960u32, 1920, 2560, w.max(h).max(2560)] {
            let det = DetModel::from_bytes(DET_MODEL, None)
                .expect("det model")
                .with_options(
                    DetOptions::new()
                        .with_max_side_len(ceiling)
                        .with_box_border(BOX_BORDER),
                );
            let t = std::time::Instant::now();
            let boxes = det.detect(&img).expect("detect");
            eprintln!("det ceiling {ceiling}: {} boxes in {:?}", boxes.len(), t.elapsed());
            native_boxes = boxes;
        }

        // Predicted (serial cost model) vs the batched reality below —
        // the correction factor for re-calibrating the tier budget.
        let predicted = predict_small_rec_ms(
            native_boxes
                .iter()
                .map(|b| (b.rect.width(), b.rect.height())),
        );
        eprintln!("predicted serial small-rec: {predicted:.0} ms");

        // Recognition quality on the native-res boxes, both tiers, batched
        // the way recognize() batches (width-sorted chunks of REC_BATCH).
        // Samples are the WIDEST crops (real text lines) at fixed indices
        // so the two tiers print the same lines for direct comparison.
        let crops: Vec<DynamicImage> = native_boxes
            .iter()
            .map(|b| {
                let e = b.expand(BOX_BORDER, w, h);
                let r = &e.rect;
                img.crop_imm(r.left() as u32, r.top() as u32, r.width(), r.height())
            })
            .collect();
        let mut order: Vec<usize> = (0..crops.len()).collect();
        order.sort_by_key(|&i| {
            let (cw, ch) = (crops[i].width() as u64, crops[i].height().max(1) as u64);
            cw * 48 / ch
        });
        let widest: Vec<usize> = order.iter().rev().take(5).copied().collect();
        for (name, model, charset) in [("small", REC_MODEL, CHARSET), ("tiny", TINY_REC_MODEL, TINY_CHARSET)] {
            let rec = RecModel::from_bytes_with_charset(model, charset, None).expect("rec model");
            let t = std::time::Instant::now();
            let mut results: Vec<Option<RecognitionResult>> = std::iter::repeat_with(|| None)
                .take(crops.len())
                .collect();
            for chunk in order.chunks(REC_BATCH) {
                let refs: Vec<&DynamicImage> = chunk.iter().map(|&i| &crops[i]).collect();
                if let Ok(rs) = rec.recognize_batch_ref(&refs) {
                    for (&i, r) in chunk.iter().zip(rs) {
                        results[i] = Some(r);
                    }
                }
            }
            let elapsed = t.elapsed();
            let kept: Vec<&RecognitionResult> = results
                .iter()
                .flatten()
                .filter(|r| !r.text.trim().is_empty() && r.confidence >= MIN_CONFIDENCE)
                .collect();
            let mean_conf = kept
                .iter()
                .map(|r| r.confidence)
                .sum::<f32>()
                / kept.len().max(1) as f32;
            eprintln!(
                "rec {name}: {}/{} lines in {:?}, mean conf {:.3}",
                kept.len(),
                crops.len(),
                elapsed,
                mean_conf
            );
            for &i in &widest {
                if let Some(r) = &results[i] {
                    eprintln!("  [{i}] {:.2} {}", r.confidence, r.text);
                }
            }
            // The batching suspect: the same widest crops recognized
            // INDIVIDUALLY — if these come back better than the batched
            // rows above, batch padding is degrading recognition.
            for &i in &widest {
                if let Ok(r) = rec.recognize(&crops[i]) {
                    eprintln!("  solo[{i}] {:.2} {}", r.confidence, r.text);
                }
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
        let img = image::open(&path)
            .expect("CLOWD_OCR_BENCH_IMAGE must decode")
            .to_rgba8();
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
