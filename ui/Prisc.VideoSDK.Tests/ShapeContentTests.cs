using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="ShapeContent"/> outside the pixels (those are <see cref="ShapeComposeTests"/>, the
    /// line geometry <see cref="ShapeLineLayoutTests"/>): the file format, normalization,
    /// validation, the timeline operations every item goes through, and the session's add.
    /// </summary>
    [Collection("EditorSession clipboard")] // the copy/paste tests share its one static clipboard
    public class ShapeContentTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static Project EmptyProject(int width = 1920, int height = 1080) => new Project
        {
            Output = new OutputSettings { WidthPx = width, HeightPx = height, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        private static (Project Project, Track Track, Item Item) OneShape(ShapeContent content = null)
        {
            var project = EmptyProject();
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Overlay", Order = 0 };
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(5_000),
                Content = content ?? new ShapeContent(),
                Transform = new Transform { Scale = 0.25, ScaleY = 0.25 * 16 / 9 },
            };
            project.Tracks.Add(track);
            project.Items.Add(item);
            return (project, track, item);
        }

        /// <summary>A shape with every field off its default, so a copy that drops one shows.</summary>
        private static ShapeContent Everything() => new ShapeContent
        {
            Kind = ShapeKind.Star,
            StrokeColor = "#FF112233",
            StrokeWidth = 11.5,
            Dash = ShapeDash.Dashed,
            Fill = true,
            FillColor = "#80445566",
            CornerRadius = 7,
            PolygonSides = 9,
            StarPoints = 7,
            StarInnerRatio = 0.3,
            StartX = 0.1,
            StartY = 0.9,
            EndX = 0.8,
            EndY = 0.2,
            Bend = -0.4,
            BendSkew = 0.125,
        };

        private static string Json(ItemContent content) =>
            JsonSerializer.Serialize(content, ProjectJsonContext.Default.ItemContent);

        // ----------------------------------------------------------------------- file format

        [Fact]
        public void Round_trips_through_the_project_file()
        {
            var (project, _, item) = OneShape(Everything());

            var json = project.ToJson();
            Assert.Contains("\"$type\": \"shape\"", json);
            Assert.Contains("\"Kind\": \"Star\"", json); // enums as strings: the names are wire contract
            Assert.Contains("\"Dash\": \"Dashed\"", json);
            Assert.DoesNotContain("\"IsLine\"", json); // computed, never stored

            var back = Assert.IsType<ShapeContent>(Project.FromJson(json).Items.Single().Content);
            Assert.Equal(Json(item.Content), Json(back));
        }

        [Fact]
        public void A_bare_shape_entry_reads_as_a_red_rectangle_outline()
        {
            var (project, _, item) = OneShape();
            var json = project.ToJson();
            // strip every shape field, leaving the discriminator alone
            var bare = Regex.Replace(json, @"""\$type"": ""shape""[^}]*", @"""$type"": ""shape""");
            Assert.DoesNotContain("\"StrokeWidth\"", bare);

            var content = Assert.IsType<ShapeContent>(Project.FromJson(bare).Items.Single().Content);
            Assert.Equal(Json(new ShapeContent()), Json(content));
            Assert.Equal(ShapeKind.Rectangle, content.Kind);
            Assert.Equal(ShapeContent.DefaultStrokeColor, content.StrokeColor);
            Assert.Equal(ShapeContent.DefaultStrokeWidth, content.StrokeWidth);
            Assert.False(content.Fill);
            Assert.Equal((0.0, 1.0, 1.0, 0.0), (content.StartX, content.StartY, content.EndX, content.EndY));
        }

        [Fact]
        public void Clone_is_deep_and_equal()
        {
            var original = Everything();
            var copy = Assert.IsType<ShapeContent>(original.Clone());

            Assert.NotSame(original, copy);
            Assert.Equal(Json(original), Json(copy));
        }

        [Fact]
        public void The_line_kinds_and_the_cornered_kinds()
        {
            var lines = Enum.GetValues<ShapeKind>().Where(ShapeContent.IsLineKind);
            Assert.Equal(new[] { ShapeKind.Line, ShapeKind.Arrow, ShapeKind.DoubleArrow }, lines);
            Assert.True(new ShapeContent { Kind = ShapeKind.Arrow }.IsLine);
            Assert.False(new ShapeContent { Kind = ShapeKind.Heart }.IsLine);

            var cornered = Enum.GetValues<ShapeKind>().Where(ShapeContent.HasCorners);
            Assert.Equal(new[] { ShapeKind.Rectangle, ShapeKind.Triangle, ShapeKind.Diamond, ShapeKind.Polygon,
                ShapeKind.Star, ShapeKind.Bubble }, cornered);
        }

        // --------------------------------------------------------------------- normalization

        [Fact]
        public void Normalize_writes_out_the_free_height_once()
        {
            var (project, _, item) = OneShape();
            item.Transform = null;

            project.Normalize();
            Assert.NotNull(item.Transform);
            Assert.Equal(item.Transform.Scale, item.Transform.ScaleY);

            item.Transform.ScaleY = 0.3;
            project.Normalize();
            Assert.Equal(0.3, item.Transform.ScaleY);

            item.Transform = new Transform { Scale = 0.4 };
            project.Normalize();
            Assert.Equal(0.4, item.Transform.ScaleY);
        }

        // ------------------------------------------------------------------------ validation

        [Fact]
        public void A_valid_shape_validates()
        {
            var (project, _, _) = OneShape(Everything());
            Assert.Empty(project.Validate());
        }

        public static IEnumerable<object[]> OutOfRange()
        {
            object[] Case(string error, Action<ShapeContent> edit) => new object[] { error, edit };

            yield return Case("stroke width", s => s.StrokeWidth = -1);
            yield return Case("stroke width", s => s.StrokeWidth = 201);
            yield return Case("stroke width", s => s.StrokeWidth = double.NaN);
            yield return Case("corner radius", s => s.CornerRadius = -0.5);
            yield return Case("corner radius", s => s.CornerRadius = 1001);
            yield return Case("corner radius", s => s.CornerRadius = double.NaN);
            yield return Case("polygon sides", s => s.PolygonSides = 2);
            yield return Case("polygon sides", s => s.PolygonSides = 13);
            yield return Case("star points", s => s.StarPoints = 2);
            yield return Case("star points", s => s.StarPoints = 13);
            yield return Case("star inner ratio", s => s.StarInnerRatio = 0.05);
            yield return Case("star inner ratio", s => s.StarInnerRatio = 0.96);
            yield return Case("star inner ratio", s => s.StarInnerRatio = double.NaN);
            yield return Case("line start x", s => s.StartX = -1.5);
            yield return Case("line start y", s => s.StartY = 2.5);
            yield return Case("line end x", s => s.EndX = double.NaN);
            yield return Case("line end y", s => s.EndY = -1.01);
            yield return Case("line bend", s => s.Bend = 4.5);
            yield return Case("line bend", s => s.Bend = -4.5);
            yield return Case("line bend", s => s.Bend = double.NaN);
            yield return Case("line bend skew", s => s.BendSkew = 0.3);
            yield return Case("line bend skew", s => s.BendSkew = double.NaN);
            yield return Case("unknown shape kind", s => s.Kind = (ShapeKind)99);
            yield return Case("unknown shape dash", s => s.Dash = (ShapeDash)7);
        }

        [Theory]
        [MemberData(nameof(OutOfRange))]
        public void A_value_outside_its_range_is_rejected(string error, Action<ShapeContent> edit)
        {
            var (project, _, item) = OneShape();
            edit((ShapeContent)item.Content);

            Assert.Contains(project.Validate(), e => e.Contains(error));
        }

        [Fact]
        public void The_range_ends_validate()
        {
            var (project, _, item) = OneShape(new ShapeContent
            {
                StrokeWidth = ShapeContent.MaxStrokeWidth,
                CornerRadius = ShapeContent.MaxCornerRadius,
                PolygonSides = ShapeContent.MinPolygonSides,
                StarPoints = ShapeContent.MaxStarPoints,
                StarInnerRatio = ShapeContent.MinStarInnerRatio,
                StartX = ShapeContent.MinLineCoord,
                EndY = ShapeContent.MaxLineCoord,
                Bend = -ShapeContent.MaxBend,
                BendSkew = ShapeContent.MaxBendSkew,
            });
            Assert.Empty(project.Validate());

            // a closed shape may draw no outline at all; colors are never checked
            var shape = (ShapeContent)item.Content;
            shape.StrokeWidth = 0;
            shape.StrokeColor = "not a color";
            Assert.Empty(project.Validate());
        }

        [Theory]
        [InlineData(TrackKind.Audio)]
        [InlineData(TrackKind.Effect)]
        public void A_shape_belongs_on_a_video_row(TrackKind kind)
        {
            var (project, track, _) = OneShape();
            track.Kind = kind;

            Assert.Contains(project.Validate(), e => e.Contains("ShapeContent on"));
        }

        [Fact]
        public void Every_transition_but_a_ramp_validates_on_a_shape()
        {
            foreach (var kind in Enum.GetValues<TransitionKind>())
            {
                var (project, _, item) = OneShape();
                item.Entry = new Transition { Kind = kind, DurationTicks = Ms(300) };
                item.Exit = new Transition { Kind = kind, DurationTicks = Ms(300) };

                var errors = project.Validate();
                if (kind == TransitionKind.Ramp)
                {
                    Assert.Contains(errors, e => e.Contains("ramp entry"));
                    Assert.Contains(errors, e => e.Contains("ramp exit"));
                }
                else
                {
                    Assert.Empty(errors);
                }
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Only_a_shape_may_be_drawn_on(bool entry)
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video };
            project.Tracks.Add(track);
            var text = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                DurationTicks = Ms(5_000),
                Content = new TextContent { Text = "Hi" },
                Transform = new Transform(),
            };
            project.Items.Add(text);
            Assert.Empty(project.Validate());

            var draw = new Transition { Kind = TransitionKind.Draw, DurationTicks = Ms(300) };
            if (entry)
                text.Entry = draw;
            else
                text.Exit = draw;

            Assert.Contains(project.Validate(), e => e.Contains("draw transition, which only shapes support"));
        }

        // ------------------------------------------------------------------- timeline ops

        [Fact]
        public void Split_halves_are_independent_shapes()
        {
            var (project, _, item) = OneShape(Everything());

            Assert.True(TimelineOps.Split(project, item.Id, Ms(2_000)));

            var halves = project.Items.OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(2, halves.Count);
            var left = Assert.IsType<ShapeContent>(halves[0].Content);
            var right = Assert.IsType<ShapeContent>(halves[1].Content);
            Assert.NotSame(left, right);
            Assert.Equal(Json(left), Json(right));

            left.Bend = 0.5;
            Assert.Equal(-0.4, right.Bend);
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void Copies_and_pastes_into_a_gap()
        {
            var (project, track, item) = OneShape(Everything());
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = Ms(9_000),
                DurationTicks = Ms(1_000),
                Content = new ShapeContent(),
                Transform = new Transform { ScaleY = 1 },
            });
            var session = new EditorSession(project, null, null);

            Assert.True(session.CanCopyItem(item.Id));
            Assert.True(session.CopyItem(item.Id));

            var gap = session.FindGap(track.Id, Ms(7_000));
            Assert.NotNull(gap);
            Assert.True(session.CanPasteIntoGap(gap.Value));
            var pasted = session.PasteIntoGap(gap.Value);

            Assert.NotNull(pasted);
            var copy = session.Project.Items.Single(i => i.Id == pasted);
            Assert.Equal(Json(Everything()), Json(copy.Content));
            Assert.Equal(Ms(5_000), copy.TimelineStartTicks);
        }

        [Fact]
        public void A_shape_never_pastes_onto_an_audio_row()
        {
            var (project, _, item) = OneShape();
            var audio = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 1 };
            project.Tracks.Add(audio);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audio.Id,
                TimelineStartTicks = Ms(9_000),
                DurationTicks = Ms(1_000),
                Content = new MediaContent(),
            });
            var gap = TimelineOps.FindGap(project, audio.Id, Ms(1_000));

            Assert.NotNull(gap);
            Assert.False(TimelineOps.CanPasteIntoGap(project, gap.Value, item, TrackKind.Video));
        }

        // --------------------------------------------------------------------------- session

        [Fact]
        public void AddShape_puts_a_square_rectangle_on_the_front_row_in_one_undo()
        {
            var session = new EditorSession(EmptyProject(), null, null);
            var text = session.AddText(0, Ms(5_000)); // the row the shape must not share
            int changes = 0;
            session.ProjectChanged += (_, e) =>
            {
                Assert.Equal(ProjectChangeKind.Structural, e.Kind);
                changes++;
            };

            var added = session.AddShape(Ms(1_000), Ms(5_000));

            Assert.NotNull(added);
            Assert.Equal(1, changes);
            var shape = Assert.IsType<ShapeContent>(added.Content);
            Assert.Equal(ShapeKind.Rectangle, shape.Kind);
            Assert.Equal(6, shape.StrokeWidth); // 1080 / 180
            Assert.Equal(ShapeContent.DefaultStrokeColor, shape.StrokeColor);
            Assert.Equal(EditorSession.DefaultShapeExtent, added.Transform.Scale);
            // square in output pixels
            Assert.Equal(added.Transform.Scale * 1920, added.Transform.ScaleY.Value * 1080, 9);
            Assert.Equal((0.5, 0.5, 0.0), (added.Transform.X, added.Transform.Y, added.Transform.Rotation));

            var boxRow = session.Project.Tracks.Single(t => t.Id == added.TrackId);
            var textRow = session.Project.Tracks.Single(t => t.Id == text.TrackId);
            Assert.True(boxRow.Order > textRow.Order);

            session.Undo();
            Assert.DoesNotContain(session.Project.Items, i => i.Content is ShapeContent);
            Assert.Single(session.Project.Items); // the text stays: one entry, one add
        }

        [Theory]
        [InlineData(1920, 1080, 6)]
        [InlineData(1280, 720, 4)]
        [InlineData(3840, 2160, 12)]
        [InlineData(320, 180, 2)]
        public void AddShape_sizes_the_outline_to_the_output(int width, int height, double stroke)
        {
            var session = new EditorSession(EmptyProject(width, height), null, null);
            var added = session.AddShape(0, Ms(5_000), ShapeKind.Ellipse);

            var shape = (ShapeContent)added.Content;
            Assert.Equal(ShapeKind.Ellipse, shape.Kind);
            Assert.Equal(stroke, shape.StrokeWidth);
            Assert.Equal(added.Transform.Scale * width, added.Transform.ScaleY.Value * height, 9);
        }

        [Fact]
        public void AddShape_lays_an_arrow_across_the_square_box()
        {
            var session = new EditorSession(EmptyProject(), null, null);
            var added = session.AddShape(0, Ms(5_000), ShapeKind.Arrow);

            var shape = (ShapeContent)added.Content;
            Assert.Equal(ShapeKind.Arrow, shape.Kind);
            Assert.Equal(EditorSession.DefaultShapeExtent, added.Transform.Scale);

            // bottom left to top right, inset by the pad on both axes of the 480 px square
            double pad = Math.Max(Composition.ShapeLineLayout.LinePadPx(shape), Composition.ShapeLineLayout.MinLineBoxPadPx);
            double inset = pad / 480;
            Assert.Equal(inset, shape.StartX, 9);
            Assert.Equal(1 - inset, shape.StartY, 9);
            Assert.Equal(1 - inset, shape.EndX, 9);
            Assert.Equal(inset, shape.EndY, 9);
            Assert.Equal((0.0, 0.0), (shape.Bend, shape.BendSkew));
            Assert.Empty(session.Project.Validate());
        }
    }
}
