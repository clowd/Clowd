using System;
using System.IO;
using System.Text.Json;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Editing
{
    /// <summary>
    /// Writes the job file <c>Clowd.VideoRender</c> takes: the v2 <see cref="Project"/> itself, plus
    /// the two things the model deliberately does not carry — the file the render is written to and
    /// the encoder quality it is written at — as siblings of the project's own properties.
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

        /// <summary>The job file's bytes.</summary>
        public static byte[] Serialize(Project project, string outputPath, int crf)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (String.IsNullOrEmpty(outputPath))
                throw new ArgumentException("The render output path is empty.", nameof(outputPath));

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
                        String.Equals(property.Name, CrfProperty, StringComparison.Ordinal))
                        continue; // ours, rewritten below

                    property.WriteTo(writer);
                }

                writer.WriteString(OutputProperty, outputPath);
                writer.WriteNumber(CrfProperty, crf);

                writer.WriteEndObject();
            }

            return stream.ToArray();
        }

        /// <summary>Writes the job file to <paramref name="path"/> and returns that path.</summary>
        public static string Write(string path, Project project, string outputPath, int crf)
        {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentException("The job file path is empty.", nameof(path));

            File.WriteAllBytes(path, Serialize(project, outputPath, crf));
            return path;
        }
    }
}
