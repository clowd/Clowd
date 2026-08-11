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
    /// Reads and writes <c>videoedit.json</c>. The file is the v2 <see cref="Project"/> itself
    /// (<c>ProjectJsonContext</c>) — the same document the compositor plays and the renderer will
    /// take — and version 1 (the flat trim/cut/webcam DTO) is migrated <b>one way</b> on load: its
    /// values go through <see cref="VideoEditDocument"/>'s own setters and the next save writes the
    /// project the editor built from them.
    ///
    /// The project is the authority on what is played and rendered, but it is a <i>lossy</i> view
    /// of the edit surface: it carries only the keep segments, so a cut the trim range currently
    /// excludes leaves no gap between two items and cannot be read back. That is why the file also
    /// carries a small <see cref="EditorStateProperty"/> block as a sibling of the project's own
    /// properties (<see cref="EditorState"/>): the trim range and the unclamped cut list, so
    /// widening the trim after a reload restores the cuts instead of silently bringing the
    /// cut-out material back. The block is advisory — a file without one loads exactly as before,
    /// and one that disagrees with the project's own keep segments is ignored.
    ///
    /// The editor window owns the write scheduling (debounced latest-wins background writes,
    /// synchronous flush on close — the graphics.json pattern); this class is just the format.
    /// </summary>
    internal static class VideoEditPersistence
    {
        /// <summary>File name, stored beside session.json in the session directory.</summary>
        public const string FileName = "videoedit.json";

        /// <summary>Name of the editor-state block, a sibling of the project's own properties.
        /// Deliberately not a <see cref="Project"/> member: the compositor and the render tool read
        /// this file as a plain project and ignore it.</summary>
        public const string EditorStateProperty = "EditorState";

        /// <summary>Version of the editor-state block; a block from the future is skipped rather
        /// than half-read (the project alone still describes the edit).</summary>
        private const int EditorStateVersion = 1;

        /// <summary>Serializes the project to UTF-8 JSON bytes (UI thread — reads live values),
        /// with the editor-state block of the document it was built from when there is one (see
        /// <see cref="EditorProject.StateOf"/>).</summary>
        public static byte[] Serialize(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);

            var json = project.ToJson();
            var state = EditorProject.StateOf(project);
            if (state == null)
                return Encoding.UTF8.GetBytes(json);

            return WriteWithEditorState(json, state);
        }

        /// <summary>Re-emits the project's JSON with the editor-state block appended as a sibling
        /// property. Written by hand rather than through a DTO so the project's own JSON is copied
        /// through verbatim, whatever the model gains later.</summary>
        private static byte[] WriteWithEditorState(string projectJson, EditorState state)
        {
            using var doc = JsonDocument.Parse(projectJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Encoding.UTF8.GetBytes(projectJson);

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (String.Equals(property.Name, EditorStateProperty, StringComparison.Ordinal))
                        continue; // ours, rewritten below
                    property.WriteTo(writer);
                }

                writer.WriteStartObject(EditorStateProperty);
                writer.WriteNumber("Version", EditorStateVersion);
                writer.WriteNumber("TrimStartMs", state.TrimStartMs);
                writer.WriteNumber("TrimEndMs", state.TrimEndMs);
                writer.WriteStartArray("Cuts");
                foreach (var cut in state.Cuts ?? (IReadOnlyList<CutRegion>)Array.Empty<CutRegion>())
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("StartMs", cut.StartMs);
                    writer.WriteNumber("EndMs", cut.EndMs);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            return stream.ToArray();
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

            try
            {
                ApplyEditorState(bytes, document, sourceDurationMs);
            }
            catch (Exception ex)
            {
                // the project is the authority; a broken sidecar block must never cost the edit.
                Debug.WriteLine("Ignoring the videoedit.json editor block: " + ex.Message);
            }

            return true;
        }

        /// <summary>
        /// Layers the editor-state block over the document the project just produced: the trim
        /// range as the user set it and the full, unclamped cut list — including cuts the current
        /// trim excludes, which the project's keep segments cannot express.
        ///
        /// Applied only when it agrees with the project: the block must reproduce exactly the keep
        /// segments the project describes, or it is discarded and the project-derived edit kept.
        /// So a hand-edited (or stale) block can add back detail the project cannot carry, but can
        /// never change what is played or rendered.
        /// </summary>
        private static void ApplyEditorState(byte[] bytes, VideoEditDocument document, long sourceDurationMs)
        {
            if (!TryReadEditorState(bytes, out long trimStartMs, out long trimEndMs, out var cuts))
                return;

            var fromProject = document.GetKeepSegments(sourceDurationMs);
            long restoreTrimStart = document.TrimStartMs;
            long restoreTrimEnd = document.TrimEndMs;
            var restoreCuts = document.GetCutRanges();

            document.TrimStartMs = trimStartMs;
            // the same "to the end" sentinel ApplyToDocument resolves to: a literal end at the
            // media duration must not out-live a re-probe that reports a slightly different one.
            document.TrimEndMs = sourceDurationMs > 0 && trimEndMs >= sourceDurationMs ? 0 : trimEndMs;
            document.SetCuts(cuts);

            if (SameSegments(fromProject, document.GetKeepSegments(sourceDurationMs)))
                return;

            document.TrimStartMs = restoreTrimStart;
            document.TrimEndMs = restoreTrimEnd;
            document.SetCuts(restoreCuts);
        }

        /// <summary>Reads the editor-state block straight off the JSON (the file is a project, so
        /// the block is not part of any DTO). False when there is none, or it is not this
        /// version.</summary>
        private static bool TryReadEditorState(byte[] bytes, out long trimStartMs, out long trimEndMs,
            out List<CutRegion> cuts)
        {
            trimStartMs = 0;
            trimEndMs = 0;
            cuts = null;

            using var doc = JsonDocument.Parse(bytes.AsMemory());
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(EditorStateProperty, out var state) ||
                state.ValueKind != JsonValueKind.Object)
                return false;

            if (ReadNumber(state, "Version") != EditorStateVersion)
                return false;

            trimStartMs = ReadNumber(state, "TrimStartMs");
            trimEndMs = ReadNumber(state, "TrimEndMs");

            cuts = new List<CutRegion>();
            if (state.TryGetProperty("Cuts", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var cut in array.EnumerateArray())
                {
                    if (cut.ValueKind == JsonValueKind.Object)
                        cuts.Add(new CutRegion(ReadNumber(cut, "StartMs"), ReadNumber(cut, "EndMs")));
                }
            }

            return true;
        }

        private static long ReadNumber(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out long number)
                ? number
                : 0;

        private static bool SameSegments(IReadOnlyList<CutRegion> a, IReadOnlyList<CutRegion> b)
        {
            if (a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].StartMs != b[i].StartMs || a[i].EndMs != b[i].EndMs)
                    return false;
            }

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
