using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The item surround (shadow / glow / outline): the model's own rules, the fraction→pixel maths
    /// the compositor and the editor's tiles share, and the inspector's SURROUND section — which
    /// items offer it, what a style change seeds, that a dial writes the whole row, and what the
    /// tiles preview. Framework-free like <see cref="EffectInspectorTests"/> — no Avalonia here.
    ///
    /// Not to be confused with <see cref="EffectInspectorTests"/>, which covers the effect
    /// <i>items</i> (speed/zoom) on the pinned effect row — a different thing entirely.
    /// </summary>
    public class SurroundTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>A screen row of two linked segments (so the fan-out is visible), an image item,
        /// and a text card — one of each thing the section must and must not appear for.</summary>
        private static (EditorSession Session, SelectedItemViewModel Vm) NewInspector(
            out Item video, out Item image, out Item text)
        {
            var sourceId = Guid.NewGuid();
            var videoTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var overlayTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Overlay", Order = 1 };

            var group = Guid.NewGuid();
            video = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = videoTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(5_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0 },
                LinkGroupId = group,
            };
            var second = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = videoTrack.Id,
                TimelineStartTicks = Ms(5_000),
                DurationTicks = Ms(5_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = Ms(5_000) },
                LinkGroupId = group,
            };
            image = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = overlayTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(4_000),
                Content = new ImageContent { Path = @"C:\pics\logo.png" },
            };
            text = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = overlayTrack.Id,
                TimelineStartTicks = Ms(4_000),
                DurationTicks = Ms(4_000),
                Content = new TextContent { Text = "hello", Size = 48 },
            };

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\input.mp4",
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                        },
                    },
                },
                Tracks = { videoTrack, overlayTrack },
                Items = { video, second, image, text },
            };

            var session = new EditorSession(project, null, null);
            return (session, new SelectedItemViewModel { Session = session });
        }

        private static Surround[] RowSurrounds(EditorSession session, Item member) =>
            session.Project.Items.Where(i => i.TrackId == member.TrackId).Select(i => i.Surround).ToArray();

        // ----------------------------------------------------------------------------- the model

        [Fact]
        public void No_surround_is_a_null_surround_and_a_stored_None_is_a_validation_error()
        {
            var (session, _) = NewInspector(out var video, out _, out _);
            Assert.Null(video.Surround);
            Assert.Null(Surround.Create(SurroundKind.None, cursor: false));
            Assert.Empty(session.Project.Validate());

            video.Surround = new Surround { Kind = SurroundKind.None };
            Assert.Contains("kind None", String.Join("\n", session.Project.Validate()));
        }

        [Theory]
        [InlineData(0.6, 0)]
        [InlineData(0, 0.6)]
        [InlineData(Double.NaN, 0)]
        public void Dials_outside_the_models_range_are_rejected(double size, double distance)
        {
            var (session, _) = NewInspector(out var video, out _, out _);
            video.Surround = new Surround
            {
                Kind = SurroundKind.Shadow,
                Size = size,
                Distance = distance,
            };

            Assert.NotEmpty(session.Project.Validate());
        }

        [Fact]
        public void Each_style_seeds_its_own_numbers_and_a_cursors_are_bigger()
        {
            // the reference extent is a glyph's tens of pixels against a picture's hundreds, so the
            // fraction that reads as a shadow on one would be a smear on the other
            var (pictureColor, pictureSize, pictureDistance) =
                Surround.DefaultsFor(SurroundKind.Shadow, cursor: false);
            var (cursorColor, cursorSize, cursorDistance) =
                Surround.DefaultsFor(SurroundKind.Shadow, cursor: true);

            Assert.Equal(Surround.DefaultShadowColor, pictureColor);
            Assert.Equal(pictureColor, cursorColor);
            Assert.True(cursorSize > pictureSize);
            Assert.True(cursorDistance > pictureDistance);

            // a glow and an outline never fall anywhere: distance is a shadow's alone
            Assert.Equal(0, Surround.DefaultsFor(SurroundKind.Glow, cursor: true).Distance);
            Assert.Equal(0, Surround.DefaultsFor(SurroundKind.Outline, cursor: true).Distance);
        }

        [Fact]
        public void Clone_carries_every_field_and_shares_nothing()
        {
            var surround = new Surround
            {
                Kind = SurroundKind.Outline,
                Color = 0x40FF00FF,
                Size = 0.02,
                Distance = 0.05,
            };
            var clone = surround.Clone();

            Assert.Equal(surround.Kind, clone.Kind);
            Assert.Equal(surround.Color, clone.Color);
            Assert.Equal(surround.Size, clone.Size);
            Assert.Equal(surround.Distance, clone.Distance);

            clone.Size = 0.4;
            Assert.Equal(0.02, surround.Size);
        }

        [Fact]
        public void Split_carries_the_surround_onto_both_halves()
        {
            var (session, _) = NewInspector(out var video, out _, out _);
            session.EditItem(video.Id,
                i => i.Surround = Surround.Create(SurroundKind.Glow, cursor: false),
                "test", structural: false, origin: new object());

            Assert.True(session.SplitAt(video.Id, Ms(2_000)));

            var halves = session.Project.Items
                .Where(i => i.TrackId == video.TrackId && i.TimelineStartTicks < Ms(5_000))
                .ToList();
            Assert.Equal(2, halves.Count);
            Assert.All(halves, i => Assert.Equal(SurroundKind.Glow, i.Surround.Kind));
            // clones, not the one object: an edit to either half must not reach the other
            Assert.NotSame(halves[0].Surround, halves[1].Surround);
        }

        // ------------------------------------------------------------------------------- pixels

        [Fact]
        public void Fractions_scale_with_the_items_shorter_side()
        {
            // 400 wide, 200 tall: the shorter side is what a blur must not outgrow
            var extent = SurroundMath.ReferenceExtent(new SKRect(0, 0, 400, 200));
            Assert.Equal(200, extent);

            var surround = new Surround { Kind = SurroundKind.Shadow, Size = 0.05, Distance = 0.1 };
            Assert.Equal(10, SurroundMath.BlurPx(surround, extent));
            // the light is fixed at 45°, so a distance of 20px offsets both axes by 20·cos45
            Assert.Equal(20 * Math.Sqrt(0.5), SurroundMath.OffsetPx(surround, extent), 6);
        }

        [Fact]
        public void A_decoration_is_only_built_when_it_would_draw_something()
        {
            Assert.Null(SurroundMath.CreateDecoration(null, 100));
            Assert.Null(SurroundMath.CreateDecoration(
                new Surround { Kind = SurroundKind.Shadow, Size = 0.1 }, extentPx: 0));
            // a transparent colour, a shadow with nowhere to fall and no blur, a glow with no
            // spread, and an outline thinner than half a pixel all draw nothing
            Assert.Null(SurroundMath.CreateDecoration(
                new Surround { Kind = SurroundKind.Glow, Color = 0x00FFFFFF, Size = 0.2 }, 100));
            Assert.Null(SurroundMath.CreateDecoration(
                new Surround { Kind = SurroundKind.Shadow, Size = 0, Distance = 0 }, 100));
            Assert.Null(SurroundMath.CreateDecoration(
                new Surround { Kind = SurroundKind.Glow, Size = 0 }, 100));
            Assert.Null(SurroundMath.CreateDecoration(
                new Surround { Kind = SurroundKind.Outline, Size = 0.001 }, 100));

            using var shadow = SurroundMath.CreateDecoration(
                new Surround { Kind = SurroundKind.Shadow, Size = 0.03, Distance = 0.03 }, 100);
            using var outline = SurroundMath.CreateDecoration(
                new Surround { Kind = SurroundKind.Outline, Size = 0.03 }, 100);
            Assert.NotNull(shadow);
            Assert.NotNull(outline);
        }

        // ---------------------------------------------------------------------- section gating

        [Fact]
        public void Pictures_offer_the_section_and_text_does_not()
        {
            var (session, vm) = NewInspector(out var video, out var image, out var text);

            session.Select(video.Id);
            Assert.True(vm.ShowSurround);

            session.Select(image.Id);
            Assert.True(vm.ShowSurround);

            session.Select(text.Id);
            Assert.False(vm.ShowSurround);
        }

        [Fact]
        public void The_tiles_start_on_None_with_no_rows_below_them()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            Assert.True(vm.SurroundNone);
            Assert.False(vm.SurroundShadow);
            Assert.False(vm.ShowSurroundColor);
            Assert.False(vm.ShowSurroundSize);
            Assert.False(vm.ShowSurroundDistance);
        }

        // --------------------------------------------------------------------------- the writes

        [Fact]
        public void Picking_a_style_writes_the_whole_row_at_that_styles_defaults()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            vm.SurroundShadow = true;

            var (color, size, distance) = Surround.DefaultsFor(SurroundKind.Shadow, cursor: false);
            Assert.All(RowSurrounds(session, video), e =>
            {
                Assert.Equal(SurroundKind.Shadow, e.Kind);
                Assert.Equal(color, e.Color);
                Assert.Equal(size, e.Size);
                Assert.Equal(distance, e.Distance);
            });
            Assert.Equal(size, vm.SurroundSize);
            Assert.Equal(distance, vm.SurroundDistance);
            Assert.True(vm.ShowSurroundDistance);
            Assert.Equal("Softness", vm.SurroundSizeLabel);
        }

        [Fact]
        public void Switching_style_reseeds_rather_than_carrying_the_numbers_over()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            vm.SurroundShadow = true;
            vm.SurroundSize = 0.2;

            vm.SurroundOutline = true;

            // the dials mean different things per style, so an outline starts at an outline's
            // thickness rather than inheriting a shadow's softness
            var outlineDefaults = Surround.DefaultsFor(SurroundKind.Outline, cursor: false);
            Assert.Equal(outlineDefaults.Size, vm.SurroundSize);
            Assert.All(RowSurrounds(session, video), e =>
            {
                Assert.Equal(SurroundKind.Outline, e.Kind);
                Assert.Equal(outlineDefaults.Size, e.Size);
            });
            Assert.False(vm.ShowSurroundDistance);
            Assert.Equal("Thickness", vm.SurroundSizeLabel);
        }

        [Fact]
        public void Dials_write_the_whole_row_and_clamp_to_the_models_range()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);
            vm.SurroundShadow = true;

            vm.SurroundSize = 0.12;
            vm.SurroundDistance = 0.08;
            vm.SurroundColorHex = "#4000FF00";

            Assert.All(RowSurrounds(session, video), e =>
            {
                Assert.Equal(0.12, e.Size);
                Assert.Equal(0.08, e.Distance);
                Assert.Equal(0x4000FF00u, e.Color);
            });

            vm.SurroundSize = 5;
            Assert.Equal(SelectedItemViewModel.MaxSurroundSize, vm.SurroundSize);
            vm.SurroundDistance = Double.NaN;
            Assert.Equal(0, vm.SurroundDistance);

            // a half-typed colour stays in the well and never reaches the model
            vm.SurroundColorHex = "#40FF";
            Assert.All(RowSurrounds(session, video), e => Assert.Equal(0x4000FF00u, e.Color));
        }

        [Fact]
        public void Choosing_None_removes_the_surround_from_the_row()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);
            vm.SurroundGlow = true;
            Assert.All(RowSurrounds(session, video), Assert.NotNull);

            vm.SurroundNone = true;

            Assert.All(RowSurrounds(session, video), Assert.Null);
            Assert.Empty(session.Project.Validate());
        }

        // ------------------------------------------------------------------------- the previews

        [Fact]
        public void The_picked_tile_previews_the_live_dials_and_the_others_their_own_defaults()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            vm.SurroundShadow = true;
            vm.SurroundSize = 0.2;
            vm.SurroundDistance = 0.15;
            vm.SurroundColorHex = "#40FF0000";

            var shadow = vm.SurroundPreviewShadow;
            Assert.Equal(SurroundKind.Shadow, shadow.Kind);
            Assert.Equal(0.2, shadow.Size);
            Assert.Equal(0.15, shadow.Distance);
            Assert.Equal(0x40FF0000u, shadow.Color);

            // the two unpicked styles have no configuration of their own yet
            var glowDefaults = Surround.DefaultsFor(SurroundKind.Glow, cursor: false);
            Assert.Equal(SurroundKind.Glow, vm.SurroundPreviewGlow.Kind);
            Assert.Equal(glowDefaults.Size, vm.SurroundPreviewGlow.Size);
            Assert.Equal(glowDefaults.Color, vm.SurroundPreviewGlow.Color);
            Assert.Equal(SurroundKind.Outline, vm.SurroundPreviewOutline.Kind);
            Assert.Equal(Surround.DefaultsFor(SurroundKind.Outline, cursor: false).Size,
                vm.SurroundPreviewOutline.Size);
        }

        [Fact]
        public void The_previews_are_raised_whenever_a_dial_moves()
        {
            // the tiles bind these, and a fresh object per read is what repaints them — so the
            // notification is the whole mechanism
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);
            vm.SurroundShadow = true;

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SurroundSize = 0.11;
            Assert.Contains(nameof(SelectedItemViewModel.SurroundPreviewShadow), raised);

            raised.Clear();
            vm.SurroundColorHex = "#4000FF00";
            Assert.Contains(nameof(SelectedItemViewModel.SurroundPreviewShadow), raised);

            raised.Clear();
            vm.SurroundDistance = 0.07;
            Assert.Contains(nameof(SelectedItemViewModel.SurroundPreviewShadow), raised);

            // and picking a style moves all three: the newly picked one starts following the dials
            raised.Clear();
            vm.SurroundGlow = true;
            Assert.Contains(nameof(SelectedItemViewModel.SurroundPreviewGlow), raised);
            Assert.Contains(nameof(SelectedItemViewModel.SurroundPreviewShadow), raised);
        }

        [Fact]
        public void Picking_a_style_previews_its_seeded_numbers_not_the_previous_styles()
        {
            // regression: the notification used to be raised while the dials still held the style
            // being left behind — picking one from None previewed a zero-sized surround, which draws
            // nothing, so the tile looked empty until a dial was touched.
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);
            Assert.True(vm.SurroundNone);

            Surround previewedAtNotification = null;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SelectedItemViewModel.SurroundPreviewShadow))
                    previewedAtNotification = vm.SurroundPreviewShadow;
            };

            vm.SurroundShadow = true;

            var (_, size, distance) = Surround.DefaultsFor(SurroundKind.Shadow, cursor: false);
            Assert.NotNull(previewedAtNotification);
            Assert.Equal(size, previewedAtNotification.Size);
            Assert.Equal(distance, previewedAtNotification.Distance);
        }

        [Fact]
        public void A_cursor_tile_previews_the_glyphs_bigger_defaults()
        {
            var (session, vm) = NewInspectorWithCapture(out var cursor);
            session.Select(cursor.Id);

            // the picked style is Shadow (the cursor factory's default), so Glow is previewing a
            // default — and a cursor's defaults are the bigger ones
            Assert.Equal(Surround.DefaultsFor(SurroundKind.Glow, cursor: true).Size,
                vm.SurroundPreviewGlow.Size);
            Assert.True(vm.SurroundPreviewGlow.Size
                > Surround.DefaultsFor(SurroundKind.Glow, cursor: false).Size);
        }

        [Fact]
        public void The_section_re_reads_a_change_from_elsewhere()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            session.EditItem(video.Id, i => i.Surround = new Surround
            {
                Kind = SurroundKind.Glow,
                Color = 0x80112233,
                Size = 0.09,
            }, "test", structural: false, origin: new object());

            Assert.True(vm.SurroundGlow);
            Assert.False(vm.SurroundNone);
            Assert.Equal("#80112233", vm.SurroundColorHex);
            Assert.Equal(0.09, vm.SurroundSize);
            Assert.Equal("Spread", vm.SurroundSizeLabel);
            Assert.True(vm.ShowSurroundColor);
            Assert.False(vm.ShowSurroundDistance);
        }

        [Fact]
        public void A_cursor_row_starts_with_a_shadow_at_the_glyphs_own_numbers()
        {
            // the cursor factory's default, and the one place the section's defaults differ
            var (session, vm) = NewInspectorWithCapture(out var cursor);
            session.Select(cursor.Id);

            var (color, size, distance) = Surround.DefaultsFor(SurroundKind.Shadow, cursor: true);
            Assert.True(vm.ShowSurround);
            Assert.True(vm.SurroundShadow);
            Assert.Equal(size, vm.SurroundSize);
            Assert.Equal(size, vm.DefaultSurroundSize);
            Assert.Equal(distance, vm.SurroundDistance);
            Assert.All(RowSurrounds(session, cursor), e =>
            {
                Assert.Equal(SurroundKind.Shadow, e.Kind);
                Assert.Equal(color, e.Color);
                Assert.Equal(size, e.Size);
            });
        }

        /// <summary>A recording whose source carries input capture, with a cursor row added by the
        /// session's own factory — the only way to get the row's real defaults.</summary>
        private static (EditorSession Session, SelectedItemViewModel Vm) NewInspectorWithCapture(
            out Item cursor)
        {
            var sourceId = Guid.NewGuid();
            var screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\input.mp4",
                        InputCapturePath = @"C:\rec\input-capture.jsonl",
                        CursorStreamIndex = 2,
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 2, Kind = StreamKind.Video, Width = 512, Height = 512, DurationTicks = Ms(60_000) },
                        },
                    },
                },
                Tracks = { screenTrack },
                Items =
                {
                    new Item
                    {
                        Id = Guid.NewGuid(),
                        TrackId = screenTrack.Id,
                        TimelineStartTicks = 0,
                        DurationTicks = Ms(10_000),
                        Content = new MediaContent { SourceId = sourceId, StreamIndex = 0 },
                    },
                },
            };

            var session = new EditorSession(project, null, null);
            cursor = session.AddCursorTrack();
            return (session, new SelectedItemViewModel { Session = session });
        }
    }
}
