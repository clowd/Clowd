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
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080 },
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
                    Style = "vision",
                    Size = 1.5,
                    ClickAnimation = "ripple",
                    ClickColor = 0xFF3366CC,
                    FillOpacity = 0.4,
                    HoldSize = 1.25,
                    ClickSize = 0.75,
                    AnimationSpeed = 2.0,
                },
                // the glyph's decoration lives on the ITEM, not the content — a cursor row is the
                // one overlay that carries one by default (a shadow, replaced here by a glow)
                Surround = new Surround
                {
                    Kind = SurroundKind.Glow,
                    Color = 0x8022CCFF,
                    Size = 0.11,
                    Distance = 0, // a glow sits on the glyph; only a shadow falls
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
                    PauseBreakMs = 1500,
                    TextColor = 0xFF22DD88,
                    BackgroundColor = 0x66112233,
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

            var restoredCursorItem = restored.Items.Single(i => i.Id == cursorItem.Id);
            var cursor = (CursorContent)restoredCursorItem.Content;
            Assert.Equal(((CursorContent)cursorItem.Content).SourceId, cursor.SourceId);
            Assert.Equal("vision", cursor.Style);
            Assert.Equal(1.5, cursor.Size);
            Assert.Equal("ripple", cursor.ClickAnimation);
            Assert.Equal(0xFF3366CCu, cursor.ClickColor);
            Assert.Equal(0.4, cursor.FillOpacity);
            Assert.Equal(1.25, cursor.HoldSize);
            Assert.Equal(0.75, cursor.ClickSize);
            Assert.Equal(2.0, cursor.AnimationSpeed);

            var effect = restoredCursorItem.Surround;
            Assert.Equal(SurroundKind.Glow, effect.Kind);
            Assert.Equal(0x8022CCFFu, effect.Color);
            Assert.Equal(0.11, effect.Size);
            Assert.Equal(0, effect.Distance);

            var keyboard = (KeyboardContent)restored.Items.Single(i => i.Id == keyboardItem.Id).Content;
            Assert.Equal(((KeyboardContent)keyboardItem.Content).SourceId, keyboard.SourceId);
            Assert.Equal(36, keyboard.FontSize);
            Assert.Equal(500, keyboard.LingerMs);
            Assert.Equal(1500, keyboard.PauseBreakMs);
            Assert.Equal(0xFF22DD88u, keyboard.TextColor);
            Assert.Equal(0x66112233u, keyboard.BackgroundColor);
        }

        /// <summary>The fade time is gone: rows now leave with the item's exit transition. The
        /// property was never on a shipped wire format, and the reader skips unmapped members, so
        /// a project that somehow carries one still opens — the old value is simply dropped.</summary>
        [Fact]
        public void A_keyboard_item_no_longer_writes_a_fade_and_tolerates_a_stale_one()
        {
            var project = OverlayProject(out _, out _, out var keyboardItem);
            Assert.DoesNotContain("fadeMs", project.ToJson(), StringComparison.OrdinalIgnoreCase);

            var stale = project.ToJson().Replace(
                "\"lingerMs\": 500", "\"fadeMs\": 250,\n        \"lingerMs\": 500");
            var restored = Project.FromJson(stale);

            var keyboard = (KeyboardContent)restored.Items.Single(i => i.Id == keyboardItem.Id).Content;
            Assert.Equal(500, keyboard.LingerMs);
            Assert.Empty(restored.Validate());
        }

        [Fact]
        public void Round_trip_preserves_the_source_capture_fields()
        {
            var project = OverlayProject(out _, out _, out _);
            var restored = Project.FromJson(project.ToJson());

            var source = restored.Sources.Single();
            Assert.Equal(@"C:\rec\input-capture.jsonl", source.InputCapturePath);
        }

        [Fact]
        public void Defaults_match_the_design()
        {
            var cursor = new CursorContent();
            Assert.Equal("vision", cursor.Style);
            Assert.Equal(1.0, cursor.Size);
            Assert.Equal("none", cursor.ClickAnimation);
            Assert.Equal(0xFFFF0000u, cursor.ClickColor);
            Assert.Equal(CursorContent.DefaultFillOpacity, cursor.FillOpacity);
            Assert.Equal(1.0, cursor.HoldSize);
            Assert.Equal(1.0, cursor.ClickSize);
            Assert.Equal(1.0, cursor.AnimationSpeed);

            var keyboard = new KeyboardContent();
            Assert.Equal(40, keyboard.FontSize);
            Assert.Equal(1000, keyboard.LingerMs);
            Assert.Equal(1000, keyboard.PauseBreakMs);
            Assert.Equal(0xFFFFFFFFu, keyboard.TextColor);
            Assert.Equal(0x8C000000u, keyboard.BackgroundColor); // black at 55%
        }

        [Fact]
        public void Overlay_content_clones_are_independent()
        {
            var cursor = new CursorContent
            {
                SourceId = Guid.NewGuid(), Style = "vision", Variant = "light", Size = 2.0,
                Debounce = false, FillOpacity = 0.6, HoldSize = 1.5, ClickSize = 0.5,
                AnimationSpeed = 3.0,
            };
            var cursorCopy = (CursorContent)cursor.Clone();
            cursorCopy.Style = "native";
            cursorCopy.Size = 0.5;
            cursorCopy.Debounce = true;
            cursorCopy.FillOpacity = 0.1;
            cursorCopy.HoldSize = 4;
            cursorCopy.ClickSize = 4;
            cursorCopy.AnimationSpeed = 4;
            Assert.Equal("vision", cursor.Style);
            Assert.Equal("light", cursor.Variant);
            Assert.Equal(2.0, cursor.Size);
            Assert.False(cursor.Debounce);
            Assert.Equal(0.6, cursor.FillOpacity);
            Assert.Equal(1.5, cursor.HoldSize);
            Assert.Equal(0.5, cursor.ClickSize);
            Assert.Equal(3.0, cursor.AnimationSpeed);
            Assert.Equal(cursor.SourceId, cursorCopy.SourceId);

            var keyboard = new KeyboardContent
            {
                SourceId = Guid.NewGuid(), FontSize = 40, LingerMs = 100, TextColor = 0xFF00FF00,
            };
            var keyboardCopy = (KeyboardContent)keyboard.Clone();
            keyboardCopy.FontSize = 12;
            keyboardCopy.LingerMs = 900;
            keyboardCopy.TextColor = 0xFF0000FF;
            Assert.Equal(40, keyboard.FontSize);
            Assert.Equal(100, keyboard.LingerMs);
            Assert.Equal(0xFF00FF00u, keyboard.TextColor);
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
        [InlineData(5.1)]
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
        [InlineData(5.0)]
        public void Validate_accepts_boundary_cursor_sizes(double size)
        {
            var project = OverlayProject(out _, out var cursorItem, out _);
            ((CursorContent)cursorItem.Content).Size = size;

            Assert.Empty(project.Validate());
        }

        [Theory]
        [InlineData(0.2)]
        [InlineData(4.1)]
        [InlineData(0)]
        [InlineData(Double.NaN)]
        public void Validate_rejects_out_of_range_highlight_factors(double factor)
        {
            var project = OverlayProject(out _, out var cursorItem, out _);
            var cursor = (CursorContent)cursorItem.Content;
            cursor.HoldSize = factor;
            cursor.ClickSize = factor;
            cursor.AnimationSpeed = factor;

            var errors = project.Validate();
            Assert.Contains(errors, e => e.Contains("hold size"));
            Assert.Contains(errors, e => e.Contains("click size"));
            Assert.Contains(errors, e => e.Contains("animation speed"));
        }

        [Theory]
        [InlineData(0.25)]
        [InlineData(1.0)]
        [InlineData(4.0)]
        public void Validate_accepts_boundary_highlight_factors(double factor)
        {
            var project = OverlayProject(out _, out var cursorItem, out _);
            var cursor = (CursorContent)cursorItem.Content;
            cursor.HoldSize = factor;
            cursor.ClickSize = factor;
            cursor.AnimationSpeed = factor;

            Assert.Empty(project.Validate());
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(1.1)]
        [InlineData(Double.NaN)]
        public void Validate_rejects_out_of_range_fill_opacity(double opacity)
        {
            var project = OverlayProject(out _, out var cursorItem, out _);
            ((CursorContent)cursorItem.Content).FillOpacity = opacity;

            Assert.Contains(project.Validate(), e => e.Contains("fill opacity"));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        public void Validate_accepts_boundary_fill_opacity(double opacity)
        {
            var project = OverlayProject(out _, out var cursorItem, out _);
            ((CursorContent)cursorItem.Content).FillOpacity = opacity;

            Assert.Empty(project.Validate());
        }

        [Theory]
        [InlineData(7.9, 0, 0)]
        [InlineData(201, 0, 0)]
        [InlineData(Double.NaN, 0, 0)]
        [InlineData(28, -1, 0)]
        [InlineData(28, 10_001, 0)]
        [InlineData(28, 0, -1)]
        [InlineData(28, 0, 10_001)]
        public void Validate_rejects_out_of_range_keyboard_values(double fontSize, int linger, int pause)
        {
            var project = OverlayProject(out _, out _, out var keyboardItem);
            var content = (KeyboardContent)keyboardItem.Content;
            content.FontSize = fontSize;
            content.LingerMs = linger;
            content.PauseBreakMs = pause;

            Assert.Contains(project.Validate(), e => e.Contains("keyboard"));
        }

        [Theory]
        [InlineData(8, 0, 0)]
        [InlineData(200, 10_000, 10_000)]
        public void Validate_accepts_boundary_keyboard_values(double fontSize, int linger, int pause)
        {
            var project = OverlayProject(out _, out _, out var keyboardItem);
            var content = (KeyboardContent)keyboardItem.Content;
            content.FontSize = fontSize;
            content.LingerMs = linger;
            content.PauseBreakMs = pause;

            Assert.Empty(project.Validate());
        }

        /// <summary>A keystroke row's entry/exit are ordinary picture transitions — the ramp-only
        /// rule belongs to effect items, and must not reach across to these.</summary>
        [Theory]
        [InlineData(TransitionKind.SlideUp, TransitionKind.Fade)]
        [InlineData(TransitionKind.Wipe, TransitionKind.SlideDown)]
        public void Validate_accepts_row_transitions_on_a_keyboard_item(TransitionKind entry, TransitionKind exit)
        {
            var project = OverlayProject(out _, out _, out var keyboardItem);
            keyboardItem.Entry = new Transition { Kind = entry, DurationTicks = Ms(300) };
            keyboardItem.Exit = new Transition { Kind = exit, DurationTicks = Ms(300) };

            Assert.Empty(project.Validate());
        }

        // -------------------------------------------------------------------- recording project

        [Fact]
        public void Build_wires_the_capture_path()
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
                InputCapturePath = @"C:\rec\input-capture.jsonl",
                FpsNum = 30,
                FpsDen = 1,
                Segments = new[] { new KeepSegment(0, Ms(30_000)) },
            };

            var project = RecordingProject.Build(spec);

            Assert.Empty(project.Validate());
            var source = project.Sources.Single();
            Assert.Equal(@"C:\rec\input-capture.jsonl", source.InputCapturePath);

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
