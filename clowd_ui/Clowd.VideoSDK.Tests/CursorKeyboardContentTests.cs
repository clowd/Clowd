using System;
using System.Linq;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The input-overlay item model: <see cref="CursorContent"/>/<see cref="KeyboardContent"/>
    /// serialization (discriminators included), clones, the <see cref="Project.Validate"/>
    /// accept/reject matrix that keeps overlay rows well-formed, and the
    /// <see cref="RecordingProject.Build"/> wiring of the new <see cref="Source"/> fields.
    /// </summary>
    public class CursorKeyboardContentTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>A recording-shaped fixture: one source, a screen row playing it, and a linked
        /// cursor row + keyboard row above it — every overlay rule exercised without touching the
        /// filesystem.</summary>
        private static Project OverlayProject(out Item screenItem, out Item cursorItem, out Item keyboardItem)
        {
            var source = new Source
            {
                Id = Guid.NewGuid(),
                Path = @"C:\rec\in.mp4",
                InputCapturePath = @"C:\rec\input-capture.jsonl",
                CursorStreamIndex = 2,
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080 },
                    new SourceStream { Index = 2, Kind = StreamKind.Video, Width = 512, Height = 512 },
                },
            };

            var screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var keysTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Keys", Order = 1 };
            var cursorTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Cursor", Order = 2 };

            var group = Guid.NewGuid();
            screenItem = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = screenTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(20_000),
                Content = new MediaContent { SourceId = source.Id, StreamIndex = 0 },
                LinkGroupId = group,
            };
            cursorItem = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = cursorTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(20_000),
                Content = new CursorContent
                {
                    SourceId = source.Id,
                    StreamIndex = 2,
                    Style = "material",
                    Size = 1.5,
                    DropShadow = true,
                    ClickAnimation = "ripple",
                    ClickColor = 0xFF3366CC,
                },
                LinkGroupId = group,
            };
            keyboardItem = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = keysTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(20_000),
                Content = new KeyboardContent
                {
                    SourceId = source.Id,
                    FontSize = 36,
                    LingerMs = 500,
                    FadeMs = 200,
                    PauseBreakMs = 1500,
                },
                Transform = new Transform { X = 0.5, Y = 0.85, Scale = 0.5 },
                LinkGroupId = group,
            };

            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources = { source },
                Tracks = { screenTrack, keysTrack, cursorTrack },
                Items = { screenItem, cursorItem, keyboardItem },
            };
        }

        // ---------------------------------------------------------------------------- round trip

        [Fact]
        public void Overlay_project_round_trips_byte_identical_and_valid()
        {
            var project = OverlayProject(out _, out _, out _);
            project.Normalize();
            Assert.Empty(project.Validate());

            var json = project.ToJson();
            var restored = Project.FromJson(json);

            Assert.Equal(json, restored.ToJson());
            Assert.Empty(restored.Validate());
        }

        [Fact]
        public void Round_trip_uses_the_wire_discriminators()
        {
            var project = OverlayProject(out _, out _, out _);
            var json = project.ToJson();

            Assert.Contains("\"$type\": \"cursor\"", json);
            Assert.Contains("\"$type\": \"keyboard\"", json);
        }

        [Fact]
        public void Round_trip_preserves_overlay_content_fields()
        {
            var project = OverlayProject(out _, out var cursorItem, out var keyboardItem);
            var restored = Project.FromJson(project.ToJson());

            var cursor = (CursorContent)restored.Items.Single(i => i.Id == cursorItem.Id).Content;
            Assert.Equal(((CursorContent)cursorItem.Content).SourceId, cursor.SourceId);
            Assert.Equal(2, cursor.StreamIndex);
            Assert.Equal("material", cursor.Style);
            Assert.Equal(1.5, cursor.Size);
            Assert.True(cursor.DropShadow);
            Assert.Equal("ripple", cursor.ClickAnimation);
            Assert.Equal(0xFF3366CCu, cursor.ClickColor);

            var keyboard = (KeyboardContent)restored.Items.Single(i => i.Id == keyboardItem.Id).Content;
            Assert.Equal(((KeyboardContent)keyboardItem.Content).SourceId, keyboard.SourceId);
            Assert.Equal(36, keyboard.FontSize);
            Assert.Equal(500, keyboard.LingerMs);
            Assert.Equal(200, keyboard.FadeMs);
            Assert.Equal(1500, keyboard.PauseBreakMs);
        }

        [Fact]
        public void Round_trip_preserves_the_source_capture_fields()
        {
            var project = OverlayProject(out _, out _, out _);
            var restored = Project.FromJson(project.ToJson());

            var source = restored.Sources.Single();
            Assert.Equal(@"C:\rec\input-capture.jsonl", source.InputCapturePath);
            Assert.Equal(2, source.CursorStreamIndex);
        }

        [Fact]
        public void Defaults_match_the_design()
        {
            var cursor = new CursorContent();
            Assert.Equal(-1, cursor.StreamIndex);
            Assert.Equal("ios-glyph", cursor.Style);
            Assert.Equal(1.0, cursor.Size);
            Assert.False(cursor.DropShadow);
            Assert.Equal("none", cursor.ClickAnimation);
            Assert.Equal(0xFFFF0000u, cursor.ClickColor);

            var keyboard = new KeyboardContent();
            Assert.Equal(28, keyboard.FontSize);
            Assert.Equal(300, keyboard.LingerMs);
            Assert.Equal(250, keyboard.FadeMs);
            Assert.Equal(1000, keyboard.PauseBreakMs);
        }

        [Fact]
        public void Overlay_content_clones_are_independent()
        {
            var cursor = new CursorContent { SourceId = Guid.NewGuid(), StreamIndex = 2, Style = "fluent", Size = 2.0 };
            var cursorCopy = (CursorContent)cursor.Clone();
            cursorCopy.Style = "native";
            cursorCopy.Size = 0.5;
            Assert.Equal("fluent", cursor.Style);
            Assert.Equal(2.0, cursor.Size);
            Assert.Equal(cursor.SourceId, cursorCopy.SourceId);
            Assert.Equal(2, cursorCopy.StreamIndex);

            var keyboard = new KeyboardContent { SourceId = Guid.NewGuid(), FontSize = 40, LingerMs = 100 };
            var keyboardCopy = (KeyboardContent)keyboard.Clone();
            keyboardCopy.FontSize = 12;
            keyboardCopy.LingerMs = 900;
            Assert.Equal(40, keyboard.FontSize);
            Assert.Equal(100, keyboard.LingerMs);
            Assert.Equal(keyboard.SourceId, keyboardCopy.SourceId);
        }

        // ---------------------------------------------------------------------------- validation

        [Fact]
        public void Validate_rejects_overlay_content_on_audio_tracks()
        {
            var project = OverlayProject(out _, out var cursorItem, out _);
            var audio = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 3 };
            project.Tracks.Add(audio);
            cursorItem.TrackId = audio.Id;

            Assert.Contains(project.Validate(), e => e.Contains("non-video track"));
        }

        [Fact]
        public void Validate_rejects_overlay_content_on_effect_tracks()
        {
            var project = OverlayProject(out _, out _, out var keyboardItem);
            var effect = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = "Zoom", Order = 3 };
            project.Tracks.Add(effect);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = effect.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(1_000),
                Content = new ZoomContent(),
            });
            keyboardItem.TrackId = effect.Id;
            keyboardItem.TimelineStartTicks = Ms(2_000); // dodge the overlap check

            var errors = project.Validate();
            Assert.Contains(errors, e => e.Contains("non-video track"));
            Assert.Contains(errors, e => e.Contains("on effect track"));
        }

        [Fact]
        public void Validate_requires_a_link_group_on_overlay_items()
        {
            var project = OverlayProject(out _, out var cursorItem, out var keyboardItem);
            cursorItem.LinkGroupId = null;
            keyboardItem.LinkGroupId = null;

            var errors = project.Validate();
            Assert.Equal(2, errors.Count(e => e.Contains("carries no link group")));
        }

        [Fact]
        public void Validate_rejects_an_unknown_overlay_source()
        {
            var project = OverlayProject(out _, out var cursorItem, out var keyboardItem);
            ((CursorContent)cursorItem.Content).SourceId = Guid.NewGuid();
            ((KeyboardContent)keyboardItem.Content).SourceId = Guid.NewGuid();

            var errors = project.Validate();
            Assert.Equal(2, errors.Count(e => e.Contains("unknown source")));
        }

        [Theory]
        [InlineData(0.2)]
        [InlineData(4.1)]
        [InlineData(0)]
        [InlineData(Double.NaN)]
        public void Validate_rejects_out_of_range_cursor_sizes(double size)
        {
            var project = OverlayProject(out _, out var cursorItem, out _);
            ((CursorContent)cursorItem.Content).Size = size;

            Assert.Contains(project.Validate(), e => e.Contains("cursor size"));
        }

        [Theory]
        [InlineData(0.25)]
        [InlineData(1.0)]
        [InlineData(4.0)]
        public void Validate_accepts_boundary_cursor_sizes(double size)
        {
            var project = OverlayProject(out _, out var cursorItem, out _);
            ((CursorContent)cursorItem.Content).Size = size;

            Assert.Empty(project.Validate());
        }

        [Theory]
        [InlineData(7.9, 0, 0, 0)]
        [InlineData(201, 0, 0, 0)]
        [InlineData(Double.NaN, 0, 0, 0)]
        [InlineData(28, -1, 0, 0)]
        [InlineData(28, 10_001, 0, 0)]
        [InlineData(28, 0, -1, 0)]
        [InlineData(28, 0, 10_001, 0)]
        [InlineData(28, 0, 0, -1)]
        [InlineData(28, 0, 0, 10_001)]
        public void Validate_rejects_out_of_range_keyboard_values(double fontSize, int linger, int fade, int pause)
        {
            var project = OverlayProject(out _, out _, out var keyboardItem);
            var content = (KeyboardContent)keyboardItem.Content;
            content.FontSize = fontSize;
            content.LingerMs = linger;
            content.FadeMs = fade;
            content.PauseBreakMs = pause;

            Assert.Contains(project.Validate(), e => e.Contains("keyboard"));
        }

        [Theory]
        [InlineData(8, 0, 0, 0)]
        [InlineData(200, 10_000, 10_000, 10_000)]
        public void Validate_accepts_boundary_keyboard_values(double fontSize, int linger, int fade, int pause)
        {
            var project = OverlayProject(out _, out _, out var keyboardItem);
            var content = (KeyboardContent)keyboardItem.Content;
            content.FontSize = fontSize;
            content.LingerMs = linger;
            content.FadeMs = fade;
            content.PauseBreakMs = pause;

            Assert.Empty(project.Validate());
        }

        [Fact]
        public void Validate_accepts_a_cursor_without_a_box_stream()
        {
            // a recording whose sidecar exists but whose box stream does not: StreamIndex -1
            // (the native style draws nothing) must not be rejected against Source.Streams.
            var project = OverlayProject(out _, out var cursorItem, out _);
            ((CursorContent)cursorItem.Content).StreamIndex = -1;

            Assert.Empty(project.Validate());
        }

        // -------------------------------------------------------------------- recording project

        [Fact]
        public void Build_wires_the_capture_fields_and_cursor_stream()
        {
            var spec = new RecordingProjectSpec
            {
                InputPath = @"C:\rec\in.mp4",
                Screen = new VideoStreamProbe
                {
                    StreamIndex = 0, Width = 1920, Height = 1080,
                    AvgFrameRateNum = 30, AvgFrameRateDen = 1,
                    DurationTicks = Ms(30_000),
                },
                Cursor = new VideoStreamProbe
                {
                    StreamIndex = 1, Width = 512, Height = 512,
                    AvgFrameRateNum = 30, AvgFrameRateDen = 1,
                    DurationTicks = Ms(30_000),
                },
                InputCapturePath = @"C:\rec\input-capture.jsonl",
                FpsNum = 30,
                FpsDen = 1,
                Segments = new[] { new KeepSegment(0, Ms(30_000)) },
            };

            var project = RecordingProject.Build(spec);

            Assert.Empty(project.Validate());
            var source = project.Sources.Single();
            Assert.Equal(@"C:\rec\input-capture.jsonl", source.InputCapturePath);
            Assert.Equal(1, source.CursorStreamIndex);
            Assert.Contains(source.Streams, s => s.Index == 1 && s.Kind == StreamKind.Video && s.Width == 512);

            // no cursor/keyboard tracks are auto-created — the editor factories own that.
            Assert.All(project.Tracks, t => Assert.NotEqual("Cursor", t.Name));
            Assert.All(project.Tracks, t => Assert.NotEqual("Keys", t.Name));
            Assert.DoesNotContain(project.Items, i => i.Content is CursorContent or KeyboardContent);
        }

        [Fact]
        public void Build_without_capture_leaves_the_source_fields_null()
        {
            var spec = new RecordingProjectSpec
            {
                InputPath = @"C:\rec\in.mp4",
                Screen = new VideoStreamProbe
                {
                    StreamIndex = 0, Width = 1920, Height = 1080,
                    AvgFrameRateNum = 30, AvgFrameRateDen = 1,
                    DurationTicks = Ms(30_000),
                },
                FpsNum = 30,
                FpsDen = 1,
                Segments = new[] { new KeepSegment(0, Ms(30_000)) },
            };

            var source = RecordingProject.Build(spec).Sources.Single();

            Assert.Null(source.InputCapturePath);
            Assert.Null(source.CursorStreamIndex);
        }

        [Fact]
        public void RecordingIds_mint_the_overlay_track_ids()
        {
            var ids = RecordingIds.New(1);

            Assert.NotEqual(Guid.Empty, ids.CursorTrackId);
            Assert.NotEqual(Guid.Empty, ids.KeyboardTrackId);
            Assert.NotEqual(ids.CursorTrackId, ids.KeyboardTrackId);
        }
    }
}
