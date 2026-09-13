using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Playback;
using FFmpeg.AutoGen.Abstractions;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // Real-encode round trips: Mp4Writer -> temp mp4 -> probe back. These need the FFmpeg
    // natives; when they cannot be located the tests skip (compile coverage remains).
    public class EncoderTests
    {
        // ------------------------------------------------------------------ FFmpeg availability

        /// <summary>
        /// Initializes FFmpeg exactly the way production does (FFmpegLoader: CLOWD_FFMPEG_PATH
        /// first, then a fallback resolver pointing at the obs-express build output — the DLLs
        /// ship alongside that binary). The test resolver walks up from the test assembly to any
        /// ancestor with an obs-express-rs sibling checkout, probing target/release then
        /// target/debug, mirroring ObsBinaryLocator's dev layout.
        /// </summary>
        private static bool FFmpegAvailable => TestFFmpeg.Available;


        private static void RequireFFmpeg() =>
            Assert.SkipUnless(FFmpegAvailable,
                TestFFmpeg.SkipReason);

        // -------------------------------------------------------------------------- raw probe

        /// <summary>What MediaProbe deliberately does not expose: codec ids, pixel format, audio
        /// stream facts. Read straight off the container's codec parameters.</summary>
        private sealed class RawStreamFacts
        {
            public AVCodecID VideoCodecId;
            public AVPixelFormat VideoPixelFormat;
            public int VideoStreams;
            public int AudioStreams;
            public AVCodecID AudioCodecId;
            public int AudioSampleRate;
            public int AudioChannels;
        }

        private static unsafe RawStreamFacts ProbeRaw(string path)
        {
            AVFormatContext* fmt = null;
            int err = ffmpeg.avformat_open_input(&fmt, path, null, null);
            if (err < 0)
                throw new InvalidOperationException($"open failed: {FFmpegLoader.ErrorToString(err)}");
            try
            {
                err = ffmpeg.avformat_find_stream_info(fmt, null);
                if (err < 0)
                    throw new InvalidOperationException($"stream info failed: {FFmpegLoader.ErrorToString(err)}");

                var facts = new RawStreamFacts();
                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    var par = fmt->streams[i]->codecpar;
                    if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        facts.VideoStreams++;
                        facts.VideoCodecId = par->codec_id;
                        facts.VideoPixelFormat = (AVPixelFormat)par->format;
                    }
                    else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        facts.AudioStreams++;
                        facts.AudioCodecId = par->codec_id;
                        facts.AudioSampleRate = par->sample_rate;
                        facts.AudioChannels = par->ch_layout.nb_channels;
                    }
                }
                return facts;
            }
            finally
            {
                ffmpeg.avformat_close_input(&fmt);
            }
        }

        // ---------------------------------------------------------------------------- helpers

        private static string TempMp4() =>
            Path.Combine(Path.GetTempPath(), $"clowd-encoder-test-{Guid.NewGuid():N}.mp4");

        /// <summary>Submits <paramref name="frames"/> solid-color BGRA frames (color varies per
        /// frame so x264 actually encodes motion).</summary>
        private static void SubmitSolidFrames(Mp4Writer writer, int width, int height, int frames)
        {
            var bgra = new byte[width * height * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int n = 0; n < frames; n++)
                {
                    byte b = (byte)(n * 4);
                    for (int i = 0; i < bgra.Length; i += 4)
                    {
                        bgra[i] = b;            // B
                        bgra[i + 1] = 0x80;     // G
                        bgra[i + 2] = 0x20;     // R
                        bgra[i + 3] = 0xFF;     // A
                    }
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), width * 4, width, height, n);
                }
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>2ch 440 Hz sine, fed in deliberately awkward chunk sizes so the FIFO has to
        /// split and join encoder frames (aac frame_size is 1024; 4801 shares no factor with it).</summary>
        private static void SubmitSine(Mp4Writer writer, int sampleRate, int totalFrames)
        {
            const int chunk = 4801;
            int fed = 0;
            while (fed < totalFrames)
            {
                int n = Math.Min(chunk, totalFrames - fed);
                var buf = new float[n * 2];
                for (int i = 0; i < n; i++)
                {
                    float s = 0.25f * MathF.Sin(2f * MathF.PI * 440f * (fed + i) / sampleRate);
                    buf[i * 2] = s;
                    buf[i * 2 + 1] = s;
                }
                writer.SubmitAudioSamples(buf, n);
                fed += n;
            }
        }

        /// <summary>+faststart puts the moov box before mdat; without it movenc appends moov at
        /// the end. Box scan on the raw bytes.</summary>
        private static void AssertFastStart(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int moov = IndexOfAscii(bytes, "moov");
            int mdat = IndexOfAscii(bytes, "mdat");
            Assert.True(moov >= 0, "no moov box found");
            Assert.True(mdat >= 0, "no mdat box found");
            Assert.True(moov < mdat, $"moov at {moov} should precede mdat at {mdat} (+faststart)");
        }

        private static int IndexOfAscii(byte[] haystack, string needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == (byte)needle[j])
                    j++;
                if (j == needle.Length)
                    return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------------------ tests

        [Fact]
        public void Encodes_video_and_audio_mp4()
        {
            RequireFFmpeg();

            const int W = 320, H = 240, Fps = 30, Frames = 60; // 2s of video
            const int Rate = 48000;
            string path = TempMp4();
            try
            {
                using (var writer = new Mp4Writer(path, new Mp4WriterOptions
                {
                    Width = W,
                    Height = H,
                    FpsNum = Fps,
                    FpsDen = 1,
                    Audio = new Mp4AudioOptions { SampleRate = Rate, Channels = 2 },
                }))
                {
                    Assert.True(writer.HasAudio);
                    Assert.Equal(VideoEncoder.Software, writer.Encoder); // the writer's default
                    Assert.Equal("libx264", writer.EncoderName);
                    SubmitSolidFrames(writer, W, H, Frames);
                    SubmitSine(writer, Rate, 2 * Rate); // 2s of sine
                    writer.Finish();
                }

                Assert.True(new FileInfo(path).Length > 0);

                var probe = MediaProbe.ProbeDetailed(path);
                Assert.True(probe.HasAudio);
                var v = Assert.Single(probe.VideoStreams);
                Assert.Equal(W, v.Width);
                Assert.Equal(H, v.Height);
                Assert.Equal("h264", v.CodecName);
                // rational equality by cross-multiply: avg rate must be exactly 30/1
                Assert.True(v.AvgFrameRateNum > 0 && v.AvgFrameRateDen > 0, "no avg frame rate");
                Assert.Equal(30L * v.AvgFrameRateDen, (long)v.AvgFrameRateNum);
                // 2s of CFR video; aac priming/edit-list may pad the container a little
                Assert.InRange(probe.DurationTicks, 19_000_000, 22_500_000);

                var raw = ProbeRaw(path);
                Assert.Equal(1, raw.VideoStreams);
                Assert.Equal(AVCodecID.AV_CODEC_ID_H264, raw.VideoCodecId);
                Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUV420P, raw.VideoPixelFormat);
                Assert.Equal(1, raw.AudioStreams);
                Assert.Equal(AVCodecID.AV_CODEC_ID_AAC, raw.AudioCodecId);
                Assert.Equal(Rate, raw.AudioSampleRate);
                Assert.Equal(2, raw.AudioChannels);

                AssertFastStart(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Encodes_video_only_mp4()
        {
            RequireFFmpeg();

            const int W = 320, H = 240, Fps = 30, Frames = 30; // 1s
            string path = TempMp4();
            try
            {
                using (var writer = new Mp4Writer(path, new Mp4WriterOptions
                {
                    Width = W,
                    Height = H,
                    FpsNum = Fps,
                    FpsDen = 1,
                }))
                {
                    Assert.False(writer.HasAudio);
                    SubmitSolidFrames(writer, W, H, Frames);
                    writer.Finish();
                }

                Assert.True(new FileInfo(path).Length > 0);

                var probe = MediaProbe.ProbeDetailed(path);
                Assert.False(probe.HasAudio);
                var v = Assert.Single(probe.VideoStreams);
                Assert.Equal(W, v.Width);
                Assert.Equal(H, v.Height);
                Assert.InRange(probe.DurationTicks, 9_500_000, 10_500_000);

                var raw = ProbeRaw(path);
                Assert.Equal(1, raw.VideoStreams);
                Assert.Equal(0, raw.AudioStreams);
                Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUV420P, raw.VideoPixelFormat);

                AssertFastStart(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Submitting_audio_to_video_only_writer_throws()
        {
            RequireFFmpeg();

            string path = TempMp4();
            try
            {
                using var writer = new Mp4Writer(path, new Mp4WriterOptions
                {
                    Width = 320,
                    Height = 240,
                    FpsNum = 30,
                });
                Assert.Throws<InvalidOperationException>(() => writer.SubmitAudioSamples(new float[64], 32));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Submitting_after_finish_throws()
        {
            RequireFFmpeg();

            const int W = 320, H = 240;
            string path = TempMp4();
            try
            {
                using var writer = new Mp4Writer(path, new Mp4WriterOptions
                {
                    Width = W,
                    Height = H,
                    FpsNum = 30,
                });
                SubmitSolidFrames(writer, W, H, 2);
                writer.Finish();
                writer.Finish(); // idempotent

                var bgra = new byte[W * H * 4];
                var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                try
                {
                    Assert.Throws<InvalidOperationException>(
                        () => writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, 2));
                }
                finally
                {
                    pin.Free();
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Abandon_skips_the_trailer_and_releases_the_file()
        {
            RequireFFmpeg();

            const int W = 320, H = 240;
            string path = TempMp4();
            try
            {
                using (var writer = new Mp4Writer(path, new Mp4WriterOptions
                {
                    Width = W,
                    Height = H,
                    FpsNum = 30,
                }))
                {
                    SubmitSolidFrames(writer, W, H, 30);
                    writer.Abandon(); // cancel/error path: the caller deletes the output next
                }

                // No trailer ran: with +faststart the trailer is a whole-file rewrite (moov
                // relocation), pure wasted I/O before a delete. An abandoned file therefore has
                // no moov box at all — it is deliberately unreadable.
                byte[] bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length > 0, "abandoned writer wrote nothing at all");
                Assert.True(IndexOfAscii(bytes, "moov") < 0,
                    "abandoned writer still wrote a moov box — the +faststart trailer rewrite ran");
            }
            finally
            {
                File.Delete(path); // also proves Dispose released the handle
            }
        }

        [Fact]
        public void Rejects_invalid_options()
        {
            RequireFFmpeg();

            string path = TempMp4();
            // odd dimensions (yuv420p), zero fps, out-of-range crf
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new Mp4Writer(path, new Mp4WriterOptions { Width = 321, Height = 240, FpsNum = 30 }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new Mp4Writer(path, new Mp4WriterOptions { Width = 320, Height = 240, FpsNum = 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new Mp4Writer(path, new Mp4WriterOptions { Width = 320, Height = 240, FpsNum = 30, Crf = 52 }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new Mp4Writer(path, new Mp4WriterOptions { Width = 320, Height = 240, FpsNum = 30, Encoder = (VideoEncoder)99 }));
            Assert.False(File.Exists(path) && new FileInfo(path).Length > 0);
        }

        // -------------------------------------------------------------------- encoder choice

        /// <summary>Encodes one second through <paramref name="encoder"/> and checks the mp4
        /// plays back as H.264 with the expected frame count, at the probe size (above every
        /// hardware encoder's minimum; NVENC refuses 64x64).</summary>
        private static (List<string> Log, VideoEncoder Ran, string Name) RenderWith(VideoEncoder encoder, int crf = Mp4WriterOptions.DefaultCrf)
        {
            const int W = H264EncoderProbe.ProbeWidth, H = H264EncoderProbe.ProbeHeight, Fps = 30, Frames = 30;
            string path = TempMp4();
            var log = new List<string>();
            try
            {
                VideoEncoder ran;
                string name;
                using (var writer = new Mp4Writer(path, new Mp4WriterOptions
                {
                    Width = W,
                    Height = H,
                    FpsNum = Fps,
                    FpsDen = 1,
                    Crf = crf,
                    Encoder = encoder,
                    DiagnosticLog = log.Add,
                    Audio = new Mp4AudioOptions { SampleRate = 48000, Channels = 2 },
                }))
                {
                    ran = writer.Encoder;
                    name = writer.EncoderName;
                    Assert.NotEqual(VideoEncoder.Auto, ran);
                    Assert.Equal(H264EncoderSettings.CodecNameOf(ran), name);
                    Assert.False(string.IsNullOrEmpty(writer.EncoderDescription));
                    SubmitSolidFrames(writer, W, H, Frames);
                    SubmitSine(writer, 48000, 48000);
                    writer.Finish();
                }

                var probe = MediaProbe.ProbeDetailed(path);
                var v = Assert.Single(probe.VideoStreams);
                Assert.Equal("h264", v.CodecName);
                Assert.Equal(W, v.Width);
                Assert.Equal(H, v.Height);
                Assert.Equal(30L * v.AvgFrameRateDen, (long)v.AvgFrameRateNum);
                Assert.InRange(probe.DurationTicks, 9_500_000, 11_500_000);
                Assert.True(probe.HasAudio);

                var raw = ProbeRaw(path);
                Assert.Equal(AVCodecID.AV_CODEC_ID_H264, raw.VideoCodecId);
                Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUV420P, raw.VideoPixelFormat);
                Assert.Equal(AVCodecID.AV_CODEC_ID_AAC, raw.AudioCodecId);
                AssertFastStart(path);

                // the one line that names the encoder and its effective settings
                var chosen = Assert.Single(log, line => line.StartsWith("Mp4Writer: video encoder ", StringComparison.Ordinal));
                Assert.Contains(name, chosen, StringComparison.Ordinal);
                Assert.Contains($"requested {VideoEncoderNames.Of(encoder)}, crf {crf}", chosen, StringComparison.Ordinal);
                return (log, ran, name);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Software_encoder_writes_a_playable_mp4()
        {
            RequireFFmpeg();
            var (log, ran, name) = RenderWith(VideoEncoder.Software, crf: 18);
            Assert.Equal(VideoEncoder.Software, ran);
            Assert.Equal("libx264", name);
            Assert.Contains(log, line => line.Contains("x264 crf=18 preset=fast", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("falling back", StringComparison.Ordinal));
        }

        /// <summary>crf 0 is x264's lossless mode, which the High profile forbids
        /// (x264_param_apply_profile: "high profile doesn't support lossless"); the old writer
        /// set no profile and rendered it, so the settings must leave the profile to x264
        /// there, which picks High 4:4:4 Predictive.</summary>
        [Fact]
        public void Software_encoder_renders_lossless_at_crf_0()
        {
            RequireFFmpeg();
            var (log, ran, name) = RenderWith(VideoEncoder.Software, crf: 0);
            Assert.Equal(VideoEncoder.Software, ran);
            Assert.Equal("libx264", name);
            Assert.Contains(log, line => line.Contains("x264 crf=0 (lossless) preset=fast profile=high444", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("falling back", StringComparison.Ordinal));
        }

        [Fact]
        public void Nvenc_encoder_writes_a_playable_mp4_when_it_opens_here()
        {
            RequireFFmpeg();
            Assert.SkipUnless(H264EncoderProbe.CanOpen(VideoEncoder.Nvenc, out var reason), "h264_nvenc does not open here: " + reason);
            var (log, ran, name) = RenderWith(VideoEncoder.Nvenc, crf: 21);
            Assert.Equal(VideoEncoder.Nvenc, ran);
            Assert.Equal("h264_nvenc", name);
            Assert.Contains(log, line => line.Contains("NVENC vbr cq=25 preset=p6", StringComparison.Ordinal));
        }

        [Fact]
        public void Amf_encoder_writes_a_playable_mp4_when_it_opens_here()
        {
            RequireFFmpeg();
            Assert.SkipUnless(H264EncoderProbe.CanOpen(VideoEncoder.Amf, out var reason), "h264_amf does not open here: " + reason);
            var (log, ran, name) = RenderWith(VideoEncoder.Amf, crf: 21);
            Assert.Equal(VideoEncoder.Amf, ran);
            Assert.Equal("h264_amf", name);
            Assert.Contains(log, line => line.Contains("AMF cqp qp=21", StringComparison.Ordinal));
        }

        [Fact]
        public void VideoToolbox_encoder_writes_a_playable_mp4_when_it_opens_here()
        {
            RequireFFmpeg();
            Assert.SkipUnless(OperatingSystem.IsMacOS() && H264EncoderProbe.CanOpen(VideoEncoder.VideoToolbox, out _),
                "h264_videotoolbox is macOS only");
            var (log, ran, name) = RenderWith(VideoEncoder.VideoToolbox, crf: 21);
            Assert.Equal(VideoEncoder.VideoToolbox, ran);
            Assert.Equal("h264_videotoolbox", name);
            Assert.Contains(log, line => line.Contains("VideoToolbox ", StringComparison.Ordinal));
        }

        /// <summary>Asking for a hardware encoder never fails the writer: where it does not open
        /// the render lands on x264 with a fallback line, where it does it runs. Every explicit
        /// choice is exercised, so this covers whichever hardware the machine lacks.</summary>
        [Theory]
        [InlineData(VideoEncoder.Nvenc)]
        [InlineData(VideoEncoder.Amf)]
        [InlineData(VideoEncoder.VideoToolbox)]
        public void An_unavailable_hardware_encoder_falls_back_to_x264(VideoEncoder requested)
        {
            RequireFFmpeg();
            bool available = H264EncoderProbe.CanOpen(requested, out _);
            var (log, ran, name) = RenderWith(requested);
            if (available)
            {
                Assert.Equal(requested, ran);
                Assert.DoesNotContain(log, line => line.Contains("falling back", StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal(VideoEncoder.Software, ran);
                Assert.Equal("libx264", name);
                var fallback = Assert.Single(log, line => line.Contains("falling back to libx264", StringComparison.Ordinal));
                Assert.Contains(H264EncoderSettings.CodecNameOf(requested), fallback, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Auto_lands_on_the_probed_encoder()
        {
            RequireFFmpeg();
            var (log, ran, _) = RenderWith(VideoEncoder.Auto);
            Assert.Equal(H264EncoderProbe.Resolve(VideoEncoder.Auto, null), ran);
            Assert.Contains(log, line => line.Contains("requested auto", StringComparison.Ordinal));
        }

        /// <summary>The recorder's rate, so an edit of a recording does not lose audio quality.</summary>
        [Fact]
        public void Audio_is_aac_at_192_kbps()
        {
            RequireFFmpeg();
            Assert.Equal(192_000, Mp4Writer.AudioBitRate);
            string path = TempMp4();
            try
            {
                using (var writer = new Mp4Writer(path, new Mp4WriterOptions
                {
                    Width = 64,
                    Height = 64,
                    FpsNum = 30,
                    Audio = new Mp4AudioOptions { SampleRate = 48000, Channels = 2 },
                }))
                {
                    SubmitSolidFrames(writer, 64, 64, 300);
                    SubmitSine(writer, 48000, 48000 * 10);
                    writer.Finish();
                }
                // the aac encoder's declared rate survives into the container's codec parameters
                Assert.InRange(ProbeAudioBitRate(path), 150_000, 230_000);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static unsafe long ProbeAudioBitRate(string path)
        {
            AVFormatContext* fmt = null;
            int err = ffmpeg.avformat_open_input(&fmt, path, null, null);
            if (err < 0)
                throw new InvalidOperationException($"open failed: {FFmpegLoader.ErrorToString(err)}");
            try
            {
                ffmpeg.avformat_find_stream_info(fmt, null);
                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    var par = fmt->streams[i]->codecpar;
                    if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                        return par->bit_rate;
                }
                return 0;
            }
            finally
            {
                ffmpeg.avformat_close_input(&fmt);
            }
        }
    }
}
