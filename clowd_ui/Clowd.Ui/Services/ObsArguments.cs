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

        public static IReadOnlyList<string> Build(ScreenRect region, string outputMp4, string settingsPath)
        {
            return new List<string>
            {
                "--region", FormattableString.Invariant($"{region.X},{region.Y},{region.Width},{region.Height}"),
                "--output", outputMp4,
                "--settings", settingsPath,
                "--pause",
            };
        }

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
            };

            File.WriteAllText(path, JsonSerializer.Serialize(model, ClowdUiJsonContext.Default.ObsSettingsJson));
        }

        private static string[] DeviceList(string deviceId)
            => String.IsNullOrEmpty(deviceId) ? Array.Empty<string>() : new[] { deviceId };
    }
}
