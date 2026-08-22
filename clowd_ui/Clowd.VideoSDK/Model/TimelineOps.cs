using System;
using System.Collections.Generic;
using System.Linq;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// The editing operations, and the <b>only</b> place <see cref="Item.LinkGroupId"/> semantics
/// live — timeline control, keyboard shortcuts and tests all come through here, so link behavior
/// cannot drift between entry points. The operations that change <i>when</i> content plays
/// (<see cref="Move"/>, <see cref="Split"/>, <see cref="RippleDelete"/>) resolve the target item's
/// link group first and apply to the members concerned — all of them for a move, those covering
/// the instant for a split, those overlapping the item's span for a delete (an unlinked item is a
/// group of one); the operations
/// that change how much of an item is shown (<see cref="TrimStart"/>, <see cref="TrimEnd"/>) or
/// remove a lone item (<see cref="Delete"/>) are single-item.
///
/// Operations clamp rather than corrupt: a move that would push a group member before the
/// timeline origin, or a trim that would take an item under <see cref="MinSegmentTicks"/> or
/// before the start of its source, is reduced to the largest amount that fits, and the applied
/// amount is returned so callers can reflect it. Operations that cannot be partially applied
/// (<see cref="Split"/>, <see cref="TryRelinkTrack"/>) reject instead.
/// </summary>
public static class TimelineOps
{
    /// <summary>Shortest item an edit may produce, in 100ns ticks: 100ms, mirroring the v1
    /// <c>VideoEditDocument.MinSegmentMs = 100</c> — anything below this is an accidental click,
    /// not an edit.</summary>
    public const long MinSegmentTicks = 1_000_000;

    /// <summary>The items an operation on <paramref name="itemId"/> applies to: every item
    /// sharing its non-null <see cref="Item.LinkGroupId"/>, or just the item itself when
    /// unlinked. Throws when the id is not in the project.</summary>
    public static IReadOnlyList<Item> GetLinkedItems(Project project, Guid itemId)
    {
        var item = Require(project, itemId);
        if (item.LinkGroupId == null)
            return new[] { item };

        return project.Items.Where(i => i.LinkGroupId == item.LinkGroupId).ToList();
    }

    /// <summary>Shifts the item's whole link group along the timeline by
    /// <paramref name="deltaTicks"/>, clamped so no member starts before 0. Returns the delta
    /// actually applied.</summary>
    public static long Move(Project project, Guid itemId, long deltaTicks)
    {
        var members = GetLinkedItems(project, itemId);

        var minStart = members.Min(m => m.TimelineStartTicks);
        if (deltaTicks < -minStart)
            deltaTicks = -minStart;

        if (deltaTicks == 0)
            return 0;

        foreach (var m in members)
            m.TimelineStartTicks += deltaTicks;

        return deltaTicks;
    }

    /// <summary>
    /// Moves the in-point of a <b>single</b> item, link group or not: positive
    /// <paramref name="deltaTicks"/> shrinks it from the start, negative extends it earlier.
    /// Clamped so the item keeps at least <see cref="MinSegmentTicks"/>, starts at or after 0,
    /// and — for media — never rewinds before the start of its source
    /// (<see cref="MediaContent.SourceInTicks"/> stays ≥ 0). The media in-point moves with the
    /// trim, so every instant the item still covers maps to the source frame it mapped to before:
    /// trimming one member of a group cannot desync it from the others, which is why trim needs
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
        var maxExtend = media == null
            ? item.TimelineStartTicks
            : Math.Min(item.TimelineStartTicks, (long)Math.Floor(media.SourceInTicks / speed));
        if (deltaTicks < -maxExtend)
            deltaTicks = -maxExtend;

        if (deltaTicks == 0)
            return 0;

        item.TimelineStartTicks += deltaTicks;
        item.DurationTicks -= deltaTicks;
        if (media != null)
            media.SourceInTicks = Math.Max(0, media.SourceInTicks + ToSourceTicks(deltaTicks, speed));

        return deltaTicks;
    }

    /// <summary>
    /// Moves the out-point of a <b>single</b> item, link group or not: positive
    /// <paramref name="deltaTicks"/> lengthens it, negative shortens it, clamped so it keeps at
    /// least <see cref="MinSegmentTicks"/> and — for media whose stream duration is known — never
    /// extends past the end of its source (there is no material there: video would freeze on the
    /// last frame and audio would mix to silence). An item already hanging past the source end
    /// (older project, or a stale probe) may still shrink, it just cannot grow. The item's start
    /// and in-point are untouched, so the source↔timeline mapping of every instant it still
    /// covers is unchanged and linked rows stay in sync. Returns the delta actually applied.
    /// </summary>
    public static long TrimEnd(Project project, Guid itemId, long deltaTicks)
    {
        var item = Require(project, itemId);

        var maxShrink = item.DurationTicks - MinSegmentTicks;
        if (deltaTicks < -maxShrink)
            deltaTicks = Math.Min(0, -maxShrink);

        if (item.Content is MediaContent media)
        {
            var streamDuration = StreamDurationOf(project, media);
            if (streamDuration > 0)
            {
                // remaining source, expressed in timeline ticks at the item's speed
                var speed = SpeedOf(media);
                var remainingTimeline = (long)Math.Floor((streamDuration - media.SourceInTicks) / speed);
                var maxExtend = Math.Max(0, remainingTimeline - item.DurationTicks);
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
    /// Splits the item's link group at <paramref name="timelineTicks"/>: every member covering
    /// that instant becomes two back-to-back items, the right half starting exactly there with
    /// its media in-point advanced by the left half's length. Right halves of a linked group get
    /// a fresh shared <see cref="Item.LinkGroupId"/> (two linked clips become two linked pairs);
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

        var covered = GetLinkedItems(project, itemId).Where(m => Covers(m, timelineTicks)).ToList();

        // the right halves become a link group of their own, so the two sides of the cut stay
        // synced within themselves without the left side dragging the right one around.
        return SplitCore(project, covered, timelineTicks,
            target.LinkGroupId == null ? (Guid?)null : Guid.NewGuid());
    }

    /// <summary>
    /// Cuts <b>one</b> item, leaving the rest of its link group alone — the timeline's right-click
    /// split, where the pointer picked out a single clip and cutting its neighbors with it would
    /// be an edit the user did not ask for.
    ///
    /// <para>Both halves keep the item's existing <see cref="Item.LinkGroupId"/>: the clip is still
    /// part of the same recording, it simply has two segments on its row now. Group operations
    /// cope with that already — they only ever act on the members that cover the instant in
    /// question (see <see cref="Split"/>).</para>
    /// </summary>
    public static bool SplitItem(Project project, Guid itemId, long timelineTicks)
    {
        var target = Require(project, itemId);
        if (!Covers(target, timelineTicks))
            return false;

        return SplitCore(project, new[] { target }, timelineTicks, target.LinkGroupId);
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
                media.SourceInTicks += ToSourceTicks(leftLength, SpeedOf(media));

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
                LinkGroupId = rightGroup,
            };

            m.DurationTicks = leftLength;
            m.Exit = null;
            project.Items.Add(right);
        }

        project.Normalize();
        return true;
    }

    /// <summary>
    /// Cuts the item's <b>own span</b> out of its link group and closes the gap: every group
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
    /// of its link group in place, leaving the gap open and everything outside the group
    /// untouched. The delete for an imported file's linked rows, whose group means "streams of
    /// one file" — closing the gap under unrelated material is the recording cut's semantics, not
    /// the overlay's.</summary>
    public static void DeleteLinked(Project project, Guid itemId)
    {
        var target = Require(project, itemId);
        CutGroupRange(project, itemId, target.TimelineStartTicks, target.TimelineEndTicks);
    }

    /// <summary>
    /// Removes what every member of the item's link group plays inside <c>[start, end)</c>: a
    /// member inside the range is removed, one straddling an edge is trimmed (splitting in two
    /// when it hangs over both, exactly as <see cref="SplitCore"/> would cut it — in-point
    /// advanced, entry left / exit right). A remnant shorter than <see cref="MinSegmentTicks"/>
    /// is culled with the member rather than kept: the group's rows need not agree on their edges
    /// (a recording's audio ends a hair before its video), and a delete that left a sub-minimum
    /// sliver behind would strand an item no edit is allowed to produce.
    /// </summary>
    private static void CutGroupRange(Project project, Guid itemId, long start, long end)
    {
        foreach (var m in GetLinkedItems(project, itemId)
                     .Where(m => m.TimelineStartTicks < end && m.TimelineEndTicks > start).ToList())
        {
            var leftLength = start - m.TimelineStartTicks;
            var rightLength = m.TimelineEndTicks - end;

            if (rightLength >= MinSegmentTicks)
            {
                var content = m.Content?.Clone();
                if (content is MediaContent media)
                    media.SourceInTicks += ToSourceTicks(end - m.TimelineStartTicks, SpeedOf(media));

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
                    LinkGroupId = m.LinkGroupId,
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

    /// <summary>Removes a single item, leaving a gap where it was: no ripple, and link group
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

    /// <summary>Clears <see cref="Item.LinkGroupId"/> on the given items so they edit
    /// independently. Items not in the project throw; the rest of their old group is left
    /// linked.</summary>
    public static void Unlink(Project project, IEnumerable<Guid> itemIds)
    {
        foreach (var id in itemIds)
            Require(project, id).LinkGroupId = null;
    }

    /// <summary>Links the given items into a fresh group (replacing any group they were in) and
    /// returns the new group id.</summary>
    public static Guid Link(Project project, IEnumerable<Guid> itemIds)
    {
        var group = Guid.NewGuid();
        foreach (var id in itemIds)
            Require(project, id).LinkGroupId = group;

        return group;
    }

    /// <summary>Clears <see cref="Item.LinkGroupId"/> on every item of a track — the row's sync
    /// toggle turned off. The other members of those groups stay linked to each other;
    /// <see cref="TryRelinkTrack"/> is the inverse while the row is still aligned.</summary>
    public static void UnlinkTrack(Project project, Guid trackId)
    {
        foreach (var item in project.Items)
        {
            if (item.TrackId == trackId)
                item.LinkGroupId = null;
        }
    }

    /// <summary>
    /// Puts a track back into the link groups it was unlinked from, but only when that is still
    /// true: every item of the row must overlap exactly one group on the other tracks, and must
    /// agree with it on source alignment — same <c>TimelineStartTicks - SourceInTicks</c> offset,
    /// the invariant a trim preserves and a move breaks. Items of the row may resolve to
    /// <i>different</i> groups: after a <see cref="Split"/> each contiguous segment is its own
    /// group, and each of the row's segments re-joins the one that covers it. Returns false
    /// leaving the project untouched when any item overlaps no group, more than one, or a group it
    /// has drifted from; a track with no items trivially succeeds.
    /// </summary>
    public static bool TryRelinkTrack(Project project, Guid trackId)
    {
        var row = project.Items.Where(i => i.TrackId == trackId).ToList();

        // effect items never link: they have no source clock to be in sync with.
        if (row.Any(i => i.Content is SpeedContent or ZoomContent))
            return false;

        var candidates = project.Items.Where(i => i.TrackId != trackId && i.LinkGroupId != null).ToList();

        var resolved = new List<(Item Item, Guid Group)>(row.Count);
        foreach (var item in row)
        {
            var overlapping = candidates.Where(c => Overlaps(item, c)).ToList();
            var groups = overlapping.Select(c => c.LinkGroupId.Value).Distinct().ToList();
            if (groups.Count != 1)
                return false;

            // one dissenting member is enough to make the row's claim of sync a lie.
            if (!overlapping.All(c => Aligned(item, c)))
                return false;

            resolved.Add((item, groups[0]));
        }

        foreach (var (item, group) in resolved)
            item.LinkGroupId = group;

        return true;
    }

    private static bool Overlaps(Item a, Item b) =>
        a.TimelineStartTicks < b.TimelineEndTicks && b.TimelineStartTicks < a.TimelineEndTicks;

    /// <summary>Whether two items map source time to timeline time identically. Only media carries
    /// such a mapping — text, images and solids have nothing to disagree about. A re-timed item
    /// (speed ≠ 1) never re-links: its clock has left the recording's for good.</summary>
    private static bool Aligned(Item a, Item b) =>
        a.Content is not MediaContent ma || b.Content is not MediaContent mb ||
        (SpeedOf(ma) == 1.0 && SpeedOf(mb) == 1.0 &&
         a.TimelineStartTicks - ma.SourceInTicks == b.TimelineStartTicks - mb.SourceInTicks);

    /// <summary>
    /// Sets a media item's <see cref="MediaContent.Speed"/>, re-timing the clip in place: the item
    /// keeps showing the same stretch of source, so its timeline duration scales by
    /// <c>oldSpeed / newSpeed</c>, anchored at its start. The new duration is clamped to at least
    /// <see cref="MinSegmentTicks"/> and to the gap before the next item on the track (slowing a
    /// clip down must not run it into its neighbor — the content is end-trimmed instead). Single
    /// item, media only; returns the speed actually stored (unchanged for non-media).
    /// </summary>
    public static double SetSpeed(Project project, Guid itemId, double speed)
    {
        var item = Require(project, itemId);
        if (item.Content is not MediaContent media)
            return 1.0;

        speed = Math.Clamp(speed, 0.01, 100);
        var oldSpeed = SpeedOf(media);
        if (speed == oldSpeed)
            return speed;

        var sourceSpan = ToSourceTicks(item.DurationTicks, oldSpeed);
        var newDuration = (long)Math.Round(sourceSpan / speed);

        var limit = long.MaxValue;
        foreach (var other in project.Items)
        {
            if (other.Id != item.Id && other.TrackId == item.TrackId &&
                other.TimelineStartTicks > item.TimelineStartTicks)
                limit = Math.Min(limit, other.TimelineStartTicks - item.TimelineStartTicks);
        }

        media.Speed = speed;
        item.DurationTicks = Math.Clamp(newDuration, MinSegmentTicks, Math.Max(MinSegmentTicks, limit));
        return speed;
    }

    /// <summary>The item's playback speed with the model's "unset means realtime" collapsed:
    /// always a positive factor, 1.0 for null media.</summary>
    public static double SpeedOf(MediaContent media) =>
        media != null && media.Speed > 0 ? media.Speed : 1.0;

    /// <summary>A timeline span rendered into source ticks at <paramref name="speed"/> — exact
    /// for realtime so speed-1 projects keep their integer-perfect math.</summary>
    private static long ToSourceTicks(long timelineTicks, double speed) =>
        speed == 1.0 ? timelineTicks : (long)Math.Round(timelineTicks * speed);

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
