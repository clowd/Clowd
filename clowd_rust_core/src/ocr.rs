//! The text-recognition contract between the capture overlay and the
//! `clowd_ocr` binary that does the recognizing.
//!
//! Recognition runs out-of-process for one reason: the engine underneath it
//! is a static C++ library (MNN, via `ocr-rs`). A Rust panic in it would
//! unwind harmlessly, but an `abort`, a segfault, or an allocation failure on
//! a degenerate selection takes down whatever process it is running in — and
//! in-process that is the overlay, mid-capture, with the user's selection
//! already framed. Out-of-process the same failure is an exit code the
//! capturer reports as [`OcrError::Failed`].
//!
//! # Wire format
//!
//! The capturer spawns `clowd_ocr --out <path>` per request and speaks to it
//! exactly once:
//!
//! * **stdin** — one [`RequestHeader`] as a single JSON line, then the raw
//!   BGRA pixels (`width * height * 4` bytes, tightly packed) through to
//!   EOF. Pixels never touch the disk: they are a picture of the user's
//!   screen, and a temp file holding one outlives a killed process.
//! * **`--out`** — one [`OcrResponse`] as JSON, written only after
//!   recognition finishes. Exit code 0 means the file is there and can be
//!   trusted; any other exit means the child died or could not write, and
//!   the capturer reports [`OcrError::Failed`] without reading it.
//! * **stdout** — nothing. MNN prints device capabilities to stdout on
//!   session creation, so stdout is unusable as a protocol channel here;
//!   that is why the response is a file rather than the NDJSON line the
//!   overlay's own host protocol uses. The capturer spawns the child with
//!   stdout redirected to null for the same reason — its *own* stdout is the
//!   host protocol channel `Clowd.Ui` parses (see
//!   `clowd_capture/src/host/protocol.rs`), and inheriting it would let MNN's
//!   chatter corrupt that.
//!
//! The two binaries always ship from the same build, so this format carries
//! no version negotiation.

use serde::{Deserialize, Serialize};

use crate::geometry::{RectExt, ScreenRect, ScreenRectF};

/// Wire shape of a rect: `{"x":..,"y":..,"width":..,"height":..}`, the same
/// explicit form `session::RectJson` uses.
///
/// euclid's own serde impls are deliberately not enabled for this. They write
/// a `Point2D` as a bare two-element tuple, so a rect becomes
/// `{"origin":[0,0],"size":[3440,1440]}` — opaque in a response file someone
/// is reading to diagnose a bad recognition, and it would couple this
/// contract to euclid's internal representation across a version bump.
#[derive(Serialize, Deserialize)]
struct RectRepr<T> {
    x: T,
    y: T,
    width: T,
    height: T,
}

/// `#[serde(with = ...)]` bridges for the two rect flavours the contract
/// carries. The domain types keep their `ScreenRect`/`ScreenRectF` fields, so
/// nothing downstream of recognition has to know the wire shape exists.
macro_rules! rect_serde {
    ($module:ident, $rect:ty, $scalar:ty) => {
        mod $module {
            use super::*;

            pub fn serialize<S: serde::Serializer>(r: &$rect, s: S) -> Result<S::Ok, S::Error> {
                RectRepr::<$scalar> {
                    x: r.min_x(),
                    y: r.min_y(),
                    width: r.width(),
                    height: r.height(),
                }
                .serialize(s)
            }

            pub fn deserialize<'de, D: serde::Deserializer<'de>>(d: D) -> Result<$rect, D::Error> {
                let r = RectRepr::<$scalar>::deserialize(d)?;
                Ok(<$rect>::from_xy_size(r.x, r.y, r.width, r.height))
            }
        }
    };
}

rect_serde!(rect_f32, ScreenRectF, f32);
rect_serde!(rect_i32, ScreenRect, i32);

/// Response file name used when the request's output directory is a capture
/// session directory. Sits beside `capture.log` and `scroll.log` as one more
/// per-capture artefact.
pub const RESULT_FILE_NAME: &str = "ocr.json";

/// One recognized line of text.
///
/// Everything here is `Clone` because the result travels through the
/// capturer's `sync::Latch<T: Clone>` from its OCR worker thread to the app
/// thread.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OcrLine {
    pub text: String,
    /// The line's approximate glyph-ink rect, in virtual-desktop screen
    /// coordinates — the bubble renderer sizes and places its pill from
    /// this, so it must track the SOURCE text's visual extent, not the
    /// detector's padded box (see `UNCLIP_TIGHTEN` in `clowd_ocr`). Already
    /// offset by the crop origin the extractor actually used.
    #[serde(with = "rect_f32")]
    pub rect: ScreenRectF,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OcrOutcome {
    pub lines: Vec<OcrLine>,
    /// Newline-joined line texts — what COPY/SEARCH/UPLOAD act on. Derivable
    /// from `lines`, and carried anyway so the join happens once, on the side
    /// that already has the strings in reading order.
    pub full_text: String,
    /// Detected skew in degrees, 0.0 when none reported. Logged when a result
    /// is accepted and otherwise unused: the lift pass draws axis-aligned
    /// quads, so the angle informs diagnosis of a bad-looking lift but never
    /// the geometry. A de-skewed lift would be its first real consumer.
    /// (PaddleOCR reports no skew estimate, so this is currently always 0.0.)
    pub text_angle: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub enum OcrError {
    /// The engine failed to initialize (cause logged at error level by the
    /// child). Reported for the lifetime of that child; since one child
    /// serves one request, a later request tries again from scratch.
    Unavailable,
    Failed(String),
}

/// One BGRA8 image plus where it lives on the virtual desktop.
/// `bgra` is tightly packed at `width * 4` bytes per row.
///
/// Not serialized as a whole: the pixels ride stdin raw and the rest travels
/// as a [`RequestHeader`], because a 3440x1440 selection is 19.8 MB and
/// base64 inside JSON would be a pointless copy of it.
pub struct OcrRequest {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
    /// Screen rect the crop ACTUALLY covers (the capturer's
    /// `extract_selection_bgra` clamps to the desktop bitmap). Result rects
    /// are offset by this, so it is the one value that makes them land in
    /// the right place on negative-origin multi-monitor layouts.
    pub origin: ScreenRect,
}

/// The JSON line that precedes the pixels on stdin — everything about the
/// request except the pixels themselves.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RequestHeader {
    pub width: u32,
    pub height: u32,
    #[serde(with = "rect_i32")]
    pub origin: ScreenRect,
}

impl RequestHeader {
    /// Byte count the pixel payload following this header must have.
    pub fn payload_len(&self) -> usize {
        self.width as usize * self.height as usize * 4
    }
}

/// Contents of the `--out` file. `Err` is a recognition that ran and failed,
/// which is distinct from — and reported more precisely than — a child that
/// died before writing anything.
pub type OcrResponse = Result<OcrOutcome, OcrError>;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::geometry::RectExt;

    /// The header round-trips, and its declared payload length is what a
    /// tightly packed BGRA buffer of those dimensions actually measures —
    /// the child rejects a short read against this.
    #[test]
    fn header_round_trips_with_payload_len() {
        // Negative origin: the multi-monitor layout that makes the offset
        // matter at all.
        let header = RequestHeader {
            width: 320,
            height: 240,
            origin: ScreenRect::from_xy_size(-500, -300, 320, 240),
        };
        let json = serde_json::to_string(&header).expect("header serializes");
        // Pinned shape, for the same reason session::RectJson pins its own:
        // this is a file a human reads when a recognition looks wrong.
        assert_eq!(
            json,
            r#"{"width":320,"height":240,"origin":{"x":-500,"y":-300,"width":320,"height":240}}"#
        );
        // One line, so the child can read it with read_line before switching
        // to a raw byte read.
        assert!(!json.contains('\n'));
        let back: RequestHeader = serde_json::from_str(&json).expect("header deserializes");
        assert_eq!(back.width, 320);
        assert_eq!(back.height, 240);
        assert_eq!(back.origin, header.origin);
        assert_eq!(back.payload_len(), 320 * 240 * 4);
    }

    /// Both response arms round-trip, including the line rects — a rect that
    /// serialized lossily would misplace every bubble on screen.
    #[test]
    fn response_round_trips_both_arms() {
        let outcome = OcrOutcome {
            lines: vec![OcrLine {
                text: "hello".into(),
                rect: ScreenRectF::from_exact(-12.5, 7.25, 100.0, 21.5),
            }],
            full_text: "hello".into(),
            text_angle: 0.0,
        };
        let json = serde_json::to_string::<OcrResponse>(&Ok(outcome)).expect("outcome serializes");
        assert_eq!(
            json,
            r#"{"Ok":{"lines":[{"text":"hello","rect":{"x":-12.5,"y":7.25,"width":112.5,"height":14.25}}],"full_text":"hello","text_angle":0.0}}"#
        );
        let back: OcrResponse = serde_json::from_str(&json).expect("outcome deserializes");
        let back = back.expect("round-tripped as Ok");
        assert_eq!(back.lines.len(), 1);
        assert_eq!(back.lines[0].text, "hello");
        assert_eq!(back.lines[0].rect, ScreenRectF::from_exact(-12.5, 7.25, 100.0, 21.5));
        assert_eq!(back.full_text, "hello");

        for err in [OcrError::Unavailable, OcrError::Failed("det exploded".into())] {
            let json = serde_json::to_string::<OcrResponse>(&Err(err.clone())).expect("error serializes");
            let back: OcrResponse = serde_json::from_str(&json).expect("error deserializes");
            match (back.expect_err("round-tripped as Err"), err) {
                (OcrError::Unavailable, OcrError::Unavailable) => {}
                (OcrError::Failed(a), OcrError::Failed(b)) => assert_eq!(a, b),
                (a, b) => panic!("arm changed: {a:?} vs {b:?}"),
            }
        }
    }
}
