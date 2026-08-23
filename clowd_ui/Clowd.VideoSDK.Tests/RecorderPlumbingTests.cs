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

        /// <summary>By default a configured device is also captured — the captureSpeaker/captureMic
        /// overrides express "configured but turned off".</summary>
        private static SettingsRecording Settings(bool composition = true, string speaker = "default",
            string mic = "default", bool webcam = false, bool? captureSpeaker = null, bool? captureMic = null)
            => new SettingsRecording
            {
                EnableComposition = composition,
                SpeakerDeviceId = speaker,
                CaptureSpeaker = captureSpeaker ?? !String.IsNullOrEmpty(speaker),
                MicrophoneDeviceId = mic,
                CaptureMicrophone = captureMic ?? !String.IsNullOrEmpty(mic),
                CaptureWebcam = webcam,
                WebcamDeviceId = webcam ? "cam-1" : "",
            };

        private static string[] Build(SettingsRecording settings)
            => ObsArguments.Build(new ScreenRect(10, 20, 640, 480), TestPath.Native(@"C:\out\video.mp4"), TestPath.Native(@"C:\out\obs.json"), settings)
                           .ToArray();

        [Fact]
        public void The_recorder_is_spawned_with_the_region_output_settings_and_pause()
        {
            var args = Build(Settings());

            Assert.Equal("--region", args[0]);
            Assert.Equal("10,20,640,480", args[1]);
            Assert.Equal("--output", args[2]);
            Assert.Equal(TestPath.Native(@"C:\out\video.mp4"), args[3]);
            Assert.Equal("--settings", args[4]);
            Assert.Equal(TestPath.Native(@"C:\out\obs.json"), args[5]);
            Assert.Equal("--pause", args[6]);
        }

        /// <summary>Composition IS the multi-track layout, so the switch decides the flag on its
        /// own — whatever audio the user happens to have configured. A composed recording with no
        /// audio at all is still editable (trims, text, a placed webcam), so "no audio device" is
        /// no longer a reason to withhold it.</summary>
        [Fact]
        public void Composition_asks_for_multi_track_whatever_the_audio_devices_are()
        {
            Assert.Contains("--multi-track", Build(Settings()));
            Assert.Contains("--multi-track", Build(Settings(speaker: "default", mic: "")));
            Assert.Contains("--multi-track", Build(Settings(speaker: "", mic: "default")));
            Assert.Contains("--multi-track", Build(Settings(speaker: "", mic: "")));
            Assert.Contains("--multi-track", Build(Settings(captureSpeaker: false, captureMic: false)));
        }

        /// <summary>…and composition off is the single-track muxer, with no exceptions: this is
        /// what makes the finished recording non-editable, so nothing may quietly re-enable it.</summary>
        [Fact]
        public void Composition_off_records_one_flattened_track()
        {
            Assert.DoesNotContain("--multi-track", Build(Settings(composition: false)));
            Assert.DoesNotContain("--multi-track", Build(Settings(composition: false, webcam: true)));
        }

        /// <summary>A webcam is a second video track, which only the hybrid mp4 output carries — the
        /// recorder refuses to start on a webcam device without the flag. So composition off must
        /// drop the camera rather than pass a device the recorder would reject.</summary>
        [Fact]
        public void A_webcam_needs_composition_a_camera_and_the_tick()
        {
            Assert.True(ObsArguments.UsesWebcam(Settings(webcam: true)));
            Assert.False(ObsArguments.UsesWebcam(Settings(composition: false, webcam: true)));
            Assert.False(ObsArguments.UsesWebcam(Settings(webcam: false)));

            var ticked = Settings(webcam: true);
            ticked.WebcamDeviceId = "";
            Assert.False(ObsArguments.UsesWebcam(ticked));
        }

        [Fact]
        public void Null_settings_never_ask_for_multi_track()
        {
            Assert.False(ObsArguments.UsesMultiTrack(null));
            Assert.False(ObsArguments.UsesWebcam(null));
        }

        /// <summary>Input capture rides with multi-track: every composed recording gets the jsonl
        /// (cursor sprites, key and mouse events), written into the session directory beside the
        /// mp4. A single-track recording has no editor to read it, so it is not asked for.</summary>
        [Fact]
        public void Multi_track_recordings_also_capture_input()
        {
            var args = Build(Settings());
            var i = Array.IndexOf(args, "--input-capture");

            Assert.True(i >= 0);
            Assert.Equal(TestPath.Native(@"C:\out\input-capture.jsonl"), args[i + 1]);

            Assert.DoesNotContain("--input-capture", Build(Settings(composition: false)));
        }

        // ------------------------------------------------------------------ settings file

        private static JsonDocument WriteSettings(SettingsRecording settings)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "clowd-obs-settings-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ObsArguments.WriteSettingsFile(path, settings);
                return JsonDocument.Parse(System.IO.File.ReadAllText(path));
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        /// <summary>With input capture active the editor owns the cursor (the jsonl's sprites) and
        /// the click highlight (its events) — baking either into the screen frames would double
        /// them up in the composed output, so the settings file forces both off whatever the user's
        /// settings say.</summary>
        [Fact]
        public void Input_capture_hands_cursor_and_tracker_to_the_editor()
        {
            var settings = Settings();
            settings.ShowMouseCursor = true;
            settings.HighlightClicks = true;

            using var file = WriteSettings(settings);
            Assert.False(file.RootElement.GetProperty("cursor").GetBoolean());
            Assert.False(file.RootElement.GetProperty("tracker").GetBoolean());
        }

        /// <summary>…and a single-track recording keeps the legacy behavior: the flattened file is
        /// all the user ever gets, so their cursor/highlight settings apply directly.</summary>
        [Fact]
        public void Single_track_recordings_keep_the_cursor_settings()
        {
            var settings = Settings(composition: false);
            settings.ShowMouseCursor = true;
            settings.HighlightClicks = true;

            using var file = WriteSettings(settings);
            Assert.True(file.RootElement.GetProperty("cursor").GetBoolean());
            Assert.True(file.RootElement.GetProperty("tracker").GetBoolean());
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

        /// <summary>An input-capture recording's report echoes the jsonl path back (a top-level
        /// field beside <c>tracks</c>) — the mp4 carries no cursor track anymore, the sprites live
        /// in the jsonl itself.</summary>
        [Fact]
        public void An_input_capture_report_carries_the_jsonl_path()
        {
            var tracks = Parse("""
                {"type":"started_recording","input_capture":"C:\\s\\input-capture.jsonl","tracks":{
                  "screen":{"index":0,"width":1920,"height":1080},
                  "webcam":{"index":1,"width":1280,"height":720}}}
                """);

            Assert.Equal(@"C:\s\input-capture.jsonl", tracks.InputCapturePath);
        }

        /// <summary>The field is optional and forward-tolerant: a recorder that predates it (or a
        /// malformed value) reads as "no jsonl", never as a failure — and a stale report still
        /// naming the retired <c>cursor</c> box track is read straight past.</summary>
        [Fact]
        public void A_report_without_input_capture_reads_as_none()
        {
            var tracks = Parse("""{"tracks":{"screen":{"index":0,"width":800,"height":600}}}""");
            Assert.Null(tracks.InputCapturePath);

            Assert.Null(Parse("""{"input_capture":42,"tracks":{"screen":{"index":0}}}""").InputCapturePath);
            Assert.Null(Parse("""{"input_capture":"","tracks":{"screen":{"index":0}}}""").InputCapturePath);

            var stale = Parse("""
                {"tracks":{"screen":{"index":0,"width":800,"height":600},
                           "cursor":{"index":2,"width":512,"height":512}}}
                """);
            Assert.Equal(new ObsTrackInfo(0, 800, 600), stale.Screen);
            Assert.Null(stale.Webcam);
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
