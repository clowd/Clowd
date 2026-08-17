//! macOS platform implementation (DESIGN §2.2). Ports the pre-refactor
//! CoreGraphics logic behind the new platform signatures. Monitor bounds are
//! CG points (§1.1 capture space).

use std::env;
use std::ffi::CStr;
use std::path::Path;

use obs::data::ObsData;

use crate::cursor_sprite::SpriteEvent;

use super::{CursorKind, CursorState, MonitorInfo, MouseInfo, ObsPaths};

/// `platform` field of the input-capture header (wire contract).
pub const PLATFORM_NAME: &str = "macos";

pub const GRAPHICS_MODULE: &CStr = c"libobs-metal.dylib";
pub const DISPLAY_CAPTURE_ID: &str = "screen_capture";
pub const AUDIO_INPUT_CAPTURE_ID: &str = "coreaudio_input_capture";
/// Webcam capture source (`--webcam` / `--list-cameras`): AVFoundation. The
/// async ("macos-avcapture") variant rather than the fast path: it delivers
/// frames without needing the source to be "showing" in a rendered scene.
pub const WEBCAM_SOURCE_ID: &str = "macos-avcapture";
/// The `WEBCAM_SOURCE_ID` settings key (and property) holding the device id
/// (an `AVCaptureDevice.uniqueID`).
pub const WEBCAM_DEVICE_KEY: &str = "device";

extern "C" {
    fn CGGetActiveDisplayList(
        max_displays: u32,
        active_displays: *mut u32,
        display_count: *mut u32,
    ) -> i32;
    fn CGDisplayBounds(display: u32) -> CGRect;
    fn CGMainDisplayID() -> u32;
    fn CGDisplayCreateUUIDFromDisplayID(display: u32) -> *const std::ffi::c_void;
    fn CGDisplayCopyDisplayMode(display: u32) -> *mut std::ffi::c_void;
    fn CGDisplayModeGetPixelWidth(mode: *mut std::ffi::c_void) -> usize;
    fn CGDisplayModeGetWidth(mode: *mut std::ffi::c_void) -> usize;
    fn CGDisplayModeRelease(mode: *mut std::ffi::c_void);
    fn CFUUIDCreateString(
        allocator: *const std::ffi::c_void,
        uuid: *const std::ffi::c_void,
    ) -> *const std::ffi::c_void;
    fn CFRelease(cf: *const std::ffi::c_void);

    /// `CGEventCreate(NULL)` snapshots the current event state; its location is
    /// the cursor position in global display coordinates (points).
    fn CGEventCreate(source: *const std::ffi::c_void) -> *const std::ffi::c_void;
    fn CGEventGetLocation(event: *const std::ffi::c_void) -> CGPoint;
    /// Reads button state without an event tap, so no Accessibility /
    /// Input Monitoring permission is involved.
    fn CGEventSourceButtonState(state_id: i32, button: u32) -> bool;
    /// Whether the cursor is currently drawn. Deprecated since 10.9 but still
    /// the only public answer, and it needs no permission — the alternative is
    /// private CGS SPI.
    fn CGCursorIsVisible() -> bool;
}

/// `kCGEventSourceStateCombinedSessionState` — the session's combined state,
/// which includes synthesized clicks (the closest analogue to Win32's
/// `GetAsyncKeyState`).
const CG_EVENT_SOURCE_STATE_COMBINED_SESSION: i32 = 0;
const CG_MOUSE_BUTTON_LEFT: u32 = 0;
const CG_MOUSE_BUTTON_RIGHT: u32 = 1;

#[repr(C)]
#[derive(Debug, Copy, Clone)]
struct CGPoint {
    x: f64,
    y: f64,
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
struct CGSize {
    width: f64,
    height: f64,
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
struct CGRect {
    origin: CGPoint,
    size: CGSize,
}

fn cfstring_to_string(cfstr: *const std::ffi::c_void) -> String {
    if cfstr.is_null() {
        return String::new();
    }
    extern "C" {
        fn CFStringGetLength(the_string: *const std::ffi::c_void) -> isize;
        fn CFStringGetCString(
            the_string: *const std::ffi::c_void,
            buffer: *mut u8,
            buffer_size: isize,
            encoding: u32,
        ) -> bool;
    }
    unsafe {
        let len = CFStringGetLength(cfstr);
        let mut buf = vec![0u8; (len as usize + 1) * 4];
        let ok = CFStringGetCString(cfstr, buf.as_mut_ptr(), buf.len() as isize, 0x08000100); // kCFStringEncodingUTF8
        if ok {
            let s = CStr::from_ptr(buf.as_ptr() as *const _);
            s.to_string_lossy().into_owned()
        } else {
            String::new()
        }
    }
}

/// No-op on macOS.
pub fn init_process() {}

pub fn enumerate_monitors() -> Vec<MonitorInfo> {
    let mut monitors = Vec::new();
    let mut display_ids = [0u32; 32];
    let mut count: u32 = 0;

    let ret = unsafe { CGGetActiveDisplayList(32, display_ids.as_mut_ptr(), &mut count) };
    if ret != 0 {
        return monitors;
    }

    let main_display = unsafe { CGMainDisplayID() };

    for &display_id in display_ids.iter().take(count as usize) {
        let bounds = unsafe { CGDisplayBounds(display_id) };

        // Retina backing scale = current mode pixel width / point width; SCK
        // captures at the same mode's pixel resolution.
        let scale = unsafe {
            let mode = CGDisplayCopyDisplayMode(display_id);
            if mode.is_null() {
                1.0
            } else {
                let px = CGDisplayModeGetPixelWidth(mode) as f64;
                let pt = CGDisplayModeGetWidth(mode) as f64;
                CGDisplayModeRelease(mode);
                if px > 0.0 && pt > 0.0 {
                    px / pt
                } else {
                    1.0
                }
            }
        };

        let uuid_ref = unsafe { CGDisplayCreateUUIDFromDisplayID(display_id) };
        let uuid = if !uuid_ref.is_null() {
            let cfstr = unsafe { CFUUIDCreateString(std::ptr::null(), uuid_ref) };
            let s = cfstring_to_string(cfstr);
            if !cfstr.is_null() {
                unsafe { CFRelease(cfstr) };
            }
            unsafe { CFRelease(uuid_ref) };
            s
        } else {
            format!("{display_id}")
        };

        monitors.push(MonitorInfo {
            id: uuid,
            alt_id: Some(display_id.to_string()),
            x: bounds.origin.x as i32,
            y: bounds.origin.y as i32,
            width: bounds.size.width as u32,
            height: bounds.size.height as u32,
            scale,
            is_primary: display_id == main_display,
        });
    }

    monitors
}

pub fn find_monitor(id: &str) -> Option<MonitorInfo> {
    super::match_monitor(id, &enumerate_monitors())
}

/// Cursor position in global display points (the same space as
/// `CGDisplayBounds`, hence as `MonitorInfo` and `--region`) plus the
/// left/right button state.
///
/// `scale` is 1.0: unlike Windows physical pixels, points are already
/// density-independent, so the highlight needs no DPI compensation here (the
/// region planner separately scales points → canvas pixels).
pub fn get_mouse_info() -> MouseInfo {
    let pressed = unsafe {
        CGEventSourceButtonState(CG_EVENT_SOURCE_STATE_COMBINED_SESSION, CG_MOUSE_BUTTON_LEFT)
            || CGEventSourceButtonState(
                CG_EVENT_SOURCE_STATE_COMBINED_SESSION,
                CG_MOUSE_BUTTON_RIGHT,
            )
    };

    let event = unsafe { CGEventCreate(std::ptr::null()) };
    let (x, y) = if event.is_null() {
        (0.0, 0.0)
    } else {
        let p = unsafe { CGEventGetLocation(event) };
        unsafe { CFRelease(event) };
        (p.x, p.y)
    };

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
    let event = unsafe { CGEventCreate(std::ptr::null()) };
    let (x, y) = if event.is_null() {
        (0.0, 0.0)
    } else {
        let p = unsafe { CGEventGetLocation(event) };
        unsafe { CFRelease(event) };
        (p.x, p.y)
    };
    // Checked before classifying: a hidden cursor still has a shape, and
    // reporting it would make the editor composite a pointer over content
    // where macOS was drawing nothing.
    let kind = if unsafe { CGCursorIsVisible() } {
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

    use super::{CGPoint, CGSize, CursorKind};
    use crate::cursor_sprite::{RawSprite, SpriteEvent, SpritePixels};

    /// How stale a sample may get when the seed is unavailable. At 60 fps this
    /// caps the cost of the fallback path at roughly 0.5% of one core.
    const RESAMPLE_INTERVAL: Duration = Duration::from_millis(100);

    const RTLD_DEFAULT: *mut c_void = -2isize as *mut c_void;
    /// `NSApplicationActivationPolicyProhibited` — no Dock icon, no menu bar.
    const ACTIVATION_POLICY_PROHIBITED: isize = 2;

    // Linked by build.rs (AppKit + libobjc), like the CoreGraphics symbols above.
    extern "C" {
        fn objc_autoreleasePoolPush() -> *mut c_void;
        fn objc_autoreleasePoolPop(pool: *mut c_void);
        fn objc_getClass(name: *const c_char) -> *mut c_void;
        fn sel_registerName(name: *const c_char) -> *mut c_void;
        fn objc_msgSend();
        fn dlsym(handle: *mut c_void, symbol: *const c_char) -> *mut c_void;
    }

    type Msg0Ptr = unsafe extern "C" fn(*mut c_void, *mut c_void) -> *mut c_void;
    type Msg0Len = unsafe extern "C" fn(*mut c_void, *mut c_void) -> usize;
    type Msg1Isize = unsafe extern "C" fn(*mut c_void, *mut c_void, isize) -> bool;
    type Msg1Ptr = unsafe extern "C" fn(*mut c_void, *mut c_void, *mut c_void) -> *mut c_void;
    type Msg1Usize = unsafe extern "C" fn(*mut c_void, *mut c_void, usize) -> *mut c_void;
    type Msg2UsizePtr =
        unsafe extern "C" fn(*mut c_void, *mut c_void, usize, *mut c_void) -> *mut c_void;
    /// NSPoint/NSSize are two doubles — returned in registers on both arm64
    /// and x86_64, so plain `objc_msgSend` (not `_stret`) is the right call.
    type Msg0Point = unsafe extern "C" fn(*mut c_void, *mut c_void) -> CGPoint;
    type Msg0Size = unsafe extern "C" fn(*mut c_void, *mut c_void) -> CGSize;

    /// Runs `f` inside an autorelease pool.
    ///
    /// Nothing here executes on an AppKit-managed thread — the tick callback is
    /// libobs's graphics thread — so there is no ambient pool to catch what
    /// AppKit autoreleases. `TIFFRepresentation` and the PNG encode return
    /// autoreleased objects holding the cursor bitmap at every representation
    /// size; without a pool each capture strands them. Measured at ~750 KB per
    /// cursor change on macOS 15.7, which over a long recording is unbounded
    /// growth rather than a fixed overhead.
    fn autoreleased<T>(f: impl FnOnce() -> T) -> T {
        let pool = unsafe { objc_autoreleasePoolPush() };
        let out = f();
        unsafe { objc_autoreleasePoolPop(pool) };
        out
    }

    unsafe fn class(name: &str) -> *mut c_void {
        let n = CString::new(name).unwrap();
        objc_getClass(n.as_ptr())
    }

    unsafe fn selector(name: &str) -> *mut c_void {
        let n = CString::new(name).unwrap();
        sel_registerName(n.as_ptr())
    }

    /// `[obj sel]` returning an object pointer.
    unsafe fn msg(obj: *mut c_void, sel: &str) -> *mut c_void {
        if obj.is_null() {
            return std::ptr::null_mut();
        }
        let f: Msg0Ptr = std::mem::transmute(objc_msgSend as *const ());
        f(obj, selector(sel))
    }

    /// `[obj sel]` returning an NSUInteger.
    unsafe fn msg_len(obj: *mut c_void, sel: &str) -> usize {
        if obj.is_null() {
            return 0;
        }
        let f: Msg0Len = std::mem::transmute(objc_msgSend as *const ());
        f(obj, selector(sel))
    }

    /// `[obj sel:arg]` with an object argument, returning an object pointer.
    unsafe fn msg1(obj: *mut c_void, sel: &str, arg: *mut c_void) -> *mut c_void {
        if obj.is_null() {
            return std::ptr::null_mut();
        }
        let f: Msg1Ptr = std::mem::transmute(objc_msgSend as *const ());
        f(obj, selector(sel), arg)
    }

    /// `[obj sel:index]`, returning an object pointer.
    unsafe fn msg_idx(obj: *mut c_void, sel: &str, index: usize) -> *mut c_void {
        if obj.is_null() {
            return std::ptr::null_mut();
        }
        let f: Msg1Usize = std::mem::transmute(objc_msgSend as *const ());
        f(obj, selector(sel), index)
    }

    /// `[obj sel:int arg2:obj]`, returning an object pointer.
    unsafe fn msg2(obj: *mut c_void, sel: &str, arg1: usize, arg2: *mut c_void) -> *mut c_void {
        if obj.is_null() {
            return std::ptr::null_mut();
        }
        let f: Msg2UsizePtr = std::mem::transmute(objc_msgSend as *const ());
        f(obj, selector(sel), arg1, arg2)
    }

    /// `[obj sel]` returning an NSPoint.
    unsafe fn msg_point(obj: *mut c_void, sel: &str) -> CGPoint {
        if obj.is_null() {
            return CGPoint { x: 0.0, y: 0.0 };
        }
        let f: Msg0Point = std::mem::transmute(objc_msgSend as *const ());
        f(obj, selector(sel))
    }

    /// `[obj sel]` returning an NSSize.
    unsafe fn msg_size(obj: *mut c_void, sel: &str) -> CGSize {
        if obj.is_null() {
            return CGSize {
                width: 0.0,
                height: 0.0,
            };
        }
        let f: Msg0Size = std::mem::transmute(objc_msgSend as *const ());
        f(obj, selector(sel))
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
        INIT.get_or_init(|| unsafe {
            let app = msg(class("NSApplication"), "sharedApplication");
            if !app.is_null() {
                let f: Msg1Isize = std::mem::transmute(objc_msgSend as *const ());
                f(
                    app,
                    selector("setActivationPolicy:"),
                    ACTIVATION_POLICY_PROHIBITED,
                );
            }
        });
    }

    /// FNV-1a over an `NSCursor`'s image bytes. `None` when the cursor, its
    /// image, or the encode is unavailable.
    unsafe fn hash_cursor(cursor: *mut c_void) -> Option<u64> {
        let data = msg(msg(cursor, "image"), "TIFFRepresentation");
        let bytes = msg(data, "bytes") as *const u8;
        let len = msg_len(data, "length");
        if bytes.is_null() || len == 0 {
            return None;
        }
        let mut h: u64 = 0xcbf2_9ce4_8422_2325;
        for i in 0..len {
            h ^= *bytes.add(i) as u64;
            h = h.wrapping_mul(0x1000_0000_01b3);
        }
        Some(h)
    }

    /// Stock `NSCursor` class selectors paired with the wire kind they mean.
    ///
    /// Only unambiguous mappings are listed. macOS has no stock cursor for
    /// `Wait`/`AppStarting` (the beachball is not an `NSCursor`), `Help`,
    /// `Pen`, `Person` or `UpArrow`, and exposes the diagonal resize cursors
    /// only as private SPI, so `SizeNwse`/`SizeNesw` are unreachable too —
    /// anything unmatched falls through to `Custom`, which is exactly what
    /// those cases are from the wire contract's point of view.
    const STOCK: [(&str, CursorKind); 14] = [
        ("arrowCursor", CursorKind::Arrow),
        ("IBeamCursor", CursorKind::IBeam),
        ("IBeamCursorForVerticalLayout", CursorKind::IBeam),
        ("crosshairCursor", CursorKind::Cross),
        ("pointingHandCursor", CursorKind::Hand),
        ("operationNotAllowedCursor", CursorKind::No),
        ("resizeLeftRightCursor", CursorKind::SizeWe),
        ("resizeUpDownCursor", CursorKind::SizeNs),
        ("resizeLeftCursor", CursorKind::SizeWe),
        ("resizeRightCursor", CursorKind::SizeWe),
        ("resizeUpCursor", CursorKind::SizeNs),
        ("resizeDownCursor", CursorKind::SizeNs),
        // The pan cursors are the closest thing macOS has to SizeAll.
        ("openHandCursor", CursorKind::SizeAll),
        ("closedHandCursor", CursorKind::SizeAll),
    ];

    /// Hashes of the stock cursors, built once (~7 ms) on first classify.
    fn stock_table() -> &'static Vec<(u64, CursorKind)> {
        static TABLE: OnceLock<Vec<(u64, CursorKind)>> = OnceLock::new();
        TABLE.get_or_init(|| {
            ensure_appkit();
            autoreleased(|| {
            let mut out = Vec::with_capacity(STOCK.len());
            for (sel, kind) in STOCK {
                let cursor = unsafe { msg(class("NSCursor"), sel) };
                if let Some(h) = unsafe { hash_cursor(cursor) } {
                    // First mapping wins, so the aliases above cannot displace
                    // the canonical kind for a shared image.
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
        let hash = autoreleased(|| unsafe { hash_cursor(msg(class("NSCursor"), "currentSystemCursor")) });
        match hash {
            Some(h) => table
                .iter()
                .find(|(hh, _)| *hh == h)
                .map(|(_, k)| *k)
                .unwrap_or(CursorKind::Custom),
            // No readable image: report the fallback kind rather than invent a
            // shape.
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

    /// `NSBitmapImageRepFileTypePNG`.
    const BITMAP_FILE_TYPE_PNG: usize = 4;

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
        autoreleased(|| unsafe {
            let cursor = msg(class("NSCursor"), "currentSystemCursor");
            let image = msg(cursor, "image");
            let size = msg_size(image, "size");
            let rep = pick_rep(image, size.width * target_backing_scale())?;

            let png = msg2(
                rep,
                "representationUsingType:properties:",
                BITMAP_FILE_TYPE_PNG,
                std::ptr::null_mut(),
            );
            let bytes = msg(png, "bytes") as *const u8;
            let len = msg_len(png, "length");
            if bytes.is_null() || len == 0 {
                return None;
            }
            let w = msg_len(rep, "pixelsWide") as u32;
            let h = msg_len(rep, "pixelsHigh") as u32;
            if w == 0 || h == 0 {
                return None;
            }
            // hotSpot is in points; the sprite is pixel-sized, so scale by the
            // chosen representation's pixels-per-point ratio.
            let hot = msg_point(cursor, "hotSpot");
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
                bmp: SpritePixels::Png(std::slice::from_raw_parts(bytes, len).to_vec()),
                mask: None,
            })
        })
    }

    /// The densest backing scale in use, matching `region::plan_region`'s
    /// `canvas_scale` — the factor the canvas (and therefore the sprite) is
    /// sized by.
    fn target_backing_scale() -> f64 {
        super::enumerate_monitors()
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
    unsafe fn pick_rep(image: *mut c_void, target_px: f64) -> Option<*mut c_void> {
        let reps = msg(image, "representations");
        let count = msg_len(reps, "count");
        if count == 0 {
            return None;
        }
        let mut best: Option<(*mut c_void, f64)> = None;
        for i in 0..count {
            let rep = msg_idx(reps, "objectAtIndex:", i);
            if rep.is_null() {
                continue;
            }
            let w = msg_len(rep, "pixelsWide") as f64;
            if w <= 0.0 {
                continue;
            }
            let delta = (w - target_px).abs();
            if best.is_none_or(|(_, b)| delta < b) {
                best = Some((rep, delta));
            }
        }
        best.map(|(rep, _)| rep)
    }
}

/// The input-capture header's per-monitor `scale`. On macOS coordinates are
/// points, so the Retina backing scale already stored on the monitor is the
/// right density factor.
pub fn monitor_display_scale(m: &MonitorInfo) -> f64 {
    m.scale
}

pub fn display_capture_settings(m: &MonitorInfo, show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
    settings.set_int("type", 0);
    settings.set_string("display_uuid", &m.id);
    settings.set_bool("show_cursor", show_cursor);
    settings
}

/// Partial `obs_source_update` payload toggling cursor capture on an existing
/// display source (mac-capture applies it live).
pub fn cursor_update_settings(show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
    settings.set_bool("show_cursor", show_cursor);
    settings
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

pub fn default_obs_paths(exe_dir: &Path) -> ObsPaths {
    // Base plugin dir. A bundled `obs-plugins` dir next to the executable (the
    // relocatable release layout) wins; otherwise honour the OBS_PLUGIN_PATH
    // override, then the absolute path baked in by build.rs (dev builds run
    // against the OBS build tree in place — §2.4).
    let base = env::var("OBS_PLUGIN_PATH").unwrap_or_else(|_| {
        let bundled = exe_dir.join("obs-plugins");
        if bundled.is_dir() {
            bundled.to_string_lossy().into_owned()
        } else {
            env!("OBS_PLUGIN_DIR").to_string()
        }
    });
    let module_bin = format!("{base}/%module%/RelWithDebInfo/%module%.plugin/Contents/MacOS");
    let module_data = match env::var("OBS_PLUGIN_DATA_PATH") {
        Ok(v) => format!("{v}/%module%"),
        Err(_) => format!("{base}/%module%/RelWithDebInfo/%module%.plugin/Contents/Resources"),
    };
    // libobs core data is framework-embedded on macOS; only an explicit
    // override registers an extra data path.
    let libobs_data = env::var("OBS_DATA_PATH").ok().map(std::path::PathBuf::from);
    ObsPaths {
        module_bin,
        module_data,
        libobs_data,
    }
}

pub fn exit_process(code: i32) -> ! {
    unsafe { libc::_exit(code) }
}
