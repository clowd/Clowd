using System;
using System.Linq;
using System.Text.Json;
using Prisc.Config;
using Prisc.Ipc;
using Prisc.PlatformUtil;
using Prisc.UI;
using Prisc.UI.VideoEditor;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The seam between the recorder and the editor: which arguments prisc_recorder is spawned with,
    /// what its <c>tracks</c> report is read as, and how that becomes the audio row labels a fresh
    /// edit opens with (Prisc.Ui exposes its internals to this project).
    ///
    /// The report shapes here are the recorder's own, copied from its documented protocol: a
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
                Mode = composition ? RecordingMode.Studio : RecordingMode.Instant,
                SpeakerDeviceId = speaker,
                CaptureSpeaker = captureSpeaker ?? !String.IsNullOrEmpty(speaker),
                MicrophoneDeviceId = mic,
                CaptureMicrophone = captureMic ?? !String.IsNullOrEmpty(mic),
                CaptureWebcam = webcam,
                WebcamDeviceId = webcam ? "cam-1" : "",
            };

        private static string[] Build(SettingsRecording settings)
            => RecorderArguments.Build(new ScreenRect(10, 20, 640, 480), TestPath.Native(@"C:\out\video.mp4"), TestPath.Native(@"C:\out\obs.json"), settings)
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

        /// <summary>The output path is the recorder's only container selector, so an .mkv path
        /// has to reach the command line untouched — nothing in here may normalize it to .mp4.</summary>
        [Fact]
        public void The_output_extension_reaches_the_recorder_verbatim()
        {
            var mkv = TestPath.Native(@"C:\out\video.mkv");
            var args = RecorderArguments.Build(new ScreenRect(0, 0, 8, 8), mkv, TestPath.Native(@"C:\out\obs.json"), Settings(composition: false)).ToArray();

            Assert.Equal("--output", args[2]);
            Assert.Equal(mkv, args[3]);
            Assert.DoesNotContain("--multi-track", args);
        }

        [Fact]
        public void The_container_setting_maps_to_the_extension_the_recorder_reads()
        {
            Assert.Equal(".mp4", VideoContainer.Mp4.ToExtension());
            Assert.Equal(".mkv", VideoContainer.Mkv.ToExtension());
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
            Assert.True(RecorderArguments.UsesWebcam(Settings(webcam: true)));
            Assert.False(RecorderArguments.UsesWebcam(Settings(composition: false, webcam: true)));
            Assert.False(RecorderArguments.UsesWebcam(Settings(webcam: false)));

            var ticked = Settings(webcam: true);
            ticked.WebcamDeviceId = "";
            Assert.False(RecorderArguments.UsesWebcam(ticked));
        }

        /// <summary>The capture API is a spawn-time argument, always emitted so the recorder's own
        /// default can never disagree with what the settings page shows. The values are the Rust
        /// parser's spellings — a mismatch is a spawn failure, not a fallback.</summary>
        [Fact]
        public void The_capture_method_is_always_on_the_command_line()
        {
            var args = Build(Settings());
            Assert.Equal("auto", args[Array.IndexOf(args, "--capture-method") + 1]);

            var dxgi = Settings();
            dxgi.CaptureMethod = ScreenCaptureMethod.Dxgi;
            Assert.Equal("dxgi", Build(dxgi)[Array.IndexOf(Build(dxgi), "--capture-method") + 1]);

            var wgc = Settings();
            wgc.CaptureMethod = ScreenCaptureMethod.Wgc;
            Assert.Equal("wgc", Build(wgc)[Array.IndexOf(Build(wgc), "--capture-method") + 1]);
        }

        [Fact]
        public void Null_settings_never_ask_for_multi_track()
        {
            Assert.False(RecorderArguments.UsesMultiTrack(null));
            Assert.False(RecorderArguments.UsesWebcam(null));
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

        /// <summary>Window capture rides with multi-track the same way: the shipped recorder always
        /// understands the flag, and the jsonl lands in the session directory beside the other
        /// sidecar.</summary>
        [Fact]
        public void Multi_track_recordings_always_capture_windows()
        {
            var args = Build(Settings());
            var i = Array.IndexOf(args, "--window-capture");

            Assert.True(i >= 0);
            Assert.Equal(TestPath.Native(@"C:\out\window-capture.jsonl"), args[i + 1]);
            Assert.Contains("--input-capture", args);
        }

        /// <summary>A single-track recording has no editor to read either sidecar, so neither is
        /// asked for.</summary>
        [Fact]
        public void Single_track_recordings_ask_for_neither_sidecar()
        {
            var args = Build(Settings(composition: false));

            Assert.DoesNotContain("--input-capture", args);
            Assert.DoesNotContain("--window-capture", args);
        }

        /// <summary>The recorder refuses to open two sidecars at one path, so the two file names
        /// must never collapse into each other.</summary>
        [Fact]
        public void The_two_sidecars_are_different_files()
        {
            var args = Build(Settings());
            var input = args[Array.IndexOf(args, "--input-capture") + 1];
            var window = args[Array.IndexOf(args, "--window-capture") + 1];

            Assert.NotEqual(input, window);
        }

        // ------------------------------------------------------------------ settings file

        private static JsonDocument WriteSettings(SettingsRecording settings)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "prisc-obs-settings-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                RecorderArguments.WriteSettingsFile(path, settings);
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

        /// <summary>Studio records at the fixed CRF whatever the (hidden) preset says; Instant
        /// records at the preset the user picked.</summary>
        [Fact]
        public void Studio_records_at_a_fixed_crf_and_instant_at_the_preset()
        {
            var studio = Settings();
            studio.Quality = VideoQuality.Low;
            using var studioFile = WriteSettings(studio);
            Assert.Equal(SettingsRecording.StudioCrf, studioFile.RootElement.GetProperty("crf").GetInt32());

            var instant = Settings(composition: false);
            instant.Quality = VideoQuality.Low;
            using var instantFile = WriteSettings(instant);
            Assert.Equal((int)VideoQuality.Low, instantFile.RootElement.GetProperty("crf").GetInt32());
        }

        /// <summary>"Lower CPU usage" is the recorder's low_cpu key, off unless the user asks.</summary>
        [Fact]
        public void Low_cpu_usage_reaches_the_settings_file()
        {
            using var off = WriteSettings(Settings());
            Assert.False(off.RootElement.GetProperty("low_cpu").GetBoolean());

            var settings = Settings();
            settings.LowCpuUsage = true;
            using var on = WriteSettings(settings);
            Assert.True(on.RootElement.GetProperty("low_cpu").GetBoolean());
        }

        // ------------------------------------------------------------------ tracks report

        /// <summary>The report after one wire line, decoded with the generated codec exactly as the
        /// driver receives it.</summary>
        private static RecorderReport Parse(string json, RecorderReport previous = null)
            => RecorderReport.Merge(previous, RecorderCodec.DecodeEvent(json));

        [Fact]
        public void A_multi_track_report_is_read_as_its_video_and_audio_tracks()
        {
            var tracks = Parse("""
                {"type":"started_recording","tracks":{
                  "screen":{"index":0,"width":1920,"height":1080},
                  "webcam":{"index":1,"width":1280,"height":720},
                  "audio":[{"index":0,"kind":"speaker","device":"default","name":"Speaker 1"},
                           {"index":1,"kind":"microphone","device":"mic-id","name":"Microphone 1"}]}}
                """).Tracks;

            Assert.Equal(new RecorderVideoTrack(0, 1920, 1080), tracks.Screen);
            Assert.Equal(new RecorderVideoTrack(1, 1280, 720), tracks.Webcam);
            Assert.Equal(new[] { (0u, RecorderAudioKind.Speaker), (1u, RecorderAudioKind.Microphone) },
                tracks.Audio.Select(a => (a.Index, a.Kind)));
        }

        [Fact]
        public void A_single_track_report_is_read_as_one_mixed_track()
        {
            var tracks = Parse("""
                {"type":"started_recording","tracks":{"screen":{"index":0,"width":800,"height":600},
                           "audio":[{"index":0,"kind":"mixed","name":"Audio"}]}}
                """).Tracks;

            Assert.Null(tracks.Webcam);
            Assert.Equal(RecorderAudioKind.Mixed, Assert.Single(tracks.Audio).Kind);
        }

        /// <summary>An empty <c>audio</c> array (or a missing one) reads as no audio tracks; the
        /// editor still gets its rows from the file.</summary>
        [Fact]
        public void A_report_without_audio_reads_as_no_audio_tracks()
        {
            var tracks = Parse("""{"type":"started_recording","tracks":{"screen":{"index":0,"width":800,"height":600},"audio":[]}}""").Tracks;

            Assert.NotNull(tracks.Screen);
            Assert.Empty(tracks.Audio);
            Assert.Empty(Parse("""{"type":"started_recording","tracks":{"screen":{"index":0,"width":8,"height":8}}}""").Tracks.Audio);
        }

        /// <summary>An input-capture recording's report echoes the jsonl path back (a top-level field
        /// beside <c>tracks</c>), and a window-capture one does the same beside it.</summary>
        [Fact]
        public void A_report_carries_the_sidecar_paths()
        {
            var report = Parse("""
                {"type":"started_recording","input_capture":"C:\\s\\input-capture.jsonl","window_capture":"C:\\s\\window-capture.jsonl","tracks":{
                  "screen":{"index":0,"width":1920,"height":1080},
                  "webcam":{"index":1,"width":1280,"height":720},"audio":[]}}
                """);

            Assert.Equal(@"C:\s\input-capture.jsonl", report.InputCapturePath);
            Assert.Equal(@"C:\s\window-capture.jsonl", report.WindowCapturePath);
        }

        /// <summary>A recorder that was not asked for a sidecar omits the key, and an empty value
        /// reads as "no sidecar" too.</summary>
        [Fact]
        public void A_report_without_a_sidecar_reads_as_none()
        {
            var report = Parse("""{"type":"started_recording","input_capture":"C:\\s\\input-capture.jsonl","tracks":{"screen":{"index":0,"width":800,"height":600},"audio":[]}}""");
            Assert.Null(report.WindowCapturePath);
            Assert.Equal(@"C:\s\input-capture.jsonl", report.InputCapturePath);

            Assert.Null(Parse("""{"type":"started_recording","input_capture":"","tracks":{"screen":{"index":0,"width":8,"height":8},"audio":[]}}""").InputCapturePath);
        }

        /// <summary><c>done</c> is the second report; one carrying no tracks must not clear what
        /// started_recording said.</summary>
        [Fact]
        public void A_message_with_no_usable_tracks_keeps_the_previous_report()
        {
            var previous = Parse("""
                {"type":"started_recording","tracks":{"screen":{"index":0,"width":800,"height":600},
                           "audio":[{"index":0,"kind":"microphone","name":"Mic"}]}}
                """);

            Assert.Same(previous, Parse("""{"type":"done","result":"stopped","code":0,"message":"ok"}""", previous));
            Assert.Same(previous, Parse("""{"type":"status","time_ms":1,"fps":30.0,"dropped":0,"dropped_perc":0.0}""", previous));
            Assert.Null(Parse("""{"type":"done","result":"cancelled","code":0,"message":"ok"}""", null));

            var final = Parse("""{"type":"done","result":"stopped","code":0,"message":"ok","tracks":{"screen":{"index":0,"width":640,"height":480},"audio":[]}}""", previous);
            Assert.Equal(640u, final.Tracks.Screen.Width);
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
            var report = Parse("""
                {"type":"started_recording","tracks":{"screen":{"index":0,"width":1920,"height":1080},
                           "audio":[{"index":0,"kind":"speaker","name":"Speaker 1"},
                                    {"index":1,"kind":"microphone","name":"Microphone 1"}]}}
                """);

            var session = report.ToSessionAudioTracks();

            Assert.Equal(new[] { "System Audio", "Microphone" }, AudioTrackLabels.From(session));
        }
    }
}
