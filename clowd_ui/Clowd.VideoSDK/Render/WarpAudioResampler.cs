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
    /// through a <see cref="TimeWarp"/>. A warped span is rendered one of two ways, chosen by its
    /// speed item's <see cref="Model.SpeedContent.PitchCorrect"/>: pitch-corrected spans are
    /// time-stretched through a <see cref="WsolaStretcher"/> (time bends, pitch stays put), plain
    /// spans run a per-sample project cursor advancing at the warp's local slope, linearly
    /// interpolating between neighboring project frames so pitch rides with the speed — the same
    /// pair of algorithms <c>AudioMixWorker</c> plays in preview.
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
    /// project frames are held in a shared <see cref="MixerWindow"/> across chunk and span
    /// boundaries so the mixer is asked for each project frame exactly once — an interpolation's
    /// right neighbor, or a stretcher's alignment look-behind, is needed again by the next hop
    /// or chunk, and re-requesting it would be a backwards read.
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

        private readonly TimeWarp _warp;
        private readonly int _rate;
        private readonly SampleSpan[] _spans;
        private readonly MixerWindow _window;
        private int _spanIndex;
        private int _stretchSpanIndex = -1; // the span the stretcher's state belongs to
        private long _nextOutputFrame;

        private WsolaStretcher _stretcher;
        private Func<long, double> _targetOf;
        private double[] _positions = Array.Empty<double>();
        private double _lastPos = -1; // monotonic clamp across span joins

        /// <summary>One warp segment rendered into the output sample-frame domain. Direct spans
        /// (speed 1) copy project frame <c>output + OffsetFrames</c> verbatim; other spans map
        /// each output sample's tick through <see cref="TimeWarp.ToProject"/> — through the
        /// stretcher when <see cref="Pitch"/>, through linear interpolation otherwise.</summary>
        private readonly struct SampleSpan
        {
            public SampleSpan(long outStart, long outEnd, bool direct, long offsetFrames, bool pitch)
            {
                OutStart = outStart;
                OutEnd = outEnd;
                Direct = direct;
                OffsetFrames = offsetFrames;
                Pitch = pitch;
            }

            public long OutStart { get; }
            public long OutEnd { get; }      // exclusive; the trailing span is long.MaxValue
            public bool Direct { get; }
            public long OffsetFrames { get; }
            public bool Pitch { get; }
        }

        public WarpAudioResampler(AudioMixer mixer, TimeWarp warp, int sampleRate)
        {
            ArgumentNullException.ThrowIfNull(mixer);
            ArgumentNullException.ThrowIfNull(warp);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

            _warp = warp;
            _rate = sampleRate;
            _window = new MixerWindow(mixer, GuardFrames);

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
                    : 0, !direct && seg.PitchCorrect));
            }

            // past the last segment the warp continues at speed 1 — a trailing direct span
            spans.Add(new SampleSpan(AudioTime.SamplesCeil(outputEndTicks, sampleRate), long.MaxValue,
                true, AudioTime.SamplesNearest(projectEndTicks - outputEndTicks, sampleRate), false));
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
                _window.Mixer.MixChunk(firstFrame, frames, dst);
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
                else if (span.Pitch)
                    StretchRun(span, o, run, dst, done * Channels);
                else
                    ResampleRun(o, run, dst, done * Channels);
                done += run;
            }
        }

        /// <summary>The speed-1 run: verbatim copy of project frames at the span's constant
        /// offset — no float math, so unwarped spans stay bit-exact against a straight mix.</summary>
        private void CopyRun(long firstProjectFrame, int run, float[] dst, int dstOffset)
        {
            _window.Ensure(firstProjectFrame, firstProjectFrame + run);
            for (int i = 0; i < run; i++)
            {
                int src = (int)(_window.Clamp(firstProjectFrame + i) - _window.StartFrame) * Channels;
                int di = dstOffset + i * Channels;
                dst[di] = _window.Buffer[src];
                dst[di + 1] = _window.Buffer[src + 1];
            }
            _lastPos = Math.Max(_lastPos, firstProjectFrame + run - 1);
        }

        /// <summary>The pitch-corrected run: the stretcher synthesizes the span's output on its
        /// own hop grid and this copies the requested frames out. Entering the span (or coming
        /// back to it — there is one stretcher, reset between spans) re-anchors the synthesis
        /// at the span's first output frame.</summary>
        private void StretchRun(in SampleSpan span, long firstOutputFrame, int run, float[] dst, int dstOffset)
        {
            if (_stretchSpanIndex != _spanIndex)
            {
                _stretcher ??= new WsolaStretcher(_rate);
                _targetOf ??= o => _warp.ToProject(AudioTime.TicksFloor(o, _rate)) * (double)_rate
                    / TimeBase.TicksPerSecond;
                _stretcher.Reset();
                _stretchSpanIndex = _spanIndex;
            }

            _stretcher.EnsureOutput(span.OutStart, firstOutputFrame, firstOutputFrame + run, _targetOf, _window);
            Array.Copy(_stretcher.OutputBuffer, (int)(firstOutputFrame - _stretcher.OutputStart) * Channels,
                dst, dstOffset, run * Channels);
            // the span's project cursor moved under the stretcher; keep the join clamp current
            _lastPos = Math.Max(_lastPos, _targetOf(firstOutputFrame + run - 1));
        }

        /// <summary>The plain warped run: each output sample's tick maps through the warp to a
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
            _window.Ensure(firstNeeded, endNeeded);

            long windowLast = _window.StartFrame + _window.Frames - 1;
            var buffer = _window.Buffer;
            for (int i = 0; i < run; i++)
            {
                double p = _positions[i];
                long f0 = _window.Clamp((long)Math.Floor(p));
                long f1 = Math.Min(f0 + 1, windowLast);
                float frac = (float)(p - f0);
                if (frac < 0f)
                    frac = 0f;
                else if (frac > 1f)
                    frac = 1f;

                int b0 = (int)(f0 - _window.StartFrame) * Channels;
                int b1 = (int)(f1 - _window.StartFrame) * Channels;
                int di = dstOffset + i * Channels;
                for (int ch = 0; ch < Channels; ch++)
                {
                    float a = buffer[b0 + ch];
                    float b = buffer[b1 + ch];
                    dst[di + ch] = a + (b - a) * frac;
                }
            }
        }
    }
}
