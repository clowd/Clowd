using System;
using System.Collections.Generic;

namespace Clowd.VideoSDK.Model;

/// <summary>One media file feeding the composition, with the probed shape of each of its streams.
/// Items reference it by <see cref="Id"/> (<see cref="MediaContent.SourceId"/>) rather than by
/// path so the file can be relinked without touching the timeline.</summary>
public sealed class Source
{
    public Guid Id { get; set; }

    /// <summary>Full path to the media file.</summary>
    public string Path { get; set; }

    /// <summary>Full path to the recording's input-capture JSONL sidecar (cursor positions, key
    /// and mouse events), or null when the recording carries none. A missing/corrupt file
    /// degrades to no data — it never blocks the project.</summary>
    public string InputCapturePath { get; set; }

    public List<SourceStream> Streams { get; set; } = new List<SourceStream>();
}

public enum StreamKind
{
    Video,
    Audio,
}

/// <summary>The probed shape of one stream inside a <see cref="Source"/>. Width/height and the
/// frame-rate fields are zero for audio streams. The average frame rate is a rational, and
/// <see cref="IsVariableFrameRate"/> is the probe's hint (<c>avg_frame_rate</c> ≠
/// <c>r_frame_rate</c>) that per-frame PTS must be trusted over any nominal rate.</summary>
public sealed class SourceStream
{
    /// <summary>The stream's index within the container (the screen recording is 0).</summary>
    public int Index { get; set; }

    public StreamKind Kind { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int AvgFrameRateNum { get; set; }

    public int AvgFrameRateDen { get; set; }

    public bool IsVariableFrameRate { get; set; }

    /// <summary>The container's start_time for this stream, in 100ns ticks. Decoders normalize it
    /// away at open time; it is recorded here so source timestamps stay interpretable.</summary>
    public long StartTimeTicks { get; set; }

    public long DurationTicks { get; set; }
}
