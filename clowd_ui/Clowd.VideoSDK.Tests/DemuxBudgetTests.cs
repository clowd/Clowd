using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The demuxer's read-ahead rule. There is one read thread feeding every stream, so when it
    /// parks it parks them all — the rule exists to guarantee it never parks while a stream is
    /// still starved. Getting this wrong deadlocks playback outright: an obs recording carries
    /// ~60 video packets before its first audio packet, so a reader that stops on a full video
    /// queue never delivers the audio the master clock needs, the video presenter waits on that
    /// clock forever, and the queue it is parked on is never drained.
    /// </summary>
    public class DemuxBudgetTests
    {
        [Fact]
        public void Keeps_reading_while_any_stream_is_starved()
        {
            // the deadlock case: one stream far ahead, the other with nothing.
            Assert.False(DemuxBudget.ShouldPause(minPacketsAcrossStreams: 0, totalBytes: 512 * 1024));
            Assert.False(DemuxBudget.ShouldPause(minPacketsAcrossStreams: 1, totalBytes: 512 * 1024));
        }

        [Fact]
        public void Pauses_once_every_stream_is_stocked()
        {
            Assert.False(DemuxBudget.ShouldPause(DemuxBudget.MinPacketsPerStream - 1, 0));
            Assert.True(DemuxBudget.ShouldPause(DemuxBudget.MinPacketsPerStream, 0));
            Assert.True(DemuxBudget.ShouldPause(DemuxBudget.MinPacketsPerStream + 100, 0));
        }

        [Fact]
        public void Pauses_on_the_memory_backstop_even_with_a_starved_stream()
        {
            // a stream that has genuinely run out (audio ending before video) would otherwise
            // pull the whole rest of the file into memory.
            Assert.True(DemuxBudget.ShouldPause(0, DemuxBudget.MaxBufferedBytes));
            Assert.True(DemuxBudget.ShouldPause(0, DemuxBudget.MaxBufferedBytes + 1));
        }

        [Fact]
        public void Backstop_is_far_above_any_real_interleaving_skew()
        {
            // it must not be the thing that stops read-ahead in normal playback, or it would
            // reintroduce the starvation it is a backstop for.
            Assert.True(DemuxBudget.MaxBufferedBytes >= 16L * 1024 * 1024);
        }
    }
}
