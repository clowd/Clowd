using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.Interop;

namespace Prisc.UI.Harness
{
    internal static partial class InteropProbe
    {
        /// <summary>The wrapper-level items of this area: one success, one documented failure and one
        /// round trip per export family, keyed <c>&lt;area&gt;.&lt;item&gt;</c>. Never plays audio and
        /// never starts the microphone: the output is opened and closed without a start, the input is
        /// opened (which reads the stored grant and never prompts) and closed without a start.</summary>
        static partial void ProbeAudio(ProbeRun run)
        {
            // Success: each list as names (what system_profiler shows), ids only by count and default
            // flag, so two runs on one machine compare equal.
            foreach (var kind in new[] { DeviceKind.Video, DeviceKind.AudioInput, DeviceKind.AudioOutput })
            {
                run.Item("audio.devices." + kind.ToString().ToLowerInvariant(), () =>
                {
                    var devices = CaptureDevices.Enumerate(kind);
                    return new Dictionary<string, object>
                    {
                        ["names"] = devices.Select(d => d.Name).ToList(),
                        ["defaults"] = devices.Where(d => d.IsDefault).Select(d => d.Name).ToList(),
                        ["inputChannels"] = devices.Select(d => d.InputChannels).ToList(),
                        ["idsUnique"] = devices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() == devices.Count,
                    };
                }, anyThread: true);
            }

            // Documented failure: an unknown kind is an argument error.
            run.Item("audio.devices.bad_kind", () =>
            {
                try
                {
                    CaptureDevices.Enumerate((DeviceKind)7);
                    return new ProbeOutcome("error", Error: "an unknown device kind was not refused");
                }
                catch (InteropException e) when (e.Status == InteropStatus.InvalidArg)
                {
                    return "InvalidArg";
                }
            }, anyThread: true);

            // The output: opened (binds the device, measures latency) and closed, never started.
            run.Item("audio.output.open", () =>
            {
                try
                {
                    using var output = CoreAudioOutputDevice.Open(48000, 2, (buffer, _, _) => buffer.Clear());
                    return new Dictionary<string, object>
                    {
                        ["deviceRate"] = output.DeviceSampleRateHz,
                        ["latencyKnown"] = output.LatencyFrames != null,
                    };
                }
                catch (InvalidOperationException e)
                {
                    return ProbeOutcome.Unavailable(e.Message);
                }
            }, anyThread: true);

            // The microphone: opened on the default input (resolves the device, builds the queue) and
            // closed, never started, so no audio flows and the privacy indicator never shows.
            run.Item("audio.input.open", () =>
            {
                try
                {
                    using var input = CoreAudioInput.Open(null, 48000, 50, _ => false, (_, _) => { });
                    return new Dictionary<string, object>
                    {
                        ["name"] = input.Name,
                        ["channels"] = input.Channels,
                        ["rate"] = input.SampleRateHz,
                        ["usedDefault"] = input.UsedDefault,
                    };
                }
                catch (UnauthorizedAccessException e)
                {
                    return ProbeOutcome.Denied(e.Message);
                }
                catch (InvalidOperationException e)
                {
                    return ProbeOutcome.Unavailable(e.Message);
                }
            }, anyThread: true);
        }
    }
}
