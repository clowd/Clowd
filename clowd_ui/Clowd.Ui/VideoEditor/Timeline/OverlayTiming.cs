using System;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// Where an input-overlay item's span sits in its recording. A cursor or keys item carries no
    /// clock of its own — hard sync means it rides the screen media item it is linked to, exactly
    /// as <c>FrameComposer</c> composes it — so the preview the timeline draws on it has to map
    /// timeline time through that screen item to reach the capture's timebase. Pure model math,
    /// kept out of the surface so the tests can pin it.
    /// </summary>
    internal static class OverlayTiming
    {
        /// <summary>
        /// Resolves the source time at the overlay item's start and the speed its span plays at,
        /// through the screen media item of the same source that overlaps it — the item in its own
        /// link group when one qualifies (the hard-sync partner), else any. False when no screen
        /// item covers it: the cursor then draws nothing (the composer draws no cursor either) and
        /// the keys fall back to item-relative time, the composer's own degrade.
        /// </summary>
        public static bool TryResolve(Project project, Item item, Guid sourceId,
            out long sourceInTicks, out double speed)
        {
            sourceInTicks = 0;
            speed = 1.0;

            if (project?.Items == null || project.Tracks == null || item == null)
                return false;

            Source source = null;
            if (project.Sources != null)
            {
                foreach (var candidate in project.Sources)
                {
                    if (candidate.Id == sourceId)
                    {
                        source = candidate;
                        break;
                    }
                }
            }

            if (source == null)
                return false;

            Item screen = null;
            foreach (var other in project.Items)
            {
                if (ReferenceEquals(other, item) || other.Content is not MediaContent media ||
                    media.SourceId != sourceId)
                    continue;
                if (other.TimelineStartTicks >= item.TimelineEndTicks || other.TimelineEndTicks <= item.TimelineStartTicks)
                    continue;
                if (!IsVideoTrack(project, other.TrackId) || !FrameComposer.IsScreenStream(source, media.StreamIndex))
                    continue;

                if (item.LinkGroupId != null && other.LinkGroupId == item.LinkGroupId)
                {
                    screen = other;
                    break;
                }

                screen ??= other;
            }

            if (screen?.Content is not MediaContent screenMedia)
                return false;

            speed = TimelineOps.SpeedOf(screenMedia);
            var elapsed = item.TimelineStartTicks - screen.TimelineStartTicks;
            sourceInTicks = screenMedia.SourceInTicks + (speed == 1.0 ? elapsed : (long)Math.Round(elapsed * speed));
            return true;
        }

        private static bool IsVideoTrack(Project project, Guid trackId)
        {
            foreach (var track in project.Tracks)
            {
                if (track.Id == trackId)
                    return track.Kind == TrackKind.Video;
            }
            return false;
        }
    }
}
