using System;
using System.Collections.Generic;
using System.Globalization;
using Clowd.UI;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The clowd_share_region wire contract, pinned line by line. <see cref="ShareRegionProtocol"/>
    /// is deliberately free of <c>Process</c>, <c>Dispatcher</c>, threads and timers precisely so
    /// that this file can exist: the whole risk surface of the share feature — the one-decision
    /// handshake, the permanence of a blur failure, the "an exit is not a failure" rule that
    /// obs-express gets the other way round — is exercised here without libobs, without a meeting
    /// app, and without Avalonia.
    ///
    /// <para>
    /// Every test below is named for the RULE it protects and carries a one-line note saying which
    /// part of the contract it pins, because the expensive failures of this feature are all silent:
    /// a share that reports itself cancelled while it is still mirroring, a blur button that no
    /// longer does anything, a helper crash swallowed as "the user changed their mind". None of
    /// those show up as an exception, so the only way they are ever caught is here.
    /// </para>
    ///
    /// <para>
    /// The wire, for reference (helper source: <c>main.rs §protocol</c>) — one compact JSON object
    /// per line, LF, <c>initialized</c> always first, then EXACTLY ONE of <c>sharing_started</c> /
    /// <c>cancelled</c> for the life of the session:
    /// <code>
    /// {"type":"initialized"}
    /// {"type":"sharing_started","region":{"x":0,"y":0,"w":800,"h":600}}
    /// {"type":"cancelled"}
    /// {"type":"region_changed","region":{...}}
    /// {"type":"obscure","mode":"none|blur|pixelate|hide","strength":50}
    /// {"type":"status","fps":29.9}
    /// {"type":"command_error","message":"..."}
    /// </code>
    /// </para>
    /// </summary>
    public class ShareRegionProtocolTests
    {
        /// <summary>
        /// Everything the protocol says, in arrival order, with counts — because most of the rules
        /// here are about a line NOT being raised a second time, which an assertion on state alone
        /// cannot see.
        /// </summary>
        private sealed class Recorder
        {
            public int Initialized;
            public int Cancelled;
            public readonly List<ShareRegionRect> Started = new();
            public readonly List<ShareRegionRect> Moved = new();
            public readonly List<double> Status = new();
            public readonly List<string> Errors = new();
            public readonly List<string> Chatter = new();
            public readonly List<(ShareObscureMode Mode, int Strength, bool Unsolicited)> Obscure = new();
            public readonly List<(ShareHandshake Handshake, int ExitCode)> Ended = new();

            public Recorder(ShareRegionProtocol protocol)
            {
                protocol.Initialized += () => Initialized++;
                protocol.Cancelled += () => Cancelled++;
                protocol.SharingStarted += r => Started.Add(r);
                protocol.RegionChanged += r => Moved.Add(r);
                protocol.StatusReceived += fps => Status.Add(fps);
                protocol.CommandError += m => Errors.Add(m);
                protocol.Chatter += c => Chatter.Add(c);
                protocol.ObscureChanged += (mode, strength, unsolicited) => Obscure.Add((mode, strength, unsolicited));
                protocol.Ended += (handshake, code) => Ended.Add((handshake, code));
            }
        }

        private static (ShareRegionProtocol Protocol, Recorder Events) New()
        {
            var protocol = new ShareRegionProtocol();
            return (protocol, new Recorder(protocol));
        }

        /// <summary>One region-carrying line. Built by concatenation rather than interpolation
        /// because the payload is nothing but braces, and assembled with the invariant culture for
        /// the same reason <see cref="ShareRegionRect.ToWireString"/> is: a negative coordinate must
        /// be spelled with an ASCII hyphen whatever the test machine's locale prefers.</summary>
        private static string Region(string type, int x, int y, int w, int h)
            => "{\"type\":\"" + type + "\",\"region\":{"
               + "\"x\":" + Inv(x) + ",\"y\":" + Inv(y) + ",\"w\":" + Inv(w) + ",\"h\":" + Inv(h)
               + "}}";

        private static string Obscure(string mode, int strength)
            => "{\"type\":\"obscure\",\"mode\":\"" + mode + "\",\"strength\":" + Inv(strength) + "}";

        private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ the handshake

        /// <summary>Pins: <c>initialized</c> means "the prompt window is up", NOT "the user said yes".</summary>
        [Fact]
        public void Initialized_announces_the_prompt_and_settles_nothing()
        {
            var (protocol, events) = New();

            protocol.HandleLine("""{"type":"initialized"}""");

            Assert.Equal(1, events.Initialized);
            Assert.Equal(ShareHandshake.Pending, protocol.Handshake);

            // …and nothing else has been claimed yet: no region, no obscure state, blur still offered.
            Assert.Null(protocol.AppliedRegion);
            Assert.Equal(ShareObscureMode.None, protocol.ObscureMode);
            Assert.True(protocol.BlurAvailable);
            Assert.Empty(events.Chatter);
        }

        /// <summary>Pins: the region on the wire is the one being mirrored — the helper forces the
        /// size to >=64 and to an even number, so the request and the result routinely differ and
        /// only the ack is true.</summary>
        [Fact]
        public void Sharing_started_settles_started_and_reports_the_applied_region_not_the_requested_one()
        {
            var (protocol, events) = New();
            var requested = new ShareRegionRect(100, 200, 801, 33);   // odd width, sub-64 height

            protocol.HandleLine("""{"type":"initialized"}""");
            protocol.HandleLine(Region("sharing_started", 100, 200, 802, 64));

            Assert.Equal(ShareHandshake.Started, protocol.Handshake);

            var applied = new ShareRegionRect(100, 200, 802, 64);
            Assert.Equal(applied, protocol.AppliedRegion);
            Assert.Equal(applied, Assert.Single(events.Started));
            Assert.NotEqual(requested, protocol.AppliedRegion);

            // the region is assigned BEFORE the event fires, so a handler may read the property.
            Assert.Same(protocol.AppliedRegion, events.Started[0]);
        }

        /// <summary>Pins: <c>cancelled</c> is the normal, silent outcome — the user closed the
        /// prompt or pressed Escape, and nothing was ever mirrored.</summary>
        [Fact]
        public void Cancelled_settles_cancelled()
        {
            var (protocol, events) = New();

            protocol.HandleLine("""{"type":"initialized"}""");
            protocol.HandleLine("""{"type":"cancelled"}""");

            Assert.Equal(ShareHandshake.Cancelled, protocol.Handshake);
            Assert.Equal(1, events.Cancelled);
            Assert.Empty(events.Started);
            Assert.Null(protocol.AppliedRegion);   // a cancelled session never mirrored a rectangle
        }

        /// <summary>Pins: EXACTLY ONE of the two settles, forever. A second settling line is a
        /// helper bug, and honouring it would report a live share as cancelled — far worse than a
        /// log line.</summary>
        [Fact]
        public void Only_the_first_handshake_line_counts()
        {
            var (protocol, events) = New();

            protocol.HandleLine(Region("sharing_started", 0, 0, 640, 480));
            protocol.HandleLine(Region("sharing_started", 5, 5, 100, 100));
            protocol.HandleLine("""{"type":"cancelled"}""");

            Assert.Equal(ShareHandshake.Started, protocol.Handshake);
            Assert.Equal(new ShareRegionRect(0, 0, 640, 480), protocol.AppliedRegion);
            Assert.Single(events.Started);
            Assert.Equal(0, events.Cancelled);

            // the two ignored lines are logged rather than dropped, so a helper bug is still visible.
            Assert.Equal(2, events.Chatter.Count);
        }

        /// <summary>…and the same rule from the other side: nothing re-settles a cancelled session
        /// into a started one, which would leave the UI mirroring a window that does not exist.</summary>
        [Fact]
        public void Only_the_first_handshake_line_counts_when_it_was_a_cancel()
        {
            var (protocol, events) = New();

            protocol.HandleLine("""{"type":"cancelled"}""");
            protocol.HandleLine(Region("sharing_started", 0, 0, 640, 480));
            protocol.HandleLine("""{"type":"cancelled"}""");

            Assert.Equal(ShareHandshake.Cancelled, protocol.Handshake);
            Assert.Equal(1, events.Cancelled);
            Assert.Empty(events.Started);
            Assert.Null(protocol.AppliedRegion);
        }

        // ------------------------------------------------------------------ the end of the process

        /// <summary>
        /// Pins the rule that turns a libobs crash into a real error instead of a silent close: an
        /// exit while the handshake is still PENDING is <see cref="ShareHandshake.Failed"/>
        /// REGARDLESS OF EXIT CODE. The helper exits 0 both when the user declines and when it falls
        /// over, so the code cannot tell them apart — but a decline emits a <c>cancelled</c> line
        /// first and a crash does not, and that missing line is the whole signal.
        /// </summary>
        [Fact]
        public void A_death_before_the_user_decided_is_a_failure_even_on_exit_code_zero()
        {
            var (protocol, events) = New();

            protocol.HandleLine("""{"type":"initialized"}""");
            protocol.HandleProcessEnded(0);

            Assert.Equal(ShareHandshake.Failed, protocol.Handshake);
            Assert.Equal((ShareHandshake.Failed, 0), Assert.Single(events.Ended));

            // Ended is raised exactly once, however many times the owner notices the exit.
            protocol.HandleProcessEnded(1);
            Assert.Single(events.Ended);
            Assert.Equal(ShareHandshake.Failed, protocol.Handshake);
        }

        /// <summary>Pins the difference from obs-express: there is NO terminal message at the end of
        /// a share. Once mirroring has started the helper simply exits 0 and the pipe closes, so
        /// "an exit without a terminal message is fatal" must not be copied here.</summary>
        [Fact]
        public void A_clean_exit_after_sharing_started_is_the_normal_end_of_a_share()
        {
            var (protocol, events) = New();

            protocol.HandleLine("""{"type":"initialized"}""");
            protocol.HandleLine(Region("sharing_started", 0, 0, 1920, 1080));
            protocol.HandleProcessEnded(0);

            Assert.Equal(ShareHandshake.Started, protocol.Handshake);   // NOT re-settled to Failed
            Assert.Equal((ShareHandshake.Started, 0), Assert.Single(events.Ended));
            Assert.Equal(new ShareRegionRect(0, 0, 1920, 1080), protocol.AppliedRegion);
        }

        /// <summary>…and a settled cancel likewise stands: the exit that follows a <c>cancelled</c>
        /// line is the helper doing as it was told, not a second outcome.</summary>
        [Fact]
        public void A_settled_cancel_survives_the_exit_that_follows_it()
        {
            var (protocol, events) = New();

            protocol.HandleLine("""{"type":"cancelled"}""");
            protocol.HandleProcessEnded(0);

            Assert.Equal((ShareHandshake.Cancelled, 0), Assert.Single(events.Ended));
        }

        // ------------------------------------------------------------------ status

        /// <summary>Pins: <c>status</c> carries fps and NOTHING else. Unlike obs-express there is no
        /// <c>timeMs</c>, no <c>dropped</c>, no <c>droppedPerc</c> — a reader that required any of
        /// them would drop every status line this helper sends.</summary>
        [Fact]
        public void Status_carries_fps_only_and_it_is_a_double()
        {
            var (protocol, events) = New();

            protocol.HandleLine("""{"type":"status","fps":29.9}""");
            protocol.HandleLine("""{"type":"status","fps":30}""");        // an integral rate is still a double
            protocol.HandleLine("""{"type":"status","fps":0}""");         // the first second of a share

            Assert.Equal(new[] { 29.9, 30d, 0d }, events.Status);
            Assert.Empty(events.Chatter);

            // a status line with no usable fps is a bad line, not a zero-fps report.
            protocol.HandleLine("""{"type":"status"}""");
            protocol.HandleLine("""{"type":"status","fps":"29.9"}""");
            Assert.Equal(3, events.Status.Count);
            Assert.Equal(2, events.Chatter.Count);
        }

        // ------------------------------------------------------------------ region_changed

        /// <summary>Pins: a <c>move</c> ack updates the region and says NOTHING about the user's
        /// decision — the region is movable while the prompt is still up, and treating the ack as a
        /// settling line would report a share as started before anyone was watching it.</summary>
        [Fact]
        public void Region_changed_updates_the_region_without_settling_the_handshake()
        {
            var (protocol, events) = New();

            // during the prompt phase…
            protocol.HandleLine("""{"type":"initialized"}""");
            protocol.HandleLine(Region("region_changed", 10, 20, 640, 480));

            Assert.Equal(ShareHandshake.Pending, protocol.Handshake);
            Assert.Equal(new ShareRegionRect(10, 20, 640, 480), protocol.AppliedRegion);
            Assert.Single(events.Moved);

            // …and again once mirroring is live.
            protocol.HandleLine(Region("sharing_started", 10, 20, 640, 480));
            protocol.HandleLine(Region("region_changed", 12, 22, 700, 500));

            Assert.Equal(ShareHandshake.Started, protocol.Handshake);
            Assert.Equal(new ShareRegionRect(12, 22, 700, 500), protocol.AppliedRegion);
            Assert.Equal(2, events.Moved.Count);
            Assert.Empty(events.Chatter);
        }

        /// <summary>Pins the coordinate space: regions are CAPTURE SPACE on the virtual desktop, so
        /// a monitor left of or above the primary produces negative x/y that must survive the round
        /// trip intact — clamping them to zero would mirror the wrong part of the desktop into a
        /// meeting.</summary>
        [Fact]
        public void Negative_coordinates_survive_the_round_trip()
        {
            var (protocol, _) = New();

            protocol.HandleLine(Region("sharing_started", -1920, -300, 1000, 900));

            Assert.Equal(new ShareRegionRect(-1920, -300, 1000, 900), protocol.AppliedRegion);
            Assert.Equal("-1920,-300,1000,900", protocol.AppliedRegion.ToWireString());
        }

        /// <summary>…and a region object that is missing a field, or carries a non-integer one, is
        /// an unreadable line rather than a half-built rectangle: the last known-good region stands.</summary>
        [Fact]
        public void A_malformed_region_object_leaves_the_last_good_region_alone()
        {
            var (protocol, events) = New();

            protocol.HandleLine(Region("sharing_started", 0, 0, 640, 480));
            protocol.HandleLine("""{"type":"region_changed","region":{"x":1,"y":2,"w":3}}""");
            protocol.HandleLine("""{"type":"region_changed","region":{"x":1,"y":2,"w":3,"h":"4"}}""");
            protocol.HandleLine("""{"type":"region_changed"}""");

            Assert.Equal(new ShareRegionRect(0, 0, 640, 480), protocol.AppliedRegion);
            Assert.Empty(events.Moved);
            Assert.Equal(3, events.Chatter.Count);
        }

        // ------------------------------------------------------------------ obscure / blur

        /// <summary>Pins the solicited/unsolicited split: the helper answers every command with
        /// exactly one line in arrival order, so an ack with a command outstanding is a confirmation
        /// and an ack with none is the helper speaking on its own initiative.</summary>
        [Fact]
        public void An_ack_with_a_command_outstanding_is_solicited()
        {
            var (protocol, events) = New();

            protocol.NoteObscureSent();
            protocol.HandleLine("""{"type":"obscure","mode":"blur","strength":50}""");

            Assert.Equal((ShareObscureMode.Blur, 50, false), Assert.Single(events.Obscure));
            Assert.Equal(ShareObscureMode.Blur, protocol.ObscureMode);
            Assert.Equal(50, protocol.ObscureStrength);
            Assert.True(protocol.BlurAvailable);

            // the outstanding count is consumed by that ack: the NEXT one is unsolicited again.
            protocol.HandleLine("""{"type":"obscure","mode":"blur","strength":50}""");
            Assert.True(events.Obscure[1].Unsolicited);
        }

        /// <summary>
        /// Pins the one permanent failure on this protocol. An unsolicited retraction to
        /// <c>none</c> means the helper's GPU effect could not be built — <c>GfxState::Failed</c>,
        /// obscure.rs:325 — which is TERMINAL for the life of the process: the helper never retries.
        /// So <c>BlurAvailable</c> latches false and the tile must be retired outright, not merely
        /// shown as off, because every later command would silently do nothing.
        /// </summary>
        [Fact]
        public void An_unsolicited_none_permanently_retires_blur()
        {
            var (protocol, events) = New();

            protocol.HandleLine(Region("sharing_started", 0, 0, 640, 480));
            protocol.NoteObscureSent();
            protocol.HandleLine("""{"type":"obscure","mode":"blur","strength":50}""");
            Assert.True(protocol.BlurAvailable);

            // the helper gives up, with nothing of ours outstanding.
            protocol.HandleLine("""{"type":"obscure","mode":"none","strength":0}""");

            Assert.False(protocol.BlurAvailable);
            Assert.Equal((ShareObscureMode.None, 0, true), events.Obscure[1]);

            // …and it never comes back, whatever arrives afterwards — including a perfectly
            // well-formed solicited blur ack. The latch is the whole point: a UI that re-enabled the
            // tile here would offer a button that can no longer do anything.
            protocol.NoteObscureSent();
            protocol.HandleLine("""{"type":"obscure","mode":"blur","strength":80}""");

            Assert.False(protocol.BlurAvailable);
            Assert.Equal((ShareObscureMode.Blur, 80, false), events.Obscure[2]);
            Assert.Equal(ShareObscureMode.Blur, protocol.ObscureMode);
        }

        /// <summary>…and a SOLICITED none does not retire anything: turning blur off is the normal
        /// second half of a toggle, and mistaking it for the failure would kill the feature on the
        /// user's first click.</summary>
        [Fact]
        public void Turning_blur_off_on_purpose_keeps_the_feature()
        {
            var (protocol, events) = New();

            protocol.NoteObscureSent();
            protocol.HandleLine("""{"type":"obscure","mode":"blur","strength":50}""");
            protocol.NoteObscureSent();
            protocol.HandleLine("""{"type":"obscure","mode":"none","strength":0}""");

            Assert.True(protocol.BlurAvailable);
            Assert.Equal(ShareObscureMode.None, protocol.ObscureMode);
            Assert.False(events.Obscure[1].Unsolicited);
        }

        /// <summary>Acks are consumed in arrival order, one per command — so two commands in flight
        /// are answered by two solicited acks, not one solicited and one "the helper gave up".</summary>
        [Fact]
        public void Two_commands_in_flight_are_answered_in_order()
        {
            var (protocol, events) = New();

            protocol.NoteObscureSent();
            protocol.NoteObscureSent();
            protocol.HandleLine("""{"type":"obscure","mode":"blur","strength":50}""");
            protocol.HandleLine("""{"type":"obscure","mode":"none","strength":0}""");

            Assert.All(events.Obscure, o => Assert.False(o.Unsolicited));
            Assert.True(protocol.BlurAvailable);
        }

        /// <summary>An unknown mode is an unreadable line, never a silent <c>none</c> — "none" is
        /// the one value that carries the permanent-failure meaning, so guessing it wrong would
        /// retire the feature over a spelling the helper added later.</summary>
        [Fact]
        public void An_unknown_obscure_mode_is_chatter_not_a_failure()
        {
            var (protocol, events) = New();

            protocol.HandleLine("""{"type":"obscure","mode":"frosted","strength":50}""");
            protocol.HandleLine("""{"type":"obscure","strength":50}""");

            Assert.Empty(events.Obscure);
            Assert.Equal(2, events.Chatter.Count);
            Assert.True(protocol.BlurAvailable);
            Assert.Equal(ShareObscureMode.None, protocol.ObscureMode);
        }

        /// <summary>The two spellings live side by side so they cannot drift: what we parse is what
        /// we write back on the <c>obscure</c> command line.</summary>
        [Fact]
        public void Every_mode_round_trips_through_its_wire_spelling()
        {
            var (protocol, events) = New();

            foreach (var (wire, mode) in new[]
                     {
                         ("none", ShareObscureMode.None),
                         ("blur", ShareObscureMode.Blur),
                         ("pixelate", ShareObscureMode.Pixelate),
                         ("hide", ShareObscureMode.Hide),
                     })
            {
                Assert.Equal(wire, ShareRegionProtocol.ToWireString(mode));

                protocol.NoteObscureSent();
                protocol.HandleLine(Obscure(wire, 7));
                Assert.Equal(mode, protocol.ObscureMode);
            }

            Assert.Equal(4, events.Obscure.Count);
            Assert.Empty(events.Chatter);
        }

        // ------------------------------------------------------------------ command_error

        /// <summary>Pins: a refused command is reported with the helper's own message text, intact —
        /// including non-ASCII, which is the whole reason both pipes are UTF-8. It is never fatal:
        /// the share carries on with the state it already had.</summary>
        [Fact]
        public void Command_error_carries_the_helpers_message_verbatim()
        {
            var (protocol, events) = New();

            protocol.HandleLine(Region("sharing_started", 0, 0, 640, 480));
            protocol.HandleLine("""{"type":"command_error","message":"région trop petite — 幅 < 64 px"}""");

            Assert.Equal("région trop petite — 幅 < 64 px", Assert.Single(events.Errors));
            Assert.Equal(ShareHandshake.Started, protocol.Handshake);   // not fatal
            Assert.Equal(new ShareRegionRect(0, 0, 640, 480), protocol.AppliedRegion);

            // a rejection with no message still reports something the user could be shown.
            protocol.HandleLine("""{"type":"command_error"}""");
            Assert.Equal(2, events.Errors.Count);
            Assert.False(String.IsNullOrEmpty(events.Errors[1]));
        }

        // ------------------------------------------------------------------ tolerance

        /// <summary>
        /// Pins the tolerance rule: this runs on a pump thread whose death would silently deafen the
        /// whole session, so nothing on stdout may throw and nothing may corrupt state. libobs
        /// shares the stream, the helper's own logging may change at any time, and a line can arrive
        /// truncated — all of it is chatter, and the share keeps running.
        /// </summary>
        [Fact]
        public void Nothing_on_stdout_can_throw_or_corrupt_the_session()
        {
            var (protocol, events) = New();

            protocol.HandleLine(Region("sharing_started", 10, 20, 640, 480));
            protocol.NoteObscureSent();
            protocol.HandleLine("""{"type":"obscure","mode":"blur","strength":50}""");

            var before = protocol.AppliedRegion;

            protocol.HandleLine(null);                                     // the pump's end of stream
            protocol.HandleLine("");                                       // a blank line
            protocol.HandleLine("   ");                                    // …and a whitespace one
            protocol.HandleLine("info: [obs-d3d11] loading shader");       // libobs sharing the stream
            protocol.HandleLine("""{"type":"status","fps":""");            // truncated mid-object
            protocol.HandleLine("""{"type":"status","fps":}""");           // braced but not JSON
            protocol.HandleLine("""{"type":42}""");                        // type is not a string
            protocol.HandleLine("""{"fps":29.9}""");                       // no type at all
            protocol.HandleLine("""{}""");                                 // an empty object
            protocol.HandleLine("""[{"type":"initialized"}]""");           // an array, not an object
            protocol.HandleLine("""{"type":"teleported","to":"mars"}""");  // a newer helper's line

            // no throw, and not one byte of state moved.
            Assert.Equal(ShareHandshake.Started, protocol.Handshake);
            Assert.Same(before, protocol.AppliedRegion);
            Assert.Equal(ShareObscureMode.Blur, protocol.ObscureMode);
            Assert.Equal(50, protocol.ObscureStrength);
            Assert.True(protocol.BlurAvailable);
            Assert.Empty(events.Status);
            Assert.Empty(events.Errors);

            // the blank/whitespace/null lines are dropped; everything with content is logged.
            Assert.Equal(8, events.Chatter.Count);
        }

        /// <summary>Pins CRLF tolerance: the helper writes LF, but a pipe read on Windows can hand
        /// the reader the CR too, and a share must not go deaf over a line ending.</summary>
        [Fact]
        public void Crlf_line_endings_are_read_as_protocol_lines()
        {
            var (protocol, events) = New();

            protocol.HandleLine("{\"type\":\"initialized\"}\r");
            protocol.HandleLine("  " + Region("sharing_started", 0, 0, 800, 600) + "\r\n");
            protocol.HandleLine("\t{\"type\":\"status\",\"fps\":29.9}\r\n");

            Assert.Equal(1, events.Initialized);
            Assert.Equal(ShareHandshake.Started, protocol.Handshake);
            Assert.Equal(new ShareRegionRect(0, 0, 800, 600), protocol.AppliedRegion);
            Assert.Equal(29.9, Assert.Single(events.Status));
            Assert.Empty(events.Chatter);
        }

        /// <summary>
        /// Pins BOM tolerance, which here means "survives it", not "parses it". U+FEFF is a format
        /// character and NOT whitespace to <c>String.Trim</c>, so a BOM-prefixed line fails the
        /// brace-framing check and is logged as chatter — deliberately the same treatment as any
        /// other unrecognizable line, and the important half of the contract: no throw, no state
        /// change, no deafened pump. It costs nothing in practice because the driver reads stdout
        /// through a <c>StreamReader</c>, which strips a real byte-order mark before the protocol
        /// ever sees the text; a BOM reaching this method means something upstream is already wrong.
        /// </summary>
        [Fact]
        public void A_byte_order_mark_is_survivable()
        {
            var (protocol, events) = New();

            protocol.HandleLine(Region("sharing_started", 0, 0, 800, 600));
            protocol.HandleLine("\uFEFF" + "{\"type\":\"status\",\"fps\":29.9}");

            Assert.Equal(ShareHandshake.Started, protocol.Handshake);
            Assert.Equal(new ShareRegionRect(0, 0, 800, 600), protocol.AppliedRegion);
            Assert.Empty(events.Status);
            Assert.Single(events.Chatter);   // accounted for, never silently swallowed
        }

        // ------------------------------------------------------------------ the rect itself

        /// <summary>The wire form of a rectangle is <c>X,Y,W,H</c> and its equality is by value —
        /// which is what lets a caller tell "the helper applied what we asked" from "the helper
        /// clamped it".</summary>
        [Fact]
        public void A_region_is_a_value_with_one_wire_spelling()
        {
            var rect = new ShareRegionRect(-10, 20, 640, 480);

            Assert.Equal("-10,20,640,480", rect.ToWireString());
            Assert.Equal(rect.ToWireString(), rect.ToString());

            Assert.True(rect == new ShareRegionRect(-10, 20, 640, 480));
            Assert.True(rect != new ShareRegionRect(-10, 20, 640, 481));
            Assert.Equal(rect.GetHashCode(), new ShareRegionRect(-10, 20, 640, 480).GetHashCode());

            // null is never a region — the driver hands out a null AppliedRegion until the helper
            // has said one, and == must answer that without dereferencing anything.
            ShareRegionRect nothing = null;
            Assert.False(rect == null);
            Assert.True(rect != null);
            Assert.True(nothing == null);
            Assert.False(rect.Equals(nothing));
            Assert.False(rect.Equals("-10,20,640,480"));   // its own wire spelling is not itself
        }

        // ------------------------------------------------------------------ outbound commands

        /// <summary>The strength argument belongs to <c>blur</c> and <c>pixelate</c> and to nothing
        /// else: the helper's parser routes <c>hide</c> through a reject-strength arm, so
        /// <c>obscure hide 50</c> comes back as <c>command_error</c> with the region untouched — and
        /// with the pending-ack count stranded, which would misread the next unsolicited retraction
        /// as our own ack.</summary>
        [Fact]
        public void Only_blur_and_pixelate_carry_a_strength_on_the_wire()
        {
            Assert.Equal("unobscure", ShareRegionProtocol.BuildObscureCommand(ShareObscureMode.None, 50));
            Assert.Equal("obscure blur 50", ShareRegionProtocol.BuildObscureCommand(ShareObscureMode.Blur, 50));
            Assert.Equal("obscure pixelate 80", ShareRegionProtocol.BuildObscureCommand(ShareObscureMode.Pixelate, 80));
            Assert.Equal("obscure hide", ShareRegionProtocol.BuildObscureCommand(ShareObscureMode.Hide, 50));
        }

        /// <summary>The helper takes 1..=100 and answers anything else with <c>command_error</c>, so
        /// the clamp happens before the write rather than being discovered on the wire.</summary>
        [Fact]
        public void Obscure_strength_is_clamped_into_the_helpers_range()
        {
            Assert.Equal("obscure blur 1", ShareRegionProtocol.BuildObscureCommand(ShareObscureMode.Blur, 0));
            Assert.Equal("obscure blur 1", ShareRegionProtocol.BuildObscureCommand(ShareObscureMode.Blur, -5));
            Assert.Equal("obscure blur 100", ShareRegionProtocol.BuildObscureCommand(ShareObscureMode.Blur, 1000));
        }

        // ------------------------------------------------------------------ the binary

        /// <summary>The helper is spawned by name out of the obs-express payload, so the name has to
        /// carry the platform's executable extension — asserted without spawning anything, because
        /// the one-HWND invariant means a real spawn is never something a test may do.</summary>
        [Fact]
        public void The_helper_binary_is_named_for_the_platform()
        {
            Assert.Equal(OperatingSystem.IsWindows() ? "clowd_share_region.exe" : "clowd_share_region",
                ShareRegionDriver.BinaryFileName);
            Assert.Equal("CLOWD_SHARE_REGION_PATH", ShareRegionDriver.EnvVarName);
        }
    }
}
