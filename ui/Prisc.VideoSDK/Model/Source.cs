using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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

    /// <summary>Full path to the recording's window-capture JSONL sidecar (the live geometry of
    /// every on-screen window that intersected the capture region, region-relative and on the
    /// input-capture timebase), or null when the recording carries none. Degrades exactly like
    /// <see cref="InputCapturePath"/>: a missing or corrupt file is no data, never an error.</summary>
    public string WindowCapturePath { get; set; }

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

    /// <summary>Stored (coded) frame width in pixels — the size every decoded buffer has. Not
    /// necessarily the shape the picture is meant to be shown at: see <see cref="PixelAspectNum"/>.</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>
    /// The stream's pixel aspect ratio (FFmpeg's <c>sample_aspect_ratio</c>) as a rational,
    /// width over height of one stored pixel. Anamorphic and legacy SD material stores its frame
    /// at one size and declares that each pixel is wider or narrower than square, so the picture
    /// must be shown at <see cref="DisplayWidth"/>×<see cref="DisplayHeight"/> rather than
    /// <see cref="Width"/>×<see cref="Height"/>. 0/0 (a project written before the field existed,
    /// or a stream that declares nothing) means square pixels, exactly like 1/1. Clowd's own
    /// recordings are always square; only imported files carry anything else.
    /// </summary>
    public int PixelAspectNum { get; set; }

    public int PixelAspectDen { get; set; }

    /// <summary>The pixel aspect ratio as a factor: 1.0 for square pixels (and for an undeclared
    /// ratio), 0.75 for the classic 720×720-stored / 540×720-shown case.</summary>
    [JsonIgnore]
    public double PixelAspect => PixelAspectNum > 0 && PixelAspectDen > 0
        ? PixelAspectNum / (double)PixelAspectDen
        : 1.0;

    /// <summary>The width the picture is meant to be shown at, in square pixels:
    /// <see cref="Width"/> × <see cref="PixelAspect"/>. Equal to <see cref="Width"/> for every
    /// square-pixel stream. This, not <see cref="Width"/>, is what any aspect or placement math
    /// must read.</summary>
    [JsonIgnore]
    public double DisplayWidth => Width * PixelAspect;

    /// <summary>The height the picture is meant to be shown at — always <see cref="Height"/>,
    /// since the pixel aspect is applied on the horizontal axis; here so callers read a matched
    /// pair.</summary>
    [JsonIgnore]
    public double DisplayHeight => Height;

    public int AvgFrameRateNum { get; set; }

    public int AvgFrameRateDen { get; set; }

    public bool IsVariableFrameRate { get; set; }

    /// <summary>The container's start_time for this stream, in 100ns ticks. Decoders normalize it
    /// away at open time; it is recorded here so source timestamps stay interpretable.</summary>
    public long StartTimeTicks { get; set; }

    public long DurationTicks { get; set; }
}
