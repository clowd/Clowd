using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// Sums the project's active audio items into interleaved stereo float chunks at the output
    /// sample rate — the audio counterpart of <c>FrameComposer</c>: the ONLY place that knows
    /// what the mix sounds like, against the dual-source pattern (<see cref="IAudioSource"/>).
    ///
    /// For each output chunk, every audible item (a <see cref="MediaContent"/> on a non-muted
    /// <see cref="TrackKind.Audio"/> track whose span covers the samples) contributes
    /// <c>source · Volume · entry-ramp · exit-ramp</c>. Ramps reuse the visual transition
    /// evaluation (<see cref="TransitionMath"/> + the shared <c>Easing</c>) so a fade sounds the
    /// way it looks; every active transition kind ramps audio by its shown-fraction — a slide-out
    /// that left the audio at full volume would be jarring.
    ///
    /// <para>
    /// The summed mix is <b>hard-clamped</b> to [-1, 1]. vid-render (render.rs) applied no
    /// limiting at all — it had a single audio stream, so there was nothing to sum — and the aac
    /// encoder would accept out-of-range floats; the clamp only engages where overlapping items
    /// would otherwise clip anyway. No soft limiter: predictable, and WYSIWYG with any future
    /// preview mix.
    /// </para>
    ///
    /// Timeline→source mapping mirrors the video path (<c>SourceInTicks + (t − start)</c>) but is
    /// computed once per item as a constant sample offset (<see cref="AudioTime.SourceSampleOffset"/>)
    /// so chunk boundaries never re-round through ticks — back-to-back cut items read their
    /// shared stream gaplessly.
    ///
    /// <para>
    /// Items flagged <see cref="MediaContent.SpeedWarpExempt"/> live on the OUTPUT clock when
    /// the project's <see cref="TimeWarp"/> bends time: they are left out of
    /// <see cref="MixChunk"/> (the project-domain mix the warp stages bend) and mixed by
    /// <see cref="MixOutputChunk"/> instead, at output sample positions, with the very same
    /// per-item code. The two consumers of the mixer (the render's <c>WarpAudioResampler</c> and
    /// the preview's <c>AudioMixWorker</c>) both add that output-domain mix on top of the bent
    /// project mix, so an exempt clip sounds identical in both. Under an identity warp the two
    /// clocks coincide and exempt items are ordinary items: nothing about the unwarped path
    /// changes, sample for sample.
    /// </para>
    /// </summary>
    public sealed class AudioMixer
    {
        /// <summary>The SDK's fixed mixing layout: interleaved stereo.</summary>
        public const int Channels = 2;

        private readonly IAudioSource _source;
        private readonly int _rate;
        private readonly TimeWarp _warp;
        private readonly List<ActiveItem> _items = new List<ActiveItem>();
        private readonly List<ActiveItem> _outputItems = new List<ActiveItem>();
        private float[] _scratch = Array.Empty<float>();
        private float[] _outputScratch = Array.Empty<float>();

        private sealed class ActiveItem
        {
            public ActiveItem(Item item, MediaContent media, long firstSample, long endSample,
                long sourceOffset, bool ramped, double speed, long srcBase)
            {
                Item = item;
                Media = media;
                FirstSample = firstSample;
                EndSample = endSample;
                SourceOffset = sourceOffset;
                Ramped = ramped;
                Speed = speed;
                SrcBase = srcBase;
            }

            public Item Item { get; }
            public MediaContent Media { get; }
            public long FirstSample { get; }     // first output sample the item covers
            public long EndSample { get; }       // exclusive
            public long SourceOffset { get; }    // source sample = output sample + offset (speed 1)
            public bool Ramped { get; }          // any active entry/exit transition
            public double Speed { get; }         // source frames one output frame consumes
            public long SrcBase { get; }         // source frame under FirstSample (speed ≠ 1 path)

            // Speed ≠ 1 resample window: source frames [BufStart, BufStart + BufFrames) held for
            // interpolation. Kept per item so the underlying forward-only source is asked for each
            // frame exactly once — the fractional cursor needs its left neighbor again on the
            // next chunk, and re-requesting it would be a backwards read.
            public float[] Buf = Array.Empty<float>();
            public long BufStart;
            public int BufFrames;
        }

        /// <summary>Snapshots the project's audible items; the project must not be mutated while
        /// this mixer is in use (render treats it as immutable). <paramref name="warp"/> is the
        /// project's speed warp, which decides which clock each item is mixed on (see the class
        /// remarks); null or an identity warp puts every item on the project clock.</summary>
        public AudioMixer(Project project, IAudioSource source, TimeWarp warp = null)
        {
            ArgumentNullException.ThrowIfNull(project);
            ArgumentNullException.ThrowIfNull(source);
            if (project.Output == null || project.Output.SampleRate <= 0)
                throw new ArgumentException("Project has no positive output sample rate.", nameof(project));

            _source = source;
            _rate = project.Output.SampleRate;
            _warp = warp is { IsIdentity: false } ? warp : null;

            var audioTracks = new Dictionary<Guid, Track>();
            foreach (var track in project.Tracks ?? new List<Track>())
            {
                if (track.Kind == TrackKind.Audio)
                    audioTracks.TryAdd(track.Id, track);
            }

            foreach (var item in project.Items ?? new List<Item>())
            {
                if (item.Content is not MediaContent media || item.DurationTicks <= 0)
                    continue;
                if (!audioTracks.TryGetValue(item.TrackId, out var track) || track.Muted)
                    continue;

                if (_warp != null && media.SpeedWarpExempt)
                {
                    // the output clock: the span is the warp image of the item's project span,
                    // and the source offset anchors on where that image starts
                    long outputStart = _warp.ToOutput(item.TimelineStartTicks);
                    long outputEnd = _warp.ToOutput(item.TimelineEndTicks);
                    if (outputEnd <= outputStart)
                        continue;

                    _outputItems.Add(new ActiveItem(item, media,
                        AudioTime.SamplesCeil(outputStart, _rate),
                        AudioTime.SamplesCeil(outputEnd, _rate),
                        AudioTime.SourceSampleOffset(media.SourceInTicks, outputStart, _rate),
                        IsActive(item.Entry) || IsActive(item.Exit),
                        TimelineOps.SpeedOf(media),
                        AudioTime.SamplesNearest(media.SourceInTicks, _rate)));
                    continue;
                }

                _items.Add(new ActiveItem(item, media,
                    AudioTime.SamplesCeil(item.TimelineStartTicks, _rate),
                    AudioTime.SamplesCeil(item.TimelineEndTicks, _rate),
                    AudioTime.SourceSampleOffset(media.SourceInTicks, item.TimelineStartTicks, _rate),
                    IsActive(item.Entry) || IsActive(item.Exit),
                    TimelineOps.SpeedOf(media),
                    AudioTime.SamplesNearest(media.SourceInTicks, _rate)));
            }
        }

        /// <summary>Number of items that can contribute to the mix (test/diagnostic), on either
        /// clock.</summary>
        public int AudibleItemCount => _items.Count + _outputItems.Count;

        /// <summary>True when any item is mixed on the output clock: exempt clips under a warp
        /// that bends time. False under an identity warp however many clips are flagged, which
        /// is what lets the consumers skip <see cref="MixOutputChunk"/> entirely on the unwarped
        /// fast paths.</summary>
        public bool HasOutputItems => _outputItems.Count > 0;

        /// <summary>Number of items mixed on the output clock (test/diagnostic).</summary>
        public int OutputItemCount => _outputItems.Count;

        /// <summary>True when the project has any audio-stream item at all (audible or muted) —
        /// the renderer writes an audio stream exactly when this holds, so muting a track
        /// silences it without changing the output's stream layout.</summary>
        public static bool HasAudioItems(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);
            var audioTracks = new HashSet<Guid>();
            foreach (var track in project.Tracks ?? new List<Track>())
            {
                if (track.Kind == TrackKind.Audio)
                    audioTracks.Add(track.Id);
            }

            foreach (var item in project.Items ?? new List<Item>())
            {
                if (item.Content is MediaContent && item.DurationTicks > 0 && audioTracks.Contains(item.TrackId))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The end of the last audio-stream item on the timeline (100ns ticks; 0 when there are
        /// none). The renderer's audio stream runs to this instant rather than to the video's end:
        /// when the source audio track is shorter than the video, the output audio track is
        /// shorter too — the same result vid-render's atrim/concat graph produced.
        /// </summary>
        public static long GetAudioEndTicks(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);
            var audioTracks = new HashSet<Guid>();
            foreach (var track in project.Tracks ?? new List<Track>())
            {
                if (track.Kind == TrackKind.Audio)
                    audioTracks.Add(track.Id);
            }

            long end = 0;
            foreach (var item in project.Items ?? new List<Item>())
            {
                if (item.Content is MediaContent && item.DurationTicks > 0 && audioTracks.Contains(item.TrackId))
                    end = Math.Max(end, item.TimelineEndTicks);
            }
            return end;
        }

        /// <summary>
        /// Mixes output samples [<paramref name="firstFrame"/>, <paramref name="firstFrame"/> +
        /// <paramref name="frames"/>) into <paramref name="dst"/> (interleaved stereo, length at
        /// least <c>frames * 2</c>; fully overwritten). Chunks must be requested in forward order
        /// — the sequential sources underneath are forward-only.
        /// </summary>
        public void MixChunk(long firstFrame, int frames, float[] dst)
        {
            ValidateChunk(firstFrame, frames, dst);
            Array.Clear(dst, 0, frames * Channels);
            MixItems(_items, firstFrame, frames, dst, outputClock: false);
            Clamp(dst, frames);
        }

        /// <summary>
        /// The output-clock counterpart of <see cref="MixChunk"/>: fills <paramref name="dst"/>
        /// with the mix of the exempt items over OUTPUT samples [<paramref name="firstOutputFrame"/>,
        /// <paramref name="firstOutputFrame"/> + <paramref name="frames"/>). Silence (and no
        /// source read at all) when <see cref="HasOutputItems"/> is false. Forward order only,
        /// like <see cref="MixChunk"/>; the two sequences are independent of each other.
        /// </summary>
        public void MixOutputChunk(long firstOutputFrame, int frames, float[] dst)
        {
            ValidateChunk(firstOutputFrame, frames, dst);
            Array.Clear(dst, 0, frames * Channels);
            if (_outputItems.Count == 0)
                return;

            MixItems(_outputItems, firstOutputFrame, frames, dst, outputClock: true);
            Clamp(dst, frames);
        }

        /// <summary>
        /// Adds the exempt items' output-clock mix on top of a chunk of already-bent project mix
        /// (<see cref="MixOutputChunk"/> into scratch, summed in, clamped again): what a warp
        /// stage calls once it has produced output samples [<paramref name="firstOutputFrame"/>,
        /// <paramref name="firstOutputFrame"/> + <paramref name="frames"/>) into
        /// <paramref name="dst"/>. Leaves <paramref name="dst"/> untouched, byte for byte, when
        /// <see cref="HasOutputItems"/> is false.
        /// </summary>
        public void AddOutputChunk(long firstOutputFrame, int frames, float[] dst)
        {
            ValidateChunk(firstOutputFrame, frames, dst);
            if (_outputItems.Count == 0)
                return;

            int floats = frames * Channels;
            if (_outputScratch.Length < floats)
                _outputScratch = new float[floats];
            MixOutputChunk(firstOutputFrame, frames, _outputScratch);
            for (int i = 0; i < floats; i++)
                dst[i] += _outputScratch[i];
            Clamp(dst, frames);
        }

        private static void ValidateChunk(long firstFrame, int frames, float[] dst)
        {
            ArgumentNullException.ThrowIfNull(dst);
            ArgumentOutOfRangeException.ThrowIfNegative(frames);
            ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
            if ((long)frames * Channels > dst.Length)
                throw new ArgumentOutOfRangeException(nameof(frames),
                    $"{frames} stereo frames do not fit in a buffer of {dst.Length} floats.");
        }

        /// <summary>The per-item sum shared by both clocks: each item's covered run is read (or
        /// resampled) and added into <paramref name="dst"/> with its volume and transition gain.
        /// <paramref name="outputClock"/> says which domain the frames are in; it only matters
        /// for the transition ramps, which are defined in project time.</summary>
        private void MixItems(List<ActiveItem> items, long firstFrame, int frames, float[] dst, bool outputClock)
        {
            long chunkEnd = firstFrame + frames;

            foreach (var active in items)
            {
                long runStart = Math.Max(firstFrame, active.FirstSample);
                long runEnd = Math.Min(chunkEnd, active.EndSample);
                if (runEnd <= runStart)
                    continue;

                int runFrames = (int)(runEnd - runStart);

                if (active.Speed != 1.0)
                {
                    MixResampledRun(active, runStart, runFrames, firstFrame, dst, outputClock);
                    continue;
                }

                int runFloats = runFrames * Channels;
                if (_scratch.Length < runFloats)
                    _scratch = new float[runFloats];

                _source.ReadSamples(active.Media.SourceId, active.Media.StreamIndex,
                    runStart + active.SourceOffset, _scratch, runFrames, out _);

                double volume = Math.Max(0, active.Item.Volume);
                int dstBase = (int)(runStart - firstFrame) * Channels;

                if (!active.Ramped)
                {
                    if (volume <= 0)
                        continue;
                    float gain = (float)volume;
                    for (int i = 0; i < runFloats; i++)
                        dst[dstBase + i] += _scratch[i] * gain;
                }
                else
                {
                    for (int s = 0; s < runFrames; s++)
                    {
                        long tick = ProjectTickOf(runStart + s, outputClock);
                        double gain = volume
                            * TransitionMath.EntryProgress(active.Item, tick)
                            * TransitionMath.ExitProgress(active.Item, tick);
                        if (gain <= 0)
                            continue;
                        int di = dstBase + s * Channels;
                        int si = s * Channels;
                        dst[di] += (float)(_scratch[si] * gain);
                        dst[di + 1] += (float)(_scratch[si + 1] * gain);
                    }
                }
            }
        }

        /// <summary>The project instant a sample frame's gain is evaluated at: the frame's own
        /// tick on the project clock, its warp pre-image on the output clock (so an exempt
        /// clip's fades land where its project span says they do).</summary>
        private long ProjectTickOf(long frame, bool outputClock)
        {
            long tick = AudioTime.TicksFloor(frame, _rate);
            return outputClock && _warp != null ? _warp.ToProject(tick) : tick;
        }

        /// <summary>The hard clamp (see class remarks).</summary>
        private static void Clamp(float[] dst, int frames)
        {
            int floats = frames * Channels;
            for (int i = 0; i < floats; i++)
            {
                float v = dst[i];
                if (v > 1f)
                    dst[i] = 1f;
                else if (v < -1f)
                    dst[i] = -1f;
            }
        }

        /// <summary>
        /// The speed ≠ 1 run: output frame <c>o</c> samples source frame
        /// <c>SrcBase + (o - FirstSample) · Speed</c>, linearly interpolated between its two
        /// neighbors — pitch rides with the speed, matching the preview's rate control
        /// (<c>AudioMixWorker.MixResampledChunk</c>). The item's window buffer keeps every frame
        /// already read so the forward-only source is asked for each source frame exactly once;
        /// per-sample gain (volume × ramps) is evaluated in timeline time exactly like the
        /// realtime path.
        /// </summary>
        private void MixResampledRun(ActiveItem active, long runStart, int runFrames,
            long chunkFirstFrame, float[] dst, bool outputClock)
        {
            double volume = Math.Max(0, active.Item.Volume);
            if (volume <= 0)
                return;

            double p0 = active.SrcBase + (runStart - active.FirstSample) * active.Speed;
            long f0 = Math.Max(0, (long)Math.Floor(p0));
            long needEnd = (long)Math.Floor(p0 + (runFrames - 1) * active.Speed) + 2; // right neighbor, exclusive

            // drop window frames the cursor has passed; a jump backwards (only possible on a
            // rebuilt preview mixer) restarts the window — the seekable source underneath copes.
            if (active.BufFrames > 0 && f0 > active.BufStart)
            {
                int drop = (int)Math.Min(f0 - active.BufStart, active.BufFrames);
                Array.Copy(active.Buf, drop * Channels, active.Buf, 0, (active.BufFrames - drop) * Channels);
                active.BufStart += drop;
                active.BufFrames -= drop;
            }
            if (active.BufFrames == 0 || f0 < active.BufStart)
            {
                active.BufStart = f0;
                active.BufFrames = 0;
            }

            long readStart = active.BufStart + active.BufFrames;
            int readCount = (int)Math.Max(0, needEnd - readStart);
            if (readCount > 0)
            {
                int haveFloats = active.BufFrames * Channels;
                int needFloats = haveFloats + readCount * Channels;
                if (active.Buf.Length < needFloats)
                {
                    var grown = new float[Math.Max(needFloats, active.Buf.Length * 2)];
                    Array.Copy(active.Buf, grown, haveFloats);
                    active.Buf = grown;
                }

                if (_scratch.Length < readCount * Channels)
                    _scratch = new float[readCount * Channels];
                _source.ReadSamples(active.Media.SourceId, active.Media.StreamIndex,
                    readStart, _scratch, readCount, out _);
                Array.Copy(_scratch, 0, active.Buf, haveFloats, readCount * Channels);
                active.BufFrames += readCount;
            }

            long avail = active.BufStart + active.BufFrames; // exclusive
            for (int s = 0; s < runFrames; s++)
            {
                double p = p0 + s * active.Speed;
                long i0 = (long)Math.Floor(p);
                if (i0 < active.BufStart)
                    i0 = active.BufStart;
                if (i0 >= avail)
                    break;
                long i1 = Math.Min(i0 + 1, avail - 1);
                float frac = (float)(p - i0);

                double gain = volume;
                if (active.Ramped)
                {
                    long tick = ProjectTickOf(runStart + s, outputClock);
                    gain *= TransitionMath.EntryProgress(active.Item, tick)
                          * TransitionMath.ExitProgress(active.Item, tick);
                    if (gain <= 0)
                        continue;
                }

                int di = (int)(runStart - chunkFirstFrame + s) * Channels;
                int b0 = (int)(i0 - active.BufStart) * Channels;
                int b1 = (int)(i1 - active.BufStart) * Channels;
                for (int ch = 0; ch < Channels; ch++)
                {
                    float a = active.Buf[b0 + ch];
                    float b = active.Buf[b1 + ch];
                    dst[di + ch] += (float)((a + (b - a) * frac) * gain);
                }
            }
        }

        private static bool IsActive(Transition tr)
            => tr != null && tr.Kind != TransitionKind.None && tr.DurationTicks > 0;
    }
}
