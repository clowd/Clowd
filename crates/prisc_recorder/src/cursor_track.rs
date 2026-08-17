//! 512x512 cursor video track (DESIGN §2): a second (or third, after the
//! webcam) video track carrying a native-resolution crop of the screen
//! centered on the cursor, so the editor can re-composite a crisp cursor at
//! any zoom without re-encoding the screen track.
//!
//! Shape is `webcam.rs::create` verbatim: private scene -> `obs_view` with its
//! own 512x512 mix -> dedicated x264 encoder attached via
//! `obs_output_set_video_encoder2`. The scene holds one FRESH display-capture
//! source per monitor intersecting the recording region with cursor capture
//! forced ON — the main scene's sources (and their `cursor` setting) are
//! untouched. Requires `--multi-track` (mp4_output): ffmpeg_muxer silently
//! drops video encoder indices > 0.
//!
//! Recentering runs on the input-capture tick (`InputCapture`'s cursor
//! observer, graphics thread): each rendered frame the active monitor's item
//! is positioned so the sampled cursor hotspot lands at the canvas center —
//! the SAME sample the frame row records (consistency contract, DESIGN §1).
//!
//! CRITICAL invariant (same as the webcam's): `obs_reset_video` destroys ALL
//! view mixes. Any code path calling `obs_reset_video` while a `CursorTrack`
//! exists MUST drop it first (detaching its encoder from the output and
//! clearing the input-capture observer) and rebuild + rebind afterwards — see
//! `Recorder::configure_full`.

use obs::encoder::ObsEncoder;
use obs::scene::{ObsScene, ObsSceneItem};
use obs::source::ObsSource;
use obs::video::VideoInfo;
use obs::view::ObsView;

use crate::platform::{self, CursorState, MonitorInfo};
use crate::region::{self, Rect};
use crate::settings::Settings;
use crate::webcam;

/// The cursor mix is a fixed square: large enough for any OS cursor at any
/// DPI, small enough that the extra track's encode cost is negligible. The
/// editor knows the box is centered on the hotspot, so the size is part of
/// the contract with it (`tracks_json` reports it too).
pub const SIZE: u32 = 512;

/// Output video track index the cursor encoder attaches at: the webcam (when
/// present) owns track 1, so the cursor takes the next slot.
pub fn track_index(webcam_present: bool) -> usize {
    1 + webcam_present as usize
}

/// Whether the cursor position (capture coordinate space) is on `m`.
/// Half-open bounds, matching `EnumDisplayMonitors` rect semantics — a cursor
/// on a shared edge belongs to the monitor whose origin it matches.
pub fn monitor_contains(m: &MonitorInfo, cx: i32, cy: i32) -> bool {
    let (cx, cy) = (cx as i64, cy as i64);
    let (mx, my) = (m.x as i64, m.y as i64);
    cx >= mx && cx < mx + m.width as i64 && cy >= my && cy < my + m.height as i64
}

/// Index of the monitor the cursor is on, if any of the track's monitors
/// contain it (the cursor can be on a display outside the recording region —
/// every item hides then and the box goes black).
pub fn active_monitor(monitors: &[MonitorInfo], cx: i32, cy: i32) -> Option<usize> {
    monitors.iter().position(|m| monitor_contains(m, cx, cy))
}

/// Scene-item position placing the cursor hotspot at the canvas center
/// (SIZE/2, SIZE/2). The capture source emits pixel-sized frames at item
/// scale 1.0 (a 1:1 native crop), so the cursor's offset from the monitor
/// origin is converted to capture pixels via the monitor's scale — a no-op on
/// Windows (coords are already physical px), the Retina factor on macOS.
pub fn item_pos(m: &MonitorInfo, cx: i32, cy: i32) -> (f32, f32) {
    let half = (SIZE / 2) as f64;
    (
        (half - (cx - m.x) as f64 * m.scale) as f32,
        (half - (cy - m.y) as f64 * m.scale) as f32,
    )
}

/// The complete cursor-track chain. Field order is drop order and it matters
/// (same reasoning as `Webcam`): the encoder must release before the view
/// (its `video_t` belongs to the view's mix), and the view's refs must clear
/// before the items, scene and sources are released.
pub struct CursorTrack {
    /// The cursor track's video encoder, bound to the view mix's `video_t`.
    pub encoder: ObsEncoder,
    _view: ObsView,
    items: Vec<ObsSceneItem>,
    _scene: ObsScene,
    _sources: Vec<ObsSource>,
    /// Monitor geometry parallel to `items`, for the positioner.
    monitors: Vec<MonitorInfo>,
}

/// One monitor's item plus the recentering state, packaged (parallel to a
/// monitor list) for the positioner closure.
struct Target {
    item: ObsSceneItem,
    /// Last applied visibility; `None` until the first call forces a write
    /// (the closure must not assume what state the items are in).
    visible: Option<bool>,
}

// SAFETY: the closure holding these runs on the graphics thread while the
// recorder thread created them. `obs_sceneitem_set_pos` / `set_visible` are
// callable from any thread (libobs locks internally), and the held
// `ObsSceneItem` ref keeps the pointer alive for the closure's lifetime.
unsafe impl Send for Target {}

fn reposition(monitors: &[MonitorInfo], targets: &mut [Target], cursor: &CursorState) {
    let active = active_monitor(monitors, cursor.x, cursor.y);
    for (i, t) in targets.iter_mut().enumerate() {
        let show = active == Some(i);
        if show {
            let (x, y) = item_pos(&monitors[i], cursor.x, cursor.y);
            t.item.set_pos(x, y);
        }
        // Visibility writes are deduplicated: obs_sceneitem_set_visible fires
        // scene signals, which per rendered frame would be pure churn.
        if t.visible != Some(show) {
            t.item.set_visible(show);
            t.visible = Some(show);
        }
    }
}

/// Builds the whole cursor-track chain: one fresh display capture per monitor
/// intersecting `region` (cursor capture forced on) -> private "cursor" scene
/// -> `obs_view` channel 0 -> `obs_view_add2` 512x512 mix -> x264 encoder
/// bound to the mix. Everything fallible happens in here, before the caller
/// mutates the output (`webcam::create` pattern).
pub fn create(
    settings: &Settings,
    region: Rect,
    monitors: &[MonitorInfo],
) -> Result<CursorTrack, String> {
    // Same intersection rule as the main scene (the cursor can only be over
    // recorded content while it is on one of these).
    let plan = region::plan_region(region, monitors)
        .map_err(|e| format!("Failed to plan the cursor track region: {e}"))?;
    let track_monitors: Vec<MonitorInfo> = plan
        .items
        .iter()
        .map(|item| monitors[item.monitor_index].clone())
        .collect();

    let scene = ObsScene::create("cursor")
        .map_err(|e| format!("Failed to create the cursor scene: {e}"))?;
    let mut sources = Vec::new();
    let mut items = Vec::new();
    for (i, m) in track_monitors.iter().enumerate() {
        // capture_cursor unconditionally TRUE — this track exists to record
        // the cursor even when the main scene's `cursor` setting hides it.
        let source_settings = platform::display_capture_settings(m, true);
        let source = ObsSource::create(
            platform::DISPLAY_CAPTURE_ID,
            &format!("cursor_display_{i}"),
            Some(&source_settings),
        )
        .map_err(|e| {
            format!(
                "Failed to create cursor-track display capture for monitor '{}': {e}",
                m.id
            )
        })?;
        let item = scene.add(&source);
        // Item scale stays 1.0: the mix shows a native-pixel crop; only the
        // per-tick position moves. Hidden until the first reposition decides
        // which monitor is active.
        item.set_visible(false);
        sources.push(source);
        items.push(item);
    }

    let mut view = ObsView::new().map_err(|e| format!("Failed to create the cursor view: {e}"))?;
    view.set_source_raw(0, scene.get_source());
    let video = view
        .add2(&VideoInfo {
            graphics_module: platform::GRAPHICS_MODULE,
            base_width: SIZE,
            base_height: SIZE,
            output_width: SIZE,
            output_height: SIZE,
            fps_num: settings.fps,
            fps_den: 1,
        })
        .map_err(|e| format!("Failed to create the cursor video mix: {e}"))?;

    // Same x264 settings as the webcam track — crucially the same keyint_sec:
    // mp4_output can only flush a fragment once EVERY track has caught up to
    // the track-0 keyframe, so a divergent GOP would stall the
    // crash-resilience cadence. The encoder name becomes the mp4 track name.
    let encoder = ObsEncoder::create_video(
        "obs_x264",
        "Cursor",
        Some(&webcam::encoder_settings(settings.crf)),
    )
    .map_err(|e| format!("Failed to create the cursor track encoder: {e}"))?;
    encoder.set_video(video);

    let track = CursorTrack {
        encoder,
        _view: view,
        items,
        _scene: scene,
        _sources: sources,
        monitors: track_monitors,
    };
    // Center on the current cursor immediately, so frames rendered before the
    // run loop arms the input-capture tick are already positioned.
    (track.positioner())(&platform::get_cursor_state());
    Ok(track)
}

impl CursorTrack {
    /// A recentering closure for `InputCapture::set_cursor_observer`: called
    /// once per rendered frame on the graphics thread with the frame's cursor
    /// sample. The captured item refs keep the scene items alive even if the
    /// chain is dropped first, but the recorder still clears the observer
    /// before any teardown (stale repositioning is pointless work).
    pub fn positioner(&self) -> Box<dyn FnMut(&CursorState) + Send> {
        let monitors = self.monitors.clone();
        let mut targets: Vec<Target> = self
            .items
            .iter()
            .map(|item| Target {
                item: item.clone(),
                visible: None,
            })
            .collect();
        Box::new(move |cursor| reposition(&monitors, &mut targets, cursor))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mon(x: i32, y: i32, w: u32, h: u32, scale: f64) -> MonitorInfo {
        MonitorInfo {
            id: format!("mon-{x},{y}"),
            alt_id: None,
            x,
            y,
            width: w,
            height: h,
            scale,
            is_primary: x == 0 && y == 0,
        }
    }

    #[test]
    fn track_index_follows_the_webcam() {
        assert_eq!(track_index(false), 1);
        assert_eq!(track_index(true), 2);
    }

    #[test]
    fn monitor_containment_is_half_open() {
        let m = mon(-1920, 200, 1920, 1080, 1.0);
        assert!(monitor_contains(&m, -1920, 200)); // top-left corner: inside
        assert!(monitor_contains(&m, -1, 1279)); // bottom-right pixel
        assert!(!monitor_contains(&m, 0, 200)); // right edge: excluded
        assert!(!monitor_contains(&m, -1920, 1280)); // bottom edge: excluded
        assert!(!monitor_contains(&m, -1921, 500));
    }

    #[test]
    fn active_monitor_picks_the_containing_display() {
        let mons = [mon(0, 0, 2560, 1440, 1.0), mon(2560, 0, 1920, 1080, 1.0)];
        assert_eq!(active_monitor(&mons, 100, 100), Some(0));
        // A shared edge belongs to the monitor whose origin it matches.
        assert_eq!(active_monitor(&mons, 2560, 100), Some(1));
        assert_eq!(active_monitor(&mons, 3000, 500), Some(1));
        // Off every recorded display: no active item (black box).
        assert_eq!(active_monitor(&mons, -1, 0), None);
        assert_eq!(active_monitor(&mons, 100, 2000), None);
    }

    #[test]
    fn item_pos_centers_the_hotspot() {
        let m = mon(0, 0, 2560, 1440, 1.0);
        // Cursor at the monitor origin: the origin pixel lands at (256,256).
        assert_eq!(item_pos(&m, 0, 0), (256.0, 256.0));
        // Cursor at (300,400): item shifts so that pixel hits the center.
        assert_eq!(item_pos(&m, 300, 400), (256.0 - 300.0, 256.0 - 400.0));
    }

    #[test]
    fn item_pos_uses_the_monitor_origin() {
        // Negative virtual-desktop origin: offsets are monitor-relative.
        let m = mon(-1920, 200, 1920, 1080, 1.0);
        assert_eq!(item_pos(&m, -1820, 300), (256.0 - 100.0, 256.0 - 100.0));
    }

    #[test]
    fn item_pos_converts_points_to_pixels_on_scaled_displays() {
        // macOS 2x Retina: coords are points, the capture frame is pixels, so
        // a 100-point offset is 200 capture pixels.
        let m = mon(0, 0, 1728, 1117, 2.0);
        assert_eq!(item_pos(&m, 100, 50), (256.0 - 200.0, 256.0 - 100.0));
    }
}
