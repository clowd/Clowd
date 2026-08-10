using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Composition;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // TryGetFrame semantics tested in isolation on SequentialFrameCursor — the pts-selection
    // state machine SequentialFrameSource runs — against synthetic PTS sequences, no FFmpeg.
    public class FrameLookupTests
    {
        /// <summary>Feeds a scripted (pts, frame) list to the cursor and records discards.</summary>
        private sealed class Script
        {
            private readonly Queue<(long Pts, string Frame)> _frames;
            public readonly List<string> Discarded = new List<string>();
            public readonly SequentialFrameCursor<string> Cursor;

            public Script(params long[] ptsList)
            {
                _frames = new Queue<(long, string)>();
                for (int i = 0; i < ptsList.Length; i++)
                    _frames.Enqueue((ptsList[i], "f" + i));
                Cursor = new SequentialFrameCursor<string>(Pull, f => Discarded.Add(f));
            }

            private bool Pull(out long pts, out string frame)
            {
                if (_frames.Count == 0)
                {
                    pts = 0;
                    frame = null;
                    return false;
                }

                (pts, frame) = _frames.Dequeue();
                return true;
            }
        }

        [Fact]
        public void Selects_latest_pts_at_or_before_time()
        {
            var s = new Script(0, 10, 20);

            Assert.True(s.Cursor.TryAdvance(0, out long pts, out string frame));
            Assert.Equal(0, pts);
            Assert.Equal("f0", frame); // delivered on first sight

            Assert.True(s.Cursor.TryAdvance(5, out pts, out frame));
            Assert.Equal(0, pts);
            Assert.Null(frame); // unchanged — already delivered

            Assert.True(s.Cursor.TryAdvance(10, out pts, out frame));
            Assert.Equal(10, pts);
            Assert.Equal("f1", frame);

            Assert.True(s.Cursor.TryAdvance(25, out pts, out frame));
            Assert.Equal(20, pts);
            Assert.Equal("f2", frame);
        }

        [Fact]
        public void Vfr_gap_holds_last_frame()
        {
            var s = new Script(0, 5, 50); // long timestamp gap after 5

            Assert.True(s.Cursor.TryAdvance(30, out long pts, out string frame));
            Assert.Equal(5, pts); // frame holds through the gap
            Assert.Equal("f1", frame);

            Assert.True(s.Cursor.TryAdvance(49, out pts, out frame));
            Assert.Equal(5, pts);
            Assert.Null(frame); // still the same held frame

            Assert.True(s.Cursor.TryAdvance(50, out pts, out frame));
            Assert.Equal(50, pts); // next PTS finally arrives
            Assert.Equal("f2", frame);
        }

        [Fact]
        public void Freeze_duplicate_pts_latest_wins()
        {
            var s = new Script(0, 10, 10, 20);

            Assert.True(s.Cursor.TryAdvance(10, out long pts, out string frame));
            Assert.Equal(10, pts);
            Assert.Equal("f2", frame);              // the later duplicate wins
            Assert.Contains("f1", s.Discarded);      // the superseded duplicate was discarded
        }

        [Fact]
        public void Backwards_pts_jump_is_clamped_and_never_rewinds()
        {
            var s = new Script(0, 20, 10, 30); // 10 is a backwards jump after 20

            Assert.True(s.Cursor.TryAdvance(20, out long pts, out string frame));
            // f2's pts clamps to 20, so it supersedes f1 at the same instant — no rewind.
            Assert.Equal(20, pts);
            Assert.Equal("f2", frame);
            Assert.Contains("f1", s.Discarded);

            Assert.True(s.Cursor.TryAdvance(25, out pts, out frame));
            Assert.Equal(20, pts); // still clamped-forward, never 10
            Assert.Null(frame);

            Assert.True(s.Cursor.TryAdvance(30, out pts, out frame));
            Assert.Equal(30, pts);
            Assert.Equal("f3", frame);
        }

        [Fact]
        public void Time_before_first_frame_returns_first_frame()
        {
            var s = new Script(100, 200);

            Assert.True(s.Cursor.TryAdvance(0, out long pts, out string frame));
            Assert.Equal(100, pts); // hold-first: no empty flash before the first PTS
            Assert.Equal("f0", frame);
        }

        [Fact]
        public void Eof_holds_last_frame_forever()
        {
            var s = new Script(0, 10);

            Assert.True(s.Cursor.TryAdvance(1000, out long pts, out string frame));
            Assert.Equal(10, pts);
            Assert.Equal("f1", frame);

            Assert.True(s.Cursor.TryAdvance(2000, out pts, out frame));
            Assert.Equal(10, pts);
            Assert.Null(frame);
        }

        [Fact]
        public void Empty_stream_returns_false()
        {
            var s = new Script();
            Assert.False(s.Cursor.TryAdvance(0, out _, out _));
            Assert.False(s.Cursor.TryAdvance(10, out _, out _)); // stays false, no re-pull crash
        }

        [Fact]
        public void Regressing_request_time_throws()
        {
            var s = new Script(0, 10, 20);
            Assert.True(s.Cursor.TryAdvance(10, out _, out _));
            Assert.Throws<InvalidOperationException>(() => s.Cursor.TryAdvance(5, out _, out _));
            // equal (non-decreasing) is fine
            Assert.True(s.Cursor.TryAdvance(10, out long pts, out _));
            Assert.Equal(10, pts);
        }

        [Fact]
        public void Skipped_frames_discarded_exactly_once_delivered_frames_never()
        {
            var s = new Script(0, 1, 2, 3, 4, 5);

            Assert.True(s.Cursor.TryAdvance(5, out long pts, out string frame));
            Assert.Equal(5, pts);
            Assert.Equal("f5", frame); // delivered to caller, never discarded

            Assert.Equal(new[] { "f0", "f1", "f2", "f3", "f4" }, s.Discarded);
            s.Cursor.Dispose();
            Assert.Equal(5, s.Discarded.Count); // dispose adds nothing: f5 was handed out
        }

        [Fact]
        public void Dispose_discards_undelivered_and_lookahead_frames()
        {
            var s = new Script(0, 10);
            Assert.True(s.Cursor.TryAdvance(0, out _, out string frame));
            Assert.Equal("f0", frame); // f0 delivered; f1 is the cursor's lookahead

            s.Cursor.Dispose();
            Assert.Equal(new[] { "f1" }, s.Discarded);

            // undelivered current is discarded too
            var s2 = new Script(0, 10);
            Assert.True(s2.Cursor.TryAdvance(10, out _, out frame)); // f1 delivered, f0 skipped
            Assert.Equal("f1", frame);
            s2.Cursor.Dispose();
            Assert.Equal(new[] { "f0" }, s2.Discarded);
        }
    }
}
