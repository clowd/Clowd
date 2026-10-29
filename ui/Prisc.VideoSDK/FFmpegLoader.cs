using System;
using System.IO;
using Prisc.Interop;
using Prisc.Ipc;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace Prisc.VideoSDK
{
    /// <summary>
    /// One-time process-wide initialization of the dynamically-loaded FFmpeg bindings.
    /// Resolution order for the native DLL directory: the <c>PRISC_FFMPEG_PATH</c> environment
    /// variable, then the caller-supplied resolver (normally <see cref="FindBesideRecorder"/>: the
    /// FFmpeg libraries ship with prisc_recorder).
    /// </summary>
    public static class FFmpegLoader
    {
        private static readonly object _sync = new object();
        private static bool _attempted;
        private static bool _available;
        private static string _failureReason;
        private static string _librariesDirectory;
        private static string _attemptedDirectory;
        private static int _preparedImages;

        /// <summary>True once <see cref="TryInitialize"/> has succeeded.</summary>
        public static bool IsAvailable
        {
            get { lock (_sync) return _available; }
        }

        /// <summary>Why the last <see cref="TryInitialize"/> failed (null when available).</summary>
        public static string FailureReason
        {
            get { lock (_sync) return _failureReason; }
        }

        /// <summary>The directory the native libraries were loaded from (null until initialized).</summary>
        public static string LibrariesDirectory
        {
            get { lock (_sync) return _librariesDirectory; }
        }

        /// <summary>How many images the native directory preparation loaded ahead of the bindings
        /// (the macOS dylib closure; 0 elsewhere and before an attempt).</summary>
        public static int PreparedImages
        {
            get { lock (_sync) return _preparedImages; }
        }

        /// <summary>
        /// Initializes the FFmpeg bindings. Idempotent: the first result (success or failure) is
        /// cached for the process lifetime — native libraries cannot be re-pathed once loaded.
        /// </summary>
        /// <param name="fallbackDirectoryResolver">Returns a directory containing the FFmpeg DLLs
        /// when the environment variable is not set; may be null or return null.</param>
        public static bool TryInitialize(Func<string> fallbackDirectoryResolver = null)
        {
            lock (_sync)
            {
                if (_attempted)
                    return _available;
                _attempted = true;

                try
                {
                    var dir = Environment.GetEnvironmentVariable(EnvVars.FfmpegPath);
                    if (String.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    {
                        try { dir = fallbackDirectoryResolver?.Invoke(); }
                        catch { dir = null; }
                    }

                    if (String.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    {
                        _failureReason = $"No FFmpeg directory found (set {EnvVars.FfmpegPath} or install the recorder).";
                        return false;
                    }

                    // recorded before GetFullPath, and as it was given rather than as resolved: a
                    // path malformed enough to throw there is still named in the failure message,
                    // and it is named in the shape whoever set it would recognize.
                    _attemptedDirectory = dir;
                    dir = Path.GetFullPath(dir);

                    // FFmpeg links against siblings in the same folder (libx264, zlib, srt, ...), and
                    // the bindings load it by absolute path, which does not make those resolvable:
                    // Windows searches the normal DLL order, and the macOS payload asks for them by
                    // @rpath with no LC_RPATH anywhere. prisc_interop makes the directory loadable,
                    // the right way for each OS, in one call.
                    _preparedImages = ProcessNative.PrepareFfmpegDirectory(dir);
                    Console.Error.WriteLine($"FFmpegLoader: prepared '{dir}': {_preparedImages} libraries loaded ahead");

                    DynamicallyLoadedBindings.LibrariesPath = dir;
                    DynamicallyLoadedBindings.Initialize();

                    // Force the libraries to actually load and assert the ABI matches the bindings —
                    // a mismatched avcodec would otherwise fail at some arbitrary later call.
                    var fmtMajor = (int)(ffmpeg.avformat_version() >> 16);
                    var codMajor = (int)(ffmpeg.avcodec_version() >> 16);
                    if (fmtMajor != ffmpeg.LIBAVFORMAT_VERSION_MAJOR || codMajor != ffmpeg.LIBAVCODEC_VERSION_MAJOR)
                    {
                        _failureReason =
                            $"FFmpeg version mismatch in '{dir}': found avformat {fmtMajor}/avcodec {codMajor}, " +
                            $"bindings need avformat {ffmpeg.LIBAVFORMAT_VERSION_MAJOR}/avcodec {ffmpeg.LIBAVCODEC_VERSION_MAJOR}.";
                        return false;
                    }

                    // Decoder chatter (SEI warnings etc.) would otherwise spam stderr on every file.
                    ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

                    _librariesDirectory = dir;
                    _failureReason = null;
                    _available = true;
                    return true;
                }
                catch (Exception ex)
                {
                    // FFmpeg.AutoGen reports every macOS load failure as "Specified method is not
                    // supported", which names neither the library nor the reason. Say where we
                    // were looking, since that is the part anyone debugging this needs.
                    _failureReason = OperatingSystem.IsMacOS()
                        ? $"Failed to load FFmpeg from '{_attemptedDirectory}': {ex.Message}"
                        : "Failed to load FFmpeg: " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// The directory holding the FFmpeg native libraries that ship with prisc_recorder, or null
        /// when the recorder cannot be found; the usual fallback resolver for
        /// <see cref="TryInitialize"/>. FFmpeg is not a helper, so it has no lookup of its own: the
        /// libraries sit beside the recorder on Windows and in a <c>Frameworks/</c> subdirectory next
        /// to it in a macOS bundle.
        /// <para>
        /// Each candidate is confirmed to hold an avcodec of the major the bindings were generated
        /// against before it is returned: a directory that merely exists has repeatedly been what
        /// turned a missing dependency into an unrelated failure much further along. When neither
        /// holds one, the recorder's directory is still the best guess, and the loader's own error
        /// names it.
        /// </para>
        /// </summary>
        public static string FindBesideRecorder()
        {
            var recorder = HelperLocator.Find(HelperNames.Recorder);
            if (String.IsNullOrEmpty(recorder))
                return null;

            var dir = Path.GetDirectoryName(Path.GetFullPath(recorder));
            if (HasFFmpeg(dir))
                return dir;

            var frameworks = Path.Combine(dir ?? "", "Frameworks");
            return HasFFmpeg(frameworks) ? frameworks : dir;
        }

        /// <summary>True when <paramref name="directory"/> holds an avcodec of the major the bindings
        /// were generated against. The name is built from the binding constant, so bumping
        /// FFmpeg.AutoGen cannot leave a stale literal behind, and the unversioned dev symlink is not
        /// accepted: it says nothing about the major.</summary>
        private static bool HasFFmpeg(string directory)
        {
            if (String.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return false;

            int major = ffmpeg.LIBAVCODEC_VERSION_MAJOR;
            string name = OperatingSystem.IsWindows() ? $"avcodec-{major}.dll"
                : OperatingSystem.IsMacOS() ? $"libavcodec.{major}.dylib"
                : $"libavcodec.so.{major}";

            return File.Exists(Path.Combine(directory, name));
        }

        /// <summary>Throws when the bindings are not initialized; call before any FFmpeg use.</summary>
        public static void EnsureInitialized()
        {
            if (!IsAvailable)
                throw new InvalidOperationException(
                    "FFmpeg is not available: " + (FailureReason ?? "FFmpegLoader.TryInitialize has not been called."));
        }

        /// <summary>Formats an FFmpeg error code (no allocation on success paths — call on errors only).</summary>
        public static unsafe string ErrorToString(int error)
        {
            const int bufSize = 256;
            byte* buf = stackalloc byte[bufSize];
            if (ffmpeg.av_strerror(error, buf, bufSize) < 0)
                return "ffmpeg error " + error;
            return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buf) + " (" + error + ")";
        }
    }
}
