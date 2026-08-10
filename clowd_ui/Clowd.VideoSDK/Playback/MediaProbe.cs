using System;
using System.Collections.Generic;
using System.IO;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>
    /// Cheap open/inspect of a media file (no decoding): duration, video stream dimensions and
    /// frame rates, audio presence. Used by the editor and by auto-open logic.
    /// </summary>
    public static unsafe class MediaProbe
    {
        public static MediaInfo Probe(string path)
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

                return BuildInfo(path, fmt);
            }
            finally
            {
                ffmpeg.avformat_close_input(&fmt);
            }
        }

        internal static MediaInfo BuildInfo(string path, AVFormatContext* fmt)
        {
            var videoStreams = new List<VideoStreamInfo>();
            bool hasAudio = false;

            for (int i = 0; i < fmt->nb_streams; i++)
            {
                var st = fmt->streams[i];
                var par = st->codecpar;
                if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    // attached pictures (cover art) masquerade as video streams; skip them.
                    if ((st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                        continue;

                    double fps = 0;
                    if (st->avg_frame_rate.den != 0)
                        fps = ffmpeg.av_q2d(st->avg_frame_rate);
                    if (fps <= 0 && st->r_frame_rate.den != 0)
                        fps = ffmpeg.av_q2d(st->r_frame_rate);

                    var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                    videoStreams.Add(new VideoStreamInfo
                    {
                        StreamIndex = i,
                        Width = par->width,
                        Height = par->height,
                        Fps = fps,
                        CodecName = codec != null
                            ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)codec->name)
                            : par->codec_id.ToString(),
                    });
                }
                else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    hasAudio = true;
                }
            }

            var duration = fmt->duration != ffmpeg.AV_NOPTS_VALUE
                ? TimeSpan.FromSeconds(fmt->duration / (double)ffmpeg.AV_TIME_BASE)
                : TimeSpan.Zero;

            return new MediaInfo
            {
                Path = path,
                Duration = duration,
                VideoStreams = videoStreams,
                HasAudio = hasAudio,
            };
        }
    }
}
