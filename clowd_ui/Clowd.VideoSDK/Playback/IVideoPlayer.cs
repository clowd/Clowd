using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Clowd.VideoSDK.Playback
{
    public enum PlayerState
    {
        /// <summary>No media open.</summary>
        Idle,
        /// <summary>OpenAsync in progress.</summary>
        Opening,
        /// <summary>Media open, not advancing. The first/current frame is presented.</summary>
        Paused,
        Playing,
        /// <summary>Reached end of media; Play() rewinds and restarts.</summary>
        Ended,
        /// <summary>Unrecoverable open/decode failure.</summary>
        Failed,
    }

    public enum SeekMode
    {
        /// <summary>Keyframe seek: present the first decodable frame immediately (scrub drag).</summary>
        Fast,
        /// <summary>Decode-forward from the previous keyframe and present the exact frame (release).</summary>
        Exact,
    }

    public sealed class VideoOpenOptions
    {
        /// <summary>Frames are converted to BGRA at no larger than this height (aspect preserved);
        /// scaling to the displayed size instead of the source size is the single biggest
        /// conversion-cost lever. 0 = never downscale.</summary>
        public int MaxPresentHeight { get; set; } = 1080;

        /// <summary>Try d3d11va hardware decoding first, with automatic software fallback.</summary>
        public bool EnableHardwareDecode { get; set; } = true;

        /// <summary>Engine mix rate; audio is converted to float stereo at this rate.
        /// <see cref="CompositionPlayer"/> mixes at the project's own output rate instead and
        /// falls back to this only when the project does not carry one.</summary>
        public int AudioSampleRate { get; set; } = 48000;

        /// <summary>Factory for the audio output device; null picks the platform default
        /// (WASAPI on Windows). Tests inject <see cref="Audio.SilentAudioOutput"/> here so
        /// playback runs on real timing without touching a device.</summary>
        public Func<Audio.IAudioOutput> CreateAudioOutput { get; set; }

        /// <summary>Directory holding the project's AI sidecar files (the one with
        /// <c>videoedit.json</c> — see <see cref="Ai.AiSidecars"/>): where the audio mix looks
        /// for denoise sidecars when a track's <see cref="Model.Track.Denoise"/> is on, and where
        /// <see cref="CompositionPlayer"/> looks for matte sidecars when an item's
        /// <see cref="Model.VideoEffect"/> needs one. Null (the dev harness) plays every stream
        /// raw and the segmented effects degrade to plain draws.</summary>
        public string SidecarCacheDir { get; set; }
    }

    public sealed class VideoStreamInfo
    {
        public int StreamIndex { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double Fps { get; init; }
        public string CodecName { get; init; }
    }

    public sealed class MediaInfo
    {
        public string Path { get; init; }
        public TimeSpan Duration { get; init; }
        public IReadOnlyList<VideoStreamInfo> VideoStreams { get; init; }
        public bool HasAudio { get; init; }
    }

    /// <summary>
    /// The swappable playback boundary. The editor UI talks only to this interface (plus
    /// <see cref="IFrameSink"/>); if the in-process FFmpeg engine ever fails the performance
    /// gate, an mpv-backed implementation replaces it with no changes above this line.
    /// </summary>
    public interface IVideoPlayer : IDisposable
    {
        Task<MediaInfo> OpenAsync(string path, VideoOpenOptions options);

        MediaInfo Info { get; }
        PlayerState State { get; }
        TimeSpan Position { get; }
        double Volume { get; set; }

        /// <summary>Sink for the primary (screen) video track. Set before OpenAsync.</summary>
        IFrameSink ScreenSink { get; set; }

        /// <summary>Sink for the optional second (webcam) video track.</summary>
        IFrameSink WebcamSink { get; set; }

        /// <summary>Ranges that playback skips over (cut preview). On entering a range the engine
        /// performs an internal exact seek to the range end; a ~50-100ms hiccup is acceptable.</summary>
        void SetSkipRanges(IReadOnlyList<TimeRange> ranges);

        void Play();
        void Pause();

        /// <summary>Seek; concurrent calls are coalesced (last position wins).</summary>
        Task SeekAsync(TimeSpan position, SeekMode mode);

        /// <summary>Step one frame forward (+1) or backward (-1) while paused.</summary>
        Task StepFrameAsync(int direction);

        /// <summary>Raised on the dispatcher passed at construction (UI thread).</summary>
        event EventHandler PositionChanged;

        /// <summary>Raised on the dispatcher passed at construction (UI thread).</summary>
        event EventHandler<PlayerState> StateChanged;

        /// <summary>Interval statistics (per-track decode/present metrics); resets interval
        /// counters on read — poll at a fixed cadence.</summary>
        PlaybackStatistics GetStatistics();
    }
}
