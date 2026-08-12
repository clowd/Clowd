using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.UI.VideoEditor.Timeline;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // TimelineRowLayout turns a Project into the timeline's vertical layout. Pure model math, no
    // Avalonia runtime — Clowd.Ui exposes its internals to this project via InternalsVisibleTo.
    public class TimelineRowLayoutTests
    {
        private static Track NewTrack(TrackKind kind, int order, string name = null) => new Track
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Order = order,
            Name = name ?? kind.ToString(),
        };

        private static Item NewItem(Track track, ItemContent content, long startTicks = 0,
            long durationTicks = TimeSpan.TicksPerSecond) => new Item
        {
            Id = Guid.NewGuid(),
            TrackId = track.Id,
            TimelineStartTicks = startTicks,
            DurationTicks = durationTicks,
            Content = content,
        };

        private static MediaContent Media() => new MediaContent { SourceId = Guid.NewGuid(), StreamIndex = 0 };

        /// <summary>A recording-shaped project plus a text row, an image row, an audio row and an
        /// empty row — one of every classification the layout can produce.</summary>
        private static Project BuildProject(out Track screen, out Track webcam, out Track titles,
            out Track logo, out Track mic, out Track empty)
        {
            screen = NewTrack(TrackKind.Video, 0, "Screen");
            webcam = NewTrack(TrackKind.Video, 1, "Webcam");
            titles = NewTrack(TrackKind.Video, 2, "Titles");
            logo = NewTrack(TrackKind.Video, 3, "Logo");
            mic = NewTrack(TrackKind.Audio, 4, "Microphone");
            empty = NewTrack(TrackKind.Video, 5, "Overlay");

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Tracks = new List<Track> { screen, webcam, titles, logo, mic, empty },
            };

            project.Items = new List<Item>
            {
                NewItem(screen, Media()),
                NewItem(webcam, Media()),
                NewItem(titles, new TextContent { Text = "Title", Size = 80 }),
                NewItem(logo, new ImageContent { Path = "logo.png" }),
                NewItem(mic, Media()),
            };

            return project;
        }

        // ------------------------------------------------------------------------ classification

        [Fact]
        public void KindOf_classifies_by_track_kind_then_content()
        {
            var project = BuildProject(out var screen, out _, out var titles, out var logo, out var mic, out var empty);

            IEnumerable<Item> ItemsOf(Track t) => project.Items.Where(i => i.TrackId == t.Id);

            Assert.Equal(TimelineRowKind.Video, TimelineRowLayout.KindOf(screen, ItemsOf(screen)));
            Assert.Equal(TimelineRowKind.Text, TimelineRowLayout.KindOf(titles, ItemsOf(titles)));
            Assert.Equal(TimelineRowKind.Image, TimelineRowLayout.KindOf(logo, ItemsOf(logo)));
            Assert.Equal(TimelineRowKind.Audio, TimelineRowLayout.KindOf(mic, ItemsOf(mic)));
            Assert.Equal(TimelineRowKind.Video, TimelineRowLayout.KindOf(empty, ItemsOf(empty)));
            Assert.Equal(TimelineRowKind.Video, TimelineRowLayout.KindOf(empty, null));
        }

        [Fact]
        public void KindOf_audio_track_wins_over_content()
        {
            // nonsense the model would reject, but the layout must not crash on it mid-edit.
            var audio = NewTrack(TrackKind.Audio, 0);
            var items = new[] { NewItem(audio, new TextContent { Text = "x" }) };
            Assert.Equal(TimelineRowKind.Audio, TimelineRowLayout.KindOf(audio, items));
        }

        [Fact]
        public void KindOf_uses_the_earliest_item_whatever_order_it_is_given()
        {
            var track = NewTrack(TrackKind.Video, 0);
            var text = NewItem(track, new TextContent { Text = "first" }, 0);
            var media = NewItem(track, Media(), TimeSpan.TicksPerSecond * 10);

            Assert.Equal(TimelineRowKind.Text, TimelineRowLayout.KindOf(track, new[] { text, media }));
            Assert.Equal(TimelineRowKind.Text, TimelineRowLayout.KindOf(track, new[] { media, text }));
        }

        // -------------------------------------------------------------------------------- layout

        /// <summary>Video rows run highest layer first — the reverse of the paint order — and the
        /// audio rows sit below all of them whatever their Order says.</summary>
        [Fact]
        public void Build_stacks_video_rows_front_first_with_audio_underneath()
        {
            var project = BuildProject(out var screen, out var webcam, out var titles, out var logo,
                out var mic, out var empty);

            var rows = TimelineRowLayout.Build(project);

            Assert.Equal(6, rows.Count);
            // Orders are Screen 0, Webcam 1, Titles 2, Logo 3, Mic 4 (audio), Overlay 5.
            Assert.Equal(new[] { empty.Id, logo.Id, titles.Id, webcam.Id, screen.Id, mic.Id },
                rows.Select(r => r.TrackId).ToArray());
            Assert.Equal(new[]
            {
                TimelineRowKind.Video, TimelineRowKind.Image, TimelineRowKind.Text,
                TimelineRowKind.Video, TimelineRowKind.Video, TimelineRowKind.Audio,
            }, rows.Select(r => r.Kind).ToArray());

            Assert.Equal(new[] { 56d, 26d, 26d, 56d, 56d, 36d }, rows.Select(r => r.Height).ToArray());
            Assert.Equal(new[] { 0d, 56d, 82d, 108d, 164d, 220d }, rows.Select(r => r.Top).ToArray());
            Assert.Equal(256d, TimelineRowLayout.TotalHeight(rows));
        }

        /// <summary>The video rows are exactly <c>Project.Normalize</c>'s order reversed, ties
        /// included — the composer paints that sequence and the last one painted is the one the
        /// timeline must show at the top.</summary>
        [Fact]
        public void Build_reverses_the_paint_order_ties_included()
        {
            var a = NewTrack(TrackKind.Video, 3, "A");
            var b = NewTrack(TrackKind.Video, 3, "B");
            var c = NewTrack(TrackKind.Video, 1, "C");
            var project = new Project { Tracks = new List<Track> { a, b, c } };

            var rows = TimelineRowLayout.Build(project);

            project.Normalize();
            Assert.Equal(project.Tracks.Select(t => t.Id).Reverse().ToArray(),
                rows.Select(r => r.TrackId).ToArray());
        }

        [Fact]
        public void Build_on_an_empty_project_produces_no_rows()
        {
            Assert.Empty(TimelineRowLayout.Build(new Project()));
            Assert.Empty(TimelineRowLayout.Build(null));
            Assert.Equal(0, TimelineRowLayout.TotalHeight(Array.Empty<TimelineRow>()));
        }

        [Fact]
        public void Build_tolerates_a_project_whose_items_list_is_null()
        {
            var track = NewTrack(TrackKind.Video, 0);
            var project = new Project { Tracks = new List<Track> { track }, Items = null };

            var rows = TimelineRowLayout.Build(project);

            Assert.Single(rows);
            Assert.Equal(TimelineRowKind.Video, rows[0].Kind);
        }

        // -------------------------------------------------------------------------- row hit rows

        [Fact]
        public void RowIndexAtY_finds_the_row_owning_the_coordinate()
        {
            var project = BuildProject(out _, out _, out _, out _, out _, out _);
            var rows = TimelineRowLayout.Build(project);

            // row tops are 0, 56, 82, 108, 164, 220 (see the stacking test above)
            Assert.Equal(0, TimelineRowLayout.RowIndexAtY(rows, 0));
            Assert.Equal(0, TimelineRowLayout.RowIndexAtY(rows, 55.9));
            Assert.Equal(1, TimelineRowLayout.RowIndexAtY(rows, 56));      // rows own their top edge
            Assert.Equal(3, TimelineRowLayout.RowIndexAtY(rows, 112));
            Assert.Equal(4, TimelineRowLayout.RowIndexAtY(rows, 164 + 35));
            Assert.Equal(5, TimelineRowLayout.RowIndexAtY(rows, 255.9));
        }

        [Fact]
        public void RowIndexAtY_outside_the_rows_is_minus_one()
        {
            var project = BuildProject(out _, out _, out _, out _, out _, out _);
            var rows = TimelineRowLayout.Build(project);

            Assert.Equal(-1, TimelineRowLayout.RowIndexAtY(rows, -1));
            Assert.Equal(-1, TimelineRowLayout.RowIndexAtY(rows, 256));
            Assert.Equal(-1, TimelineRowLayout.RowIndexAtY(rows, 10_000));
            Assert.Equal(-1, TimelineRowLayout.RowIndexAtY(null, 10));
        }
    }
}
