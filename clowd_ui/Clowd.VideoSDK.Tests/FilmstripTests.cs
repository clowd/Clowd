using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Thumbs;
using Xunit;
using ThumbnailRequest = Clowd.UI.VideoEditor.Timeline.ThumbnailRequest;

namespace Clowd.VideoSDK.Tests
{
    // Real-decode round trips for the timeline filmstrip. The fixture is one distinct solid color
    // per second, so a thumbnail's own center pixel says which second it was decoded from: every
    // color assertion below is really "this thumbnail is at the source time it claims to be at",
    // which is the only property a filmstrip has to get right.
    //
    // Scheduling is injected, so the two-tier pass runs synchronously inside the call that triggers
    // it (InlineQueue) and the tests never sleep waiting for a background thread. Skips when the
    // FFmpeg natives are absent (same resolver as EncoderTests).
    public class FilmstripTests : IDisposable
    {
        private const int W = 160, H = 120, Fps = 30;
        private const int FixtureSeconds = 10;
        private const int ThumbHeight = 48;
        private const long Second = TimeBase.TicksPerSecond;
        private const long FrameDurTicks = Second / Fps;

        /// <summary>One color per second, as BGR. No two are closer than 70 units on their
        /// furthest channel, so a yuv420p round trip (which moves a solid color by single digits)
        /// can never carry a frame closer to a neighbor's color than to its own.</summary>
        private static readonly (byte B, byte G, byte R)[] Palette =
        {
            (60, 60, 60), (200, 60, 60), (60, 200, 60), (60, 60, 200), (200, 200, 60),
            (200, 60, 200), (60, 200, 200), (200, 200, 200), (130, 130, 130), (60, 130, 200),
        };

        private static bool FFmpegAvailable => FFmpegLoader.TryInitialize(FindFFmpegDirectory);

        private static string FindFFmpegDirectory()
        {
            string probeFile = OperatingSystem.IsWindows() ? "avcodec-61.dll" : "libavcodec.so.61";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                foreach (var cfg in new[] { "release", "debug" })
                {
                    var candidate = Path.Combine(dir.FullName, "obs-express-rs", "target", cfg);
                    if (File.Exists(Path.Combine(candidate, probeFile)))
                        return candidate;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static void RequireFFmpeg() =>
            Assert.SkipUnless(FFmpegAvailable,
                $"FFmpeg natives not found (set {FFmpegLoader.EnvVarName} or build obs-express-rs): {FFmpegLoader.FailureReason}");

        // ----------------------------------------------------------------------------- fixture

        private readonly List<string> _tempFiles = new List<string>();
        private string _fixture;

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
        }

        /// <summary>Encodes (once per test) a 10 s mp4 whose every second is a different solid
        /// color. Ten seconds is past x264's default keyframe interval, so the stream is
        /// guaranteed more than one keyframe even if scene-cut detection never fires.</summary>
        private string ColorFixture()
        {
            if (_fixture != null)
                return _fixture;

            string path = Path.Combine(Path.GetTempPath(), $"clowd-filmstrip-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);

            using (var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
            }))
            {
                var bgra = new byte[W * H * 4];
                var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                try
                {
                    for (int n = 0; n < Fps * FixtureSeconds; n++)
                    {
                        var c = Palette[n / Fps % Palette.Length];
                        for (int i = 0; i < bgra.Length; i += 4)
                        {
                            bgra[i] = c.B;
                            bgra[i + 1] = c.G;
                            bgra[i + 2] = c.R;
                            bgra[i + 3] = 0xFF;
                        }
                        writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
                    }
                }
                finally
                {
                    pin.Free();
                }

                writer.Finish();
            }

            _fixture = path;
            return path;
        }

        // ------------------------------------------------------------------------- scheduling

        /// <summary>Runs every work item synchronously inside <c>Enqueue</c>: the call that starts
        /// a pass returns only once the pass is finished, so the tests are deterministic.</summary>
        private sealed class InlineQueue : IThumbWorkQueue
        {
            public readonly List<int> Priorities = new List<int>();

            public IDisposable Enqueue(int priority, Action<CancellationToken> work)
            {
                Priorities.Add(priority);
                work(CancellationToken.None);
                return new Handle();
            }

            private sealed class Handle : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }

        /// <summary>An inline queue whose <c>HasPendingBelow</c> answers yes a controlled number of
        /// times: how the tests make a pass or refinement item park itself deterministically, and
        /// prove it re-enqueues and resumes rather than abandoning the strip.</summary>
        private sealed class YieldingInlineQueue : IThumbWorkQueue
        {
            public readonly List<int> Priorities = new List<int>();

            /// <summary>How many more times <see cref="HasPendingBelow"/> should claim more urgent
            /// work is waiting.</summary>
            public int PendingBelowAnswers;

            public IDisposable Enqueue(int priority, Action<CancellationToken> work)
            {
                Priorities.Add(priority);
                work(CancellationToken.None);
                return new Handle();
            }

            public bool HasPendingBelow(int priorityBand)
            {
                if (PendingBelowAnswers <= 0)
                    return false;
                PendingBelowAnswers--;
                return true;
            }

            private sealed class Handle : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }

        /// <summary>The real scheduler's shape in miniature: one worker thread, items removed when
        /// their handle is disposed before they start, token canceled when it is disposed after.
        /// The gate lets a test hold an item in the queue and prove it is dropped rather than
        /// decoded.</summary>
        private sealed class ThreadQueue : IThumbWorkQueue, IDisposable
        {
            private readonly BlockingCollection<Item> _items = new BlockingCollection<Item>();
            private readonly ManualResetEventSlim _gate;
            private readonly Thread _thread;
            private int _ran;
            private int _dropped;

            public ThreadQueue(bool open)
            {
                _gate = new ManualResetEventSlim(open);
                _thread = new Thread(Loop) { IsBackground = true, Name = "test-thumb-queue" };
                _thread.Start();
            }

            /// <summary>Set once a work item has actually begun running.</summary>
            public readonly ManualResetEventSlim Started = new ManualResetEventSlim(false);

            public int Ran => Volatile.Read(ref _ran);
            public int Dropped => Volatile.Read(ref _dropped);

            public void Open() => _gate.Set();

            public IDisposable Enqueue(int priority, Action<CancellationToken> work)
            {
                var item = new Item(work);
                _items.Add(item);
                return item;
            }

            private void Loop()
            {
                foreach (var item in _items.GetConsumingEnumerable())
                {
                    _gate.Wait();
                    if (item.Canceled)
                    {
                        Interlocked.Increment(ref _dropped);
                        continue;
                    }

                    Interlocked.Increment(ref _ran);
                    Started.Set();
                    try { item.Work(item.Cts.Token); }
                    catch { /* the provider handles its own failures; a throw here would kill the loop */ }
                }
            }

            public void Dispose()
            {
                if (!_items.IsAddingCompleted)
                    _items.CompleteAdding();
                _gate.Set();
                _thread.Join(TimeSpan.FromSeconds(10));
            }

            private sealed class Item : IDisposable
            {
                public Item(Action<CancellationToken> work) => Work = work;

                public readonly Action<CancellationToken> Work;
                public readonly CancellationTokenSource Cts = new CancellationTokenSource();
                public volatile bool Canceled;

                public void Dispose()
                {
                    Canceled = true;
                    Cts.Cancel();
                }
            }
        }

        // ---------------------------------------------------------------------------- helpers

        private static (byte B, byte G, byte R) CenterPixel(FilmstripThumbnail thumb)
        {
            int offset = thumb.Height / 2 * thumb.Stride + thumb.Width / 2 * 4;
            return (thumb.Pixels[offset], thumb.Pixels[offset + 1], thumb.Pixels[offset + 2]);
        }

        /// <summary>Which palette entry a decoded pixel is, plus how far off it landed — the codec
        /// round trip moves solid colors by a few units, never by a hundred.</summary>
        private static (int Index, int Distance) NearestColor((byte B, byte G, byte R) px)
        {
            int best = 0, bestDistance = Int32.MaxValue;
            for (int i = 0; i < Palette.Length; i++)
            {
                var c = Palette[i];
                int d = Math.Max(Math.Abs(c.B - px.B), Math.Max(Math.Abs(c.G - px.G), Math.Abs(c.R - px.R)));
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            return (best, bestDistance);
        }

        private static void AssertColorOfSecond(int second, FilmstripThumbnail thumb, string what)
        {
            var (index, distance) = NearestColor(CenterPixel(thumb));
            Assert.True(distance <= 30, $"{what}: center pixel {CenterPixel(thumb)} is not a palette color (off by {distance}).");
            Assert.Equal(second % Palette.Length, index);
        }

        // -------------------------------------------------------------------------- the tests

        [Fact]
        public void Quantize_snaps_down_to_power_of_two_multiples_of_the_base()
        {
            const long Base = FilmstripProvider.BaseIntervalTicks;
            Assert.Equal(TimeSpan.TicksPerMillisecond * 250, Base);

            // The contract the timeline's ThumbnailRequest.QuantizeInterval states: round DOWN to a
            // power-of-two multiple of the base, so a coarse grid's instants are a subset of a fine
            // one's and zooming reuses what is already decoded.
            Assert.Equal(Base, FilmstripProvider.QuantizeInterval(TimeSpan.TicksPerMillisecond * 300));
            Assert.Equal(Base, FilmstripProvider.QuantizeInterval(Base));
            Assert.Equal(Base, FilmstripProvider.QuantizeInterval(TimeSpan.TicksPerMillisecond * 10));
            Assert.Equal(Base, FilmstripProvider.QuantizeInterval(0));
            Assert.Equal(Base, FilmstripProvider.QuantizeInterval(-5));
            Assert.Equal(2 * Base, FilmstripProvider.QuantizeInterval(TimeSpan.TicksPerMillisecond * 500));
            Assert.Equal(2 * Base, FilmstripProvider.QuantizeInterval(TimeSpan.TicksPerMillisecond * 999));
            Assert.Equal(4 * Base, FilmstripProvider.QuantizeInterval(TimeSpan.TicksPerSecond));
            Assert.Equal(4 * Base, FilmstripProvider.QuantizeInterval(TimeSpan.TicksPerMillisecond * 1999));
            Assert.Equal(8 * Base, FilmstripProvider.QuantizeInterval(TimeSpan.TicksPerSecond * 2));

            // a nonsense interval must terminate and stay in range rather than overflow
            long extreme = FilmstripProvider.QuantizeInterval(Int64.MaxValue);
            Assert.True(extreme > 0 && extreme % Base == 0);

            // every result is a power-of-two multiple of the base, and never coarser than asked
            foreach (long ms in new long[] { 1, 250, 300, 400, 700, 1500, 5000, 60000 })
            {
                long q = FilmstripProvider.QuantizeInterval(TimeSpan.TicksPerMillisecond * ms);
                long multiple = q / Base;
                Assert.Equal(0L, q % Base);
                Assert.Equal(0L, multiple & (multiple - 1));
                Assert.True(q <= Math.Max(Base, TimeSpan.TicksPerMillisecond * ms));
            }
        }

        // The quantizer is written out twice — here in the SDK and as ThumbnailRequest.QuantizeInterval
        // in Clowd.Ui — because the SDK cannot reference the UI. The two grids MUST agree or the
        // timeline asks for instants the provider never decodes; this is the only thing keeping the
        // copies honest, so it compares the real implementations rather than restating the contract.
        [Fact]
        public void Quantize_matches_the_timelines_own_copy_of_the_grid()
        {
            Assert.Equal(FilmstripProvider.BaseIntervalTicks, ThumbnailRequest.BaseIntervalTicks);

            var intervals = new List<long> { Int64.MinValue, -5, 0, 1, Int64.MaxValue };
            foreach (long ms in new long[] { 1, 60, 125, 249, 250, 251, 300, 499, 500, 501, 700, 999,
                                             1000, 1001, 1999, 2000, 5000, 60000, 3600000 })
                intervals.Add(TimeSpan.TicksPerMillisecond * ms);

            foreach (long interval in intervals)
            {
                Assert.Equal(FilmstripProvider.QuantizeInterval(interval),
                    ThumbnailRequest.QuantizeInterval(interval));
            }
        }

        [Fact]
        public void Nearest_lookup_picks_the_closest_thumbnail_on_either_side()
        {
            var thumbs = new[]
            {
                new FilmstripThumbnail(0, new byte[4], 1, 1, 4),
                new FilmstripThumbnail(1000, new byte[4], 1, 1, 4),
                new FilmstripThumbnail(5000, new byte[4], 1, 1, 4),
            };
            var snapshot = new FilmstripSnapshot(FilmstripProvider.BaseIntervalTicks, 5000, thumbs, true, 1, null);

            Assert.True(snapshot.TryGetNearest(-100, out var t));
            Assert.Equal(0L, t.SourceTicks);
            Assert.True(snapshot.TryGetNearest(400, out t));
            Assert.Equal(0L, t.SourceTicks);
            Assert.True(snapshot.TryGetNearest(600, out t));
            Assert.Equal(1000L, t.SourceTicks);
            Assert.True(snapshot.TryGetNearest(4000, out t));
            Assert.Equal(5000L, t.SourceTicks);
            Assert.True(snapshot.TryGetNearest(99999, out t));
            Assert.Equal(5000L, t.SourceTicks);

            Assert.False(FilmstripSnapshot.Empty.TryGetNearest(0, out _));
        }

        [Fact]
        public void Keyframe_pass_thumbnails_show_the_color_of_their_own_timestamp()
        {
            RequireFFmpeg();
            string fixture = ColorFixture();

            var queue = new InlineQueue();
            using var provider = new FilmstripProvider(queue);

            // the first call starts (and, on the inline queue, completes) the keyframe pass
            Assert.Empty(provider.GetOrStart(fixture, 0, ThumbHeight).Thumbnails);
            Assert.Equal(FilmstripProvider.KeyframePassPriority, Assert.Single(queue.Priorities));

            var snapshot = provider.GetOrStart(fixture, 0, ThumbHeight);
            Assert.Null(snapshot.Error);
            Assert.True(snapshot.IsComplete);
            Assert.Equal(1d, snapshot.Progress);
            Assert.InRange(snapshot.DurationTicks, Second * 9, Second * 11);

            // 10 s is past x264's default keyframe interval, so there is always more than one
            Assert.True(snapshot.Thumbnails.Count >= 2, $"the keyframe pass produced {snapshot.Thumbnails.Count} thumbnails");

            long previous = Int64.MinValue;
            foreach (var thumb in snapshot.Thumbnails)
            {
                Assert.True(thumb.SourceTicks > previous, "thumbnails must come back sorted");
                previous = thumb.SourceTicks;

                Assert.Equal(ThumbHeight, thumb.Height);
                Assert.Equal(W * ThumbHeight / H, thumb.Width); // width follows the source aspect
                Assert.Equal(thumb.Width * 4, thumb.Stride);
                Assert.Equal(thumb.Stride * thumb.Height, thumb.Pixels.Length);

                // sample the middle of the frame's display interval, so a pts that rounded a tick
                // the wrong way cannot move it across a color change
                long frame = TimeBase.TicksToFrameIndex(thumb.SourceTicks + FrameDurTicks / 2, Fps, 1);
                AssertColorOfSecond((int)(frame / Fps), thumb, $"keyframe at {thumb.SourceTicks}");
            }
        }

        [Fact]
        public void Viewport_refinement_fills_the_requested_grid()
        {
            RequireFFmpeg();
            string fixture = ColorFixture();

            var queue = new InlineQueue();
            using var provider = new FilmstripProvider(queue);
            provider.GetOrStart(fixture, 0, ThumbHeight);

            int afterPass = provider.GetOrStart(fixture, 0, ThumbHeight).Thumbnails.Count;

            // 300 ms is not a legal grid step: it quantizes down to the 250 ms base
            provider.SetViewport(fixture, 0, ThumbHeight, 2 * Second, 4 * Second, TimeSpan.TicksPerMillisecond * 300);
            Assert.Contains(FilmstripProvider.RefinePriority, queue.Priorities);

            var snapshot = provider.GetOrStart(fixture, 0, ThumbHeight);
            const long Interval = FilmstripProvider.BaseIntervalTicks;
            Assert.Equal(Interval, snapshot.IntervalTicks);
            Assert.True(snapshot.IsComplete);
            Assert.True(snapshot.Thumbnails.Count > afterPass, "refinement added no thumbnails");

            for (long slot = 2 * Second; slot <= 4 * Second; slot += Interval)
            {
                Assert.True(snapshot.TryGetNearest(slot, out var thumb));
                Assert.True(Math.Abs(thumb.SourceTicks - slot) <= Interval / 4,
                    $"grid slot {slot} is only covered by a thumbnail at {thumb.SourceTicks}");

                // the covering frame is the last one at or before the slot; when that is the first
                // or last frame of a second, a one-frame difference is a different color and the
                // assertion would be about pts rounding rather than about the filmstrip
                long frame = TimeBase.TicksToFrameIndex(slot, Fps, 1);
                if (frame % Fps <= 1 || frame % Fps >= Fps - 1)
                    continue;

                AssertColorOfSecond((int)(frame / Fps), thumb, $"grid slot {slot}");
            }
        }

        /// <summary>A cut recording's viewport is a list of kept-segment spans, not their union:
        /// refinement must fill each visible span and never decode the removed material between
        /// them (with a union, the picker centers on the middle of the cut and the visible strips
        /// never refine at all).</summary>
        [Fact]
        public void Refinement_fills_each_visible_span_and_skips_the_cut_between_them()
        {
            RequireFFmpeg();
            string fixture = ColorFixture();

            var queue = new InlineQueue();
            using var provider = new FilmstripProvider(queue);
            provider.GetOrStart(fixture, 0, ThumbHeight);
            var passThumbs = new HashSet<long>();
            foreach (var thumb in provider.GetOrStart(fixture, 0, ThumbHeight).Thumbnails)
                passThumbs.Add(thumb.SourceTicks);

            // two kept segments of a cut recording, both on screen; source 1.5 s .. 8 s is cut out
            const long Interval = FilmstripProvider.BaseIntervalTicks;
            long[] spanStarts = { Second / 2, 8 * Second };
            provider.SetViewport(fixture, 0, ThumbHeight,
                new[] { (Second / 2, Second * 3 / 2), (8 * Second, 9 * Second) }, Interval);

            var snapshot = provider.GetOrStart(fixture, 0, ThumbHeight);
            Assert.Null(snapshot.Error);
            Assert.True(snapshot.IsComplete);

            // both spans are filled to the grid…
            foreach (long spanStart in spanStarts)
            {
                for (long slot = spanStart; slot <= spanStart + Second; slot += Interval)
                {
                    Assert.True(snapshot.TryGetNearest(slot, out var thumb));
                    Assert.True(Math.Abs(thumb.SourceTicks - slot) <= Interval / 4,
                        $"grid slot {slot} is only covered by a thumbnail at {thumb.SourceTicks}");
                }
            }

            // …and nothing was decoded for the cut-out middle: every thumbnail there predates the
            // viewport (a keyframe-pass leftover), never a refinement product.
            foreach (var thumb in snapshot.Thumbnails)
            {
                if (thumb.SourceTicks > Second * 3 / 2 + Interval && thumb.SourceTicks < 8 * Second - Interval)
                {
                    Assert.True(passThumbs.Contains(thumb.SourceTicks),
                        $"refinement decoded {thumb.SourceTicks}, inside the cut-out span nothing displays");
                }
            }
        }

        /// <summary>Retiring a viewport (its rows scrolled away or were deleted) makes the strip's
        /// thumbnails the FIRST eviction candidates — without it they would keep scoring
        /// "in view" against their stale span and outlive the pixels actually being drawn.</summary>
        [Fact]
        public void A_cleared_viewport_gives_its_thumbnails_up_first()
        {
            RequireFFmpeg();
            string fixture = ColorFixture();

            const int Cap = 6;
            const int OtherHeight = 32; // a second strip over the same stream, sharing the cache
            var queue = new InlineQueue();
            using var provider = new FilmstripProvider(queue, maxThumbnails: Cap);

            // strip A: decoded, aimed, refined — then its rows go away
            provider.GetOrStart(fixture, 0, ThumbHeight);
            provider.SetViewport(fixture, 0, ThumbHeight, 2 * Second, 3 * Second, FilmstripProvider.BaseIntervalTicks);
            provider.ClearViewport(fixture, 0, ThumbHeight);

            // strip B: what the user is looking at now
            provider.GetOrStart(fixture, 0, OtherHeight);
            provider.SetViewport(fixture, 0, OtherHeight, 8 * Second, 9 * Second, FilmstripProvider.BaseIntervalTicks);

            // the retired strip lost everything to the live one…
            Assert.Empty(provider.GetOrStart(fixture, 0, ThumbHeight).Thumbnails);

            // …which kept its viewport coverage under the cap (eviction is lazy, so a stray far
            // keyframe may survive when the cap is never crossed again — what matters is that the
            // retired strip lost first and the live viewport kept its pixels)
            var live = provider.GetOrStart(fixture, 0, OtherHeight).Thumbnails;
            Assert.NotEmpty(live);
            Assert.True(live.Count <= Cap);
            Assert.Contains(live, t => t.SourceTicks >= 8 * Second);
        }

        /// <summary>The keyframe pass must yield the single scheduler thread when a lower band (a
        /// waveform) is waiting: it parks its decoder, re-enqueues itself at its own band, and the
        /// resumed item finishes the stream — <c>IsComplete</c> flips only at the real end.</summary>
        [Fact]
        public void Keyframe_pass_parks_for_more_urgent_work_and_resumes_where_it_left_off()
        {
            RequireFFmpeg();
            string fixture = ColorFixture();

            var queue = new YieldingInlineQueue { PendingBelowAnswers = 1 };
            using var provider = new FilmstripProvider(queue);
            provider.GetOrStart(fixture, 0, ThumbHeight);

            var snapshot = provider.GetOrStart(fixture, 0, ThumbHeight);
            Assert.Null(snapshot.Error);
            Assert.True(snapshot.IsComplete);
            Assert.Equal(1d, snapshot.Progress);
            Assert.True(snapshot.Thumbnails.Count >= 2, "the resumed pass lost the rest of the stream");

            // the pass ran as two scheduler items, both at its own band
            Assert.Equal(new[] { FilmstripProvider.KeyframePassPriority, FilmstripProvider.KeyframePassPriority },
                queue.Priorities);

            // resuming from the parked decoder re-decoded nothing and skipped nothing
            long previous = Int64.MinValue;
            foreach (var thumb in snapshot.Thumbnails)
            {
                Assert.True(thumb.SourceTicks > previous, "thumbnails must come back sorted and unique");
                previous = thumb.SourceTicks;

                long frame = TimeBase.TicksToFrameIndex(thumb.SourceTicks + FrameDurTicks / 2, Fps, 1);
                AssertColorOfSecond((int)(frame / Fps), thumb, $"keyframe at {thumb.SourceTicks}");
            }
        }

        /// <summary>Refinement's yield: with more urgent work pending it bails between slots and
        /// re-enqueues itself, resuming (same viewport, same Attempted set) once the band has
        /// drained — the grid still ends up complete.</summary>
        [Fact]
        public void Refinement_parks_for_more_urgent_work_and_requeues_itself()
        {
            RequireFFmpeg();
            string fixture = ColorFixture();

            var queue = new YieldingInlineQueue();
            using var provider = new FilmstripProvider(queue);
            provider.GetOrStart(fixture, 0, ThumbHeight);

            queue.PendingBelowAnswers = 1;
            provider.SetViewport(fixture, 0, ThumbHeight, 2 * Second, 3 * Second, FilmstripProvider.BaseIntervalTicks);

            var snapshot = provider.GetOrStart(fixture, 0, ThumbHeight);
            Assert.Null(snapshot.Error);
            Assert.True(snapshot.IsComplete);
            Assert.Equal(2, queue.Priorities.Count(p => p == FilmstripProvider.RefinePriority));

            const long Interval = FilmstripProvider.BaseIntervalTicks;
            for (long slot = 2 * Second; slot <= 3 * Second; slot += Interval)
            {
                Assert.True(snapshot.TryGetNearest(slot, out var thumb));
                Assert.True(Math.Abs(thumb.SourceTicks - slot) <= Interval / 4,
                    $"grid slot {slot} is only covered by a thumbnail at {thumb.SourceTicks}");
            }
        }

        [Fact]
        public void Cache_stays_under_its_cap_and_keeps_the_viewport()
        {
            RequireFFmpeg();
            string fixture = ColorFixture();

            const int Cap = 6;
            var queue = new InlineQueue();
            using var provider = new FilmstripProvider(queue, maxThumbnails: Cap);

            provider.GetOrStart(fixture, 0, ThumbHeight);
            Assert.True(provider.GetOrStart(fixture, 0, ThumbHeight).Thumbnails.Count <= Cap);

            // a fine grid over the tail: far more slots than the cache may hold alongside the
            // keyframes from the start of the file
            provider.SetViewport(fixture, 0, ThumbHeight, 8 * Second, 10 * Second, FilmstripProvider.BaseIntervalTicks);

            var snapshot = provider.GetOrStart(fixture, 0, ThumbHeight);
            Assert.True(snapshot.Thumbnails.Count <= Cap,
                $"{snapshot.Thumbnails.Count} thumbnails retained with a cap of {Cap}");

            // what survives is what the viewport is on: the thumbnails at the head of the file are
            // the furthest away and go first
            Assert.All(snapshot.Thumbnails, t => Assert.True(t.SourceTicks >= 6 * Second,
                $"a thumbnail at {t.SourceTicks} survived eviction while the viewport was at 8-10 s"));
            Assert.Contains(snapshot.Thumbnails, t => t.SourceTicks >= 8 * Second);
        }

        [Fact]
        public void Dispose_drops_queued_work_without_decoding_it()
        {
            RequireFFmpeg();
            string fixture = ColorFixture();

            var queue = new ThreadQueue(open: false); // items are held before they run
            try
            {
                var provider = new FilmstripProvider(queue);
                Assert.Empty(provider.GetOrStart(fixture, 0, ThumbHeight).Thumbnails);

                var sw = Stopwatch.StartNew();
                provider.Dispose();
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds < 1500, $"Dispose took {sw.ElapsedMilliseconds} ms with nothing running");

                queue.Open();
            }
            finally
            {
                queue.Dispose(); // drains the worker
            }

            Assert.Equal(0, queue.Ran);
            Assert.Equal(1, queue.Dropped);
        }

        [Fact]
        public void Dispose_cancels_the_running_pass_and_goes_quiet()
        {
            RequireFFmpeg();
            string fixture = ColorFixture();

            var queue = new ThreadQueue(open: true);
            try
            {
                var provider = new FilmstripProvider(queue);
                int changes = 0;
                provider.Changed += (s, e) => Interlocked.Increment(ref changes);

                provider.GetOrStart(fixture, 0, ThumbHeight);
                Assert.True(queue.Started.Wait(TimeSpan.FromSeconds(10)), "the keyframe pass never started");

                var sw = Stopwatch.StartNew();
                provider.Dispose(); // waits for the work item to unwind, so this is the real latency
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds < 3000, $"Dispose took {sw.ElapsedMilliseconds} ms");

                // nothing keeps decoding (or announcing) behind a disposed provider
                Thread.Sleep(300);
                int settled = Volatile.Read(ref changes);
                Thread.Sleep(300);
                Assert.Equal(settled, Volatile.Read(ref changes));

                Assert.Same(FilmstripSnapshot.Empty, provider.GetOrStart(fixture, 0, ThumbHeight));
                provider.Dispose(); // idempotent
            }
            finally
            {
                queue.Dispose();
            }
        }
    }
}
