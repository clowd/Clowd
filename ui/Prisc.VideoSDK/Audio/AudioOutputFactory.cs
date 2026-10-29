using System;

namespace Prisc.VideoSDK.Audio
{
    /// <summary>Picks the audio backend for the running OS.</summary>
    public static class AudioOutputFactory
    {
        /// <summary>
        /// WASAPI on Windows, the default-output AudioUnit on macOS; elsewhere the clock-driven
        /// silent output, so playback still runs (silently) instead of throwing.
        /// <para>
        /// A macOS machine with no usable output device falls back to the silent output too, for
        /// the same reason: a preview that runs without sound beats one that will not open. A
        /// missing prisc_interop is not such a case: the app requires the library and stops at
        /// startup without it, so nothing here probes for it.
        /// </para>
        /// </summary>
        public static IAudioOutput Create()
        {
            if (OperatingSystem.IsWindows())
                return new WasapiAudioOutput();

            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    return new CoreAudioOutput();
                }
                catch (Exception ex)
                {
                    // constructing one binds no device and calls nothing native, so this is close
                    // to unreachable; a CoreAudio that will not open is not worth failing playback
                    // over.
                    System.Diagnostics.Debug.WriteLine("CoreAudio unavailable, playing silently: " + ex.Message);
                }
            }

            return new SilentAudioOutput();
        }
    }
}
