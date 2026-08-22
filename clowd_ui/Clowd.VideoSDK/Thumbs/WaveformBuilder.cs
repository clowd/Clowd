using System;
using System.Threading;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Media;

namespace Clowd.VideoSDK.Thumbs
{
    /// <summary>
    /// An immutable view of one audio stream's waveform: mono-folded min/max peaks, one pair per
    /// bucket, ascending from source time 0. Snapshots are handed out while the analysis is still
    /// running — <see cref="ReadyBuckets"/> grows and <see cref="IsComplete"/> flips when the pass
    /// reaches the end of the stream — so a row can draw the part that exists without waiting for
    /// the rest.
    ///
    /// <para>
    /// Peaks are stored as <see cref="sbyte"/> pairs (a 30-minute stream is ~720 KB at the default
    /// 200 buckets/s) and surfaced as floats in <c>[-1, 1]</c>, which is the domain the timeline's
    /// <c>AudioPeaks</c> contract draws straight against half a row height.
    /// </para>
    /// </summary>
    public sealed class WaveformSnapshot
    {
        /// <summary>Quantization scale of the stored pairs: a sample of ±1.0 stores as ±127, so
        /// the sbyte range stays symmetric about zero (−128 is never produced).</summary>
        internal const int Scale = 127;

        /// <summary>Nothing analyzed yet — what a provider returns for a stream whose pass has not
        /// produced its first bucket.</summary>
        public static readonly WaveformSnapshot Empty =
            new WaveformSnapshot(WaveformBuilder.DefaultBucketsPerSecond, Array.Empty<sbyte>(), 0, false);

        private readonly sbyte[] _pairs;

        internal WaveformSnapshot(int bucketsPerSecond, sbyte[] pairs, int readyBuckets, bool isComplete)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bucketsPerSecond, 0);

            _pairs = pairs ?? Array.Empty<sbyte>();
            BucketsPerSecond = bucketsPerSecond;
            ReadyBuckets = Math.Clamp(readyBuckets, 0, _pairs.Length / 2);
            IsComplete = isComplete;
        }

        /// <summary>Buckets per second of source time — the resolution the peaks were built at.
        /// The timeline re-buckets from this to whatever the current zoom needs.</summary>
        public int BucketsPerSecond { get; }

        /// <summary>Buckets analyzed so far, from source time 0.</summary>
        public int ReadyBuckets { get; }

        /// <summary>True once the pass has reached the end of the stream, so the consumer can stop
        /// re-asking.</summary>
        public bool IsComplete { get; }

        /// <summary>Nominal bucket width. Exact whenever <see cref="BucketsPerSecond"/> divides
        /// 10^7 (it does at the default 200); <see cref="BucketStartTicks"/> is the authority.</summary>
        public long TicksPerBucket => TimeBase.TicksPerSecond / BucketsPerSecond;

        /// <summary>Source time analyzed so far.</summary>
        public long ReadyTicks => BucketStartTicks(ReadyBuckets);

        /// <summary>Source time bucket <paramref name="index"/> starts at.</summary>
        public long BucketStartTicks(int index) =>
            (long)index * TimeBase.TicksPerSecond / BucketsPerSecond;

        /// <summary>The bucket covering <paramref name="sourceTicks"/> (may be past
        /// <see cref="ReadyBuckets"/>; <see cref="TryGetBucket"/> reports silence there).</summary>
        public int BucketAt(long sourceTicks)
        {
            if (sourceTicks <= 0)
                return 0;

            long index = sourceTicks / TimeBase.TicksPerSecond * BucketsPerSecond
                         + (sourceTicks % TimeBase.TicksPerSecond) * BucketsPerSecond / TimeBase.TicksPerSecond;
            return (int)Math.Min(index, Int32.MaxValue);
        }

        /// <summary>Reads one bucket's mono-folded peaks. Returns false (and silence) outside the
        /// analyzed range, so callers can walk a pixel range without bounds-checking every step —
        /// the same shape the timeline's own <c>AudioPeaks</c> accessor has.</summary>
        public bool TryGetBucket(int index, out float min, out float max)
        {
            if (index < 0 || index >= ReadyBuckets)
            {
                min = 0;
                max = 0;
                return false;
            }

            min = _pairs[index * 2] / (float)Scale;
            max = _pairs[index * 2 + 1] / (float)Scale;
            return true;
        }

        /// <summary>The raw pairs, for the disk cache. Valid up to <c>ReadyBuckets * 2</c>.</summary>
        internal sbyte[] Pairs => _pairs;
    }

    /// <summary>
    /// The growable side of a waveform: the analysis thread appends buckets here and publishes,
    /// readers take <see cref="Snapshot"/> from any thread. Publishing is what makes appended
    /// buckets visible — the array is only ever written past the last published bucket, and growth
    /// copies into a NEW array (the old one is what already-published snapshots hold), so a
    /// snapshot's prefix is immutable for its lifetime without any locking.
    /// </summary>
    internal sealed class WaveformBuffer
    {
        private const int MinCapacityBuckets = 4096;

        private readonly int _bucketsPerSecond;
        private sbyte[] _pairs;
        private int _count;
        private WaveformSnapshot _snapshot;

        public WaveformBuffer(int bucketsPerSecond)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bucketsPerSecond, 0);

            _bucketsPerSecond = bucketsPerSecond;
            _pairs = Array.Empty<sbyte>();
            _snapshot = new WaveformSnapshot(bucketsPerSecond, _pairs, 0, false);
        }

        public int BucketsPerSecond => _bucketsPerSecond;

        /// <summary>Buckets appended (writer thread's view — may be ahead of the last publish).</summary>
        public int Count => _count;

        public WaveformSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public void Append(sbyte min, sbyte max)
        {
            EnsureCapacity(_count + 1);
            _pairs[_count * 2] = min;
            _pairs[_count * 2 + 1] = max;
            _count++;
        }

        /// <summary>Appends flat buckets — a timestamp gap in the stream is silence, not a shift of
        /// everything after it.</summary>
        public void AppendSilence(int buckets)
        {
            if (buckets <= 0)
                return;

            EnsureCapacity(_count + buckets);
            Array.Clear(_pairs, _count * 2, buckets * 2);
            _count += buckets;
        }

        /// <summary>Makes everything appended so far visible to <see cref="Snapshot"/> readers.
        /// The volatile write is also the release barrier that publishes the bucket data.</summary>
        public void Publish(bool isComplete = false) =>
            Volatile.Write(ref _snapshot, new WaveformSnapshot(_bucketsPerSecond, _pairs, _count, isComplete));

        private void EnsureCapacity(int buckets)
        {
            if (_pairs.Length >= buckets * 2)
                return;

            int capacity = Math.Max(MinCapacityBuckets, Math.Max(buckets, _count * 2));
            var grown = new sbyte[capacity * 2];
            Array.Copy(_pairs, grown, _count * 2);
            _pairs = grown;
        }
    }

    /// <summary>
    /// Builds an audio stream's waveform in one forward decode pass over its own
    /// <c>AVFormatContext</c> (a <see cref="SyncAudioStreamDecoder"/> of its own, so analysis never
    /// contends with playback's decoders), folding the decoded stereo to mono and reducing it to
    /// per-bucket min/max at <see cref="DefaultBucketsPerSecond"/>.
    ///
    /// <para>
    /// Positioning follows the decoder's reported timestamps rather than assuming contiguity: a
    /// forward jump pads whole silent buckets so peaks stay aligned to source time, and a backwards
    /// timestamp is clamped (the decode head never rewinds in a forward pass).
    /// </para>
    /// </summary>
    internal static class WaveformBuilder
    {
        /// <summary>Roughly one bucket per 5 ms — fine enough that the timeline is re-bucketing
        /// (never interpolating) at every zoom level it offers, and ~400 bytes per second of audio.</summary>
        public const int DefaultBucketsPerSecond = 200;

        /// <summary>Analysis decodes at a fixed rate; peaks do not care about the project's output
        /// rate, and a fixed rate keeps the bucket arithmetic exact.</summary>
        public const int DecodeSampleRate = 48000;

        /// <summary>Hard ceiling (12 hours) so a stream with nonsense timestamps cannot pad its way
        /// into an unbounded allocation.</summary>
        public const int MaxBuckets = DefaultBucketsPerSecond * 60 * 60 * 12;

        private const int Channels = 2; // SyncAudioStreamDecoder always converts to stereo

        /// <summary>
        /// Runs the pass into <paramref name="buffer"/>, publishing (and calling
        /// <paramref name="onProgress"/>) after every decoded chunk. Returns true when the stream
        /// was analyzed to its end — the buffer's final publish is marked complete — and false when
        /// <paramref name="cancellationToken"/> stopped it, in which case the buffer keeps whatever
        /// was published and stays incomplete.
        /// </summary>
        public static bool Build(string path, int streamIndex, WaveformBuffer buffer, Action onProgress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(buffer);

            int bps = buffer.BucketsPerSecond;
            using var decoder = new SyncAudioStreamDecoder(path, streamIndex, DecodeSampleRate);

            long nextSample = 0;        // decode head, in sample frames from the stream's own zero
            bool positioned = false;
            float min = 0, max = 0;
            bool bucketHasSamples = false;

            while (buffer.Count < MaxBuckets)
            {
                // one check per decode chunk (~20 ms of audio): a closing editor stops within a
                // chunk, not at the end of the file.
                if (cancellationToken.IsCancellationRequested)
                    return false;

                if (!decoder.DecodeNext(out long ptsTicks, out float[] samples, out int frames))
                    break;
                if (frames <= 0)
                    continue;

                long start = nextSample;
                if (ptsTicks == long.MinValue)
                {
                    positioned = true; // resampler flush chunk: contiguous with the head by contract
                }
                else
                {
                    long pts = AudioTime.SamplesNearest(ptsTicks, DecodeSampleRate);
                    if (!positioned)
                    {
                        start = Math.Max(0, pts);
                        positioned = true;
                    }
                    else if (pts > nextSample)
                    {
                        start = pts;
                    }
                }

                if (start > nextSample)
                {
                    if (bucketHasSamples)
                    {
                        buffer.Append(Quantize(min), Quantize(max));
                        bucketHasSamples = false;
                    }

                    long targetBucket = Math.Min(MaxBuckets, SampleToBucket(start, bps));
                    if (targetBucket > buffer.Count)
                        buffer.AppendSilence((int)(targetBucket - buffer.Count));
                    nextSample = start;
                }

                int i = 0;
                while (i < frames && buffer.Count < MaxBuckets)
                {
                    long bucketEnd = BucketEndSample(buffer.Count, bps);
                    if (nextSample >= bucketEnd)
                    {
                        // the head landed past the open bucket (gap padding rounds to whole
                        // buckets): close it and re-derive the boundary.
                        buffer.Append(Quantize(min), Quantize(max));
                        bucketHasSamples = false;
                        min = 0;
                        max = 0;
                        continue;
                    }

                    int take = (int)Math.Min(frames - i, bucketEnd - nextSample);
                    int end = (i + take) * Channels;
                    for (int s = i * Channels; s < end; s += Channels)
                    {
                        float v = (samples[s] + samples[s + 1]) * 0.5f; // mono fold
                        if (!bucketHasSamples)
                        {
                            min = v;
                            max = v;
                            bucketHasSamples = true;
                        }
                        else if (v < min)
                        {
                            min = v;
                        }
                        else if (v > max)
                        {
                            max = v;
                        }
                    }

                    i += take;
                    nextSample += take;

                    if (nextSample >= bucketEnd)
                    {
                        buffer.Append(Quantize(min), Quantize(max));
                        bucketHasSamples = false;
                        min = 0;
                        max = 0;
                    }
                }

                buffer.Publish();
                onProgress?.Invoke();
            }

            if (bucketHasSamples && buffer.Count < MaxBuckets)
                buffer.Append(Quantize(min), Quantize(max)); // the stream's short final bucket

            buffer.Publish(isComplete: true);
            return true;
        }

        /// <summary>The bucket a sample position falls in.</summary>
        private static long SampleToBucket(long sample, int bucketsPerSecond) =>
            sample * bucketsPerSecond / DecodeSampleRate;

        /// <summary>First sample of the bucket after <paramref name="bucketIndex"/> — computed from
        /// the index every time, so bucket widths never drift on rates that do not divide evenly.</summary>
        private static long BucketEndSample(int bucketIndex, int bucketsPerSecond) =>
            ((long)bucketIndex + 1) * DecodeSampleRate / bucketsPerSecond;

        private static sbyte Quantize(float value)
        {
            if (Single.IsNaN(value))
                return 0;

            int q = (int)MathF.Round(value * WaveformSnapshot.Scale);
            return (sbyte)Math.Clamp(q, -WaveformSnapshot.Scale, WaveformSnapshot.Scale);
        }
    }
}
