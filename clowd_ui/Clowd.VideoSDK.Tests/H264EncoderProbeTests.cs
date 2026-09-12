using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Media;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Auto selection (<see cref="H264EncoderProbe"/>): the order and fallback policy over a fake
    /// availability list, without FFmpeg; then the real probe on this machine — whatever it picks
    /// must actually open, and the answer must be cached.
    /// </summary>
    public class H264EncoderProbeTests
    {
        /// <summary>An oracle that opens only the listed encoders and records every ask.</summary>
        private static Func<VideoEncoder, string> Opens(List<VideoEncoder> asked, params VideoEncoder[] available)
            => candidate =>
            {
                asked.Add(candidate);
                return Array.IndexOf(available, candidate) >= 0 ? null : "not here";
            };

        [Fact]
        public void Auto_order_is_nvenc_amf_then_videotoolbox_on_mac_then_x264()
        {
            Assert.Equal(new[] { VideoEncoder.Nvenc, VideoEncoder.Amf, VideoEncoder.Software },
                H264EncoderProbe.AutoCandidates(macOS: false));
            Assert.Equal(new[] { VideoEncoder.Nvenc, VideoEncoder.Amf, VideoEncoder.VideoToolbox, VideoEncoder.Software },
                H264EncoderProbe.AutoCandidates(macOS: true));
        }

        [Fact]
        public void First_candidate_that_opens_wins_and_later_ones_are_not_probed()
        {
            var asked = new List<VideoEncoder>();
            var log = new List<string>();
            var choice = H264EncoderProbe.Choose(H264EncoderProbe.AutoCandidates(false),
                Opens(asked, VideoEncoder.Nvenc, VideoEncoder.Amf), log.Add);
            Assert.Equal(VideoEncoder.Nvenc, choice);
            Assert.Equal(new[] { VideoEncoder.Nvenc }, asked);
            Assert.Contains("auto -> h264_nvenc", Assert.Single(log), StringComparison.Ordinal);
        }

        [Fact]
        public void A_failed_nvenc_falls_through_to_amf()
        {
            var asked = new List<VideoEncoder>();
            var log = new List<string>();
            var choice = H264EncoderProbe.Choose(H264EncoderProbe.AutoCandidates(false),
                Opens(asked, VideoEncoder.Amf), log.Add);
            Assert.Equal(VideoEncoder.Amf, choice);
            Assert.Equal(new[] { VideoEncoder.Nvenc, VideoEncoder.Amf }, asked);
            var line = Assert.Single(log);
            Assert.Contains("auto -> h264_amf", line, StringComparison.Ordinal);
            Assert.Contains("h264_nvenc: not here", line, StringComparison.Ordinal);
        }

        [Fact]
        public void VideoToolbox_is_reached_on_mac_after_the_windows_encoders()
        {
            var asked = new List<VideoEncoder>();
            var choice = H264EncoderProbe.Choose(H264EncoderProbe.AutoCandidates(true),
                Opens(asked, VideoEncoder.VideoToolbox), null);
            Assert.Equal(VideoEncoder.VideoToolbox, choice);
            Assert.Equal(new[] { VideoEncoder.Nvenc, VideoEncoder.Amf, VideoEncoder.VideoToolbox }, asked);
        }

        [Fact]
        public void No_hardware_encoder_means_x264_without_probing_it()
        {
            var asked = new List<VideoEncoder>();
            var log = new List<string>();
            var choice = H264EncoderProbe.Choose(H264EncoderProbe.AutoCandidates(true), Opens(asked), log.Add);
            Assert.Equal(VideoEncoder.Software, choice);
            Assert.DoesNotContain(VideoEncoder.Software, asked);
            Assert.Equal(3, asked.Count);
            var line = Assert.Single(log);
            Assert.Contains("auto -> libx264", line, StringComparison.Ordinal);
            Assert.Contains("h264_videotoolbox: not here", line, StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_candidate_list_is_x264()
        {
            Assert.Equal(VideoEncoder.Software, H264EncoderProbe.Choose(Array.Empty<VideoEncoder>(), _ => null, null));
            Assert.Throws<ArgumentException>(() => H264EncoderProbe.Choose(new[] { VideoEncoder.Auto }, _ => null, null));
        }

        [Fact]
        public void Explicit_choices_resolve_to_themselves_without_probing()
        {
            var log = new List<string>();
            foreach (var encoder in new[] { VideoEncoder.Software, VideoEncoder.Nvenc, VideoEncoder.Amf, VideoEncoder.VideoToolbox })
                Assert.Equal(encoder, H264EncoderProbe.Resolve(encoder, log.Add));
            Assert.Empty(log);
        }

        [Fact]
        public void Auto_resolves_to_an_encoder_that_opens_here_and_is_cached()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

            var first = new List<string>();
            var choice = H264EncoderProbe.Resolve(VideoEncoder.Auto, first.Add);
            Assert.NotEqual(VideoEncoder.Auto, choice);
            Assert.True(H264EncoderProbe.CanOpen(choice, out var reason), $"{choice} was chosen but does not open: {reason}");

            // the second ask is answered from the cache: same answer, no probe line
            var second = new List<string>();
            Assert.Equal(choice, H264EncoderProbe.Resolve(VideoEncoder.Auto, second.Add));
            Assert.Empty(second);
        }

        [Fact]
        public void Software_always_opens_and_auto_is_never_openable()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            Assert.True(H264EncoderProbe.CanOpen(VideoEncoder.Software, out var reason), reason);
            Assert.False(H264EncoderProbe.CanOpen(VideoEncoder.Auto, out reason));
            Assert.Contains("Auto", reason, StringComparison.Ordinal);
        }

        [Fact]
        public void VideoToolbox_does_not_open_off_a_mac()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            Assert.SkipWhen(OperatingSystem.IsMacOS(), "VideoToolbox is present on macOS");
            Assert.False(H264EncoderProbe.CanOpen(VideoEncoder.VideoToolbox, out var reason));
            Assert.Contains("not present", reason, StringComparison.Ordinal);
        }
    }
}
