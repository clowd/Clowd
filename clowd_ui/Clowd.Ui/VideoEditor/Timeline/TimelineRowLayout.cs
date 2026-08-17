using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>What a timeline row holds. Purely a presentation classification — the model knows
    /// only <see cref="TrackKind"/>; text and image rows are video tracks whose items happen to be
    /// cards rather than media, and they get a shorter row because there is nothing to draw in it
    /// but a name.</summary>
    internal enum TimelineRowKind
    {
        Video,
        Audio,
        Text,
        Image,
        Speed,
        Zoom,
    }

    /// <summary>One laid-out row: which track it draws, how tall it is and where its top edge sits
    /// in the surface's vertical (scrolled) coordinate space.</summary>
    internal sealed record TimelineRow(Guid TrackId, TimelineRowKind Kind, double Top, double Height)
    {
        /// <summary>Exclusive bottom edge.</summary>
        public double Bottom => Top + Height;
    }

    /// <summary>
    /// Turns a <see cref="Project"/> into the timeline's vertical layout: one row per track, in the
    /// project's canonical track order, stacked from the top. Pure — no Avalonia types — so the row
    /// order the surface draws, the order the header panel builds and the order the tests assert
    /// are the same code.
    /// </summary>
    internal static class TimelineRowLayout
    {
        /// <summary>Video rows carry a filmstrip, so they get the tall row.</summary>
        public const double VideoRowHeight = 56;

        /// <summary>Audio rows carry a waveform — enough height to read, less than a filmstrip.</summary>
        public const double AudioRowHeight = 36;

        /// <summary>Text rows show an icon and the string; one line is all they need.</summary>
        public const double TextRowHeight = 26;

        /// <summary>Image rows show an icon and the file name.</summary>
        public const double ImageRowHeight = 26;

        /// <summary>Effect rows (speed and zoom) show a glyph and a factor label — a card row,
        /// nothing to draw taller.</summary>
        public const double EffectRowHeight = 26;

        public static double HeightOf(TimelineRowKind kind) => kind switch
        {
            TimelineRowKind.Audio => AudioRowHeight,
            TimelineRowKind.Text => TextRowHeight,
            TimelineRowKind.Image => ImageRowHeight,
            TimelineRowKind.Speed => EffectRowHeight,
            TimelineRowKind.Zoom => EffectRowHeight,
            _ => VideoRowHeight,
        };

        /// <summary>
        /// Classifies a row: audio tracks are audio; an effect track is a speed or zoom row by its
        /// earliest item's content; a video track takes its kind from its earliest item's content
        /// (text card, still image, otherwise media). Order-independent — the earliest item wins
        /// however <paramref name="trackItems"/> arrives — and an empty track falls back to
        /// <see cref="TimelineRowKind.Video"/>, which is the row a media clip dropped onto it would
        /// want anyway. An empty effect track (only a hand-edited file can carry one — the session
        /// prunes them) reads as zoom: the speed row is identified by its items, so a row without
        /// any cannot be it.
        /// </summary>
        public static TimelineRowKind KindOf(Track track, IEnumerable<Item> trackItems)
        {
            ArgumentNullException.ThrowIfNull(track);

            if (track.Kind == TrackKind.Audio)
                return TimelineRowKind.Audio;

            var first = trackItems?.OrderBy(i => i.TimelineStartTicks).ThenBy(i => i.Id).FirstOrDefault();

            if (track.Kind == TrackKind.Effect)
                return first?.Content is SpeedContent ? TimelineRowKind.Speed : TimelineRowKind.Zoom;

            return first?.Content switch
            {
                TextContent => TimelineRowKind.Text,
                ImageContent => TimelineRowKind.Image,
                _ => TimelineRowKind.Video,
            };
        }

        /// <summary>
        /// Builds the rows top to bottom in three blocks: the speed row (if any) pinned first, then
        /// the video block — video and zoom tracks interleaved, <b>highest layer first</b> — then
        /// the audio tracks below them.
        ///
        /// <para>The video-block rows are the exact reverse of the order <c>FrameComposer</c>
        /// paints in (which is ascending <see cref="Track.Order"/> then <see cref="Track.Id"/>,
        /// last painted on top), so the row nearer the top of the timeline is the picture nearer
        /// the front — the convention every other editor uses, and the one "move up" has to mean
        /// if it is to read as raising a clip. A zoom row shares that space because its position
        /// in it is meaningful: the zoom applies to the rows beneath it. Audio has no stacking, so
        /// it keeps its natural ascending order and stays pinned underneath; the speed row is
        /// global (no z of its own) and sits above everything.</para>
        /// </summary>
        public static IReadOnlyList<TimelineRow> Build(Project project)
        {
            if (project?.Tracks == null || project.Tracks.Count == 0)
                return Array.Empty<TimelineRow>();

            var items = project.Items ?? new List<Item>();
            var byTrack = items.GroupBy(i => i.TrackId).ToDictionary(g => g.Key, g => (IEnumerable<Item>)g);

            var classified = project.Tracks
                .Select(t =>
                {
                    byTrack.TryGetValue(t.Id, out var trackItems);
                    return (Track: t, Kind: KindOf(t, trackItems));
                })
                .ToList();

            var ordered = classified
                .Where(x => x.Kind == TimelineRowKind.Speed)
                .OrderByDescending(x => x.Track.Order)
                .ThenByDescending(x => x.Track.Id)
                .Concat(classified
                    .Where(x => x.Kind != TimelineRowKind.Speed && x.Track.Kind != TrackKind.Audio)
                    .OrderByDescending(x => x.Track.Order)
                    .ThenByDescending(x => x.Track.Id))
                .Concat(classified
                    .Where(x => x.Track.Kind == TrackKind.Audio)
                    .OrderBy(x => x.Track.Order)
                    .ThenBy(x => x.Track.Id))
                .ToList();

            var rows = new List<TimelineRow>(ordered.Count);
            double top = 0;
            foreach (var (track, kind) in ordered)
            {
                var height = HeightOf(kind);
                rows.Add(new TimelineRow(track.Id, kind, top, height));
                top += height;
            }

            return rows;
        }

        /// <summary>Total height of all rows — what the surface reports as its desired height.</summary>
        public static double TotalHeight(IReadOnlyList<TimelineRow> rows)
        {
            if (rows == null || rows.Count == 0)
                return 0;

            return rows[^1].Bottom;
        }

        /// <summary>Index of the row containing <paramref name="y"/> (rows own their top edge and
        /// not their bottom one), or -1 above the first row / below the last.</summary>
        public static int RowIndexAtY(IReadOnlyList<TimelineRow> rows, double y)
        {
            if (rows == null)
                return -1;

            for (var i = 0; i < rows.Count; i++)
            {
                if (y >= rows[i].Top && y < rows[i].Bottom)
                    return i;
            }

            return -1;
        }
    }
}
