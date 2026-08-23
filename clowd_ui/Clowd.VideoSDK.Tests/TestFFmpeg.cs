using System;
using System.IO;
using Clowd.UI;
using Clowd.VideoSDK;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Finds the FFmpeg natives for the suites that need real decoding, encoding or probing, and
    /// is the single place that decides whether those tests run or skip.
    ///
    /// It asks <see cref="ObsBinaryLocator.ResolveFFmpegDirectory"/> — the app's own resolver — so
    /// a checkout where the editor runs is a checkout where these tests run. That matters more
    /// than it sounds: this is the gate on roughly a hundred of them (every encoder, render,
    /// composition-player, filmstrip, waveform and AI-sidecar test), and when the probe and the
    /// app disagree the whole lot skips green while nobody notices. A probe that only knew the
    /// Windows and Linux library names is exactly how they came to skip on macOS.
    /// </summary>
    internal static class TestFFmpeg
    {
        /// <summary>True when the natives were found and the bindings initialized. Idempotent —
        /// FFmpegLoader caches the first outcome for the process.</summary>
        public static bool Available => FFmpegLoader.TryInitialize(FindDirectory);

        private static string FindDirectory()
        {
            // the dev layout the app does not know about: a sibling obs-express-rs build tree.
            string probe = OperatingSystem.IsWindows() ? "avcodec-61.dll"
                : OperatingSystem.IsMacOS() ? "libavcodec.61.dylib"
                : "libavcodec.so.61";

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                foreach (var cfg in new[] { "release", "debug" })
                {
                    var candidate = Path.Combine(dir.FullName, "obs-express-rs", "target", cfg);
                    if (File.Exists(Path.Combine(candidate, probe)))
                        return candidate;
                }
                dir = dir.Parent;
            }

            return ObsBinaryLocator.ResolveFFmpegDirectory();
        }

        /// <summary>The message a skipped test explains itself with.</summary>
        public static string SkipReason =>
            $"FFmpeg natives not found (set {FFmpegLoader.EnvVarName}, install ffmpeg@7, or build " +
            $"obs-express-rs): {FFmpegLoader.FailureReason}";
    }
}
