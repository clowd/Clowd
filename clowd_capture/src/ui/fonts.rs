//! Every font the overlay draws with, and the background scan that finds the
//! system faces the bundled ones do not cover.
//!
//! Two things live here. The bundled Cascadia Mono faces and the
//! [`egui::FontDefinitions`] built from them — Mono rather than Code because
//! Code ships `calt` ligatures, which would turn "->" in a label into an
//! arrow and corrupt recognised OCR text. And the one process-wide scan of
//! the machine's own fonts, which supplies the faces egui falls back to for
//! scripts Cascadia lacks (CJK, kana, Hangul, Indic, Thai, symbols, emoji).
//!
//! The scan is deliberately late and deliberately cheap. It starts at the
//! first OCR press, on a background-priority thread, because this overlay is
//! startup-latency-sensitive and a cold page cache makes the scan disk-bound
//! for seconds. Its curated faces are memory-mapped rather than read, so the
//! bytes stay in the page cache instead of becoming tens of megabytes of
//! private heap in a process that spends most of its life warm and idle, and
//! they are shared by every host's context rather than copied per context.

use std::borrow::Cow;
use std::sync::{Arc, LazyLock, Once, OnceLock};
use std::time::{Duration, Instant};

use egui::FontFamily;

use crate::sync::Latch;

pub const MONO_REGULAR: &[u8] = include_bytes!("../../assets/fonts/CascadiaMono-Regular.ttf");
pub const MONO_BOLD_BYTES: &[u8] = include_bytes!("../../assets/fonts/CascadiaMono-Bold.ttf");

/// Key of the regular Cascadia Mono face in [`egui::FontDefinitions::font_data`].
pub const MONO_REGULAR_NAME: &str = "CascadiaMono";
/// Family key for the bold Cascadia Mono face. egui picks a face per family,
/// not per weight, so bold is a family of its own.
pub const MONO_BOLD_NAME: &str = "CascadiaMonoBold";
pub static MONO_BOLD: LazyLock<FontFamily> = LazyLock::new(|| FontFamily::Name(Arc::from(MONO_BOLD_NAME)));

/// One curated system face: the key it gets in egui's font table, and its
/// bytes. The bytes are a leaked memory map, so the `Arc<FontData>` is shared
/// by every monitor's context instead of each deep-copying the file.
pub struct SystemFace {
    pub name: String,
    pub data: Arc<egui::FontData>,
}

pub type SystemFaces = Arc<Vec<SystemFace>>;

/// The bundled Cascadia Mono faces, with `extra` appended to every family as
/// per-glyph fallback. ASCII therefore always comes out of Cascadia and the
/// system faces are reached only for what it lacks; the order of `extra` is
/// the whole fallback policy, because egui takes the first family in the list
/// that has the glyph.
pub fn font_definitions(extra: &[SystemFace]) -> egui::FontDefinitions {
    let mut d = egui::FontDefinitions::empty();
    d.font_data
        .insert(MONO_REGULAR_NAME.into(), Arc::new(egui::FontData::from_static(MONO_REGULAR)));
    d.font_data
        .insert(MONO_BOLD_NAME.into(), Arc::new(egui::FontData::from_static(MONO_BOLD_BYTES)));
    for face in extra {
        d.font_data
            .insert(face.name.clone(), face.data.clone());
    }
    let chain = |first: &str| {
        let mut names = Vec::with_capacity(1 + extra.len());
        names.push(first.to_owned());
        names.extend(extra.iter().map(|f| f.name.clone()));
        names
    };
    d.families
        .insert(FontFamily::Monospace, chain(MONO_REGULAR_NAME));
    // Both built-in family keys must exist even though we only draw mono:
    // egui resolves Proportional for anything it lays out itself.
    d.families
        .insert(FontFamily::Proportional, chain(MONO_REGULAR_NAME));
    d.families
        .insert(MONO_BOLD.clone(), chain(MONO_BOLD_NAME));
    d
}

/// Windows family names that cover the scripts Cascadia Mono does not.
/// Cascadia already carries Latin, Greek, Cyrillic, Arabic and Hebrew
/// letters, so no general UI face is needed here; the order is the fallback
/// policy.
#[cfg(windows)]
const FALLBACK_FAMILIES: &[&str] = &[
    "Microsoft YaHei UI",
    "Yu Gothic UI",
    "Malgun Gothic",
    "Nirmala UI",
    "Leelawadee UI",
    "Segoe UI Symbol",
    "Segoe UI Emoji",
];

/// The same list for macOS. Written blind: this backend cannot be compiled or
/// run here, and a name that does not resolve is simply skipped, so the worst
/// case is the tofu the old path would also have shown.
#[cfg(target_os = "macos")]
const FALLBACK_FAMILIES: &[&str] = &[
    "PingFang SC",
    "Hiragino Sans",
    "Apple SD Gothic Neo",
    "Kohinoor Devanagari",
    "Thonburi",
    "Apple Symbols",
];

#[cfg(not(any(windows, target_os = "macos")))]
const FALLBACK_FAMILIES: &[&str] = &[];

/// Walk the machine's fonts and memory-map the curated fallback faces.
///
/// Runs on the scan thread only. Each face is probed with skrifa before it is
/// kept, because that is the parser egui shapes with and `FontsImpl::new`
/// panics on the app thread for any face it cannot read — a face this process
/// cannot use must never reach a context.
fn scan() -> SystemFaces {
    let t0 = Instant::now();
    let mut db = fontdb::Database::new();
    db.load_system_fonts();
    let total = db.faces().count();

    let mut faces: Vec<SystemFace> = Vec::with_capacity(FALLBACK_FAMILIES.len());
    for family in FALLBACK_FAMILIES {
        let query = fontdb::Query {
            families: &[fontdb::Family::Name(family)],
            weight: fontdb::Weight::NORMAL,
            stretch: fontdb::Stretch::Normal,
            style: fontdb::Style::Normal,
        };
        let Some(id) = db.query(&query) else {
            continue;
        };
        // SAFETY: a system font file is not rewritten in place while the OS
        // has it registered, which is the same assumption the shaper this
        // replaced made for every face it rasterised.
        let Some((shared, index)) = (unsafe { db.make_shared_face_data(id) }) else {
            continue;
        };
        // The map outlives the process on purpose: egui's `FontData` borrows
        // `&'static [u8]`, and there is exactly one of these per curated
        // family per process.
        let leaked: &'static Arc<dyn AsRef<[u8]> + Send + Sync> = Box::leak(Box::new(shared));
        let bytes: &'static [u8] = (**leaked).as_ref();
        if skrifa::FontRef::from_index(bytes, index).is_err() {
            log::warn!("skipping system font {family} (face {index}): skrifa cannot parse it");
            continue;
        }
        faces.push(SystemFace {
            name: format!("sys:{family}"),
            data: Arc::new(egui::FontData {
                font: Cow::Borrowed(bytes),
                index,
                tweak: Default::default(),
            }),
        });
    }
    log::info!(
        "system font scan: {total} faces, {} fallback faces mapped in {:?}",
        faces.len(),
        t0.elapsed()
    );
    Arc::new(faces)
}

/// The one process-wide scan slot. A [`Latch`] rather than a `OnceLock`
/// because the OCR worker thread needs a blocking-with-timeout wait
/// ([`wait_for_system_font_scan`]) while the app thread needs a non-blocking
/// peek ([`system_faces`]).
fn latch() -> &'static Latch<SystemFaces> {
    static LATCH: OnceLock<Latch<SystemFaces>> = OnceLock::new();
    LATCH.get_or_init(Latch::new)
}

/// Start the system-font scan on a background thread, once per process.
///
/// Called at the first OCR press, overlapping the scan with the recognizer
/// child's own cold start — by decision, nothing OCR-related loads before OCR
/// is actually used, so capture startup never pays for a rarely-used feature.
/// Background priority, not merely below-normal: the scan is disk-bound on a
/// cold page cache (font files plus on-access scanning) and only the
/// background tier lowers I/O priority too.
pub fn begin_system_font_scan() {
    static STARTED: Once = Once::new();
    STARTED.call_once(|| {
        let spawned = std::thread::Builder::new()
            .name("font-scan".into())
            .spawn(|| {
                crate::system::background_thread_priority();
                latch().set(scan());
            });
        if let Err(e) = spawned {
            log::warn!("failed to spawn the font-scan thread: {e}");
        }
    });
}

/// Block until the scan lands, or `timeout`. For the OCR worker thread only,
/// and only for a page that actually needs a fallback face: it holds the
/// recognition result back so the reveal never lays non-Latin lines out
/// against a Cascadia-only font set. The app thread and the render threads
/// must never call this.
pub fn wait_for_system_font_scan(timeout: Duration) {
    if latch().wait_timeout(timeout).is_none() {
        log::warn!("system font scan did not finish within {timeout:?}; OCR bubbles may lack non-ASCII glyphs");
    }
}

/// The curated faces, or `None` while the scan is still running.
pub fn system_faces() -> Option<SystemFaces> {
    latch().try_get()
}

/// Whether any non-whitespace character of `lines` is missing from the bundled
/// Cascadia Mono.
///
/// Reads the embedded bytes with skrifa, so it answers on any thread with no
/// egui context in sight — which is what lets the OCR worker decide whether to
/// wait for the scan at all, and lets an ASCII page skip the font install
/// entirely. A face that will not parse is reported as needing fallback, since
/// that is the safe direction.
pub fn needs_fallback<'a>(lines: impl IntoIterator<Item = &'a str>) -> bool {
    use skrifa::MetadataProvider;
    let Ok(font) = skrifa::FontRef::new(MONO_REGULAR) else {
        return true;
    };
    let cmap = font.charmap();
    lines
        .into_iter()
        .flat_map(str::chars)
        .any(|c| !c.is_whitespace() && cmap.map(c).is_none())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn definitions_bind_monospace_proportional_and_bold() {
        let d = font_definitions(&[]);
        assert!(d.font_data.contains_key(MONO_REGULAR_NAME));
        assert!(d.font_data.contains_key(MONO_BOLD_NAME));
        assert_eq!(d.families[&FontFamily::Monospace], vec![MONO_REGULAR_NAME.to_owned()]);
        assert_eq!(d.families[&FontFamily::Proportional], vec![MONO_REGULAR_NAME.to_owned()]);
        assert_eq!(d.families[&MONO_BOLD.clone()], vec![MONO_BOLD_NAME.to_owned()]);
    }

    /// The bundled face stays first in every family and the extras follow in
    /// the order the scan chose: that order is the fallback policy.
    #[test]
    fn extra_faces_are_appended_after_cascadia_in_order() {
        let extra = vec![
            SystemFace {
                name: "sys:First".to_owned(),
                data: Arc::new(egui::FontData::from_static(MONO_REGULAR)),
            },
            SystemFace {
                name: "sys:Second".to_owned(),
                data: Arc::new(egui::FontData::from_static(MONO_BOLD_BYTES)),
            },
        ];
        let d = font_definitions(&extra);
        assert_eq!(
            d.families[&FontFamily::Monospace],
            vec!["CascadiaMono".to_owned(), "sys:First".to_owned(), "sys:Second".to_owned()]
        );
        assert_eq!(
            d.families[&MONO_BOLD.clone()],
            vec![MONO_BOLD_NAME.to_owned(), "sys:First".to_owned(), "sys:Second".to_owned()]
        );
        assert!(d.font_data.contains_key("sys:First"));
        assert!(d.font_data.contains_key("sys:Second"));
    }

    #[test]
    fn needs_fallback_is_false_for_ascii_and_true_for_cjk() {
        assert!(!needs_fallback(["Select #1A2B3C", "  W \u{00D7} H  "]));
        assert!(needs_fallback(["hello", "\u{4F60}\u{597D}"]));
        assert!(!needs_fallback(std::iter::empty::<&str>()));
    }

    /// egui builds its atlas with skrifa and panics on a face it cannot read,
    /// so the bundled bytes have to parse before anything else is worth
    /// testing.
    #[test]
    fn the_bundled_face_parses_with_skrifa() {
        assert!(skrifa::FontRef::new(MONO_REGULAR).is_ok());
        assert!(skrifa::FontRef::new(MONO_BOLD_BYTES).is_ok());
    }

    /// Perf probe, kept as the record of why the scan lives on a background
    /// thread and why nothing on a render thread may ever call
    /// `load_system_fonts`: ~11 ms over 363 faces warm on the dev box (fontdb
    /// only parses name tables), but seconds from a cold page cache. Prints
    /// with --nocapture; asserts only a sanity bound.
    ///
    /// The bound is deliberately enormous relative to that measurement, and it
    /// has to be: a hosted Windows runner was seen taking 5.11 s over 176
    /// faces — three orders of magnitude off the dev box, from cold disk and
    /// on-access scanning rather than from anything in this code. What the
    /// assertion is for is a catastrophic regression (a scan that walks glyph
    /// tables, or rescans per frame), and that shows up as minutes, not as the
    /// difference between 5 and 30 seconds. Timing the machine instead of the
    /// code is how a probe becomes a flake, so read the printed number, not
    /// the bound.
    #[test]
    fn probe_system_font_load_cost() {
        let mut db = fontdb::Database::new();
        let t = Instant::now();
        db.load_system_fonts();
        eprintln!("load_system_fonts: {} faces in {:?}", db.faces().count(), t.elapsed());
        assert!(t.elapsed().as_secs() < 60, "system font scan took {:?}", t.elapsed());
    }

    /// The whole curated path end to end: scan the machine, install the faces
    /// on a real context and run one pass. Building `FontsImpl` is where a
    /// face egui cannot parse panics, so the pass is the assertion.
    #[cfg(windows)]
    #[test]
    fn probe_system_font_scan() {
        let t = Instant::now();
        let faces = scan();
        eprintln!("curated scan: {} faces in {:?}", faces.len(), t.elapsed());
        assert!(t.elapsed().as_secs() < 60, "system font scan took {:?}", t.elapsed());
        let ctx = egui::Context::default();
        ctx.set_fonts(font_definitions(&faces));
        let mut out = ctx.run_ui(egui::RawInput::default(), |ui| {
            ui.label("\u{4F60}\u{597D} hello");
        });
        assert!(!out.textures_delta.set.is_empty(), "the first pass uploads the atlas");
        // epaint panics on a dropped delta: in the app a painter applies it.
        out.textures_delta.clear();
    }
}
