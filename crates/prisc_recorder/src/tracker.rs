//! Mouse-click highlight ("tracker"): a coloured circle that flashes at the
//! pointer on every click and fades as it expands.
//!
//! Port of the C++ original's `tick_obs_frame_processing` /
//! `update_mouse_tracker_state` pair. The scene gets one extra item on top of
//! the display captures — a 100x100 white disc (`image_source`) behind a
//! `color_filter` that tints it and drives its alpha — and a libobs tick
//! callback repositions/rescales/fades it once per rendered frame. Nothing is
//! composited by us; libobs draws the item like any other source, so the
//! highlight lands in the recording (and only in the recording — the real
//! screen is untouched).
//!
//! The animation constants are the original's verbatim: 400 ms, 85% peak
//! opacity, radius 10 → 40 density-independent units.

use std::ffi::c_void;
use std::fs;
use std::path::PathBuf;
use std::time::Instant;

use obs::data::ObsData;
use obs::scene::{ObsScene, ObsSceneItem};
use obs::source::ObsSource;

use crate::platform::{self, MouseInfo};
use crate::region::Rect;

/// How long one click's animation runs.
const DURATION_MS: f64 = 400.0;
/// Peak opacity, in `color_filter`'s 0-100 units (never fully opaque, so the
/// content under the highlight stays readable).
const MAX_OPACITY: f64 = 85.0;
/// Radius at the moment of the click, and how much it grows over `DURATION_MS`
/// — both in density-independent units (see `MouseInfo::scale`).
const RADIUS_START: f64 = 10.0;
const RADIUS_GROWTH: f64 = 30.0;
/// `tracker.png` is 100x100 with the disc inscribed, so its native radius is 50
/// image px; the scene-item scale is the wanted radius over that.
const IMAGE_RADIUS: f64 = 50.0;

/// The disc is shipped inside the binary rather than as a file next to it: it
/// is 1.6 KB, `image_source` needs a *path*, and embedding keeps the feature
/// working in dev builds and in every release bundle without a packaging step
/// that can silently go missing.
const TRACKER_PNG: &[u8] = include_bytes!("../assets/tracker.png");

const IMAGE_SOURCE_ID: &str = "image_source";
const COLOR_FILTER_ID: &str = "color_filter";

// ---------------------------------------------------------------------------
// Colour
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Rgb {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Rgb {
    /// libobs colour ints are little-endian RGBA (`vec4_from_rgba`), i.e. the
    /// same byte order as Win32's `RGB()` macro the original used. Alpha stays
    /// 0, which zeroes `color_filter`'s additive colour-wash term and leaves a
    /// pure multiply — white disc × colour = the colour.
    fn to_obs_int(self) -> i64 {
        (self.r as i64) | ((self.g as i64) << 8) | ((self.b as i64) << 16)
    }
}

/// Parses `"R,G,B"` with each component in 0-255.
pub fn parse_color(s: &str) -> Result<Rgb, String> {
    let parts: Vec<&str> = s.split(',').map(str::trim).collect();
    if parts.len() != 3 {
        return Err(format!("invalid color '{s}': must be R,G,B"));
    }
    let mut vals = [0u8; 3];
    for (i, part) in parts.iter().enumerate() {
        vals[i] = part
            .parse::<u8>()
            .map_err(|_| format!("invalid color '{s}': '{part}' is not a number in 0-255"))?;
    }
    Ok(Rgb {
        r: vals[0],
        g: vals[1],
        b: vals[2],
    })
}

// ---------------------------------------------------------------------------
// Animation math (pure)
// ---------------------------------------------------------------------------

/// One frame of the highlight animation, in the units libobs wants: canvas
/// pixels for `pos`, an image-relative factor for `scale`, and 0-100 for
/// `opacity`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TrackerFrame {
    pub pos: (f32, f32),
    pub scale: f32,
    pub opacity: i32,
}

/// The highlight `elapsed_ms` after a click at `click` (whose coordinates are
/// in the capture space of `region`), or `None` once the animation is over.
///
/// `canvas_scale` converts capture-space units to canvas pixels (1.0 on
/// Windows; the Retina backing scale on macOS), which is what makes this the
/// same formula as the original on Windows and correct on a Retina canvas.
pub fn animation_frame(
    click: MouseInfo,
    elapsed_ms: f64,
    region: Rect,
    canvas_scale: f64,
) -> Option<TrackerFrame> {
    if elapsed_ms >= DURATION_MS {
        return None;
    }
    let t = elapsed_ms / DURATION_MS;

    // Fades linearly from 85 to 0.
    let opacity = ((1.0 - t) * MAX_OPACITY) as i32;

    // Grows from 10 to 40 units, scaled for the density of the display the
    // click happened on.
    let radius = (RADIUS_START + t * RADIUS_GROWTH) * click.scale;

    // The item is positioned by its top-left corner, so back off one radius to
    // centre the disc on the click.
    let pos = (
        ((click.x - radius - region.x as f64) * canvas_scale) as f32,
        ((click.y - radius - region.y as f64) * canvas_scale) as f32,
    );

    Some(TrackerFrame {
        pos,
        scale: (radius * canvas_scale / IMAGE_RADIUS) as f32,
        opacity,
    })
}

// ---------------------------------------------------------------------------
// OBS wiring
// ---------------------------------------------------------------------------

/// Everything the tick callback touches. Boxed and handed to libobs as the
/// callback's `param`; only the graphics thread ever dereferences it.
struct TrackerState {
    filter: *mut obs_sys::obs_source_t,
    item: *mut obs_sys::obs_sceneitem_t,
    region: Rect,
    canvas_scale: f64,
    /// Position + press time of the most recent click, if any.
    last_click: Option<(MouseInfo, Instant)>,
    /// Whether the last applied frame left the disc drawn — the reset to fully
    /// transparent runs once, not on every idle tick.
    visible: bool,
}

impl TrackerState {
    fn apply(&self, frame: Option<TrackerFrame>) {
        let frame = frame.unwrap_or(TrackerFrame {
            pos: (0.0, 0.0),
            scale: 1.0,
            opacity: 0,
        });

        #[repr(C)]
        struct Vec2 {
            x: f32,
            y: f32,
        }
        let pos = Vec2 {
            x: frame.pos.0,
            y: frame.pos.1,
        };
        let scale = Vec2 {
            x: frame.scale,
            y: frame.scale,
        };
        unsafe {
            obs_sys::obs_sceneitem_set_pos(self.item, &pos as *const Vec2 as *const obs_sys::vec2);
            obs_sys::obs_sceneitem_set_scale(
                self.item,
                &scale as *const Vec2 as *const obs_sys::vec2,
            );
        }

        // Only "opacity" is sent: obs_source_update merges into the existing
        // settings, so the colour set at creation is preserved.
        let settings = ObsData::new();
        settings.set_int("opacity", frame.opacity as i64);
        unsafe { obs_sys::obs_source_update(self.filter, settings.as_ptr()) };
    }
}

/// libobs tick callback — runs on the graphics thread once per frame.
unsafe extern "C" fn tick(param: *mut c_void, _seconds: f32) {
    let state = &mut *(param as *mut TrackerState);

    let mouse = platform::get_mouse_info();
    // Sampled while held, not on the press edge: a held button keeps refreshing
    // the anchor, so a drag pins the highlight to the pointer and the fade
    // starts on release (the original's behaviour).
    if mouse.pressed {
        state.last_click = Some((mouse, Instant::now()));
    }

    let frame = state.last_click.and_then(|(click, at)| {
        animation_frame(
            click,
            at.elapsed().as_secs_f64() * 1000.0,
            state.region,
            state.canvas_scale,
        )
    });

    match frame {
        Some(_) => {
            state.visible = true;
            state.apply(frame);
        }
        None if state.visible => {
            state.visible = false;
            state.apply(None);
        }
        None => {}
    }
}

/// Owns the tracker's scene item, sources, and tick-callback registration.
/// Dropping it deregisters the callback before the state it points at goes
/// away and removes the item from the scene — a runtime `configure` can turn
/// the tracker off and the highlight actually disappears.
pub struct MouseTracker {
    _image: ObsSource,
    filter: ObsSource,
    item: ObsSceneItem,
    /// Boxed so the address handed to libobs stays valid when `self` moves.
    state: Box<TrackerState>,
}

impl MouseTracker {
    /// Adds the highlight to `scene` (on top of everything already in it) and
    /// starts the per-frame animation. `region` and `canvas_scale` come from
    /// the region plan and map capture coordinates onto the canvas.
    pub fn create(
        scene: &ObsScene,
        color: Rgb,
        region: Rect,
        canvas_scale: f64,
    ) -> Result<MouseTracker, String> {
        // NOT obs_source_create != null: libobs happily creates a placeholder
        // source for an unknown id, so the highlight would silently never draw.
        for id in [IMAGE_SOURCE_ID, COLOR_FILTER_ID] {
            let id_c = std::ffi::CString::new(id).unwrap();
            if unsafe { obs_sys::obs_source_get_display_name(id_c.as_ptr()) }.is_null() {
                return Err(format!(
                    "source type '{id}' is not registered — the plugin providing it (image-source \
                     / obs-filters) failed to load"
                ));
            }
        }

        let png_path = extract_png()?;

        let image_settings = ObsData::new();
        image_settings.set_bool("unload", true);
        image_settings.set_string("file", &png_path.to_string_lossy());
        let image = ObsSource::create(IMAGE_SOURCE_ID, "mouse_highlight", Some(&image_settings))
            .map_err(|e| format!("failed to create the highlight image source: {e}"))?;

        // Starts fully transparent: the first click makes it visible.
        let filter_settings = ObsData::new();
        filter_settings.set_int("opacity", 0);
        filter_settings.set_int("color", color.to_obs_int());
        let filter = ObsSource::create(
            COLOR_FILTER_ID,
            "mouse_color_correction",
            Some(&filter_settings),
        )
        .map_err(|e| format!("failed to create the highlight color filter: {e}"))?;

        image.add_filter(&filter);
        let item = scene.add(&image);

        let mut state = Box::new(TrackerState {
            filter: filter.as_ptr(),
            item: item.as_ptr(),
            region,
            canvas_scale,
            last_click: None,
            visible: false,
        });
        // Fully transparent until the first click lands.
        state.apply(None);

        let param = &mut *state as *mut TrackerState as *mut c_void;
        unsafe { obs_sys::obs_add_tick_callback(Some(tick), param) };

        Ok(MouseTracker {
            _image: image,
            filter,
            item,
            state,
        })
    }

    /// Retints the highlight. Only "color" is sent — `obs_source_update`
    /// merges, so the tick callback's "opacity" writes are untouched.
    pub fn set_color(&self, color: Rgb) {
        let settings = ObsData::new();
        settings.set_int("color", color.to_obs_int());
        self.filter.update(&settings);
    }
}

impl Drop for MouseTracker {
    fn drop(&mut self) {
        let param = &mut *self.state as *mut TrackerState as *mut c_void;
        unsafe { obs_sys::obs_remove_tick_callback(Some(tick), param) };
        // Detach from the scene so a runtime tracker-off stops rendering the
        // disc; the sources are released by the field drops that follow.
        self.item.remove();
    }
}

/// Writes the embedded disc to the temp dir and returns its path. The name is
/// fixed (one file per machine, rewritten each run rather than accumulating);
/// the write goes to a pid-unique name first so a second instance starting up
/// can never read a half-written file.
fn extract_png() -> Result<PathBuf, String> {
    let dir = std::env::temp_dir();
    let path = dir.join("obs-express-tracker.png");
    let staging = dir.join(format!("obs-express-tracker.{}.png", std::process::id()));

    fs::write(&staging, TRACKER_PNG)
        .map_err(|e| format!("failed to write '{}': {e}", staging.display()))?;
    if let Err(e) = fs::rename(&staging, &path) {
        let _ = fs::remove_file(&staging);
        return Err(format!("failed to write '{}': {e}", path.display()));
    }
    Ok(path)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn region() -> Rect {
        Rect {
            x: 100,
            y: 200,
            w: 800,
            h: 600,
        }
    }

    fn click_at(x: f64, y: f64, scale: f64) -> MouseInfo {
        MouseInfo {
            x,
            y,
            pressed: true,
            scale,
        }
    }

    #[test]
    fn color_parses() {
        assert_eq!(parse_color("255,0,0").unwrap(), Rgb { r: 255, g: 0, b: 0 });
        assert_eq!(
            parse_color(" 1 , 2 , 3 ").unwrap(),
            Rgb { r: 1, g: 2, b: 3 }
        );
    }

    #[test]
    fn color_rejects_malformed() {
        assert!(parse_color("255,0").is_err());
        assert!(parse_color("255,0,0,0").is_err());
        assert!(parse_color("a,b,c").is_err());
        assert!(parse_color("256,0,0").is_err());
        assert!(parse_color("-1,0,0").is_err());
        assert!(parse_color("").is_err());
    }

    #[test]
    fn color_uses_little_endian_rgba() {
        // Win32 RGB(255,0,0) == 0x0000FF, matching vec4_from_rgba's byte order.
        assert_eq!(Rgb { r: 255, g: 0, b: 0 }.to_obs_int(), 0x0000_00FF);
        assert_eq!(Rgb { r: 0, g: 0, b: 255 }.to_obs_int(), 0x00FF_0000);
        assert_eq!(
            Rgb {
                r: 0x12,
                g: 0x34,
                b: 0x56
            }
            .to_obs_int(),
            0x0056_3412
        );
    }

    #[test]
    fn frame_at_click_is_small_and_opaque() {
        let f = animation_frame(click_at(500.0, 400.0, 1.0), 0.0, region(), 1.0).unwrap();
        assert_eq!(f.opacity, 85);
        // radius 10 -> scale 10/50, centred on the click minus the region origin.
        assert_eq!(f.scale, 0.2);
        assert_eq!(f.pos, (500.0 - 10.0 - 100.0, 400.0 - 10.0 - 200.0));
    }

    #[test]
    fn frame_expands_and_fades() {
        let mid = animation_frame(click_at(500.0, 400.0, 1.0), 200.0, region(), 1.0).unwrap();
        assert_eq!(mid.opacity, 42); // (1 - 0.5) * 85, truncated
        assert_eq!(mid.scale, 25.0 / 50.0); // radius 10 + 0.5*30
        assert_eq!(mid.pos, (500.0 - 25.0 - 100.0, 400.0 - 25.0 - 200.0));

        let late = animation_frame(click_at(500.0, 400.0, 1.0), 399.0, region(), 1.0).unwrap();
        assert!(late.opacity < mid.opacity);
        assert!(late.scale > mid.scale);
    }

    #[test]
    fn frame_ends_after_the_duration() {
        assert!(animation_frame(click_at(0.0, 0.0, 1.0), 400.0, region(), 1.0).is_none());
        assert!(animation_frame(click_at(0.0, 0.0, 1.0), 10_000.0, region(), 1.0).is_none());
    }

    #[test]
    fn frame_scales_with_display_density() {
        // 200% Windows display: everything doubles, so the highlight does too.
        let f = animation_frame(click_at(500.0, 400.0, 2.0), 0.0, region(), 1.0).unwrap();
        assert_eq!(f.scale, 20.0 / 50.0);
        assert_eq!(f.pos, (500.0 - 20.0 - 100.0, 400.0 - 20.0 - 200.0));
    }

    #[test]
    fn frame_maps_onto_a_retina_canvas() {
        // macOS 2x canvas: capture-space points, canvas pixels. The disc keeps
        // its point-space geometry and is doubled onto the canvas.
        let f = animation_frame(click_at(500.0, 400.0, 1.0), 0.0, region(), 2.0).unwrap();
        assert_eq!(f.scale, 20.0 / 50.0);
        assert_eq!(
            f.pos,
            ((500.0 - 10.0 - 100.0) * 2.0, (400.0 - 10.0 - 200.0) * 2.0)
        );
    }

    #[test]
    fn embedded_png_is_the_original_asset() {
        // 100x100 RGBA PNG — the geometry IMAGE_RADIUS assumes.
        assert_eq!(&TRACKER_PNG[..8], b"\x89PNG\r\n\x1a\n");
        let width = u32::from_be_bytes(TRACKER_PNG[16..20].try_into().unwrap());
        let height = u32::from_be_bytes(TRACKER_PNG[20..24].try_into().unwrap());
        assert_eq!((width, height), (100, 100));
    }
}
