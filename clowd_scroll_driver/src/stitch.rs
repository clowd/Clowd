//! Frame registration and the composite image.
//!
//! The problem is deliberately narrower than general image registration:
//! consecutive frames photograph the same fixed screen region, the content
//! under it only ever moves up (we only scroll down), and the movement is a
//! pure vertical translation. What breaks the naive version of that model
//! is everything a live page does while "holding still": sticky headers and
//! footers that do not move with the content, a blinking caret, ClearType
//! shimmer, hover sparkle, a video playing in a corner. ShareX's stitcher —
//! whose overall shape this keeps — matches rows with exact `memcmp` and is
//! defeated by a single animated pixel; this one matches tolerantly and then
//! verifies, so sparse noise costs a little confidence instead of the whole
//! match.
//!
//! Per offered frame, against the last *registered* frame:
//!
//! 1. **Row profiles.** Each row is reduced to its mean luma over the
//!    columns between the side margins (the margins hold scrollbars and
//!    ragged edges — same exclusion rule as the driver's settle comparison).
//!    Coarse matching happens on these H-element vectors, never on full
//!    frames.
//! 2. **Sticky chrome.** Rows static across the pair (per-row mean absolute
//!    luma difference under [`CHROME_MAD`]) are walked off the top and the
//!    bottom, capped at a third of the frame each; the rows between them
//!    are the content band everything else operates on. The estimate is
//!    taken from the first pair that registers with movement and then
//!    frozen, because the composite's geometry is built on it: every append
//!    lands the rows ending at `frame_h - footer`, so a footer that grew
//!    mid-run would duplicate that many rows at the seam and one that
//!    shrank would skip them. A pair static all the way through is the
//!    no-movement case and short-circuits to `dy == 0`, which is what the
//!    driver's end detection eats.
//! 3. **Coarse dy.** Normalized cross-correlation of the two profiles over
//!    every dy that leaves [`MIN_OVERLAP`] rows of overlap. Windows with
//!    degenerate variance (a blank page has a flat profile — there is
//!    nothing to correlate) are skipped rather than allowed to divide by
//!    almost-zero and vote nonsense.
//! 4. **Verification.** The global correlation maximum alone cannot be
//!    trusted: uniform text lines — articles, code, chat logs — make the
//!    profile near-periodic, so the curve peaks at every multiple of the
//!    line pitch, all within a whisker of each other, and capture noise
//!    decides which multiple comes out on top. What tells the multiples
//!    apart is exactly what a row mean averages away (a line number, a
//!    changed word), so raw pixels get the final say over a *field* of
//!    candidates: the top [`NCC_PEAKS_MAX`] local maxima reaching
//!    [`NCC_ACCEPT`] are each checked, best first, at their dy and its ±2
//!    neighbours, by mean absolute luma difference over a
//!    [`VERIFY_STRIP`]-row strip from the middle of the overlap. Sparse
//!    noise raises this a little; a wrong dy raises it an order of
//!    magnitude. The first peak with a candidate under [`VERIFY_MAD_MAX`]
//!    wins, and the minimum-MAD candidate within that peak picks the
//!    exact row.
//! 5. **Verification scan.** The walk over-reads on lined text — rows a
//!    line-pitch apart differ by a few glyphs, so it marches inward through
//!    real content from both ends — and once the frozen band is narrower
//!    than the scroll step, the correct dy is not even expressible inside
//!    it. Widening the band to the whole frame does not rescue it either:
//!    scored there the true alignment sits opposite real chrome on both
//!    edges, so its correlation collapses below hypotheses that are plainly
//!    wrong. The strip does not care — taken from the middle of the overlap
//!    it lands in content and reads ~0 at the true dy — so when the in-band
//!    sweep fails, every displacement is verified directly and the
//!    best-agreeing one wins. Same strip, same threshold, so this widens
//!    what is considered and never what is accepted.
//! 6. **Fallback.** A failed match is first tested against the cheaper
//!    hypothesis that the page never moved: scrolling decorrelates
//!    essentially every content row, while the usual verification-breaker
//!    on an unmoved page — a playing video, an animated ad — is a bounded
//!    band. If fewer than [`MOVED_FRACTION_MIN`] of the committed content
//!    band's rows changed, the result is `dy == 0` with the reference
//!    kept, which is what feeds the driver's end detection at the bottom
//!    of an animated page. Otherwise the content did move but the
//!    displacement could not be measured — most often a frame
//!    photographed mid-repaint — and the stitcher returns
//!    [`AppendResult::Hold`] rather than guess a step: a guess that
//!    overshoots the true displacement silently drops the difference
//!    from the composite, and no consumer of the image can detect the
//!    missing rows afterwards. Holding is lossless — the reference
//!    stays, so a later frame registers against it with the full
//!    accumulated displacement. Every seam in the composite is verified;
//!    the only degraded outcome is stopping short of the page bottom
//!    ([`Quality::Partial`]), never a plausible-looking wrong image.
//!
//! The composite keeps full-width rows — the side margins are excluded from
//! *matching* only. Frame 0 is kept whole minus the footer (it is the one
//! frame whose header rows are real content, seen once, at the top); every
//! registered frame appends the last `dy` rows of its content band; and the
//! final frame's footer band goes on in [`Stitcher::finish`], so a real
//! page bottom — which often *is* the "footer" — appears exactly once.
//! Negative dy is not even hypothesised: we never scroll up, and elastic
//! bounce is absorbed by the driver's settle loop.
//!
//! Cost per append: one profile pass and one chrome walk (both O(W·H) over
//! bytes), an O(H²) f32 correlation sweep on the profiles, and five 48-row
//! strips per coarse peak tried — one peak on the typical page, at most
//! [`NCC_PEAKS_MAX`] — a few milliseconds for a 2000 px frame, and never a
//! full-frame 2-D correlation. The verification scan is the one exception,
//! one strip per candidate displacement — tens of milliseconds, paid only
//! on a pair the in-band sweep has already failed.

use crate::frame::Frame;

/// Minimum rows of overlap a dy hypothesis must leave between the two
/// frames. Below this the correlation has too little evidence to mean
/// anything, so the search space is `0 ..= content_len - MIN_OVERLAP`.
const MIN_OVERLAP: usize = 32;

/// Coarse gate: the best profile correlation must reach this before the
/// strip verification is consulted at all. An aligned pair scores close to
/// 1.0 even through noise; an unrelated pair hovers near 0.
const NCC_ACCEPT: f64 = 0.85;

/// Local maxima of the correlation curve offered to strip verification,
/// strongest first. One would do for aperiodic content, where the true dy
/// is the clear global maximum. But a page of uniform text lines has a
/// near-periodic profile: the curve peaks at every multiple of the line
/// pitch, all within a whisker of each other, and whichever noise the two
/// captures picked up decides which multiple ranks first. Only the strip —
/// raw pixels, where the lines actually differ — can tell the multiples
/// apart, so it must be shown every plausible peak, not just the winner.
/// Eight covers several pitch multiples either side of the true step; the
/// cost cap is eight five-strip verifications on a pair whose every peak
/// fails.
const NCC_PEAKS_MAX: usize = 8;

/// Rows in the raw-pixel verification strip, taken from the middle of the
/// overlap (the middle is furthest from any chrome the walk under-counted).
const VERIFY_STRIP: usize = 48;

/// Mean absolute luma difference (0–255 scale) the verification strip may
/// show before the candidate is rejected. Carets, ClearType shimmer and a
/// percent of salt noise land around 1–2; a misregistration by even one row
/// of text lands around 10.
const VERIFY_MAD_MAX: f32 = 6.0;

/// Per-row mean absolute luma difference below which a row counts as static
/// during sticky-chrome detection. Loose enough that sparse noise does not
/// break a genuinely static row, tight enough that scrolled content —
/// which decorrelates whole rows — always registers as moving.
const CHROME_MAD: f32 = 2.0;

/// Winsorization cap for the chrome walk's per-pixel differences. A static
/// chrome row with a few salted pixels on it has *sparse, huge* differences
/// — one random pixel against dark chrome is a ~145-level outlier, and a
/// handful of those pushes an honest row past [`CHROME_MAD`], which
/// un-detects the footer and leaves its rows to poison the correlation
/// window. Clipping each pixel's contribution keeps sparse outliers sparse
/// while a genuinely scrolled row — where *every* pixel moves — still
/// clears the threshold several times over. The verification strip stays
/// unclipped: there, large differences are exactly the evidence of a wrong
/// dy that the check exists to find.
const CHROME_CLIP: i32 = 24;

/// Profile variance (per element) below which a correlation window is
/// treated as blank and skipped. A flat profile correlates equally well at
/// every offset, which is to say not at all.
const VAR_EPS: f64 = 1e-3;

/// Fraction of the committed content band's rows that must have changed
/// before a failed registration may be blamed on scrolling (and become a
/// hold). Below it, the change is a bounded animated band on an otherwise
/// unmoved page and the honest answer is `dy == 0` — real scrolling
/// decorrelates essentially every row, so it clears this threshold several
/// times over.
const MOVED_FRACTION_MIN: f32 = 0.30;

/// What happened when a frame was offered to the stitcher.
pub enum AppendResult {
    /// Registered: `dy` rows of new content were added to the composite.
    /// `dy == 0` means the page did not move — the frame is the previous
    /// one again, modulo noise.
    Appended { dy: u32 },
    /// Could not register this frame against the reference, but the run is
    /// still healthy: the reference is kept, so a later frame — most
    /// simply a re-capture of the unmoved page once a mid-repaint artifact
    /// has cleared — registers against it with the full accumulated
    /// displacement. A held frame costs time, never content.
    Hold,
    /// Registration has failed in a way that will not recover. Whatever has
    /// been composited so far is still good and must still be written —
    /// captured content is never discarded.
    Failed,
}

/// How much to trust the finished composite. Every seam in it is verified
/// either way; the only question is whether the run covered everything
/// that was on screen.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Quality {
    /// Every seam was verified and no frame was left unresolved.
    Exact,
    /// The run ended on unresolved holds; the composite stops short of
    /// what was on screen.
    Partial,
}

/// The finished image: RGBA (the order `image` wants for a PNG), top-down,
/// `width * height * 4` bytes.
pub struct Composite {
    pub rgba: Vec<u8>,
    pub width: u32,
    pub height: u32,
    pub quality: Quality,
}

pub struct Stitcher {
    width: u32,
    /// Height of every incoming frame — the viewport. Fixed for the run.
    frame_h: u32,
    /// Column range `[c0, c1)` used for all matching: the side margins are
    /// excluded exactly as in the driver's settle comparison, and for the
    /// same reason — that is where scrollbars live.
    c0: usize,
    c1: usize,
    /// The first frame, kept whole until [`Stitcher::finish`]: how much of
    /// its bottom is sticky footer is only known once the session's footer
    /// estimate has settled, so its contribution cannot be materialized up
    /// front.
    frame0: Frame,
    /// The last *registered* frame. Registration is always against this —
    /// held frames deliberately do not replace it — and its bottom rows
    /// supply the composite's footer band at finish.
    reference: Frame,
    /// Cached row profile of `reference`, so each append computes exactly
    /// one profile.
    reference_profile: Vec<f32>,
    /// Full-width content rows appended by registered frames, in order.
    appended: Vec<u8>,
    appended_rows: u32,
    /// Session sticky-chrome estimate, each side capped at a third of the
    /// frame. Taken once — from the first pair that registers with
    /// movement, since a static-but-animated pair would "detect" its
    /// animation boundary as chrome — and then frozen for the rest of the
    /// run.
    ///
    /// Frozen because the composite's geometry is built on it: every
    /// append lands the rows ending at `frame_h - footer`, so a footer
    /// that grew mid-run would duplicate that many rows at the seam and
    /// one that shrank would skip them. Re-estimating per pair is only
    /// safe for *matching*, never for the append range.
    header: u32,
    footer: u32,
    /// Whether [`Stitcher::header`]/[`Stitcher::footer`] are the frozen
    /// session estimate rather than the initial zeros.
    chrome_locked: bool,
    /// Holds not yet resolved by a later verified registration. Any
    /// verified result resolves them — `dy > 0` folds the held frames'
    /// displacement into a measured seam, and a verified `dy == 0` proves
    /// the screen still matches the reference, so the held frames showed
    /// nothing the composite lacks. Nonzero at finish means content on
    /// screen never made it into the composite.
    consecutive_holds: u32,
    frames: u32,
}

impl Stitcher {
    /// Start a composite from the first frame, captured before any
    /// scrolling. It is always kept whole — it is the only frame whose
    /// header rows are real content rather than chrome repeated at a seam.
    pub fn new(frame0: Frame) -> Self {
        let side = side_ignore(frame0.width);
        let (c0, c1) = band(frame0.width, side);
        let reference_profile = profile(&frame0, c0, c1);
        Self {
            width: frame0.width,
            frame_h: frame0.height,
            c0,
            c1,
            frame0: frame0.clone(),
            reference: frame0,
            reference_profile,
            appended: Vec::new(),
            appended_rows: 0,
            header: 0,
            footer: 0,
            chrome_locked: false,
            consecutive_holds: 0,
            frames: 1,
        }
    }

    /// Offer the frame captured after one scroll step.
    pub fn append(&mut self, frame: Frame) -> AppendResult {
        if frame.width != self.width || frame.height != self.frame_h {
            // The region is fixed for the whole run, so this can only mean
            // the capture came back malformed.
            error!(
                "frame is {}x{} but the composite is {}x{}",
                frame.width, frame.height, self.width, self.frame_h
            );
            return AppendResult::Failed;
        }

        // Sticky-chrome walk for this pair. A frame static all the way
        // through (within tolerance, over the matching band) is the page
        // not moving at all: report zero new rows and keep the reference —
        // there is nothing better to re-register against.
        let Some((pair_header, pair_footer)) = self.pair_chrome(&frame) else {
            self.consecutive_holds = 0;
            return AppendResult::Appended {
                dy: 0,
            };
        };
        // Once frozen the session estimate governs, and this pair's own
        // observation is discarded: chrome belongs to the page, and the
        // append geometry may not move under the composite.
        let cap = self.frame_h / 3;
        let (header, footer) = if self.chrome_locked {
            (self.header, self.footer)
        } else {
            (pair_header.min(cap), pair_footer.min(cap))
        };

        let cur_profile = profile(&frame, self.c0, self.c1);

        match self.register(&cur_profile, &frame, header, footer) {
            // Verified "did not move" — some rows changed (an animation,
            // a caret) but the content is where it was. The changed pixels
            // are noise, not a better reference, so the old one stays; and
            // an unmoved pair must not commit chrome either, or an
            // animation boundary would masquerade as a header.
            Some(0) => {
                self.consecutive_holds = 0;
                AppendResult::Appended {
                    dy: 0,
                }
            }
            Some(dy) => {
                // A scrolled pair: its chrome observation is trustworthy,
                // so commit it, and append using the same footer the match
                // was computed against — mixing estimates would shear the
                // seam by their difference.
                self.header = header;
                self.footer = footer;
                self.chrome_locked = true;
                let bottom = self.frame_h - footer;
                self.push_rows(&frame, bottom - dy, bottom);
                self.reference = frame;
                self.reference_profile = cur_profile;
                self.frames += 1;
                self.consecutive_holds = 0;
                AppendResult::Appended {
                    dy,
                }
            }
            None => {
                // A failed registration has two very different causes with
                // the same symptom: the content scrolled but could not be
                // verified, or it never moved and something animated (a
                // video, a GIF ad) broke the verification. What separates
                // them is how much of the frame changed — scrolling
                // decorrelates essentially every content row, an animation
                // touches a bounded band — so test the did-not-move
                // hypothesis first. Zero keeps the reference (there is
                // nothing better to register against) and feeds the
                // driver's end detection, which is what lets a page with a
                // video at the bottom finish instead of appending the same
                // screenful until a hard cap.
                if self.moved_fraction(&frame) < MOVED_FRACTION_MIN {
                    info!(
                        "registration failed but under {:.0}% of the content band moved; reporting no movement",
                        MOVED_FRACTION_MIN * 100.0
                    );
                    self.consecutive_holds = 0;
                    return AppendResult::Appended {
                        dy: 0,
                    };
                }
                // The content moved but the displacement could not be
                // measured — most often a frame photographed mid-repaint.
                // A guessed step that overshoots the true one silently
                // drops the difference from the composite, so hold
                // instead: the reference stays, and a later frame (a
                // clean look at the same position, or the next scroll)
                // registers against it with the full displacement.
                self.consecutive_holds += 1;
                // The chrome numbers make this line answer "why did my
                // capture stop early" from the log alone: a runaway walk
                // shows up as a pair walk of hundreds of rows.
                warn!(
                    "frame after {} registered frames failed verification (hold #{}; chrome band {header}+{footer}, pair walked {pair_header}+{pair_footer})",
                    self.frames, self.consecutive_holds
                );
                AppendResult::Hold
            }
        }
    }

    /// Frames that made it into the composite, first one included. Read by
    /// the status/done events and the frame cap.
    pub fn frames(&self) -> u32 {
        self.frames
    }

    /// Composite height in pixels so far. Read by the status/done events and
    /// the height cap.
    pub fn height(&self) -> u32 {
        self.frame_h + self.appended_rows
    }

    pub fn finish(self) -> Composite {
        let stride = self.frame0.stride();
        let h = self.frame_h as usize;
        let footer = self.footer as usize;

        let mut bgra = Vec::with_capacity((h - footer) * stride + self.appended.len() + footer * stride);
        // Frame 0 minus the footer; then everything registered; then the
        // *final* frame's footer band, so a real page bottom shows once.
        bgra.extend_from_slice(&self.frame0.bgra[..(h - footer) * stride]);
        bgra.extend_from_slice(&self.appended);
        if footer > 0 {
            bgra.extend_from_slice(&self.reference.bgra[(h - footer) * stride..]);
        }

        let quality = if self.consecutive_holds > 0 {
            Quality::Partial
        } else {
            Quality::Exact
        };

        Composite {
            rgba: bgra_to_rgba(bgra),
            width: self.width,
            height: self.frame_h + self.appended_rows,
            quality,
        }
    }

    /// Fraction of the committed content band's rows that changed against
    /// the reference (clipped row MAD at or above [`CHROME_MAD`], same
    /// yardstick as the chrome walk). The *committed* chrome bounds the
    /// band, not this pair's walk: on an unmoved pair the walk runs
    /// through the static content and stops at the animation, which would
    /// shrink the band to little but the animated rows and read as
    /// movement everywhere. The committed header and footer are each
    /// capped at a third of the frame, so the band is never empty.
    fn moved_fraction(&self, frame: &Frame) -> f32 {
        let top = self.header as usize;
        let bottom = self.frame_h as usize - self.footer as usize;
        let moved = (top..bottom)
            .filter(|&y| row_mad(&self.reference, y, frame, y, self.c0, self.c1, CHROME_CLIP) >= CHROME_MAD)
            .count();
        moved as f32 / (bottom - top) as f32
    }

    /// Walk static rows off the top and bottom of the (reference, frame)
    /// pair. `None` means every row is static — the pair is identical
    /// within tolerance.
    fn pair_chrome(&self, frame: &Frame) -> Option<(u32, u32)> {
        let h = self.frame_h as usize;
        let is_static = |y: usize| row_mad(&self.reference, y, frame, y, self.c0, self.c1, CHROME_CLIP) < CHROME_MAD;
        let mut header = 0;
        while header < h && is_static(header) {
            header += 1;
        }
        if header == h {
            return None;
        }
        let mut footer = 0;
        while footer < h - header && is_static(h - 1 - footer) {
            footer += 1;
        }
        Some((header as u32, footer as u32))
    }

    /// Steps 3 and 4: coarse profile correlation, then raw-strip
    /// verification of the surviving peaks, best first. `None` means no
    /// candidate survived — the caller drops to the fallback ladder.
    fn register(&self, cur_profile: &[f32], cur: &Frame, header: u32, footer: u32) -> Option<u32> {
        if let Some(dy) = self.register_in_band(cur_profile, cur, header as usize, footer as usize) {
            return Some(dy);
        }
        // The chrome estimate is a heuristic and lined text defeats it: rows
        // one line-pitch apart differ by a few glyphs, so the static-row walk
        // marches inward from both ends through ordinary content. That shrinks
        // the searchable band, and with it the largest displacement the sweep
        // can express — a scroll step wider than the band has no correct
        // hypothesis to offer, so every peak is spurious and the pair can
        // never register. Retry once across the whole frame, where the true
        // dy is always representable. `dy` is a property of the scroll, not
        // of the band it was measured in, so the caller still appends against
        // the frozen footer and the seam stays continuous. The strip check is
        // untouched: this widens what is considered, never what is accepted.
        // Correlation cannot rank the answer here. Scored across the whole
        // frame the true alignment sits opposite real chrome on both edges,
        // so its NCC collapses below hypotheses that are outright wrong, and
        // no threshold or peak budget recovers it. The strip is unbothered —
        // taken from the middle of the overlap it lands in content and reads
        // ~0 at the true dy — so let it do the searching: walk every
        // displacement and keep the best-verified one. This is the last
        // resort before holding, it runs only when the in-band sweep has
        // already failed, and it accepts nothing the in-band path would have
        // rejected: the same strip and the same threshold decide.
        if header > 0 || footer > 0 {
            return self.register_by_strip_scan(cur, footer);
        }
        None
    }

    /// Exhaustive verification scan over the full frame: the displacement
    /// whose strip agrees best, provided it agrees well enough. `footer`
    /// bounds the hypotheses, not the comparison: every seam lands the
    /// rows ending at `frame_h - footer`, so a dy wider than that span
    /// has new content below the append line and cannot be placed without
    /// dropping rows — it is not a measurable step here, whatever the
    /// strip would say about it.
    fn register_by_strip_scan(&self, cur: &Frame, footer: u32) -> Option<u32> {
        let h = self.frame_h as usize;
        let max_dy = (h - MIN_OVERLAP).min(h - footer as usize);
        let mut best: Option<(usize, f32)> = None;
        for dy in 0..=max_dy {
            let mad = self.strip_mad(cur, 0, 0, dy);
            if mad <= VERIFY_MAD_MAX && best.is_none_or(|(_, m)| mad < m) {
                best = Some((dy, mad));
            }
        }
        best.map(|(dy, _)| dy as u32)
    }

    /// One registration attempt within an explicit chrome band.
    ///
    /// Every peak is verified and the **best-agreeing** candidate wins, not
    /// the first one that merely passes. The distinction only shows up when
    /// two hypotheses both verify, and then it is the whole ball game: on
    /// text whose lines are told apart by little more than their line
    /// numbers, a strip laid over the *wrong* multiple of the line pitch
    /// disagrees in a few glyphs out of a full row — a MAD of one or two,
    /// comfortably inside [`VERIFY_MAD_MAX`] — while the true displacement
    /// reads ~0. Since correlation cannot rank near-identical multiples
    /// either (their NCC differs in the third decimal, so noise decides the
    /// order), taking the first passing peak is taking a coin flip, and
    /// losing it composites a screen of duplicated content that nothing
    /// downstream can detect. Measured on a 400-line document scrolled three
    /// lines at a time: every step registered ~18 lines out, and every seam
    /// in the composite jumped back a screen.
    ///
    /// Ties go to the smaller displacement — with equal evidence, the
    /// hypothesis that claims less is the one that leaves the *next* pair
    /// something to register against.
    fn register_in_band(&self, cur_profile: &[f32], cur: &Frame, header: usize, footer: usize) -> Option<u32> {
        let h = self.frame_h as usize;
        let n = h - header - footer;
        if n < MIN_OVERLAP {
            return None;
        }
        let max_dy = n - MIN_OVERLAP;
        // The profiles locate a seam to within a row or two; raw pixels pick
        // the exact one, over every peak's dy and its ±2 neighbours.
        let mut chosen: Option<(usize, f32)> = None;
        for (peak_dy, _) in self.coarse_peaks(cur_profile, header, footer) {
            for delta in [0isize, -1, 1, -2, 2] {
                let Some(dy) = peak_dy
                    .checked_add_signed(delta)
                    .filter(|d| *d <= max_dy)
                else {
                    continue;
                };
                let mad = self.strip_mad(cur, header, footer, dy);
                if mad <= VERIFY_MAD_MAX && chosen.is_none_or(|(cdy, m)| mad < m || (mad == m && dy < cdy)) {
                    chosen = Some((dy, mad));
                }
            }
        }
        chosen.map(|(dy, _)| dy as u32)
    }

    /// Step 3 alone: the correlation sweep, reduced to the dy hypotheses
    /// worth verifying — the local maxima of the NCC curve that reach
    /// [`NCC_ACCEPT`], strongest first, at most [`NCC_PEAKS_MAX`] of them.
    /// Local maxima, not the top samples: the curve around any peak is
    /// smooth, so the K best samples would be K neighbours of the global
    /// maximum, and the whole point is to surface *distinct* hypotheses.
    /// The caller guarantees the content band is at least [`MIN_OVERLAP`]
    /// rows.
    fn coarse_peaks(&self, cur_profile: &[f32], header: usize, footer: usize) -> Vec<(usize, f64)> {
        let h = self.frame_h as usize;
        let n = h - header - footer;
        let a = &self.reference_profile[header..h - footer];
        let b = &cur_profile[header..h - footer];

        // Prefix sums (in f64: sums of squares over thousands of ~255²
        // terms would lose the variance to f32 cancellation) make every
        // window's mean and variance O(1); only the dot product is O(L)
        // per candidate, giving the O(H²) sweep the module doc budgets for.
        let mut sa = vec![0f64; n + 1];
        let mut sa2 = vec![0f64; n + 1];
        let mut sb = vec![0f64; n + 1];
        let mut sb2 = vec![0f64; n + 1];
        for i in 0..n {
            let (av, bv) = (a[i] as f64, b[i] as f64);
            sa[i + 1] = sa[i] + av;
            sa2[i + 1] = sa2[i] + av * av;
            sb[i + 1] = sb[i] + bv;
            sb2[i + 1] = sb2[i] + bv * bv;
        }

        // score(dy) = NCC of prev[header+dy ..] against cur[.. -dy]: the
        // reference's rows reappear higher up in the new frame after a
        // downward scroll. dy starts at 0 — negative dy is not a
        // hypothesis we entertain. Skipped (degenerate-variance) windows
        // keep NEG_INFINITY, which also makes their computed neighbours
        // eligible as peaks.
        let max_dy = n - MIN_OVERLAP;
        let mut ncc = vec![f64::NEG_INFINITY; max_dy + 1];
        for (dy, score) in ncc.iter_mut().enumerate() {
            let l = (n - dy) as f64;
            let dot: f64 = a[dy..]
                .iter()
                .zip(&b[..n - dy])
                .map(|(x, y)| *x as f64 * *y as f64)
                .sum();
            let sum_a = sa[n] - sa[dy];
            let sum_a2 = sa2[n] - sa2[dy];
            let sum_b = sb[n - dy];
            let sum_b2 = sb2[n - dy];
            let var_a = sum_a2 - sum_a * sum_a / l;
            let var_b = sum_b2 - sum_b * sum_b / l;
            if var_a / l < VAR_EPS || var_b / l < VAR_EPS {
                continue;
            }
            *score = (dot - sum_a * sum_b / l) / (var_a * var_b).sqrt();
        }

        let mut peaks: Vec<(usize, f64)> = Vec::new();
        for (dy, &v) in ncc.iter().enumerate() {
            if v < NCC_ACCEPT {
                continue;
            }
            let left = dy
                .checked_sub(1)
                .map_or(f64::NEG_INFINITY, |i| ncc[i]);
            let right = ncc
                .get(dy + 1)
                .copied()
                .unwrap_or(f64::NEG_INFINITY);
            if v >= left && v > right {
                peaks.push((dy, v));
            }
        }
        peaks.sort_by(|x, y| y.1.total_cmp(&x.1));
        peaks.truncate(NCC_PEAKS_MAX);
        peaks
    }

    /// Mean absolute luma difference of a [`VERIFY_STRIP`]-row raw strip
    /// from the middle of the overlap at hypothesis `dy`.
    fn strip_mad(&self, cur: &Frame, header: usize, footer: usize, dy: usize) -> f32 {
        let h = self.frame_h as usize;
        let overlap = (h - header - footer) - dy;
        let strip = VERIFY_STRIP.min(overlap);
        let cur_start = header + (overlap - strip) / 2;
        let prev_start = cur_start + dy;
        let sum: f32 = (0..strip)
            .map(|r| row_mad(&self.reference, prev_start + r, cur, cur_start + r, self.c0, self.c1, i32::MAX))
            .sum();
        sum / strip as f32
    }

    /// Copy full-width rows `[from, to)` of `frame` onto the composite.
    fn push_rows(&mut self, frame: &Frame, from: u32, to: u32) {
        let stride = frame.stride();
        self.appended
            .extend_from_slice(&frame.bgra[from as usize * stride..to as usize * stride]);
        self.appended_rows += to - from;
    }
}

/// Per-side column margin excluded from all matching. Identical to the
/// driver's settle-comparison rule (`drive::side_ignore`), deliberately:
/// "has it stopped moving" and "where did it move to" must ignore the same
/// scrollbar, or the settle loop hands the stitcher frames it then trips on.
fn side_ignore(width: u32) -> u32 {
    50.max(width / 20).min(width / 3)
}

/// Matching band `[c0, c1)` in columns. A region narrower than its margins
/// is used whole — better to match on scrollbar pixels than on nothing.
fn band(width: u32, side: u32) -> (usize, usize) {
    if side * 2 >= width {
        (0, width as usize)
    } else {
        (side as usize, (width - side) as usize)
    }
}

/// Rec. 601 luma in 8-bit fixed point (29 + 150 + 77 = 256); `px` is BGRA.
#[inline]
fn luma(px: &[u8]) -> i32 {
    ((29 * px[0] as u32 + 150 * px[1] as u32 + 77 * px[2] as u32 + 128) >> 8) as i32
}

/// Per-row mean luma over the matching band — one frame reduced to H
/// numbers.
fn profile(frame: &Frame, c0: usize, c1: usize) -> Vec<f32> {
    let stride = frame.stride();
    (0..frame.height as usize)
        .map(|y| {
            let row = &frame.bgra[y * stride + c0 * 4..y * stride + c1 * 4];
            let sum: i32 = row.chunks_exact(4).map(luma).sum();
            sum as f32 / (c1 - c0) as f32
        })
        .collect()
}

/// Mean absolute luma difference between row `ya` of `a` and row `yb` of
/// `b`, over the matching band, with each pixel's contribution clipped to
/// `clip` (pass `i32::MAX` for the plain mean — see [`CHROME_CLIP`] for why
/// the chrome walk clips).
fn row_mad(a: &Frame, ya: usize, b: &Frame, yb: usize, c0: usize, c1: usize, clip: i32) -> f32 {
    let (sa, sb) = (a.stride(), b.stride());
    let ra = &a.bgra[ya * sa + c0 * 4..ya * sa + c1 * 4];
    let rb = &b.bgra[yb * sb + c0 * 4..yb * sb + c1 * 4];
    let sum: u32 = ra
        .chunks_exact(4)
        .zip(rb.chunks_exact(4))
        .map(|(pa, pb)| (luma(pa) - luma(pb)).abs().min(clip) as u32)
        .sum();
    sum as f32 / (c1 - c0) as f32
}

/// In-place channel swap. The capture path keeps everything in the BGRA
/// order `GetDIBits` produces; PNG encoding is the first and only consumer
/// that cares.
fn bgra_to_rgba(mut buf: Vec<u8>) -> Vec<u8> {
    for px in buf.chunks_exact_mut(4) {
        px.swap(0, 2);
    }
    buf
}

#[cfg(test)]
mod tests {
    use super::*;

    // ── Synthetic page ─────────────────────────────────────────────────
    //
    // A deterministic tall "document" scrolled behind sticky chrome. All
    // content is grayscale on purpose: B == R makes the BGRA→RGBA swap the
    // identity, so composites can be compared byte-for-byte against
    // expectations built in the same order.

    /// Viewport width. `side_ignore(300)` = 50 per side, so the matching
    /// band is columns [50, 250).
    const W: u32 = 300;
    /// Viewport height; the chrome cap is H/3 = 80, comfortably above the
    /// injected chrome.
    const VIEW_H: u32 = 240;
    const HDR: u32 = 30;
    const FTR: u32 = 20;
    /// Content rows visible at once.
    const C_LEN: u32 = VIEW_H - HDR - FTR;
    /// Total document height. The furthest scroll position is
    /// `PAGE_H - C_LEN` = 1010.
    const PAGE_H: u32 = 1200;
    const BOTTOM: u32 = PAGE_H - C_LEN;

    /// xorshift64 — deterministic, no crates.
    struct Rng(u64);

    impl Rng {
        fn new(seed: u64) -> Self {
            Rng(seed.max(1))
        }

        fn next(&mut self) -> u64 {
            let mut x = self.0;
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            self.0 = x;
            x
        }

        fn below(&mut self, n: u64) -> u64 {
            self.next() % n
        }
    }

    /// The document: horizontal blocks of random luma (text-line structure,
    /// so profiles have real variance) plus per-pixel noise (so no two rows
    /// are ever byte-identical and matching cannot luck into a wrong dy).
    fn build_page() -> Vec<u8> {
        let mut rng = Rng::new(0x5eed_cafe);
        let mut page = Vec::with_capacity((PAGE_H * W * 4) as usize);
        let mut y = 0;
        while y < PAGE_H {
            let level = 40 + rng.below(176) as i32;
            let rows = (6 + rng.below(20) as u32).min(PAGE_H - y);
            for _ in 0..rows {
                for _ in 0..W {
                    let v = (level + rng.below(33) as i32 - 16).clamp(0, 255) as u8;
                    page.extend_from_slice(&[v, v, v, 255]);
                }
            }
            y += rows;
        }
        page
    }

    /// Sticky chrome pixels: a fixed function of position, so they are
    /// identical in every frame — exactly what makes chrome sticky.
    fn chrome_pixel(y: u32, x: u32, base: u8) -> u8 {
        base.wrapping_add(((x * 7 + y * 13) % 40) as u8)
    }

    fn push_chrome_rows(out: &mut Vec<u8>, rows: u32, base: u8) {
        for y in 0..rows {
            for x in 0..W {
                let v = chrome_pixel(y, x, base);
                out.extend_from_slice(&[v, v, v, 255]);
            }
        }
    }

    /// The viewport with the document scrolled to `pos`: sticky header,
    /// `C_LEN` document rows, sticky footer.
    fn render(page: &[u8], pos: u32) -> Frame {
        let stride = (W * 4) as usize;
        let mut bgra = Vec::with_capacity((VIEW_H * W * 4) as usize);
        push_chrome_rows(&mut bgra, HDR, 200);
        bgra.extend_from_slice(&page[pos as usize * stride..(pos + C_LEN) as usize * stride]);
        push_chrome_rows(&mut bgra, FTR, 15);
        Frame {
            bgra,
            width: W,
            height: VIEW_H,
        }
    }

    /// What a perfect stitch of a scroll ending at `last_pos` looks like:
    /// the header once, the document from the top down to the last visible
    /// row, the footer once.
    fn expected_composite(page: &[u8], last_pos: u32) -> Vec<u8> {
        let stride = (W * 4) as usize;
        let mut out = Vec::new();
        push_chrome_rows(&mut out, HDR, 200);
        out.extend_from_slice(&page[..(last_pos + C_LEN) as usize * stride]);
        push_chrome_rows(&mut out, FTR, 15);
        out
    }

    /// 1% salt noise everywhere plus, optionally, a "caret": a small black
    /// rectangle at a fixed viewport position that the toggling caller
    /// blinks on and off between frames.
    fn corrupt(frame: &mut Frame, rng: &mut Rng, caret: bool) {
        for _ in 0..(W * VIEW_H / 100) {
            let i = rng.below((W * VIEW_H) as u64) as usize * 4;
            let v = rng.below(256) as u8;
            frame.bgra[i..i + 3].copy_from_slice(&[v, v, v]);
        }
        if caret {
            let stride = frame.stride();
            for y in 100..114 {
                for x in 120..122 {
                    let i = y * stride + x * 4;
                    frame.bgra[i..i + 3].copy_from_slice(&[0, 0, 0]);
                }
            }
        }
    }

    fn blank(v: u8) -> Frame {
        Frame {
            bgra: [v, v, v, 255].repeat((W * VIEW_H) as usize),
            width: W,
            height: VIEW_H,
        }
    }

    fn append_dy(st: &mut Stitcher, frame: Frame) -> u32 {
        match st.append(frame) {
            AppendResult::Appended {
                dy,
            } => dy,
            AppendResult::Hold => panic!("frame unexpectedly held"),
            AppendResult::Failed => panic!("frame unexpectedly failed"),
        }
    }

    // ── The deliverable: §5.8 ──────────────────────────────────────────

    #[test]
    fn reconstructs_a_scrolled_page_exactly() {
        let page = build_page();
        let positions = [130u32, 260, 390, 520, 650, 780, 910, BOTTOM];
        let mut st = Stitcher::new(render(&page, 0));

        let mut prev = 0;
        for pos in positions {
            // Every measured dy is the true scroll distance — the partial
            // last page included, where the step shrinks to whatever room
            // the document had left.
            assert_eq!(append_dy(&mut st, render(&page, pos)), pos - prev, "step to {pos}");
            prev = pos;
            // The chrome the first registered pair saw is the chrome the
            // whole run uses: every append lands rows ending at
            // `frame_h - footer`, so a value that drifted would duplicate
            // rows at the seam where it grew and skip them where it shrank.
            assert!(st.chrome_locked);
            assert_eq!((st.header, st.footer), (HDR, FTR), "chrome moved at {pos}");
        }

        // The bottom: the same viewport again is zero new rows, twice —
        // the driver's end-detection contract.
        for _ in 0..2 {
            assert_eq!(append_dy(&mut st, render(&page, BOTTOM)), 0);
        }

        assert_eq!(st.frames(), 1 + positions.len() as u32);
        assert_eq!(st.height(), HDR + PAGE_H + FTR);

        let composite = st.finish();
        assert_eq!(composite.quality, Quality::Exact);
        assert_eq!(composite.width, W);
        assert_eq!(composite.height, HDR + PAGE_H + FTR);
        // Byte-exact reconstruction: header once, whole document, footer
        // once — proving the chrome was cropped at every seam and kept
        // exactly where it belongs. (Grayscale content, so the RGBA swap
        // does not disturb the comparison.)
        assert_eq!(composite.rgba, expected_composite(&page, BOTTOM));
    }

    /// An over-read chrome band must not make a scroll unmeasurable. On
    /// lined text the static-row walk marches inward through real content
    /// (rows a line-pitch apart differ by a few glyphs), and once the band
    /// is narrower than the scroll step there is no correct hypothesis
    /// inside it — the sweep can only offer spurious peaks, every one of
    /// which the strip rejects. Observed live: a 740 px viewport read as
    /// 246 header + 242 footer, leaving a largest expressible dy of 220
    /// against real steps of 228.
    #[test]
    fn a_scroll_wider_than_the_chrome_band_still_registers() {
        let page = build_page();
        let mut st = Stitcher::new(render(&page, 0));
        // Freeze the pathological estimate: the cap on both sides leaves 80
        // content rows, so the in-band sweep tops out at dy = 48.
        let cap = VIEW_H / 3;
        st.header = cap;
        st.footer = cap;
        st.chrome_locked = true;
        let in_band_max_dy = VIEW_H - 2 * cap - MIN_OVERLAP as u32;
        assert!(in_band_max_dy < 100, "the step must be unrepresentable in-band to test the retry");

        assert_eq!(
            append_dy(&mut st, render(&page, 100)),
            100,
            "the full-frame retry should measure the true step"
        );
        // The retry measures dy; it must not adopt its own band as the
        // session chrome, or the append geometry would move under the
        // composite.
        assert_eq!((st.header, st.footer), (cap, cap));
    }

    #[test]
    fn tolerates_noise_jitter_and_a_caret() {
        let page = build_page();
        let mut rng = Rng::new(0xabc_def);
        // The scripted steps carry ±1 px jitter; the stitcher measures
        // actual displacement, so the assertions still expect exact dy.
        let positions = [131u32, 259, 391, 520, 649, 781, 910, BOTTOM];

        let mut frame0 = render(&page, 0);
        corrupt(&mut frame0, &mut rng, false);
        let mut st = Stitcher::new(frame0);

        let mut prev = 0;
        for (i, pos) in positions.iter().copied().enumerate() {
            let mut frame = render(&page, pos);
            corrupt(&mut frame, &mut rng, i % 2 == 0);
            assert_eq!(append_dy(&mut st, frame), pos - prev, "step to {pos}");
            prev = pos;
        }

        let composite = st.finish();
        // Every seam verified despite the noise: salt and the caret raise
        // the strip MAD by ~1, far under the acceptance threshold.
        assert_eq!(composite.quality, Quality::Exact);
        assert_eq!(composite.height, HDR + PAGE_H + FTR);

        // Reconstruction modulo noise: only the salted pixels and the
        // caret may disagree with the clean expectation.
        let expected = expected_composite(&page, BOTTOM);
        let matching = composite
            .rgba
            .chunks_exact(4)
            .zip(expected.chunks_exact(4))
            .filter(|(a, b)| a == b)
            .count();
        let total = (composite.height * W) as usize;
        let fraction = matching as f64 / total as f64;
        assert!(fraction > 0.97, "only {fraction:.4} of pixels survived the stitch intact");
    }

    #[test]
    fn nothing_scrolled_reports_zero_dy_every_time() {
        let page = build_page();
        let mut st = Stitcher::new(render(&page, 0));
        for _ in 0..3 {
            assert_eq!(append_dy(&mut st, render(&page, 0)), 0);
        }
        // The composite is exactly the one frame — the driver turns this
        // into the `no_movement` result.
        assert_eq!(st.frames(), 1);
        assert_eq!(st.height(), VIEW_H);
        let composite = st.finish();
        assert_eq!(composite.quality, Quality::Exact);
        assert_eq!(composite.height, VIEW_H);
        assert_eq!(composite.rgba, render(&page, 0).bgra);
    }

    #[test]
    fn a_blank_page_never_registers() {
        // Uniform frames in different shades: clearly not identical, but
        // their profiles are flat — the degenerate-variance guard must
        // refuse to correlate them rather than report a confident garbage
        // dy. With nothing measurable, each becomes a hold.
        let mut st = Stitcher::new(blank(200));
        assert!(matches!(st.append(blank(210)), AppendResult::Hold));
        assert!(matches!(st.append(blank(190)), AppendResult::Hold));
        let composite = st.finish();
        // Unresolved holds at the end mean on-screen content never made it
        // into the composite.
        assert_eq!(composite.quality, Quality::Partial);
        assert_eq!(composite.height, VIEW_H);
    }

    #[test]
    fn holds_an_unmatchable_frame_and_catches_up() {
        let page = build_page();
        let mut st = Stitcher::new(render(&page, 0));

        // A frame with nothing to register: held, not appended — and the
        // reference deliberately stays frame 0.
        assert!(matches!(st.append(blank(200)), AppendResult::Hold));
        assert_eq!(st.frames(), 1);

        // The next frame registers against that same reference with the
        // full accumulated displacement: the held frame cost nothing.
        assert_eq!(append_dy(&mut st, render(&page, 150)), 150);

        let composite = st.finish();
        assert_eq!(composite.quality, Quality::Exact);
        assert_eq!(composite.height, VIEW_H + 150);
    }

    /// A frame photographed mid-repaint: everything above the tear still
    /// shows the old scroll position, everything below it the new one.
    fn torn(page: &[u8], old_pos: u32, new_pos: u32, tear_row: u32) -> Frame {
        let stride = (W * 4) as usize;
        let mut frame = render(page, new_pos);
        let old = render(page, old_pos);
        frame.bgra[..tear_row as usize * stride].copy_from_slice(&old.bgra[..tear_row as usize * stride]);
        frame
    }

    #[test]
    fn a_torn_frame_is_held_and_the_recapture_loses_no_rows() {
        let page = build_page();
        let mut st = Stitcher::new(render(&page, 0));
        assert_eq!(append_dy(&mut st, render(&page, 100)), 100);

        // Mid-repaint at the 200 → 230 step: the top 30 content rows are
        // from the old position, the rest from the new. Neither alignment
        // explains more than half the overlap, so registration must
        // refuse the frame — appending it under any assumed step would
        // splice rows from two positions into one seam and leave the
        // difference silently missing from the page.
        let torn_frame = torn(&page, 200, 230, HDR + 30);
        assert!(matches!(st.append(torn_frame), AppendResult::Hold));
        assert_eq!(st.frames(), 2);

        // A clean look at the same position registers against the
        // unchanged reference with the full accumulated displacement:
        // the held frame cost nothing.
        assert_eq!(append_dy(&mut st, render(&page, 230)), 130);

        let mut prev = 230;
        for pos in [360u32, 490, 620, 750, 880, BOTTOM] {
            assert_eq!(append_dy(&mut st, render(&page, pos)), pos - prev, "step to {pos}");
            prev = pos;
        }
        for _ in 0..2 {
            assert_eq!(append_dy(&mut st, render(&page, BOTTOM)), 0);
        }

        let composite = st.finish();
        assert_eq!(composite.quality, Quality::Exact);
        assert_eq!(composite.height, HDR + PAGE_H + FTR);
        // Byte-exact reconstruction: every document row is present exactly
        // once — nothing was dropped at the seam the torn frame disturbed.
        assert_eq!(composite.rgba, expected_composite(&page, BOTTOM));
    }

    // ── Periodic content ───────────────────────────────────────────────
    //
    // Uniform text lines on a fixed pitch, the shape of an article, a code
    // listing or a chat log. The row profile is exactly periodic — each
    // line's distinguishing marker adds luma in one column block and
    // subtracts the same amount in another, so the row *mean* cannot see
    // it — which makes every pitch multiple an equally good coarse
    // hypothesis. Only raw pixels can identify the true alignment.

    /// Line pitch of the periodic page.
    const PITCH: u32 = 38;
    /// Rows of "text" at the top of each line; the rest is background.
    const LINE_TEXT: u32 = 28;

    /// Per-line marker amplitude. The cycle is longer than any whole-line
    /// shift the coarse search can hypothesise on this viewport, and every
    /// pairwise difference within the cycle is at least 25 luma levels, so
    /// a strip laid over misaligned lines always fails verification.
    fn marker(line: u32) -> i32 {
        [10, 60, 110, 35, 85][(line % 5) as usize]
    }

    fn build_periodic_page() -> Vec<u8> {
        let mut page = Vec::with_capacity((PAGE_H * W * 4) as usize);
        for y in 0..PAGE_H {
            let (line, r) = (y / PITCH, y % PITCH);
            for x in 0..W {
                let v = if r >= LINE_TEXT {
                    230
                } else if (60..110).contains(&x) {
                    128 + marker(line)
                } else if (110..160).contains(&x) {
                    128 - marker(line)
                } else {
                    // Row-varying structure outside the marker blocks, so
                    // even a ±1-row misalignment fails the strip loudly.
                    30 + 24 * (r % 8) as i32
                };
                let v = v as u8;
                page.extend_from_slice(&[v, v, v, 255]);
            }
        }
        page
    }

    /// Capture noise for the periodic page, concentrated where it biases
    /// the coarse pass: coherent per-row luma offsets on rows that sit
    /// inside the true hypothesis's correlation window but outside every
    /// larger-dy window, so the true peak scores measurably below the
    /// wrong pitch multiples while staying above the acceptance gate. The
    /// rows also lie outside the true dy's verification strip and above
    /// the appended band, so they neither disturb the strip check nor
    /// reach the composite.
    fn dampen_true_alignment(frame: &mut Frame) {
        let stride = frame.stride();
        for y in 108..130usize {
            let off: i32 = if y % 2 == 0 { 25 } else { -25 };
            for x in 0..W as usize {
                let i = y * stride + x * 4;
                for c in i..i + 3 {
                    frame.bgra[c] = (frame.bgra[c] as i32 + off).clamp(0, 255) as u8;
                }
            }
        }
    }

    #[test]
    fn periodic_lines_register_the_verified_peak_not_the_correlation_maximum() {
        let page = build_periodic_page();
        let mut st = Stitcher::new(render(&page, 0));

        let mut cur = render(&page, 76);
        dampen_true_alignment(&mut cur);

        // The trap must actually be set: the coarse pass ranks some wrong
        // pitch multiple above the true displacement of 76 (two lines),
        // while the true displacement survives as a lesser peak. A search
        // that verified only the global maximum would fail this pair on
        // every retry.
        let cur_profile = profile(&cur, st.c0, st.c1);
        let (ph, pf) = st
            .pair_chrome(&cur)
            .expect("a scrolled pair must not read as static");
        let peaks = st.coarse_peaks(&cur_profile, ph as usize, pf as usize);
        assert!(
            peaks
                .iter()
                .any(|&(dy, _)| (74..=78).contains(&dy)),
            "true dy is not among the coarse peaks: {peaks:?}"
        );
        assert!(
            !(74..=78).contains(&peaks[0].0),
            "the decoy failed: the true dy won the coarse pass outright: {peaks:?}"
        );

        // Verification, offered every peak, picks the true one exactly.
        assert_eq!(append_dy(&mut st, cur), 76);

        // The rest of the scroll — including the next pair, whose
        // reference still carries the noisy rows — and the reconstruction
        // must come out byte-exact: no line lost, none doubled.
        let mut prev = 76;
        while prev < BOTTOM {
            let pos = (prev + 76).min(BOTTOM);
            assert_eq!(append_dy(&mut st, render(&page, pos)), pos - prev, "step to {pos}");
            prev = pos;
        }
        for _ in 0..2 {
            assert_eq!(append_dy(&mut st, render(&page, BOTTOM)), 0);
        }

        let composite = st.finish();
        assert_eq!(composite.quality, Quality::Exact);
        assert_eq!(composite.height, HDR + PAGE_H + FTR);
        assert_eq!(composite.rgba, expected_composite(&page, BOTTOM));
    }

    /// The same trap with a faint discriminator, which is the shape real
    /// lined text takes: the lines are identical except for a line number,
    /// so a strip laid over the *wrong* pitch multiple disagrees only
    /// slightly and passes verification too. Nothing then separates the
    /// hypotheses but which agrees *better* — measured against a 400-line
    /// document on macOS, taking the first passing peak instead registered
    /// ~18 lines out on every step and composited a screen of duplicated
    /// content per seam.
    fn faint_tint(line: u32) -> i32 {
        [0, 2, 4, 6, 8][(line % 5) as usize]
    }

    fn build_faintly_lined_page() -> Vec<u8> {
        let mut page = Vec::with_capacity((PAGE_H * W * 4) as usize);
        for y in 0..PAGE_H {
            let (line, r) = (y / PITCH, y % PITCH);
            for _ in 0..W {
                // Strong structure *within* a line, so the row profiles have
                // real variance to correlate — and a faint per-line tint,
                // which is all that distinguishes one pitch multiple from
                // another. A wrong multiple therefore disagrees by a couple
                // of luma levels: enough to be seen, nowhere near enough to
                // be rejected.
                let v = if r >= LINE_TEXT {
                    230 - faint_tint(line)
                } else {
                    30 + 24 * (r % 8) as i32 + faint_tint(line)
                };
                let v = v as u8;
                page.extend_from_slice(&[v, v, v, 255]);
            }
        }
        page
    }

    #[test]
    fn faintly_lined_text_registers_the_best_agreeing_peak_not_the_first_passing_one() {
        let page = build_faintly_lined_page();
        let mut st = Stitcher::new(render(&page, 0));

        // Two lines: the small step a real wheel notch produces, which is
        // the case that leaves the most ambiguity — every one of the pitch
        // multiples that fits the overlap looks nearly as good.
        let dy_true = 2 * PITCH;
        let mut cur = render(&page, dy_true);
        dampen_true_alignment(&mut cur);

        let cur_profile = profile(&cur, st.c0, st.c1);
        let (ph, pf) = st
            .pair_chrome(&cur)
            .expect("a scrolled pair must not read as static");
        let peaks = st.coarse_peaks(&cur_profile, ph as usize, pf as usize);

        // The trap: a wrong multiple ranks first *and* verifies, so a search
        // that stopped at the first passing peak would return it.
        let first = peaks[0].0;
        assert!(
            !(dy_true as usize - 2..=dy_true as usize + 2).contains(&first),
            "the decoy failed: the true dy won the coarse pass outright: {peaks:?}"
        );
        assert!(
            st.strip_mad(&cur, ph as usize, pf as usize, first) <= VERIFY_MAD_MAX,
            "the decoy failed: the leading wrong peak does not verify, so first-passing would have skipped it"
        );

        // …and the truth is what comes out, exactly.
        assert_eq!(append_dy(&mut st, cur), dy_true);

        let mut prev = dy_true;
        while prev < BOTTOM {
            let pos = (prev + dy_true).min(BOTTOM);
            assert_eq!(append_dy(&mut st, render(&page, pos)), pos - prev, "step to {pos}");
            prev = pos;
        }
        let composite = st.finish();
        assert_eq!(composite.height, HDR + BOTTOM + C_LEN + FTR);
        assert_eq!(composite.rgba, expected_composite(&page, BOTTOM));
    }

    // ── Lined text without chrome ──────────────────────────────────────
    //
    // A plain editor or terminal: every line is the same base pattern on a
    // fixed pitch, no sticky chrome anywhere, and only a small marker
    // block telling the lines apart. Lines five apart differ by a single
    // clipped marker block — under the chrome walk's threshold — so on a
    // five-line scroll the walk reads screenfuls of ordinary content as
    // chrome from both ends. Only the sparse "heading" lines ever break
    // the walk, wherever heading meets body across the pair.

    /// Line pitch and text rows per line of the lined page.
    const LN_PITCH: u32 = 24;
    const LN_TEXT: u32 = 20;

    /// Two 15-column marker blocks per line, lumas walking a cyclic Gray
    /// path on a 3×3 grid ordered so that lines *five* apart sit adjacent
    /// on the cycle: exactly one block changes between them, and
    /// (15/200) · CHROME_CLIP ≈ 1.8 keeps such a row under the walk's
    /// threshold while the unclipped strip sees the full ≥ 115-level jump
    /// — the same row reads as chrome to the walk and screams at the
    /// verifier. Any two distinct codes differ somewhere by ≥ 115, and
    /// the cycle is nine lines, longer than any hypothesis the sweep can
    /// reach, so every whole-line misalignment fails the strip.
    fn line_marker(line: u32) -> (i32, i32) {
        const GRAY: [(i32, i32); 9] = [(0, 0), (0, 1), (0, 2), (1, 2), (1, 0), (1, 1), (2, 1), (2, 2), (2, 0)];
        let (a, b) = GRAY[((line * 2) % 9) as usize];
        (a * 115, b * 115)
    }

    fn build_lined_page() -> Vec<u8> {
        let mut page = Vec::with_capacity((PAGE_H * W * 4) as usize);
        for y in 0..PAGE_H {
            let (line, r) = (y / LN_PITCH, y % LN_PITCH);
            let (ma, mb) = line_marker(line);
            for x in 0..W {
                let v = if r >= LN_TEXT {
                    230
                } else if (60..75).contains(&x) {
                    ma
                } else if (80..95).contains(&x) {
                    mb
                } else {
                    // Every ninth line is a darker "heading" — the only
                    // rows that ever break the walk on this page.
                    let base = 60 + 24 * (r % 8) as i32;
                    if line % 9 == 0 {
                        base - 45
                    } else {
                        base
                    }
                };
                let v = v as u8;
                page.extend_from_slice(&[v, v, v, 255]);
            }
        }
        page
    }

    /// A chromeless viewport: the page slice itself.
    fn render_bare(page: &[u8], pos: u32) -> Frame {
        let stride = (W * 4) as usize;
        Frame {
            bgra: page[pos as usize * stride..(pos + VIEW_H) as usize * stride].to_vec(),
            width: W,
            height: VIEW_H,
        }
    }

    #[test]
    fn lined_text_freezes_the_chrome_an_honest_pair_saw() {
        let page = build_lined_page();
        let mut st = Stitcher::new(render_bare(&page, 0));

        // A two-line step first: marker codes two lines apart differ, so
        // the walk stops at once (line 0 is a heading; only line 9's
        // trailing background rows read as footer) and the pair freezes
        // an honest, near-zero estimate for the chromeless page.
        assert_eq!(append_dy(&mut st, render_bare(&page, 48)), 48);
        assert!(st.chrome_locked);
        assert_eq!((st.header, st.footer), (0, 4));

        // Five-line steps from here: each pair's own walk reads dozens of
        // content rows as chrome, and every one of those readings is
        // discarded — the frozen estimate keeps the band wide enough that
        // the true dy stays expressible in-band.
        let mut prev = 48;
        for pos in [168u32, 288, 408, 528, 648, 768, 888] {
            assert_eq!(append_dy(&mut st, render_bare(&page, pos)), pos - prev, "step to {pos}");
            assert_eq!((st.header, st.footer), (0, 4), "chrome inflated at {pos}");
            prev = pos;
        }
        assert_eq!(append_dy(&mut st, render_bare(&page, 960)), 72);
        for _ in 0..2 {
            assert_eq!(append_dy(&mut st, render_bare(&page, 960)), 0);
        }

        let composite = st.finish();
        assert_eq!(composite.quality, Quality::Exact);
        assert_eq!(composite.height, PAGE_H);
        // The whole page, byte-exact: nothing the over-reading walks saw
        // ever moved a seam.
        assert_eq!(composite.rgba, page);
    }

    #[test]
    fn an_over_read_first_pair_still_measures_via_the_strip_scan() {
        let page = build_lined_page();
        // Framed so the nearest heading sits lines away from both edges:
        // on the first five-line step the walk reads 72 header and 28
        // footer rows of plain content as chrome, and the 140-row band
        // that leaves tops out at dy = 108 — the true 120 px step is not
        // a representable hypothesis in-band.
        let mut st = Stitcher::new(render_bare(&page, 24));
        assert_eq!(append_dy(&mut st, render_bare(&page, 144)), 120);
        assert!(st.chrome_locked);
        assert_eq!((st.header, st.footer), (72, 28));

        // The frozen over-read keeps the band too narrow for every later
        // five-line step too, so each one rides the scan — and the seams
        // still assemble losslessly, because the walk only over-counts:
        // the append line sits in rows that translate with the content.
        let mut prev = 144;
        for pos in [264u32, 384, 504, 624, 744, 864] {
            assert_eq!(append_dy(&mut st, render_bare(&page, pos)), pos - prev, "step to {pos}");
            prev = pos;
        }
        assert_eq!(append_dy(&mut st, render_bare(&page, 960)), 96);
        for _ in 0..2 {
            assert_eq!(append_dy(&mut st, render_bare(&page, 960)), 0);
        }
        assert_eq!((st.header, st.footer), (72, 28));

        let composite = st.finish();
        assert_eq!(composite.quality, Quality::Exact);
        assert_eq!(composite.height, PAGE_H - 24);
        // Byte-exact from the first captured row down: an over-read costs
        // extra work, never rows.
        assert_eq!(composite.rgba[..], page[24 * (W as usize) * 4..]);
    }

    #[test]
    fn registration_failure_after_a_verified_step_still_holds() {
        let page = build_page();
        let mut st = Stitcher::new(render(&page, 0));
        assert_eq!(append_dy(&mut st, render(&page, 130)), 130);

        // A frame unrelated to anything before it: per-pixel random, so
        // its profile has healthy variance but correlates with nothing,
        // and far more of it changed than any bounded animation. A
        // verified step behind it earns no guess — the frame is held and
        // the reference stays where it is.
        let mut rng = Rng::new(0x9e37_79b9);
        let mut garbage = blank(0);
        for px in garbage.bgra.chunks_exact_mut(4) {
            let v = rng.below(256) as u8;
            px[..3].copy_from_slice(&[v, v, v]);
        }
        assert!(matches!(st.append(garbage), AppendResult::Hold));
        assert_eq!(st.frames(), 2);

        let composite = st.finish();
        // The held frame contributed nothing; ending on it means the
        // composite stops short of what was on screen.
        assert_eq!(composite.quality, Quality::Partial);
        assert_eq!(composite.height, VIEW_H + 130);
    }

    /// An animated block mid-viewport: contents are a pure function of
    /// `phase`, so consecutive frames differ inside the block and nowhere
    /// else. It spans the verification strip's rows and most of the
    /// matching band's columns — enough churn to defeat the NCC gate and
    /// the strip check both — while staying under [`MOVED_FRACTION_MIN`]
    /// of the content band.
    fn paint_animation(frame: &mut Frame, phase: u64) {
        let mut rng = Rng::new(0xA111_0000 + phase);
        let stride = frame.stride();
        for y in 100..150usize {
            for x in 60..240usize {
                let v = rng.below(256) as u8;
                let i = y * stride + x * 4;
                frame.bgra[i..i + 3].copy_from_slice(&[v, v, v]);
            }
        }
    }

    #[test]
    fn an_animated_block_at_the_page_bottom_still_ends_via_zero_dy() {
        // Scroll a page to its bottom, then keep wheeling while a
        // video/GIF plays in the viewport. The page no longer moves, but
        // every consecutive pair differs inside the animation, so plain
        // registration fails; the did-not-move hypothesis is what turns
        // each of those failures into the `dy == 0` the driver's end
        // detection needs, instead of holds that would stop the run at
        // the driver's cap with the bottom "unfinished".
        let page = build_page();
        let positions = [130u32, 260, 390, 520, 650, 780, 910, BOTTOM];
        let mut st = Stitcher::new(render(&page, 0));
        let mut prev = 0;
        for pos in positions {
            assert_eq!(append_dy(&mut st, render(&page, pos)), pos - prev, "step to {pos}");
            prev = pos;
        }

        // Bottom reached; the animation churns on. Every append must
        // report dy == 0 so the driver's end detection (two consecutive
        // zeros) fires — termination by dy == 0, not by the frame, height
        // or time caps.
        for phase in 0..4 {
            let mut frame = render(&page, BOTTOM);
            paint_animation(&mut frame, phase);
            assert_eq!(append_dy(&mut st, frame), 0, "animated frame at phase {phase}");
        }

        // Nothing was appended for the animated frames: the composite is
        // the exact reconstruction, not the bottom screenful duplicated
        // four more times.
        assert_eq!(st.height(), HDR + PAGE_H + FTR);
        let composite = st.finish();
        assert_eq!(composite.quality, Quality::Exact);
        assert_eq!(composite.rgba, expected_composite(&page, BOTTOM));
    }

    #[test]
    fn a_persistently_unverifiable_stream_only_ever_holds() {
        // Frames of pure noise: nearly every row reads as moved, so the
        // did-not-move hypothesis correctly rejects them, yet nothing ever
        // verifies. Every one must be held — an unverified seam never
        // reaches the composite — and it is the driver's consecutive-hold
        // cap that ends such a stream, keeping everything verified so far.
        let page = build_page();
        let mut st = Stitcher::new(render(&page, 0));
        assert_eq!(append_dy(&mut st, render(&page, 130)), 130);

        let mut rng = Rng::new(0x0dd_ba11);
        let mut garbage = || {
            let mut f = blank(0);
            for px in f.bgra.chunks_exact_mut(4) {
                let v = rng.below(256) as u8;
                px[..3].copy_from_slice(&[v, v, v]);
            }
            f
        };
        for i in 0..4 {
            assert!(matches!(st.append(garbage()), AppendResult::Hold), "hold #{i}");
        }

        // Holding discards nothing already verified, and adds nothing
        // unverified: the composite is exactly the two registered frames,
        // disclosed as stopping short.
        assert_eq!(st.frames(), 2);
        let composite = st.finish();
        assert_eq!(composite.quality, Quality::Partial);
        assert_eq!(composite.height, VIEW_H + 130);
    }

    #[test]
    fn a_malformed_frame_fails() {
        let page = build_page();
        let mut st = Stitcher::new(render(&page, 0));
        let narrow = Frame {
            bgra: vec![0; ((W - 1) * VIEW_H * 4) as usize],
            width: W - 1,
            height: VIEW_H,
        };
        assert!(matches!(st.append(narrow), AppendResult::Failed));
    }

    #[test]
    fn finish_swaps_channels() {
        let st = Stitcher::new(Frame {
            bgra: vec![1, 2, 3, 255],
            width: 1,
            height: 1,
        });
        assert_eq!(st.finish().rgba, vec![3, 2, 1, 255]);
    }
}
