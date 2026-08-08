using System.Text.Json.Serialization;

namespace Clowd.UI
{
    /// <summary>
    /// One NDJSON line from the capturer's <c>--scroll-drive</c> mode (CAPTURE_PROTOCOL.md).
    /// Deliberately one flat shape rather than a polymorphic hierarchy: the four event kinds
    /// share a discriminator and between them use six fields, and System.Text.Json's source
    /// generator (which this app requires — reflection-based serialization is trimmed away)
    /// pays for a type hierarchy in generated code and attribute ceremony we would gain nothing
    /// from. Fields absent for a given <see cref="Type"/> simply stay at their default.
    /// </summary>
    internal sealed record ScrollDriverEvent
    {
        /// <summary><c>ready</c>, <c>status</c>, <c>done</c> or <c>fatal_error</c>.</summary>
        [JsonPropertyName("type")]
        public string Type { get; init; }

        /// <summary>Frames captured so far (<c>status</c>) or in total (<c>done</c>).</summary>
        [JsonPropertyName("frames")]
        public int Frames { get; init; }

        /// <summary>Height of the composite in physical px (<c>status</c> / <c>done</c>).</summary>
        [JsonPropertyName("height_px")]
        public int HeightPx { get; init; }

        /// <summary>What the driver is doing right now: <c>scrolling</c>, <c>settling</c> or
        /// <c>stitching</c> (<c>status</c> only).</summary>
        [JsonPropertyName("state")]
        public string State { get; init; }

        /// <summary>How the run ended (<c>done</c> only) — see <see cref="ScrollDriverResult"/>.
        /// Anything but <see cref="ScrollDriverResult.Failed"/> means session.json is on disk.</summary>
        [JsonPropertyName("result")]
        public string Result { get; init; }

        /// <summary>Why there is no session at all (<c>fatal_error</c> only).</summary>
        [JsonPropertyName("message")]
        public string Message { get; init; }
    }

    /// <summary>The <c>type</c> values the driver sends. Names, not spellings, at the call sites.</summary>
    internal static class ScrollDriverEventType
    {
        public const string Ready = "ready";
        public const string Status = "status";
        public const string Done = "done";
        public const string FatalError = "fatal_error";
    }

    /// <summary>The <c>done.result</c> values. All but <see cref="Failed"/> leave a finished
    /// session in the directory, which is why they are all routed to the editor.</summary>
    internal static class ScrollDriverResult
    {
        /// <summary>Reached the bottom of the document.</summary>
        public const string Complete = "complete";

        /// <summary>Esc, the user moving the mouse, a <c>stop</c> command, or the target window
        /// going away. The partial capture is kept.</summary>
        public const string Stopped = "stopped";

        /// <summary>Hit one of the driver's hard caps (frames / height / wall clock).</summary>
        public const string MaxReached = "max_reached";

        /// <summary>Nothing the driver could inject moved the target; a single-frame session is
        /// still written.</summary>
        public const string NoMovement = "no_movement";

        /// <summary>The run produced no session.</summary>
        public const string Failed = "failed";
    }

    /// <summary>
    /// A command on the driver's stdin. Only two exist, and they are singletons rather than
    /// constructed per call because the wire form is fixed: <c>{"type":"stop"}</c> finishes the
    /// run and keeps what has been captured, <c>{"type":"cancel"}</c> abandons it and writes
    /// nothing at all.
    /// </summary>
    internal sealed record ScrollDriverCommand
    {
        [JsonPropertyName("type")]
        public string Type { get; init; }

        public static ScrollDriverCommand Stop { get; } = new() { Type = "stop" };

        public static ScrollDriverCommand Cancel { get; } = new() { Type = "cancel" };
    }
}
