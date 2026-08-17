//! Output track layout (RECORDER CORE R2).
//!
//! Single-track mode (the default, `ffmpeg_muxer`) is what obs-express always
//! did: one video track, and every audio source mixed down into one audio
//! track.
//!
//! `--multi-track` (the hybrid `mp4_output`) instead gives every stream its
//! own track: video track 0 = screen, video track 1 = webcam, and one audio
//! track per configured device — speakers first, then microphones, in the
//! order they were given. A screen + webcam + speaker + mic recording is
//! therefore a 4-track mp4.
//!
//! The mapping is pure so it can be unit-tested on every platform: libobs
//! routes a source to track `i` by giving the source the audio-mixer bitmask
//! `1 << i` and creating an audio encoder bound to mixer `i` (see
//! `recorder::Recorder`).

/// Maximum audio tracks a libobs output can carry (`MAX_AUDIO_MIXES` /
/// `MAX_OUTPUT_AUDIO_ENCODERS`, both 6 in libobs 32.1.2). Only relevant in
/// multi-track mode — single-track recordings mix any number of sources
/// (capped by `cli::MAX_AUDIO_SOURCES`) into their one track.
pub const MAX_AUDIO_TRACKS: usize = 6;

// A libobs bump that lowered either limit would otherwise go unnoticed:
// `audio_mixer_mask` folds out-of-range sources into the last track, so the
// devices past the new limit would silently share a track instead of failing
// validation. Both limits are checked — a mask needs a mixer AND an encoder
// slot.
const _: () = assert!(MAX_AUDIO_TRACKS <= obs_sys::MAX_AUDIO_MIXES as usize);
const _: () = assert!(MAX_AUDIO_TRACKS <= obs_sys::MAX_OUTPUT_AUDIO_ENCODERS as usize);
// The webcam occupies video track 1 and the cursor track the slot after it
// (`webcam::create` / `cursor_track::create` / `Recorder::new`), so a
// screen + webcam + cursor recording needs three video encoder slots.
const _: () = assert!(obs_sys::MAX_OUTPUT_VIDEO_ENCODERS >= 3);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AudioKind {
    /// One speaker (output/system audio) device.
    Speaker,
    /// One microphone (input) device.
    Microphone,
    /// Every configured device mixed together (single-track mode), or silence
    /// when no device is configured at all.
    Mixed,
}

impl AudioKind {
    /// The `kind` string in the `tracks` protocol payload.
    pub fn as_str(self) -> &'static str {
        match self {
            AudioKind::Speaker => "speaker",
            AudioKind::Microphone => "microphone",
            AudioKind::Mixed => "mixed",
        }
    }
}

/// One audio track of the recording, in output track order.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AudioTrack {
    pub kind: AudioKind,
    /// The device id feeding this track; `None` for a `Mixed` track.
    pub device: Option<String>,
    /// Human-readable track name. libobs hands the audio encoder's name to
    /// `mp4_output`, which writes it into the track's `udta` box — so this is
    /// what a player shows in its audio-track menu.
    pub name: String,
}

/// Resolves the audio track layout for a configuration.
///
/// Always returns at least one track: a libobs A/V output cannot start
/// without an audio encoder on track 0, so a recording with no audio device
/// still carries one (silent) `Mixed` track — exactly what obs-express
/// produced before multi-track existed.
///
/// Sources beyond [`MAX_AUDIO_TRACKS`] are not silently dropped here; callers
/// reject that configuration up front (`Cli::validate`), and
/// [`audio_mixer_mask`] folds any excess into the last track as a backstop.
pub fn plan_audio_tracks(
    speakers: &[String],
    mics: &[String],
    multi_track: bool,
) -> Vec<AudioTrack> {
    if !multi_track || (speakers.is_empty() && mics.is_empty()) {
        return vec![AudioTrack {
            kind: AudioKind::Mixed,
            device: None,
            name: "Audio".to_string(),
        }];
    }

    let speaker_tracks = speakers.iter().enumerate().map(|(i, device)| AudioTrack {
        kind: AudioKind::Speaker,
        device: Some(device.clone()),
        name: format!("Speaker {}", i + 1),
    });
    let mic_tracks = mics.iter().enumerate().map(|(i, device)| AudioTrack {
        kind: AudioKind::Microphone,
        device: Some(device.clone()),
        name: format!("Microphone {}", i + 1),
    });
    speaker_tracks.chain(mic_tracks).collect()
}

/// Audio-mixer bitmask for the audio source at `index` of the combined
/// speakers-then-microphones source list.
///
/// Multi-track: source `i` feeds mixer `i` alone, which is the mixer its
/// track's encoder reads. Single-track: every source feeds mixer 0, the one
/// mixer that is encoded (libobs defaults sources to `0xFF` — all mixers —
/// which works out the same, but being explicit keeps the two modes
/// symmetrical and makes a mode switch a pure re-apply).
pub fn audio_mixer_mask(index: usize, multi_track: bool) -> u32 {
    if multi_track {
        1 << index.min(MAX_AUDIO_TRACKS - 1)
    } else {
        1
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ids(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn single_track_always_yields_one_mixed_track() {
        for (speakers, mics) in [
            (ids(&[]), ids(&[])),
            (ids(&["spk"]), ids(&[])),
            (ids(&["spk0", "spk1"]), ids(&["mic0", "mic1"])),
        ] {
            let tracks = plan_audio_tracks(&speakers, &mics, false);
            assert_eq!(tracks.len(), 1);
            assert_eq!(tracks[0].kind, AudioKind::Mixed);
            assert_eq!(tracks[0].device, None);
        }
    }

    #[test]
    fn multi_track_gives_every_device_its_own_track() {
        // The headline case: screen + webcam + speaker + mic = 4 streams, of
        // which these two are the audio ones.
        let tracks = plan_audio_tracks(&ids(&["spk"]), &ids(&["mic"]), true);
        assert_eq!(tracks.len(), 2);
        assert_eq!(tracks[0].kind, AudioKind::Speaker);
        assert_eq!(tracks[0].device.as_deref(), Some("spk"));
        assert_eq!(tracks[0].name, "Speaker 1");
        assert_eq!(tracks[1].kind, AudioKind::Microphone);
        assert_eq!(tracks[1].device.as_deref(), Some("mic"));
        assert_eq!(tracks[1].name, "Microphone 1");
    }

    #[test]
    fn multi_track_orders_speakers_before_mics() {
        let tracks = plan_audio_tracks(&ids(&["s0", "s1"]), &ids(&["m0", "m1", "m2"]), true);
        let devices: Vec<&str> = tracks
            .iter()
            .map(|t| t.device.as_deref().unwrap())
            .collect();
        assert_eq!(devices, ["s0", "s1", "m0", "m1", "m2"]);
        let names: Vec<&str> = tracks.iter().map(|t| t.name.as_str()).collect();
        assert_eq!(
            names,
            [
                "Speaker 1",
                "Speaker 2",
                "Microphone 1",
                "Microphone 2",
                "Microphone 3"
            ]
        );
        assert_eq!(tracks.len(), MAX_AUDIO_TRACKS - 1);
    }

    #[test]
    fn multi_track_without_devices_still_has_a_silent_track() {
        // A libobs A/V output refuses to start without an audio encoder.
        let tracks = plan_audio_tracks(&[], &[], true);
        assert_eq!(tracks.len(), 1);
        assert_eq!(tracks[0].kind, AudioKind::Mixed);
    }

    #[test]
    fn mixer_masks_are_one_hot_per_track_in_multi_track() {
        assert_eq!(audio_mixer_mask(0, true), 0b1);
        assert_eq!(audio_mixer_mask(1, true), 0b10);
        assert_eq!(audio_mixer_mask(5, true), 0b10_0000);
        // Beyond the libobs track limit: fold into the last track rather than
        // shifting out of the mask (which would silently drop the device).
        assert_eq!(audio_mixer_mask(6, true), 0b10_0000);
        assert_eq!(audio_mixer_mask(99, true), 0b10_0000);
    }

    #[test]
    fn mixer_masks_are_all_mixer_zero_in_single_track() {
        for i in 0..8 {
            assert_eq!(audio_mixer_mask(i, false), 0b1);
        }
    }

    #[test]
    fn kind_strings_are_the_protocol_values() {
        assert_eq!(AudioKind::Speaker.as_str(), "speaker");
        assert_eq!(AudioKind::Microphone.as_str(), "microphone");
        assert_eq!(AudioKind::Mixed.as_str(), "mixed");
    }
}
