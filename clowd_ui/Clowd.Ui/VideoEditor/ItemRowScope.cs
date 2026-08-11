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
    /// segment writes every linked segment of the row in one <see cref="EditorSession.EditItems"/>
    /// call, exactly as the v1 webcam pane behaved. An unlinked item (an import, a text card, an
    /// unlinked row) is a row of one.
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

            if (item.LinkGroupId == null || session == null)
                return new[] { item.Id };

            return session.Project.Items
                .Where(i => i.TrackId == item.TrackId && i.LinkGroupId != null)
                .Select(i => i.Id)
                .ToList();
        }
    }
}
