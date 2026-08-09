using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clowd.Video
{
    /// <summary>
    /// The render-args file (version 1) handed to the external <c>vid-render</c> tool as its one
    /// and only argument. This is a wire contract with a Rust binary, so every member carries an
    /// explicit snake_case <see cref="JsonPropertyNameAttribute"/> — the C# names may be renamed
    /// freely, the JSON keys may not.
    ///
    /// <code>
    /// {"version":1,"input":"...","output":"...","segments":[{"start_ms":0,"end_ms":5000}],
    ///  "webcam":{"stream_index":1,"rect":{"x":0,"y":0,"w":320,"h":240},"mask_png":"..."},"crf":21}
    /// </code>
    ///
    /// <see cref="Segments"/> is the keep list from
    /// <see cref="VideoEditDocument.GetKeepSegments"/> — the tool concatenates them in order.
    /// <see cref="Webcam"/> is omitted entirely when the overlay is disabled (the tool treats a
    /// missing and a null <c>webcam</c> identically).
    /// </summary>
    public sealed class RenderArgs
    {
        /// <summary>Contract version; only 1 exists. Written on every file, checked by the tool.</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        public const int CurrentVersion = 1;

        /// <summary>Full path to the source recording.</summary>
        [JsonPropertyName("input")]
        public string Input { get; set; }

        /// <summary>Full path the tool writes; it must not exist yet.</summary>
        [JsonPropertyName("output")]
        public string Output { get; set; }

        /// <summary>Keep segments in source-timeline order; concatenated back to back.</summary>
        [JsonPropertyName("segments")]
        public List<RenderSegment> Segments { get; set; } = new List<RenderSegment>();

        /// <summary>The webcam overlay, or null/absent when there is none.</summary>
        [JsonPropertyName("webcam")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RenderWebcam Webcam { get; set; }

        /// <summary>Encoder quality for the re-encode (x264 CRF / hardware CQP), same scale as the
        /// recorder's <c>--crf</c>.</summary>
        [JsonPropertyName("crf")]
        public int Crf { get; set; }

        /// <summary>Serializes with the source-generated context (indented — the file is small and
        /// gets read by humans when a render goes wrong).</summary>
        public string ToJson() => JsonSerializer.Serialize(this, RenderArgsJsonContext.Default.RenderArgs);

        /// <summary>Round-trip counterpart of <see cref="ToJson"/>. Throws
        /// <see cref="JsonException"/> on malformed input.</summary>
        public static RenderArgs FromJson(string json) =>
            JsonSerializer.Deserialize(json, RenderArgsJsonContext.Default.RenderArgs);

        /// <summary>Convenience for the keep list: <see cref="CutRegion"/> is the shared span type,
        /// <see cref="RenderSegment"/> is its wire form.</summary>
        public static List<RenderSegment> ToSegments(IEnumerable<CutRegion> segments)
        {
            var list = new List<RenderSegment>();
            if (segments == null)
                return list;

            foreach (var s in segments)
                list.Add(new RenderSegment { StartMs = s.StartMs, EndMs = s.EndMs });

            return list;
        }
    }

    /// <summary>One kept span of the source, half-open <c>[start_ms, end_ms)</c>.</summary>
    public sealed class RenderSegment
    {
        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; set; }
    }

    /// <summary>The webcam overlay: which video stream of the input carries the camera, where it
    /// lands in the output frame, and the mask applied to it.</summary>
    public sealed class RenderWebcam
    {
        /// <summary>Index of the webcam video stream within the input file (the screen is 0).</summary>
        [JsonPropertyName("stream_index")]
        public int StreamIndex { get; set; }

        /// <summary>Destination rectangle in output-frame pixels.</summary>
        [JsonPropertyName("rect")]
        public RenderRect Rect { get; set; }

        /// <summary>Full path to a greyscale PNG exactly <c>rect.w</c> x <c>rect.h</c>, white =
        /// opaque. Null/absent means an unmasked rectangle.</summary>
        [JsonPropertyName("mask_png")]
        public string MaskPng { get; set; }
    }

    /// <summary>An integer pixel rectangle in the output frame.</summary>
    public sealed class RenderRect
    {
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("w")]
        public int W { get; set; }

        [JsonPropertyName("h")]
        public int H { get; set; }
    }
}
