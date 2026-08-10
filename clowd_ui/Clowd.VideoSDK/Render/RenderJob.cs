using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using SkiaSharp;

namespace Clowd.VideoSDK.Render
{
    /// <summary>Settings for one <see cref="RenderJob"/> run.</summary>
    public sealed class RenderJobOptions
    {
        /// <summary>x264 constant rate factor, 0-51 (default matches vid-render's contract).</summary>
        public int Crf { get; init; } = Mp4WriterOptions.DefaultCrf;

        /// <summary>Try the GPU surface backend first, falling back to CPU when headless context
        /// creation fails (RDP, VMs, CI). False composes on CPU unconditionally.</summary>
        public bool PreferGpu { get; init; } = true;

        /// <summary>Receives backend selection and render diagnostics (the SDK has no logging
        /// dependency). Called from the render and composer threads.</summary>
        public Action<string> DiagnosticLog { get; init; }

        /// <summary>
        /// Optional explicit output-frame schedule (100ns ticks, strictly increasing): one output
        /// frame is composed and encoded at each instant, instead of the uniform
        /// <c>FpsNum/FpsDen</c> grid. This is how the v1 compat path reproduces vid-render's VFR
        /// passthrough — vid-render re-encoded every kept source frame on its own source
        /// timestamp, while the v2 model renders CFR. Null renders the normal CFR grid.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<long> FrameTimestampsTicks { get; init; }

        /// <summary>Passes <see cref="Mp4WriterOptions.LegacyContainerTiming"/> through — v1
        /// compat renders must mux byte-compatibly with vid-render.</summary>
        public bool LegacyContainerTiming { get; init; }
    }

    public enum RenderOutcome
    {
        Completed,
        Cancelled,
    }

    /// <summary>What a render produced. On cancellation the partial output file has been
    /// deleted (matching vid-render's semantics) and <see cref="OutputBytes"/> is 0.</summary>
    public sealed class RenderResult
    {
        public RenderOutcome Outcome { get; init; }

        public string OutputPath { get; init; }

        /// <summary>Size of the finished file in bytes (0 when cancelled).</summary>
        public long OutputBytes { get; init; }

        /// <summary>The surface backend the frames were composed on ("CPU", "Direct3D 12",
        /// "Metal") — reported so render diagnostics always say which path ran.</summary>
        public string SurfaceBackend { get; init; }

        /// <summary>Video frames actually encoded (the full count when completed).</summary>
        public long VideoFrames { get; init; }
    }

    /// <summary>
    /// Renders a <see cref="Project"/> to an mp4 — the work-order's render loop: for each output
    /// frame <c>n</c>, <c>FrameComposer.Compose</c> at <c>TimeBase.FrameIndexToTicks(n)</c> into a
    /// factory surface on the <see cref="ComposerThread"/>, read the pixels back to a BGRA staging
    /// buffer, and hand them to <see cref="Mp4Writer"/>. Two surfaces and two staging buffers are
    /// in flight, so frame N+1 composes on the composer thread while frame N encodes on the
    /// caller's thread — the readback double-buffering seam the surface factory was designed
    /// around, kept deliberately simple (a bounded post-ahead of one frame).
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
    /// Cancellation is polled between frames: the partial output is deleted and a
    /// <see cref="RenderOutcome.Cancelled"/> result returned (vid-render removed partial output
    /// on quit; errors also delete the partial file before propagating).
    /// </para>
    /// </summary>
    public static class RenderJob
    {
        private const int InFlight = 2; // surfaces + staging buffers pipelined

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

            var problems = project.Validate();
            if (problems.Count > 0)
                throw new ArgumentException(
                    "Project is not renderable: " + string.Join(" ", problems), nameof(project));

            var output = project.Output;
            long durationTicks = project.GetDurationTicks();
            if (durationTicks <= 0)
                throw new InvalidOperationException("The project has no items — nothing to render.");

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
                ?? TimeBase.TicksToFrameIndex(durationTicks - 1, output.FpsNum, output.FpsDen) + 1;
            bool hasAudio = AudioMixer.HasAudioItems(project);
            // The audio stream runs to the end of the last audio item, not to the video's end:
            // where the source audio track is shorter than the video (real recordings routinely
            // are, by a few hundredths of a second), vid-render's atrim/concat chain ended the
            // output audio there too rather than padding silence to the video duration.
            long audioEndTicks = Math.Min(durationTicks, AudioMixer.GetAudioEndTicks(project));
            long totalAudioFrames = hasAudio ? AudioTime.SamplesCeil(audioEndTicks, output.SampleRate) : 0;

            ComposerThread composer = null;
            var pool = new FrameBufferPool();
            FrameTextureCache cache = null;
            SequentialFrameSource frameSource = null;
            var surfaces = new SKSurface[InFlight];
            var staging = new FrameBuffer[InFlight];
            SequentialAudioSource audioSource = null;
            Mp4Writer writer = null;

            bool cancelled = false;
            long encoded = 0, outputBytes = 0;
            string backend = null;

            try
            {
                try
                {
                    composer = ComposerThread.Start(options.PreferGpu, options.DiagnosticLog);
                    backend = composer.BackendName;
                    options.DiagnosticLog?.Invoke(
                        $"RenderJob: {frameCount} frames at {output.WidthPx}x{output.HeightPx} " +
                        $"{output.FpsNum}/{output.FpsDen} fps on {backend}" +
                        (hasAudio ? $", audio {output.SampleRate} Hz" : ", no audio"));

                    // everything context-affine (cache, frame source, surfaces) lives on the
                    // composer thread
                    composer.Send(() =>
                    {
                        cache = new FrameTextureCache(composer.Factory);
                        frameSource = new SequentialFrameSource(project, cache, pool);
                        for (int i = 0; i < InFlight; i++)
                            surfaces[i] = composer.Factory.CreateSurface(output.WidthPx, output.HeightPx);
                    });

                    int rowBytes = output.WidthPx * 4;
                    for (int i = 0; i < InFlight; i++)
                        staging[i] = pool.Rent(rowBytes * output.HeightPx);

                    AudioMixer mixer = null;
                    float[] mixBuffer = null;
                    if (hasAudio)
                    {
                        audioSource = new SequentialAudioSource(project);
                        mixer = new AudioMixer(project, audioSource);
                        // one video frame's worth of audio is the largest per-iteration chunk
                        long perFrame = AudioTime.SamplesCeil(
                            TimeBase.FrameIndexToTicks(1, output.FpsNum, output.FpsDen), output.SampleRate);
                        mixBuffer = new float[(perFrame + 16) * AudioMixer.Channels];
                    }

                    writer = new Mp4Writer(outputPath, new Mp4WriterOptions
                    {
                        Width = output.WidthPx,
                        Height = output.HeightPx,
                        FpsNum = output.FpsNum,
                        FpsDen = output.FpsDen,
                        Crf = options.Crf,
                        // a schedule's instants are arbitrary, so pts go through in microseconds
                        UseMicrosecondTimeBase = schedule != null,
                        LegacyContainerTiming = options.LegacyContainerTiming,
                        Audio = hasAudio
                            ? new Mp4AudioOptions { SampleRate = output.SampleRate, Channels = AudioMixer.Channels }
                            : null,
                    });

                    // completed compose+readback jobs, in submission order (single composer thread)
                    var results = new BlockingCollection<(long Frame, ExceptionDispatchInfo Error)>();
                    long submitted = 0, audioPos = 0;
                    progress?.Report(0);

                    while (encoded < frameCount)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        // keep up to InFlight frames composing ahead; slot n % InFlight is free
                        // because its previous occupant (n - InFlight) has been encoded already.
                        while (submitted < frameCount && submitted - encoded < InFlight)
                        {
                            long n = submitted;
                            var surface = surfaces[n % InFlight];
                            var stage = staging[n % InFlight];
                            long tTicks = schedule?[(int)n]
                                ?? TimeBase.FrameIndexToTicks(n, output.FpsNum, output.FpsDen);
                            composer.Post(() =>
                            {
                                try
                                {
                                    FrameComposer.Compose(project, tTicks, frameSource,
                                        surface.Canvas, output.WidthPx, output.HeightPx);
                                    if (!composer.Factory.TryReadPixels(surface, output.WidthPx,
                                            output.HeightPx, stage.Address, rowBytes))
                                        throw new InvalidOperationException(
                                            $"Pixel readback failed on the {composer.BackendName} backend.");
                                    results.Add((n, null));
                                }
                                catch (Exception ex)
                                {
                                    results.Add((n, ExceptionDispatchInfo.Capture(ex)));
                                }
                            });
                            submitted++;
                        }

                        var done = results.Take();
                        done.Error?.Throw();

                        // drive audio up to this frame's end before muxing the frame, so the
                        // interleaver always has both streams' data for the span
                        if (mixer != null)
                        {
                            // frame end = the next scheduled instant (schedule mode) or the next
                            // grid instant; the last frame drives audio out to its full extent.
                            long frameEndTicks = schedule != null
                                ? (done.Frame + 1 < frameCount ? schedule[(int)(done.Frame + 1)] : audioEndTicks)
                                : TimeBase.FrameIndexToTicks(done.Frame + 1, output.FpsNum, output.FpsDen);
                            long target = Math.Min(totalAudioFrames,
                                AudioTime.SamplesFloor(frameEndTicks, output.SampleRate));
                            audioPos = MixUpTo(mixer, writer, mixBuffer, audioPos, target);
                        }

                        // pts: frame index on the CFR grid, microseconds under a schedule
                        long pts = schedule != null
                            ? TimeBase.TicksToStreamTime(schedule[(int)done.Frame], 1, 1_000_000)
                            : done.Frame;
                        writer.SubmitVideoFrame(staging[done.Frame % InFlight].Address, rowBytes,
                            output.WidthPx, output.HeightPx, pts);
                        encoded++;
                        progress?.Report(Math.Min(99.0, encoded * 100.0 / frameCount));
                    }

                    if (!cancelled)
                    {
                        if (mixer != null && audioPos < totalAudioFrames)
                            MixUpTo(mixer, writer, mixBuffer, audioPos, totalAudioFrames);
                        writer.Finish();
                        outputBytes = new FileInfo(outputPath).Length;
                        progress?.Report(100);
                    }
                }
                finally
                {
                    // pending posted jobs still reference the surfaces/staging: the cleanup Send
                    // queues behind them and Dispose joins, so teardown is ordered and on-thread.
                    if (composer != null)
                    {
                        try
                        {
                            composer.Send(() =>
                            {
                                frameSource?.Dispose();
                                cache?.Dispose();
                                foreach (var surface in surfaces)
                                    surface?.Dispose();
                            });
                        }
                        catch (Exception ex)
                        {
                            options.DiagnosticLog?.Invoke("RenderJob: composer teardown failed: " + ex);
                        }
                        composer.Dispose();
                    }

                    foreach (var stage in staging)
                        stage?.Return();
                    pool.Dispose();
                    audioSource?.Dispose();
                    writer?.Dispose(); // abort path finalizes the container; the file is deleted below when needed
                }
            }
            catch
            {
                TryDelete(outputPath);
                throw;
            }

            if (cancelled)
                TryDelete(outputPath);

            return new RenderResult
            {
                Outcome = cancelled ? RenderOutcome.Cancelled : RenderOutcome.Completed,
                OutputPath = outputPath,
                OutputBytes = outputBytes,
                SurfaceBackend = backend,
                VideoFrames = encoded,
            };
        }

        /// <summary>Mixes and submits audio in encoder-friendly chunks until <paramref name="target"/>
        /// (absolute output sample frames); returns the new position.</summary>
        private static long MixUpTo(AudioMixer mixer, Mp4Writer writer, float[] buffer,
            long position, long target)
        {
            int capacity = buffer.Length / AudioMixer.Channels;
            while (position < target)
            {
                int chunk = (int)Math.Min(capacity, target - position);
                mixer.MixChunk(position, chunk, buffer);
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
