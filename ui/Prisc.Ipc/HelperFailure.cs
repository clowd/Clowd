using System;

namespace Prisc.Ipc
{
    /// <summary>Why a helper run did not end with a terminal event (HELPER_PROTOCOL.md §7.2).</summary>
    public enum HelperFailure
    {
        /// <summary>The run ended with a terminal non-error event.</summary>
        None,

        /// <summary>The process could not be started, or exited before <c>hello</c>.</summary>
        StartFailed,

        /// <summary>No <c>hello</c> within <see cref="HelperStartOptions.HelloTimeout"/>.</summary>
        HelloTimeout,

        /// <summary><c>hello</c> named another envelope version, catalog or catalog hash: a broken
        /// install or a stale dev build.</summary>
        ProtocolMismatch,

        /// <summary>The helper reported a fatal <c>error</c>.</summary>
        FatalError,

        /// <summary>Exit code 3 with no terminal event: macOS Screen Recording is not granted.</summary>
        NoScreenPermission,

        /// <summary>Exit code 4 with no terminal event: the desktop screenshot failed.</summary>
        CaptureFailed,

        /// <summary>Exit code 7 with no terminal event: inference is unavailable.</summary>
        InferenceUnavailable,

        /// <summary>The process exited without a terminal event, for any other reason.</summary>
        Crashed,

        /// <summary><see cref="HelperClient{TEvent, TCommand}.StopAsync"/> had to kill it.</summary>
        Timeout,
    }

    /// <summary>How a helper run ended: the terminal event (or fatal error) if there was one, the exit
    /// code, the failure classification and the tail of the helper's log.</summary>
    /// <param name="Terminal">The terminal event or fatal error; null when none arrived.</param>
    /// <param name="ExitCode">Null when the process could not be observed exiting.</param>
    /// <param name="Failure"><see cref="HelperFailure.None"/> only when <paramref name="Terminal"/> is a
    /// non-error terminal event.</param>
    public sealed record HelperOutcome<TEvent>(TEvent Terminal, int? ExitCode, HelperFailure Failure, string StderrTail)
    {
        /// <summary>The fatal error, when that is how the run ended.</summary>
        public IIpcError Error => Terminal as IIpcError;

        /// <summary>A one-line description for logs and error reports.</summary>
        public string Describe()
        {
            var exit = ExitCode is int code ? FormattableString.Invariant($"exit code {code}") : "exit code unknown";
            return Failure switch
            {
                HelperFailure.None => "finished (" + exit + ")",
                HelperFailure.FatalError => Error?.Message ?? "failed",
                HelperFailure.Timeout => "did not stop in time and was killed",
                _ => Failure + " (" + exit + ")",
            };
        }
    }

    /// <summary>A helper could not be started: it failed to spawn, never said <c>hello</c>, said the wrong
    /// one, or exited first. The process has already been killed.</summary>
    public sealed class HelperStartException : Exception
    {
        public HelperStartException(HelperFailure failure, string message, int? exitCode = null, Exception inner = null)
            : base(message, inner)
        {
            Failure = failure;
            ExitCode = exitCode;
        }

        public HelperFailure Failure { get; }

        public int? ExitCode { get; }
    }

    /// <summary>The helper answered a command with a non-fatal <c>error</c> (<c>bad_command</c>,
    /// <c>rejected</c> or <c>failed</c>) instead of its reply.</summary>
    public sealed class HelperCommandException : Exception
    {
        public HelperCommandException(IIpcError error)
            : base(error?.Message ?? "The helper refused the command.")
        {
            Error = error;
        }

        public IIpcError Error { get; }
    }
}
