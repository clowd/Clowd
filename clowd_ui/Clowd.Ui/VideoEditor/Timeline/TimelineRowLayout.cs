using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.VideoSDK.Composition;
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
        Cursor,
        Keyboard,
        Background,
    }

    /// <summary>The three reorder blocks the rows stack in; see <see cref="TimelineRowLayout.BlockOf"/>.</summary>
    internal enum TimelineRowBlock
    {
        Speed,
        Video,
        Audio,
    }

    /// <summary>One laid-out row: which track it draws, how tall it is, where its top edge sits
    /// in the surface's vertical (scrolled) coordinate space, and where its track stands in the
    /// model. <paramref name="LayerIndex"/> counts in the index space
    /// <c>EditorSession.MoveTrackToIndex</c> takes — ascending <c>(Order, Id)</c> within the
    /// video group (every non-audio track except the speed row; 0 is the backmost layer) or
    /// within the audio group for audio rows; -1 for the pinned speed row, which sits outside
    /// that space. The reorder math needs it because the displayed video block is <i>not</i>
    /// always the exact reverse of the model order (the input-overlay rows are drawn glued to
    /// their screen row wherever their <c>Order</c> really sits). <paramref name="PinnedTo"/> is
    /// the screen row a cursor/keyboard row is glued above — the row it is laid out with, travels
    /// with when that row is reordered, and is drawn as one combined track with; null for every
    /// other row, and for an overlay whose screen row is gone.</summary>
    internal sealed record TimelineRow(Guid TrackId, TimelineRowKind Kind, double Top, double Height,
        int LayerIndex = -1, Guid? PinnedTo = null)
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

        /// <summary>Background rows show an icon and the wallpaper's name. The artwork itself is
        /// on the canvas, not in the row, so there is nothing a taller row could draw.</summary>
        public const double BackgroundRowHeight = 26;

        /// <summary>Effect rows (speed and zoom) show a glyph and a factor label — a card row,
        /// nothing to draw taller.</summary>
        public const double EffectRowHeight = 26;

        /// <summary>Cursor and keyboard rows are cards too: their content is the recording's input
        /// capture, drawn in the picture and not in the row, so a glyph and a name is all the row
        /// has to say — and the pair sits between the screen row and everything above it, where a
        /// tall row would push the picture rows apart.</summary>
        public const double InputOverlayRowHeight = 26;

        /// <summary>The gutter between the three reorder blocks — the speed row, the video block
        /// and the audio block (see <see cref="BlockOf"/>). A row can only ever be dragged within
        /// its own block, and nothing says so as plainly as the blocks not touching: the surface
        /// and the header leave this strip in the bare surface colour between them, so the
        /// three read as three sections rather than one list with invisible walls.</summary>
        public const double BlockGap = 8;

        public static double HeightOf(TimelineRowKind kind) => kind switch
        {
            TimelineRowKind.Audio => AudioRowHeight,
            TimelineRowKind.Text => TextRowHeight,
            TimelineRowKind.Image => ImageRowHeight,
            TimelineRowKind.Background => BackgroundRowHeight,
            TimelineRowKind.Speed => EffectRowHeight,
            TimelineRowKind.Zoom => EffectRowHeight,
            TimelineRowKind.Cursor => InputOverlayRowHeight,
            TimelineRowKind.Keyboard => InputOverlayRowHeight,
            _ => VideoRowHeight,
        };

        /// <summary>Which of the three reorder blocks a row kind lays out in: the pinned speed
        /// row, the video block (everything that composites, the zoom, background and
        /// input-overlay rows included), or the audio block. <see cref="Build"/> stacks the blocks in that order with
        /// a <see cref="BlockGap"/> between them, and <c>TimelineReorder</c> never lets a drag
        /// cross from one into another.</summary>
        public static TimelineRowBlock BlockOf(TimelineRowKind kind) => kind switch
        {
            TimelineRowKind.Speed => TimelineRowBlock.Speed,
            TimelineRowKind.Audio => TimelineRowBlock.Audio,
            _ => TimelineRowBlock.Video,
        };

        /// <summary>Whether the row is one of the recording's input-capture overlays — the rows
        /// that are pinned above their screen row (and travel with it when it is reordered),
        /// refuse a reorder of their own and cannot be unlinked.</summary>
        public static bool IsInputOverlay(TimelineRowKind kind) =>
            kind is TimelineRowKind.Cursor or TimelineRowKind.Keyboard;

        /// <summary>
        /// Classifies a row: audio tracks are audio; an effect track is a speed or zoom row by its
        /// earliest item's content; a video track takes its kind from its earliest item's content
        /// (cursor overlay, keyboard overlay, text card, still image, wallpaper, otherwise
        /// media).
        /// Order-independent — the earliest item wins
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
                CursorContent => TimelineRowKind.Cursor,
                KeyboardContent => TimelineRowKind.Keyboard,
                TextContent => TimelineRowKind.Text,
                ImageContent => TimelineRowKind.Image,
                BackgroundContent => TimelineRowKind.Background,
                _ => TimelineRowKind.Video,
            };
        }

        /// <summary>
        /// Builds the rows top to bottom in three blocks: the speed row (if any) pinned first, then
        /// the video block — video and zoom tracks interleaved, <b>highest layer first</b> — then
        /// the audio tracks below them, with a <see cref="BlockGap"/> wherever one block ends
        /// and the next begins.
        ///
        /// <para>The video-block rows are the exact reverse of the order <c>FrameComposer</c>
        /// paints in (which is ascending <see cref="Track.Order"/> then <see cref="Track.Id"/>,
        /// last painted on top), so the row nearer the top of the timeline is the picture nearer
        /// the front — the convention every other editor uses, and the one "move up" has to mean
        /// if it is to read as raising a clip. A zoom row shares that space because its position
        /// in it is meaningful: the zoom applies to the rows beneath it. Audio has no stacking, so
        /// it keeps its natural ascending order and stays pinned underneath; the speed row is
        /// global (no z of its own) and sits above everything.</para>
        ///
        /// <para>Cursor and keyboard rows are lifted out of that descending run and put back
        /// <b>directly above their own screen row</b> — cursor first, then keyboard, then the
        /// screen row itself. Their <c>Order</c> already says as much on a project the session
        /// built (<c>EditorSession.AddCursorTrack</c> inserts them there and then refuses to move
        /// them), but another video row may still step between the pair, and an overlay row drawn
        /// away from the recording it annotates would read as unrelated picture. That lift makes
        /// the video block's display order <i>not</i> always the exact reverse of the model's —
        /// which is why every row carries its <see cref="TimelineRow.LayerIndex"/>: the reorder
        /// math must count drops in the model's own space, not in display positions.</para>
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

            var videoBlock = classified
                .Where(x => x.Kind != TimelineRowKind.Speed && x.Track.Kind != TrackKind.Audio)
                .OrderByDescending(x => x.Track.Order)
                .ThenByDescending(x => x.Track.Id)
                .ToList();

            var ordered = classified
                .Where(x => x.Kind == TimelineRowKind.Speed)
                .OrderByDescending(x => x.Track.Order)
                .ThenByDescending(x => x.Track.Id)
                .Concat(PinInputOverlays(project, videoBlock, byTrack, out var screenOf))
                .Concat(classified
                    .Where(x => x.Track.Kind == TrackKind.Audio)
                    .OrderBy(x => x.Track.Order)
                    .ThenBy(x => x.Track.Id))
                .ToList();

            // each track's index in the session's reorder space (see TimelineRow.LayerIndex):
            // ascending (Order, Id) within its own group, exactly as EditorSession.Reorder lists
            // them. The speed row is outside both spaces and keeps the -1 default.
            var layerOf = classified
                .Where(x => x.Kind != TimelineRowKind.Speed && x.Track.Kind != TrackKind.Audio)
                .OrderBy(x => x.Track.Order).ThenBy(x => x.Track.Id)
                .Select((x, i) => (x.Track.Id, Index: i))
                .Concat(classified
                    .Where(x => x.Track.Kind == TrackKind.Audio)
                    .OrderBy(x => x.Track.Order).ThenBy(x => x.Track.Id)
                    .Select((x, i) => (x.Track.Id, Index: i)))
                .ToDictionary(p => p.Id, p => p.Index);

            var rows = new List<TimelineRow>(ordered.Count);
            double top = 0;
            foreach (var (track, kind) in ordered)
            {
                if (rows.Count > 0 && BlockOf(rows[^1].Kind) != BlockOf(kind))
                    top += BlockGap;

                var height = HeightOf(kind);
                var layer = layerOf.TryGetValue(track.Id, out var index) ? index : -1;
                var pinnedTo = screenOf.TryGetValue(track.Id, out var screenId) ? screenId : (Guid?)null;
                rows.Add(new TimelineRow(track.Id, kind, top, height, layer, pinnedTo));
                top += height;
            }

            return rows;
        }

        /// <summary>
        /// The video block with every cursor/keyboard row moved to sit directly above its own
        /// screen row (cursor above keyboard above screen), leaving every other row where the
        /// descending layer order put it. An overlay whose screen row is gone — the row draws
        /// nothing then — keeps its natural place rather than disappearing from the list.
        /// <paramref name="screenOf"/> comes back as the glue: overlay track id to the screen track
        /// it was laid out with.
        /// </summary>
        private static List<(Track Track, TimelineRowKind Kind)> PinInputOverlays(
            Project project,
            List<(Track Track, TimelineRowKind Kind)> videoBlock,
            Dictionary<Guid, IEnumerable<Item>> byTrack,
            out Dictionary<Guid, Guid> screenOf)
        {
            screenOf = new Dictionary<Guid, Guid>();
            foreach (var row in videoBlock)
            {
                if (!IsInputOverlay(row.Kind))
                    continue;

                byTrack.TryGetValue(row.Track.Id, out var overlayItems);
                if (OverlaySourceId(overlayItems) is not Guid sourceId)
                    continue;

                var source = project.Sources?.FirstOrDefault(s => s.Id == sourceId);
                if (source == null)
                    continue;

                var screen = ScreenTrackOf(videoBlock, byTrack, source);
                if (screen != null)
                    screenOf[row.Track.Id] = screen.Id;
            }

            if (screenOf.Count == 0)
                return videoBlock;

            var glue = screenOf; // an out parameter cannot be read inside the lambda below
            var pinned = new List<(Track Track, TimelineRowKind Kind)>(videoBlock.Count);
            foreach (var row in videoBlock)
            {
                if (glue.ContainsKey(row.Track.Id))
                    continue; // laid out with its screen row instead

                // cursor sits above keyboard sits above the screen row — the reverse of the
                // compositing order, exactly as the rest of the video block is drawn.
                foreach (var overlay in videoBlock
                    .Where(x => glue.TryGetValue(x.Track.Id, out var id) && id == row.Track.Id)
                    .OrderBy(x => x.Kind == TimelineRowKind.Cursor ? 0 : 1)
                    .ThenByDescending(x => x.Track.Order)
                    .ThenByDescending(x => x.Track.Id))
                {
                    pinned.Add(overlay);
                }

                pinned.Add(row);
            }

            return pinned;
        }

        /// <summary>The source a cursor/keyboard row's items annotate.</summary>
        private static Guid? OverlaySourceId(IEnumerable<Item> trackItems)
        {
            if (trackItems == null)
                return null;

            foreach (var item in trackItems)
            {
                if (item.Content is CursorContent cursor)
                    return cursor.SourceId;
                if (item.Content is KeyboardContent keyboard)
                    return keyboard.SourceId;
            }

            return null;
        }

        /// <summary>The screen row an overlay pins above: the backmost video row playing the
        /// source's <b>screen stream</b> — the same row <c>EditorSession.FindOverlayScreenTrack</c>
        /// resolves. A webcam row plays the same source (a different stream), and matching by
        /// source alone would pin the overlays to it whenever the user orders it behind the
        /// screen row.</summary>
        private static Track ScreenTrackOf(List<(Track Track, TimelineRowKind Kind)> videoBlock,
            Dictionary<Guid, IEnumerable<Item>> byTrack, Source source) => videoBlock
                .Where(x => x.Track.Kind == TrackKind.Video && !IsInputOverlay(x.Kind) &&
                            byTrack.TryGetValue(x.Track.Id, out var trackItems) &&
                            trackItems.Any(i => i.Content is MediaContent media && media.SourceId == source.Id &&
                                                FrameComposer.IsScreenStream(source, media.StreamIndex)))
                .OrderBy(x => x.Track.Order)
                .ThenBy(x => x.Track.Id)
                .Select(x => x.Track)
                .FirstOrDefault();

        /// <summary>Total height of all rows — what the surface reports as its desired height.</summary>
        public static double TotalHeight(IReadOnlyList<TimelineRow> rows)
        {
            if (rows == null || rows.Count == 0)
                return 0;

            return rows[^1].Bottom;
        }

        /// <summary>Index of the row containing <paramref name="y"/> (rows own their top edge and
        /// not their bottom one), or -1 above the first row, below the last, or in the
        /// <see cref="BlockGap"/> between two blocks.</summary>
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
