using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Ai
{
    /// <summary>
    /// Generates the person-matte sidecar for one video stream: decode the frames, scale each to
    /// the analysis resolution and stream it as RGB24 through <c>clowd_tractnni matte</c>, then
    /// encode the returned gray8 mattes (alpha in luma) as
    /// <c>matte-{SourceId}-{StreamIndex}.mp4</c> (H.264 yuv420p, companion json per
    /// <see cref="AiSidecars"/>) beside the project. Every matte frame keeps its source frame's
    /// PTS — the sidecar shares the source stream's timeline, so the consumers pair the two by
    /// plain latest-PTS-at-or-before lookup with no mapping.
    ///
    /// <para>The analysis resolution is the source scaled so its shorter side is at most
    /// <see cref="MaxAnalysisSide"/>, both dimensions even (yuv420p): large enough for a clean
    /// silhouette, small enough that inference keeps up with a screen recording's frame count.
    /// The consumer maps the matte onto the frame by fractions, so the exact numbers never leak
    /// past this file and the companion json.</para>
    ///
    /// <para>The decode+scale runs on its own task feeding stdin while this thread drains stdout
    /// into the encoder (see <see cref="TractnniClient"/> for why), with each input frame's PTS
    /// queued across to be re-attached to its output matte — the streams are 1:1 in order by the
    /// CLI contract. The mp4 spools at a temp name that becomes the sidecar in one atomic move,
    /// and the companion json is written only after, so a valid companion always implies a
    /// complete sidecar.</para>
    /// </summary>
    public static class MatteGenerator
    {
        /// <summary>The analysis resolution's cap on the shorter side (see class remarks).</summary>
        public const int MaxAnalysisSide = 540;

        /// <summary>
        /// Generates the sidecar for (<paramref name="source"/>, <paramref name="streamIndex"/>),
        /// replacing any existing one. Returns false without doing anything when there is nowhere
        /// to cache (<paramref name="cacheDir"/> null — the dev harness) or no
        /// <c>clowd_tractnni</c> binary resolves; throws on decode or inference failure (with the
        /// process's stderr tail) and <see cref="OperationCanceledException"/> on cancellation.
        /// Progress is 0..1.
        /// </summary>
        public static bool Generate(Source source, int streamIndex, string cacheDir,
            IProgress<double> progress = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (String.IsNullOrEmpty(cacheDir))
                return false;

            var exe = TractnniLoader.TryGetPath();
            if (exe == null)
                return false;

            int sourceW, sourceH;
            using (var probePool = new FrameBufferPool())
            using (var probe = new SyncStreamDecoder(source.Path, streamIndex, probePool))
                (sourceW, sourceH) = (probe.Width, probe.Height);
            var (width, height) = AnalysisSize(sourceW, sourceH);

            int fpsNum = 30, fpsDen = 1;
            long expectedFrames = 0;
            foreach (var stream in source.Streams ?? [])
            {
                if (stream.Index != streamIndex)
                    continue;
                if (stream.AvgFrameRateNum > 0 && stream.AvgFrameRateDen > 0)
                    (fpsNum, fpsDen) = (stream.AvgFrameRateNum, stream.AvgFrameRateDen);
                if (stream.DurationTicks > 0)
                    expectedFrames = TimeBase.TicksToFrameIndex(stream.DurationTicks - 1, fpsNum, fpsDen) + 1;
            }

            var mp4Path = AiSidecars.MattePath(cacheDir, source.Id, streamIndex);
            Directory.CreateDirectory(cacheDir);
            var temp = mp4Path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            using var client = TractnniClient.Start(exe, new[]
            {
                "matte",
                "--width", width.ToString(CultureInfo.InvariantCulture),
                "--height", height.ToString(CultureInfo.InvariantCulture),
            });
            using var killOnCancel = cancellationToken.Register(client.Kill);

            // decode+scale → stdin on its own task; this thread drains stdout into the encoder.
            // CloseInput runs in the pump's finally either way, so the process always sees EOF and
            // the drain below always terminates — a decode failure surfaces after the exit code
            // has told its story. Each frame's PTS is queued BEFORE its pixels enter the pipe, so
            // the drain can never dequeue past what was sent.
            var ptsQueue = new ConcurrentQueue<long>();
            var pump = Task.Run(() => PumpInput(client, source, streamIndex, width, height, ptsQueue, cancellationToken));

            Mp4Writer writer = null;
            bool finished = false;
            try
            {
                writer = new Mp4Writer(temp, new Mp4WriterOptions
                {
                    Width = width,
                    Height = height,
                    FpsNum = fpsNum,
                    FpsDen = fpsDen,
                    // the matte's frames sit on the SOURCE's grid (CFR or not), so pts go
                    // through in microseconds — the same passthrough mode the v1 VFR render uses
                    UseMicrosecondTimeBase = true,
                });

                var gray = new byte[width * height];
                var bgra = new byte[width * height * 4];
                long framesReceived = 0;
                long lastPtsUs = long.MinValue;
                while (ReadMatteFrame(client.Output, gray))
                {
                    if (!ptsQueue.TryDequeue(out long ptsTicks))
                    {
                        throw new InvalidOperationException(
                            "clowd_tractnni produced more output frames than were sent."
                            + StderrSuffix(client));
                    }

                    for (int i = 0; i < gray.Length; i++)
                    {
                        byte v = gray[i];
                        int o = i * 4;
                        bgra[o] = v;
                        bgra[o + 1] = v;
                        bgra[o + 2] = v;
                        bgra[o + 3] = 0xFF;
                    }

                    // strictly increasing pts for the muxer; the decoder's clock can stutter on
                    // a malformed source, and a one-microsecond nudge is invisible to the
                    // at-or-before lookup.
                    long ptsUs = TimeBase.TicksToStreamTime(ptsTicks, 1, 1_000_000);
                    if (ptsUs <= lastPtsUs)
                        ptsUs = lastPtsUs + 1;
                    lastPtsUs = ptsUs;

                    unsafe
                    {
                        fixed (byte* p = bgra)
                            writer.SubmitVideoFrame((IntPtr)p, width * 4, width, height, ptsUs);
                    }

                    framesReceived++;
                    if (progress != null && expectedFrames > 0)
                        progress.Report(Math.Min(0.99, framesReceived / (double)expectedFrames));
                }

                Exception pumpError = null;
                long framesSent = 0;
                try { framesSent = pump.GetAwaiter().GetResult(); }
                catch (Exception ex) { pumpError = ex; }

                cancellationToken.ThrowIfCancellationRequested();
                if (!client.WaitForExit(10_000))
                {
                    client.Kill();
                    throw new InvalidOperationException(
                        "clowd_tractnni did not exit after its output ended." + StderrSuffix(client));
                }
                client.ThrowIfFailed();
                if (pumpError != null)
                    throw new InvalidOperationException("Decoding the source video failed.", pumpError);
                if (framesReceived != framesSent)
                {
                    throw new InvalidOperationException(
                        $"clowd_tractnni returned {framesReceived} matte frames for {framesSent} " +
                        "input frames — the streams must match 1:1." + StderrSuffix(client));
                }

                writer.Finish();
                finished = true;
                writer.Dispose();
                writer = null;

                File.Move(temp, mp4Path, overwrite: true);
            }
            catch
            {
                client.Kill();
                try { pump.Wait(TimeSpan.FromSeconds(3)); }
                catch { /* its failure is already the story, or was killed */ }
                if (writer != null && !finished)
                    writer.Abandon(); // the temp file is deleted below — never pay the faststart rewrite
                writer?.Dispose();
                writer = null;
                try { File.Delete(temp); }
                catch { /* best effort */ }
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                writer?.Dispose();
            }

            AiSidecars.TryWriteCompanion(mp4Path, AiSidecars.DescribeSource(source.Path, width, height));
            progress?.Report(1.0);
            return true;
        }

        /// <summary>
        /// The video streams the project wants a person matte for: every
        /// (sourceId, streamIndex) referenced by a media item on a visible video track whose
        /// <see cref="Item.Effect"/> is a matte-needing kind — the video mirror of
        /// <see cref="Clowd.VideoSDK.Audio.DenoisedAudioSource.CollectDenoisedStreams"/>, with
        /// the same visibility rule the player's stream map applies (a hidden track's streams
        /// have no pipelines for a matte to ride).
        /// </summary>
        public static HashSet<(Guid SourceId, int StreamIndex)> CollectMatteStreams(Project project)
        {
            var videoTracks = new HashSet<Guid>();
            foreach (var track in project?.Tracks ?? new List<Track>())
            {
                if (track.Kind == TrackKind.Video && !track.Hidden)
                    videoTracks.Add(track.Id);
            }

            var keys = new HashSet<(Guid, int)>();
            if (videoTracks.Count == 0)
                return keys;

            foreach (var item in project.Items ?? new List<Item>())
            {
                if (item.Content is MediaContent media && item.DurationTicks > 0
                    && item.Effect is { } effect && VideoEffect.NeedsMatte(effect.Kind)
                    && videoTracks.Contains(item.TrackId))
                    keys.Add((media.SourceId, media.StreamIndex));
            }

            return keys;
        }

        /// <summary>The analysis resolution for a source of the given size: scaled so
        /// min(W,H) is at most <see cref="MaxAnalysisSide"/>, each dimension rounded to even
        /// (yuv420p subsampling), aspect preserved. A source already small enough keeps its size
        /// (modulo the even rounding).</summary>
        internal static (int Width, int Height) AnalysisSize(int sourceWidth, int sourceHeight)
        {
            double scale = Math.Min(1.0, MaxAnalysisSide / (double)Math.Min(sourceWidth, sourceHeight));
            int width = Math.Max(2, (int)Math.Round(sourceWidth * scale / 2, MidpointRounding.AwayFromZero) * 2);
            int height = Math.Max(2, (int)Math.Round(sourceHeight * scale / 2, MidpointRounding.AwayFromZero) * 2);
            return (width, height);
        }

        /// <summary>Decodes the stream forward, sws-scales each BGRA frame to RGB24 at the
        /// analysis size and writes it to the process's stdin, returning the frame count sent
        /// (with each frame's PTS enqueued first). Always closes stdin, so the process (and with
        /// it the caller's stdout drain) terminates whatever happens here.</summary>
        private static unsafe long PumpInput(TractnniClient client, Source source, int streamIndex,
            int width, int height, ConcurrentQueue<long> ptsQueue, CancellationToken cancellationToken)
        {
            using var pool = new FrameBufferPool();
            using var decoder = new SyncStreamDecoder(source.Path, streamIndex, pool);
            SwsContext* sws = null;
            var srcData = new byte*[4];
            var srcStride = new int[4];
            var dstData = new byte*[4];
            var dstStride = new int[4];
            var rgb = new byte[width * height * 3];
            long frames = 0;
            try
            {
                fixed (byte* rgbPtr = rgb)
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!decoder.DecodeNext(out long ptsTicks, out var buffer,
                                out int srcW, out int srcH, out int srcRowBytes))
                            break;

                        try
                        {
                            sws = ffmpeg.sws_getCachedContext(sws, srcW, srcH,
                                AVPixelFormat.AV_PIX_FMT_BGRA, width, height,
                                AVPixelFormat.AV_PIX_FMT_RGB24, ffmpeg.SWS_BILINEAR, null, null, null);
                            if (sws == null)
                                throw new InvalidOperationException(
                                    $"sws_getCachedContext failed for {srcW}x{srcH} -> {width}x{height}.");

                            srcData[0] = (byte*)buffer.Address;
                            srcStride[0] = srcRowBytes;
                            srcData[1] = srcData[2] = srcData[3] = null;
                            srcStride[1] = srcStride[2] = srcStride[3] = 0;
                            dstData[0] = rgbPtr;
                            dstStride[0] = width * 3;
                            dstData[1] = dstData[2] = dstData[3] = null;
                            dstStride[1] = dstStride[2] = dstStride[3] = 0;

                            ffmpeg.sws_scale(sws, srcData, srcStride, 0, srcH, dstData, dstStride);
                        }
                        finally
                        {
                            buffer.Return();
                        }

                        ptsQueue.Enqueue(ptsTicks);
                        client.Input.Write(rgb, 0, rgb.Length);
                        frames++;
                    }
                }

                client.Input.Flush();
                return frames;
            }
            finally
            {
                if (sws != null)
                    ffmpeg.sws_freeContext(sws);
                client.CloseInput();
            }
        }

        /// <summary>Reads one gray8 matte frame from stdout into <paramref name="gray"/>. False on
        /// a clean end of stream; a torn frame (EOF mid-frame — the process died) throws.</summary>
        private static bool ReadMatteFrame(Stream output, byte[] gray)
        {
            int offset = 0;
            while (offset < gray.Length)
            {
                int read = output.Read(gray, offset, gray.Length - offset);
                if (read <= 0)
                {
                    if (offset == 0)
                        return false;
                    throw new InvalidOperationException(
                        $"clowd_tractnni's output ended {gray.Length - offset} bytes into a matte frame.");
                }
                offset += read;
            }

            return true;
        }

        private static string StderrSuffix(TractnniClient client)
        {
            var tail = client.StderrTail;
            return tail.Length > 0 ? Environment.NewLine + tail : "";
        }
    }
}
