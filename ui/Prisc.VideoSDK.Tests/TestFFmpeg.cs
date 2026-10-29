using Prisc.Ipc;
using Prisc.VideoSDK;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Finds the FFmpeg natives for the suites that need real decoding, encoding or probing, and
    /// is the single place that decides whether those tests run or skip.
    ///
    /// It asks <see cref="FFmpegLoader.FindBesideRecorder"/>, the app's own resolver, so a checkout
    /// where the editor runs is a checkout where these tests run. That matters more than it sounds:
    /// this is the gate on roughly a hundred of them (every encoder, render, composition-player,
    /// filmstrip, waveform and AI-sidecar test), and when the probe and the app disagree the whole
    /// lot skips green while nobody notices.
    /// </summary>
    internal static class TestFFmpeg
    {
        /// <summary>True when the natives were found and the bindings initialized. Idempotent:
        /// FFmpegLoader caches the first outcome for the process.</summary>
        public static bool Available => FFmpegLoader.TryInitialize(FFmpegLoader.FindBesideRecorder);

        /// <summary>The message a skipped test explains itself with.</summary>
        public static string SkipReason =>
            $"FFmpeg natives not found (set {Prisc.Interop.EnvVars.FfmpegPath} or {Prisc.Interop.EnvVars.HelpersDir}, " +
            $"or run `cargo rec` in the Prisc repository): {FFmpegLoader.FailureReason}";
    }
}
