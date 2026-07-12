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
    }
}
