using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The v2 render job file (<see cref="ProjectFileWriter"/>): the project verbatim, plus the
    /// output path and crf as siblings. Both halves of the contract are checked — the tool reads
    /// the siblings straight off the JSON and the project through <see cref="Project.FromJson"/>,
    /// so the file has to satisfy both readers at once.
    /// </summary>
    public class ProjectFileWriterTests
    {
        private const string Output = @"C:\rec\video-edited.mp4";

        private static readonly Guid SourceId = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid TrackId = new Guid("bbbbbbbb-0000-0000-0000-000000000001");
        private static readonly Guid ItemId = new Guid("dddddddd-0000-0000-0000-000000000001");

        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static Project Sample() => new Project
        {
            Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            Sources =
            {
                new Source
                {
                    Id = SourceId,
                    Path = @"C:\rec\video.mp4",
                    Streams =
                    {
                        new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(30_000) },
                    },
                },
            },
            Tracks = { new Track { Id = TrackId, Kind = TrackKind.Video, Name = "Screen", Order = 0 } },
            Items =
            {
                new Item
                {
                    Id = ItemId,
                    TrackId = TrackId,
                    TimelineStartTicks = 0,
                    DurationTicks = Ms(5_000),
                    Content = new MediaContent { SourceId = SourceId, StreamIndex = 0, SourceInTicks = Ms(1_000) },
                },
            },
        };

        private static string Serialize(Project project, string output = Output, int crf = 23)
            => Encoding.UTF8.GetString(ProjectFileWriter.Serialize(project, output, crf));

        [Fact]
        public void The_job_file_is_the_project_with_output_and_crf_beside_it()
        {
            using var document = JsonDocument.Parse(Serialize(Sample(), crf: 16));
            var root = document.RootElement;

            Assert.Equal(Project.CurrentVersion, root.GetProperty("Version").GetInt32());
            Assert.Equal(Output, root.GetProperty("output").GetString());
            Assert.Equal(16, root.GetProperty("crf").GetInt32());

            // the siblings are lower-case precisely so they cannot shadow the project's own output
            // block — the tool would then read the canvas settings as a path.
            Assert.Equal(1920, root.GetProperty("Output").GetProperty("WidthPx").GetInt32());
        }

        [Fact]
        public void The_job_file_still_reads_back_as_the_same_project()
        {
            var project = Sample();

            var restored = Project.FromJson(Serialize(project));

            Assert.Equal(SourceId, Assert.Single(restored.Sources).Id);
            Assert.Equal(TrackId, Assert.Single(restored.Tracks).Id);
            var item = Assert.Single(restored.Items);
            Assert.Equal(ItemId, item.Id);
            Assert.Equal(TrackId, item.TrackId);
            Assert.Equal(SourceId, ((MediaContent)item.Content).SourceId);
            Assert.Empty(restored.Validate());

            // the siblings ride alongside the model, they do not change it.
            Assert.Equal(project.ToJson(), restored.ToJson());
        }

        [Fact]
        public void Write_puts_those_very_bytes_on_disk()
        {
            var project = Sample();
            var path = Path.Combine(Path.GetTempPath(), $"clowd-jobfile-test-{Guid.NewGuid():N}.json");
            try
            {
                Assert.Equal(path, ProjectFileWriter.Write(path, project, Output, 23));
                Assert.Equal(ProjectFileWriter.Serialize(project, Output, 23), File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_job_file_needs_somewhere_to_write()
        {
            Assert.Throws<ArgumentNullException>(() => ProjectFileWriter.Serialize(null, Output, 23));
            Assert.Throws<ArgumentException>(() => ProjectFileWriter.Serialize(Sample(), "", 23));
        }
    }
}
