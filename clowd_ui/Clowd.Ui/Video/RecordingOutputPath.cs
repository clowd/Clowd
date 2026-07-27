using System;
using System.Diagnostics;
using System.IO;
using Clowd.Config;

namespace Clowd.UI
{
    /// <summary>
    /// Resolves where a finished recording is saved (issue #50 — restores the WPF
    /// <c>VideoCaptureWindow.StopRecording</c> behavior the rewrite dropped, where every recording
    /// landed in a non-configurable <c>video.mp4</c> inside the session directory).
    ///
    /// obs-express still writes into the session directory while recording — that directory is what
    /// the cancel/crash lifecycle owns (§4.2) — and <see cref="VideoCapturePage"/> moves the
    /// finished file here afterwards. Every method degrades to <c>null</c> rather than throwing:
    /// a bad output folder must cost the user the move, never the recording.
    /// </summary>
    public static class RecordingOutputPath
    {
        public const string DefaultFilenamePattern = "yyyy-MM-dd HH-mm-ss";

        /// <summary>
        /// The configured output folder as a full path, created if it does not exist yet. Falls
        /// back to the Videos folder when the configured one is blank or cannot be created (an
        /// unplugged drive, a path the user has no write access to), and returns null when even
        /// that fails — the caller then leaves the video in the session directory.
        /// </summary>
        public static string ResolveDirectory(SettingsRecording settings)
        {
            var configured = settings?.OutputDirectory;
            if (!String.IsNullOrWhiteSpace(configured))
            {
                var prepared = TryPrepareDirectory(configured);
                if (prepared != null)
                    return prepared;
            }

            return TryPrepareDirectory(SettingsRecording.DefaultOutputDirectory);
        }

        /// <summary>
        /// The full path a finished recording should be saved to: the resolved output directory
        /// plus the filename pattern rendered against the current time, uniquified against the
        /// files already there (WPF parity — "name (1)", "name (2)", …). Returns null when no
        /// writable directory could be resolved.
        /// </summary>
        public static string GetSavePath(SettingsRecording settings, string extension = ".mp4")
        {
            var dir = ResolveDirectory(settings);
            if (dir == null)
                return null;

            // an extension typed into the pattern ("yyyy-MM-dd.mp4") would otherwise be baked into
            // the name and doubled by the extension appended below — the WPF version stripped it too.
            var pattern = settings?.FilenamePattern;
            if (String.IsNullOrWhiteSpace(pattern))
                pattern = DefaultFilenamePattern;
            pattern = Path.GetFileNameWithoutExtension(pattern);

            string name;
            try
            {
                // throws on an invalid .NET date format string, and on an unreadable directory.
                name = PathConstants.GetFreePatternFileName(dir, pattern);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Recording filename pattern could not be applied: " + ex.Message);
                name = null;
            }

            // a pattern containing a separator ("yyyy/MM/dd") would write outside the chosen folder,
            // and one made only of literal text collides with itself on the next recording.
            if (String.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                name = "recording_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

            return Path.Combine(dir, name + extension);
        }

        private static string TryPrepareDirectory(string dir)
        {
            if (String.IsNullOrWhiteSpace(dir))
                return null;

            try
            {
                var full = Path.GetFullPath(dir);
                Directory.CreateDirectory(full);
                return full;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Recording output folder '{dir}' is unusable: {ex.Message}");
                return null;
            }
        }
    }
}
