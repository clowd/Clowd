//! macOS platform implementation (DESIGN §2.2) — the recorder-specific
//! remainder: cursor/mouse sampling and audio/webcam helpers. The monitor /
//! paths / display-capture layer moved to the shared `obs-platform` crate
//! (SHARE_REGION_PLAN §4.3). Coordinates are CG points (§1.1 capture space).

use obs::data::ObsData;
use objc2_core_graphics::{CGEvent, CGEventSource, CGEventSourceStateID, CGMouseButton};

use crate::cursor_sprite::SpriteEvent;

use super::{CursorKind, CursorState, MouseInfo};

pub const AUDIO_INPUT_CAPTURE_ID: &str = "coreaudio_input_capture";
/// Webcam capture source (`--webcam` / `--list-cameras`): AVFoundation. The
/// async ("macos-avcapture") variant rather than the fast path: it delivers
/// frames without needing the source to be "showing" in a rendered scene.
pub const WEBCAM_SOURCE_ID: &str = "macos-avcapture";
/// The `WEBCAM_SOURCE_ID` settings key (and property) holding the device id
/// (an `AVCaptureDevice.uniqueID`).
pub const WEBCAM_DEVICE_KEY: &str = "device";

/// `CGEvent::new(None)` (CGEventCreate) snapshots the current event state; its
/// location is the cursor position in global display coordinates (points).
/// The `CFRetained` handle follows the CF Create rule and releases on drop.
fn cursor_location() -> (f64, f64) {
    match CGEvent::new(None) {
        Some(event) => {
            let p = CGEvent::location(Some(&event));
            (p.x, p.y)
        }
        None => (0.0, 0.0),
    }
}

/// Cursor position in global display points (the same space as
/// `CGDisplayBounds`, hence as `MonitorInfo` and `--region`) plus the
/// left/right button state.
///
/// `scale` is 1.0: unlike Windows physical pixels, points are already
/// density-independent, so the highlight needs no DPI compensation here (the
/// region planner separately scales points → canvas pixels).
pub fn get_mouse_info() -> MouseInfo {
    // `CombinedSessionState` is the session's combined state, which includes
    // synthesized clicks (the closest analogue to Win32's `GetAsyncKeyState`).
    // `CGEventSourceButtonState` reads it without an event tap, so no
    // Accessibility / Input Monitoring permission is involved.
    let pressed = CGEventSource::button_state(
        CGEventSourceStateID::CombinedSessionState,
        CGMouseButton::Left,
    ) || CGEventSource::button_state(
        CGEventSourceStateID::CombinedSessionState,
        CGMouseButton::Right,
    );

    let (x, y) = cursor_location();

    MouseInfo {
        x,
        y,
        pressed,
        scale: 1.0,
    }
}

/// Position from the same CGEvent snapshot as `get_mouse_info`, plus the
/// classified cursor shape (see [`cursor_shape`] for how that is identified
/// and why it is cheap enough to do here, on the graphics thread).
pub fn get_cursor_state() -> CursorState {
    let (x, y) = cursor_location();
    // Checked before classifying: a hidden cursor still has a shape, and
    // reporting it would make the editor composite a pointer over content
    // where macOS was drawing nothing.
    //
    // `CGCursorIsVisible` is deprecated since 10.9 but still the only public
    // answer, and it needs no permission — the alternative is private CGS SPI.
    #[allow(deprecated)]
    let visible = objc2_core_graphics::CGCursorIsVisible();
    let kind = if visible {
        cursor_shape::current()
    } else {
        CursorKind::Hidden
    };
    // Points are fractional here (Windows' GetCursorInfo is integral), so round
    // rather than truncate — truncation biases toward zero and would skew
    // negative coordinates on displays left of / above the primary.
    CursorState {
        x: x.round() as i32,
        y: y.round() as i32,
        kind,
        // No cursor handle exists on macOS: identity lives in the classifier's
        // seed gate, which `take_cursor_sprite` piggybacks on.
        handle: 0,
    }
}

/// Rasterizes the current cursor into a sprite event, piggybacking the
/// classifier's seed gate: `Unchanged` while the seed holds, a fresh PNG
/// sprite only on the ticks where the cursor actually changed (the same
/// frames that already pay for a full classify). Hidden state comes from the
/// sampled `CursorState`, i.e. the existing `CGCursorIsVisible` check.
pub fn take_cursor_sprite(state: &CursorState) -> SpriteEvent {
    cursor_shape::take_sprite(state.kind)
}

/// Identifies the active system cursor by matching its image against the stock
/// `NSCursor` set.
///
/// Windows gets a comparable `HCURSOR` straight from `GetCursorInfo`; macOS has
/// no such handle, so the only reliable identity is the cursor's own bitmap.
/// Hashing it costs ~470 µs — far too much to repeat every rendered frame — so
/// the work is gated on `CGSCurrentCursorSeed`, a change counter that costs
/// ~2 ns to read. Steady state is therefore a couple of nanoseconds per frame,
/// and the full classify runs only on the frames where the cursor actually
/// changed. That is why this needs no sampling thread of its own.
///
/// The seed is private CoreGraphics SPI, so it is resolved with `dlsym` and
/// absence is not fatal: without it the classify is time-gated to
/// [`RESAMPLE_INTERVAL`] instead, trading exactness for the same bounded cost.
///
/// Measured on macOS 15.7 (arm64): off-main-thread calls are fine, which
/// matters because this runs on the OBS graphics thread.
mod cursor_shape {
    use std::ffi::{c_char, c_void, CString};
    use std::sync::{Mutex, OnceLock};
    use std::time::{Duration, Instant};

    use objc2::rc::{autoreleasepool, AutoreleaseSafe, Retained};
    use objc2::MainThreadMarker;
    use objc2_app_kit::{
        NSApplication, NSApplicationActivationPolicy, NSBitmapImageFileType, NSBitmapImageRep,
        NSCursor, NSImage, NSImageRep,
    };
    use objc2_foundation::NSDictionary;

    use super::CursorKind;
    use crate::cursor_sprite::{RawSprite, SpriteEvent, SpritePixels};

    /// How stale a sample may get when the seed is unavailable. At 60 fps this
    /// caps the cost of the fallback path at roughly 0.5% of one core.
    const RESAMPLE_INTERVAL: Duration = Duration::from_millis(100);

    const RTLD_DEFAULT: *mut c_void = -2isize as *mut c_void;

    // `CGSCurrentCursorSeed` is private CGS SPI, so no ecosystem crate binds
    // it; `dlsym` (libSystem) is the only way to reach it.
    extern "C" {
        fn dlsym(handle: *mut c_void, symbol: *const c_char) -> *mut c_void;
    }

    /// Runs `f` inside an autorelease pool (`objc2::rc::autoreleasepool`).
    ///
    /// Nothing here executes on an AppKit-managed thread — the tick callback is
    /// libobs's graphics thread — so there is no ambient pool to catch what
    /// AppKit autoreleases. `TIFFRepresentation` and the PNG encode return
    /// autoreleased objects holding the cursor bitmap at every representation
    /// size; without a pool each capture strands them. Measured at ~750 KB per
    /// cursor change on macOS 15.7, which over a long recording is unbounded
    /// growth rather than a fixed overhead.
    fn autoreleased<T, F: FnOnce() -> T + AutoreleaseSafe>(f: F) -> T {
        autoreleasepool(|_| f())
    }

    /// AppKit needs an `NSApplication` to exist before `+[NSCursor
    /// arrowCursor]` and `IBeamCursor` will resolve — they return nil in a bare
    /// CLI process, while the other stock cursors work either way. Those two
    /// are the most common cursors on screen, so the bootstrap is not optional.
    ///
    /// Done lazily, so a run without `--input-capture` never creates it, and
    /// pinned to the prohibited activation policy so the recorder cannot
    /// acquire a Dock icon or menu bar by side effect.
    fn ensure_appkit() {
        static INIT: OnceLock<()> = OnceLock::new();
        INIT.get_or_init(|| {
            // objc2 gates `NSApplication` behind `MainThreadMarker`, but this
            // runs on the OBS graphics thread — where the pre-objc2 msgSend
            // always made this call, measured safe (module doc). Mint the
            // marker unchecked off-main rather than introduce a panic or skip
            // path the hand-rolled version did not have.
            let mtm = MainThreadMarker::new()
                .unwrap_or_else(|| unsafe { MainThreadMarker::new_unchecked() });
            let app = NSApplication::sharedApplication(mtm);
            // The success flag was always ignored (the raw msgSend result was
            // dropped); keep that.
            let _ = app.setActivationPolicy(NSApplicationActivationPolicy::Prohibited);
        });
    }

    /// FNV-1a over an `NSCursor`'s image bytes. `None` when the image or the
    /// encode is unavailable.
    fn hash_cursor(cursor: &NSCursor) -> Option<u64> {
        let data = cursor.image().TIFFRepresentation()?;
        // SAFETY: the freshly encoded NSData is not mutated while the slice is
        // borrowed.
        let bytes = unsafe { data.as_bytes_unchecked() };
        if bytes.is_empty() {
            return None;
        }
        let mut h: u64 = 0xcbf2_9ce4_8422_2325;
        for &b in bytes {
            h ^= b as u64;
            h = h.wrapping_mul(0x1000_0000_01b3);
        }
        Some(h)
    }

    /// Stock `NSCursor` class methods paired with the wire kind they mean.
    ///
    /// Only unambiguous mappings are listed. macOS has no stock cursor for
    /// `Wait`/`AppStarting` (the beachball is not an `NSCursor`), `Help`,
    /// `Pen`, `Person` or `UpArrow`, and exposes the diagonal resize cursors
    /// only as private SPI, so `SizeNwse`/`SizeNesw` are unreachable too —
    /// anything unmatched falls through to `Custom`, which is exactly what
    /// those cases are from the wire contract's point of view.
    //
    // The `resize*Cursor` set is deprecated in favor of the directional
    // variants, but the deprecated ones are what running apps still set, so
    // the table must keep matching their images.
    type StockCursorFn = fn() -> Retained<NSCursor>;
    #[allow(deprecated)]
    const STOCK: [(StockCursorFn, CursorKind); 14] = [
        (NSCursor::arrowCursor, CursorKind::Arrow),
        (NSCursor::IBeamCursor, CursorKind::IBeam),
        (NSCursor::IBeamCursorForVerticalLayout, CursorKind::IBeam),
        (NSCursor::crosshairCursor, CursorKind::Cross),
        (NSCursor::pointingHandCursor, CursorKind::Hand),
        (NSCursor::operationNotAllowedCursor, CursorKind::No),
        (NSCursor::resizeLeftRightCursor, CursorKind::SizeWe),
        (NSCursor::resizeUpDownCursor, CursorKind::SizeNs),
        (NSCursor::resizeLeftCursor, CursorKind::SizeWe),
        (NSCursor::resizeRightCursor, CursorKind::SizeWe),
        (NSCursor::resizeUpCursor, CursorKind::SizeNs),
        (NSCursor::resizeDownCursor, CursorKind::SizeNs),
        // The pan cursors are the closest thing macOS has to SizeAll.
        (NSCursor::openHandCursor, CursorKind::SizeAll),
        (NSCursor::closedHandCursor, CursorKind::SizeAll),
    ];

    /// Hashes of the stock cursors, built once (~7 ms) on first classify.
    fn stock_table() -> &'static Vec<(u64, CursorKind)> {
        static TABLE: OnceLock<Vec<(u64, CursorKind)>> = OnceLock::new();
        TABLE.get_or_init(|| {
            ensure_appkit();
            autoreleased(|| {
                let mut out = Vec::with_capacity(STOCK.len());
                for (cursor_fn, kind) in STOCK {
                    if let Some(h) = hash_cursor(&cursor_fn()) {
                        // First mapping wins, so the aliases above cannot
                        // displace the canonical kind for a shared image.
                        if !out.iter().any(|(hh, _)| *hh == h) {
                            out.push((h, kind));
                        }
                    }
                }
                out
            })
        })
    }

    /// The private change counter, or `None` if this macOS build lacks it.
    fn seed() -> Option<i32> {
        static SEED_FN: OnceLock<Option<unsafe extern "C" fn() -> i32>> = OnceLock::new();
        let f = *SEED_FN.get_or_init(|| unsafe {
            let name = CString::new("CGSCurrentCursorSeed").unwrap();
            let p = dlsym(RTLD_DEFAULT, name.as_ptr());
            if p.is_null() {
                None
            } else {
                Some(std::mem::transmute::<
                    *mut c_void,
                    unsafe extern "C" fn() -> i32,
                >(p))
            }
        });
        f.map(|f| unsafe { f() })
    }

    fn classify() -> CursorKind {
        ensure_appkit();
        let table = stock_table();
        // `currentSystemCursor` deprecation: see the `None` arm below.
        #[allow(deprecated)]
        let hash = autoreleased(|| NSCursor::currentSystemCursor().and_then(|c| hash_cursor(&c)));
        match hash {
            Some(h) => table
                .iter()
                .find(|(hh, _)| *hh == h)
                .map(|(_, k)| *k)
                .unwrap_or(CursorKind::Custom),
            // No cursor or no readable image: report the fallback kind rather
            // than invent a shape.
            //
            // This is the path Apple has signposted. `currentSystemCursor` is
            // deprecated, and the AppKit header is unusually blunt about it —
            // "This property will always be `nil` in a future version of
            // macOS". The suggested replacement is ScreenCaptureKit's
            // `SCStreamConfiguration.showsCursor`, which composites the cursor
            // into the captured pixels and reports no shape at all, so there is
            // no supported successor for classification. When that day comes
            // this falls back to reporting `arrow` for every visible sample,
            // which is precisely the behavior this function replaced — a
            // degradation, not a break.
            None => CursorKind::Arrow,
        }
    }

    struct Cache {
        seed: Option<i32>,
        sampled_at: Instant,
        kind: CursorKind,
    }

    static CACHE: Mutex<Option<Cache>> = Mutex::new(None);

    /// The current cursor kind, classified at most once per actual change.
    pub fn current() -> CursorKind {
        let now_seed = seed();
        // Poisoning cannot corrupt this: the cache is pure memoisation, so the
        // worst a panicking holder leaves behind is a stale kind.
        let mut cache = CACHE.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(c) = cache.as_ref() {
            let fresh = match (now_seed, c.seed) {
                (Some(now), Some(then)) => now == then,
                _ => c.sampled_at.elapsed() < RESAMPLE_INTERVAL,
            };
            if fresh {
                return c.kind;
            }
        }
        let kind = classify();
        *cache = Some(Cache {
            seed: now_seed,
            sampled_at: Instant::now(),
            kind,
        });
        kind
    }

    /// Sprite-side twin of [`Cache`]: its own seed snapshot, so classify and
    /// sprite capture can be called independently without stealing each
    /// other's change edges.
    struct SpriteCache {
        seed: Option<i32>,
        sampled_at: Instant,
    }

    static SPRITE_CACHE: Mutex<Option<SpriteCache>> = Mutex::new(None);

    /// The current cursor as a sprite event, captured at most once per actual
    /// change (the same seed gate as [`current`], with the same
    /// [`RESAMPLE_INTERVAL`] fallback when the seed SPI is absent).
    pub fn take_sprite(kind: CursorKind) -> SpriteEvent {
        if kind == CursorKind::Hidden {
            // A `Hidden` event makes the writer drop its `ci` ref, and only a
            // fresh `Candidate` can restore it — so the seed cache must be
            // dropped too. The seed does not necessarily change across a
            // hide/unhide, and a stale cache would read as `Unchanged` and
            // pin the ref absent until the cursor next changes shape.
            *SPRITE_CACHE.lock().unwrap_or_else(|e| e.into_inner()) = None;
            return SpriteEvent::Hidden;
        }
        let now_seed = seed();
        {
            let cache = SPRITE_CACHE.lock().unwrap_or_else(|e| e.into_inner());
            if let Some(c) = cache.as_ref() {
                let fresh = match (now_seed, c.seed) {
                    (Some(now), Some(then)) => now == then,
                    _ => c.sampled_at.elapsed() < RESAMPLE_INTERVAL,
                };
                if fresh {
                    return SpriteEvent::Unchanged;
                }
            }
        }
        // The cache only advances once a sprite was actually produced; a
        // failed capture clears it instead, so the next tick retries rather
        // than reporting `Unchanged` for a sprite the writer never received.
        let (event, cache) = match capture_sprite(kind) {
            Some(s) => (
                SpriteEvent::Candidate(s),
                Some(SpriteCache {
                    seed: now_seed,
                    sampled_at: Instant::now(),
                }),
            ),
            // Unreadable cursor (the `currentSystemCursor` deprecation path):
            // report unavailable so frame rows drop their `ci` ref, rather
            // than `Unchanged`, which would pin them to a stale sprite.
            None => (SpriteEvent::Hidden, None),
        };
        *SPRITE_CACHE.lock().unwrap_or_else(|e| e.into_inner()) = cache;
        event
    }

    /// Reads `currentSystemCursor` into a PNG sprite. AppKit's own PNG encode
    /// (`representationUsingType:`) yields canonical straight-alpha bytes, so
    /// unlike Windows there is no raw plane for the writer to encode — and the
    /// cost sits behind the seed gate, exactly like the classify. macOS
    /// cursors are plain alpha bitmaps: `mask` is always `None`.
    fn capture_sprite(kind: CursorKind) -> Option<RawSprite> {
        ensure_appkit();
        autoreleased(|| {
            // `currentSystemCursor` deprecation: see `classify`.
            #[allow(deprecated)]
            let cursor = NSCursor::currentSystemCursor()?;
            let image = cursor.image();
            let size = image.size();
            let rep = pick_rep(&image, size.width * target_backing_scale())?;
            // Cursor reps are bitmap reps in practice; anything else has no
            // `representationUsingType:` (the raw msgSend this replaces would
            // have thrown on it), so a failed downcast is a failed capture.
            let rep = rep.downcast::<NSBitmapImageRep>().ok()?;

            // The empty properties dictionary is the typed spelling of the nil
            // the ObjC API accepts: no encode options either way.
            // SAFETY: an empty dictionary trivially has correctly-typed
            // contents.
            let png = unsafe {
                rep.representationUsingType_properties(
                    NSBitmapImageFileType::PNG,
                    &NSDictionary::new(),
                )
            }?;
            // SAFETY: the freshly encoded NSData is not mutated while the
            // slice is borrowed.
            let bytes = unsafe { png.as_bytes_unchecked() };
            if bytes.is_empty() {
                return None;
            }
            let w = rep.pixelsWide() as u32;
            let h = rep.pixelsHigh() as u32;
            if w == 0 || h == 0 {
                return None;
            }
            // hotSpot is in points; the sprite is pixel-sized, so scale by the
            // chosen representation's pixels-per-point ratio.
            let hot = cursor.hotSpot();
            let sx = if size.width > 0.0 {
                w as f64 / size.width
            } else {
                1.0
            };
            let sy = if size.height > 0.0 {
                h as f64 / size.height
            } else {
                1.0
            };
            Some(RawSprite {
                kind: kind.as_str(),
                w,
                h,
                hotx: (hot.x * sx).round() as i32,
                hoty: (hot.y * sy).round() as i32,
                bmp: SpritePixels::Png(bytes.to_vec()),
                mask: None,
            })
        })
    }

    /// The densest backing scale in use, matching `region::plan_region`'s
    /// `canvas_scale` — the factor the canvas (and therefore the sprite) is
    /// sized by.
    fn target_backing_scale() -> f64 {
        obs_platform::enumerate_monitors()
            .iter()
            .map(|m| m.scale)
            .fold(1.0f64, f64::max)
    }

    /// The `NSImageRep` whose pixel width is closest to `target_px`.
    ///
    /// A cursor `NSImage` carries a whole ladder of representations — 17x23,
    /// 34x46, 85x115 and 170x230 for the stock arrow on macOS 15.7, the upper
    /// rungs feeding the accessibility pointer-size slider. `TIFFRepresentation`
    /// serializes all of them and `imageRepWithData:` then returns the *first*,
    /// which is the largest: a 10x sprite for a cursor the OS draws at 17x23.
    /// Selecting explicitly is what keeps `RawSprite::w/h` honest about being
    /// physical pixels.
    fn pick_rep(image: &NSImage, target_px: f64) -> Option<Retained<NSImageRep>> {
        let reps = image.representations();
        let count = reps.count();
        if count == 0 {
            return None;
        }
        let mut best: Option<(Retained<NSImageRep>, f64)> = None;
        for i in 0..count {
            let rep = reps.objectAtIndex(i);
            let w = rep.pixelsWide() as f64;
            if w <= 0.0 {
                continue;
            }
            let delta = (w - target_px).abs();
            if best.as_ref().is_none_or(|(_, b)| delta < *b) {
                best = Some((rep, delta));
            }
        }
        best.map(|(rep, _)| rep)
    }
}

/// Source id + settings for a speaker (output) capture source. Must be called
/// after modules are loaded (the registration probe reads plugin state).
///
/// Prefers ScreenCaptureKit system-audio capture (macOS 13+), which captures
/// all system output — `device_id` is ignored on that path. Falls back to
/// coreaudio_output_capture on macOS 12.
pub fn audio_output_capture(device_id: &str) -> (&'static str, ObsData) {
    // NOT obs_source_create != null: libobs creates a placeholder source for
    // unknown ids; get_display_name returns null exactly when unregistered.
    let sck_registered =
        !unsafe { obs_sys::obs_source_get_display_name(c"sck_audio_capture".as_ptr()) }.is_null();
    if sck_registered {
        let settings = ObsData::new();
        settings.set_int("type", 0);
        ("sck_audio_capture", settings)
    } else {
        let settings = ObsData::new();
        settings.set_string("device_id", device_id);
        ("coreaudio_output_capture", settings)
    }
}

/// Settings for a `WEBCAM_SOURCE_ID` instance capturing `device_id` (an
/// `AVCaptureDevice.uniqueID`, exactly as printed by `--list-cameras`).
pub fn webcam_settings(device_id: &str) -> ObsData {
    let settings = ObsData::new();
    settings.set_string(WEBCAM_DEVICE_KEY, device_id);
    // Keep the plugin's default "High" session preset (it picks the device's
    // best supported format); the recorder downscales the mix itself.
    settings.set_bool("use_preset", true);
    // Many cameras expose a muxed audio stream. `webcam::create` already
    // clears the source's audio-mixer mask and mutes it, but not asking the
    // device for audio at all also avoids the microphone-permission prompt.
    settings.set_bool("enable_audio", false);
    settings
}

/// System-audio capture on macOS (ScreenCaptureKit) taps upstream of the
/// output volume, so the Windows software-master-volume problem does not
/// exist here — compensation is always unity.
pub fn speaker_compensation_gain(_device_id: &str) -> f32 {
    1.0
}
