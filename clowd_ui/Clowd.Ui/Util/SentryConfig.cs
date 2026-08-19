using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
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
    /// (clowd_rust_core/src/telemetry.rs); the shell does not.</para>
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

        /// <summary>Key under which an exception carries the recent output of the child process it
        /// relates to (a string in <see cref="Exception.Data"/>). <see cref="CaptureHandled"/>
        /// extracts it and uploads it as a <c>process-log.txt</c> attachment on the event, so the
        /// log reaches Sentry no matter how many layers up the exception is finally reported —
        /// "exited unexpectedly" / "timed out" events are undiagnosable without it (CLOWD-Z,
        /// CLOWD-C).</summary>
        public const string ProcessLogKey = "clowd.process-log";

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
            var processLog = TakeProcessLog(ex);

            SentrySdk.CaptureException(ex, scope =>
            {
                scope.SetTag("operation", operation);
                // the app kept running, so this is not fatal — but it is still a real defect
                scope.Level = SentryLevel.Error;

                if (!String.IsNullOrEmpty(processLog))
                {
                    scope.AddAttachment(Encoding.UTF8.GetBytes(processLog), "process-log.txt");
                    // the tail inline as well — most triage should not need the attachment
                    scope.SetExtra("process_log_tail",
                        processLog.Length <= 4096 ? processLog : processLog.Substring(processLog.Length - 4096));
                }
            });
#endif
        }

        /// <summary>Removes and returns the <see cref="ProcessLogKey"/> entry from
        /// <paramref name="ex"/> or the nearest inner exception carrying one. Removed rather than
        /// read: the SDK serializes <see cref="Exception.Data"/> into the event body, and a
        /// quarter-megabyte log inline would blow the event payload — as an attachment it rides
        /// in the same envelope without that limit.</summary>
        private static string TakeProcessLog(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                try
                {
                    if (e.Data[ProcessLogKey] is string log)
                    {
                        e.Data.Remove(ProcessLogKey);
                        return log;
                    }
                }
                catch
                {
                    // Exception.Data may be read-only or reject lookups for exotic exception
                    // types; losing the log must never lose the event itself.
                }
            }

            return null;
        }

        /// <summary>Cap on the assembled log. A per-session capture log is a few KB, but the warm
        /// host's rolling log has no such bound — and a multi-megabyte attachment helps nobody.
        /// Trimmed from the front: the failure is always at the end.</summary>
        private const int MaxProcessLogChars = 256 * 1024;

        /// <summary>
        /// Stows a child process's output on <paramref name="ex"/> under <see cref="ProcessLogKey"/>,
        /// so <see cref="CaptureHandled"/> uploads it as the <c>process-log.txt</c> attachment no
        /// matter how many layers up the exception is finally reported.
        /// </summary>
        /// <param name="ex">The exception about to be reported.</param>
        /// <param name="sections">
        /// Labelled log sources in the order they should appear — put the most diagnostic last,
        /// since both the size cap above and Sentry's inline <c>process_log_tail</c> keep the end.
        /// Null and blank sections are skipped, so a caller can pass an optional log file without
        /// checking for it first.
        /// </param>
        /// <remarks>
        /// Prefer this to parking a log in <see cref="Exception.Data"/> directly: <c>Data</c> is
        /// serialized inline into the event body, so anything stored there has to be cut down to a
        /// few lines to fit — which is how a crash report arrives carrying nothing that explains the
        /// crash. Small scalars (an exit code, a frame count) are still fine in <c>Data</c>.
        /// </remarks>
        public static void AttachProcessLog(Exception ex, params (string Label, string Content)[] sections)
        {
            if (ex is null || sections is null)
                return;

            try
            {
                // first writer wins, matching TakeProcessLog's "nearest log to the failure" search:
                // an inner frame knows more about what died than the layer re-reporting it.
                if (ex.Data.Contains(ProcessLogKey))
                    return;

                var builder = new StringBuilder();
                foreach (var (label, content) in sections)
                {
                    if (String.IsNullOrWhiteSpace(content))
                        continue;

                    if (builder.Length > 0)
                        builder.Append('\n');
                    builder.Append("===== ").Append(label).Append(" =====\n")
                           .Append(content.TrimEnd()).Append('\n');
                }

                if (builder.Length == 0)
                    return;

                var log = builder.ToString();
                if (log.Length > MaxProcessLogChars)
                    log = "[earlier output trimmed]\n" + log.Substring(log.Length - MaxProcessLogChars);

                ex.Data[ProcessLogKey] = log;
            }
            catch
            {
                // Exception.Data may be read-only or reject writes for exotic exception types;
                // losing the log must never lose the event itself.
            }
        }

        /// <summary>
        /// <see cref="CaptureHandled"/> for a call site whose failure mode includes "the network
        /// didn't work": transient faults (see <see cref="IsTransientNetworkFailure"/>) are dropped,
        /// everything else — including our own bugs on the same code path — still reports.
        /// </summary>
        /// <param name="alsoDropErrorStatuses">
        /// Also drop a response that arrived carrying an error status code. Only correct for an
        /// endpoint we do not own and do not control the request shape of — the GitHub release
        /// feed, where a 403 is a rate limit or an interposed proxy. Leave this off for anything
        /// speaking to our own services: there, a 4xx is usually us sending the wrong request.
        /// </param>
        /// <remarks>
        /// Only for operations where a failed transfer is already handled gracefully — the update
        /// check retries on its own schedule, an upload surfaces the error on the row. A network
        /// call whose failure would leave the app in a bad state should keep using
        /// <see cref="CaptureHandled"/>.
        /// </remarks>
        public static void CaptureHandledNetwork(Exception ex, string operation, bool alsoDropErrorStatuses = false)
        {
            if (IsTransientNetworkFailure(ex, alsoDropErrorStatuses))
                return;

            CaptureHandled(ex, operation);
        }

        /// <summary>
        /// True when <paramref name="ex"/> is a network fault that says nothing about Clowd: DNS
        /// failure, refused or reset connection, a captive portal or corporate proxy interposing
        /// itself, a TLS handshake that didn't complete.
        /// </summary>
        /// <remarks>
        /// <para>Every one of these is a property of the user's network or the far end on that
        /// particular day, and none of them is actionable here. They also arrive in enormous
        /// volume — a single machine behind an expired proxy token retried the update check
        /// hundreds of times a day (CLOWD-5, CLOWD-6) — which buries the real defects.</para>
        ///
        /// <para>The line drawn is <b>did anything answer us</b>. Nothing did: the request died in
        /// the transport, so no judgement was ever passed on it and none of it can be our fault.
        /// Something did: the remote read our request and rejected it — which is exactly the shape
        /// of a bug in provider code (wrong endpoint, stale auth header, malformed multipart), so
        /// it keeps reporting unless <paramref name="alsoDropErrorStatuses"/> says otherwise.</para>
        ///
        /// <para>Only the outermost exception is classified, deliberately. If our code caught a
        /// network fault and wrapped it in something of its own, that wrapper is a decision our
        /// code made, and the decision is ours to get wrong — an <c>AmazonS3Exception</c> around a
        /// dead socket must not be filtered just because a socket is somewhere underneath it.</para>
        ///
        /// <para>Typed rather than message-matched: the OS messages are localised into the user's
        /// system language, so "No such host is known" and "O nome solicitado é válido, mas..."
        /// are the same fault filed under two Sentry issues.</para>
        ///
        /// <para>Not listed, and deliberately: <see cref="TaskCanceledException"/> (an
        /// <c>HttpClient.Timeout</c> is indistinguishable from a request our own code wedged — a
        /// deadlocked streaming-zip pipe would time out exactly like a slow uplink), and plain
        /// <see cref="TimeoutException"/>, which in this codebase mostly means a child process
        /// never answered rather than a network fault. Note <see cref="CaptureHandled"/> already
        /// drops every <see cref="OperationCanceledException"/>, so a client-side timeout is
        /// filtered upstream of here regardless.</para>
        /// </remarks>
        public static bool IsTransientNetworkFailure(Exception ex, bool alsoDropErrorStatuses = false)
        {
            return Unwrap(ex) switch
            {
                // StatusCode is non-null only when a response came back and something called
                // EnsureSuccessStatusCode on it; null means the exception was raised below that,
                // in name resolution, connect, TLS, or the proxy tunnel.
                HttpRequestException http => http.StatusCode is null
                    ? !IsRequestSideError(http.HttpRequestError)
                    : alsoDropErrorStatuses,

                SocketException or WebException => true,
                AuthenticationException => true, // TLS handshake / certificate rejection

                // a connection dropped mid-body surfaces as a plain IOException wrapping the
                // socket error; a bare IOException on its own is a disk fault, not this.
                IOException io => io.InnerException is SocketException,

                _ => false,
            };
        }

        /// <summary>The handful of <see cref="HttpRequestError"/> values that indict the request we
        /// sent rather than the network it was sent over — a protocol violation we put on the wire,
        /// or a limit configured on our own <see cref="HttpClient"/>. Rare, and worth reporting.</summary>
        private static bool IsRequestSideError(HttpRequestError error) =>
            error is HttpRequestError.HttpProtocolError
                  or HttpRequestError.ConfigurationLimitExceeded
                  or HttpRequestError.ExtendedConnectNotSupported;

        /// <summary>Strips the plumbing wrappers that carry no information of their own, so the
        /// classification above sees the exception the failing code actually raised.</summary>
        private static Exception Unwrap(Exception ex) =>
            ex switch
            {
                AggregateException { InnerExceptions.Count: 1 } agg => Unwrap(agg.InnerExceptions[0]),
                TargetInvocationException { InnerException: { } inner } => Unwrap(inner),
                _ => ex,
            };

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
