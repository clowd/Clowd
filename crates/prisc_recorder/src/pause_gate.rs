//! Spacing between pause and unpause so libobs's encoder pause bookkeeping
//! cannot wedge.
//!
//! The recorder's outputs are OBS_OUTPUT_ENCODED, so `obs_output_pause` goes
//! through `obs_encoded_output_pause` (libobs/obs-output.c). A pause stamps
//! every encoder's `ts_start` with `get_closest_v_ts`, a frame-grid time one
//! to two frame intervals ahead of now; an unpause stamps `ts_end` the same
//! way. The audio encoders then apply the span in `audio_pause_check`
//! (obs-encoder.c) one 1024-frame chunk at a time: the chunk holding
//! `ts_start` is cut short, chunks inside the span are dropped, and the chunk
//! holding `ts_end` is trimmed and clears the span. That last step only runs
//! for a chunk that starts *after* `ts_start`. If `ts_end` falls in the same
//! chunk as `ts_start` (a resume less than about a frame after the pause makes
//! them equal), the span is never cleared and every later audio chunk is
//! dropped. Audio packets stop, the output's interleaver holds back the video
//! behind them, the muxer never sees a packet past the stop time, and `quit`
//! waits out the whole stop deadline (done{failed} -99, truncated file).
//!
//! `obs_output_stop` unpauses before it stops, so a quit while paused is an
//! unpause too.
//!
//! The other direction has its own window: after an unpause, libobs refuses a
//! new pause (`pause_can_start`) until the video frame at `ts_end` and the
//! audio chunk holding it have been processed, and audio runs behind real
//! time by its buffering (up to about a second). A refused pause in that
//! window is retried rather than reported as a failure.

use std::time::Duration;

/// Frames per libobs audio chunk (`AUDIO_OUTPUT_FRAMES`).
const AUDIO_CHUNK_FRAMES: u64 = prisc_obs_sys::AUDIO_OUTPUT_FRAMES as u64;

/// One libobs audio chunk in ns at the recorder's sample rate, rounded up.
const AUDIO_CHUNK_NS: u64 = (AUDIO_CHUNK_FRAMES * 1_000_000_000).div_ceil(
    crate::encoder_config::AUDIO_SAMPLE_RATE as u64,
);

/// How long after an unpause a refused pause keeps being retried: libobs's
/// maximum audio buffering (45 ticks, about 960 ms) plus the two frames to
/// `ts_end`, with headroom.
const PAUSE_SETTLE_NS: u64 = 3_000_000_000;

/// How often a refused pause is retried inside the settle window.
pub const PAUSE_RETRY_INTERVAL: Duration = Duration::from_millis(5);

/// The minimum time between a pause and the following unpause.
///
/// With `P` read after `obs_output_pause(true)` returns and `R` read before
/// `obs_output_pause(false)` is called, `get_closest_v_ts` guarantees
/// `ts_start <= P + 2i` and `ts_end > R + i` (i = frame interval), whatever
/// the last video timestamp was. So `R - P >= i + chunk` puts `ts_end` more
/// than one audio chunk past `ts_start`, outside the chunk that holds
/// `ts_start`. One more chunk is headroom.
pub fn unpause_hold_ns(frame_interval_ns: u64) -> u64 {
    frame_interval_ns + 2 * AUDIO_CHUNK_NS
}

/// The libobs-clock (`os_gettime_ns`) times of the last pause and unpause.
#[derive(Debug, Default)]
pub struct PauseGate {
    paused_at_ns: Option<u64>,
    unpaused_at_ns: Option<u64>,
}

impl PauseGate {
    /// Record a pause libobs accepted; `now_ns` is read after the call.
    pub fn paused(&mut self, now_ns: u64) {
        self.paused_at_ns = Some(now_ns);
    }

    /// Record an unpause libobs accepted; `now_ns` is read after the call.
    pub fn unpaused(&mut self, now_ns: u64) {
        self.paused_at_ns = None;
        self.unpaused_at_ns = Some(now_ns);
    }

    /// How long to wait before unpausing (a resume, or a stop while paused)
    /// so the unpause cannot land in the pause's own audio chunk. Zero when
    /// not paused or when the pause is already old enough.
    pub fn unpause_delay(&self, now_ns: u64, frame_interval_ns: u64) -> Duration {
        match self.paused_at_ns {
            Some(paused_at) => Duration::from_nanos(
                (paused_at + unpause_hold_ns(frame_interval_ns)).saturating_sub(now_ns),
            ),
            None => Duration::ZERO,
        }
    }

    /// Whether a pause libobs just refused at `now_ns` should be retried: only
    /// inside the settle window after an unpause, where a refusal means the
    /// encoders have not consumed the unpause yet.
    pub fn retry_refused_pause(&self, now_ns: u64) -> bool {
        self.unpaused_at_ns
            .is_some_and(|unpaused_at| now_ns < unpaused_at + PAUSE_SETTLE_NS)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE_RATE: u64 = crate::encoder_config::AUDIO_SAMPLE_RATE as u64;

    /// libobs's `struct pause_data`, one per encoder.
    #[derive(Debug, Default, Clone, Copy)]
    struct PauseData {
        last_video_ts: u64,
        ts_start: u64,
        ts_end: u64,
        ts_offset: u64,
    }

    fn frames_to_ns(frames: u64) -> u64 {
        frames * 1_000_000_000 / SAMPLE_RATE
    }

    fn ns_to_frames(ns: u64) -> u64 {
        ns * SAMPLE_RATE / 1_000_000_000
    }

    /// A port of the libobs pause arithmetic for one video and one audio
    /// encoder (obs-output.c `obs_encoded_output_pause`, obs-encoder.c
    /// `video_pause_check_internal` / `audio_pause_check_internal`).
    struct Libobs {
        interval: u64,
        video: PauseData,
        audio: PauseData,
        /// Audio chunks dropped after the pause span should have ended.
        audio_frames_kept: u64,
    }

    impl Libobs {
        fn closest_v_ts(&self, now: u64) -> u64 {
            let last = self.video.last_video_ts;
            last + ((now - last + 2 * self.interval) / self.interval) * self.interval
        }

        fn can_start(p: &PauseData) -> bool {
            p.ts_start == 0 && p.ts_end == 0
        }

        fn pause(&mut self, now: u64) -> bool {
            let ts = self.closest_v_ts(now);
            if !Self::can_start(&self.video) || !Self::can_start(&self.audio) {
                return false;
            }
            self.video.ts_start = ts;
            self.audio.ts_start = ts;
            true
        }

        fn unpause(&mut self, now: u64) -> bool {
            let ts = self.closest_v_ts(now);
            for p in [&mut self.video, &mut self.audio] {
                if p.ts_start == 0 || p.ts_end != 0 {
                    return false;
                }
            }
            for p in [&mut self.video, &mut self.audio] {
                p.ts_end = ts;
                p.ts_offset += p.ts_end - p.ts_start;
            }
            true
        }

        fn video_frame(&mut self, ts: u64) {
            let p = &mut self.video;
            p.last_video_ts = ts;
            if p.ts_start != 0 && ts == p.ts_end {
                p.ts_start = 0;
                p.ts_end = 0;
            }
        }

        fn audio_chunk(&mut self, ts: u64) {
            let p = &mut self.audio;
            let mut frames = AUDIO_CHUNK_FRAMES;
            if p.ts_start != 0 {
                let end_ts = ts + frames_to_ns(frames);
                if p.ts_start >= ts {
                    if p.ts_start <= end_ts {
                        frames = ns_to_frames(p.ts_start - ts);
                    }
                } else if p.ts_end >= ts && p.ts_end <= end_ts {
                    frames -= ns_to_frames(p.ts_end - ts);
                    p.ts_start = 0;
                    p.ts_end = 0;
                } else {
                    frames = 0;
                }
            }
            self.audio_frames_kept += frames;
        }

        fn settled(&self) -> bool {
            Self::can_start(&self.video) && Self::can_start(&self.audio)
        }
    }

    /// Simulate a recording at `fps` whose video grid starts at `video_phase`
    /// and audio grid at `audio_phase`, pausing at `pause_at` and unpausing as
    /// soon as `wait` allows. Video frames are processed at their own time,
    /// audio chunks `buffering` ns after they end. Returns whether libobs's
    /// pause state cleared and the kept audio matched the run minus the span.
    fn simulate(
        fps: u64,
        video_phase: u64,
        audio_phase: u64,
        pause_at: u64,
        buffering: u64,
        gate_resume: bool,
    ) -> bool {
        let interval = 1_000_000_000 / fps;
        let mut obs = Libobs {
            interval,
            video: PauseData::default(),
            audio: PauseData::default(),
            audio_frames_kept: 0,
        };
        let mut gate = PauseGate::default();
        let base = 1_000_000_000;
        let end = base + 3_000_000_000;
        let mut next_video = base + video_phase;
        let mut audio_index = 0u64;
        let audio_ts = |k: u64| base + audio_phase + frames_to_ns(k * AUDIO_CHUNK_FRAMES);
        let pause_at = base + 500_000_000 + pause_at;
        let mut pause_done = false;
        let mut resume_at = None;
        let mut resumed = false;
        let mut now = base;
        while now < end {
            while next_video <= now {
                obs.video_frame(next_video);
                next_video += interval;
            }
            while audio_ts(audio_index + 1) + buffering <= now {
                obs.audio_chunk(audio_ts(audio_index));
                audio_index += 1;
            }
            if !pause_done && now >= pause_at {
                assert!(obs.pause(now));
                gate.paused(now);
                pause_done = true;
                let wait = if gate_resume {
                    gate.unpause_delay(now, interval).as_nanos() as u64
                } else {
                    0
                };
                resume_at = Some(now + wait);
            }
            if !resumed && resume_at.is_some_and(|r| now >= r) {
                assert!(obs.unpause(now));
                gate.unpaused(now);
                resumed = true;
            }
            now += 100_000;
        }
        let span = obs.audio.ts_offset;
        let expected = audio_index * AUDIO_CHUNK_FRAMES - ns_to_frames(span);
        // Truncating ns to frames at the two cut points can lose a frame each.
        obs.settled() && obs.audio_frames_kept + 2 >= expected
    }

    #[test]
    fn model_reproduces_the_wedge_without_the_gate() {
        // An immediate resume wedges libobs's audio pause state for some
        // phase of the video and audio grids: the bug this module prevents.
        let wedged = (0..33).any(|ms| !simulate(30, 0, ms * 650_000, ms * 1_000_000, 64_000_000, false));
        assert!(wedged, "the libobs model no longer reproduces the immediate resume wedge");
    }

    #[test]
    fn gated_resume_always_settles() {
        for fps in [10, 24, 30, 60, 120] {
            let interval = 1_000_000_000 / fps;
            for video_step in 0..4 {
                for audio_step in 0..7 {
                    for pause_step in 0..9 {
                        let video_phase = video_step * interval / 4;
                        let audio_phase = audio_step * AUDIO_CHUNK_NS / 7;
                        let pause_at = pause_step * interval / 4 + 37_000;
                        assert!(
                            simulate(fps, video_phase, audio_phase, pause_at, 64_000_000, true),
                            "wedged: fps {fps} video {video_phase} audio {audio_phase} pause {pause_at}"
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn unpause_delay_counts_from_the_pause() {
        let interval = 33_333_333;
        let hold = unpause_hold_ns(interval);
        let mut gate = PauseGate::default();
        assert_eq!(gate.unpause_delay(5, interval), Duration::ZERO);
        gate.paused(1_000);
        assert_eq!(gate.unpause_delay(1_000, interval), Duration::from_nanos(hold));
        assert_eq!(gate.unpause_delay(1_000 + hold / 2, interval), Duration::from_nanos(hold - hold / 2));
        assert_eq!(gate.unpause_delay(1_000 + hold, interval), Duration::ZERO);
        assert_eq!(gate.unpause_delay(1_000 + 10 * hold, interval), Duration::ZERO);
        gate.unpaused(1_000 + hold);
        assert_eq!(gate.unpause_delay(1_000 + hold, interval), Duration::ZERO);
    }

    #[test]
    fn hold_exceeds_a_frame_and_an_audio_chunk() {
        assert_eq!(AUDIO_CHUNK_NS, 21_333_334);
        for fps in [1, 30, 60, 240] {
            let interval = 1_000_000_000 / fps;
            assert!(unpause_hold_ns(interval) > interval + AUDIO_CHUNK_NS);
        }
    }

    #[test]
    fn refused_pause_retries_only_after_a_recent_unpause() {
        let mut gate = PauseGate::default();
        assert!(!gate.retry_refused_pause(1_000));
        gate.paused(1_000);
        assert!(!gate.retry_refused_pause(2_000));
        gate.unpaused(10_000);
        assert!(gate.retry_refused_pause(10_000));
        assert!(gate.retry_refused_pause(10_000 + PAUSE_SETTLE_NS - 1));
        assert!(!gate.retry_refused_pause(10_000 + PAUSE_SETTLE_NS));
    }
}
