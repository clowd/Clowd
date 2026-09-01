using System;
using System.Diagnostics;
using System.Threading;
using Clowd.UI.Services;
using Clowd.VideoSDK;

namespace Clowd.UI.Preview
{
    /// <summary>
    /// The preview engine's one-time answer to "can we open a video at all". Every producer that
    /// touches FFmpeg asks here first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FFmpegLoader.TryInitialize"/> is already idempotent and already caches its own
    /// result, so this is not a second cache — it is the guarantee about <i>which thread pays for
    /// the first attempt</i>. That call probes directories, resolves a fallback path and loads
    /// eight native libraries; behind a <see cref="Lazy{T}"/> first touched on a Lane B worker it
    /// can never land on the UI or render thread, which is the standing cold-start rule.
    /// </para>
    /// <para>
    /// A false answer is permanent and is meant to be. Native libraries cannot be re-pathed once
    /// the process has tried, so a machine with no obs-express beside it will fail identically for
    /// every row, forever — and a recents list of two hundred video sessions must not turn that
    /// into two hundred directory probes. Every video and project source simply degrades to its
    /// file-type icon, which is a perfectly good preview for a machine that cannot decode.
    /// </para>
    /// </remarks>
    public static class FFmpegGate
    {
        private static readonly Lazy<bool> _ready = new Lazy<bool>(Initialize,
            LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// True when FFmpeg is loaded and a decode may be attempted. <b>Worker threads only</b> —
        /// the first call is the expensive one.
        /// </summary>
        public static bool Ensure() => _ready.Value;

        /// <summary>
        /// Whether the gate has already been asked, without asking it. Lets a caller that is on the
        /// wrong thread (or that only wants to know whether the cheap path is available) avoid
        /// forcing the load; it says nothing about the answer.
        /// </summary>
        public static bool Attempted => _ready.IsValueCreated;

        private static bool Initialize()
        {
            try
            {
                // The same resolver every other caller in the app passes (VideoRenderManager,
                // VideoCapturePage, VideoEditorWindow), so the preview engine cannot end up loading
                // a different FFmpeg than the one the editor and the renderer use.
                var ok = FFmpegLoader.TryInitialize(ObsBinaryLocator.ResolveFFmpegDirectory);
                if (!ok)
                {
                    // Said once per process, because that is how often it is worth saying: the
                    // condition is permanent and the visible consequence (icons instead of poster
                    // frames) is otherwise indistinguishable from a session with no video in it.
                    Debug.WriteLine("FFmpegGate: video previews are disabled — " +
                                    (FFmpegLoader.FailureReason ?? "FFmpeg could not be initialized."));
                }

                return ok;
            }
            catch (Exception ex)
            {
                // TryInitialize is documented not to throw, but it runs a native load; a Lazy body
                // that throws would rethrow the same exception to every subsequent caller, which is
                // exactly the retry storm this type exists to prevent.
                Debug.WriteLine("FFmpegGate: FFmpeg initialization threw — " + ex.Message);
                return false;
            }
        }
    }
}
