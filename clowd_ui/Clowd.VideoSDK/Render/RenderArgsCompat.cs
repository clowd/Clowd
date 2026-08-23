using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using SkiaSharp;

namespace Clowd.VideoSDK.Render
{
    /// <summary>
    /// Reads the <b>version 1</b> render-args file the old Rust <c>vid-render</c> tool took as its
    /// single argument (keep-segment list in source milliseconds, an optional webcam overlay with a
    /// pixel rect and a grayscale mask PNG, an x264 crf) and turns it into a v2
    /// <see cref="Project"/>, so <c>Clowd.VideoRender</c> is a drop-in replacement for the binary
    /// the UI spawns today.
    ///
    /// <para><b>The mapping</b> (work-order step 8, table in the design doc):
    /// <list type="bullet">
    /// <item>keep segments → one <see cref="Item"/> per segment per stream, placed back to back on
    /// the output timeline; each item's <see cref="MediaContent.SourceInTicks"/> is the segment's
    /// source start, which is exactly what the old <c>trim,setpts=PTS-STARTPTS,concat</c> chain
    /// produced.</item>
    /// <item>three tracks — screen video (order 0), webcam video (order 1, so it composites on top),
    /// audio — all items sharing one <see cref="Item.LinkGroupId"/>: the sync toggle for the rows
    /// that came from a single recording.</item>
    /// <item>webcam rect → <see cref="Transform"/>: the rect is in screen-frame pixels, and the
    /// model's geometry is normalized against the canvas (which <i>is</i> the screen frame), so
    /// <c>X/Y</c> is the rect center over the frame size and <c>Scale</c> is <c>rect.w / frameW</c>.
    /// The rect's height is redundant under the model — height follows the camera's own aspect
    /// ratio, which is precisely how the v1 <c>WebcamOverlay</c> (normalized center + width only)
    /// defined it before the UI expanded it into pixels.</item>
    /// <item>mask PNG → <see cref="Mask"/>: v1 carries a rasterized grayscale PNG rather than the
    /// shape that produced it, so the shape and corner radius are recovered from the image (see
    /// <see cref="InferMask"/>).</item>
    /// </list>
    /// </para>
    ///
    /// <para>Parsing and structural validation (<see cref="Parse"/>), filesystem checks
    /// (<see cref="CheckFiles"/>) and the model mapping (<see cref="Build"/>) are separate for the
    /// same reason the Rust tool separated them: every rule is testable without touching disk or
    /// the FFmpeg natives. Error messages are kept close to the originals — they are surfaced to
    /// the user through the runner's <c>error &lt;message&gt;</c> line.</para>
    /// </summary>
    public static class RenderArgsCompat
    {
        /// <summary>The only args version this shim understands.</summary>
        public const int LegacyVersion = 1;

        /// <summary>vid-render's <c>DEFAULT_CRF</c>, used when the file carries no <c>crf</c>.</summary>
        public const int DefaultCrf = 21;

        /// <summary>Frame rate used when the input declares none at all (neither avg_frame_rate nor
        /// r_frame_rate). Output is CFR, so a rate must be picked; 30/1 matches the recorder's
        /// default.</summary>
        public const int FallbackFpsNum = RecordingProject.FallbackFpsNum;

        /// <summary>Output sample rate used when the input has no audio stream to take one from.
        /// Only reached for a video-only render, where no audio stream is written at all.</summary>
        public const int FallbackSampleRate = RecordingProject.FallbackSampleRate;

        private const long TicksPerMs = TimeBase.TicksPerSecond / 1000;

        // ------------------------------------------------------------------------ public entry

        /// <summary>
        /// Loads a v1 args file: parse + validate + filesystem checks + probe the input, and build
        /// the project. Throws <see cref="InvalidOperationException"/> with a single-line message
        /// for every contract violation.
        /// </summary>
        public static LegacyRenderPlan Load(string argsPath)
        {
            if (String.IsNullOrWhiteSpace(argsPath))
                throw new ArgumentException("Args file path is empty.", nameof(argsPath));

            string text;
            try
            {
                text = File.ReadAllText(argsPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"could not read args file: {argsPath}: {ex.Message}", ex);
            }

            var args = Parse(text);
            CheckFiles(args);
            var probe = MediaProbe.ProbeDetailed(args.Input);

            // VFR inputs render as a frame-for-frame passthrough of the kept source frames, the
            // way vid-render's trim/concat graph did — a CFR resample would change both the frame
            // count and every frame's instant. The schedule needs the real packet timestamps,
            // which the probe alone cannot provide.
            var videoStreams = probe.VideoStreams;
            IReadOnlyList<long> screenPts = null;
            if (videoStreams is { Count: > 0 } && videoStreams[0].IsVariableFrameRate)
                screenPts = MediaProbe.ReadVideoPacketPtsTicks(args.Input, videoStreams[0].StreamIndex);

            return Build(args, probe, screenPts);
        }

        // ------------------------------------------------------------------------------- parse

        /// <summary>Parses and structurally validates the JSON — no filesystem access, no FFmpeg.</summary>
        internal static LegacyArgs Parse(string json)
        {
            LegacyArgs raw;
            try
            {
                raw = JsonSerializer.Deserialize(json, LegacyRenderArgsJsonContext.Default.LegacyArgs);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("invalid args JSON: " + ex.Message, ex);
            }

            if (raw == null)
                throw new InvalidOperationException("invalid args JSON: the file is empty");
            if (raw.Version != LegacyVersion)
                throw new InvalidOperationException($"unsupported args version {raw.Version} (expected {LegacyVersion})");
            if (String.IsNullOrEmpty(raw.Input))
                throw new InvalidOperationException("input path is empty");
            if (String.IsNullOrEmpty(raw.Output))
                throw new InvalidOperationException("output path is empty");
            if (String.Equals(raw.Input, raw.Output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"output path equals input path ({raw.Input})");

            var segments = raw.Segments ?? new List<LegacySegment>();
            for (var i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s == null)
                    throw new InvalidOperationException($"segment {i}: is null");
                if (s.StartMs < 0)
                    throw new InvalidOperationException($"segment {i}: start_ms {s.StartMs} is negative");
                if (s.EndMs <= s.StartMs)
                    throw new InvalidOperationException(
                        $"segment {i}: end_ms {s.EndMs} must be greater than start_ms {s.StartMs}");
                if (i > 0 && s.StartMs < segments[i - 1].EndMs)
                    throw new InvalidOperationException(
                        $"segment {i}: start_ms {s.StartMs} overlaps or precedes the previous segment " +
                        $"ending at {segments[i - 1].EndMs}");
            }
            raw.Segments = segments;

            var cam = raw.Webcam;
            if (cam != null)
            {
                var r = cam.Rect;
                if (r == null)
                    throw new InvalidOperationException("webcam has no rect");
                if (r.W <= 0 || r.H <= 0)
                    throw new InvalidOperationException($"webcam rect has non-positive size {r.W}x{r.H}");
                if (r.X < 0 || r.Y < 0)
                    throw new InvalidOperationException($"webcam rect origin ({r.X}, {r.Y}) is negative");
                if (cam.StreamIndex < 0)
                    throw new InvalidOperationException($"webcam stream_index {cam.StreamIndex} is negative");
            }

            if (raw.Crf is < 0 or > 51)
                throw new InvalidOperationException($"crf {raw.Crf} out of range (0-51)");

            return raw;
        }

        /// <summary>Filesystem-facing validation, separated from <see cref="Parse"/> for
        /// testability: the input and the mask must exist, and the output directory is created.</summary>
        internal static void CheckFiles(LegacyArgs args)
        {
            ArgumentNullException.ThrowIfNull(args);

            if (!File.Exists(args.Input))
                throw new InvalidOperationException($"input file not found: {args.Input}");

            var mask = args.Webcam?.MaskPng;
            if (!String.IsNullOrEmpty(mask) && !File.Exists(mask))
                throw new InvalidOperationException($"mask file not found: {mask}");

            var parent = Path.GetDirectoryName(Path.GetFullPath(args.Output));
            if (!String.IsNullOrEmpty(parent))
            {
                try
                {
                    Directory.CreateDirectory(parent);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"could not create {parent}: {ex.Message}", ex);
                }
            }
        }

        // ------------------------------------------------------------------------------- build

        /// <summary>
        /// The pure mapping: validated v1 args plus the probed input become a renderable project.
        /// Takes the probe result rather than a path so the whole mapping is unit-testable without
        /// the FFmpeg natives. <paramref name="screenFramePtsTicks"/> is the screen stream's
        /// sorted packet timestamps (ticks) — required for a VFR source, where the render follows
        /// the source frames rather than a CFR grid; null (CFR source) renders on the grid.
        /// </summary>
        internal static LegacyRenderPlan Build(LegacyArgs args, MediaProbeResult probe,
            IReadOnlyList<long> screenFramePtsTicks = null)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(probe);

            var videoStreams = probe.VideoStreams ?? Array.Empty<VideoStreamProbe>();
            if (videoStreams.Count == 0)
                throw new InvalidOperationException("input has no video stream");

            // "Screen" is the first video stream — the same "track 0" rule vid-render used.
            var screen = videoStreams[0];
            if (screen.Width <= 0 || screen.Height <= 0)
                throw new InvalidOperationException(
                    $"screen stream {screen.StreamIndex} has no usable size ({screen.Width}x{screen.Height})");

            VideoStreamProbe cam = null;
            if (args.Webcam != null)
                cam = ResolveWebcamStream(args.Webcam, videoStreams, screen);

            // First audio stream, if any — vid-render took the first decodable one.
            var audio = probe.AudioStreams is { Count: > 0 } ? probe.AudioStreams[0] : null;

            var (fpsNum, fpsDen) = RecordingProject.ChooseFrameRate(screen);

            long sourceDurationTicks = MaxDuration(probe, screen);
            bool wholeFile = args.Segments == null || args.Segments.Count == 0;
            if (wholeFile && !screen.IsVariableFrameRate)
            {
                // vid-render passed every source frame through, so a whole-file CFR render must
                // emit exactly the source's frame count. The probed duration is only
                // microsecond-precise (a 244-frame 60 fps file probes as 4.066667 s — 4 ticks past
                // the 244-frame boundary), and covering it with grid frames would append a
                // spurious duplicate final frame. Snap it to the frame grid.
                sourceDurationTicks = SnapToFrameGrid(sourceDurationTicks, screen, fpsNum, fpsDen);
            }

            var segments = BuildSegments(args.Segments, sourceDurationTicks);
            if (segments.Count == 0)
                throw new InvalidOperationException("the edit keeps nothing of the recording");

            // The row/item layout is shared with the editor's own import (one recording => three
            // rows over one source, one link group); only the v1-specific geometry — the pixel
            // rect and the rasterized mask — is recovered here.
            var keep = new List<KeepSegment>(segments.Count);
            foreach (var (startTicks, durationTicks) in segments)
                keep.Add(new KeepSegment(startTicks, durationTicks));

            var project = RecordingProject.Build(new RecordingProjectSpec
            {
                InputPath = args.Input,
                Screen = screen,
                Webcam = cam,
                // v1 knew exactly one audio row, so a v1 file must keep producing exactly one.
                AudioStreams = audio != null ? new[] { audio } : Array.Empty<AudioStreamProbe>(),
                FpsNum = fpsNum,
                FpsDen = fpsDen,
                Segments = keep,
                WebcamTransform = cam != null ? BuildWebcamTransform(args.Webcam, screen) : null,
            });

            var problems = project.Validate();
            if (problems.Count > 0)
                throw new InvalidOperationException(
                    "the v1 args do not describe a renderable project: " + String.Join(" ", problems));

            // VFR: render exactly the kept source frames on their own (rebased) timestamps, the
            // frame-for-frame behavior of vid-render's trim/setpts/concat graph.
            IReadOnlyList<long> schedule = null;
            if (screenFramePtsTicks != null)
            {
                schedule = BuildFrameSchedule(screenFramePtsTicks, segments);
                if (schedule.Count == 0)
                    throw new InvalidOperationException("the edit keeps no video frames of the recording");
            }

            return new LegacyRenderPlan
            {
                Project = project,
                InputPath = args.Input,
                OutputPath = args.Output,
                Crf = args.Crf ?? DefaultCrf,
                MaskPngPath = args.Webcam?.MaskPng,
                FrameTimestampsTicks = schedule,
                LegacyContainerTiming = true,
            };
        }

        /// <summary>Geometry checks that need the probed input — the same ones vid-render deferred
        /// to <c>Input::open</c>.</summary>
        private static VideoStreamProbe ResolveWebcamStream(LegacyWebcam webcam,
            IReadOnlyList<VideoStreamProbe> videoStreams, VideoStreamProbe screen)
        {
            if (webcam.StreamIndex == screen.StreamIndex)
                throw new InvalidOperationException(
                    $"webcam stream_index {webcam.StreamIndex} is the screen track itself; " +
                    "the input has no separate webcam track");

            VideoStreamProbe cam = null;
            foreach (var s in videoStreams)
            {
                if (s.StreamIndex == webcam.StreamIndex)
                {
                    cam = s;
                    break;
                }
            }

            if (cam == null)
                throw new InvalidOperationException(
                    $"webcam stream_index {webcam.StreamIndex} is not a video stream of the input");

            var r = webcam.Rect;
            if (r.X + r.W > screen.Width || r.Y + r.H > screen.Height)
                throw new InvalidOperationException(
                    $"webcam rect {r.W}x{r.H}+{r.X}+{r.Y} exceeds the {screen.Width}x{screen.Height} screen frame");

            return cam;
        }

        /// <summary>Normalizes the v1 pixel rect back into the canvas-relative geometry the model
        /// uses, and recovers the mask shape from the rasterized PNG.</summary>
        private static Transform BuildWebcamTransform(LegacyWebcam webcam, VideoStreamProbe screen)
        {
            // height follows the camera's aspect ratio, exactly as WebcamOverlay defined it —
            // the UI derived rect.h from that same ratio when it wrote these args.
            var r = webcam.Rect;
            return RecordingProject.WebcamTransform(r.X, r.Y, r.W, r.H, screen.Width, screen.Height,
                InferMask(webcam.MaskPng));
        }

        /// <summary>
        /// Recovers <see cref="Mask"/> from the grayscale overlay mask v1 shipped as a PNG (black =
        /// hidden, white = shown, antialiased edge). The model stores the shape, not a bitmap, and
        /// v1 stores only the bitmap — so the shape is measured back out of the image, from the one
        /// number that separates the two shapes it can be: the fraction of the rectangle the white
        /// area covers.
        /// <list type="bullet">
        /// <item>an inscribed ellipse covers exactly <c>π/4 ≈ 0.785</c> of its bounding box,
        /// whatever the aspect ratio;</item>
        /// <item>a rounded rect of radius <c>r</c> loses only its four corners:
        /// <c>1 − (4−π)·r² / (w·h)</c>, which is at its smallest — and still above π/4 — when
        /// <c>r</c> reaches half the shorter side. So the midpoint between the two is a clean
        /// threshold, and inverting the same formula gives <c>r</c> back:
        /// <c>r = sqrt((1 − coverage)·w·h / (4 − π))</c>, reported as the fraction of the
        /// <b>height</b> both models use for a corner radius.</item>
        /// </list>
        /// Where the two shapes converge (a rounded rect on a square, radius = half the side, i.e.
        /// a circle) the classification can go either way and it does not matter — the picture is
        /// identical. Anti-aliased edge pixels are counted at the halfway point, so they cancel out
        /// rather than bias the area. Returns null when there is no mask.
        /// </summary>
        internal static Mask InferMask(string maskPngPath)
        {
            if (String.IsNullOrEmpty(maskPngPath))
                return null;

            using var bitmap = SKBitmap.Decode(maskPngPath);
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                throw new InvalidOperationException($"could not decode the mask image: {maskPngPath}");

            int width = bitmap.Width, height = bitmap.Height;
            long white = CountWhitePixels(bitmap);
            double coverage = white / ((double)width * height);

            // the least a rounded rect can cover: corners at their maximum radius.
            double maxRadius = Math.Min(width, height) / 2.0;
            double roundedFloor = 1 - (4 - Math.PI) * maxRadius * maxRadius / ((double)width * height);
            const double ellipseCoverage = Math.PI / 4;

            if (coverage <= (ellipseCoverage + roundedFloor) / 2)
                return new Mask { Shape = MaskShape.Circle };

            double radius = Math.Sqrt(Math.Max(0, 1 - coverage) * width * height / (4 - Math.PI));
            return new Mask
            {
                Shape = MaskShape.RoundedRect,
                CornerRadius = Math.Clamp(radius / height, 0, 0.5),
            };
        }

        /// <summary>Counts pixels at or above mid gray. The mask is grayscale by construction
        /// (R=G=B, opaque), so any color channel is its luminance — green sits at byte 1 of both
        /// RGBA and BGRA, which is why the fast path reads that one.</summary>
        private static long CountWhitePixels(SKBitmap bitmap)
        {
            int width = bitmap.Width, height = bitmap.Height, rowBytes = bitmap.RowBytes;
            var pixels = bitmap.GetPixelSpan();
            bool direct = bitmap.BytesPerPixel == 4 && pixels.Length >= (long)rowBytes * height;

            long white = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte luminance = direct
                        ? pixels[y * rowBytes + x * 4 + 1]
                        : bitmap.GetPixel(x, y).Green;

                    if (luminance >= 128)
                        white++;
                }
            }

            return white;
        }

        // ----------------------------------------------------------------------------- helpers

        /// <summary>The keep list as (source-in ticks, duration ticks) pairs. An empty v1 segment
        /// list means "keep the whole file" (vid-render's passthrough graph); segments are clamped
        /// to the probed duration so a stale args file cannot ask for a freeze-frame tail.</summary>
        private static List<(long StartTicks, long DurationTicks)> BuildSegments(
            List<LegacySegment> segments, long sourceDurationTicks)
        {
            var result = new List<(long, long)>();

            if (segments == null || segments.Count == 0)
            {
                if (sourceDurationTicks > 0)
                    result.Add((0, sourceDurationTicks));
                return result;
            }

            foreach (var s in segments)
            {
                long start = s.StartMs * TicksPerMs;
                long end = s.EndMs * TicksPerMs;
                if (sourceDurationTicks > 0)
                {
                    start = Math.Min(start, sourceDurationTicks);
                    end = Math.Min(end, sourceDurationTicks);
                }

                if (end > start)
                    result.Add((start, end - start));
            }

            return result;
        }

        /// <summary>
        /// Snaps a probed whole-file duration onto the output frame grid so the frame count
        /// equals the source's. Prefers the container's own <c>nb_frames</c> when it agrees with
        /// the duration to within one frame (mp4s from our recorder always carry it); otherwise
        /// rounds the duration to the nearest whole frame count.
        /// </summary>
        private static long SnapToFrameGrid(long durationTicks, VideoStreamProbe screen,
            int fpsNum, int fpsDen)
        {
            if (durationTicks <= 0)
                return durationTicks;

            long oneFrame = TimeBase.FrameIndexToTicks(1, fpsNum, fpsDen);
            if (screen.NbFrames > 0)
            {
                long snapped = TimeBase.FrameIndexToTicks(screen.NbFrames, fpsNum, fpsDen);
                if (Math.Abs(snapped - durationTicks) <= oneFrame)
                    return snapped;
                // nb_frames wildly off the probed duration: fall through to rounding.
            }

            long frames = Math.Max(1, TimeBase.TicksToFrameIndex(durationTicks + oneFrame / 2, fpsNum, fpsDen));
            return TimeBase.FrameIndexToTicks(frames, fpsNum, fpsDen);
        }

        /// <summary>
        /// The output frame schedule for a VFR source: every source frame timestamp falling inside
        /// a kept segment (<c>start &lt;= pts &lt; end</c>, trim's bounds), rebased onto the output
        /// timeline the same way the items are (segment source start maps to the segment's
        /// timeline start). <paramref name="sourcePtsTicks"/> must be sorted ascending.
        /// </summary>
        internal static long[] BuildFrameSchedule(IReadOnlyList<long> sourcePtsTicks,
            IReadOnlyList<(long StartTicks, long DurationTicks)> segments)
        {
            ArgumentNullException.ThrowIfNull(sourcePtsTicks);
            ArgumentNullException.ThrowIfNull(segments);

            var result = new List<long>();
            long timelineStart = 0;
            foreach (var (startTicks, durationTicks) in segments)
            {
                long endTicks = startTicks + durationTicks;
                foreach (var pts in sourcePtsTicks)
                {
                    if (pts < startTicks)
                        continue;
                    if (pts >= endTicks)
                        break;
                    result.Add(timelineStart + (pts - startTicks));
                }
                timelineStart += durationTicks;
            }

            return result.ToArray();
        }

        private static long MaxDuration(MediaProbeResult probe, VideoStreamProbe screen)
        {
            long duration = Math.Max(probe.DurationTicks, screen.DurationTicks);
            if (probe.AudioStreams != null)
            {
                foreach (var a in probe.AudioStreams)
                    duration = Math.Max(duration, a.DurationTicks);
            }
            return duration;
        }

    }

    /// <summary>What a v1 args file describes once mapped: the project to render, where it goes,
    /// and the encoder quality it asked for.</summary>
    public sealed class LegacyRenderPlan
    {
        public Project Project { get; init; }

        public string InputPath { get; init; }

        public string OutputPath { get; init; }

        /// <summary>x264 constant rate factor from the args (or vid-render's default).</summary>
        public int Crf { get; init; }

        /// <summary>The mask PNG the shape was recovered from, for diagnostics only.</summary>
        public string MaskPngPath { get; init; }

        /// <summary>For VFR inputs: the exact output-frame instants (ticks) — one per kept source
        /// frame, vid-render's passthrough timing. Null renders the normal CFR grid.</summary>
        public IReadOnlyList<long> FrameTimestampsTicks { get; init; }

        /// <summary>True for v1 plans: mux with vid-render's container timing
        /// (<see cref="Media.Mp4WriterOptions.LegacyContainerTiming"/>). v2 project renders leave
        /// it false and write true sample durations.</summary>
        public bool LegacyContainerTiming { get; init; }
    }

    // -------------------------------------------------------------------------------- v1 DTOs
    // Deliberately duplicated from Clowd.Shared's RenderArgs rather than referenced: the SDK does
    // not depend on Clowd.Shared (and must not — Clowd.Shared is the UI's world). These mirror the
    // JSON keys, which are the actual contract; the C# names are free.

    internal sealed class LegacyArgs
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("input")]
        public string Input { get; set; }

        [JsonPropertyName("output")]
        public string Output { get; set; }

        [JsonPropertyName("segments")]
        public List<LegacySegment> Segments { get; set; }

        [JsonPropertyName("webcam")]
        public LegacyWebcam Webcam { get; set; }

        /// <summary>Nullable so an absent crf takes vid-render's default rather than 0 (lossless).</summary>
        [JsonPropertyName("crf")]
        public int? Crf { get; set; }
    }

    internal sealed class LegacySegment
    {
        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; set; }
    }

    internal sealed class LegacyWebcam
    {
        [JsonPropertyName("stream_index")]
        public int StreamIndex { get; set; }

        [JsonPropertyName("rect")]
        public LegacyRect Rect { get; set; }

        [JsonPropertyName("mask_png")]
        public string MaskPng { get; set; }
    }

    internal sealed class LegacyRect
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

    /// <summary>Source-generated reader for the v1 args file (same pattern as the v2
    /// <c>ProjectJsonContext</c>).</summary>
    [JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
    [JsonSerializable(typeof(LegacyArgs))]
    internal partial class LegacyRenderArgsJsonContext : JsonSerializerContext { }
}
