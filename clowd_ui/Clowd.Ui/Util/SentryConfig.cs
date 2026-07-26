using System;
using System.Reflection;
using Sentry;

namespace Clowd
{
    /// <summary>
    /// Sentry (crash/error reporting) setup for the shell. <see cref="Init"/> runs at the very top
    /// of <see cref="Program.Main"/> — before Velopack and before Avalonia — so failures in the
    /// install/update hooks and during startup are still reported.
    /// </summary>
    /// <remarks>
    /// <para>Release builds only. Debug builds compile every call in this class down to a no-op:
    /// local crashes belong in the debugger, not in the issue tracker.</para>
    /// <para>Exceptions only — there is deliberately no logging bridge on this side. The Rust
    /// capturer additionally routes its <c>error!</c> log calls to Sentry
    /// (clowd_capture/src/telemetry/crash.rs); the shell does not.</para>
    /// <para>Lives in the root <c>Clowd</c> namespace so every call site resolves it without a
    /// using directive, the same way <see cref="Constants"/> does.</para>
    /// </remarks>
    internal static class SentryConfig
    {
        /// <summary>Client-side DSN. These are not secrets — they are meant to ship inside the
        /// application and only grant permission to submit events.</summary>
        private const string Dsn = "https://b2be10cecdc152d0d1f53878b366e5cf@o118339.ingest.us.sentry.io/4511796263387136";

        /// <summary>Set to any non-empty value to turn reporting off. The capturer honours the same
        /// variable, so clearing it once in the environment covers both processes.</summary>
        public const string OptOutVariable = "CLOWD_DISABLE_TELEMETRY";

        /// <summary>Sentry release identifier, <c>clowd@&lt;version&gt;</c>. Kept in the same shape as
        /// the capturer's so both processes report against one release.</summary>
        public static string Release { get; } = "clowd@" + GetVersion();

        public static bool IsOptedOut =>
            !String.IsNullOrEmpty(Environment.GetEnvironmentVariable(OptOutVariable));

        /// <summary>Starts Sentry. Returns the SDK handle — disposing it flushes queued events — or
        /// <c>null</c> in debug builds and when the user has opted out. Safe to <c>using</c> either
        /// way.</summary>
        public static IDisposable Init()
        {
#if DEBUG
            return null;
#else
            if (IsOptedOut)
                return null;

            return SentrySdk.Init(o =>
            {
                o.Dsn = Dsn;
                o.Release = Release;
                o.Environment = "production";

                // desktop app: one process, one user, no per-request scope isolation to preserve.
                // Without this the scope is async-local and breadcrumbs set on one thread are
                // invisible to the handler that ends up reporting the crash.
                o.IsGlobalModeEnabled = true;

                o.AutoSessionTracking = true;
                o.AttachStacktrace = true;

                // no usernames, file paths from the machine, or clipboard/capture contents
                o.SendDefaultPii = false;

                o.DefaultTags["app"] = "clowd_ui";
            });
#endif
        }

        /// <summary>
        /// Reports an exception the app caught and recovered from. Use this in every
        /// <c>catch</c> that swallows a failure — those are invisible otherwise, and doubly so in
        /// release builds where <c>Debug.WriteLine</c> compiles away to nothing.
        /// </summary>
        /// <param name="ex">The caught exception.</param>
        /// <param name="operation">
        /// Short stable identifier for what was being attempted, e.g. <c>"upload.clipboard"</c>.
        /// Becomes the <c>operation</c> tag, so one noisy subsystem can be filtered or muted in
        /// Sentry without silencing the rest of the app.
        /// </param>
        /// <remarks>
        /// Cancellation is dropped rather than reported: <see cref="OperationCanceledException"/>
        /// is control flow here (shutdown, user-cancelled dialogs), not a fault, and reporting it
        /// would bury the real failures.
        /// </remarks>
        public static void CaptureHandled(Exception ex, string operation)
        {
#if !DEBUG
            if (ex is null || ex is OperationCanceledException)
                return;

            ex.SetSentryMechanism("Clowd.Handled", handled: true);

            SentrySdk.CaptureException(ex, scope =>
            {
                scope.SetTag("operation", operation);
                // the app kept running, so this is not fatal — but it is still a real defect
                scope.Level = SentryLevel.Error;
            });
#endif
        }

        /// <summary>Reports an exception that escaped to a global handler and was only stopped
        /// there. No-ops in debug builds.</summary>
        /// <param name="mechanism">Which global handler caught it, e.g.
        /// <c>"Dispatcher.UnhandledException"</c>.</param>
        public static void CaptureUnhandled(Exception ex, string mechanism)
        {
#if !DEBUG
            if (ex is null)
                return;

            ex.SetSentryMechanism(mechanism, handled: false);
            SentrySdk.CaptureException(ex, scope => scope.Level = SentryLevel.Fatal);
#endif
        }

        /// <summary>Version stamped by Nerdbank.GitVersioning. The informational version carries a
        /// <c>+&lt;commit&gt;</c> suffix that would fragment releases in Sentry, so it is trimmed.</summary>
        private static string GetVersion()
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(SentryConfig).Assembly;

            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!String.IsNullOrEmpty(informational))
            {
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational.Substring(0, plus) : informational;
            }

            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
    }
}
