using System;
using System.Collections.Generic;

namespace Prisc.Interop
{
    /// <summary>One enumerated device (<c>PriscDevice</c>).</summary>
    /// <param name="Id">What obs-express (prisc_recorder) consumes verbatim, never empty:
    /// <c>AVCaptureDevice.uniqueID</c> or the CoreAudio device UID on macOS, win-dshow's encoded id
    /// for a Windows camera. Not a display string.</param>
    /// <param name="Name">The display name. A camera falls back to its id; an audio device keeps an
    /// empty name when its name property reads back empty.</param>
    /// <param name="Kind">Which list it came from.</param>
    /// <param name="IsDefault">The system default input or output (audio kinds only).</param>
    /// <param name="InputChannels">Audio inputs: channels across the device's input streams; 0
    /// otherwise.</param>
    public sealed record CaptureDevice(string Id, string Name, DeviceKind Kind, bool IsDefault, int InputChannels);

    /// <summary>
    /// The device lists of <c>prisc_devices_enumerate</c>: cameras through AVFoundation (macOS) and
    /// DirectShow (Windows), audio endpoints through the CoreAudio HAL (macOS). Windows audio stays on
    /// NAudio, so the audio kinds answer <see cref="InteropStatus.Unsupported"/> there.
    /// </summary>
    /// <remarks>Any thread, reentrant; on Windows the library scopes its own COM apartment. No device is
    /// opened, so this neither needs nor triggers a camera or microphone prompt and instantiates no
    /// camera driver: safe from a click handler.</remarks>
    public static class CaptureDevices
    {
        /// <summary>The devices of <paramref name="kind"/>; an empty list means "none", which is an
        /// answer.</summary>
        /// <exception cref="InteropUnavailableException">The native library is not loaded.</exception>
        /// <exception cref="InteropException">The OS could not be asked (<see cref="InteropStatus.Os"/>),
        /// this OS has no such list (<see cref="InteropStatus.Unsupported"/>), or <paramref name="kind"/> is
        /// not a <see cref="DeviceKind"/> (<see cref="InteropStatus.InvalidArg"/>).</exception>
        public static IReadOnlyList<CaptureDevice> Enumerate(DeviceKind kind)
        {
            InteropLibrary.Require();
            return TryEnumerate(kind, out var error) ?? throw error!;
        }

        /// <summary><see cref="Enumerate"/> for callers with a fallback: null with the failure in
        /// <paramref name="error"/> when the library is missing or the OS could not be asked.</summary>
        public static unsafe IReadOnlyList<CaptureDevice>? TryEnumerate(DeviceKind kind, out Exception? error)
        {
            error = null;
            if (!InteropLibrary.IsAvailable)
            {
                error = new InteropUnavailableException(InteropLibrary.UnavailableReason);
                return null;
            }

            PriscDevice* devices = null;
            nuint count = 0;
            int status = NativeMethods.prisc_devices_enumerate((int)kind, &devices, &count);
            if (status != (int)InteropStatus.Ok)
            {
                error = Native.Failure(status, nameof(NativeMethods.prisc_devices_enumerate));
                return null;
            }

            try
            {
                var found = new List<CaptureDevice>((int)count);
                for (nuint i = 0; i < count; i++)
                {
                    PriscDevice* device = devices + i;
                    // Copied here: the free below releases every string of the array (mode 2).
                    found.Add(new CaptureDevice(
                        Utf8.Read(device->id),
                        Utf8.Read(device->name),
                        (DeviceKind)device->kind,
                        device->is_default != 0,
                        (int)device->input_channels));
                }

                return found;
            }
            finally
            {
                NativeMethods.prisc_devices_free(devices, count);
            }
        }
    }
}
