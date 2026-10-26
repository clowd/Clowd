//! One recording origin for both sidecars.
//!
//! The first screen packet identifies the encoder's timestamp origin. Texture
//! encoding labels newly rendered pixels with the preceding tick's timestamp,
//! so its origin is the first observed tick strictly after that timestamp.
//! Writers publish that tick's accumulated pause offset alongside the origin:
//! pauses before frame zero must not be subtracted from the recording again.
//! The packet callback only publishes timing and never waits or performs I/O.

use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

pub struct FrameClock {
    anchor_cts: AtomicU64,
    texture: AtomicBool,
    null_timing: AtomicBool,
    t0_ns: AtomicU64,
    base_offset: AtomicU64,
    interval_ns: AtomicU64,
    /// Writers must publish the origin and its offset together. Without this,
    /// a losing writer could overwrite the winning origin's base offset.
    publish_lock: Mutex<()>,
}

impl FrameClock {
    pub fn new() -> Self {
        Self {
            anchor_cts: AtomicU64::new(0),
            texture: AtomicBool::new(false),
            null_timing: AtomicBool::new(false),
            t0_ns: AtomicU64::new(0),
            base_offset: AtomicU64::new(0),
            interval_ns: AtomicU64::new(0),
            publish_lock: Mutex::new(()),
        }
    }

    /// Only before output.start(), while neither sidecar is armed.
    pub fn reset(&self, fps: u32) {
        self.anchor_cts.store(0, Ordering::Relaxed);
        self.texture.store(false, Ordering::Relaxed);
        self.null_timing.store(false, Ordering::Relaxed);
        self.t0_ns.store(0, Ordering::Relaxed);
        self.base_offset.store(0, Ordering::Relaxed);
        self.set_fps(fps);
    }

    fn set_fps(&self, fps: u32) {
        self.interval_ns
            .store(1_000_000_000 / u64::from(fps.max(1)), Ordering::Release);
    }

    pub fn t0(&self) -> u64 {
        self.t0_ns.load(Ordering::Acquire)
    }

    /// Read after t0() returns a nonzero origin, acquiring its publication.
    pub fn base_offset(&self) -> u64 {
        self.base_offset.load(Ordering::Relaxed)
    }

    /// Called only by writers. Entries are (frame_ns, sampled pause offset),
    /// ordered by ascending frame_ns.
    pub fn try_resolve(&self, buffered_ticks: &[(u64, u64)], buffer_full: bool) -> Option<u64> {
        let current = self.t0();
        if current != 0 {
            return Some(current);
        }
        let anchor = self.anchor_cts.load(Ordering::Acquire);
        let texture = self.texture.load(Ordering::Relaxed);
        let null = self.null_timing.load(Ordering::Acquire);
        let interval = self.interval_ns.load(Ordering::Acquire);
        let (candidate, fallback) =
            resolve_t0(anchor, texture, interval, buffered_ticks, buffer_full, null)?;
        // A duplicate frame can have no tick at its timestamp. Its offset
        // belongs to the latest preceding tick, not a later post-pause tick.
        let base = buffered_ticks
            .iter()
            .rev()
            .find(|&&(frame_ns, _)| frame_ns <= candidate)
            .map(|&(_, offset)| offset);
        match base {
            Some(base) => self.publish(candidate, base, fallback),
            None if buffer_full => self.publish(candidate, 0, true),
            None => None,
        }
    }

    /// Closing must make buffered observations writable even without a packet.
    pub fn resolve_for_close(&self, first_buffered_tick: Option<(u64, u64)>) -> Option<u64> {
        let current = self.t0();
        if current != 0 {
            return Some(current);
        }
        let (frame_ns, offset) = first_buffered_tick?;
        self.publish(frame_ns, offset, true)
    }

    fn publish(&self, candidate: u64, base: u64, fallback: bool) -> Option<u64> {
        if candidate == 0 {
            return None;
        }
        // Only startup writers take this lock; the packet callback never does.
        let Ok(_guard) = self.publish_lock.lock() else {
            return None;
        };
        let current = self.t0();
        if current != 0 {
            return Some(current);
        }
        self.base_offset.store(base, Ordering::Relaxed);
        self.t0_ns.store(candidate, Ordering::Release);
        if fallback {
            eprintln!(
                "Warning: sidecar timing used a fallback origin; packet timing was unavailable or startup buffering reached its limit"
            );
        }
        Some(candidate)
    }
}

fn resolve_t0(
    anchor: u64,
    texture: bool,
    interval: u64,
    ticks: &[(u64, u64)],
    full: bool,
    null: bool,
) -> Option<(u64, bool)> {
    if null || (anchor == 0 && full) {
        return ticks
            .first()
            .filter(|&&(t, _)| t != 0)
            .map(|&(t, _)| (t, true));
    }
    if anchor == 0 {
        return None;
    }
    if !texture {
        return Some((anchor, false));
    }
    ticks
        .iter()
        .find(|&&(t, _)| t > anchor)
        .map(|&(t, _)| (t, false))
        .or_else(|| full.then(|| (anchor.saturating_add(interval.max(1)), true)))
}

/// Uses the packet timebase specified by the sidecar clock contract. Current
/// recorder configurations use integer fps, hence timebase_num == 1.
fn packet_anchor(cts: u64, pts: i64, num: i32, den: i32) -> Option<u64> {
    if num <= 0 || den <= 0 {
        return None;
    }
    let pts_ns = i128::from(pts) * 1_000_000_000 * i128::from(num) / i128::from(den);
    let anchor = (i128::from(cts) - pts_ns).clamp(0, i128::from(u64::MAX)) as u64;
    (anchor != 0).then_some(anchor)
}

unsafe extern "C" fn packet_cb(
    _output: *mut obs_sys::obs_output_t,
    pkt: *mut obs_sys::encoder_packet,
    pkt_time: *mut obs_sys::encoder_packet_time,
    param: *mut c_void,
) {
    if pkt.is_null() || param.is_null() {
        return;
    }
    let packet = &*pkt;
    if packet.type_ != obs_sys::obs_encoder_type_OBS_ENCODER_VIDEO || packet.track_idx != 0 {
        return;
    }
    let clock = &*(param as *const FrameClock);
    if clock.t0() != 0
        || clock.anchor_cts.load(Ordering::Acquire) != 0
        || clock.null_timing.load(Ordering::Acquire)
    {
        return;
    }
    if pkt_time.is_null() {
        clock.null_timing.store(true, Ordering::Release);
        return;
    }
    let Some(anchor) = packet_anchor(
        (*pkt_time).cts,
        packet.pts,
        packet.timebase_num,
        packet.timebase_den,
    ) else {
        clock.null_timing.store(true, Ordering::Release);
        return;
    };
    let encoder = packet.encoder;
    // Texture queries are reachable only on the active output's track-0 encoder, whose video mix exists.
    let texture = !encoder.is_null()
        && obs_sys::obs_encoder_get_caps(encoder) & obs_sys::OBS_ENCODER_CAP_PASS_TEXTURE != 0
        && (obs_sys::obs_encoder_video_tex_active(encoder, obs_sys::video_format_VIDEO_FORMAT_NV12)
            || obs_sys::obs_encoder_video_tex_active(
                encoder,
                obs_sys::video_format_VIDEO_FORMAT_P010,
            ));
    // libobs serializes packet callbacks. Publish the family before its anchor.
    clock.texture.store(texture, Ordering::Relaxed);
    let _ = clock
        .anchor_cts
        .compare_exchange(0, anchor, Ordering::Release, Ordering::Relaxed);
}

/// Declared before the output in Recorder so unregistering cannot use a freed
/// output. Its Arc also keeps the callback parameter alive until removal.
pub struct FrameClockCallback {
    output: *mut obs_sys::obs_output_t,
    clock: Arc<FrameClock>,
}

impl FrameClockCallback {
    /// # Safety
    /// The output must remain alive until this registration is dropped.
    pub unsafe fn register(output: *mut obs_sys::obs_output_t, clock: Arc<FrameClock>) -> Self {
        let param = Arc::as_ptr(&clock) as *mut c_void;
        obs_sys::obs_output_add_packet_callback(output, Some(packet_cb), param);
        Self { output, clock }
    }
}

impl Drop for FrameClockCallback {
    fn drop(&mut self) {
        let param = Arc::as_ptr(&self.clock) as *mut c_void;
        unsafe {
            obs_sys::obs_output_remove_packet_callback(self.output, Some(packet_cb), param);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn raw_uses_the_packet_origin_without_waiting_for_a_tick() {
        assert_eq!(resolve_t0(100, false, 10, &[], false, false), Some((100, false)));
    }

    #[test]
    fn texture_uses_a_strict_ceiling_across_a_start_stall() {
        assert_eq!(
            resolve_t0(100, true, 10, &[(90, 0), (100, 0), (140, 0)], false, false),
            Some((140, false))
        );
        assert_eq!(
            resolve_t0(100, true, 10, &[(90, 0), (100, 0)], false, false),
            None
        );
        assert_eq!(
            resolve_t0(100, true, 10, &[(90, 0), (100, 0)], true, false),
            Some((110, true))
        );
    }

    #[test]
    fn missing_timing_waits_or_falls_back_without_inventing_a_tick() {
        let ticks = [(90, 0), (100, 0)];
        assert_eq!(resolve_t0(0, false, 10, &ticks, false, false), None);
        assert_eq!(resolve_t0(0, false, 10, &ticks, true, false), Some((90, true)));
        assert_eq!(resolve_t0(0, false, 10, &ticks, false, true), Some((90, true)));
        assert_eq!(resolve_t0(0, false, 10, &[], true, true), None);
    }

    #[test]
    fn packet_pts_is_removed_with_signed_checked_timebase_math() {
        assert_eq!(packet_anchor(2_000_000_000, 30, 1, 30), Some(1_000_000_000));
        assert_eq!(packet_anchor(1_000_000_000, -30, 1, 30), Some(2_000_000_000));
        assert_eq!(packet_anchor(1, 30, 1, 30), None);
        assert_eq!(packet_anchor(1, 0, 1, 0), None);
    }

    #[test]
    fn the_first_writer_to_resolve_wins_and_reset_clears_the_session() {
        let clock = FrameClock::new();
        clock.reset(60);
        clock.anchor_cts.store(100, Ordering::Release);
        assert_eq!(clock.try_resolve(&[(100, 20)], false), Some(100));
        assert_eq!(clock.base_offset(), 20);
        assert_eq!(clock.publish(200, 90, true), Some(100));
        assert_eq!(clock.base_offset(), 20);
        clock.reset(30);
        assert_eq!(clock.t0(), 0);
        assert_eq!(clock.base_offset(), 0);
        assert_eq!(clock.try_resolve(&[(200, 30)], false), None);
        assert_eq!(clock.resolve_for_close(Some((200, 30))), Some(200));
        assert_eq!(clock.base_offset(), 30);
    }

    #[test]
    fn coalesced_window_snapshots_do_not_discard_the_startup_ceiling() {
        let clock = FrameClock::new();
        clock.reset(30);
        clock.texture.store(true, Ordering::Relaxed);
        clock.anchor_cts.store(100, Ordering::Release);
        // The worker enumerates only at 130, but retains every received
        // trigger for calibration rather than using its snapshot history.
        let startup_ticks = [(110, 20), (120, 20), (130, 20)];
        assert_eq!(clock.try_resolve(&startup_ticks, false), Some(110));
        assert_eq!(clock.base_offset(), 20);
    }

    #[test]
    fn a_duplicate_frame_uses_the_latest_preceding_ticks_offset() {
        let clock = FrameClock::new();
        clock.reset(30);
        clock.anchor_cts.store(125, Ordering::Release);
        assert_eq!(
            clock.try_resolve(&[(90, 3), (100, 20), (140, 50)], false),
            Some(125)
        );
        assert_eq!(clock.base_offset(), 20);
    }

    #[test]
    fn a_missing_preceding_tick_waits_until_fallback_is_allowed() {
        let clock = FrameClock::new();
        clock.reset(30);
        clock.anchor_cts.store(125, Ordering::Release);
        assert_eq!(clock.try_resolve(&[(140, 50)], false), None);
        assert_eq!(clock.t0(), 0);
        // Writers allow the same bounded fallback when closing.
        assert_eq!(clock.try_resolve(&[(140, 50)], true), Some(125));
        assert_eq!(clock.base_offset(), 0);
    }

    #[test]
    fn texture_origin_uses_the_offset_of_its_selected_tick() {
        let clock = FrameClock::new();
        clock.reset(30);
        clock.texture.store(true, Ordering::Relaxed);
        clock.anchor_cts.store(100, Ordering::Release);
        assert_eq!(
            clock.try_resolve(&[(90, 0), (100, 0), (140, 30), (180, 60)], false),
            Some(140)
        );
        assert_eq!(clock.base_offset(), 30);
    }
}
