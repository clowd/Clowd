using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Ai
{
    /// <summary>The companion metadata written beside every AI sidecar file — the validity check
    /// that ties the sidecar to the exact source file it was generated from, the same
    /// length+mtime rule <c>WaveformCache</c> applies. <see cref="Width"/>/<see cref="Height"/>
    /// carry the matte's analysis resolution and stay 0 for audio sidecars.</summary>
    public sealed class AiSidecarInfo
    {
        public int Version { get; set; } = AiSidecars.CurrentVersion;

        public long SourceFileLength { get; set; }

        public long SourceMTimeUtcTicks { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AiSidecarInfo))]
    internal partial class AiSidecarJsonContext : JsonSerializerContext { }

    /// <summary>
    /// Naming and companion-json validation for the AI sidecar files stored beside the project's
    /// <c>videoedit.json</c>: <c>matte-{SourceId}-{StreamIndex}.mp4</c> (the person matte, alpha
    /// in luma at analysis resolution) and <c>denoise-{SourceId}-{StreamIndex}.wav</c> (the
    /// denoised audio, float32 at 48 kHz), each with a same-name <c>.json</c> companion. Keyed by
    /// the model's <see cref="Source.Id"/> rather than the path so relinking regenerates rather
    /// than colliding.
    ///
    /// <para>Nothing here throws outward: sidecars are an optimization the effects degrade
    /// without, so a missing directory (the dev harness opens recordings with no session
    /// directory), a corrupt companion or an unreadable file all simply mean "not valid" — the
    /// consumer falls back to the raw source.</para>
    /// </summary>
    public static class AiSidecars
    {
        public const int CurrentVersion = 1;

        public static string MatteFileName(Guid sourceId, int streamIndex) =>
            "matte-" + sourceId + "-" + streamIndex.ToString(CultureInfo.InvariantCulture) + ".mp4";

        public static string DenoiseFileName(Guid sourceId, int streamIndex) =>
            "denoise-" + sourceId + "-" + streamIndex.ToString(CultureInfo.InvariantCulture) + ".wav";

        /// <summary>The matte sidecar's full path, or null when the caller has no directory to
        /// cache into.</summary>
        public static string MattePath(string cacheDir, Guid sourceId, int streamIndex) =>
            String.IsNullOrEmpty(cacheDir) ? null : Path.Combine(cacheDir, MatteFileName(sourceId, streamIndex));

        /// <summary>The denoise sidecar's full path, or null when the caller has no directory to
        /// cache into.</summary>
        public static string DenoisePath(string cacheDir, Guid sourceId, int streamIndex) =>
            String.IsNullOrEmpty(cacheDir) ? null : Path.Combine(cacheDir, DenoiseFileName(sourceId, streamIndex));

        /// <summary>The companion json beside a sidecar (<c>.mp4</c>/<c>.wav</c> → <c>.json</c>).</summary>
        public static string CompanionPath(string sidecarPath) =>
            sidecarPath == null ? null : Path.ChangeExtension(sidecarPath, ".json");

        /// <summary>The companion describing <paramref name="sourcePath"/> as it stands on disk
        /// right now, or null when the source file does not exist.</summary>
        public static AiSidecarInfo DescribeSource(string sourcePath, int width = 0, int height = 0)
        {
            var source = new FileInfo(sourcePath);
            if (!source.Exists)
                return null;

            return new AiSidecarInfo
            {
                Version = CurrentVersion,
                SourceFileLength = source.Length,
                SourceMTimeUtcTicks = source.LastWriteTimeUtc.Ticks,
                Width = width,
                Height = height,
            };
        }

        /// <summary>Writes the companion json atomically (unique temp name, then one replace —
        /// the same discipline <c>WaveformCache.TrySave</c> applies). False when the write failed;
        /// never a reason to fail the generation that produced the sidecar.</summary>
        public static bool TryWriteCompanion(string sidecarPath, AiSidecarInfo info)
        {
            var path = CompanionPath(sidecarPath);
            if (path == null || info == null)
                return false;

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(info, AiSidecarJsonContext.Default.AiSidecarInfo));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch
            {
                try { File.Delete(temp); }
                catch { /* best effort */ }
                return false;
            }
        }

        /// <summary>The parsed companion of a sidecar, or null when it is missing or does not
        /// parse.</summary>
        public static AiSidecarInfo TryReadCompanion(string sidecarPath)
        {
            try
            {
                var path = CompanionPath(sidecarPath);
                if (path == null || !File.Exists(path))
                    return null;

                return JsonSerializer.Deserialize(File.ReadAllText(path),
                    AiSidecarJsonContext.Default.AiSidecarInfo);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Whether the sidecar at <paramref name="sidecarPath"/> still describes
        /// <paramref name="sourcePath"/>: the sidecar and its companion exist, the companion
        /// parses at <see cref="CurrentVersion"/>, and the source file's length and modification
        /// time match what the companion recorded. When valid, <paramref name="info"/> carries the
        /// parsed companion (the matte consumer needs its analysis resolution).
        /// </summary>
        public static bool IsValid(string sidecarPath, string sourcePath, out AiSidecarInfo info)
        {
            info = null;
            try
            {
                if (sidecarPath == null || !File.Exists(sidecarPath))
                    return false;

                var source = new FileInfo(sourcePath);
                if (!source.Exists)
                    return false;

                var parsed = TryReadCompanion(sidecarPath);
                if (parsed == null || parsed.Version != CurrentVersion)
                    return false;
                if (parsed.SourceFileLength != source.Length ||
                    parsed.SourceMTimeUtcTicks != source.LastWriteTimeUtc.Ticks)
                    return false;

                info = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Convenience form of <see cref="IsValid(string, string, out AiSidecarInfo)"/>.</summary>
        public static bool IsValid(string sidecarPath, string sourcePath) =>
            IsValid(sidecarPath, sourcePath, out _);
    }
}
