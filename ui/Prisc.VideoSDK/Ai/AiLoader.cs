using System;
using System.IO;

namespace Clowd.VideoSDK.Ai
{
    /// <summary>
    /// Resolves the path to the <c>clowd_ai</c> inference binary. Resolution order: the
    /// caller-supplied resolver (the app passes the location its bundled binaries ship at), then
    /// the <c>CLOWD_AI_PATH</c> environment variable. Clowd.VideoSDK deliberately has no
    /// reference to Clowd.Ui, so the app's knowledge arrives as a delegate — the same seam
    /// <see cref="FFmpegLoader"/> uses for the FFmpeg directory.
    ///
    /// <para>Unlike FFmpeg the binary is a child process, not an in-process library, so nothing is
    /// pinned at first use: every <see cref="TryGetPath"/> re-resolves, and a binary that appears
    /// mid-session (a completed download) is picked up on the next generation run.</para>
    /// </summary>
    public static class AiLoader
    {
        public const string EnvVarName = "CLOWD_AI_PATH";

        private static Func<string> _resolver;

        /// <summary>Installs the app's path resolver; may return null when the binary is not
        /// available. Null clears a previously configured resolver.</summary>
        public static void Configure(Func<string> pathResolver) => _resolver = pathResolver;

        /// <summary>The full path of an existing <c>clowd_ai</c> binary, or null when none
        /// resolves — the generators skip their work rather than failing.</summary>
        public static string TryGetPath()
        {
            string path = null;
            try { path = _resolver?.Invoke(); }
            catch { path = null; }

            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                path = Environment.GetEnvironmentVariable(EnvVarName);
                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;
            }

            return Path.GetFullPath(path);
        }
    }
}
