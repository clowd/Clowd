using System;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>Per-video-track interval statistics (interval = time between GetStatistics calls).</summary>
    public sealed class TrackStatistics
    {
        public bool IsHardware { get; init; }
        public int SourceWidth { get; init; }
        public int SourceHeight { get; init; }
        /// <summary>Average avcodec decode time per frame over the interval, in ms.</summary>
        public double DecodeMsPerFrame { get; init; }
        /// <summary>Average d3d11va GPU→CPU download time per frame, in ms (0 for software).</summary>
        public double TransferMsPerFrame { get; init; }
        /// <summary>Average sws_scale (to BGRA, at presented size) time per frame, in ms.</summary>
        public double ConvertMsPerFrame { get; init; }
        /// <summary>Frames actually handed to the sink over the interval / interval seconds.</summary>
        public double PresentedFps { get; init; }
        public long DecodedInInterval { get; init; }
        public long PresentedInInterval { get; init; }
        /// <summary>Cumulative frames dropped for being late.</summary>
        public long DroppedTotal { get; init; }
        public int FrameQueueDepth { get; init; }
    }

    public sealed class PlaybackStatistics
    {
        public TrackStatistics[] Video { get; init; } = Array.Empty<TrackStatistics>();
        /// <summary>Seconds of decoded audio waiting in the ring buffer.</summary>
        public double AudioBufferedSeconds { get; init; }
        public bool HasAudio { get; init; }
    }
}
