using System.Collections.Generic;
using System.Text.Json;
using Clowd.Video;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class RenderArgsTests
    {
        private static RenderArgs Sample() => new RenderArgs
        {
            Input = @"C:\Users\test\Videos\rec.mp4",
            Output = @"C:\Users\test\Videos\rec-edited.mp4",
            Segments = new List<RenderSegment>
            {
                new RenderSegment { StartMs = 0, EndMs = 5000 },
                new RenderSegment { StartMs = 7500, EndMs = 12_250 },
            },
            Webcam = new RenderWebcam
            {
                StreamIndex = 1,
                Rect = new RenderRect { X = 10, Y = 20, W = 320, H = 240 },
                MaskPng = @"C:\Users\test\session\mask.png",
            },
            Crf = 21,
        };

        [Fact]
        public void Serialize_UsesTheSnakeCaseWireContract()
        {
            var json = Sample().ToJson();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal(@"C:\Users\test\Videos\rec.mp4", root.GetProperty("input").GetString());
            Assert.Equal(@"C:\Users\test\Videos\rec-edited.mp4", root.GetProperty("output").GetString());
            Assert.Equal(21, root.GetProperty("crf").GetInt32());

            var segments = root.GetProperty("segments");
            Assert.Equal(2, segments.GetArrayLength());
            Assert.Equal(0L, segments[0].GetProperty("start_ms").GetInt64());
            Assert.Equal(5000L, segments[0].GetProperty("end_ms").GetInt64());
            Assert.Equal(7500L, segments[1].GetProperty("start_ms").GetInt64());
            Assert.Equal(12_250L, segments[1].GetProperty("end_ms").GetInt64());

            var webcam = root.GetProperty("webcam");
            Assert.Equal(1, webcam.GetProperty("stream_index").GetInt32());
            Assert.Equal(@"C:\Users\test\session\mask.png", webcam.GetProperty("mask_png").GetString());

            var rect = webcam.GetProperty("rect");
            Assert.Equal(10, rect.GetProperty("x").GetInt32());
            Assert.Equal(20, rect.GetProperty("y").GetInt32());
            Assert.Equal(320, rect.GetProperty("w").GetInt32());
            Assert.Equal(240, rect.GetProperty("h").GetInt32());
        }

        [Fact]
        public void Serialize_OmitsTheWebcamWhenTheOverlayIsDisabled()
        {
            var args = Sample();
            args.Webcam = null;

            using var doc = JsonDocument.Parse(args.ToJson());
            Assert.False(doc.RootElement.TryGetProperty("webcam", out _));
        }

        [Fact]
        public void RoundTrips()
        {
            var original = Sample();
            var loaded = RenderArgs.FromJson(original.ToJson());

            Assert.Equal(RenderArgs.CurrentVersion, loaded.Version);
            Assert.Equal(original.Input, loaded.Input);
            Assert.Equal(original.Output, loaded.Output);
            Assert.Equal(original.Crf, loaded.Crf);

            Assert.Equal(2, loaded.Segments.Count);
            Assert.Equal(0L, loaded.Segments[0].StartMs);
            Assert.Equal(5000L, loaded.Segments[0].EndMs);
            Assert.Equal(7500L, loaded.Segments[1].StartMs);
            Assert.Equal(12_250L, loaded.Segments[1].EndMs);

            Assert.NotNull(loaded.Webcam);
            Assert.Equal(1, loaded.Webcam.StreamIndex);
            Assert.Equal(original.Webcam.MaskPng, loaded.Webcam.MaskPng);
            Assert.Equal(10, loaded.Webcam.Rect.X);
            Assert.Equal(20, loaded.Webcam.Rect.Y);
            Assert.Equal(320, loaded.Webcam.Rect.W);
            Assert.Equal(240, loaded.Webcam.Rect.H);
        }

        [Fact]
        public void Deserialize_AcceptsTheDocumentedContractLiterally()
        {
            // the exact shape the Rust tool is specified against — a missing "webcam" is a
            // no-overlay render, not a parse failure.
            const string json =
                "{\"version\":1,\"input\":\"in.mp4\",\"output\":\"out.mp4\"," +
                "\"segments\":[{\"start_ms\":0,\"end_ms\":5000}],\"crf\":21}";

            var loaded = RenderArgs.FromJson(json);

            Assert.Equal(1, loaded.Version);
            Assert.Equal("in.mp4", loaded.Input);
            Assert.Equal("out.mp4", loaded.Output);
            Assert.Null(loaded.Webcam);
            Assert.Equal(21, loaded.Crf);
            Assert.Single(loaded.Segments);
            Assert.Equal(5000L, loaded.Segments[0].EndMs);
        }

        [Fact]
        public void ToSegments_ConvertsAKeepList()
        {
            var doc = new VideoEditDocument();
            doc.AddCut(2000, 3000);

            var segments = RenderArgs.ToSegments(doc.GetKeepSegments(10_000));

            Assert.Equal(2, segments.Count);
            Assert.Equal(0L, segments[0].StartMs);
            Assert.Equal(2000L, segments[0].EndMs);
            Assert.Equal(3000L, segments[1].StartMs);
            Assert.Equal(10_000L, segments[1].EndMs);

            Assert.Empty(RenderArgs.ToSegments(null));
        }
    }
}
