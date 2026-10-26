using System;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// The pitch-preserving alternative to linearly resampling a warped span
    /// (<see cref="Model.SpeedContent.PitchCorrect"/>): WSOLA — waveform-similarity overlap-add —
    /// time-stretching. Output is synthesized in fixed 30ms sequences on the output-frame grid;
    /// each sequence is a <b>verbatim</b> run of project audio taken from around the position the
    /// <c>TimeWarp</c> maps its output instant to, spliced onto the previous sequence's
    /// continuation through a short 8ms cross-fade whose start is chosen, within a ±12ms
    /// tolerance around the mapped position, to maximize waveform similarity with what is
    /// already playing. Long untouched runs joined by short well-aligned fades is what keeps the
    /// sound clean — only ~25% of output samples pass through a fade at all — while re-anchoring
    /// every sequence on the warp keeps the stretch following the warp's local slope (ramps
    /// included): the audio can never drift out of sync with the video by more than one
    /// sequence, and that error resets at every splice.
    ///
    /// <para>
    /// The synthesis grid is anchored to the span's first output frame (a mid-span entry — the
    /// preview seeking into a span — snaps to the same grid), and every sequence is a pure
    /// function of that anchor and the project audio, so output is independent of how consumers
    /// chunk their reads and identical between preview and render. Both consumers drive the same
    /// instance shape: <see cref="EnsureOutput"/> synthesizes forward until the requested output
    /// range is buffered, reading project frames through the shared <see cref="MixerWindow"/>
    /// (forward-only, each frame mixed once), and the consumer copies or interpolates from
    /// <see cref="OutputBuffer"/>. Call <see cref="Reset"/> when leaving a span or seeking — the
    /// next <see cref="EnsureOutput"/> re-anchors and re-primes.
    /// </para>
    ///
    /// <para>
    /// Priming: the first sequence's cross-fade tail is the verbatim project audio at the mapped
    /// position, which is exactly what a preceding speed-1 span was outputting — the entry seam
    /// is a fade between identical content, i.e. seamless. The exit seam back to verbatim copies
    /// is bounded by the search tolerance and in practice small: the default entry/exit ramps
    /// ease the speed back to 1, where the search settles on the aligned position.
    /// </para>
    /// </summary>
    public sealed class WsolaStretcher
    {
        private const int Channels = AudioMixer.Channels;

        private readonly int _hop;      // sequence length: output frames per splice, 30ms
        private readonly int _overlap;  // cross-fade length at each splice, 8ms
        private readonly int _search;   // alignment tolerance either side, 12ms
        private readonly float[] _fadeIn; // raised-cosine, _fadeIn[j] + fade-out[j] = 1

        // synthesized output frames [_outStart, _outStart + _outCount), interleaved stereo
        private float[] _out = Array.Empty<float>();
        private long _outStart;
        private int _outCount;

        private readonly float[] _tail;   // the previous sequence's continuation, stereo, _overlap frames
        private float[] _mono = Array.Empty<float>();
        private long _nextSynth;          // absolute output frame the next sequence starts at
        private bool _active;
        private bool _primed;

        public WsolaStretcher(int sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
            _hop = Math.Max(2, sampleRate * 3 / 100);
            _overlap = Math.Max(1, Math.Min(sampleRate / 125, _hop / 2));
            _search = Math.Max(1, sampleRate * 3 / 250);
            _tail = new float[_overlap * Channels];
            _fadeIn = new float[_overlap];
            for (int j = 0; j < _overlap; j++)
                _fadeIn[j] = 0.5f - 0.5f * (float)Math.Cos(Math.PI * (j + 0.5) / _overlap);
        }

        /// <summary>Synthesized output frames, interleaved stereo; index 0 is <see cref="OutputStart"/>.</summary>
        public float[] OutputBuffer => _out;

        /// <summary>Absolute output frame of the buffer's first held frame.</summary>
        public long OutputStart => _outStart;

        /// <summary>Output frames currently held.</summary>
        public int OutputFrames => _outCount;

        /// <summary>Forgets the synthesis state; the next <see cref="EnsureOutput"/> re-anchors
        /// on its span and re-primes the cross-fade. Call on a seek and whenever the consumer's
        /// cursor leaves the pitch-corrected span this instance was stretching.</summary>
        public void Reset()
        {
            _active = false;
            _outCount = 0;
        }

        /// <summary>
        /// Synthesizes forward until output frames [<paramref name="firstFrame"/>,
        /// <paramref name="endFrame"/>) are all held in <see cref="OutputBuffer"/>, dropping
        /// frames more than two behind <paramref name="firstFrame"/> (an interpolating consumer's
        /// left neighbor stays readable). Requests must move forward between resets.
        /// </summary>
        /// <param name="spanStartFrame">The span's first output frame — the synthesis grid
        /// anchor. Must not change between resets.</param>
        /// <param name="targetOf">Maps an absolute output frame to the fractional project frame
        /// the warp puts it at; monotone non-decreasing.</param>
        /// <param name="window">The consumer's mix window project frames are read through.</param>
        public void EnsureOutput(long spanStartFrame, long firstFrame, long endFrame,
            Func<long, double> targetOf, MixerWindow window)
        {
            ArgumentNullException.ThrowIfNull(targetOf);
            ArgumentNullException.ThrowIfNull(window);
            if (!_active)
            {
                // a mid-span start snaps back onto the grid the span anchors, so the sequences a
                // seeked-into preview synthesizes are the ones the render synthesizes
                long anchor = firstFrame <= spanStartFrame
                    ? spanStartFrame
                    : spanStartFrame + (firstFrame - spanStartFrame) / _hop * _hop;
                _nextSynth = anchor;
                _outStart = anchor;
                _outCount = 0;
                _primed = false;
                _active = true;
            }

            long dropBelow = firstFrame - 2;
            if (dropBelow > _outStart && _outCount > 0)
            {
                int drop = (int)Math.Min(dropBelow - _outStart, _outCount);
                Array.Copy(_out, drop * Channels, _out, 0, (_outCount - drop) * Channels);
                _outStart += drop;
                _outCount -= drop;
            }
            if (_outCount == 0 && _nextSynth > _outStart)
                _outStart = _nextSynth;

            while (_outStart + _outCount < endFrame)
                SynthesizeSequence(targetOf, window);
        }

        /// <summary>One sequence: pick the best-aligned project segment around the warp-mapped
        /// position, cross-fade the previous continuation into its first <see cref="_overlap"/>
        /// frames, copy the rest verbatim, keep the frames just past the sequence end as the
        /// next continuation.</summary>
        private void SynthesizeSequence(Func<long, double> targetOf, MixerWindow window)
        {
            long tBase = (long)Math.Floor(targetOf(_nextSynth));
            if (tBase < 0)
                tBase = 0;

            // The first sequence never looks behind the mapped position: its continuation IS the
            // audio at tBase, so the aligned candidate already scores maximal similarity — and
            // how much history the window happens to still hold there depends on the consumer's
            // chunk size, so searching into it would make output chunking-dependent. Later
            // sequences advance the search start by at least hop × min-speed per splice, which
            // outruns the window's forward drop — their full tolerance is always held.
            long lo = _primed ? tBase - _search : tBase;
            long hi = tBase + _search;
            window.Ensure(lo, hi + _hop + _overlap);
            if (lo < window.StartFrame)
                lo = window.StartFrame; // the project's own start truncates the tolerance
            if (hi < lo)
                hi = lo;

            if (!_primed)
            {
                // the continuation is the verbatim project audio at the mapped position — what a
                // preceding speed-1 span was outputting — so the entry cross-fade is seamless,
                // and the aligned candidate scores maximal similarity by construction
                for (int j = 0; j < _overlap; j++)
                {
                    int src = (int)(window.Clamp(tBase + j) - window.StartFrame) * Channels;
                    _tail[j * Channels] = window.Buffer[src];
                    _tail[j * Channels + 1] = window.Buffer[src + 1];
                }
                _primed = true;
            }

            long best = BestAlignment(lo, hi, window);

            // splice: cross-fade into the segment, then verbatim through the sequence
            int b = (int)(best - window.StartFrame) * Channels;
            EnsureOutCapacity(_outCount + _hop);
            int dst = _outCount * Channels;
            var buffer = window.Buffer;
            for (int j = 0; j < _overlap; j++)
            {
                float w = _fadeIn[j];
                int src = b + j * Channels;
                int di = dst + j * Channels;
                _out[di] = _tail[j * Channels] * (1f - w) + buffer[src] * w;
                _out[di + 1] = _tail[j * Channels + 1] * (1f - w) + buffer[src + 1] * w;
            }
            Array.Copy(buffer, b + _overlap * Channels, _out, dst + _overlap * Channels,
                (_hop - _overlap) * Channels);
            Array.Copy(buffer, b + _hop * Channels, _tail, 0, _overlap * Channels);

            _outCount += _hop;
            _nextSynth += _hop;
        }

        /// <summary>The candidate start in [<paramref name="lo"/>, <paramref name="hi"/>] whose
        /// first <see cref="_overlap"/> frames of project audio are most similar to the
        /// continuation being faded out — normalized cross-correlation on the channel sum, every
        /// offset scored (the basis is short enough that a full scan is cheap and never skips a
        /// narrow peak).</summary>
        private long BestAlignment(long lo, long hi, MixerWindow window)
        {
            if (hi == lo)
                return lo;

            int count = (int)(hi - lo) + _overlap;
            if (_mono.Length < count)
                _mono = new float[count];
            var buffer = window.Buffer;
            int b0 = (int)(lo - window.StartFrame) * Channels;
            for (int i = 0; i < count; i++)
                _mono[i] = buffer[b0 + i * Channels] + buffer[b0 + i * Channels + 1];

            long best = lo;
            double bestScore = double.NegativeInfinity;
            for (long c = lo; c <= hi; c++)
            {
                int off = (int)(c - lo);
                double dot = 0, energy = 0;
                for (int j = 0; j < _overlap; j++)
                {
                    float t = _tail[j * Channels] + _tail[j * Channels + 1];
                    float s = _mono[off + j];
                    dot += t * s;
                    energy += s * s;
                }

                // normalized by the candidate's own energy so a louder segment cannot win on
                // amplitude alone; the epsilon keeps digital silence comparable
                double score = dot / Math.Sqrt(energy + 1e-9);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }

            return best;
        }

        private void EnsureOutCapacity(int frames)
        {
            int floats = frames * Channels;
            if (_out.Length < floats)
            {
                var grown = new float[Math.Max(floats, Math.Max(_out.Length * 2, _hop * 8 * Channels))];
                Array.Copy(_out, grown, _outCount * Channels);
                _out = grown;
            }
        }
    }
}
