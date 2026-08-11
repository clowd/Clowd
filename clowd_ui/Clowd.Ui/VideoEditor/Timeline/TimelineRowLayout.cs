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

        public static double HeightOf(TimelineRowKind kind) => kind switch
        {
            TimelineRowKind.Audio => AudioRowHeight,
            TimelineRowKind.Text => TextRowHeight,
            TimelineRowKind.Image => ImageRowHeight,
            _ => VideoRowHeight,
        };

        /// <summary>
        /// Classifies a row: audio tracks are audio; a video track takes its kind from its earliest
        /// item's content (text card, still image, otherwise media). Order-independent — the
        /// earliest item wins however <paramref name="trackItems"/> arrives — and an empty track
        /// falls back to <see cref="TimelineRowKind.Video"/>, which is the row a media clip dropped
        /// onto it would want anyway.
        /// </summary>
        public static TimelineRowKind KindOf(Track track, IEnumerable<Item> trackItems)
        {
            ArgumentNullException.ThrowIfNull(track);

            if (track.Kind == TrackKind.Audio)
                return TimelineRowKind.Audio;

            var first = trackItems?.OrderBy(i => i.TimelineStartTicks).ThenBy(i => i.Id).FirstOrDefault();
            return first?.Content switch
            {
                TextContent => TimelineRowKind.Text,
                ImageContent => TimelineRowKind.Image,
                _ => TimelineRowKind.Video,
            };
        }

        /// <summary>
        /// Builds the rows for a project, ordered by <see cref="Track.Order"/> then
        /// <see cref="Track.Id"/> — the identical total order <c>Project.Normalize</c> imposes, so
        /// the rows always match the item ordering the surface iterates.
        /// </summary>
        public static IReadOnlyList<TimelineRow> Build(Project project)
        {
            if (project?.Tracks == null || project.Tracks.Count == 0)
                return Array.Empty<TimelineRow>();

            var items = project.Items ?? new List<Item>();
            var byTrack = items.GroupBy(i => i.TrackId).ToDictionary(g => g.Key, g => (IEnumerable<Item>)g);

            var ordered = project.Tracks
                .OrderBy(t => t.Order)
                .ThenBy(t => t.Id)
                .ToList();

            var rows = new List<TimelineRow>(ordered.Count);
            double top = 0;
            foreach (var track in ordered)
            {
                byTrack.TryGetValue(track.Id, out var trackItems);
                var kind = KindOf(track, trackItems);
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
