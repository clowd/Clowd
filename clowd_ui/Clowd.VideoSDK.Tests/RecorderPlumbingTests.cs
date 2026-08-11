using System;
using System.Linq;
using System.Text.Json;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI;
using Clowd.UI.VideoEditor;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The seam between the recorder and the editor: which arguments obs-express is spawned with,
    /// what its <c>tracks</c> report is read as, and how that becomes the audio row labels a fresh
    /// edit opens with (Clowd.Ui exposes its internals to this project).
    ///
    /// The report shapes here are obs-express's own, copied from its documented protocol: a
    /// multi-track recording lists one <c>{"index","kind","device","name"}</c> entry per device
    /// (<c>kind</c> = "speaker" | "microphone"), a single-track one lists a single "mixed" entry,
    /// and an older recorder sends no <c>audio</c> array at all. Everything downstream has to
    /// survive the last case, because the rows themselves come from probing the file.
    /// </summary>
    public class RecorderPlumbingTests
    {
        // ------------------------------------------------------------------ arguments

        /// <summary>By default a configured device is also captured — the toggles, not the ids,
        /// decide the track layout (a stock install has both ids set to "default" with both
        /// toggles off, and must not get multi-track silent rows). The captureSpeaker/captureMic
        /// overrides express "configured but turned off".</summary>
        private static SettingsRecording Settings(bool separate = true, string speaker = "default",
            string mic = "default", bool webcam = false, bool? captureSpeaker = null, bool? captureMic = null)
            => new SettingsRecording
            {
                SeparateAudioTracks = separate,
                SpeakerDeviceId = speaker,
                CaptureSpeaker = captureSpeaker ?? !String.IsNullOrEmpty(speaker),
                MicrophoneDeviceId = mic,
                CaptureMicrophone = captureMic ?? !String.IsNullOrEmpty(mic),
                CaptureWebcam = webcam,
                WebcamDeviceId = webcam ? "cam-1" : "",
            };

        private static string[] Build(SettingsRecording settings)
            => ObsArguments.Build(new ScreenRect(10, 20, 640, 480), @"C:\out\video.mp4", @"C:\out\obs.json", settings)
                           .ToArray();

        [Fact]
        public void The_recorder_is_spawned_with_the_region_output_settings_and_pause()
        {
            var args = Build(Settings());

            Assert.Equal("--region", args[0]);
            Assert.Equal("10,20,640,480", args[1]);
            Assert.Equal("--output", args[2]);
            Assert.Equal(@"C:\out\video.mp4", args[3]);
            Assert.Equal("--settings", args[4]);
            Assert.Equal(@"C:\out\obs.json", args[5]);
            Assert.Equal("--pause", args[6]);
        }

        [Fact]
        public void Separate_audio_tracks_adds_multi_track_when_a_device_is_captured()
        {
            Assert.Contains("--multi-track", Build(Settings()));
            Assert.Contains("--multi-track", Build(Settings(speaker: "default", mic: "")));
            Assert.Contains("--multi-track", Build(Settings(speaker: "", mic: "default")));
        }

        /// <summary>With no audio device there are no tracks to separate — the recorder writes its
        /// one silent track either way, so the single-track muxer (which is also the more compatible
        /// file) is left alone.</summary>
        [Fact]
        public void With_no_audio_device_the_flag_is_left_off()
        {
            Assert.DoesNotContain("--multi-track", Build(Settings(speaker: "", mic: "")));
            Assert.DoesNotContain("--multi-track", Build(Settings(speaker: null, mic: null)));
        }

        /// <summary>The stock install: both device ids default to "default" but neither capture
        /// toggle is on. The recorder plans one track per configured device regardless of mute
        /// state, so gating on the ids would give every default recording two confidently-named
        /// silent audio rows — the toggles are what earn a track.</summary>
        [Fact]
        public void Devices_configured_but_not_captured_do_not_ask_for_multi_track()
        {
            Assert.DoesNotContain("--multi-track", Build(Settings(captureSpeaker: false, captureMic: false)));
        }

        /// <summary>The common speaker-only case: the mic id stays configured (a live unmute needs
        /// the device listed) but its toggle is off, so it must not add a silent "Microphone" track
        /// — while the captured speaker still gets the multi-track layout.</summary>
        [Fact]
        public void A_captured_speaker_with_the_mic_configured_but_off_still_separates_tracks()
        {
            Assert.Contains("--multi-track", Build(Settings(captureMic: false)));
            Assert.DoesNotContain("--multi-track", Build(Settings(captureSpeaker: false, mic: "")));
        }

        [Fact]
        public void The_setting_turned_off_records_one_mixed_track()
        {
            Assert.DoesNotContain("--multi-track", Build(Settings(separate: false)));
        }

        /// <summary>A webcam is a second video track, which only the hybrid mp4 output carries: the
        /// recorder refuses to start on a webcam device without the flag, so it is not the audio
        /// setting's to withhold.</summary>
        [Fact]
        public void A_webcam_forces_multi_track_whatever_the_audio_setting_says()
        {
            Assert.Contains("--multi-track", Build(Settings(separate: false, speaker: "", mic: "", webcam: true)));

            // …but only when a camera is actually picked, which is the same condition the settings
            // file's webcam_device uses.
            var ticked = Settings(separate: false, speaker: "", mic: "", webcam: true);
            ticked.WebcamDeviceId = "";
            Assert.DoesNotContain("--multi-track", Build(ticked));
        }

        [Fact]
        public void Null_settings_never_ask_for_multi_track()
        {
            Assert.False(ObsArguments.UsesMultiTrack(null));
        }

        // ------------------------------------------------------------------ tracks report

        private static ObsTracks Parse(string json, ObsTracks previous = null)
        {
            using var doc = JsonDocument.Parse(json);
            return ObsCapturer.ParseTracks(doc.RootElement, previous);
        }

        [Fact]
        public void A_multi_track_report_is_read_as_its_video_and_audio_tracks()
        {
            var tracks = Parse("""
                {"type":"started_recording","tracks":{
                  "screen":{"index":0,"width":1920,"height":1080},
                  "webcam":{"index":1,"width":1280,"height":720},
                  "audio":[{"index":0,"kind":"speaker","device":"default","name":"Speaker 1"},
                           {"index":1,"kind":"microphone","device":"mic-id","name":"Microphone 1"}]}}
                """);

            Assert.Equal(new ObsTrackInfo(0, 1920, 1080), tracks.Screen);
            Assert.Equal(new ObsTrackInfo(1, 1280, 720), tracks.Webcam);
            Assert.Equal(new[] { new ObsAudioTrackInfo(0, "speaker"), new ObsAudioTrackInfo(1, "microphone") },
                tracks.Audio);
        }

        [Fact]
        public void A_single_track_report_is_read_as_one_mixed_track()
        {
            var tracks = Parse("""
                {"tracks":{"screen":{"index":0,"width":800,"height":600},
                           "audio":[{"index":0,"kind":"mixed","device":null,"name":"Audio"}]}}
                """);

            Assert.Null(tracks.Webcam);
            Assert.Equal(new ObsAudioTrackInfo(0, "mixed"), Assert.Single(tracks.Audio));
        }

        /// <summary>The <c>audio</c> array is optional — a recorder that predates it still reports
        /// its video tracks, and the editor still gets its rows from the file.</summary>
        [Fact]
        public void A_report_without_an_audio_array_reads_as_no_audio_tracks()
        {
            var tracks = Parse("""{"tracks":{"screen":{"index":0,"width":800,"height":600}}}""");

            Assert.NotNull(tracks.Screen);
            Assert.Empty(tracks.Audio);

            // …as does one whose audio field is not an array at all
            Assert.Empty(Parse("""{"tracks":{"screen":{"index":0},"audio":{"index":0}}}""").Audio);
            Assert.Empty(Parse("""{"tracks":{"screen":{"index":0},"audio":null}}""").Audio);
        }

        [Fact]
        public void Malformed_audio_entries_are_dropped_rather_than_thrown_on()
        {
            var tracks = Parse("""
                {"tracks":{"screen":{"index":0,"width":800,"height":600},
                           "audio":[7, "microphone", null, [],
                                    {"kind":"microphone"},
                                    {"index":"1","kind":"speaker"},
                                    {"index":-1,"kind":"speaker"},
                                    {"index":1},
                                    {"index":2,"kind":42},
                                    {"index":3,"kind":"microphone"}]}}
                """);

            // only the entries that name a stream survive; a missing/invalid kind is simply unnamed.
            Assert.Equal(new[] { new ObsAudioTrackInfo(1, null), new ObsAudioTrackInfo(2, null),
                                 new ObsAudioTrackInfo(3, "microphone") },
                tracks.Audio);
        }

        /// <summary>stopped_recording is the second report; a message carrying none must not clear
        /// what started_recording said.</summary>
        [Fact]
        public void A_message_with_no_usable_tracks_keeps_the_previous_report()
        {
            var previous = Parse("""
                {"tracks":{"screen":{"index":0,"width":800,"height":600},
                           "audio":[{"index":0,"kind":"microphone"}]}}
                """);

            Assert.Same(previous, Parse("""{"type":"stopped_recording","code":0}""", previous));
            Assert.Same(previous, Parse("""{"tracks":null}""", previous));
            Assert.Same(previous, Parse("""{"tracks":{}}""", previous)); // no screen track: unusable
            Assert.Null(Parse("""{"type":"stopped_recording"}""", null));
        }

        // ------------------------------------------------------------------ labels

        private static SessionAudioTrack Track(int index, string kind)
            => new SessionAudioTrack { Index = index, Kind = kind };

        [Fact]
        public void Track_kinds_become_the_editors_row_labels()
        {
            Assert.Equal(new[] { "System Audio", "Microphone" },
                AudioTrackLabels.From(new[] { Track(0, "speaker"), Track(1, "microphone") }));

            // the recorder's casing is its own business
            Assert.Equal(new[] { "Microphone" }, AudioTrackLabels.From(new[] { Track(0, "Microphone") }));
        }

        /// <summary>Labels are index-aligned with the probed streams, so an entry names the row it
        /// claims rather than the one it happens to be listed at.</summary>
        [Fact]
        public void Labels_land_on_the_index_they_claim()
        {
            var names = AudioTrackLabels.From(new[] { Track(2, "microphone"), Track(0, "speaker") });

            Assert.Equal(new[] { "System Audio", null, "Microphone" }, names);
        }

        /// <summary>Everything the recorder cannot name falls through to the model's own
        /// "Audio"/"Audio N" — the labels only ever decorate rows the probe created.</summary>
        [Fact]
        public void Nothing_to_say_leaves_the_naming_to_the_fallback()
        {
            Assert.Null(AudioTrackLabels.From(null));
            Assert.Null(AudioTrackLabels.From(Array.Empty<SessionAudioTrack>()));
            Assert.Null(AudioTrackLabels.From(new[] { Track(0, "mixed") }));  // a single mixed track
            Assert.Null(AudioTrackLabels.From(new[] { Track(0, null) }));
            Assert.Null(AudioTrackLabels.From(new[] { Track(0, "quadraphonic") }));
            Assert.Null(AudioTrackLabels.From(new SessionAudioTrack[] { null }));
            Assert.Null(AudioTrackLabels.From(new[] { Track(-1, "speaker") }));
        }

        /// <summary>The whole chain in one: the recorder's report → the session → the labels a fresh
        /// edit's audio rows carry.</summary>
        [Fact]
        public void A_recorder_report_names_the_rows_of_a_fresh_edit()
        {
            var reported = Parse("""
                {"tracks":{"screen":{"index":0,"width":1920,"height":1080},
                           "audio":[{"index":0,"kind":"speaker","name":"Speaker 1"},
                                    {"index":1,"kind":"microphone","name":"Microphone 1"}]}}
                """).Audio;

            var session = reported.Select(a => new SessionAudioTrack { Index = a.Index, Kind = a.Kind }).ToArray();

            Assert.Equal(new[] { "System Audio", "Microphone" }, AudioTrackLabels.From(session));
        }
    }
}
