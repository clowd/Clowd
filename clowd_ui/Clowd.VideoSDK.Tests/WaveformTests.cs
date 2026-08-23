using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Thumbs;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The waveform service, end to end over a real decode: a fixture whose amplitude is known
    // exactly (a 0.25 sine) is the only way to check that the mono fold, the bucket reduction and
    // the sbyte quantization all preserve the level the UI draws against half a row height. The
    // cache tests then hold the file format to its one job — being ignored whenever it might be
    // describing a different recording.
    //
    // Skips when the FFmpeg natives are absent (same resolver as the other decode tests).
    public class WaveformTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const int Rate = 48000;
        private const float Amplitude = 0.25f;
        private const double ToneHz = 440;
        private const int FixtureSeconds = 4;

        /// <summary>Mp4Writer adds video first, so the audio is stream 1.</summary>
        private const int AudioStream = 1;

        private static bool FFmpegAvailable => TestFFmpeg.Available;


        private static void RequireFFmpeg() =>
            Assert.SkipUnless(FFmpegAvailable,
                TestFFmpeg.SkipReason);

        // ----------------------------------------------------------------------------- fixture

        private readonly List<string> _tempFiles = new List<string>();
        private readonly List<string> _tempDirs = new List<string>();
        private string _fixture;

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }

            foreach (var d in _tempDirs)
            {
                try { Directory.Delete(d, recursive: true); }
                catch { /* best effort */ }
            }
        }

        /// <summary>A 4 s mp4 whose audio is a steady 440 Hz stereo sine at 0.25 — one bucket
        /// (5 ms) spans two full cycles, so every bucket's min/max IS the tone's amplitude.</summary>
        private string SineFixture()
        {
            if (_fixture != null)
                return _fixture;

            string path = Path.Combine(Path.GetTempPath(), $"clowd-waveform-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);

            using (var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
                Audio = new Mp4AudioOptions { SampleRate = Rate, Channels = 2 },
            }))
            {
                var bgra = new byte[W * H * 4];
                var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                try
                {
                    for (int n = 0; n < Fps * FixtureSeconds; n++)
                        writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
                }
                finally
                {
                    pin.Free();
                }

                int total = Rate * FixtureSeconds;
                var buf = new float[total * 2];
                for (int i = 0; i < total; i++)
                {
                    float s = Amplitude * (float)Math.Sin(2 * Math.PI * ToneHz * i / Rate);
                    buf[i * 2] = s;
                    buf[i * 2 + 1] = s; // identical channels: the mono fold keeps the amplitude
                }
                writer.SubmitAudioSamples(buf, total);
                writer.Finish();
            }

            _fixture = path;
            return path;
        }

        private string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"clowd-waveform-cache-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        private static WaveformSnapshot BuildWaveform(string path)
        {
            var buffer = new WaveformBuffer(WaveformProvider.BucketsPerSecond);
            Assert.True(WaveformBuilder.Build(path, AudioStream, buffer, null, CancellationToken.None));
            return buffer.Snapshot;
        }

        /// <summary>Polls the provider the way the timeline does — every call is a fresh ask —
        /// until the stream's waveform is complete.</summary>
        private static WaveformSnapshot WaitForComplete(WaveformProvider provider, string path, string cacheDir,
            int timeoutMs = 30_000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                var snapshot = provider.GetOrStart(path, AudioStream, cacheDir);
                if (snapshot.IsComplete)
                    return snapshot;

                Assert.True(Environment.TickCount64 < deadline,
                    $"the waveform never completed (ready {snapshot.ReadyBuckets} buckets, error {provider.Error})");
                Thread.Sleep(20);
            }
        }

        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (!condition())
            {
                if (Environment.TickCount64 >= deadline)
                    return false;
                Thread.Sleep(10);
            }

            return true;
        }

        /// <summary>The provider publishes the completed snapshot before it writes the cache file,
        /// so waiting for completion is not waiting for the write — poll for the file rather than
        /// racing the scheduler thread that saves it.</summary>
        private static void AssertCached(string dir, string path)
        {
            string file = Path.Combine(dir, WaveformCache.FileNameFor(path, AudioStream));
            Assert.True(WaitFor(() => File.Exists(file), 5000), $"the provider never wrote {file}");
        }

        private static void AssertToneAmplitude(WaveformSnapshot snapshot)
        {
            // 1 s .. 3 s: past the aac priming at either end, every bucket is pure tone
            for (int i = 200; i < 600; i++)
            {
                Assert.True(snapshot.TryGetBucket(i, out float min, out float max), $"bucket {i} missing");
                Assert.InRange(max, 0.21f, 0.29f);
                Assert.InRange(min, -0.29f, -0.21f);
            }
        }

        // ------------------------------------------------------------------------------- build

        [Fact]
        public void Sine_fixture_buckets_carry_the_source_amplitude()
        {
            RequireFFmpeg();

            var snapshot = BuildWaveform(SineFixture());

            Assert.True(snapshot.IsComplete);
            Assert.Equal(200, snapshot.BucketsPerSecond);
            Assert.Equal(50_000L, snapshot.TicksPerBucket); // 5 ms, exact at 200 buckets/s
            // 4 s at 200 buckets/s, plus the aac encoder's priming/padding at the tail
            Assert.InRange(snapshot.ReadyBuckets, 780, 880);
            AssertToneAmplitude(snapshot);

            // the accessor is silent (and false) outside what has been analyzed
            Assert.False(snapshot.TryGetBucket(snapshot.ReadyBuckets, out float m, out float x));
            Assert.Equal(0f, m);
            Assert.Equal(0f, x);
        }

        [Fact]
        public void Bucket_indexing_round_trips_source_time()
        {
            var snapshot = WaveformSnapshot.Empty;

            Assert.Equal(0, snapshot.BucketAt(0));
            Assert.Equal(0, snapshot.BucketAt(49_999));
            Assert.Equal(1, snapshot.BucketAt(50_000));
            Assert.Equal(200, snapshot.BucketAt(TimeSpan.TicksPerSecond));
            Assert.Equal(TimeSpan.TicksPerSecond, snapshot.BucketStartTicks(200));
            // an hour in: the index still comes back exactly, with no double arithmetic in between
            Assert.Equal(720_000, snapshot.BucketAt(TimeSpan.TicksPerHour));
        }

        [Fact]
        public void Cancellation_stops_the_build_promptly()
        {
            RequireFFmpeg();
            string path = SineFixture();

            using var cts = new CancellationTokenSource();
            var buffer = new WaveformBuffer(WaveformProvider.BucketsPerSecond);

            var sw = Stopwatch.StartNew();
            // cancel from the first progress callback: the token is checked once per decode chunk,
            // so the pass must stop at the next one rather than at the end of the file.
            bool complete = WaveformBuilder.Build(path, AudioStream, buffer, () => cts.Cancel(), cts.Token);
            sw.Stop();

            Assert.False(complete);
            Assert.False(buffer.Snapshot.IsComplete);
            Assert.InRange(buffer.Snapshot.ReadyBuckets, 1, 100); // nowhere near the fixture's 800
            Assert.True(sw.ElapsedMilliseconds < 5000, $"cancellation took {sw.ElapsedMilliseconds} ms");
        }

        // ------------------------------------------------------------------------------- cache

        [Fact]
        public void Cache_round_trips_the_waveform()
        {
            RequireFFmpeg();
            string path = SineFixture();
            string dir = TempDir();

            var built = BuildWaveform(path);
            Assert.True(WaveformCache.TrySave(dir, path, AudioStream, built));
            Assert.True(File.Exists(Path.Combine(dir, WaveformCache.FileNameFor(path, AudioStream))));

            var loaded = WaveformCache.TryLoad(dir, path, AudioStream, WaveformProvider.BucketsPerSecond);

            Assert.NotNull(loaded);
            Assert.True(loaded.IsComplete);
            Assert.Equal(built.BucketsPerSecond, loaded.BucketsPerSecond);
            Assert.Equal(built.ReadyBuckets, loaded.ReadyBuckets);
            for (int i = 0; i < built.ReadyBuckets; i++)
            {
                Assert.True(built.TryGetBucket(i, out float expectedMin, out float expectedMax));
                Assert.True(loaded.TryGetBucket(i, out float actualMin, out float actualMax));
                Assert.Equal(expectedMin, actualMin);
                Assert.Equal(expectedMax, actualMax);
            }

            AssertToneAmplitude(loaded);
        }

        [Fact]
        public void Cache_is_rejected_when_the_source_changes()
        {
            RequireFFmpeg();
            string path = SineFixture();
            string dir = TempDir();

            Assert.True(WaveformCache.TrySave(dir, path, AudioStream, BuildWaveform(path)));
            Assert.NotNull(WaveformCache.TryLoad(dir, path, AudioStream, WaveformProvider.BucketsPerSecond));

            // a waveform built at another resolution is not the one being asked for
            Assert.Null(WaveformCache.TryLoad(dir, path, AudioStream, 100));

            // re-recorded in place: same name, different content — the peaks are not this file's
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

            Assert.Null(WaveformCache.TryLoad(dir, path, AudioStream, WaveformProvider.BucketsPerSecond));
        }

        [Fact]
        public void Corrupt_and_truncated_cache_files_are_ignored()
        {
            RequireFFmpeg();
            string path = SineFixture();
            string dir = TempDir();
            string cacheFile = Path.Combine(dir, WaveformCache.FileNameFor(path, AudioStream));

            Assert.True(WaveformCache.TrySave(dir, path, AudioStream, BuildWaveform(path)));

            // truncated mid-body: the header promises buckets the file does not have
            var full = File.ReadAllBytes(cacheFile);
            File.WriteAllBytes(cacheFile, full.AsSpan(0, full.Length / 2).ToArray());
            Assert.Null(WaveformCache.TryLoad(dir, path, AudioStream, WaveformProvider.BucketsPerSecond));

            // garbage where the magic should be
            File.WriteAllBytes(cacheFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.Null(WaveformCache.TryLoad(dir, path, AudioStream, WaveformProvider.BucketsPerSecond));

            // header only
            File.WriteAllBytes(cacheFile, Array.Empty<byte>());
            Assert.Null(WaveformCache.TryLoad(dir, path, AudioStream, WaveformProvider.BucketsPerSecond));
        }

        [Fact]
        public void No_cache_directory_means_no_cache_file()
        {
            RequireFFmpeg();
            string path = SineFixture();

            Assert.False(WaveformCache.TrySave(null, path, AudioStream, BuildWaveform(path)));
            Assert.Null(WaveformCache.TryLoad(null, path, AudioStream, WaveformProvider.BucketsPerSecond));
        }

        // ---------------------------------------------------------------------------- provider

        [Fact]
        public void Provider_builds_once_then_serves_the_cache()
        {
            RequireFFmpeg();
            string path = SineFixture();
            string dir = TempDir();

            int changed = 0;
            using (var provider = new WaveformProvider())
            {
                provider.Changed += (s, e) => Interlocked.Increment(ref changed);

                Assert.NotNull(provider.GetOrStart(path, AudioStream, dir)); // never blocks

                var snapshot = WaitForComplete(provider, path, dir);
                Assert.Null(provider.Error);
                Assert.InRange(snapshot.ReadyBuckets, 780, 880);
                AssertToneAmplitude(snapshot);
                Assert.Equal(1, provider.BuildCount);
                Assert.Equal(0, provider.CacheHitCount);
                AssertCached(dir, path);
                // the event is throttled and raised on a thread-pool thread — give it its window
                Assert.True(WaitFor(() => Volatile.Read(ref changed) > 0, 5000),
                    "the provider never announced its progress");
            }

            using (var second = new WaveformProvider())
            {
                var snapshot = WaitForComplete(second, path, dir);
                Assert.Equal(0, second.BuildCount); // the whole point of the disk cache
                Assert.Equal(1, second.CacheHitCount);
                AssertToneAmplitude(snapshot);
            }
        }

        [Fact]
        public void Provider_regenerates_when_the_source_moved_on()
        {
            RequireFFmpeg();
            string path = SineFixture();
            string dir = TempDir();

            using (var provider = new WaveformProvider())
                WaitForComplete(provider, path, dir);

            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

            using (var provider = new WaveformProvider())
            {
                var snapshot = WaitForComplete(provider, path, dir);
                Assert.Equal(1, provider.BuildCount);
                Assert.Equal(0, provider.CacheHitCount);
                AssertToneAmplitude(snapshot);
            }

            // the regenerated cache describes the touched file, so the next open hits it
            using (var provider = new WaveformProvider())
            {
                WaitForComplete(provider, path, dir);
                Assert.Equal(0, provider.BuildCount);
            }
        }

        [Fact]
        public void Provider_regenerates_over_a_corrupt_cache_file()
        {
            RequireFFmpeg();
            string path = SineFixture();
            string dir = TempDir();
            File.WriteAllBytes(Path.Combine(dir, WaveformCache.FileNameFor(path, AudioStream)),
                new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

            using var provider = new WaveformProvider();
            var snapshot = WaitForComplete(provider, path, dir);

            Assert.Equal(1, provider.BuildCount);
            AssertToneAmplitude(snapshot);
            // the bad file was replaced, not left to fail on every open (the replacement lands
            // just after the snapshot the wait above returned, so poll for it)
            Assert.True(WaitFor(() => WaveformCache.TryLoad(dir, path, AudioStream,
                                                           WaveformProvider.BucketsPerSecond) != null, 5000),
                        "the corrupt cache file was never replaced");
        }

        /// <summary>Stream indices are container-relative, so a recording plus an imported mp4
        /// (audio at stream 1 in both) share an index — with an index-only file name they would
        /// clobber each other's cache on every open, forever, and the disk cache would be dead for
        /// any project with an import.</summary>
        [Fact]
        public void Two_sources_sharing_a_stream_index_keep_separate_caches()
        {
            RequireFFmpeg();
            string first = SineFixture();
            string second = Path.Combine(Path.GetTempPath(), $"clowd-waveform-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(second);
            File.Copy(first, second);
            string dir = TempDir();

            Assert.NotEqual(WaveformCache.FileNameFor(first, AudioStream),
                WaveformCache.FileNameFor(second, AudioStream));

            using (var provider = new WaveformProvider())
            {
                AssertToneAmplitude(WaitForComplete(provider, first, dir));
                AssertToneAmplitude(WaitForComplete(provider, second, dir));
                Assert.Equal(2, provider.BuildCount);
            }

            AssertCached(dir, first);
            AssertCached(dir, second);

            // the reopen both caches exist for: neither source rebuilds, neither overwrote the other
            using (var provider = new WaveformProvider())
            {
                AssertToneAmplitude(WaitForComplete(provider, first, dir));
                AssertToneAmplitude(WaitForComplete(provider, second, dir));
                Assert.Equal(0, provider.BuildCount);
                Assert.Equal(2, provider.CacheHitCount);
            }
        }

        /// <summary>An explicit cache key (the editor passes the model's Source.Id) names the file
        /// instead of the path hash, so the cache survives the source file moving with its
        /// session directory.</summary>
        [Fact]
        public void An_explicit_cache_key_names_the_file_and_survives_a_path_change()
        {
            RequireFFmpeg();
            string path = SineFixture();
            string dir = TempDir();
            const string Key = "0123456789abcdef0123456789abcdef";

            Assert.True(WaveformCache.TrySave(dir, path, AudioStream, BuildWaveform(path), Key));
            Assert.True(File.Exists(Path.Combine(dir, WaveformCache.FileNameFor(path, AudioStream, Key))));
            Assert.Equal($"waveform-{Key}-1.cwf", WaveformCache.FileNameFor(path, AudioStream, Key));

            // a moved file reached through another spelling still hits, because the key — not the
            // path — names the file (the length/mtime check inside still guards the content).
            string movedSpelling = path.ToUpperInvariant();
            var loaded = WaveformCache.TryLoad(dir, movedSpelling, AudioStream,
                WaveformProvider.BucketsPerSecond, Key);
            if (OperatingSystem.IsWindows()) // the uppercased path only exists on case-insensitive file systems
                Assert.NotNull(loaded);
        }

        [Fact]
        public void A_stream_that_cannot_be_decoded_settles_as_a_flat_line()
        {
            RequireFFmpeg();
            string missing = Path.Combine(Path.GetTempPath(), $"clowd-waveform-missing-{Guid.NewGuid():N}.mp4");

            using var provider = new WaveformProvider();
            var snapshot = WaitForComplete(provider, missing, cacheDir: null, timeoutMs: 10_000);

            Assert.Equal(0, snapshot.ReadyBuckets); // complete and empty: the row draws flat
            Assert.NotNull(provider.Error);
        }

        // --------------------------------------------------------------------------- scheduler

        [Fact]
        public void Scheduler_runs_the_bands_in_priority_order()
        {
            using var scheduler = new ThumbWorkScheduler();
            using var gate = new ManualResetEventSlim(false);
            var order = new List<string>();

            // occupies the thread while the rest of the queue fills, so ordering is the queue's
            // decision and not a race with the worker starting
            var blocker = scheduler.Enqueue(ThumbWorkPriority.Waveform, _ => gate.Wait(5000));
            var refinement = scheduler.Enqueue(ThumbWorkPriority.Refinement, _ => order.Add("refinement"));
            var keyframes = scheduler.Enqueue(ThumbWorkPriority.Keyframes, _ => order.Add("keyframes"));
            var waveform = scheduler.Enqueue(ThumbWorkPriority.Waveform, _ => order.Add("waveform"));

            gate.Set();
            Assert.True(blocker.Wait(5000));
            Assert.True(refinement.Wait(5000));

            Assert.Equal(new[] { "waveform", "keyframes", "refinement" }, order);
            Assert.Null(waveform.Error);
            Assert.Null(keyframes.Error);
            Assert.Equal(0, scheduler.PendingCount);
        }

        /// <summary>The probe long filmstrip items use to yield the single thread: it must see
        /// queued work strictly below a band, and nothing else — its own band never counts, or a
        /// parked item would starve against its own replacement.</summary>
        [Fact]
        public void Scheduler_reports_queued_work_below_a_band()
        {
            using var scheduler = new ThumbWorkScheduler();
            using var started = new ManualResetEventSlim(false);
            using var gate = new ManualResetEventSlim(false);

            // occupy the thread so everything after this stays queued deterministically
            var blocker = scheduler.Enqueue(ThumbWorkPriority.Waveform, _ =>
            {
                started.Set();
                gate.Wait(5000);
            });
            Assert.True(started.Wait(5000), "the blocker never started");

            Assert.False(scheduler.HasPendingBelow(Int32.MaxValue)); // the queue is empty

            var refinement = scheduler.Enqueue(ThumbWorkPriority.Refinement, _ => { });
            Assert.False(scheduler.HasPendingBelow((int)ThumbWorkPriority.Refinement)); // its own band does not count
            Assert.True(scheduler.HasPendingBelow((int)ThumbWorkPriority.Refinement + 1));

            var waveform = scheduler.Enqueue(ThumbWorkPriority.Waveform, _ => { });
            Assert.True(scheduler.HasPendingBelow((int)ThumbWorkPriority.Keyframes));

            gate.Set();
            Assert.True(blocker.Wait(5000));
            Assert.True(waveform.Wait(5000));
            Assert.True(refinement.Wait(5000));
            Assert.False(scheduler.HasPendingBelow(Int32.MaxValue));
        }

        [Fact]
        public void Scheduler_drops_canceled_work_and_survives_a_throwing_item()
        {
            using var scheduler = new ThumbWorkScheduler();
            using var gate = new ManualResetEventSlim(false);
            bool ran = false;

            var blocker = scheduler.Enqueue(ThumbWorkPriority.Waveform, _ => gate.Wait(5000));
            var thrower = scheduler.Enqueue(ThumbWorkPriority.Keyframes,
                _ => throw new InvalidOperationException("boom"));
            var canceled = scheduler.Enqueue(ThumbWorkPriority.Keyframes, _ => ran = true);
            var after = scheduler.Enqueue(ThumbWorkPriority.Refinement, _ => { });

            canceled.Cancel();
            gate.Set();

            Assert.True(after.Wait(5000)); // the throwing item did not take the thread down
            Assert.False(ran);
            Assert.True(canceled.IsCanceled);
            Assert.IsType<InvalidOperationException>(thrower.Error);
            Assert.True(blocker.IsFinished);
        }

        [Fact]
        public void Scheduler_gives_raw_priority_work_a_token_its_handle_cancels()
        {
            using var scheduler = new ThumbWorkScheduler();
            using var started = new ManualResetEventSlim(false);
            using var observed = new ManualResetEventSlim(false);
            bool sawCancellation = false;

            // the shape the filmstrip abandons stale viewport work through
            IThumbWorkQueue queue = scheduler;
            var handle = queue.Enqueue((int)ThumbWorkPriority.Refinement, ct =>
            {
                started.Set();
                long deadline = Environment.TickCount64 + 5000;
                while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
                    Thread.Sleep(5);
                sawCancellation = ct.IsCancellationRequested;
                observed.Set();
            });

            Assert.True(started.Wait(5000));
            handle.Dispose();

            Assert.True(observed.Wait(5000));
            Assert.True(sawCancellation, "disposing the handle must cancel work that is already running");
        }

        [Fact]
        public void Scheduler_skips_work_whose_token_is_already_canceled()
        {
            using var scheduler = new ThumbWorkScheduler();
            using var cts = new CancellationTokenSource();
            bool ran = false;

            cts.Cancel();
            var handle = scheduler.Enqueue(ThumbWorkPriority.Waveform, _ => ran = true, cts.Token);

            Assert.True(handle.Wait(5000));
            Assert.False(ran);
            Assert.Null(handle.Error);
        }
    }
}
