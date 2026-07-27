using System;
using System.Collections.Generic;
using System.Globalization;
using Clowd.Config;
using Clowd.PlatformUtil;

namespace Clowd.UI
{
    /// <summary>
    /// Maps Clowd.Ui recording state onto the obs-express clap CLI (DESIGN §1.1 / §4.2).
    /// The region is emitted verbatim in the platform capture coordinate space the overlay
    /// wrote it in (physical px on Windows, CG points on macOS). <c>--pause</c> is always
    /// passed: the pipeline is built up-front and recording only starts on the stdin
    /// <c>start</c> command. Factored out of the page so it is testable without a process.
    /// </summary>
    public static class ObsArguments
    {
        public static IReadOnlyList<string> Build(ScreenRect region, string outputMp4, SettingsRecording settings)
        {
            var args = new List<string>
            {
                "--region", FormattableString.Invariant($"{region.X},{region.Y},{region.Width},{region.Height}"),
                "--output", outputMp4,
                "--fps", settings.Fps.ToString(CultureInfo.InvariantCulture),
                // the VideoQuality enum members are the CRF values (Low=29, Medium=23, High=16).
                "--crf", ((int)settings.Quality).ToString(CultureInfo.InvariantCulture),
                "--pause",
            };

            if (settings.MaxResolutionWidth > 0)
            {
                args.Add("--max-width");
                args.Add(settings.MaxResolutionWidth.ToString(CultureInfo.InvariantCulture));
            }

            if (settings.MaxResolutionHeight > 0)
            {
                args.Add("--max-height");
                args.Add(settings.MaxResolutionHeight.ToString(CultureInfo.InvariantCulture));
            }

            if (settings.HardwareAccelerated)
                args.Add("--hw-accel");

            if (!settings.ShowMouseCursor)
                args.Add("--no-cursor");

            // color stays the obs-express default; only the toggle is surfaced
            if (settings.HighlightClicks)
                args.Add("--tracker");

            // The device args are emitted regardless of the CaptureSpeaker/CaptureMicrophone
            // toggles — those are runtime mutes applied after init; gating the CLI arg on the
            // toggle would make live unmute impossible ("default" is a valid device id).
            if (!String.IsNullOrEmpty(settings.SpeakerDeviceId))
            {
                args.Add("--speaker");
                args.Add(settings.SpeakerDeviceId);
            }

            if (!String.IsNullOrEmpty(settings.MicrophoneDeviceId))
            {
                args.Add("--microphone");
                args.Add(settings.MicrophoneDeviceId);
            }

            return args;
        }

        /// <summary>
        /// True when changing <paramref name="propertyName"/> on <see cref="SettingsRecording"/>
        /// changes the CLI built above. obs-express fixes every one of these at spawn time, so an
        /// already-running (but not yet recording) process can never honor the new value — it has
        /// to be torn down and re-initialized (§4.2).
        /// </summary>
        /// <remarks>
        /// Deliberately a deny-list of the settings that do NOT reach the CLI: a setting added to
        /// <see cref="Build"/> without touching this method costs at worst a needless re-init,
        /// never a recording made with the value the user just changed away from. A null or empty
        /// name ("everything changed") therefore also requires a restart.
        /// </remarks>
        public static bool RequiresRestart(string propertyName) => propertyName switch
        {
            // runtime mutes applied over stdin after init, never CLI args (see the device-arg
            // comment above) — changing them mid-session is exactly what the toolbar buttons do.
            nameof(SettingsRecording.CaptureSpeaker) => false,
            nameof(SettingsRecording.CaptureMicrophone) => false,
            // post-recording UI behavior; the capturer never sees it.
            nameof(SettingsRecording.OpenWhenFinished) => false,
            // the capturer always writes video.mp4 into the session dir; these only decide where
            // the finished file is moved to afterwards (issue #50), which happens at stop time.
            nameof(SettingsRecording.OutputDirectory) => false,
            nameof(SettingsRecording.FilenamePattern) => false,
            _ => true,
        };
    }
}
