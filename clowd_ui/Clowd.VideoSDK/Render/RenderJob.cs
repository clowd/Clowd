using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using Clowd.VideoSDK.Ai;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;

namespace Clowd.VideoSDK.Render
{
    /// <summary>Settings for one <see cref="RenderJob"/> run.</summary>
    public sealed class RenderJobOptions
    {
        /// <summary>Constant rate factor, 0-51, on x264's scale (default matches vid-render's
        /// contract); every encoder maps it onto its own (<see cref="H264EncoderSettings"/>).</summary>
        public int Crf { get; init; } = Mp4WriterOptions.DefaultCrf;

        /// <summary>Which H.264 encoder writes the video: <see cref="VideoEncoder.Auto"/> by
        /// default — the first of NVENC, AMF, VideoToolbox that opens on this machine, else
        /// x264 — the render's counterpart of the recorder's hardware-acceleration setting.
        /// A hardware encoder that fails at the output size falls back to x264 with a
        /// <see cref="DiagnosticLog"/> line rather than failing the render;
        /// <see cref="RenderResult.Encoder"/> reports what ran.</summary>
        public VideoEncoder Encoder { get; init; } = VideoEncoder.Auto;

        /// <summary>Try the GPU surface backend first, falling back to CPU when headless context
        /// creation fails (RDP, VMs, CI). False composes on CPU unconditionally.</summary>
        public bool PreferGpu { get; init; } = true;

        /// <summary>
        /// Encode-time cap on the output height in pixels, 0 (the default) for none: the project
        /// still composes at its own canvas size and the encoder is opened at
        /// <see cref="CapSize"/> of it — aspect preserved, both dimensions rounded down to even
        /// (4:2:0 chroma), never upscaled — with the convert stage scaling BGRA → NV12 into that
        /// size. This is the render dialog's "Size" row (Full / 1080p / 720p): a smaller file from
        /// the same project, without touching the project. Negative values are rejected.
        /// A cap always renders through the readback stages: the zero-copy bridge hands the
        /// composed texture to the encoder at the canvas size, so it cannot serve a capped
        /// render (one <see cref="DiagnosticLog"/> line says so).
        /// </summary>
        public int MaxHeight { get; init; }

        /// <summary>
        /// On Windows, with the Direct3D 12 composer and a hardware encoder (NVENC, AMF), hand
        /// the composed frames to the encoder in video memory (<see cref="D3D11EncodeBridge"/>)
        /// instead of reading them back and converting on the CPU. Every part of that path is
        /// checked at startup and any refusal — no shareable ring, no Direct3D 11.4, the
        /// encoder not opening over GPU frames — logs one line and uses the readback path, so
        /// this is never a reason for a render to fail. False forces the readback path
        /// (<c>Clowd.VideoRender</c> maps <c>CLOWD_RENDER_ZEROCOPY=0</c> to it for A/B
        /// benchmarking); <see cref="RenderResult.ZeroCopy"/> reports which ran.
        /// </summary>
        public bool ZeroCopyEncode { get; init; } = true;

        /// <summary>Receives backend selection and render diagnostics (the SDK has no logging
        /// dependency). Called from the render and composer threads.</summary>
        public Action<string> DiagnosticLog { get; init; }

        /// <summary>
        /// Optional explicit output-frame schedule (100ns ticks in <b>output time</b>, strictly
        /// increasing): one output frame is composed and encoded at each instant, instead of the
        /// uniform <c>FpsNum/FpsDen</c> grid. Instants map through the project's speed warp like
        /// the grid does (a warp-free project composes them verbatim). This is how the v1 compat path reproduces vid-render's VFR
        /// passthrough — vid-render re-encoded every kept source frame on its own source
        /// timestamp, while the v2 model renders CFR. Null renders the normal CFR grid.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<long> FrameTimestampsTicks { get; init; }

        /// <summary>Passes <see cref="Mp4WriterOptions.LegacyContainerTiming"/> through — v1
        /// compat renders must mux byte-compatibly with vid-render.</summary>
        public bool LegacyContainerTiming { get; init; }

        /// <summary>Directory holding the project's AI sidecar files (the one with
        /// <c>videoedit.json</c> — see <see cref="Clowd.VideoSDK.Ai.AiSidecars"/>): where the
        /// audio mix reads denoise sidecars for tracks with <see cref="Model.Track.Denoise"/>
        /// on and the frame source reads matte sidecars for items with a segmented
        /// <see cref="Model.VideoEffect"/>. Sidecars that are needed but missing/stale are
        /// generated here before the render loop when a <c>clowd_ai</c> binary resolves
        /// (see <see cref="Clowd.VideoSDK.Ai.AiLoader"/>). Null renders every stream raw
        /// and the segmented effects degrade to plain draws.</summary>
        public string SidecarCacheDir { get; init; }
    }

    public enum RenderOutcome
    {
        Completed,
        Canceled,
    }

    /// <summary>What a render produced. On cancellation the partial output file has been
    /// deleted (matching vid-render's semantics) and <see cref="OutputBytes"/> is 0.</summary>
    public sealed class RenderResult
    {
        public RenderOutcome Outcome { get; init; }

        public string OutputPath { get; init; }

        /// <summary>Size of the finished file in bytes (0 when canceled).</summary>
        public long OutputBytes { get; init; }

        /// <summary>The surface backend the frames were composed on ("CPU", "Direct3D 12",
        /// "Metal") — reported so render diagnostics always say which path ran.</summary>
        public string SurfaceBackend { get; init; }

        /// <summary>Video frames actually encoded (the full count when completed).</summary>
        public long VideoFrames { get; init; }

        /// <summary>The H.264 encoder that wrote the video (never <see cref="VideoEncoder.Auto"/>):
        /// what <see cref="RenderJobOptions.Encoder"/> resolved to, or
        /// <see cref="VideoEncoder.Software"/> after a hardware fallback.</summary>
        public VideoEncoder Encoder { get; init; }

        /// <summary>The FFmpeg name of <see cref="Encoder"/> (<c>libx264</c>, <c>h264_nvenc</c>, …).</summary>
        public string EncoderName { get; init; }

        /// <summary>True when the encoder read the composed frames from video memory (the
        /// zero-copy path, see <see cref="RenderJobOptions.ZeroCopyEncode"/>); false when they
        /// went through the readback and convert stages.</summary>
        public bool ZeroCopy { get; init; }
    }

    /// <summary>
    /// Renders a <see cref="Project"/> to an mp4 — the work-order's render loop: for each output
    /// frame <c>n</c>, <c>FrameComposer.Compose</c> at the project instant the speed warp maps
    /// <c>TimeBase.FrameIndexToTicks(n)</c> to (<see cref="TimeWarp.ToProject"/>; the identity
    /// map when there are no speed items) into a readback-ring slot on the
    /// <see cref="ComposerThread"/>, read the pixels back, convert them to NV12, and hand them
    /// to <see cref="Mp4Writer"/>, which encodes with whichever H.264 encoder
    /// <see cref="RenderJobOptions.Encoder"/> selects — or, on the zero-copy path, hand the
    /// GPU-converted frames over without any readback at all.
    ///
    /// <para>
    /// The loop is three stages that overlap, connected by bounded queues so frames stay in
    /// order and memory stays fixed:
    /// <list type="number">
    /// <item><b>compose</b> (the composer thread): takes a free slot of the
    /// <see cref="IReadbackRing"/>, composes frame <c>n</c> into it and submits its readback —
    /// on Direct3D 12 a GPU-side copy that this thread never waits for.</item>
    /// <item><b>convert</b> (its own thread): waits for the slot's pixels to land, converts
    /// BGRA → NV12 with a slice-threaded <see cref="Nv12Converter"/> into a pooled
    /// <see cref="Nv12Frame"/> — the one input format every encoder takes — and returns the
    /// slot to the composer. On the zero-copy path (Windows, Direct3D 12 composer, NVENC or
    /// AMF, <see cref="RenderJobOptions.ZeroCopyEncode"/>) the same stage instead has the
    /// <see cref="D3D11EncodeBridge"/> convert the slot on the GPU into an NV12 texture the
    /// encoder reads directly, and returns the slot once the GPU has finished with it.</item>
    /// <item><b>encode</b> (the caller's thread): drives the audio mix up to the frame's end,
    /// submits the frame to the writer, returns the yuv frame to the pool (or drops its
    /// reference to the GPU frame; the encoder keeps its own until it has read it).</item>
    /// </list>
    /// Before this the composer did compose + a synchronous <c>ReadPixels</c> per frame and the
    /// caller did convert + encode, two frames in flight: at 1240x1166 the synchronous readback
    /// alone was 2 ms per frame (7.8 s of a 15.9 s render), the ceiling. Measured after, on the
    /// same 3863-frame job (RTX 4070, i7-14700K) with x264 preset fast: 6.9 s at 1x, where the
    /// ceiling is now the composer's own synchronous decode + upload + draw (5.5 s), and 16.9 s
    /// at 2x (was 57 s), where it is the encoder (14.1 s). NVENC p6 is engine-bound on the same
    /// job — 10.7 s at 1x, 28.6 s at 2x (2.3 / 6.9 ms per frame in the encoder) — so on a CPU
    /// this wide it is the slower choice; its value is the CPU it frees, and the zero-copy path
    /// is where that value is collected: same wall time (10.5 / 29.0 s, the engine is still the
    /// ceiling) for 11 / 14 CPU-seconds against 23 / 53 on the readback path and 75 / 312 for
    /// x264 — the readback DMA, the swscale threads and NVENC's own system-memory copy are all
    /// gone. The timing summary at the end of every render
    /// (<see cref="RenderJobOptions.DiagnosticLog"/>) shows which stage waited on which, and
    /// names the encoder that ran.
    /// </para>
    ///
    /// <para>
    /// Audio is mixed by <see cref="AudioMixer"/> in chunks driven up to each video frame's end
    /// before that frame is muxed, so <c>av_interleaved_write_frame</c> sees the two streams
    /// arrive together — the same pacing vid-render got from its demux-ordered graph pulls. An
    /// audio stream is written exactly when the project has audio-track media items (muted tracks
    /// render silence rather than changing the stream layout).
    /// </para>
    ///
    /// <para>
    /// Cancellation is polled between frames on the encode stage: the other stages are stopped,
    /// the partial output is deleted and a <see cref="RenderOutcome.Canceled"/> result returned
    /// (vid-render removed partial output on quit; errors from any stage also delete the partial
    /// file before propagating).
    /// </para>
    /// </summary>
    public static class RenderJob
    {
        /// <summary>
        /// Readback ring depth asked of an asynchronous ring. Three is the minimum for the stages
        /// to overlap without stalling — one slot being composed, one whose GPU copy is in
        /// flight, one being converted — and a fourth absorbs GPU scheduling jitter (the copy
        /// engine takes ~1 ms for a 23 MB 2x frame, compose ~1.5 ms, so a late copy would
        /// otherwise stall the composer). More buys nothing: the ceiling is a stage's own work
        /// (the encoder at 2x, the composer's decode and upload at 1x), never the ring, and every
        /// slot costs a render target in VRAM plus a readback buffer of the same size in system
        /// memory (6 MB each at 1x, 23 MB at 2x). The research harness measured 0.35 ms/frame at
        /// 1x with four slots. A synchronous ring (CPU backend, or a GPU whose driver refused the
        /// asynchronous one) has no copy in flight and comes back with fewer slots
        /// (<see cref="SyncReadbackRing.MaxUsefulSlots"/>); the loop sizes its queues from the
        /// ring it got.
        /// </summary>
        public const int ReadbackSlots = 4;

        /// <summary>NV12 frames circulating between the convert and encode stages. The
        /// encoder is the slowest stage, so this queue is normally full; a few frames of slack
        /// keep it fed across a mux write or an audio chunk without any stage waiting on
        /// another. 1.5 bytes per pixel, so the cost is negligible at any depth.</summary>
        private const int ConvertedFrames = 4;

        /// <summary>
        /// Renders <paramref name="project"/> to <paramref name="outputPath"/> synchronously.
        /// The project must validate cleanly (<see cref="Project.Validate"/>) and contain at
        /// least one item. Progress is 0..100.
        /// </summary>
        public static RenderResult Run(Project project, string outputPath,
            RenderJobOptions options = null, IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is empty.", nameof(outputPath));
            options ??= new RenderJobOptions();
            if (options.MaxHeight < 0)
                throw new ArgumentException($"MaxHeight {options.MaxHeight} is negative.", nameof(options));

            var problems = project.Validate();
            if (problems.Count > 0)
                throw new ArgumentException(
                    "Project is not renderable: " + string.Join(" ", problems), nameof(project));

            var output = project.Output;
            // Composition always runs at the canvas size; only the encoder (and the convert stage
            // feeding it) sees the cap, so every item's geometry stays exactly what the preview
            // showed and swscale does the one resample.
            var (encodeWidth, encodeHeight) = CapSize(output.WidthPx, output.HeightPx, options.MaxHeight);
            bool capped = encodeWidth != output.WidthPx || encodeHeight != output.HeightPx;
            long durationTicks = project.GetDurationTicks();
            if (durationTicks <= 0)
                throw new InvalidOperationException("The project has no items — nothing to render.");

            // The output runs on warped time: speed items compress/stretch the project onto the
            // encode grid. An identity warp maps every instant to itself exactly, so warp-free
            // projects render precisely as before.
            var warp = TimeWarp.Build(project);
            long outputDurationTicks = warp.OutputDurationTicks;

            // Explicit schedule (v1 VFR passthrough) renders one frame per instant; otherwise the
            // CFR grid: frames n with FrameIndexToTicks(n) < duration — the largest covered
            // index, plus one.
            var schedule = options.FrameTimestampsTicks;
            if (schedule != null)
            {
                if (schedule.Count == 0)
                    throw new ArgumentException("The frame schedule is empty.", nameof(options));
                for (int i = 1; i < schedule.Count; i++)
                {
                    if (schedule[i] <= schedule[i - 1])
                        throw new ArgumentException(
                            $"The frame schedule must be strictly increasing (index {i}).", nameof(options));
                }
            }

            long frameCount = schedule?.Count
                ?? TimeBase.TicksToFrameIndex(outputDurationTicks - 1, output.FpsNum, output.FpsDen) + 1;

            // AI sidecars first: the mix worker and the frame source read them at construction /
            // first use, and generation is real work that belongs inside the progress range.
            double progressBase;
            try
            {
                progressBase = GenerateSidecars(project, options, progress, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // nothing was written yet — no partial output to delete
                return new RenderResult { Outcome = RenderOutcome.Canceled, OutputPath = outputPath };
            }
            double renderShare = 100.0 - progressBase;

            bool hasAudio = AudioMixer.HasAudioItems(project);
            // The audio stream runs to the end of the last audio item, not to the video's end:
            // where the source audio track is shorter than the video (real recordings routinely
            // are, by a few hundredths of a second), vid-render's atrim/concat chain ended the
            // output audio there too rather than padding silence to the video duration.
            long audioEndOutputTicks = warp.ToOutput(Math.Min(durationTicks, AudioMixer.GetAudioEndTicks(project)));
            long totalAudioFrames = hasAudio ? AudioTime.SamplesCeil(audioEndOutputTicks, output.SampleRate) : 0;

            ComposerThread composer = null;
            var pool = new FrameBufferPool();
            FrameTextureCache cache = null;
            SequentialFrameSource frameSource = null;
            IReadbackRing ring = null;
            IEncodeBridge bridge = null; // the zero-copy path, when it came up
            Nv12Converter converter = null;
            var yuvFrames = new List<Nv12Frame>();
            SequentialAudioSource audioSource = null;
            DenoisedAudioSource denoisedSource = null;
            Mp4Writer writer = null;
            Thread convertThread = null;
            BlockingCollection<EncodeItem> converted = null;
            StageTimings timings = null;
            // trips when any stage fails or the caller cancels: every blocking hand-off between
            // the stages observes it, so all three unwind promptly and teardown can join them
            var pipeline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            bool canceled = false;
            bool finished = false; // writer.Finish() completed — the output is a real mp4
            bool zeroCopy = false;
            long encoded = 0, outputBytes = 0;
            string backend = null;
            VideoEncoder encoder = VideoEncoder.Software;
            string encoderName = null;

            try
            {
                try
                {
                    // The composer is the one serial stage (decode → upload → draw, context-
                    // affine) and shares the cores with x264's and swscale's worker pools, which
                    // oversubscribe the machine now that readback no longer throttles them. A
                    // notch of priority keeps it scheduled when they are all runnable. Measured
                    // back-to-back on an otherwise idle machine, three interleaved runs each of
                    // the 3863-frame job: at 1x (composer-bound) Normal 6.89 / 6.90 s (min /
                    // median, compose 5.63 s) → AboveNormal 6.51 / 6.52 s (compose 5.35 s), every
                    // AboveNormal run faster than every Normal one; at 2x (encoder-bound) 14.24 /
                    // 14.67 s → 14.25 / 14.80 s, inside run-to-run noise. The cost is one thread
                    // at base priority 9 (Normal class + AboveNormal) against the UI app's 8 —
                    // a single core's worth; the render process itself stays at the Normal
                    // priority class.
                    composer = ComposerThread.Start(options.PreferGpu, options.DiagnosticLog,
                        ThreadPriority.AboveNormal);
                    backend = composer.BackendName;
                    options.DiagnosticLog?.Invoke(
                        $"RenderJob: {frameCount} frames at {output.WidthPx}x{output.HeightPx} " +
                        $"{output.FpsNum}/{output.FpsDen} fps on {backend}" +
                        (capped ? $", encoded at {encodeWidth}x{encodeHeight} (max height {options.MaxHeight})" : "") +
                        (hasAudio ? $", audio {output.SampleRate} Hz" : ", no audio"));

                    // everything context-affine (cache, frame source, readback ring) lives on the
                    // composer thread
                    composer.Send(() =>
                    {
                        cache = new FrameTextureCache(composer.Factory);
                        frameSource = new SequentialFrameSource(project, cache, pool,
                            options.SidecarCacheDir);
                        ring = composer.Factory.CreateReadbackRing(output.WidthPx, output.HeightPx,
                            ReadbackSlots, options.DiagnosticLog);
                    });

                    // The zero-copy hand-off is worth trying only where every piece exists: a
                    // hardware encoder with a Direct3D 11 input, and a Direct3D 12 ring whose
                    // textures the driver let us share. The encoder is resolved here the same
                    // way the writer will (a cached probe), so both agree.
                    bridge = TryCreateBridge(composer, ring, options, capped);

                    WarpAudioResampler audioWarp = null;
                    float[] mixBuffer = null;
                    if (hasAudio)
                    {
                        audioSource = new SequentialAudioSource(project);
                        // rows with the AI-denoise flag read their sidecar wav instead of the
                        // raw stream — same decorator the preview mixes through, so the render
                        // is what the preview played
                        IAudioSource mixSource = audioSource;
                        if (DenoisedAudioSource.HasDenoise(project))
                            mixSource = denoisedSource = new DenoisedAudioSource(
                                audioSource, project, options.SidecarCacheDir);
                        audioWarp = new WarpAudioResampler(new AudioMixer(project, mixSource),
                            warp, output.SampleRate);
                        // one video frame's worth of audio is the largest per-iteration chunk
                        long perFrame = AudioTime.SamplesCeil(
                            TimeBase.FrameIndexToTicks(1, output.FpsNum, output.FpsDen), output.SampleRate);
                        mixBuffer = new float[(perFrame + 16) * AudioMixer.Channels];
                    }

                    writer = new Mp4Writer(outputPath, new Mp4WriterOptions
                    {
                        Width = encodeWidth,
                        Height = encodeHeight,
                        FpsNum = output.FpsNum,
                        FpsDen = output.FpsDen,
                        Crf = options.Crf,
                        Encoder = options.Encoder,
                        HardwareFrames = bridge?.Frames,
                        DiagnosticLog = options.DiagnosticLog,
                        // a schedule's instants are arbitrary, so pts go through in microseconds
                        UseMicrosecondTimeBase = schedule != null,
                        LegacyContainerTiming = options.LegacyContainerTiming,
                        Audio = hasAudio
                            ? new Mp4AudioOptions { SampleRate = output.SampleRate, Channels = AudioMixer.Channels }
                            : null,
                    });

                    // The writer may have declined the GPU frames (the encoder would not open
                    // over them and it went to system memory, with its own line saying why):
                    // then the bridge is idle weight and the readback stages run.
                    if (bridge != null && writer.HardwareFrames == null)
                    {
                        options.DiagnosticLog?.Invoke(
                            $"RenderJob: zero-copy encode unavailable (no encoder opened over Direct3D 11 frames; {writer.EncoderName} runs on system memory); using readback");
                        bridge.Dispose();
                        bridge = null;
                    }

                    if (bridge == null)
                    {
                        // swscale scales in the same pass that converts, so a capped render costs
                        // no extra copy — only the (cheaper, smaller) destination.
                        converter = new Nv12Converter(output.WidthPx, output.HeightPx, encodeWidth, encodeHeight);
                        for (int i = 0; i < ConvertedFrames; i++)
                            yuvFrames.Add(new Nv12Frame(encodeWidth, encodeHeight));
                        options.DiagnosticLog?.Invoke(
                            $"RenderJob: readback: {ring.Description}; convert: {converter.Threads} swscale threads, " +
                            $"{ConvertedFrames} frames" +
                            (capped ? $", scaling to {encodeWidth}x{encodeHeight}" : ""));
                    }
                    else
                    {
                        options.DiagnosticLog?.Invoke(
                            $"RenderJob: zero-copy: {bridge.Description}; ring: {ring.Description}");
                    }

                    // ------------------------------------------------------------ stage plumbing
                    // Single producer, single consumer, FIFO: frames reach the encoder in order.
                    // Capacities equal the resource counts, so the "return" adds never block.
                    encoder = writer.Encoder;
                    encoderName = writer.EncoderName;
                    zeroCopy = bridge != null;
                    var freeSlots = new BlockingCollection<int>(ring.SlotCount);
                    for (int i = 0; i < ring.SlotCount; i++)
                        freeSlots.Add(i);
                    var composed = new BlockingCollection<(long Frame, int Slot, ulong Fence)>(ring.SlotCount);
                    var freeFrames = new BlockingCollection<Nv12Frame>(ConvertedFrames);
                    foreach (var f in yuvFrames)
                        freeFrames.Add(f);
                    // On the zero-copy path the GPU frames themselves are the slack: the encoder
                    // holds what it holds and the pool grows to match, so the queue only needs
                    // to keep the encode thread fed.
                    converted = new BlockingCollection<EncodeItem>(ConvertedFrames);

                    ExceptionDispatchInfo stageError = null;
                    var errorSync = new object();
                    void Fail(Exception ex)
                    {
                        lock (errorSync)
                            stageError ??= ExceptionDispatchInfo.Capture(ex);
                        pipeline.Cancel();
                    }

                    timings = new StageTimings();
                    var token = pipeline.Token;

                    // -------------------------------------------------------------- compose stage
                    composer.Post(() =>
                    {
                        try
                        {
                            for (long n = 0; n < frameCount; n++)
                            {
                                long t0 = Stopwatch.GetTimestamp();
                                int slot = freeSlots.Take(token);
                                long t1 = Stopwatch.GetTimestamp();
                                // the frame's output-time instant, mapped to the project instant
                                // it shows (an identity warp passes both grids through exactly);
                                // the warp's rounding may land exactly on the half-open project
                                // end, so clamp to keep the unwarped grid's tTicks < duration
                                long tTicks = Math.Min(durationTicks - 1,
                                    warp.ToProject(schedule?[(int)n]
                                        ?? TimeBase.FrameIndexToTicks(n, output.FpsNum, output.FpsDen)));
                                var canvas = ring.Begin(slot);
                                FrameComposer.Compose(project, tTicks, frameSource, canvas,
                                    output.WidthPx, output.HeightPx);
                                long t2 = Stopwatch.GetTimestamp();
                                ulong fence = 0;
                                if (bridge != null)
                                    fence = bridge.SubmitFrame(slot); // the bridge's GPU consumes it
                                else
                                    ring.Submit(slot);
                                long t3 = Stopwatch.GetTimestamp();
                                timings.SlotWait += t1 - t0;
                                timings.Compose += t2 - t1;
                                timings.ReadbackSubmit += t3 - t2;
                                composed.Add((n, slot, fence), token);
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            // stopped by the encode stage (cancel) or another stage's failure
                        }
                        catch (Exception ex)
                        {
                            Fail(ex);
                        }
                        finally
                        {
                            composed.CompleteAdding();
                        }
                    });

                    // -------------------------------------------------------------- convert stage
                    convertThread = new Thread(() =>
                    {
                        try
                        {
                            while (true)
                            {
                                long t0 = Stopwatch.GetTimestamp();
                                if (!composed.TryTake(out var item, Timeout.Infinite, token))
                                    break; // the composer finished every frame
                                long t1 = Stopwatch.GetTimestamp();
                                if (bridge != null)
                                {
                                    // GPU-side: queue the wait-blit-signal for this slot, then wait
                                    // for that signal — the blit has read the slot — before the
                                    // composer may draw into it again. The encoder's hold on the
                                    // NV12 texture is the pool's business (EncoderTexturePool).
                                    var hw = bridge.Convert(item.Slot, item.Fence);
                                    long t2 = Stopwatch.GetTimestamp();
                                    long t3;
                                    try
                                    {
                                        bridge.ReleaseSlot(item.Slot);
                                        t3 = Stopwatch.GetTimestamp();
                                        freeSlots.Add(item.Slot);
                                        converted.Add(new EncodeItem(item.Frame, null, hw), token);
                                    }
                                    catch
                                    {
                                        // The frame is ours until the hand-off has succeeded: a
                                        // cancel while the queue is full (or a slot release that
                                        // fails) would otherwise strand it outside the queue, where
                                        // the teardown's drain cannot see it — and with it the pool
                                        // texture, the frames context and the D3D11 device it holds
                                        // references to (HardwareFrame has no finalizer).
                                        hw.Dispose();
                                        throw;
                                    }
                                    timings.ComposedWait += t1 - t0;
                                    timings.Convert += t2 - t1;
                                    timings.ReadbackWait += t3 - t2;
                                }
                                else
                                {
                                    var pixels = ring.Wait(item.Slot);
                                    long t2 = Stopwatch.GetTimestamp();
                                    var frame = freeFrames.Take(token);
                                    long t3 = Stopwatch.GetTimestamp();
                                    converter.Convert(pixels.Address, pixels.RowBytes, frame);
                                    long t4 = Stopwatch.GetTimestamp();
                                    // the slot's memory has been read: the composer may draw into it
                                    freeSlots.Add(item.Slot);
                                    converted.Add(new EncodeItem(item.Frame, frame, null), token);
                                    timings.ComposedWait += t1 - t0;
                                    timings.ReadbackWait += t2 - t1;
                                    timings.FrameWait += t3 - t2;
                                    timings.Convert += t4 - t3;
                                }
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                        }
                        catch (Exception ex)
                        {
                            Fail(ex);
                        }
                        finally
                        {
                            converted.CompleteAdding();
                        }
                    })
                    {
                        Name = "Clowd.VideoSDK Convert",
                        IsBackground = true,
                    };
                    convertThread.Start();

                    // --------------------------------------------------------------- encode stage
                    long audioPos = 0;
                    progress?.Report(progressBase);
                    try
                    {
                        while (encoded < frameCount)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                canceled = true;
                                break;
                            }

                            long t0 = Stopwatch.GetTimestamp();
                            if (!converted.TryTake(out var item, Timeout.Infinite, token))
                                break; // a stage stopped early: cancel or failure, sorted out below
                            long t1 = Stopwatch.GetTimestamp();

                            long t2, t3;
                            try
                            {
                                // drive audio up to this frame's end before muxing the frame, so the
                                // interleaver always has both streams' data for the span — pacing runs
                                // entirely in output time; the resampler maps back to project time.
                                if (audioWarp != null)
                                {
                                    // frame end = the next scheduled instant (schedule mode) or the next
                                    // grid instant; the last frame drives audio out to its full extent.
                                    long frameEndTicks = schedule != null
                                        ? (item.Frame + 1 < frameCount ? schedule[(int)(item.Frame + 1)] : audioEndOutputTicks)
                                        : TimeBase.FrameIndexToTicks(item.Frame + 1, output.FpsNum, output.FpsDen);
                                    long target = Math.Min(totalAudioFrames,
                                        AudioTime.SamplesFloor(frameEndTicks, output.SampleRate));
                                    audioPos = MixUpTo(audioWarp, writer, mixBuffer, audioPos, target);
                                }
                                t2 = Stopwatch.GetTimestamp();

                                // pts: frame index on the CFR grid, microseconds under a schedule
                                long pts = schedule != null
                                    ? TimeBase.TicksToStreamTime(schedule[(int)item.Frame], 1, 1_000_000)
                                    : item.Frame;
                                if (item.Hardware != null)
                                {
                                    writer.SubmitVideoFrame(item.Hardware, pts);
                                }
                                else
                                {
                                    writer.SubmitVideoFrame(item.Pixels, pts);
                                    freeFrames.Add(item.Pixels); // the encoder has copied it
                                }
                                t3 = Stopwatch.GetTimestamp();
                            }
                            finally
                            {
                                // Ours goes whether the encoder took the frame or not (a failed
                                // mix or send lands here too): it keeps its own reference for as
                                // long as it reads the texture, and nothing else would ever drop
                                // ours — the teardown drains only what is still in the queue.
                                item.Hardware?.Dispose();
                            }
                            timings.EncodeWait += t1 - t0;
                            timings.Audio += t2 - t1;
                            timings.Encode += t3 - t2;
                            encoded++;
                            progress?.Report(Math.Min(99.0, progressBase + encoded * renderShare / frameCount));
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // the hand-off was interrupted by a cancel or a stage failure
                    }

                    if (encoded < frameCount && !canceled)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            canceled = true;
                        }
                        else
                        {
                            ExceptionDispatchInfo error;
                            lock (errorSync)
                                error = stageError;
                            error?.Throw();
                            throw new InvalidOperationException(
                                $"The render pipeline stopped after {encoded} of {frameCount} frames.");
                        }
                    }

                    if (!canceled)
                    {
                        if (audioWarp != null && audioPos < totalAudioFrames)
                            MixUpTo(audioWarp, writer, mixBuffer, audioPos, totalAudioFrames);
                        long t0 = Stopwatch.GetTimestamp();
                        writer.Finish();
                        timings.Finish = Stopwatch.GetTimestamp() - t0;
                        finished = true;
                        outputBytes = new FileInfo(outputPath).Length;
                        progress?.Report(100);
                    }
                }
                finally
                {
                    timings?.Clock.Stop(); // the render is over; teardown is not part of its time

                    // Stop the stages before anything they use goes away: the convert thread reads
                    // ring slots and yuv frames, the compose loop draws into the ring. Cancelling
                    // is a no-op after a completed run; otherwise it unblocks every hand-off.
                    pipeline.Cancel();
                    convertThread?.Join();

                    // GPU frames a cancelled or failed run left queued: drop our references so
                    // their textures are back in the pool before the pool goes.
                    if (converted != null)
                    {
                        while (converted.TryTake(out var leftover))
                            leftover.Hardware?.Dispose();
                    }

                    // Every path that reaches Dispose without Finish() (cancellation, any error)
                    // deletes the partial output below — abandon the writer so Dispose skips the
                    // +faststart trailer, which would otherwise re-read and rewrite the entire
                    // partial mdat (tens of seconds on a long render) just before the delete.
                    // The writer goes before the bridge and the ring: closing the encoder is
                    // what releases the GPU textures it still referenced.
                    if (writer != null && !finished)
                        writer.Abandon();
                    writer?.Dispose();
                    bridge?.Dispose(); // waits for its last GPU blit, then frees the NV12 pool

                    // The compose loop is one posted item, so the teardown Send queues behind it
                    // and Dispose joins: everything context-affine is released on-thread, in order.
                    if (composer != null)
                    {
                        try
                        {
                            composer.Send(() =>
                            {
                                ring?.Dispose();
                                frameSource?.Dispose();
                                cache?.Dispose();
                            });
                        }
                        catch (Exception ex)
                        {
                            options.DiagnosticLog?.Invoke("RenderJob: composer teardown failed: " + ex);
                        }
                        composer.Dispose();
                    }

                    converter?.Dispose();
                    foreach (var frame in yuvFrames)
                        frame.Dispose();
                    pool.Dispose();
                    denoisedSource?.Dispose();
                    audioSource?.Dispose();
                    pipeline.Dispose();

                    // The permanent per-render timing line the benchmarks read; the stage totals
                    // are complete only now that every stage has been joined.
                    if (timings != null)
                        options.DiagnosticLog?.Invoke(timings.Summary(encoded, backend, encoderName, zeroCopy));
                }
            }
            catch
            {
                TryDelete(outputPath);
                throw;
            }

            if (canceled)
                TryDelete(outputPath);

            return new RenderResult
            {
                Outcome = canceled ? RenderOutcome.Canceled : RenderOutcome.Completed,
                OutputPath = outputPath,
                OutputBytes = outputBytes,
                SurfaceBackend = backend,
                VideoFrames = encoded,
                Encoder = encoder,
                EncoderName = encoderName,
                ZeroCopy = zeroCopy,
            };
        }

        /// <summary>One converted frame on its way to the encode stage: a system-memory NV12
        /// frame from the pool, or a GPU frame from the zero-copy bridge — never both.</summary>
        private readonly struct EncodeItem
        {
            public EncodeItem(long frame, Nv12Frame pixels, HardwareFrame hardware)
            {
                Frame = frame;
                Pixels = pixels;
                Hardware = hardware;
            }

            public long Frame { get; }

            public Nv12Frame Pixels { get; }

            public HardwareFrame Hardware { get; }
        }

        /// <summary>
        /// The size the encoder is opened at for a canvas of <paramref name="widthPx"/> x
        /// <paramref name="heightPx"/> under <paramref name="maxHeight"/>
        /// (<see cref="RenderJobOptions.MaxHeight"/>): the canvas itself when the cap is 0 or
        /// would not shrink it (a cap never upscales), otherwise the cap's height with the width
        /// that preserves the aspect ratio — both rounded down to even, which 4:2:0 chroma
        /// requires, and never below 2. The UI shows the result of this as the size a preset
        /// would produce, so the math lives here rather than in the caller.
        /// </summary>
        public static (int Width, int Height) CapSize(int widthPx, int heightPx, int maxHeight)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPx);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPx);
            ArgumentOutOfRangeException.ThrowIfNegative(maxHeight);

            if (maxHeight == 0 || maxHeight >= heightPx)
                return (widthPx, heightPx);

            int height = Math.Max(2, maxHeight & ~1);
            int width = Math.Max(2, (int)Math.Round(widthPx * (double)height / heightPx,
                MidpointRounding.AwayFromZero) & ~1);
            return (width, height);
        }

        /// <summary>
        /// Brings up the zero-copy encode path when everything it needs is there — the option
        /// on, Windows, a shareable <see cref="D3D12ReadbackRing"/>, and an encoder with a
        /// Direct3D 11 input — or returns null, with one diagnostic line naming what was
        /// missing whenever a hardware encoder would have used it. Runs the bridge's creation
        /// (which draws a self-check frame through the ring) on the composer thread.
        /// <paramref name="capped"/> renders are readback renders by construction: the bridge
        /// blits the composed slot into an NV12 texture of the same size, so a size cap has to
        /// be applied by the convert stage instead (swscale scales as it converts).
        /// </summary>
        private static IEncodeBridge TryCreateBridge(ComposerThread composer, IReadbackRing ring,
            RenderJobOptions options, bool capped)
        {
            var log = options.DiagnosticLog;
            var encoder = H264EncoderProbe.Resolve(options.Encoder, log);
            if (encoder is not (VideoEncoder.Nvenc or VideoEncoder.Amf))
                return null; // x264 and VideoToolbox read system memory; nothing to bridge

            if (capped)
            {
                log?.Invoke($"RenderJob: zero-copy encode unavailable (the output is capped to {options.MaxHeight} rows, which the GPU blit does not scale to); using readback");
                return null;
            }

            if (!options.ZeroCopyEncode)
            {
                log?.Invoke("RenderJob: zero-copy encode disabled; using readback");
                return null;
            }

            if (!OperatingSystem.IsWindows() || ring is not D3D12ReadbackRing d3dRing)
            {
                log?.Invoke($"RenderJob: zero-copy encode unavailable (no Direct3D 12 readback ring: {ring.Description}); using readback");
                return null;
            }

            return CreateBridgeOnComposer(composer, d3dRing, log);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static IEncodeBridge CreateBridgeOnComposer(ComposerThread composer, D3D12ReadbackRing ring, Action<string> log)
        {
            D3D11EncodeBridge bridge = null;
            string reason = null;
            composer.Send(() => bridge = D3D11EncodeBridge.TryCreate(ring, out reason));
            if (bridge == null)
                log?.Invoke($"RenderJob: zero-copy encode unavailable ({reason}); using readback");
            return bridge;
        }

        /// <summary>Per-stage time totals of one render, in <see cref="Stopwatch"/> ticks. Each
        /// field is written by exactly one stage's thread and read after every stage has been
        /// joined, so no synchronization is needed. Waits are where a stage sat idle because of
        /// a neighbour — the stage whose waits are near zero is the ceiling.</summary>
        private sealed class StageTimings
        {
            // composer thread
            public long SlotWait, Compose, ReadbackSubmit;
            // convert thread
            public long ComposedWait, ReadbackWait, FrameWait, Convert;
            // encode (caller) thread
            public long EncodeWait, Audio, Encode, Finish;
            // started when the stages start; the summary reads it after every stage is joined
            public readonly Stopwatch Clock = Stopwatch.StartNew();

            /// <summary>The one-line summary. On the zero-copy path "readback submit" is the
            /// shared submit, "convert" the GPU blit's submission and "readback wait" the wait
            /// for that blit to release the slot; the fields keep their names so the line stays
            /// one format for the benchmarks.</summary>
            public string Summary(long frames, string backend, string encoder, bool zeroCopy)
            {
                long wallTicks = Clock.ElapsedTicks;
                double wall = Seconds(wallTicks);
                string fps = wall > 0 ? (frames / wall).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "-";
                return $"RenderJob: {frames} frames in {S(wallTicks)} s ({fps} fps) on {backend}; " +
                    $"composer: compose {S(Compose)} s, readback submit {S(ReadbackSubmit)} s, slot wait {S(SlotWait)} s; " +
                    $"convert: convert {S(Convert)} s, readback wait {S(ReadbackWait)} s, composed wait {S(ComposedWait)} s, frame wait {S(FrameWait)} s; " +
                    $"encoder: encode {S(Encode)} s, audio {S(Audio)} s, frame wait {S(EncodeWait)} s, finish {S(Finish)} s " +
                    $"({encoder ?? "no encoder"}{(zeroCopy ? ", zero-copy" : "")})";
            }

            private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

            private static string S(long ticks) =>
                Seconds(ticks).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Progress share reserved for sidecar generation when any generation runs —
        /// enough range that a minutes-long generation visibly moves, without dwarfing the
        /// render's own reporting.</summary>
        private const double SidecarProgressShare = 8.0;

        /// <summary>
        /// Brings the AI sidecars the render consumes up to date: every needed-but-missing/stale
        /// matte (items with a segmented <see cref="VideoEffect"/>) and denoise (tracks with
        /// <see cref="Track.Denoise"/>) sidecar is generated synchronously when a
        /// <c>clowd_ai</c> binary resolves. Without a binary — or a cache directory — the
        /// render proceeds and the effects degrade exactly as the preview does: plain Blur still
        /// applies, the segmented kinds draw plain, denoise plays raw. A stream too long for the
        /// sidecar wav format (denoise's <see cref="NotSupportedException"/>) degrades the same
        /// way rather than failing the render. Returns the progress consumed (0 when nothing
        /// ran); generation failures and cancellation propagate.
        /// </summary>
        private static double GenerateSidecars(Project project, RenderJobOptions options,
            IProgress<double> progress, CancellationToken cancellationToken)
        {
            var dir = options.SidecarCacheDir;
            if (string.IsNullOrEmpty(dir))
                return 0;

            var jobs = new List<(Source Source, int StreamIndex, bool Matte)>();
            foreach (var key in MatteGenerator.CollectMatteStreams(project))
            {
                var source = FindSource(project, key.SourceId);
                if (source != null
                    && !AiSidecars.IsValid(AiSidecars.MattePath(dir, key.SourceId, key.StreamIndex), source.Path))
                    jobs.Add((source, key.StreamIndex, true));
            }

            foreach (var (key, strength) in DenoisedAudioSource.CollectDenoisedStreams(project))
            {
                if (!(strength > 0))
                    continue;
                var source = FindSource(project, key.SourceId);
                if (source != null
                    && !AiSidecars.IsValid(AiSidecars.DenoisePath(dir, key.SourceId, key.StreamIndex), source.Path))
                    jobs.Add((source, key.StreamIndex, false));
            }

            if (jobs.Count == 0)
                return 0;

            if (AiLoader.TryGetPath() == null)
            {
                options.DiagnosticLog?.Invoke(
                    $"RenderJob: {jobs.Count} AI sidecar(s) needed but no clowd_ai binary resolves — rendering without them.");
                return 0;
            }

            for (int i = 0; i < jobs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (source, streamIndex, matte) = jobs[i];
                options.DiagnosticLog?.Invoke(
                    $"RenderJob: generating {(matte ? "matte" : "denoise")} sidecar for {source.Id}:{streamIndex}");
                var sub = progress == null ? null : new ScaledProgress(progress,
                    i * SidecarProgressShare / jobs.Count, SidecarProgressShare / jobs.Count);
                if (matte)
                {
                    MatteGenerator.Generate(source, streamIndex, dir, sub, cancellationToken);
                }
                else
                {
                    try
                    {
                        DenoiseGenerator.Generate(source, streamIndex, dir, sub, cancellationToken);
                    }
                    catch (NotSupportedException ex)
                    {
                        options.DiagnosticLog?.Invoke(
                            $"RenderJob: denoise sidecar for {source.Id}:{streamIndex} skipped — {ex.Message} Rendering with raw audio.");
                    }
                }
            }

            return SidecarProgressShare;
        }

        /// <summary>One generation's 0..1 progress mapped onto its slice of the job's 0..100.</summary>
        private sealed class ScaledProgress : IProgress<double>
        {
            private readonly IProgress<double> _inner;
            private readonly double _base;
            private readonly double _span;

            public ScaledProgress(IProgress<double> inner, double @base, double span)
            {
                _inner = inner;
                _base = @base;
                _span = span;
            }

            public void Report(double value) =>
                _inner.Report(_base + Math.Clamp(value, 0, 1) * _span);
        }

        private static Source FindSource(Project project, Guid sourceId)
        {
            foreach (var source in project.Sources ?? new List<Source>())
            {
                if (source.Id == sourceId)
                    return source;
            }

            return null;
        }

        /// <summary>Mixes and submits audio in encoder-friendly chunks until <paramref name="target"/>
        /// (absolute output sample frames); returns the new position.</summary>
        private static long MixUpTo(WarpAudioResampler audio, Mp4Writer writer, float[] buffer,
            long position, long target)
        {
            int capacity = buffer.Length / AudioMixer.Channels;
            while (position < target)
            {
                int chunk = (int)Math.Min(capacity, target - position);
                audio.ReadChunk(position, chunk, buffer);
                writer.SubmitAudioSamples(buffer, chunk);
                position += chunk;
            }
            return position;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // best effort — matching vid-render's `let _ = remove_file(...)`
            }
        }
    }
}
