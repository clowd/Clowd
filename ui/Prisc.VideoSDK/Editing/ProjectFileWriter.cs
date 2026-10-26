using System;
using System.IO;
using System.Text.Json;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Editing
{
    /// <summary>
    /// Writes the job file <c>Clowd.VideoRender</c> takes: the v2 <see cref="Project"/> itself, plus
    /// the four things the model deliberately does not carry — the file the render is written to,
    /// the encoder quality it is written at, which H.264 encoder writes it, and the encode-time cap
    /// on the output height — as siblings of the project's own properties.
    ///
    /// The siblings are emitted by hand rather than through a DTO so the project's JSON is copied
    /// through verbatim, whatever the model gains later. Their names are lower-case because that is
    /// how the tool reads them (JSON property lookups are ordinal), which also keeps them clear of
    /// the project's own PascalCase <c>Output</c> block.
    /// </summary>
    public static class ProjectFileWriter
    {
        /// <summary>Sibling property naming the file the render is written to.</summary>
        public const string OutputProperty = "output";

        /// <summary>Sibling property carrying the encoder's constant rate factor.</summary>
        public const string CrfProperty = "crf";

        /// <summary>Sibling property naming the H.264 encoder (<see cref="VideoEncoderNames"/>);
        /// the tool treats a missing one as <see cref="VideoEncoder.Auto"/>.</summary>
        public const string EncoderProperty = "encoder";

        /// <summary>Sibling property carrying the encode-time cap on the output height in pixels
        /// (<see cref="Clowd.VideoSDK.Render.RenderJobOptions.MaxHeight"/>). Written only when
        /// there is a cap: absent — like 0 — means the project's own canvas height.</summary>
        public const string MaxHeightProperty = "maxHeight";

        /// <summary>The job file's bytes.</summary>
        /// <param name="maxHeight">Encode-time cap on the output height in pixels; 0 (the
        /// default) writes no <c>maxHeight</c> sibling at all.</param>
        public static byte[] Serialize(Project project, string outputPath, int crf,
            VideoEncoder encoder = VideoEncoder.Auto, int maxHeight = 0)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (String.IsNullOrEmpty(outputPath))
                throw new ArgumentException("The render output path is empty.", nameof(outputPath));
            ArgumentOutOfRangeException.ThrowIfNegative(maxHeight);

            var projectJson = project.ToJson();

            using var doc = JsonDocument.Parse(projectJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The project did not serialize to a JSON object.");

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (String.Equals(property.Name, OutputProperty, StringComparison.Ordinal) ||
                        String.Equals(property.Name, CrfProperty, StringComparison.Ordinal) ||
                        String.Equals(property.Name, EncoderProperty, StringComparison.Ordinal) ||
                        String.Equals(property.Name, MaxHeightProperty, StringComparison.Ordinal))
                        continue; // ours, rewritten below

                    property.WriteTo(writer);
                }

                writer.WriteString(OutputProperty, outputPath);
                writer.WriteNumber(CrfProperty, crf);
                writer.WriteString(EncoderProperty, VideoEncoderNames.Of(encoder));
                // An uncapped render writes no sibling: the tool reads an absent one as "none",
                // so job files from before the cap existed stay byte-identical.
                if (maxHeight > 0)
                    writer.WriteNumber(MaxHeightProperty, maxHeight);

                writer.WriteEndObject();
            }

            return stream.ToArray();
        }

        /// <summary>Writes the job file to <paramref name="path"/> and returns that path.</summary>
        public static string Write(string path, Project project, string outputPath, int crf,
            VideoEncoder encoder = VideoEncoder.Auto, int maxHeight = 0)
        {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentException("The job file path is empty.", nameof(path));

            File.WriteAllBytes(path, Serialize(project, outputPath, crf, encoder, maxHeight));
            return path;
        }
    }
}
