using System;
using System.IO;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace Clowd.VideoSDK
{
    /// <summary>
    /// One-time process-wide initialization of the dynamically-loaded FFmpeg bindings.
    /// Resolution order for the native DLL directory: the <c>CLOWD_FFMPEG_PATH</c> environment
    /// variable, then the caller-supplied resolver (the app passes the directory of the
    /// obs-express binary — the FFmpeg DLLs ship alongside it). Clowd.VideoSDK deliberately has no
    /// reference to Clowd.Ui, so the fallback is a delegate rather than ObsBinaryLocator itself.
    /// </summary>
    public static class FFmpegLoader
    {
        public const string EnvVarName = "CLOWD_FFMPEG_PATH";

        private static readonly object _sync = new object();
        private static bool _attempted;
        private static bool _available;
        private static string _failureReason;
        private static string _librariesDirectory;

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
                    var dir = Environment.GetEnvironmentVariable(EnvVarName);
                    if (String.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    {
                        try { dir = fallbackDirectoryResolver?.Invoke(); }
                        catch { dir = null; }
                    }

                    if (String.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    {
                        _failureReason = $"No FFmpeg directory found (set {EnvVarName} or install obs-express).";
                        return false;
                    }

                    dir = Path.GetFullPath(dir);

                    // The FFmpeg DLLs link against siblings that live in the same folder
                    // (libx264, zlib, srt, ...). The bindings load avcodec-61.dll by absolute
                    // path, but Windows resolves *its* imports via the normal search order, which
                    // does not include the DLL's own directory — add it explicitly.
                    if (OperatingSystem.IsWindows())
                        SetDllDirectoryW(dir);

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
                    _failureReason = "Failed to load FFmpeg: " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>Throws when the bindings are not initialized; call before any FFmpeg use.</summary>
        public static void EnsureInitialized()
        {
            if (!IsAvailable)
                throw new InvalidOperationException(
                    "FFmpeg is not available: " + (FailureReason ?? "FFmpegLoader.TryInitialize has not been called."));
        }

        [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            EntryPoint = "SetDllDirectoryW", SetLastError = true)]
        private static extern bool SetDllDirectoryW(string lpPathName);

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
