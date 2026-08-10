using System;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>Picks the audio backend for the running OS.</summary>
    public static class AudioOutputFactory
    {
        /// <summary>
        /// WASAPI on Windows; elsewhere the clock-driven silent output, so playback still runs
        /// (silently) instead of throwing. A CoreAudio backend drops in here.
        /// </summary>
        public static IAudioOutput Create()
        {
            if (OperatingSystem.IsWindows())
                return new WasapiAudioOutput();
            return new SilentAudioOutput();
        }
    }
}
