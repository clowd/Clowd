using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clowd.VideoSDK;

namespace Clowd.UI.VideoEditor
{
    /// <summary>Wire form of <see cref="VideoEditDocument"/> for the <c>videoedit.json</c> file
    /// written beside a recording session's <c>session.json</c>. A flat DTO rather than the
    /// document itself so the document's clamping/normalizing setters (and its notify plumbing)
    /// never leak into the file format, and so a hand-edited or future-versioned file fails
    /// loudly at one seam.</summary>
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

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(VideoEditDocumentDto))]
    internal partial class VideoEditJsonContext : JsonSerializerContext
    { }

    /// <summary>
    /// Serializes the video edit document to/from <c>videoedit.json</c>. The editor window owns
    /// the write scheduling (debounced latest-wins background writes, synchronous flush on close —
    /// the graphics.json pattern); this class is just the format.
    /// </summary>
    internal static class VideoEditPersistence
    {
        /// <summary>File name, stored beside session.json in the session directory.</summary>
        public const string FileName = "videoedit.json";

        /// <summary>Serializes the document to UTF-8 JSON bytes (UI thread — reads live values).</summary>
        public static byte[] Serialize(VideoEditDocument document)
        {
            var dto = new VideoEditDocumentDto
            {
                TrimStartMs = document.TrimStartMs,
                TrimEndMs = document.TrimEndMs,
                WebcamEnabled = document.Webcam.Enabled,
                WebcamShape = document.Webcam.Shape.ToString(),
                WebcamCornerRadius = document.Webcam.CornerRadius,
                WebcamCenterX = document.Webcam.CenterX,
                WebcamCenterY = document.Webcam.CenterY,
                WebcamWidth = document.Webcam.Width,
                Cuts = new List<VideoEditCutDto>(),
            };

            foreach (var cut in document.GetCutRanges())
                dto.Cuts.Add(new VideoEditCutDto { StartMs = cut.StartMs, EndMs = cut.EndMs });

            return JsonSerializer.SerializeToUtf8Bytes(dto, VideoEditJsonContext.Default.VideoEditDocumentDto);
        }

        /// <summary>
        /// Loads <paramref name="path"/> into <paramref name="document"/>. Best-effort: a missing,
        /// corrupt or future-versioned file leaves the document untouched (a fresh edit) and
        /// returns false — the recording itself is the authority, the edit is only convenience.
        /// Values pass through the document's own setters, so anything out of range in the file is
        /// clamped exactly as a live edit would be.
        /// </summary>
        public static bool TryLoadInto(string path, VideoEditDocument document)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;

                var dto = JsonSerializer.Deserialize(File.ReadAllBytes(path), VideoEditJsonContext.Default.VideoEditDocumentDto);
                if (dto == null || dto.Version != VideoEditDocumentDto.CurrentVersion)
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
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to load videoedit.json: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "videoeditor.load-doc");
                return false;
            }
        }
    }
}
