using System;
using System.Diagnostics;
using System.Text.Json;

namespace Clowd.UI
{
    /// <summary>How the shared region is obscured for the people watching it. <see cref="None"/> is
    /// the plain mirror; the other three are the privacy modes clowd_share_region can composite over
    /// the captured frame before it reaches the meeting app.</summary>
    public enum ShareObscureMode
    {
        None,
        Blur,
        Pixelate,
        Hide,
    }

    /// <summary>
    /// Where a share session stands with respect to the ONE decision the user makes in their
    /// meeting app: <see cref="Pending"/> while the helper's "share this window" prompt is up,
    /// then exactly one of the other three, forever.
    /// </summary>
    public enum ShareHandshake
    {
        /// <summary>The prompt window is showing and the user has not answered it yet. There is no
        /// timeout on this state — picking a window in a meeting app can take a while.</summary>
        Pending,

        /// <summary>The user pressed OK: the mirror window is live and the meeting app is
        /// (presumably) sharing it.</summary>
        Started,

        /// <summary>The user closed the prompt, pressed Escape, or we asked the helper to quit
        /// before they decided. A normal, silent outcome — nothing to report.</summary>
        Cancelled,

        /// <summary>The helper died before the user decided. Distinct from <see cref="Cancelled"/>
        /// because it is the one outcome worth surfacing as an error.</summary>
        Failed,
    }

    /// <summary>
    /// A rectangle in CAPTURE SPACE — bit-for-bit the same space as <c>ScreenRect</c> and
    /// obs-express's <c>--region</c>: physical pixels on the Windows virtual desktop (so
    /// <see cref="X"/>/<see cref="Y"/> may be negative on a left-of-primary monitor), CG points on
    /// macOS. Deliberately its own type rather than <c>ScreenRect</c> so this protocol file stays
    /// free of every other dependency and remains unit-testable on its own.
    /// <para>Value semantics: two rects with the same numbers are equal, which is what lets the
    /// driver and the page tell "the helper applied what we asked" from "the helper clamped it".</para>
    /// </summary>
    public sealed class ShareRegionRect : IEquatable<ShareRegionRect>
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public ShareRegionRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>The wire form the helper's <c>--region</c> flag and its <c>move</c> command both
        /// take: <c>X,Y,W,H</c>. Invariant culture, always — a comma decimal separator or a digit
        /// group separator from the user's locale would be rejected by the Rust argument parser.</summary>
        public string ToWireString() => FormattableString.Invariant($"{X},{Y},{Width},{Height}");

        public bool Equals(ShareRegionRect other) =>
            other != null && other.X == X && other.Y == Y && other.Width == Width && other.Height == Height;

        public override bool Equals(object obj) => Equals(obj as ShareRegionRect);

        public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

        public static bool operator ==(ShareRegionRect a, ShareRegionRect b) =>
            ReferenceEquals(a, b) || (a is not null && a.Equals(b));

        public static bool operator !=(ShareRegionRect a, ShareRegionRect b) => !(a == b);

        public override string ToString() => ToWireString();
    }

    /// <summary>
    /// The clowd_share_region wire protocol, and nothing else: no <c>Process</c>, no
    /// <c>Dispatcher</c>, no threads, no timers. Fed one stdout line at a time by whoever owns the
    /// process, it turns those lines into state and events.
    /// <para>
    /// It is split out from <see cref="ShareRegionDriver"/> (the same way
    /// <c>ScrollDriverProtocol</c> is split from <c>ScrollDriver</c>) because this is the entire
    /// risk surface of the feature — the handshake, the permanence of a blur failure, the ordering
    /// rules — and every one of those rules must be testable without libobs, without a meeting app,
    /// and without Avalonia. It is public rather than internal-plus-InternalsVisibleTo per the
    /// repo's standing preference for a real public API over a friend-assembly grant.
    /// </para>
    /// <para>
    /// NOT thread-safe, by design: the driver feeds it from its stdout pump only, so protocol
    /// handling stays single-threaded and strictly in arrival order. Events are raised
    /// synchronously on whatever thread called <see cref="HandleLine"/>; marshalling to the UI
    /// thread is the driver's job, not this class's.
    /// </para>
    /// <para>
    /// The wire (helper source: main.rs §protocol):
    /// <code>
    /// {"type":"initialized"}                                          prompt window is up
    /// {"type":"sharing_started","region":{"x","y","w","h"}}           user pressed OK
    /// {"type":"cancelled"}                                            exiting, never mirrored
    /// {"type":"region_changed","region":{"x","y","w","h"}}            ack of `move`, as APPLIED
    /// {"type":"obscure","mode":"none|blur|pixelate|hide","strength":N} ack of obscure/unobscure
    /// {"type":"status","fps":29.9}                                    1 Hz, fps ONLY
    /// {"type":"command_error","message":"..."}                        a line was refused
    /// </code>
    /// </para>
    /// </summary>
    public sealed class ShareRegionProtocol
    {
        /// <summary>Pending until exactly one of <c>sharing_started</c> / <c>cancelled</c> arrives,
        /// or the process dies first (which settles it to <see cref="ShareHandshake.Failed"/>).</summary>
        public ShareHandshake Handshake { get; private set; } = ShareHandshake.Pending;

        /// <summary>The region the helper says it is ACTUALLY mirroring — never the one we asked
        /// for. The helper forces the width/height to at least 64 and to an even number, and clamps
        /// to what the desktop actually has, so the request and the result routinely differ and only
        /// this one is true.</summary>
        public ShareRegionRect AppliedRegion { get; private set; }

        /// <summary>The obscure mode the helper last acknowledged. Starts at
        /// <see cref="ShareObscureMode.None"/>: the helper mirrors unobscured until told otherwise.</summary>
        public ShareObscureMode ObscureMode { get; private set; } = ShareObscureMode.None;

        /// <summary>The strength the helper last acknowledged (1..100 for blur/pixelate, 0 for the
        /// modes that do not take one).</summary>
        public int ObscureStrength { get; private set; }

        /// <summary>
        /// False once the helper has retracted to <c>none</c> with no command of ours outstanding.
        /// That unsolicited ack means its GPU effect failed to build (<c>GfxState::Failed</c>,
        /// obscure.rs:325) and — verified — that failure is PERMANENT for the life of the process:
        /// the helper never tries to build the effect again. So this latches false and the UI must
        /// retire the blur tile outright rather than merely showing it as off; every later attempt
        /// would silently do nothing.
        /// </summary>
        public bool BlurAvailable { get; private set; } = true;

        /// <summary>The helper is up and its "share this window" prompt is on screen. Always the
        /// first protocol line of a session.</summary>
        public event Action Initialized;

        /// <summary>The user pressed OK; the argument is the region actually being mirrored.</summary>
        public event Action<ShareRegionRect> SharingStarted;

        /// <summary>The session ended without ever mirroring anything.</summary>
        public event Action Cancelled;

        /// <summary>The helper applied a new region (the ack of a <c>move</c>). May legitimately
        /// arrive while the handshake is still pending — the region can be moved before anyone has
        /// started watching the window.</summary>
        public event Action<ShareRegionRect> RegionChanged;

        /// <summary>The obscure state changed: mode, strength, and whether the ack was
        /// UNSOLICITED (no command of ours was outstanding). An unsolicited one is the helper
        /// telling us it gave up, not us being told our command worked.</summary>
        public event Action<ShareObscureMode, int, bool> ObscureChanged;

        /// <summary>A 1 Hz progress line. fps ONLY — unlike obs-express there is no
        /// <c>timeMs</c>, no <c>dropped</c>, no <c>droppedPerc</c> on this protocol.</summary>
        public event Action<double> StatusReceived;

        /// <summary>The helper refused or failed a command. One per rejected line, in arrival
        /// order; never fatal on its own.</summary>
        public event Action<string> CommandError;

        /// <summary>A stdout line that was not protocol JSON — chatter, for the log buffer. Also
        /// where malformed JSON and duplicate handshake lines are routed, because neither is worth
        /// failing a live share over.</summary>
        public event Action<string> Chatter;

        /// <summary>The process is gone: the settled handshake, and the exit code. Raised exactly
        /// once.</summary>
        public event Action<ShareHandshake, int> Ended;

        /// <summary>How many obscure/unobscure commands we have written whose acks have not yet
        /// come back. The helper answers every command with exactly one line in arrival order, so a
        /// simple counter is enough to tell "ack for the thing we just asked for" from "the helper
        /// spontaneously gave up on the effect".</summary>
        private int _obscurePending;

        private bool _ended;

        /// <summary>
        /// Called by the driver immediately BEFORE it writes an <c>obscure</c> / <c>unobscure</c>
        /// command. Before, never after: the helper can answer faster than the writing thread gets
        /// its next statement scheduled, and an ack that arrived before its own bookkeeping would be
        /// misread as unsolicited — which permanently retires the blur feature. Getting this
        /// backwards is the one race in this file that has a user-visible, unrecoverable effect.
        /// </summary>
        public void NoteObscureSent() => _obscurePending++;

        /// <summary>
        /// Feeds one line of the helper's stdout to the protocol. Never throws: this runs on a pump
        /// thread whose death would silently deafen the whole session, so anything unrecognizable
        /// is reported as <see cref="Chatter"/> and the session carries on.
        /// </summary>
        public void HandleLine(string line)
        {
            if (line == null)
                return;

            // The same framing rule the recording and scrolling protocols use: a protocol line both
            // starts with '{' and ends with '}'. Anything else is the helper's own logging (or
            // libobs's) sharing the stream, and is chatter — NOT an error. Treating it as an error
            // would turn every future logging change in the helper into a broken share.
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                RaiseChatter(trimmed);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String)
                {
                    RaiseChatter(trimmed);
                    return;
                }

                switch (typeEl.GetString())
                {
                    case "initialized":
                        Initialized?.Invoke();
                        break;

                    case "sharing_started":
                        HandleSettled(ShareHandshake.Started, ReadRegion(root), trimmed);
                        break;

                    case "cancelled":
                        HandleSettled(ShareHandshake.Cancelled, null, trimmed);
                        break;

                    case "region_changed":
                        // deliberately does NOT touch the handshake: a move is legal during the
                        // prompt phase, and answering one says nothing about the user's decision.
                        var moved = ReadRegion(root);
                        if (moved != null)
                        {
                            AppliedRegion = moved;
                            RegionChanged?.Invoke(moved);
                        }
                        else
                        {
                            RaiseChatter(trimmed);
                        }

                        break;

                    case "obscure":
                        HandleObscure(root, trimmed);
                        break;

                    case "status":
                        // fps is a float on the wire — GetDouble, never GetInt32 — and it is the
                        // only field there is.
                        if (root.TryGetProperty("fps", out var fpsEl) && fpsEl.ValueKind == JsonValueKind.Number)
                            StatusReceived?.Invoke(fpsEl.GetDouble());
                        else
                            RaiseChatter(trimmed);
                        break;

                    case "command_error":
                        var message = root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                            ? msgEl.GetString()
                            : "The share helper rejected a command.";
                        CommandError?.Invoke(message);
                        break;

                    default:
                        // an unknown type is a newer helper talking to an older Clowd; log it and
                        // carry on rather than treating the session as broken.
                        RaiseChatter(trimmed);
                        break;
                }
            }
            catch (JsonException ex)
            {
                // a truncated or malformed line must never take the pump down with it. It is
                // chatter with a note, not an exception: the share is still running.
                Debug.WriteLine("Unparseable share protocol line: " + trimmed + " (" + ex.Message + ")");
                RaiseChatter(trimmed);
            }
            catch (Exception ex)
            {
                // same reasoning, one level wider: a surprising element kind (GetDouble on a
                // string, say) is a bad line, not a dead session.
                Debug.WriteLine("Share protocol line failed to handle: " + trimmed + " (" + ex.Message + ")");
                RaiseChatter(trimmed);
            }
        }

        /// <summary>
        /// Called once when the helper process has exited.
        /// <para>
        /// If the handshake never settled, it settles to <see cref="ShareHandshake.Failed"/>
        /// REGARDLESS OF EXIT CODE. The helper exits 0 both when the user closes the prompt and
        /// when it emits <c>cancelled</c>, so the code cannot distinguish them — but a process that
        /// died before the user decided anything left no <c>cancelled</c> line either, and that is
        /// precisely the case worth reporting. This is the line between a silent close (the user
        /// changed their mind) and a real error report (the helper fell over).
        /// </para>
        /// <para>
        /// If it already settled, that value stands. In particular an exit after
        /// <see cref="ShareHandshake.Started"/> is NORMAL: this protocol has no terminal message at
        /// the end of a share — the helper just exits 0 and the pipe closes. obs-express's rule that
        /// an exit without a terminal message is fatal must not be copied here.
        /// </para>
        /// </summary>
        public void HandleProcessEnded(int exitCode)
        {
            if (_ended)
                return;

            _ended = true;

            if (Handshake == ShareHandshake.Pending)
                Handshake = ShareHandshake.Failed;

            Ended?.Invoke(Handshake, exitCode);
        }

        /// <summary>Settles the handshake, or logs a second settling line as chatter. The contract
        /// guarantees exactly one of <c>sharing_started</c> / <c>cancelled</c> per session, so a
        /// second one is a helper bug — and re-settling would mean a live share silently reported
        /// as cancelled, which is far worse than a log line.</summary>
        private void HandleSettled(ShareHandshake settled, ShareRegionRect region, string raw)
        {
            if (Handshake != ShareHandshake.Pending)
            {
                RaiseChatter(raw);
                return;
            }

            Handshake = settled;

            if (settled == ShareHandshake.Started)
            {
                // the region is read before the event fires, so a handler may read AppliedRegion.
                if (region != null)
                    AppliedRegion = region;

                SharingStarted?.Invoke(AppliedRegion);
            }
            else
            {
                Cancelled?.Invoke();
            }
        }

        private void HandleObscure(JsonElement root, string raw)
        {
            var mode = ParseMode(root.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String
                ? modeEl.GetString()
                : null);

            if (mode == null)
            {
                RaiseChatter(raw);
                return;
            }

            var strength = root.TryGetProperty("strength", out var strengthEl)
                           && strengthEl.ValueKind == JsonValueKind.Number
                           && strengthEl.TryGetInt32(out var parsed)
                ? parsed
                : 0;

            // every command gets exactly one response in arrival order, so an ack with nothing
            // outstanding is one the helper sent on its own initiative.
            var unsolicited = _obscurePending <= 0;
            if (!unsolicited)
                _obscurePending--;

            ObscureMode = mode.Value;
            ObscureStrength = strength;

            // The one permanent failure on this protocol. The helper only ever retracts to none
            // unbidden when its effect could not be built, and GfxState::Failed is terminal for the
            // life of the process (obscure.rs:325) — it will not retry. Latch it so the UI retires
            // the feature instead of offering a button that can no longer do anything.
            if (unsolicited && mode.Value == ShareObscureMode.None)
                BlurAvailable = false;

            ObscureChanged?.Invoke(ObscureMode, ObscureStrength, unsolicited);
        }

        /// <summary>The <c>region</c> object of a message, or null when the message did not carry a
        /// usable one. Note the wire uses <c>w</c>/<c>h</c>, not <c>width</c>/<c>height</c>.</summary>
        private static ShareRegionRect ReadRegion(JsonElement root)
        {
            if (!root.TryGetProperty("region", out var el) || el.ValueKind != JsonValueKind.Object)
                return null;

            if (!TryReadInt(el, "x", out var x) || !TryReadInt(el, "y", out var y)
                || !TryReadInt(el, "w", out var w) || !TryReadInt(el, "h", out var h))
                return null;

            return new ShareRegionRect(x, y, w, h);
        }

        private static bool TryReadInt(JsonElement obj, string name, out int value)
        {
            value = 0;
            return obj.TryGetProperty(name, out var el)
                   && el.ValueKind == JsonValueKind.Number
                   && el.TryGetInt32(out value);
        }

        /// <summary>The wire spelling of an obscure mode, or null for one this build does not know
        /// — which is treated as an unreadable line rather than silently becoming
        /// <see cref="ShareObscureMode.None"/>, because "none" is the one value that carries the
        /// permanent-failure meaning.</summary>
        private static ShareObscureMode? ParseMode(string wire)
        {
            switch (wire)
            {
                case "none": return ShareObscureMode.None;
                case "blur": return ShareObscureMode.Blur;
                case "pixelate": return ShareObscureMode.Pixelate;
                case "hide": return ShareObscureMode.Hide;
                default: return null;
            }
        }

        /// <summary>The wire spelling of <paramref name="mode"/>, for the command the driver
        /// writes. Kept beside <see cref="ParseMode"/> so the two spellings can never drift.</summary>
        public static string ToWireString(ShareObscureMode mode)
        {
            switch (mode)
            {
                case ShareObscureMode.Blur: return "blur";
                case ShareObscureMode.Pixelate: return "pixelate";
                case ShareObscureMode.Hide: return "hide";
                default: return "none";
            }
        }

        /// <summary>
        /// The exact stdin line that asks the helper for <paramref name="mode"/>. Lives here rather
        /// than in the driver so the command text is testable without a process, next to the parser
        /// for the ack it produces.
        /// <para>The strength is only ever appended to <c>blur</c> and <c>pixelate</c>. The helper's
        /// parser routes <c>hide</c> (and <c>none</c>) through a reject-strength arm, so
        /// <c>obscure hide 50</c> is refused outright with <c>command_error</c> — the region stays
        /// exactly as it was and no <c>obscure</c> ack ever arrives.</para>
        /// <para><see cref="ShareObscureMode.None"/> spells itself <c>unobscure</c>: both spellings
        /// are accepted, and this one reads as what it is at the call site.</para>
        /// </summary>
        public static string BuildObscureCommand(ShareObscureMode mode, int strength)
        {
            var clamped = Math.Clamp(strength, 1, 100);

            switch (mode)
            {
                case ShareObscureMode.None:
                    return "unobscure";
                case ShareObscureMode.Blur:
                case ShareObscureMode.Pixelate:
                    return FormattableString.Invariant($"obscure {ToWireString(mode)} {clamped}");
                default:
                    return FormattableString.Invariant($"obscure {ToWireString(mode)}");
            }
        }

        private void RaiseChatter(string line)
        {
            if (!String.IsNullOrEmpty(line))
                Chatter?.Invoke(line);
        }
    }
}
