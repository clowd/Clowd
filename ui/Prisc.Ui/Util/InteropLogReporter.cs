using System;
using System.Diagnostics;
using Prisc.Interop;
using Sentry;

namespace Prisc
{
    /// <summary>
    /// Where prisc_interop's own diagnostics go in the app (README D3): the failures it has no
    /// status to return on (a panic caught in a CoreAudio or Services callback, an audio teardown
    /// that failed) reach the host only through its log sink. An error is reported to Sentry as
    /// handled (<c>interop.log</c>), a warning rides along as a breadcrumb, and every line goes to
    /// the debug output. Installed at startup once the library is known to be there.
    /// </summary>
    internal static class InteropLogReporter
    {
        public static void Install() => InteropLog.SetQueuedSink(Report);

        // on the queue's background task, never on the library's thread
        internal static void Report(InteropLogLevel level, string text)
        {
            Debug.WriteLine($"[prisc_interop] {level}: {text}");
            switch (level)
            {
                case InteropLogLevel.Error:
                    SentryConfig.CaptureHandled(new InteropNativeError(text), "interop.log");
                    break;
                case InteropLogLevel.Warn:
                    SentrySdk.AddBreadcrumb(text, "interop", level: BreadcrumbLevel.Warning);
                    break;
            }
        }

        /// <summary>An error line from prisc_interop, as Sentry groups it.</summary>
        private sealed class InteropNativeError(string message) : Exception(message);
    }
}
