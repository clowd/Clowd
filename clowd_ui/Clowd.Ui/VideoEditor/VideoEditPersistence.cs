using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clowd.VideoSDK;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor
{
    /// <summary>Wire form of the <b>version 1</b> <c>videoedit.json</c> — the flat trim/cut/webcam
    /// DTO the editor wrote before the composition model existed. Still read (and migrated), never
    /// written: <see cref="VideoEditPersistence"/> saves a v2 <see cref="Project"/> now.</summary>
    internal sealed class VideoEditDocumentDto
    {
        public int Version { get; set; } = CurrentVersion;

        public const int CurrentVersion = 1;

        public long TrimStartMs { get; set; }
        public long TrimEndMs { get; set; }

        public bool WebcamEnabled { get; set; }
        public string WebcamShape { get; set; }
        public double WebcamCornerRadius { get; set; }
        public double WebcamCenterX { get; set; }
        public double WebcamCenterY { get; set; }
        public double WebcamWidth { get; set; }

        public List<VideoEditCutDto> Cuts { get; set; }
    }

    /// <summary>One cut region, half-open <c>[StartMs, EndMs)</c> like <see cref="CutRegion"/>.</summary>
    internal sealed class VideoEditCutDto
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }
    }

    /// <summary>Just enough of any version of the file to tell which one it is.</summary>
    internal sealed class VideoEditVersionDto
    {
        public int Version { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(VideoEditDocumentDto))]
    [JsonSerializable(typeof(VideoEditVersionDto))]
    internal partial class VideoEditJsonContext : JsonSerializerContext
    { }

    /// <summary>
    /// Reads and writes <c>videoedit.json</c>. The file is now the v2 <see cref="Project"/> itself
    /// (<c>ProjectJsonContext</c>) — the same document the compositor plays and the renderer will
    /// take — and version 1 (the flat trim/cut/webcam DTO) is migrated <b>one way</b> on load: its
    /// values go through <see cref="VideoEditDocument"/>'s own setters and the next save writes the
    /// project the editor built from them. No editor-only sidecar is needed: everything the
    /// single-row UI can express round-trips through the project itself (see
    /// <see cref="EditorProject.ApplyToDocument"/>).
    ///
    /// The editor window owns the write scheduling (debounced latest-wins background writes,
    /// synchronous flush on close — the graphics.json pattern); this class is just the format.
    /// </summary>
    internal static class VideoEditPersistence
    {
        /// <summary>File name, stored beside session.json in the session directory.</summary>
        public const string FileName = "videoedit.json";

        /// <summary>Serializes the project to UTF-8 JSON bytes (UI thread — reads live values).</summary>
        public static byte[] Serialize(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);
            return Encoding.UTF8.GetBytes(project.ToJson());
        }

        /// <summary>
        /// Loads <paramref name="path"/> into <paramref name="document"/>, migrating a v1 file on
        /// the way. Best-effort: a missing, corrupt or future-versioned file leaves the document
        /// untouched (a fresh edit) and returns false — the recording itself is the authority, the
        /// edit is only convenience. Values pass through the document's own setters, so anything
        /// out of range in the file is clamped exactly as a live edit would be.
        /// </summary>
        /// <param name="sourceDurationMs">The probed duration of the recording, needed to resolve
        /// a v2 project's trailing trim back onto the document's "to the end" sentinel.</param>
        public static bool TryLoadInto(string path, VideoEditDocument document, long sourceDurationMs)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;

                var bytes = File.ReadAllBytes(path);
                var version = JsonSerializer.Deserialize(bytes, VideoEditJsonContext.Default.VideoEditVersionDto);

                return version?.Version switch
                {
                    VideoEditDocumentDto.CurrentVersion => LoadLegacy(bytes, document),
                    Project.CurrentVersion => LoadProject(bytes, document, sourceDurationMs),
                    _ => false,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to load videoedit.json: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "videoeditor.load-doc");
                return false;
            }
        }

        private static bool LoadProject(byte[] bytes, VideoEditDocument document, long sourceDurationMs)
        {
            var project = Project.FromJson(Encoding.UTF8.GetString(bytes));
            if (project == null)
                return false;

            EditorProject.ApplyToDocument(project, document, sourceDurationMs);
            return true;
        }

        /// <summary>The one-way v1 migration: the legacy DTO is exactly the editor's document, so
        /// applying it and letting the editor rebuild its project from it <i>is</i> the migration —
        /// the mapping onto items/tracks lives in one place (<see cref="EditorProject"/>) rather
        /// than being duplicated here.</summary>
        private static bool LoadLegacy(byte[] bytes, VideoEditDocument document)
        {
            var dto = JsonSerializer.Deserialize(bytes, VideoEditJsonContext.Default.VideoEditDocumentDto);
            if (dto == null)
                return false;

            document.TrimStartMs = dto.TrimStartMs;
            document.TrimEndMs = dto.TrimEndMs;

            document.Webcam.Enabled = dto.WebcamEnabled;
            if (Enum.TryParse<WebcamOverlayShape>(dto.WebcamShape, ignoreCase: true, out var shape))
                document.Webcam.Shape = shape;
            document.Webcam.CornerRadius = dto.WebcamCornerRadius;
            document.Webcam.CenterX = dto.WebcamCenterX;
            document.Webcam.CenterY = dto.WebcamCenterY;
            document.Webcam.Width = dto.WebcamWidth;

            if (dto.Cuts != null)
            {
                var cuts = new List<CutRegion>(dto.Cuts.Count);
                foreach (var c in dto.Cuts)
                    cuts.Add(new CutRegion(c.StartMs, c.EndMs));
                document.SetCuts(cuts);
            }

            return true;
        }
    }
}
