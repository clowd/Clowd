using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Playback;

namespace Clowd.VideoSDK.Render
{
    /// <summary>
    /// The render pipeline's warp-aware resampling stage: sits between <see cref="AudioMixer"/>
    /// (project-time sample frames) and the muxer (output-time sample frames), bending the mix
    /// through a <see cref="TimeWarp"/> the way <c>AudioMixWorker.MixResampledChunk</c> bends the
    /// preview mix through a constant rate — a per-sample project cursor advancing at the warp's
    /// local slope, linearly interpolating between neighboring project frames so pitch rides
    /// with the speed.
    ///
    /// <para>
    /// Where the warp does not bend time the stage gets out of the way <b>exactly</b>: an
    /// identity warp forwards <see cref="AudioMixer.MixChunk"/> straight into the caller's
    /// buffer, and inside any speed-1 span of a warped timeline output samples are verbatim
    /// copies of the project samples at that span's constant integer offset — no interpolation
    /// touches them, preserving the preview/render parity invariant for unwarped audio.
    /// </para>
    ///
    /// <para>
    /// Chunks must be requested in forward order (the mixer's sources are forward-only). Mixed
    /// project frames are held in a carry window across chunk and span boundaries so the mixer is
    /// asked for each project frame exactly once — the interpolation's right neighbor is needed
    /// again by the next chunk, and re-requesting it would be a backwards read.
    /// </para>
    /// </summary>
    public sealed class WarpAudioResampler
    {
        private const int Channels = AudioMixer.Channels;

        /// <summary>Project frames kept behind the cursor when the window advances: span
        /// boundaries quantize the cursor two ways (integer offsets vs tick-mapped positions),
        /// so a join can step back a fraction of a frame — the guard keeps those frames
        /// readable instead of degrading to a clamped duplicate.</summary>
        private const int GuardFrames = 4;

        private readonly AudioMixer _mixer;
        private readonly TimeWarp _warp;
        private readonly int _rate;
        private readonly SampleSpan[] _spans;
        private int _spanIndex;
        private long _nextOutputFrame;

        // project-frame carry window [_windowStart, _windowStart + _windowFrames)
        private float[] _window = Array.Empty<float>();
        private long _windowStart;
        private int _windowFrames;
        private float[] _scratch = Array.Empty<float>();
        private double[] _positions = Array.Empty<double>();
        private double _lastPos = -1; // monotonic clamp across span joins

        /// <summary>One warp segment rendered into the output sample-frame domain. Direct spans
        /// (speed 1) copy project frame <c>output + OffsetFrames</c> verbatim; other spans map
        /// each output sample's tick through <see cref="TimeWarp.ToProject"/>.</summary>
        private readonly struct SampleSpan
        {
            public SampleSpan(long outStart, long outEnd, bool direct, long offsetFrames)
            {
                OutStart = outStart;
                OutEnd = outEnd;
                Direct = direct;
                OffsetFrames = offsetFrames;
            }

            public long OutStart { get; }
            public long OutEnd { get; }      // exclusive; the trailing span is long.MaxValue
            public bool Direct { get; }
            public long OffsetFrames { get; }
        }

        public WarpAudioResampler(AudioMixer mixer, TimeWarp warp, int sampleRate)
        {
            ArgumentNullException.ThrowIfNull(mixer);
            ArgumentNullException.ThrowIfNull(warp);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

            _mixer = mixer;
            _warp = warp;
            _rate = sampleRate;

            if (warp.IsIdentity)
            {
                _spans = Array.Empty<SampleSpan>();
                return;
            }

            var spans = new List<SampleSpan>();
            long projectEndTicks = 0, outputEndTicks = 0;
            foreach (var seg in warp.Segments)
            {
                // segments tile in ticks, so ceiling both boundaries keeps the sample spans
                // contiguous (a segment narrower than one sample simply contributes none)
                long outStart = AudioTime.SamplesCeil(seg.OutputStartTicks, sampleRate);
                long outEnd = AudioTime.SamplesCeil(seg.OutputEndTicks, sampleRate);
                projectEndTicks = seg.ProjectEndTicks;
                outputEndTicks = seg.OutputEndTicks;
                if (outEnd <= outStart)
                    continue;

                bool direct = !seg.IsRamp && seg.Speed == 1.0;
                spans.Add(new SampleSpan(outStart, outEnd, direct, direct
                    ? AudioTime.SamplesNearest(seg.ProjectStartTicks - seg.OutputStartTicks, sampleRate)
                    : 0));
            }

            // past the last segment the warp continues at speed 1 — a trailing direct span
            spans.Add(new SampleSpan(AudioTime.SamplesCeil(outputEndTicks, sampleRate), long.MaxValue,
                true, AudioTime.SamplesNearest(projectEndTicks - outputEndTicks, sampleRate)));
            _spans = spans.ToArray();
        }

        /// <summary>
        /// Produces output samples [<paramref name="firstFrame"/>, <paramref name="firstFrame"/> +
        /// <paramref name="frames"/>) into <paramref name="dst"/> (interleaved stereo, length at
        /// least <c>frames * 2</c>; fully overwritten). Forward order only.
        /// </summary>
        public void ReadChunk(long firstFrame, int frames, float[] dst)
        {
            ArgumentNullException.ThrowIfNull(dst);
            ArgumentOutOfRangeException.ThrowIfNegative(frames);
            ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
            if ((long)frames * Channels > dst.Length)
                throw new ArgumentOutOfRangeException(nameof(frames),
                    $"{frames} stereo frames do not fit in a buffer of {dst.Length} floats.");
            if (firstFrame < _nextOutputFrame)
                throw new InvalidOperationException(
                    "Chunks must be requested in forward order — the mixer's sources are forward-only.");
            _nextOutputFrame = firstFrame + frames;

            if (_warp.IsIdentity)
            {
                _mixer.MixChunk(firstFrame, frames, dst);
                return;
            }

            int done = 0;
            while (done < frames)
            {
                long o = firstFrame + done;
                while (o >= _spans[_spanIndex].OutEnd)
                    _spanIndex++;
                var span = _spans[_spanIndex];

                int run = (int)Math.Min(frames - done, span.OutEnd - o);
                if (span.Direct)
                    CopyRun(o + span.OffsetFrames, run, dst, done * Channels);
                else
                    ResampleRun(o, run, dst, done * Channels);
                done += run;
            }
        }

        /// <summary>The speed-1 run: verbatim copy of project frames at the span's constant
        /// offset — no float math, so unwarped spans stay bit-exact against a straight mix.</summary>
        private void CopyRun(long firstProjectFrame, int run, float[] dst, int dstOffset)
        {
            EnsureWindow(firstProjectFrame, firstProjectFrame + run);
            for (int i = 0; i < run; i++)
            {
                int src = (int)(ClampToWindow(firstProjectFrame + i) - _windowStart) * Channels;
                int di = dstOffset + i * Channels;
                dst[di] = _window[src];
                dst[di + 1] = _window[src + 1];
            }
            _lastPos = Math.Max(_lastPos, firstProjectFrame + run - 1);
        }

        /// <summary>The warped run: each output sample's tick maps through the warp to a
        /// fractional project frame, clamped monotone across span joins, and interpolates
        /// between its two neighbors in the window.</summary>
        private void ResampleRun(long firstOutputFrame, int run, float[] dst, int dstOffset)
        {
            if (_positions.Length < run)
                _positions = new double[run];

            double last = _lastPos;
            for (int i = 0; i < run; i++)
            {
                long tick = AudioTime.TicksFloor(firstOutputFrame + i, _rate);
                double p = _warp.ToProject(tick) * (double)_rate / TimeBase.TicksPerSecond;
                if (p < last)
                    p = last;
                _positions[i] = p;
                last = p;
            }
            _lastPos = last;

            long firstNeeded = (long)Math.Floor(_positions[0]);
            long endNeeded = (long)Math.Floor(_positions[run - 1]) + 2; // right neighbor, exclusive
            EnsureWindow(firstNeeded, endNeeded);

            long windowLast = _windowStart + _windowFrames - 1;
            for (int i = 0; i < run; i++)
            {
                double p = _positions[i];
                long f0 = ClampToWindow((long)Math.Floor(p));
                long f1 = Math.Min(f0 + 1, windowLast);
                float frac = (float)(p - f0);
                if (frac < 0f)
                    frac = 0f;
                else if (frac > 1f)
                    frac = 1f;

                int b0 = (int)(f0 - _windowStart) * Channels;
                int b1 = (int)(f1 - _windowStart) * Channels;
                int di = dstOffset + i * Channels;
                for (int ch = 0; ch < Channels; ch++)
                {
                    float a = _window[b0 + ch];
                    float b = _window[b1 + ch];
                    dst[di + ch] = a + (b - a) * frac;
                }
            }
        }

        /// <summary>Drops window frames the cursor has passed (keeping <see cref="GuardFrames"/>
        /// of slack behind it) and mixes forward so the window covers
        /// [<paramref name="firstNeeded"/>, <paramref name="endNeeded"/>).</summary>
        private void EnsureWindow(long firstNeeded, long endNeeded)
        {
            if (firstNeeded < 0)
                firstNeeded = 0;

            long dropBelow = firstNeeded - GuardFrames;
            if (_windowFrames > 0 && dropBelow > _windowStart)
            {
                int drop = (int)Math.Min(dropBelow - _windowStart, _windowFrames);
                Array.Copy(_window, drop * Channels, _window, 0, (_windowFrames - drop) * Channels);
                _windowStart += drop;
                _windowFrames -= drop;
            }
            if (_windowFrames == 0 && firstNeeded > _windowStart)
                _windowStart = firstNeeded;

            long readStart = _windowStart + _windowFrames;
            int readCount = (int)Math.Max(0, endNeeded - readStart);
            if (readCount == 0)
                return;

            int haveFloats = _windowFrames * Channels;
            int needFloats = haveFloats + readCount * Channels;
            if (_window.Length < needFloats)
            {
                var grown = new float[Math.Max(needFloats, _window.Length * 2)];
                Array.Copy(_window, grown, haveFloats);
                _window = grown;
            }

            if (_scratch.Length < readCount * Channels)
                _scratch = new float[readCount * Channels];
            _mixer.MixChunk(readStart, readCount, _scratch);
            Array.Copy(_scratch, 0, _window, haveFloats, readCount * Channels);
            _windowFrames += readCount;
        }

        private long ClampToWindow(long frame)
        {
            if (frame < _windowStart)
                return _windowStart; // unreachable beyond the guard: joins regress under a frame
            long last = _windowStart + _windowFrames - 1;
            return frame > last ? last : frame;
        }
    }
}
