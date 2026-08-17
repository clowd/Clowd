using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.Util;

namespace Clowd.UI
{
    /// <summary>
    /// The obs-express settings file (DESIGN §1.1): every tunable the recorder can change at
    /// runtime lives here rather than on its command line, so a settings change during WAIT is a
    /// stdin <c>configure</c> away instead of a process restart. Field names and casing are the
    /// recorder's JSON schema — every one is written on every save (a missing field means the
    /// recorder's default, never "keep current").
    /// </summary>
    internal sealed class ObsSettingsJson
    {
        [JsonPropertyName("fps")]
        public int Fps { get; set; }

        [JsonPropertyName("crf")]
        public int Crf { get; set; }

        [JsonPropertyName("max_width")]
        public int MaxWidth { get; set; }

        [JsonPropertyName("max_height")]
        public int MaxHeight { get; set; }

        [JsonPropertyName("hw_accel")]
        public bool HwAccel { get; set; }

        [JsonPropertyName("low_cpu")]
        public bool LowCpu { get; set; }

        /// <summary>Positive polarity, unlike the recorder's old <c>--no-cursor</c> flag.</summary>
        [JsonPropertyName("cursor")]
        public bool Cursor { get; set; }

        [JsonPropertyName("tracker")]
        public bool Tracker { get; set; }

        [JsonPropertyName("tracker_color")]
        public string TrackerColor { get; set; }

        [JsonPropertyName("speakers")]
        public string[] Speakers { get; set; }

        [JsonPropertyName("microphones")]
        public string[] Microphones { get; set; }

        /// <summary>Windows only; the recorder ignores it on macOS.</summary>
        [JsonPropertyName("speaker_volume_compensation")]
        public bool SpeakerVolumeCompensation { get; set; }

        /// <summary>Camera device id to record as a second video track, or "" for none. Unlike the
        /// audio devices this is not listed unconditionally: a webcam source is a pipeline element,
        /// not a runtime mute, so "capture off" has to be an empty id.</summary>
        [JsonPropertyName("webcam_device")]
        public string WebcamDevice { get; set; }
    }

    /// <summary>
    /// Maps Clowd.Ui recording state onto obs-express (DESIGN §1.1 / §4.2): the session-fixed
    /// parameters go on the clap CLI, everything tunable goes into the settings file
    /// (<see cref="WriteSettingsFile"/>), which the recorder re-reads on every stdin
    /// <c>configure</c>. The region is emitted verbatim in the platform capture coordinate space
    /// the overlay wrote it in (physical px on Windows, CG points on macOS). <c>--pause</c> is
    /// always passed: the pipeline is built up-front and recording only starts on the stdin
    /// <c>start</c> command. Factored out of the page so it is testable without a process.
    /// </summary>
    public static class ObsArguments
    {
        /// <summary>Name of the settings file inside the session directory.</summary>
        public const string SettingsFileName = "obs-settings.json";

        /// <summary>Name of the input-capture JSONL sidecar inside the session directory. It stays
        /// there for the life of the session (like videoedit.json) — the editor reads it in place,
        /// and a missing file just means no cursor/keyboard data.</summary>
        public const string InputCaptureFileName = "input-capture.jsonl";

        /// <summary>The color the click tracker is drawn in; obs-express's own default. Not
        /// surfaced as a Clowd setting, but the file must carry every field.</summary>
        private const string TrackerColor = "255,0,0";

        /// <summary>Recorder flag selecting the hybrid mp4 output (<c>mp4_output</c>), which writes
        /// one track per stream: video track 0 = screen, video track 1 = webcam, one audio track per
        /// configured device (speakers first). Without it the single-track ffmpeg muxer writes one
        /// video track and mixes every audio device into one.</summary>
        private const string MultiTrackArg = "--multi-track";

        /// <summary>Recorder flag naming the JSONL file it should write cursor/keyboard input data
        /// into. Given together with <see cref="MultiTrackArg"/>, it also earns the mp4 a 512x512
        /// cursor-box video track; without it the file alone would still be written, but Clowd only
        /// asks for input capture on recordings the editor can open.</summary>
        private const string InputCaptureArg = "--input-capture";

        /// <summary>libobs carries at most six audio tracks (its mixer/encoder limit), and the
        /// recorder refuses to start when <see cref="MultiTrackArg"/> is given with more devices
        /// than that. Clowd lists at most one speaker and one microphone, so this is a guard, not a
        /// case that arises today.</summary>
        private const int MaxAudioTracks = 6;

        public static IReadOnlyList<string> Build(ScreenRect region, string outputMp4, string settingsPath,
            SettingsRecording settings)
        {
            var args = new List<string>
            {
                "--region", FormattableString.Invariant($"{region.X},{region.Y},{region.Width},{region.Height}"),
                "--output", outputMp4,
                "--settings", settingsPath,
                "--pause",
            };

            // …and the one setting that cannot live in the settings file: the track layout picks the
            // libobs output object itself, which is built once when the process starts, so the
            // recorder only accepts it on the command line. A change to it therefore costs a respawn
            // (see VideoCapturePage), unlike every tunable in WriteSettingsFile.
            if (UsesMultiTrack(settings))
            {
                args.Add(MultiTrackArg);

                // input capture rides with multi-track: the jsonl (and the 512x512 cursor box
                // track the recorder adds alongside it) only mean anything to the editor, which a
                // single-track recording never reaches. Session-fixed like --output, so it is a
                // CLI argument rather than a settings-file key.
                args.Add(InputCaptureArg);
                args.Add(GetInputCapturePath(Path.GetDirectoryName(outputMp4)));
            }

            return args;
        }

        /// <summary>Where the input-capture sidecar of a session lives — the recorder is told this
        /// exact path, and the editor's fallback (a recorder too old to echo it back) looks here.</summary>
        public static string GetInputCapturePath(string sessionDir)
            => Path.Combine(sessionDir ?? "", InputCaptureFileName);

        /// <summary>
        /// Whether this recording is written as one track per stream — which is exactly what
        /// <see cref="SettingsRecording.EnableComposition"/> means, so the user's switch decides it
        /// directly. A single-track recording cannot be edited afterwards (nothing is left to
        /// separate) and cannot carry a webcam at all, which is why the composition switch also
        /// gates the webcam rows in settings and the Edit affordance on a finished recording.
        /// </summary>
        internal static bool UsesMultiTrack(SettingsRecording settings)
        {
            if (settings == null || !settings.EnableComposition)
                return false;

            // libobs' own cap. Clowd configures at most one speaker and one microphone, so this
            // never bites today; if it ever did, the recorder would refuse to start with the flag,
            // and a flattened recording beats no recording.
            return AudioDeviceCount(settings) <= MaxAudioTracks;
        }

        /// <summary>Whether a camera is actually recorded: a box ticked, a device picked, and
        /// composition on to give the camera a track to live in. The one condition
        /// <see cref="WriteSettingsFile"/> emits a non-empty <c>webcam_device</c> for.</summary>
        internal static bool UsesWebcam(SettingsRecording settings)
            => settings != null
            && settings.EnableComposition
            && settings.CaptureWebcam
            && !String.IsNullOrEmpty(settings.WebcamDeviceId);

        /// <summary>The audio devices the user actually asked to record. The settings file still
        /// lists muted devices (so a live unmute stays possible — see
        /// <see cref="WriteSettingsFile"/>), but the track layout is a spawn-time choice and should
        /// reflect the capture toggles: a device that is configured yet off must not earn the file
        /// a permanent silent track. VideoCapturePage routes toggle changes through the configure
        /// path, which respawns the recorder when this flips <see cref="UsesMultiTrack"/>.</summary>
        private static int AudioDeviceCount(SettingsRecording settings)
            => (settings.CaptureSpeaker ? DeviceList(settings.SpeakerDeviceId).Length : 0)
             + (settings.CaptureMicrophone ? DeviceList(settings.MicrophoneDeviceId).Length : 0);

        /// <summary>
        /// Writes the settings file at <paramref name="path"/> in full. Must run before the
        /// process is spawned (the recorder reads the file during CLI validation) and again
        /// before every <c>configure</c>.
        /// </summary>
        public static void WriteSettingsFile(string path, SettingsRecording settings)
        {
            // input capture (which rides with multi-track, see Build) hands both cursor and click
            // highlighting to the editor: the cursor is recorded as its own 512x512 track and the
            // clicks live in the jsonl, so baking either into the screen frames would double them
            // up in the composed output. Single-track recordings keep the legacy behavior — the
            // flattened file is all the user ever gets, so the settings apply directly.
            var inputCapture = UsesMultiTrack(settings);

            var model = new ObsSettingsJson
            {
                Fps = settings.Fps,
                // the VideoQuality enum members are the CRF values (Low=29, Medium=23, High=16).
                Crf = (int)settings.Quality,
                MaxWidth = settings.MaxResolutionWidth,
                MaxHeight = settings.MaxResolutionHeight,
                HwAccel = settings.HardwareAccelerated,
                LowCpu = false,
                Cursor = !inputCapture && settings.ShowMouseCursor,
                Tracker = !inputCapture && settings.HighlightClicks,
                TrackerColor = TrackerColor,
                // The devices are listed regardless of the CaptureSpeaker/CaptureMicrophone
                // toggles — those are runtime mutes applied over stdin; omitting the device would
                // make a live unmute impossible ("default" is a valid device id).
                Speakers = DeviceList(settings.SpeakerDeviceId),
                Microphones = DeviceList(settings.MicrophoneDeviceId),
                SpeakerVolumeCompensation = settings.SpeakerVolumeCompensation,
                // …and the camera, which is the opposite case: the source has to exist in the
                // pipeline from the start or not at all, so an unticked box (or no device picked)
                // is written as "" rather than a device the recorder would open and then mute.
                // Composition off means no --multi-track, and the recorder REFUSES to start with a
                // webcam_device it has no second video track for — so the gate that greys the
                // webcam rows out in settings has to be enforced here too, not just in the UI.
                WebcamDevice = UsesWebcam(settings) ? settings.WebcamDeviceId : "",
            };

            File.WriteAllText(path, JsonSerializer.Serialize(model, ClowdUiJsonContext.Default.ObsSettingsJson));
        }

        private static string[] DeviceList(string deviceId)
            => String.IsNullOrEmpty(deviceId) ? Array.Empty<string>() : new[] { deviceId };
    }
}
