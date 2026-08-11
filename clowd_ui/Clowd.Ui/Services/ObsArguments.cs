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

        /// <summary>The color the click tracker is drawn in; obs-express's own default. Not
        /// surfaced as a Clowd setting, but the file must carry every field.</summary>
        private const string TrackerColor = "255,0,0";

        /// <summary>Recorder flag selecting the hybrid mp4 output (<c>mp4_output</c>), which writes
        /// one track per stream: video track 0 = screen, video track 1 = webcam, one audio track per
        /// configured device (speakers first). Without it the single-track ffmpeg muxer writes one
        /// video track and mixes every audio device into one.</summary>
        private const string MultiTrackArg = "--multi-track";

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
                args.Add(MultiTrackArg);

            return args;
        }

        /// <summary>
        /// Whether this recording is written as one track per stream. Two things ask for it:
        /// <list type="bullet">
        /// <item>the user's <see cref="SettingsRecording.SeparateAudioTracks"/> preference, which
        /// only means anything when the user has an audio device <i>enabled</i> — the recorder
        /// plans one track per configured device regardless of mute state, so counting the ids
        /// alone would give every stock install (ids default to "default", toggles default to off)
        /// a file full of confidently-labelled silent tracks, silent rows in the editor, and a
        /// waveform pass per silent stream;</item>
        /// <item>a webcam, unconditionally — it is a second video track, which the single-track
        /// muxer cannot carry at all, so the recorder rejects a <c>webcam_device</c> without this
        /// flag rather than dropping the camera.</item>
        /// </list>
        /// </summary>
        internal static bool UsesMultiTrack(SettingsRecording settings)
        {
            if (settings == null)
                return false;

            // the same condition WriteSettingsFile uses to emit a non-empty webcam_device.
            if (settings.CaptureWebcam && !String.IsNullOrEmpty(settings.WebcamDeviceId))
                return true;

            var devices = AudioDeviceCount(settings);
            return settings.SeparateAudioTracks && devices >= 1 && devices <= MaxAudioTracks;
        }

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
            var model = new ObsSettingsJson
            {
                Fps = settings.Fps,
                // the VideoQuality enum members are the CRF values (Low=29, Medium=23, High=16).
                Crf = (int)settings.Quality,
                MaxWidth = settings.MaxResolutionWidth,
                MaxHeight = settings.MaxResolutionHeight,
                HwAccel = settings.HardwareAccelerated,
                LowCpu = false,
                Cursor = settings.ShowMouseCursor,
                Tracker = settings.HighlightClicks,
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
                WebcamDevice = settings.CaptureWebcam && !String.IsNullOrEmpty(settings.WebcamDeviceId)
                    ? settings.WebcamDeviceId
                    : "",
            };

            File.WriteAllText(path, JsonSerializer.Serialize(model, ClowdUiJsonContext.Default.ObsSettingsJson));
        }

        private static string[] DeviceList(string deviceId)
            => String.IsNullOrEmpty(deviceId) ? Array.Empty<string>() : new[] { deviceId };
    }
}
