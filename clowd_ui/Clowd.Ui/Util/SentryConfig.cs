using System;
using System.Reflection;
using Sentry;

namespace Clowd.Util
{
    /// <summary>
    /// Sentry (crash/error reporting) setup for the shell. <see cref="Init"/> runs at the very top
    /// of <see cref="Program.Main"/> — before Velopack and before Avalonia — so failures in the
    /// install/update hooks and during startup are still reported.
    /// </summary>
    /// <remarks>
    /// <para>Release builds only. Debug builds compile every call in this class down to a no-op:
    /// local crashes belong in the debugger, not in the issue tracker.</para>
    /// <para>The Rust capturer (clowd_capture/src/telemetry/crash.rs) reports into the same Sentry
    /// project under the same rule. The two are told apart by the <c>app</c> tag and share a
    /// release name, so a crash in either process lines up against the same release.</para>
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

        /// <summary>Reports an exception the app has already handled. No-ops in debug builds, which
        /// is why callers go through here rather than touching <see cref="SentrySdk"/> directly.</summary>
        public static void CaptureException(Exception ex)
        {
#if !DEBUG
            SentrySdk.CaptureException(ex);
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
