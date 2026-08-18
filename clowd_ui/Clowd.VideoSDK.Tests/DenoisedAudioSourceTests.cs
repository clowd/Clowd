using System;
using System.IO;
using Clowd.VideoSDK.Ai;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The audio half of the AI effects: <see cref="AiSidecars"/>' naming and companion-json
    /// validity rules (the length+mtime discipline shared with <c>WaveformCache</c>), and
    /// <see cref="DenoisedAudioSource"/> — the decorator both the preview mix and the render mix
    /// read through. Sidecar wavs are synthesized directly (constant-value float32 PCM, the
    /// exact format <c>DenoiseGenerator</c> writes) so the routing and lerp math are asserted on
    /// known numbers; the raw stream is a fake constant source, so any read answered by the
    /// wrong side is immediately visible in the sample values. Wav-decoding tests skip when the
    /// FFmpeg natives are absent (same resolver as <see cref="SequentialAudioSourceTests"/>);
    /// the fallback paths never open a decoder and run everywhere.
    /// </summary>
    public class DenoisedAudioSourceTests : IDisposable
    {
        private const int Rate = 48000;
        private const float RawValue = 0.25f;
        private const float DenoisedValue = 0.5f;
        private const int SidecarFrames = Rate / 2; // 500 ms

        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

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

        // ------------------------------------------------------------------------------ fixture

        private readonly string _cacheDir;

        public DenoisedAudioSourceTests()
        {
            _cacheDir = Path.Combine(Path.GetTempPath(), $"clowd-denoise-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_cacheDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_cacheDir, recursive: true); }
            catch { /* best effort */ }
        }

        /// <summary>A stand-in recording file: the raw stream is a fake source, so only the
        /// length+mtime the companions validate against matter.</summary>
        private string WriteFakeRecording(string name = "input.mp4", int bytes = 1000)
        {
            var path = Path.Combine(_cacheDir, name);
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        /// <summary>Writes a denoise sidecar wav of constant-value stereo float32 at 48 kHz —
        /// the format <c>DenoiseGenerator</c> produces — plus its companion json.</summary>
        private string WriteSidecar(Guid sourceId, int streamIndex, string sourcePath,
            float value = DenoisedValue, int frames = SidecarFrames)
        {
            var wavPath = AiSidecars.DenoisePath(_cacheDir, sourceId, streamIndex);
            using (var stream = new FileStream(wavPath, FileMode.Create, FileAccess.Write))
            {
                DenoiseGenerator.WriteWavHeader(stream, channels: 2, Rate, (uint)(frames * 2 * 4));
                var chunk = new byte[4096 * 8];
                var one = BitConverter.GetBytes(value);
                for (int i = 0; i < chunk.Length; i += 4)
                    one.CopyTo(chunk, i);
                long remaining = (long)frames * 2 * 4;
                while (remaining > 0)
                {
                    int n = (int)Math.Min(chunk.Length, remaining);
                    stream.Write(chunk, 0, n);
                    remaining -= n;
                }
            }

            Assert.True(AiSidecars.TryWriteCompanion(wavPath, AiSidecars.DescribeSource(sourcePath)));
            return wavPath;
        }

        /// <summary>One unmuted audio row over one source stream; the denoise flags are the
        /// variables under test.</summary>
        private static Project AudioProject(Guid sourceId, string sourcePath,
            bool denoise = true, double strength = 1.0, int streamIndex = 1)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Kind = TrackKind.Audio,
                Name = "Audio",
                Order = 0,
                Denoise = denoise,
                DenoiseStrength = strength,
            };
            return new Project
            {
                Output = new OutputSettings { WidthPx = 640, HeightPx = 360, FpsNum = 30, FpsDen = 1, SampleRate = Rate },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = sourcePath,
                        Streams = { new SourceStream { Index = streamIndex, Kind = StreamKind.Audio, DurationTicks = Ms(500) } },
                    },
                },
                Tracks = { track },
                Items =
                {
                    new Item
                    {
                        Id = Guid.NewGuid(),
                        TrackId = track.Id,
                        TimelineStartTicks = 0,
                        DurationTicks = Ms(500),
                        Content = new MediaContent { SourceId = sourceId, StreamIndex = streamIndex },
                    },
                },
            };
        }

        /// <summary>The raw stream: a constant so a read answered from the wrong side shows in
        /// the values. Records the last request for passthrough assertions.</summary>
        private sealed class ConstantSource : IAudioSource
        {
            public int Reads;
            public (Guid SourceId, int StreamIndex, long Pos) LastRequest;

            public bool ReadSamples(Guid sourceId, int streamIndex, long sourcePosFrames, float[] dst,
                int frames, out int framesRead)
            {
                Reads++;
                LastRequest = (sourceId, streamIndex, sourcePosFrames);
                Array.Fill(dst, RawValue, 0, frames * 2);
                framesRead = frames;
                return true;
            }
        }

        private static float[] Read(IAudioSource source, Guid sourceId, int streamIndex,
            long pos, int frames)
        {
            var dst = new float[frames * 2];
            Assert.True(source.ReadSamples(sourceId, streamIndex, pos, dst, frames, out int read));
            Assert.Equal(frames, read);
            return dst;
        }

        // ------------------------------------------------------------------------- sidecar rules

        [Fact]
        public void Sidecar_names_are_a_pinned_contract()
        {
            var id = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
            Assert.Equal($"matte-{id}-0.mp4", AiSidecars.MatteFileName(id, 0));
            Assert.Equal($"denoise-{id}-2.wav", AiSidecars.DenoiseFileName(id, 2));
            Assert.Equal(Path.Combine(@"C:\cache", $"denoise-{id}-2.wav"),
                AiSidecars.DenoisePath(@"C:\cache", id, 2));
            Assert.Equal(@"C:\cache\file.json", AiSidecars.CompanionPath(@"C:\cache\file.wav"));
        }

        [Fact]
        public void No_cache_directory_means_no_paths_at_all()
        {
            Assert.Null(AiSidecars.MattePath(null, Guid.NewGuid(), 0));
            Assert.Null(AiSidecars.DenoisePath("", Guid.NewGuid(), 0));
            Assert.Null(AiSidecars.CompanionPath(null));
        }

        [Fact]
        public void A_matching_companion_validates_and_carries_its_metadata()
        {
            var sourcePath = WriteFakeRecording();
            var id = Guid.NewGuid();
            var wavPath = WriteSidecar(id, 1, sourcePath);

            Assert.True(AiSidecars.IsValid(wavPath, sourcePath, out var info));
            Assert.Equal(AiSidecars.CurrentVersion, info.Version);
            Assert.Equal(new FileInfo(sourcePath).Length, info.SourceFileLength);
            Assert.Equal(0, info.Width);
        }

        [Fact]
        public void The_validity_matrix_rejects_every_kind_of_staleness()
        {
            var sourcePath = WriteFakeRecording();
            var id = Guid.NewGuid();
            var wavPath = WriteSidecar(id, 1, sourcePath);
            var companion = AiSidecars.CompanionPath(wavPath);

            // missing sidecar
            Assert.False(AiSidecars.IsValid(AiSidecars.DenoisePath(_cacheDir, Guid.NewGuid(), 1), sourcePath));
            // missing source
            Assert.False(AiSidecars.IsValid(wavPath, Path.Combine(_cacheDir, "gone.mp4")));
            // null path
            Assert.False(AiSidecars.IsValid(null, sourcePath));

            // wrong version
            var goodJson = File.ReadAllText(companion);
            File.WriteAllText(companion, goodJson.Replace("\"Version\": 1", "\"Version\": 2"));
            Assert.False(AiSidecars.IsValid(wavPath, sourcePath));

            // corrupt companion
            File.WriteAllText(companion, "{ not json");
            Assert.Null(AiSidecars.TryReadCompanion(wavPath));
            Assert.False(AiSidecars.IsValid(wavPath, sourcePath));

            // missing companion
            File.Delete(companion);
            Assert.False(AiSidecars.IsValid(wavPath, sourcePath));

            // stale length: the source grew since the sidecar was generated
            File.WriteAllText(companion, goodJson);
            Assert.True(AiSidecars.IsValid(wavPath, sourcePath));
            File.AppendAllText(sourcePath, "x");
            Assert.False(AiSidecars.IsValid(wavPath, sourcePath));

            // stale mtime: same length, different write time
            var rewritten = WriteFakeRecording();
            Assert.Equal(sourcePath, rewritten);
            File.WriteAllText(companion, goodJson);
            File.SetLastWriteTimeUtc(sourcePath, new FileInfo(sourcePath).LastWriteTimeUtc.AddMinutes(5));
            Assert.False(AiSidecars.IsValid(wavPath, sourcePath));
        }

        // ------------------------------------------------------------------------ stream routing

        [Fact]
        public void Streams_are_denoised_only_from_unmuted_audio_rows_with_the_flag()
        {
            var sourceId = Guid.NewGuid();
            var project = AudioProject(sourceId, @"C:\rec\input.mp4", denoise: true, strength: 0.7);
            Assert.True(DenoisedAudioSource.HasDenoise(project));
            var map = DenoisedAudioSource.CollectDenoisedStreams(project);
            Assert.Equal(0.7, map[(sourceId, 1)]);

            project.Tracks[0].Muted = true;
            Assert.False(DenoisedAudioSource.HasDenoise(project));
            Assert.Empty(DenoisedAudioSource.CollectDenoisedStreams(project));

            project.Tracks[0].Muted = false;
            project.Tracks[0].Denoise = false;
            Assert.False(DenoisedAudioSource.HasDenoise(project));
            Assert.Empty(DenoisedAudioSource.CollectDenoisedStreams(project));
        }

        [Fact]
        public void Without_a_sidecar_or_flag_the_raw_stream_passes_through_untouched()
        {
            var sourceId = Guid.NewGuid();
            var sourcePath = WriteFakeRecording();
            var raw = new ConstantSource();

            // flag off
            using (var source = new DenoisedAudioSource(raw, AudioProject(sourceId, sourcePath, denoise: false), _cacheDir))
            {
                var dst = Read(source, sourceId, 1, 0, 100);
                Assert.All(dst, v => Assert.Equal(RawValue, v));
            }

            // flag on, no sidecar on disk
            using (var source = new DenoisedAudioSource(raw, AudioProject(sourceId, sourcePath), _cacheDir))
            {
                var dst = Read(source, sourceId, 1, 200, 100);
                Assert.All(dst, v => Assert.Equal(RawValue, v));
                Assert.Equal((sourceId, 1, 200L), raw.LastRequest);
            }

            // flag on, sidecar present but stale
            WriteSidecar(sourceId, 1, sourcePath);
            File.AppendAllText(sourcePath, "x");
            using (var source = new DenoisedAudioSource(raw, AudioProject(sourceId, sourcePath), _cacheDir))
            {
                var dst = Read(source, sourceId, 1, 0, 100);
                Assert.All(dst, v => Assert.Equal(RawValue, v));
            }

            // no cache directory at all (the dev harness)
            using (var source = new DenoisedAudioSource(raw, AudioProject(sourceId, sourcePath), null))
            {
                var dst = Read(source, sourceId, 1, 0, 100);
                Assert.All(dst, v => Assert.Equal(RawValue, v));
            }
        }

        [Fact]
        public void Full_strength_reads_the_sidecar_at_the_raw_streams_positions()
        {
            RequireFFmpeg();
            var sourceId = Guid.NewGuid();
            var sourcePath = WriteFakeRecording();
            WriteSidecar(sourceId, 1, sourcePath);
            var raw = new ConstantSource();

            using var source = new DenoisedAudioSource(raw, AudioProject(sourceId, sourcePath), _cacheDir);
            var dst = Read(source, sourceId, 1, 1000, 500);

            Assert.All(dst, v => Assert.Equal(DenoisedValue, v, 3));
            Assert.Equal(0, raw.Reads); // fully wet: the raw stream is never decoded
        }

        [Fact]
        public void Partial_strength_lerps_raw_and_denoised_per_sample()
        {
            RequireFFmpeg();
            var sourceId = Guid.NewGuid();
            var sourcePath = WriteFakeRecording();
            WriteSidecar(sourceId, 1, sourcePath);
            var raw = new ConstantSource();

            using var source = new DenoisedAudioSource(raw,
                AudioProject(sourceId, sourcePath, strength: 0.6), _cacheDir);
            var dst = Read(source, sourceId, 1, 0, 500);

            float expected = RawValue * 0.4f + DenoisedValue * 0.6f;
            Assert.All(dst, v => Assert.Equal(expected, v, 3));
            Assert.Equal(1, raw.Reads);
            Assert.Equal((sourceId, 1, 0L), raw.LastRequest);
        }

        [Fact]
        public void A_project_update_moves_the_strength_and_recheck_finds_a_new_sidecar()
        {
            RequireFFmpeg();
            var sourceId = Guid.NewGuid();
            var sourcePath = WriteFakeRecording();
            var raw = new ConstantSource();

            // no sidecar yet: raw passthrough, and the decision is cached
            using var source = new DenoisedAudioSource(raw, AudioProject(sourceId, sourcePath), _cacheDir);
            Assert.All(Read(source, sourceId, 1, 0, 100), v => Assert.Equal(RawValue, v));

            // generation finished since: an update re-checks the disk without a rebuild
            WriteSidecar(sourceId, 1, sourcePath);
            source.UpdateProject(AudioProject(sourceId, sourcePath));
            Assert.All(Read(source, sourceId, 1, 100, 100), v => Assert.Equal(DenoisedValue, v, 3));

            // and a strength edit applies from the next read
            source.UpdateProject(AudioProject(sourceId, sourcePath, strength: 0.5));
            float expected = RawValue * 0.5f + DenoisedValue * 0.5f;
            Assert.All(Read(source, sourceId, 1, 200, 100), v => Assert.Equal(expected, v, 3));
        }

        [Fact]
        public void Zero_strength_never_opens_the_sidecar()
        {
            var sourceId = Guid.NewGuid();
            var sourcePath = WriteFakeRecording();
            WriteSidecar(sourceId, 1, sourcePath);
            var raw = new ConstantSource();

            // decoder-free even with a valid sidecar on disk, so no FFmpeg gate needed: fully
            // dry reads are answered by the raw source alone
            using var source = new DenoisedAudioSource(raw,
                AudioProject(sourceId, sourcePath, strength: 0.0), _cacheDir);
            var dst = Read(source, sourceId, 1, 0, 100);

            Assert.All(dst, v => Assert.Equal(RawValue, v));
        }
    }
}
