using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.VideoSDK.Media;
using FFmpeg.AutoGen.Abstractions;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The crf → per-encoder mapping (<see cref="H264EncoderSettings"/>) is a pure function, so
    /// every row of the quality table is pinned here without FFmpeg: the option names and values
    /// each encoder is handed, the generic rate-control fields, and the description line.
    /// </summary>
    public class H264EncoderSettingsTests
    {
        private static string Opt(H264EncoderSettings s, string name)
        {
            var matches = s.PrivateOptions.Where(o => o.Key == name).ToList();
            Assert.True(matches.Count == 1, $"expected exactly one '{name}' option, found {matches.Count}");
            return matches[0].Value;
        }

        private static H264EncoderSettings At(VideoEncoder encoder, int crf, int w = 1920, int h = 1080, int fpsNum = 60, int fpsDen = 1)
            => H264EncoderSettings.For(encoder, crf, w, h, fpsNum, fpsDen);

        [Fact]
        public void X264_passes_crf_through_with_preset_fast_and_profile_high()
        {
            var s = At(VideoEncoder.Software, 18);
            Assert.Equal(VideoEncoder.Software, s.Encoder);
            Assert.Equal("libx264", s.CodecName);
            Assert.Equal("18", Opt(s, "crf"));
            Assert.Equal("fast", Opt(s, "preset"));
            Assert.Equal("high", Opt(s, "profile"));
            Assert.Equal(3, s.PrivateOptions.Count);
            Assert.Equal(0, s.BitRate);
            Assert.Equal(0, s.MaxRate);
            Assert.False(s.ConstantQuality);
            Assert.Null(s.BitrateFallback);
            Assert.Equal("x264 crf=18 preset=fast profile=high bf=2 gop=600", s.Description);
        }

        /// <summary>crf 0 is lossless, and x264 refuses to apply the High profile to a lossless
        /// encode; the option is left out so x264 picks High 4:4:4 Predictive itself.</summary>
        [Fact]
        public void X264_at_crf_0_leaves_the_profile_to_x264()
        {
            var s = At(VideoEncoder.Software, 0);
            Assert.Equal("0", Opt(s, "crf"));
            Assert.Equal("fast", Opt(s, "preset"));
            Assert.DoesNotContain(s.PrivateOptions, o => o.Key == "profile");
            Assert.Equal(2, s.PrivateOptions.Count);
            Assert.Equal("x264 crf=0 (lossless) preset=fast profile=high444 bf=2 gop=600", s.Description);

            // 1 is the first lossy crf and gets the normal profile
            Assert.Equal("high", Opt(At(VideoEncoder.Software, 1), "profile"));
        }

        [Theory]
        [InlineData(21, 25)]
        [InlineData(0, 4)]
        [InlineData(47, 51)]
        [InlineData(51, 51)] // clamped: 55 is off NVENC's scale
        public void Nvenc_is_constant_quality_vbr_at_crf_plus_four(int crf, int cq)
        {
            Assert.Equal(cq, H264EncoderSettings.NvencCq(crf));
            var s = At(VideoEncoder.Nvenc, crf);
            Assert.Equal("h264_nvenc", s.CodecName);
            Assert.Equal("vbr", Opt(s, "rc"));
            Assert.Equal(cq.ToString(), Opt(s, "cq"));
            Assert.Equal("p6", Opt(s, "preset"));
            Assert.Equal("hq", Opt(s, "tune"));
            Assert.Equal("qres", Opt(s, "multipass"));
            Assert.Equal("8", Opt(s, "rc-lookahead"));
            Assert.Equal("1", Opt(s, "spatial-aq"));
            Assert.Equal("high", Opt(s, "profile"));
            // b:v=0 and maxrate=0: anything else caps bursts instead of letting cq decide
            Assert.Equal(0, s.BitRate);
            Assert.Equal(0, s.MaxRate);
            Assert.Equal(2, s.MaxBFrames);
            Assert.StartsWith($"NVENC vbr cq={cq} preset=p6 tune=hq multipass=qres rc-lookahead=8 spatial-aq=1 ", s.Description, StringComparison.Ordinal);
        }

        [Fact]
        public void Amf_is_constant_qp_at_crf_for_every_picture_type()
        {
            var s = At(VideoEncoder.Amf, 23);
            Assert.Equal("h264_amf", s.CodecName);
            Assert.Equal("cqp", Opt(s, "rc"));
            Assert.Equal("23", Opt(s, "qp_i"));
            Assert.Equal("23", Opt(s, "qp_p"));
            Assert.Equal("23", Opt(s, "qp_b"));
            Assert.Equal("quality", Opt(s, "quality"));
            Assert.Equal("high", Opt(s, "profile"));
            // amfenc_h264.c only applies bf when max_b_frames is set too
            Assert.Equal("2", Opt(s, "max_b_frames"));
            Assert.Equal("2", Opt(s, "bf"));
            Assert.Equal(2, s.MaxBFrames);
            Assert.Equal(0, s.BitRate);
            Assert.Equal("AMF cqp qp=23 quality=quality profile=high bf=2 gop=600", s.Description);
        }

        [Theory]
        [InlineData(0, 70)]   // 100 clamped to the slider's useful top
        [InlineData(15, 70)]  // 71 -> 70
        [InlineData(16, 69)]
        [InlineData(23, 55)]
        [InlineData(24, 53)]
        [InlineData(29, 43)]
        [InlineData(44, 15)]  // 14 -> 15
        [InlineData(51, 15)]
        public void VideoToolbox_quality_inverts_crf_onto_the_clamped_slider(int crf, int q)
        {
            Assert.Equal(q, H264EncoderSettings.VideoToolboxQuality(crf));
        }

        [Fact]
        public void VideoToolbox_quality_keeps_the_presets_distinguishable()
        {
            // High(16) > Medium(23) > Low(29) must map to distinct slider values
            Assert.True(H264EncoderSettings.VideoToolboxQuality(16) > H264EncoderSettings.VideoToolboxQuality(23));
            Assert.True(H264EncoderSettings.VideoToolboxQuality(23) > H264EncoderSettings.VideoToolboxQuality(29));
        }

        [Fact]
        public void VideoToolbox_opens_in_quality_mode_with_a_bitrate_fallback()
        {
            var s = At(VideoEncoder.VideoToolbox, 23, 3440, 1440, 30, 1);
            Assert.Equal("h264_videotoolbox", s.CodecName);
            Assert.True(s.ConstantQuality);
            Assert.Equal(55 * ffmpeg.FF_QP2LAMBDA, s.GlobalQuality); // q:v=55 in lambda units
            Assert.Equal(0, s.BitRate);
            Assert.Equal("high", Opt(s, "profile"));
            Assert.Equal("0", Opt(s, "allow_sw"));
            Assert.Equal("VideoToolbox q:v=55 profile=high bf=2 gop=300", s.Description);

            var fallback = s.BitrateFallback;
            Assert.NotNull(fallback);
            Assert.Equal(VideoEncoder.VideoToolbox, fallback.Encoder);
            Assert.False(fallback.ConstantQuality);
            Assert.Equal(4000 * 1000L, fallback.BitRate); // the reference size at the reference rate
            Assert.Equal(s.PrivateOptions, fallback.PrivateOptions);
            Assert.Null(fallback.BitrateFallback);
            Assert.Contains("b:v=4000k", fallback.Description, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(3440, 1440, 30, 1, 4000)]
        [InlineData(1920, 1080, 30, 1, 1674)]
        [InlineData(1920, 1080, 60, 1, 3349)]       // 0.4186 * 2 * 4000 = 3348.8
        [InlineData(640, 480, 30, 1, 1500)]    // floor
        [InlineData(3840, 2160, 60, 1, 6000)]  // ceiling
        [InlineData(1920, 1080, 30000, 1001, 1673)] // 0.4186 * (29.97/30) * 4000 = 1672.7
        public void VideoToolbox_bitrate_scales_by_pixel_rate_within_bounds(int w, int h, int fpsNum, int fpsDen, int kbps)
        {
            Assert.Equal(kbps, H264EncoderSettings.VideoToolboxBitrateKbps(w, h, fpsNum, fpsDen));
        }

        [Theory]
        [InlineData(60, 1, 600)]
        [InlineData(30, 1, 300)]
        [InlineData(30000, 1001, 300)]
        [InlineData(24, 1, 240)]
        [InlineData(1, 100, 1)] // 0.01 fps: never below one frame
        public void Gop_is_ten_seconds_of_frames(int fpsNum, int fpsDen, int frames)
        {
            Assert.Equal(frames, H264EncoderSettings.GopFrames(fpsNum, fpsDen));
            foreach (var encoder in new[] { VideoEncoder.Software, VideoEncoder.Nvenc, VideoEncoder.Amf, VideoEncoder.VideoToolbox })
            {
                var s = At(encoder, 21, fpsNum: fpsNum, fpsDen: fpsDen);
                Assert.Equal(frames, s.GopSize);
                Assert.Equal(H264EncoderSettings.BFrames, s.MaxBFrames);
                Assert.Contains($"gop={frames}", s.Description, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Auto_and_bad_inputs_are_rejected()
        {
            Assert.Throws<ArgumentException>(() => At(VideoEncoder.Auto, 21));
            Assert.Throws<ArgumentException>(() => H264EncoderSettings.CodecNameOf(VideoEncoder.Auto));
            Assert.Throws<ArgumentOutOfRangeException>(() => At(VideoEncoder.Software, 52));
            Assert.Throws<ArgumentOutOfRangeException>(() => At(VideoEncoder.Software, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => At(VideoEncoder.Software, 21, w: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => At(VideoEncoder.Software, 21, fpsNum: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => H264EncoderSettings.GopFrames(30, 0));
        }

        [Fact]
        public void Codec_names_are_the_ffmpeg_encoder_names()
        {
            Assert.Equal("libx264", H264EncoderSettings.CodecNameOf(VideoEncoder.Software));
            Assert.Equal("h264_nvenc", H264EncoderSettings.CodecNameOf(VideoEncoder.Nvenc));
            Assert.Equal("h264_amf", H264EncoderSettings.CodecNameOf(VideoEncoder.Amf));
            Assert.Equal("h264_videotoolbox", H264EncoderSettings.CodecNameOf(VideoEncoder.VideoToolbox));
        }

        [Fact]
        public void Wire_names_round_trip_and_reject_anything_else()
        {
            foreach (var encoder in (VideoEncoder[])Enum.GetValues(typeof(VideoEncoder)))
            {
                string name = VideoEncoderNames.Of(encoder);
                Assert.Equal(name, name.ToLowerInvariant());
                Assert.True(VideoEncoderNames.TryParse(name, out var parsed));
                Assert.Equal(encoder, parsed);
                Assert.True(VideoEncoderNames.TryParse(name.ToUpperInvariant(), out parsed));
                Assert.Equal(encoder, parsed);
            }
            Assert.Equal("auto|software|nvenc|amf|videotoolbox", VideoEncoderNames.All);
            Assert.False(VideoEncoderNames.TryParse("x264", out _));
            Assert.False(VideoEncoderNames.TryParse(" nvenc", out _));
            Assert.False(VideoEncoderNames.TryParse("", out _));
            Assert.False(VideoEncoderNames.TryParse(null, out _));
        }
    }
}
