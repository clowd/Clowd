using System;
using System.Collections.Generic;
using System.Linq;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// The editing operations, and the <b>only</b> place <see cref="Item.GroupId"/> semantics
/// live — timeline control, keyboard shortcuts and tests all come through here, so link behavior
/// cannot drift between entry points. The operations that change <i>when</i> content plays
/// (<see cref="Move"/>, <see cref="Split"/>, <see cref="RippleDelete"/>) resolve the target item's
/// group first and apply to the members concerned — all of them for a move, those covering
/// the instant for a split, those overlapping the item's span for a delete (an ungrouped item is a
/// group of one); the operations
/// that change how much of an item is shown (<see cref="TrimStart"/>, <see cref="TrimEnd"/>) or
/// remove a lone item (<see cref="Delete"/>) are single-item.
///
/// Operations clamp rather than corrupt: a move that would push a group member before the
/// timeline origin, or a trim that would take an item under <see cref="MinSegmentTicks"/> or
/// before the start of its source, is reduced to the largest amount that fits, and the applied
/// amount is returned so callers can reflect it. Operations that cannot be partially applied
/// (<see cref="Split"/>, <see cref="TryRegroupTrack"/>) reject instead.
/// </summary>
public static class TimelineOps
{
    /// <summary>Shortest item an edit may produce, in 100ns ticks: 100ms, mirroring the v1
    /// <c>VideoEditDocument.MinSegmentMs = 100</c> — anything below this is an accidental click,
    /// not an edit.</summary>
    public const long MinSegmentTicks = 1_000_000;

    /// <summary>Shortest item an <i>insert</i> may produce, in 100ns ticks: 1s. A new clip is
    /// something the user is about to grab — drag it, trim it, open its properties — and
    /// <see cref="MinSegmentTicks"/> of it lands under 6px at the default zoom, narrower than the
    /// trim handles on its own edges. So an insert with no room left in front of it (the playhead
    /// parked at the end of the recording) is backed off the end rather than squeezed; only a free
    /// stretch shorter than this is taken whole.</summary>
    public const long MinInsertTicks = TimeSpan.TicksPerSecond;

    /// <summary>The items an operation on <paramref name="itemId"/> applies to: every item
    /// sharing its non-null <see cref="Item.GroupId"/>, or just the item itself when
    /// ungrouped. Throws when the id is not in the project.</summary>
    public static IReadOnlyList<Item> GetGroupedItems(Project project, Guid itemId)
    {
        var item = Require(project, itemId);
        if (item.GroupId == null)
            return new[] { item };

        return project.Items.Where(i => i.GroupId == item.GroupId).ToList();
    }

    /// <summary>Shifts the item's whole group along the timeline by
    /// <paramref name="deltaTicks"/>, clamped so no member starts before 0. A
    /// <see cref="MediaContent.SpeedWarpExempt"/> member keeps the real-time (output) span it
    /// had, which is the truth for such a clip: it plays on the output clock, so its project
    /// duration is re-derived at the new position and changes when the move crosses into or
    /// out of a speed item (clamped to the row like <see cref="SetSpeed"/>). Returns the delta
    /// actually applied.</summary>
    public static long Move(Project project, Guid itemId, long deltaTicks)
    {
        var members = GetGroupedItems(project, itemId);

        var minStart = members.Min(m => m.TimelineStartTicks);
        if (deltaTicks < -minStart)
            deltaTicks = -minStart;

        if (deltaTicks == 0)
            return 0;

        // the exempt members' output spans under the warp before anything moves; a moved
        // audio clip never changes the warp itself, so the same warp re-fits them afterwards
        List<(Item Item, long OutputSpan)> exempt = null;
        Playback.TimeWarp warp = null;
        foreach (var m in members)
        {
            if (m.Content is not MediaContent { SpeedWarpExempt: true })
                continue;

            warp ??= Playback.TimeWarp.Build(project);
            (exempt ??= new List<(Item, long)>()).Add((m,
                warp.ToOutput(m.TimelineEndTicks) - warp.ToOutput(m.TimelineStartTicks)));
        }

        foreach (var m in members)
            m.TimelineStartTicks += deltaTicks;

        if (exempt != null)
        {
            foreach (var (m, outputSpan) in exempt)
            {
                var duration = warp.ToProject(warp.ToOutput(m.TimelineStartTicks) + outputSpan) - m.TimelineStartTicks;
                m.DurationTicks = ClampDurationToRow(project, m, duration);
            }
        }

        return deltaTicks;
    }

    /// <summary>
    /// Moves the in-point of a <b>single</b> item, group or not: positive
    /// <paramref name="deltaTicks"/> shrinks it from the start, negative extends it earlier.
    /// Clamped so the item keeps at least <see cref="MinSegmentTicks"/>, starts at or after 0,
    /// and — for media — never rewinds before the start of its source
    /// (<see cref="MediaContent.SourceInTicks"/> stays ≥ 0). The media in-point moves with the
    /// trim, so every instant the item still covers maps to the source frame it mapped to before:
    /// trimming one member of a group cannot ungroup it from the others, which is why trim needs
    /// no group scope. Returns the delta actually applied.
    /// </summary>
    public static long TrimStart(Project project, Guid itemId, long deltaTicks)
    {
        var item = Require(project, itemId);
        var media = item.Content as MediaContent;
        var speed = SpeedOf(media);

        var maxShrink = item.DurationTicks - MinSegmentTicks;
        if (deltaTicks > maxShrink)
            deltaTicks = Math.Max(0, maxShrink);

        // a timeline tick consumes `speed` source ticks, so the room before the source's start
        // is SourceInTicks / speed timeline ticks (floored — never let rounding rewind past 0).
        // An exempt clip consumes source on the output clock, so its room is measured there and
        // mapped back into project ticks (see ExemptStartHeadroom).
        // a freeze frame consumes no source, so only the origin bounds it.
        var maxExtend = media == null || media.Freeze
            ? item.TimelineStartTicks
            : media.SpeedWarpExempt
                ? Math.Min(item.TimelineStartTicks, ExemptStartHeadroom(project, item, media, speed))
                : Math.Min(item.TimelineStartTicks, (long)Math.Floor(media.SourceInTicks / speed));
        if (deltaTicks < -maxExtend)
            deltaTicks = -maxExtend;

        if (deltaTicks == 0)
            return 0;

        var oldStart = item.TimelineStartTicks;
        item.TimelineStartTicks += deltaTicks;
        item.DurationTicks -= deltaTicks;
        if (media != null)
            media.SourceInTicks = Math.Max(0, media.SourceInTicks
                + SourceTicksBetween(project, media, oldStart, oldStart + deltaTicks));

        return deltaTicks;
    }

    /// <summary>
    /// Moves the out-point of a <b>single</b> item, group or not: positive
    /// <paramref name="deltaTicks"/> lengthens it, negative shortens it, clamped so it keeps at
    /// least <see cref="MinSegmentTicks"/> and — for media whose stream duration is known — never
    /// extends past the end of its source (there is no material there: video would freeze on the
    /// last frame and audio would mix to silence). An item already hanging past the source end
    /// (older project, or a stale probe) may still shrink, it just cannot grow. The item's start
    /// and in-point are untouched, so the source↔timeline mapping of every instant it still
    /// covers is unchanged and grouped rows stay in sync. Returns the delta actually applied.
    /// </summary>
    public static long TrimEnd(Project project, Guid itemId, long deltaTicks)
    {
        var item = Require(project, itemId);

        var maxShrink = item.DurationTicks - MinSegmentTicks;
        if (deltaTicks < -maxShrink)
            deltaTicks = Math.Min(0, -maxShrink);

        if (item.Content is MediaContent { Freeze: false } media)
        {
            var streamDuration = StreamDurationOf(project, media);
            if (streamDuration > 0)
            {
                // remaining source, expressed in timeline ticks at the item's speed
                var speed = SpeedOf(media);
                var remainingTimeline = (long)Math.Floor((streamDuration - media.SourceInTicks) / speed);
                long maxExtend;
                if (media.SpeedWarpExempt)
                {
                    // the remaining source plays out in output ticks; where that lands in
                    // project time depends on the warp between here and there.
                    var warp = Playback.TimeWarp.Build(project);
                    var allowedEnd = warp.ToProject(warp.ToOutput(item.TimelineStartTicks) + Math.Max(0, remainingTimeline));
                    maxExtend = Math.Max(0, allowedEnd - item.TimelineEndTicks);
                }
                else
                {
                    maxExtend = Math.Max(0, remainingTimeline - item.DurationTicks);
                }
                if (deltaTicks > maxExtend)
                    deltaTicks = maxExtend;
            }
        }

        if (deltaTicks == 0)
            return 0;

        item.DurationTicks += deltaTicks;
        return deltaTicks;
    }

    /// <summary>The probed duration of the stream an item plays, or 0 when the source/stream is
    /// missing or the probe recorded no duration — in which case trims are not source-bounded.</summary>
    private static long StreamDurationOf(Project project, MediaContent media)
    {
        foreach (var source in project.Sources ?? Enumerable.Empty<Source>())
        {
            if (source.Id != media.SourceId)
                continue;
            foreach (var stream in source.Streams ?? Enumerable.Empty<SourceStream>())
            {
                if (stream.Index == media.StreamIndex)
                    return stream.DurationTicks;
            }
        }

        return 0;
    }

    /// <summary>
    /// Splits the item's group at <paramref name="timelineTicks"/>: every member covering
    /// that instant becomes two back-to-back items, the right half starting exactly there with
    /// its media in-point advanced by the left half's length. Right halves of a grouped group get
    /// a fresh shared <see cref="Item.GroupId"/> (two grouped clips become two grouped pairs);
    /// left halves keep the original. Entry transitions stay on the left, exit transitions move
    /// to the right. All-or-nothing: returns false without touching the project when the target
    /// item does not cover the instant, or when any covered member would end up shorter than
    /// <see cref="MinSegmentTicks"/> on either side.
    /// </summary>
    public static bool Split(Project project, Guid itemId, long timelineTicks)
    {
        var target = Require(project, itemId);
        if (!Covers(target, timelineTicks))
            return false;

        var covered = GetGroupedItems(project, itemId).Where(m => Covers(m, timelineTicks)).ToList();

        // the right halves become a group of their own, so the two sides of the cut stay
        // synced within themselves without the left side dragging the right one around.
        return SplitCore(project, covered, timelineTicks,
            target.GroupId == null ? (Guid?)null : Guid.NewGuid());
    }

    /// <summary>
    /// Cuts <b>one</b> item, leaving the rest of its group alone — the timeline's right-click
    /// split, where the pointer picked out a single clip and cutting its neighbors with it would
    /// be an edit the user did not ask for.
    ///
    /// <para>Both halves keep the item's existing <see cref="Item.GroupId"/>: the clip is still
    /// part of the same recording, it simply has two segments on its row now. Group operations
    /// cope with that already — they only ever act on the members that cover the instant in
    /// question (see <see cref="Split"/>).</para>
    /// </summary>
    public static bool SplitItem(Project project, Guid itemId, long timelineTicks)
    {
        var target = Require(project, itemId);
        if (!Covers(target, timelineTicks))
            return false;

        return SplitCore(project, new[] { target }, timelineTicks, target.GroupId);
    }

    /// <summary>Cuts every item in <paramref name="covered"/> at the instant, all-or-nothing: a cut
    /// that would leave any half shorter than <see cref="MinSegmentTicks"/> is refused outright
    /// rather than applied to some rows and not others.</summary>
    private static bool SplitCore(Project project, IReadOnlyList<Item> covered, long timelineTicks,
        Guid? rightGroup)
    {
        foreach (var m in covered)
        {
            if (timelineTicks - m.TimelineStartTicks < MinSegmentTicks ||
                m.TimelineEndTicks - timelineTicks < MinSegmentTicks)
                return false;
        }

        foreach (var m in covered)
        {
            var leftLength = timelineTicks - m.TimelineStartTicks;

            var content = m.Content?.Clone();
            if (content is MediaContent media)
                media.SourceInTicks += SourceTicksBetween(project, media, m.TimelineStartTicks, timelineTicks);

            var right = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = m.TrackId,
                TimelineStartTicks = timelineTicks,
                DurationTicks = m.TimelineEndTicks - timelineTicks,
                Content = content,
                Transform = m.Transform?.Clone() ?? new Transform(),
                Surround = m.Surround?.Clone(),
                Effect = m.Effect?.Clone(),
                Entry = null,
                Exit = m.Exit,
                Volume = m.Volume,
                GroupId = rightGroup,
            };

            m.DurationTicks = leftLength;
            m.Exit = null;
            project.Items.Add(right);
        }

        project.Normalize();
        return true;
    }

    /// <summary>
    /// Cuts the item's <b>own span</b> out of its group and closes the gap: every group
    /// member is trimmed/split to remove what it played inside <c>[start, end)</c>, and every
    /// remaining item that started at or after the cut shifts left by its length (clamped so
    /// nothing shifts to before where the cut began). This is the multi-track generalization of
    /// the v1 "cut": items on <b>all</b> tracks shift, so cross-track sync is preserved.
    ///
    /// <para>Scoped to the item's span — never "the whole group" — because one group can carry
    /// several back-to-back segments per row (a recording built from keep-slices, or the two
    /// halves a <see cref="SplitItem"/> leaves): deleting one clip must not take the rest of the
    /// recording with it. When every member shares the clip's span (the common one-segment
    /// column) this removes exactly the group as before.</para>
    /// </summary>
    public static void RippleDelete(Project project, Guid itemId)
    {
        var target = Require(project, itemId);
        var start = target.TimelineStartTicks;
        var end = target.TimelineEndTicks;

        CutGroupRange(project, itemId, start, end);

        var span = end - start;
        foreach (var item in project.Items)
        {
            if (item.TimelineStartTicks >= start)
                item.TimelineStartTicks = Math.Max(start, item.TimelineStartTicks - span);
        }
    }

    /// <summary>The no-ripple counterpart of <see cref="RippleDelete"/>: cuts the item's span out
    /// of its group in place, leaving the gap open and everything outside the group
    /// untouched. The delete for an imported file's grouped rows, whose group means "streams of
    /// one file" — closing the gap under unrelated material is the recording cut's semantics, not
    /// the overlay's.</summary>
    public static void DeleteGrouped(Project project, Guid itemId)
    {
        var target = Require(project, itemId);
        CutGroupRange(project, itemId, target.TimelineStartTicks, target.TimelineEndTicks);
    }

    /// <summary>
    /// Removes what every member of the item's group plays inside <c>[start, end)</c>: a
    /// member inside the range is removed, one straddling an edge is trimmed (splitting in two
    /// when it hangs over both, exactly as <see cref="SplitCore"/> would cut it — in-point
    /// advanced, entry left / exit right). A remnant shorter than <see cref="MinSegmentTicks"/>
    /// is culled with the member rather than kept: the group's rows need not agree on their edges
    /// (a recording's audio ends a hair before its video), and a delete that left a sub-minimum
    /// sliver behind would strand an item no edit is allowed to produce.
    /// </summary>
    private static void CutGroupRange(Project project, Guid itemId, long start, long end) =>
        CutRange(project, GetGroupedItems(project, itemId), start, end);

    /// <summary>The body of <see cref="CutGroupRange"/> over any set of items: what each of
    /// <paramref name="candidates"/> plays inside <c>[start, end)</c> is removed, with the same
    /// trim/split/cull rules.</summary>
    private static void CutRange(Project project, IEnumerable<Item> candidates, long start, long end)
    {
        foreach (var m in candidates
                     .Where(m => m.TimelineStartTicks < end && m.TimelineEndTicks > start).ToList())
        {
            var leftLength = start - m.TimelineStartTicks;
            var rightLength = m.TimelineEndTicks - end;

            if (rightLength >= MinSegmentTicks)
            {
                var content = m.Content?.Clone();
                if (content is MediaContent media)
                    media.SourceInTicks += SourceTicksBetween(project, media, m.TimelineStartTicks, end);

                project.Items.Add(new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = m.TrackId,
                    TimelineStartTicks = end,
                    DurationTicks = rightLength,
                    Content = content,
                    Transform = m.Transform?.Clone() ?? new Transform(),
                    Surround = m.Surround?.Clone(),
                    Effect = m.Effect?.Clone(),
                    Entry = null,
                    Exit = m.Exit,
                    Volume = m.Volume,
                    GroupId = m.GroupId,
                });
            }

            if (leftLength >= MinSegmentTicks)
            {
                m.DurationTicks = leftLength;
                m.Exit = null;
            }
            else
            {
                project.Items.Remove(m);
            }
        }
    }

    /// <summary>Removes a single item, leaving a gap where it was: no ripple, and group
    /// members are left alone (<see cref="RippleDelete"/> is the synced-segment delete). Returns
    /// false when the id is not in the project.</summary>
    public static bool Delete(Project project, Guid itemId)
    {
        var item = project.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
            return false;

        project.Items.Remove(item);
        return true;
    }

    // ----------------------------------------------------------------------------------- gaps

    /// <summary>The empty stretch of <paramref name="trackId"/> around <paramref name="ticks"/>:
    /// from the end of the item before it (or the origin) to the start of the item after it.
    /// Null when an item covers the instant, or when no item follows it — the open run past a
    /// row's last item is the end of the row, not a gap in it.</summary>
    public static TimelineGap? FindGap(Project project, Guid trackId, long ticks)
    {
        if (ticks < 0)
            return null;

        Item left = null, right = null;
        foreach (var item in project.Items)
        {
            if (item.TrackId != trackId)
                continue;
            if (Covers(item, ticks))
                return null;

            if (item.TimelineEndTicks <= ticks)
            {
                if (left == null || item.TimelineEndTicks > left.TimelineEndTicks)
                    left = item;
            }
            else if (right == null || item.TimelineStartTicks < right.TimelineStartTicks)
            {
                right = item;
            }
        }

        if (right == null)
            return null;

        var start = left?.TimelineEndTicks ?? 0;
        return right.TimelineStartTicks > start
            ? new TimelineGap(trackId, start, right.TimelineStartTicks, left?.Id, right.Id)
            : null;
    }

    /// <summary>Whether the project still has exactly this gap — the guard every gap operation
    /// runs first, so a menu built before an edit cannot act on a row that has since
    /// changed.</summary>
    public static bool IsCurrentGap(Project project, TimelineGap gap) =>
        FindGap(project, gap.TrackId, gap.StartTicks) == gap;

    /// <summary>
    /// Closes a gap on its own row: the item after it slides left onto the gap's start, and the
    /// members of its group that sit at or past the gap slide with it, so a recording's other rows
    /// (its audio, its cursor) stay in sync. Group members before the gap stay put — they are the
    /// earlier segments the gap was opened from. Nothing else moves; a shift that runs a group
    /// member into another item on its row is left for validation to refuse. Returns false
    /// when the gap is not current.
    /// </summary>
    public static bool CloseGap(Project project, TimelineGap gap)
    {
        if (!IsCurrentGap(project, gap))
            return false;

        var span = gap.DurationTicks;
        foreach (var m in GetGroupedItems(project, gap.RightItemId)
                     .Where(m => m.TimelineStartTicks >= gap.EndTicks))
            m.TimelineStartTicks -= span;

        return true;
    }

    /// <summary>
    /// The gap's span cut out of the whole timeline: whatever any other row plays inside it is
    /// removed (trimmed, split or dropped exactly as <see cref="RippleDelete"/> cuts a group), then
    /// everything at or past the gap shifts left by its length. Cross-row sync survives because
    /// every row loses the same span. Returns false when the gap is not current.
    /// </summary>
    public static bool RippleCloseGap(Project project, TimelineGap gap)
    {
        if (!IsCurrentGap(project, gap))
            return false;

        var start = gap.StartTicks;
        var span = gap.DurationTicks;
        CutRange(project, project.Items, start, gap.EndTicks);

        foreach (var item in project.Items)
        {
            if (item.TimelineStartTicks >= start)
                item.TimelineStartTicks = Math.Max(start, item.TimelineStartTicks - span);
        }

        return true;
    }

    /// <summary>Whether <see cref="FillGapWithFreeze"/> can hold a frame of the gap's left
    /// (<paramref name="fromLeft"/>) or right neighbor: the gap is current, sits on a video row,
    /// and that neighbor exists and is media.</summary>
    public static bool CanFillGapWithFreeze(Project project, TimelineGap gap, bool fromLeft)
    {
        if (!IsCurrentGap(project, gap))
            return false;

        var neighborId = fromLeft ? gap.LeftItemId : gap.RightItemId;
        var neighbor = neighborId == null ? null : project.Items.FirstOrDefault(i => i.Id == neighborId);
        var track = project.Tracks.FirstOrDefault(t => t.Id == gap.TrackId);
        return neighbor?.Content is MediaContent && track?.Kind == TrackKind.Video;
    }

    /// <summary>
    /// Fills the gap with a freeze frame (<see cref="MediaContent.Freeze"/>) of a neighbor: the
    /// last frame the left item shows, or the first frame the right item shows, held for the
    /// gap's whole length. The new item wears the neighbor's placement, surround and effect so
    /// the picture does not jump at the seam; it carries no transitions and no group (a held
    /// frame has no source clock to keep in sync). Returns the new item's id, or null when
    /// <see cref="CanFillGapWithFreeze"/> says no.
    /// </summary>
    public static Guid? FillGapWithFreeze(Project project, TimelineGap gap, bool fromLeft)
    {
        if (!CanFillGapWithFreeze(project, gap, fromLeft))
            return null;

        var neighbor = Require(project, fromLeft ? gap.LeftItemId.Value : gap.RightItemId);
        var media = (MediaContent)neighbor.Content;

        var content = (MediaContent)media.Clone();
        content.SourceInTicks = fromLeft ? LastFrameTicks(project, neighbor, media) : media.SourceInTicks;
        content.Freeze = true;
        content.Speed = 1.0;
        content.SpeedWarpExempt = false;

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TrackId = gap.TrackId,
            TimelineStartTicks = gap.StartTicks,
            DurationTicks = gap.DurationTicks,
            Content = content,
            Transform = neighbor.Transform?.Clone() ?? new Transform(),
            Surround = neighbor.Surround?.Clone(),
            Effect = neighbor.Effect?.Clone(),
            Volume = neighbor.Volume,
        };
        project.Items.Add(item);
        project.Normalize();
        return item.Id;
    }

    /// <summary>The source instant of the last frame a media item shows: one tick short of its
    /// out-point (the out-point itself is the first instant the edit removed — the convention
    /// the preview's past-the-end clamp follows too), held inside the stream when the item hangs
    /// past the end of its source.</summary>
    private static long LastFrameTicks(Project project, Item item, MediaContent media)
    {
        if (media.Freeze)
            return media.SourceInTicks;

        var end = media.SourceInTicks
                  + SourceTicksBetween(project, media, item.TimelineStartTicks, item.TimelineEndTicks);
        var streamDuration = StreamDurationOf(project, media);
        if (streamDuration > 0)
            end = Math.Min(end, streamDuration);

        return Math.Max(media.SourceInTicks, end - 1);
    }

    /// <summary>Whether <see cref="ExtendIntoGap"/> would fill the whole gap: the neighbor has
    /// that much material on the gap's side (trimmed-off source for media; anything else stretches
    /// freely). Answered by trial on a copy, so it can never disagree with the trims
    /// themselves.</summary>
    public static bool CanExtendIntoGap(Project project, TimelineGap gap, bool fromLeft)
    {
        if (!IsCurrentGap(project, gap) || (fromLeft && gap.LeftItemId == null))
            return false;

        return ExtendIntoGapCore(Project.FromJson(project.ToJson()), gap, fromLeft);
    }

    /// <summary>
    /// Fills the gap by un-trimming a neighbor into it: the left item's out-point moves to the
    /// gap's end, or the right item's in-point moves back to the gap's start, revealing the
    /// source that was trimmed away. Single-item, like the trims it is made of — the neighbor's
    /// group partners keep their own edges. All-or-nothing: returns false without touching the
    /// project when the neighbor cannot cover the whole gap (see
    /// <see cref="CanExtendIntoGap"/>).
    /// </summary>
    public static bool ExtendIntoGap(Project project, TimelineGap gap, bool fromLeft) =>
        CanExtendIntoGap(project, gap, fromLeft) && ExtendIntoGapCore(project, gap, fromLeft);

    private static bool ExtendIntoGapCore(Project project, TimelineGap gap, bool fromLeft)
    {
        var span = gap.DurationTicks;
        return fromLeft
            ? TrimEnd(project, gap.LeftItemId.Value, span) == span
            : TrimStart(project, gap.RightItemId, -span) == -span;
    }

    /// <summary>
    /// Whether the gap's two neighbors are the halves of one cut that <see cref="RejoinGap"/> can
    /// heal: the same stream at the same speed, neither a freeze frame nor on the output clock,
    /// and the right one picking up at or after where the left one stops in the source (so the
    /// rejoined clip plays the source forward, through whatever the cut and trims removed).
    /// </summary>
    public static bool CanRejoinGap(Project project, TimelineGap gap)
    {
        if (!IsCurrentGap(project, gap) || gap.LeftItemId is not Guid leftId)
            return false;

        var left = project.Items.FirstOrDefault(i => i.Id == leftId);
        var right = project.Items.FirstOrDefault(i => i.Id == gap.RightItemId);
        if (left?.Content is not MediaContent lm || right?.Content is not MediaContent rm)
            return false;

        return lm.SourceId == rm.SourceId && lm.StreamIndex == rm.StreamIndex
               && !lm.Freeze && !rm.Freeze && !lm.SpeedWarpExempt && !rm.SpeedWarpExempt
               && SpeedOf(lm) == SpeedOf(rm)
               && rm.SourceInTicks >= lm.SourceInTicks + SourceTicksBetween(project, lm, left.TimelineStartTicks, left.TimelineEndTicks);
    }

    /// <summary>
    /// Undoes the cut between the gap's neighbors on this row only: the left clip grows to play
    /// its source straight through to where the right clip ended — the material the cut and any
    /// trims removed included — and the right clip is absorbed into it (its exit transition
    /// carried over). Everything after it on the row shifts by however far the right clip's end
    /// moved — left when the gap was wider than the removed material, right when it was
    /// narrower — so the rest of the row keeps its spacing. Other rows, group partners included,
    /// are untouched. Returns false when <see cref="CanRejoinGap"/> says no.
    /// </summary>
    public static bool RejoinGap(Project project, TimelineGap gap)
    {
        if (!CanRejoinGap(project, gap))
            return false;

        var left = Require(project, gap.LeftItemId.Value);
        var right = Require(project, gap.RightItemId);
        var lm = (MediaContent)left.Content;
        var rm = (MediaContent)right.Content;
        var speed = SpeedOf(lm);

        var sourceEnd = rm.SourceInTicks + SourceTicksBetween(project, rm, right.TimelineStartTicks, right.TimelineEndTicks);
        var sourceSpan = sourceEnd - lm.SourceInTicks;
        var newEnd = left.TimelineStartTicks + (speed == 1.0 ? sourceSpan : (long)Math.Round(sourceSpan / speed));
        var shift = newEnd - right.TimelineEndTicks;

        foreach (var item in project.Items)
        {
            if (item.TrackId == gap.TrackId && item.TimelineStartTicks >= right.TimelineEndTicks)
                item.TimelineStartTicks += shift;
        }

        left.DurationTicks = newEnd - left.TimelineStartTicks;
        left.Exit = right.Exit;
        project.Items.Remove(right);
        return true;
    }

    /// <summary>Whether <paramref name="clip"/> — an item lifted off a row of kind
    /// <paramref name="clipTrackKind"/> — can be pasted into the gap: the gap is current and at
    /// least <see cref="MinSegmentTicks"/> long, the clip is media, text, an image or a solid
    /// (effect items and input overlays belong to rows of their own), and its row kind matches
    /// the gap's (a picture never lands on an audio row, nor a sound on a video row).</summary>
    public static bool CanPasteIntoGap(Project project, TimelineGap gap, Item clip, TrackKind clipTrackKind)
    {
        if (clip == null || !IsCurrentGap(project, gap) || gap.DurationTicks < MinSegmentTicks)
            return false;

        var track = project.Tracks.FirstOrDefault(t => t.Id == gap.TrackId);
        return clip.Content switch
        {
            MediaContent => track?.Kind == clipTrackKind,
            TextContent or ImageContent or SolidContent => track?.Kind == TrackKind.Video,
            _ => false,
        };
    }

    /// <summary>
    /// Pastes <paramref name="clip"/> at the start of the gap, end-trimmed to fit when it is
    /// longer than the gap. The clip must be a private copy (it is inserted as-is, re-identified:
    /// a new id, the gap's row, no group). A media clip from another project brings its
    /// <paramref name="clipSource"/> along when this project does not have that source yet.
    /// Returns the new item's id, or null when <see cref="CanPasteIntoGap"/> says no.
    /// </summary>
    public static Guid? PasteIntoGap(Project project, TimelineGap gap, Item clip, TrackKind clipTrackKind,
        Source clipSource)
    {
        if (!CanPasteIntoGap(project, gap, clip, clipTrackKind))
            return null;

        if (clip.Content is MediaContent media && project.Sources.All(s => s.Id != media.SourceId))
        {
            if (clipSource == null || clipSource.Id != media.SourceId)
                return null;
            project.Sources.Add(clipSource);
        }

        clip.Id = Guid.NewGuid();
        clip.TrackId = gap.TrackId;
        clip.TimelineStartTicks = gap.StartTicks;
        clip.DurationTicks = Math.Min(clip.DurationTicks, gap.DurationTicks);
        clip.GroupId = null;
        project.Items.Add(clip);
        project.Normalize();
        return clip.Id;
    }

    /// <summary>Clears <see cref="Item.GroupId"/> on the given items so they edit
    /// independently. Items not in the project throw; the rest of their old group is left
    /// grouped.</summary>
    public static void Ungroup(Project project, IEnumerable<Guid> itemIds)
    {
        foreach (var id in itemIds)
            Require(project, id).GroupId = null;
    }

    /// <summary>Links the given items into a fresh group (replacing any group they were in) and
    /// returns the new group id.</summary>
    public static Guid Group(Project project, IEnumerable<Guid> itemIds)
    {
        var group = Guid.NewGuid();
        foreach (var id in itemIds)
            Require(project, id).GroupId = group;

        return group;
    }

    /// <summary>Clears <see cref="Item.GroupId"/> on every item of a track — the row's sync
    /// toggle turned off. The other members of those groups stay grouped to each other;
    /// <see cref="TryRegroupTrack"/> is the inverse while the row is still aligned.</summary>
    public static void UngroupTrack(Project project, Guid trackId)
    {
        foreach (var item in project.Items)
        {
            if (item.TrackId == trackId)
                item.GroupId = null;
        }
    }

    /// <summary>
    /// Dissolves every group whose members all sit on one track. A group exists to keep
    /// <i>rows</i> in step — the recording's screen, webcam and audio trimming and cutting as one
    /// — so once only one row is left in it (the audio rows deleted, the webcam ungrouped, a
    /// recording that never had anything but a screen) there is nothing left to keep in step,
    /// and the group would only pin that row's clips in place. Cursor/keyboard overlay items
    /// keep their group whatever the shape: validation requires it, and their row is defined by
    /// the screen row it annotates. Returns true when anything changed.
    /// </summary>
    public static bool CollapseLoneGroups(Project project)
    {
        var changed = false;
        foreach (var members in project.Items.Where(i => i.GroupId != null)
                                             .GroupBy(i => i.GroupId.Value))
        {
            if (members.Any(m => m.Content is CursorContent or KeyboardContent))
                continue;

            if (members.Select(m => m.TrackId).Distinct().Skip(1).Any())
                continue;

            foreach (var m in members)
                m.GroupId = null;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Puts a track back into the groups it was ungrouped from, but only when that is still
    /// true: every item of the row must overlap exactly one group on the other tracks, and must
    /// agree with it on source alignment — same <c>TimelineStartTicks - SourceInTicks</c> offset,
    /// the invariant a trim preserves and a move breaks. Items of the row may resolve to
    /// <i>different</i> groups: after a <see cref="Split"/> each contiguous segment is its own
    /// group, and each of the row's segments re-joins the one that covers it. Returns false
    /// leaving the project untouched when any item overlaps no group, more than one, or a group it
    /// has drifted from; a track with no items trivially succeeds.
    /// </summary>
    public static bool TryRegroupTrack(Project project, Guid trackId)
    {
        var row = project.Items.Where(i => i.TrackId == trackId).ToList();

        // effect items never link: they have no source clock to be in sync with.
        if (row.Any(i => i.Content is SpeedContent or ZoomContent))
            return false;

        var candidates = project.Items.Where(i => i.TrackId != trackId && i.GroupId != null).ToList();

        var resolved = new List<(Item Item, Guid Group)>(row.Count);
        foreach (var item in row)
        {
            var overlapping = candidates.Where(c => Overlaps(item, c)).ToList();
            var groups = overlapping.Select(c => c.GroupId.Value).Distinct().ToList();
            if (groups.Count != 1)
                return false;

            // one dissenting member is enough to make the row's claim of sync a lie.
            if (!overlapping.All(c => Aligned(item, c)))
                return false;

            resolved.Add((item, groups[0]));
        }

        foreach (var (item, group) in resolved)
            item.GroupId = group;

        return true;
    }

    private static bool Overlaps(Item a, Item b) =>
        a.TimelineStartTicks < b.TimelineEndTicks && b.TimelineStartTicks < a.TimelineEndTicks;

    /// <summary>Whether two items map source time to timeline time identically. Only media carries
    /// such a mapping — text, images and solids have nothing to disagree about. A re-timed item
    /// (speed ≠ 1) or a freeze frame never re-links: its clock has left the recording's for good.</summary>
    private static bool Aligned(Item a, Item b) =>
        a.Content is not MediaContent ma || b.Content is not MediaContent mb ||
        (!ma.Freeze && !mb.Freeze && SpeedOf(ma) == 1.0 && SpeedOf(mb) == 1.0 &&
         a.TimelineStartTicks - ma.SourceInTicks == b.TimelineStartTicks - mb.SourceInTicks);

    /// <summary>
    /// Sets a media item's <see cref="MediaContent.Speed"/>, re-timing the clip in place: the item
    /// keeps showing the same stretch of source, so its timeline duration scales by
    /// <c>oldSpeed / newSpeed</c>, anchored at its start. The new duration is clamped to at least
    /// <see cref="MinSegmentTicks"/> and to the gap before the next item on the track (slowing a
    /// clip down must not run it into its neighbor — the content is end-trimmed instead). Single
    /// item, media only (a freeze frame has no speed); returns the speed actually stored (1.0 for
    /// non-media and freeze frames).
    /// </summary>
    public static double SetSpeed(Project project, Guid itemId, double speed)
    {
        var item = Require(project, itemId);
        if (item.Content is not MediaContent media || media.Freeze)
            return 1.0;

        speed = Math.Clamp(speed, 0.01, 100);
        var oldSpeed = SpeedOf(media);
        if (speed == oldSpeed)
            return speed;

        var sourceSpan = SourceTicksBetween(project, media, item.TimelineStartTicks, item.TimelineEndTicks);
        long newDuration;
        if (media.SpeedWarpExempt)
        {
            // the same source plays out over sourceSpan / speed OUTPUT ticks; the project span
            // that covers is whatever the warp makes of it from the item's start.
            var warp = Playback.TimeWarp.Build(project);
            var outputStart = warp.ToOutput(item.TimelineStartTicks);
            newDuration = warp.ToProject(outputStart + (long)Math.Round(sourceSpan / speed)) - item.TimelineStartTicks;
        }
        else
        {
            newDuration = (long)Math.Round(sourceSpan / speed);
        }

        media.Speed = speed;
        item.DurationTicks = ClampDurationToRow(project, item, newDuration);
        return speed;
    }

    /// <summary>A duration <paramref name="item"/> may take without breaking the model: at
    /// least <see cref="MinSegmentTicks"/>, and short of the next item on its row. The clamp
    /// every re-timing applies (<see cref="SetSpeed"/>, an exempt clip's re-fit under a changed
    /// warp or at a new position): a duration that ran the clip into its neighbor would be
    /// rolled back by validation, and end-trimming the clip is the useful answer.</summary>
    public static long ClampDurationToRow(Project project, Item item, long duration)
    {
        var limit = long.MaxValue;
        foreach (var other in project.Items)
        {
            if (other.Id != item.Id && other.TrackId == item.TrackId &&
                other.TimelineStartTicks > item.TimelineStartTicks)
                limit = Math.Min(limit, other.TimelineStartTicks - item.TimelineStartTicks);
        }

        return Math.Clamp(duration, MinSegmentTicks, Math.Max(MinSegmentTicks, limit));
    }

    /// <summary>The item's playback speed with the model's "unset means realtime" collapsed:
    /// always a positive factor, 1.0 for null media.</summary>
    public static double SpeedOf(MediaContent media) =>
        media != null && media.Speed > 0 ? media.Speed : 1.0;

    /// <summary>A timeline span rendered into source ticks at <paramref name="speed"/> — exact
    /// for realtime so speed-1 projects keep their integer-perfect math.</summary>
    private static long ToSourceTicks(long timelineTicks, double speed) =>
        speed == 1.0 ? timelineTicks : (long)Math.Round(timelineTicks * speed);

    /// <summary>
    /// The source ticks a media item consumes between two project instants. For an ordinary
    /// clip that is the project span at the clip's speed; for a <see cref="MediaContent.SpeedWarpExempt"/>
    /// clip it is the OUTPUT span between the two instants (the clip plays in real time under
    /// the project's speed items) at the clip's speed. Every operation that moves an in-point
    /// (trim, split, cut) goes through here so the two clocks cannot drift apart.
    /// </summary>
    public static long SourceTicksBetween(Project project, MediaContent media, long fromProjectTicks,
        long toProjectTicks)
    {
        if (media is { Freeze: true })
            return 0;

        var speed = SpeedOf(media);
        if (media == null || !media.SpeedWarpExempt)
            return ToSourceTicks(toProjectTicks - fromProjectTicks, speed);

        var warp = Playback.TimeWarp.Build(project);
        return ToSourceTicks(warp.ToOutput(toProjectTicks) - warp.ToOutput(fromProjectTicks), speed);
    }

    /// <summary>How far an exempt clip's start may move earlier before its in-point would rewind
    /// past the start of its source, in project ticks: the source it has behind the in-point
    /// plays out in output ticks, mapped back through the warp from the clip's output start.</summary>
    private static long ExemptStartHeadroom(Project project, Item item, MediaContent media, double speed)
    {
        var warp = Playback.TimeWarp.Build(project);
        var outputStart = warp.ToOutput(item.TimelineStartTicks);
        var roomOutput = (long)Math.Floor(media.SourceInTicks / speed);
        var earliest = warp.ToProject(Math.Max(0, outputStart - roomOutput));
        return Math.Max(0, item.TimelineStartTicks - earliest);
    }

    private static bool Covers(Item item, long timelineTicks) =>
        timelineTicks >= item.TimelineStartTicks && timelineTicks < item.TimelineEndTicks;

    private static Item Require(Project project, Guid itemId)
    {
        var item = project.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
            throw new ArgumentException($"Item {itemId} is not in the project.", nameof(itemId));

        return item;
    }
}
