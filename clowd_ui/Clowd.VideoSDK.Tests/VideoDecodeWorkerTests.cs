using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // VideoDecodeWorker driven directly (demuxer + packet queue, no player above it), so a test
    // can hold the present thread at an exact instant. The sink stands in for the UI's frame
    // pool, whose BeginFrame is documented to block while the UI holds every buffer — which is
    // what makes the window between "immediate flag read" and "immediate flag cleared" span an
    // arbitrary amount of wall time.
    public class VideoDecodeWorkerTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const long Second = 10_000_000;
        private const long FrameTicks = Second / Fps;

        private static void RequireFFmpeg() =>
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
        }

        private string EncodeVideoFixture(int seconds)
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-video-decode-worker-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);
            using var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
            });

            var bgra = new byte[W * H * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int n = 0; n < seconds * Fps; n++)
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
            }
            finally
            {
                pin.Free();
            }

            writer.Finish();
            return path;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                    return true;
                Thread.Sleep(15);
            }

            return condition();
        }

        /// <summary>Sink whose BeginFrame the test can hold closed — the UI-pool "no free buffer"
        /// stall, but under test control.</summary>
        private sealed class GatedSink : IFrameSink, IDisposable
        {
            public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            private readonly object _sync = new object();
            private IntPtr _buf;
            private long _lastPts = long.MinValue;

            public long LastPts { get { lock (_sync) return _lastPts; } }

            public FrameTarget BeginFrame(int width, int height)
            {
                Entered.Set();
                Gate.Wait();
                lock (_sync)
                {
                    if (_buf == IntPtr.Zero)
                        _buf = Marshal.AllocHGlobal(width * 4 * height);
                    return new FrameTarget(_buf, width * 4, width, height, null);
                }
            }

            public void CompleteFrame(in FrameTarget target, TimeSpan pts)
            {
                lock (_sync)
                    _lastPts = pts.Ticks;
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    if (_buf != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_buf);
                        _buf = IntPtr.Zero;
                    }
                }

                Gate.Dispose();
                Entered.Dispose();
            }
        }

        // Regression: a seek issued while the PREVIOUS seek's immediate frame is still inside
        // Present() (sink blocked on a buffer) must still deliver its own frame. The present
        // thread clears the immediate-present request only after Present() returns; with a plain
        // 0/1 flag that deferred clear erased the new seek's request set in between, and the new
        // seek's frame then sat in the queue forever — not immediate, and paused, so nothing was
        // ever presented (CompositionPlayerTests.Seek_to_timeline_end… failing with pts=0). The
        // gate makes that sub-millisecond window a deterministic one.
        [Fact]
        public void Seek_during_previous_seeks_immediate_present_still_delivers_its_frame()
        {
            RequireFFmpeg();

            string fixture = EncodeVideoFixture(2);
            var demuxer = new Demuxer();
            var queue = new PacketQueue(32);
            var sink = new GatedSink();
            VideoDecodeWorker worker = null;
            try
            {
                var info = demuxer.Open(fixture);
                int streamIndex = info.VideoStreams[0].StreamIndex;
                demuxer.AttachQueue(streamIndex, queue);

                worker = new VideoDecodeWorker(
                    demuxer, streamIndex, queue,
                    new VideoOpenOptions { EnableHardwareDecode = false },
                    new PlaybackClock(),
                    () => sink,
                    () => false, // paused throughout: only an immediate present can deliver
                    (w, pts) => { });

                // the open pairing (see FFmpegVideoPlayer.OpenCore): prime at 0, then run.
                worker.PrepareSeek(TimeSpan.Zero, SeekMode.Exact);
                worker.OnSeeked(0);
                demuxer.Start();
                worker.Start();

                // the present thread is now mid-Present() for the primed first frame — its
                // immediate request already read, not yet cleared — parked in BeginFrame.
                Assert.True(sink.Entered.Wait(10000), "present thread never reached the sink");

                // the second seek runs entirely inside that window.
                long target = Second + 4 * Second / 10; // 1.4s of the 2s fixture
                worker.PrepareSeek(new TimeSpan(target), SeekMode.Exact);
                int serial = demuxer.SeekAndFlush(new TimeSpan(target));
                worker.OnSeeked(serial);

                sink.Gate.Set();

                // paused, so the seek's one-shot immediate present is the only thing that can
                // deliver: if the first present's deferred clear ate this seek's request, the
                // sink never advances past the primed frame.
                Assert.True(WaitUntil(() =>
                {
                    long p = sink.LastPts;
                    return p >= target - 2 * FrameTicks && p <= target;
                }, 10000), $"seek did not deliver its frame: lastPts={sink.LastPts}");
            }
            finally
            {
                // never leave the present thread parked in BeginFrame across Dispose (it joins
                // the threads, then frees the frames they touch).
                sink.Gate.Set();

                // stop order per CompositionPlayer.DisposePipelineSet: demux thread first, then
                // wake consumers via queue Stop, then join the worker, then free the rest.
                demuxer.Stop();
                queue.Stop();
                worker?.Dispose();
                queue.Dispose();
                demuxer.Dispose();
                sink.Dispose();
            }
        }
    }
}
