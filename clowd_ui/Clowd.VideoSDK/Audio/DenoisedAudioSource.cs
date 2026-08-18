using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Ai;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// The AI-denoise decorator over an <see cref="IAudioSource"/>: streams whose track has
    /// <see cref="Track.Denoise"/> on are answered from their denoise sidecar wav (see
    /// <see cref="DenoiseGenerator"/>) instead of the raw recording, at the same sample positions
    /// — the sidecar shares the source stream's timeline and duration, so no mapping is involved.
    /// A <see cref="Track.DenoiseStrength"/> below 1 reads BOTH signals and lerps per sample
    /// (<c>raw·(1−s) + denoised·s</c>); a missing or stale sidecar (or no cache directory — the
    /// dev harness) passes the raw stream through untouched, so the toggle can never silence a
    /// row. Both construction sites wrap through here: the preview's
    /// <see cref="SeekableAudioSource"/> inside <c>AudioMixWorker</c>, and the render's
    /// <see cref="SequentialAudioSource"/> inside <c>RenderJob</c>.
    ///
    /// <para>The sidecar is read through a private <see cref="SeekableAudioSource"/> over a
    /// one-source shadow project pointed at the wav — the decoder resamples the fixed 48 kHz
    /// sidecar to the mix rate exactly as it does the raw stream. A seekable source serves the
    /// render path too: render's reads are monotone forward, where the seekable source is
    /// sample-identical to the sequential one by construction (same decode-discard logic, and the
    /// wav's first-chunk anchor is 0 where their anchoring agrees).</para>
    ///
    /// <para>Single-threaded like the sources it wraps (the mix thread in preview, the render
    /// loop in render); <see cref="UpdateProject"/> and <see cref="Reset"/> are called from that
    /// same thread. Sidecar validity is decided once per stream when it is first read and only
    /// re-checked on <see cref="UpdateProject"/>, so the per-read cost of a passthrough stream is
    /// one dictionary probe. Does not own (or dispose) the inner source.</para>
    /// </summary>
    public sealed class DenoisedAudioSource : IAudioSource, IDisposable
    {
        private const int Channels = AudioMixer.Channels;

        private readonly IAudioSource _inner;
        private readonly string _cacheDir;
        private readonly int _rate;
        private Project _project;
        private Dictionary<(Guid, int), double> _strength;
        private readonly Dictionary<(Guid, int), SeekableAudioSource> _routes
            = new Dictionary<(Guid, int), SeekableAudioSource>();
        private float[] _blend = Array.Empty<float>();
        private bool _disposed;

        /// <param name="inner">The raw source; stays owned by the caller.</param>
        /// <param name="project">Resolves tracks/items to per-stream denoise settings and source
        /// paths; must carry the mix rate in <c>Output.SampleRate</c>.</param>
        /// <param name="cacheDir">Where the sidecars live (the directory holding
        /// <c>videoedit.json</c>); null or empty means every stream passes through raw.</param>
        public DenoisedAudioSource(IAudioSource inner, Project project, string cacheDir)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(project);
            if (project.Output == null || project.Output.SampleRate <= 0)
                throw new ArgumentException("Project has no positive output sample rate.", nameof(project));

            _inner = inner;
            _cacheDir = cacheDir;
            _rate = project.Output.SampleRate;
            _project = project;
            _strength = CollectDenoisedStreams(project);
        }

        /// <summary>Whether the project asks for any denoising at all — the render path's gate
        /// for constructing this decorator.</summary>
        public static bool HasDenoise(Project project)
        {
            foreach (var track in project?.Tracks ?? new List<Track>())
            {
                if (track.Kind == TrackKind.Audio && !track.Muted && track.Denoise)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The streams the project wants denoised, with their dry/wet strength: every
        /// (sourceId, streamIndex) referenced by a media item on an unmuted audio track with
        /// <see cref="Track.Denoise"/> on — the mirror of the player's audio stream collection,
        /// so a muted row's flag has no effect. Where two rows reference the same stream with
        /// different settings, the first flagged track in track order wins.
        /// </summary>
        public static Dictionary<(Guid SourceId, int StreamIndex), double> CollectDenoisedStreams(
            Project project)
        {
            var strengthByTrack = new Dictionary<Guid, double>();
            foreach (var track in project.Tracks ?? new List<Track>())
            {
                if (track.Kind == TrackKind.Audio && !track.Muted && track.Denoise)
                    strengthByTrack.TryAdd(track.Id, track.DenoiseStrength);
            }

            var map = new Dictionary<(Guid, int), double>();
            if (strengthByTrack.Count == 0)
                return map;

            foreach (var item in project.Items ?? new List<Item>())
            {
                if (item.Content is MediaContent media && item.DurationTicks > 0
                    && strengthByTrack.TryGetValue(item.TrackId, out double strength))
                    map.TryAdd((media.SourceId, media.StreamIndex), strength);
            }

            return map;
        }

        public bool ReadSamples(Guid sourceId, int streamIndex, long sourcePosFrames, float[] dst,
            int frames, out int framesRead)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var key = (sourceId, streamIndex);
            if (!_strength.TryGetValue(key, out double strength) || !(strength > 0))
                return _inner.ReadSamples(sourceId, streamIndex, sourcePosFrames, dst, frames, out framesRead);

            var sidecar = ResolveRoute(key);
            if (sidecar == null)
                return _inner.ReadSamples(sourceId, streamIndex, sourcePosFrames, dst, frames, out framesRead);

            // the shadow project's one source keeps the original id; the wav is its stream 0.
            if (strength >= 1)
                return sidecar.ReadSamples(sourceId, 0, sourcePosFrames, dst, frames, out framesRead);

            if (_blend.Length < frames * Channels)
                _blend = new float[frames * Channels];
            bool denOk = sidecar.ReadSamples(sourceId, 0, sourcePosFrames, _blend, frames, out int denRead);
            bool rawOk = _inner.ReadSamples(sourceId, streamIndex, sourcePosFrames, dst, frames, out int rawRead);
            if (!denOk)
            {
                // a sidecar that yields nothing at all leaves the raw read standing
                framesRead = rawRead;
                return rawOk;
            }

            // both buffers are fully written (silence outside coverage), so the lerp runs the
            // whole window
            float wet = (float)strength;
            int floats = frames * Channels;
            for (int i = 0; i < floats; i++)
                dst[i] += (_blend[i] - dst[i]) * wet;

            framesRead = Math.Max(denRead, rawRead);
            return true;
        }

        /// <summary>Adopts an edited project: strength edits apply from the next read, and streams
        /// previously left raw for want of a sidecar re-check the disk — a generation that
        /// finished since is picked up without a pipeline rebuild. Open sidecar decoders are kept.
        /// Called from the reading thread only, like everything here.</summary>
        public void UpdateProject(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);
            _project = project;
            _strength = CollectDenoisedStreams(project);

            List<(Guid, int)> stale = null;
            foreach (var (key, sidecar) in _routes)
            {
                if (sidecar == null)
                    (stale ??= new List<(Guid, int)>()).Add(key);
            }

            if (stale != null)
            {
                foreach (var key in stale)
                    _routes.Remove(key);
            }
        }

        /// <summary>Forwards the preview's seek invalidation to the sidecar decoders (the inner
        /// source is reset by its own owner).</summary>
        public void Reset()
        {
            foreach (var sidecar in _routes.Values)
                sidecar?.Reset();
        }

        /// <summary>The sidecar reader for a denoised stream, or null when the sidecar is missing
        /// or stale (raw passthrough). The decision is cached; <see cref="UpdateProject"/> is the
        /// re-check point for streams that resolved to passthrough.</summary>
        private SeekableAudioSource ResolveRoute((Guid SourceId, int StreamIndex) key)
        {
            if (_routes.TryGetValue(key, out var existing))
                return existing;

            SeekableAudioSource sidecar = null;
            var wavPath = AiSidecars.DenoisePath(_cacheDir, key.SourceId, key.StreamIndex);
            var sourcePath = FindSourcePath(_project, key.SourceId);
            if (wavPath != null && sourcePath != null && AiSidecars.IsValid(wavPath, sourcePath))
            {
                var shadow = new Project
                {
                    Output = new OutputSettings { SampleRate = _rate },
                    Sources = { new Source { Id = key.SourceId, Path = wavPath } },
                };
                sidecar = new SeekableAudioSource(shadow);
            }

            _routes[key] = sidecar;
            return sidecar;
        }

        private static string FindSourcePath(Project project, Guid sourceId)
        {
            foreach (var source in project.Sources ?? new List<Source>())
            {
                if (source.Id == sourceId)
                    return source.Path;
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var sidecar in _routes.Values)
                sidecar?.Dispose();
            _routes.Clear();
        }
    }
}
