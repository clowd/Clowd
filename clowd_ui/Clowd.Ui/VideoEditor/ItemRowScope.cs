using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// Which items a <b>placement</b> edit reaches. A recording row's items are the split segments
    /// of one continuous feed: where that picture sits on the canvas, and what it is masked or
    /// cropped to, are properties of the <i>feed</i>, not of a segment — so a transform edit on any
    /// segment writes every segment of that feed on the row in one <see cref="EditorSession.EditItems"/>
    /// call, exactly as the v1 webcam pane behaved. "The same feed" is the same source stream on
    /// the same track — not the group, which says whether rows trim and cut together and is
    /// dissolved once a recording is down to one row (see <c>TimelineOps.CollapseLoneGroups</c>);
    /// a screen-only recording's segments still share their placement. A cursor or keyboard
    /// overlay row is one feed too — its segments mirror the screen's, and its style is the row's
    /// — so an overlay item reaches every overlay item of its kind on the row. Anything else (a
    /// text card, an image) is a row of one, and so is a media item whose row holds nothing else
    /// from its stream.
    ///
    /// Shared by the inspector's transform/mask/crop setters and the preview gizmo so a spinner and
    /// a drag always touch the same set of items; everything about a segment's own edges or its own
    /// sound (transitions, volume) stays single-item and does not come through here.
    /// </summary>
    internal static class ItemRowScope
    {
        public static IReadOnlyList<Guid> RowItemIds(EditorSession session, Item item)
        {
            if (item == null)
                return Array.Empty<Guid>();

            Func<Item, bool> sameFeed = item.Content switch
            {
                MediaContent media => i => i.Content is MediaContent m
                                           && m.SourceId == media.SourceId
                                           && m.StreamIndex == media.StreamIndex,
                CursorContent => i => i.Content is CursorContent,
                KeyboardContent => i => i.Content is KeyboardContent,
                _ => null,
            };
            if (sameFeed == null || session == null)
                return new[] { item.Id };

            return session.Project.Items
                .Where(i => i.TrackId == item.TrackId && sameFeed(i))
                .Select(i => i.Id)
                .ToList();
        }

        /// <summary>The identity an undo-coalesce key for a placement edit is scoped to: the track
        /// when the edit fans out across the row, the item alone otherwise. A bare per-selection
        /// key would let a selection change inside the coalesce window merge two different items'
        /// edits into one undo entry.</summary>
        public static Guid CoalesceScope(EditorSession session, Item item) =>
            RowItemIds(session, item).Count > 1 ? item.TrackId : item.Id;
    }
}
