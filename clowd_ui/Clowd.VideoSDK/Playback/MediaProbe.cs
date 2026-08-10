using System;
using System.Collections.Generic;
using System.IO;
using Clowd.VideoSDK.Media;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>
    /// Per video stream timing facts, in the exact form the composition model wants them: rational
    /// frame rates and 100ns ticks, never a reduced <see cref="double"/>.
    /// </summary>
    public sealed class VideoStreamProbe
    {
        public int StreamIndex { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string CodecName { get; init; }

        /// <summary>avg_frame_rate — total frames / duration. 0/0 when the container gives no hint.</summary>
        public int AvgFrameRateNum { get; init; }
        public int AvgFrameRateDen { get; init; }

        /// <summary>r_frame_rate — the lowest rate all timestamps are representable in (FFmpeg's
        /// guess at the "real" base rate). 0/0 when unknown.</summary>
        public int RFrameRateNum { get; init; }
        public int RFrameRateDen { get; init; }

        /// <summary>
        /// Hint only: avg_frame_rate != r_frame_rate. False when either rate is unknown — absence of
        /// evidence, so callers must stay VFR-correct regardless (frame lookup is by time, not index).
        /// </summary>
        public bool IsVariableFrameRate { get; init; }

        /// <summary>Stream start_time rescaled to ticks; 0 when AV_NOPTS_VALUE. Can be negative.</summary>
        public long StartTimeTicks { get; init; }

        /// <summary>nb_frames; 0 when the container does not know (streamed/fragmented files).</summary>
        public long NbFrames { get; init; }

        /// <summary>Stream duration in ticks, falling back to the container duration when the stream
        /// carries none; 0 when neither is known.</summary>
        public long DurationTicks { get; init; }
    }

    /// <summary>Per audio stream facts. The composition model needs the stream <b>index</b> (to
    /// point a <c>MediaContent</c> at it) and the source sample rate (the output rate an import
    /// defaults to), neither of which the boolean <c>HasAudio</c> can carry.</summary>
    public sealed class AudioStreamProbe
    {
        public int StreamIndex { get; init; }

        public int SampleRate { get; init; }

        public int Channels { get; init; }

        /// <summary>Stream duration in ticks, falling back to the container duration; 0 when
        /// neither is known.</summary>
        public long DurationTicks { get; init; }
    }

    /// <summary>
    /// The full probe result. <see cref="MediaInfo"/> is the (lossy, double-fps) legacy view of this
    /// for the existing player surface; new code should consume this type.
    /// </summary>
    public sealed class MediaProbeResult
    {
        public string Path { get; init; }

        /// <summary>Container duration in ticks; 0 when AV_NOPTS_VALUE.</summary>
        public long DurationTicks { get; init; }

        public IReadOnlyList<VideoStreamProbe> VideoStreams { get; init; }

        /// <summary>Every audio stream, in container order. Empty when the file has none.</summary>
        public IReadOnlyList<AudioStreamProbe> AudioStreams { get; init; } = Array.Empty<AudioStreamProbe>();

        public bool HasAudio { get; init; }

        /// <summary>Projects onto the legacy <see cref="MediaInfo"/> shape (frame rate collapsed to
        /// a double) for callers that predate the rational model.</summary>
        public MediaInfo ToMediaInfo()
        {
            var streams = new List<VideoStreamInfo>(VideoStreams.Count);
            foreach (var s in VideoStreams)
            {
                // legacy semantics: avg_frame_rate, falling back to r_frame_rate.
                double fps = 0;
                if (s.AvgFrameRateDen != 0)
                    fps = s.AvgFrameRateNum / (double)s.AvgFrameRateDen;
                if (fps <= 0 && s.RFrameRateDen != 0)
                    fps = s.RFrameRateNum / (double)s.RFrameRateDen;

                streams.Add(new VideoStreamInfo
                {
                    StreamIndex = s.StreamIndex,
                    Width = s.Width,
                    Height = s.Height,
                    Fps = fps,
                    CodecName = s.CodecName,
                });
            }

            return new MediaInfo
            {
                Path = Path,
                Duration = TimeSpan.FromTicks(DurationTicks),
                VideoStreams = streams,
                HasAudio = HasAudio,
            };
        }
    }

    /// <summary>
    /// Cheap open/inspect of a media file (no decoding): duration, video stream dimensions and
    /// frame rates, audio presence. Used by the editor and by auto-open logic.
    /// </summary>
    public static unsafe class MediaProbe
    {
        public static MediaInfo Probe(string path) => ProbeDetailed(path).ToMediaInfo();

        /// <summary>Probe returning rational frame rates, the VFR hint, start_time and nb_frames.</summary>
        public static MediaProbeResult ProbeDetailed(string path)
        {
            FFmpegLoader.EnsureInitialized();
            if (!File.Exists(path))
                throw new FileNotFoundException("Media file not found.", path);

            AVFormatContext* fmt = null;
            int err = ffmpeg.avformat_open_input(&fmt, path, null, null);
            if (err < 0)
                throw new InvalidOperationException($"Failed to open '{path}': {FFmpegLoader.ErrorToString(err)}");

            try
            {
                err = ffmpeg.avformat_find_stream_info(fmt, null);
                if (err < 0)
                    throw new InvalidOperationException($"Failed to read stream info: {FFmpegLoader.ErrorToString(err)}");

                return BuildProbe(path, fmt);
            }
            finally
            {
                ffmpeg.avformat_close_input(&fmt);
            }
        }

        /// <summary>
        /// Reads every packet timestamp of one video stream (demux only — nothing is decoded) and
        /// returns them in presentation order as 100ns ticks, normalized by the stream's
        /// start_time the same way the decode path is (<c>SyncStreamDecoder</c> subtracts it from
        /// frame pts, so these values match frame-lookup ticks exactly). Used by the v1 compat
        /// path to build a VFR render's frame schedule. Packets without a pts fall back to dts;
        /// packets with neither are skipped.
        /// </summary>
        public static long[] ReadVideoPacketPtsTicks(string path, int streamIndex)
        {
            FFmpegLoader.EnsureInitialized();
            if (!File.Exists(path))
                throw new FileNotFoundException("Media file not found.", path);

            AVFormatContext* fmt = null;
            int err = ffmpeg.avformat_open_input(&fmt, path, null, null);
            if (err < 0)
                throw new InvalidOperationException($"Failed to open '{path}': {FFmpegLoader.ErrorToString(err)}");

            AVPacket* pkt = null;
            try
            {
                err = ffmpeg.avformat_find_stream_info(fmt, null);
                if (err < 0)
                    throw new InvalidOperationException($"Failed to read stream info: {FFmpegLoader.ErrorToString(err)}");

                if (streamIndex < 0 || streamIndex >= fmt->nb_streams)
                    throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex,
                        $"The file has {fmt->nb_streams} streams.");

                var st = fmt->streams[streamIndex];
                var tb = st->time_base;
                if (tb.num <= 0 || tb.den <= 0)
                    throw new InvalidOperationException($"Stream {streamIndex} has no usable time base.");

                long startTimeTicks = st->start_time != ffmpeg.AV_NOPTS_VALUE
                    ? TimeBase.StreamTimeToTicks(st->start_time, tb.num, tb.den)
                    : 0;

                pkt = ffmpeg.av_packet_alloc();
                if (pkt == null)
                    throw new InvalidOperationException("Could not allocate a packet.");

                var ticks = new List<long>();
                while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
                {
                    if (pkt->stream_index == streamIndex)
                    {
                        long ts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts
                            : pkt->dts != ffmpeg.AV_NOPTS_VALUE ? pkt->dts
                            : long.MinValue;
                        if (ts != long.MinValue)
                            ticks.Add(TimeBase.StreamTimeToTicks(ts, tb.num, tb.den) - startTimeTicks);
                    }
                    ffmpeg.av_packet_unref(pkt);
                }

                // packets arrive in decode (dts) order; presentation order is what a schedule needs
                ticks.Sort();
                return ticks.ToArray();
            }
            finally
            {
                if (pkt != null)
                    ffmpeg.av_packet_free(&pkt);
                ffmpeg.avformat_close_input(&fmt);
            }
        }

        internal static MediaInfo BuildInfo(string path, AVFormatContext* fmt) => BuildProbe(path, fmt).ToMediaInfo();

        internal static MediaProbeResult BuildProbe(string path, AVFormatContext* fmt)
        {
            var videoStreams = new List<VideoStreamProbe>();
            var audioStreams = new List<AudioStreamProbe>();

            long containerDurationTicks = fmt->duration != ffmpeg.AV_NOPTS_VALUE
                ? TimeBase.Rescale(fmt->duration, 1, ffmpeg.AV_TIME_BASE, 1, TimeBase.TicksPerSecond)
                : 0;

            for (int i = 0; i < fmt->nb_streams; i++)
            {
                var st = fmt->streams[i];
                var par = st->codecpar;
                if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    // attached pictures (cover art) masquerade as video streams; skip them.
                    if ((st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                        continue;

                    var avg = st->avg_frame_rate;
                    var r = st->r_frame_rate;
                    bool avgValid = avg.num > 0 && avg.den > 0;
                    bool rValid = r.num > 0 && r.den > 0;

                    // rational inequality by cross-multiply — no av_q2d, no double comparison.
                    bool vfr = avgValid && rValid && (long)avg.num * r.den != (long)r.num * avg.den;

                    var tb = st->time_base;
                    bool tbValid = tb.num > 0 && tb.den > 0;

                    long startTicks = tbValid && st->start_time != ffmpeg.AV_NOPTS_VALUE
                        ? TimeBase.StreamTimeToTicks(st->start_time, tb.num, tb.den)
                        : 0;

                    long durationTicks = tbValid && st->duration != ffmpeg.AV_NOPTS_VALUE && st->duration > 0
                        ? TimeBase.StreamTimeToTicks(st->duration, tb.num, tb.den)
                        : containerDurationTicks;

                    var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                    videoStreams.Add(new VideoStreamProbe
                    {
                        StreamIndex = i,
                        Width = par->width,
                        Height = par->height,
                        CodecName = codec != null
                            ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)codec->name)
                            : par->codec_id.ToString(),
                        AvgFrameRateNum = avgValid ? avg.num : 0,
                        AvgFrameRateDen = avgValid ? avg.den : 0,
                        RFrameRateNum = rValid ? r.num : 0,
                        RFrameRateDen = rValid ? r.den : 0,
                        IsVariableFrameRate = vfr,
                        StartTimeTicks = startTicks,
                        NbFrames = st->nb_frames > 0 ? st->nb_frames : 0,
                        DurationTicks = durationTicks,
                    });
                }
                else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    var tb = st->time_base;
                    bool tbValid = tb.num > 0 && tb.den > 0;

                    audioStreams.Add(new AudioStreamProbe
                    {
                        StreamIndex = i,
                        SampleRate = par->sample_rate,
                        Channels = par->ch_layout.nb_channels,
                        DurationTicks = tbValid && st->duration != ffmpeg.AV_NOPTS_VALUE && st->duration > 0
                            ? TimeBase.StreamTimeToTicks(st->duration, tb.num, tb.den)
                            : containerDurationTicks,
                    });
                }
            }

            return new MediaProbeResult
            {
                Path = path,
                DurationTicks = containerDurationTicks,
                VideoStreams = videoStreams,
                AudioStreams = audioStreams,
                HasAudio = audioStreams.Count > 0,
            };
        }
    }
}
