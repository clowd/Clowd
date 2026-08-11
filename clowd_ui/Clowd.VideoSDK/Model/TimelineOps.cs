using System;
using System.Collections.Generic;
using System.Linq;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// The editing operations, and the <b>only</b> place <see cref="Item.LinkGroupId"/> semantics
/// live — timeline control, keyboard shortcuts and tests all come through here, so link behaviour
/// cannot drift between entry points. The operations that change <i>when</i> content plays
/// (<see cref="Move"/>, <see cref="Split"/>, <see cref="RippleDelete"/>) resolve the target item's
/// link group first and apply to all members (an unlinked item is a group of one); the operations
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

        var maxShrink = item.DurationTicks - MinSegmentTicks;
        if (deltaTicks > maxShrink)
            deltaTicks = Math.Max(0, maxShrink);

        var maxExtend = media == null
            ? item.TimelineStartTicks
            : Math.Min(item.TimelineStartTicks, media.SourceInTicks);
        if (deltaTicks < -maxExtend)
            deltaTicks = -maxExtend;

        if (deltaTicks == 0)
            return 0;

        item.TimelineStartTicks += deltaTicks;
        item.DurationTicks -= deltaTicks;
        if (media != null)
            media.SourceInTicks += deltaTicks;

        return deltaTicks;
    }

    /// <summary>
    /// Moves the out-point of a <b>single</b> item, link group or not: positive
    /// <paramref name="deltaTicks"/> lengthens it, negative shortens it, clamped so it keeps at
    /// least <see cref="MinSegmentTicks"/>. Extension past the end of a media source is allowed —
    /// the compositor holds the last frame, matching VFR gap behaviour. The item's start and
    /// in-point are untouched, so the source↔timeline mapping of every instant it still covers is
    /// unchanged and linked rows stay in sync. Returns the delta actually applied.
    /// </summary>
    public static long TrimEnd(Project project, Guid itemId, long deltaTicks)
    {
        var item = Require(project, itemId);

        var maxShrink = item.DurationTicks - MinSegmentTicks;
        if (deltaTicks < -maxShrink)
            deltaTicks = Math.Min(0, -maxShrink);

        if (deltaTicks == 0)
            return 0;

        item.DurationTicks += deltaTicks;
        return deltaTicks;
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
        var members = GetLinkedItems(project, itemId);

        if (!Covers(target, timelineTicks))
            return false;

        var covered = members.Where(m => Covers(m, timelineTicks)).ToList();
        foreach (var m in covered)
        {
            if (timelineTicks - m.TimelineStartTicks < MinSegmentTicks ||
                m.TimelineEndTicks - timelineTicks < MinSegmentTicks)
                return false;
        }

        var rightGroup = target.LinkGroupId == null ? (Guid?)null : Guid.NewGuid();
        foreach (var m in covered)
        {
            var leftLength = timelineTicks - m.TimelineStartTicks;

            var content = m.Content?.Clone();
            if (content is MediaContent media)
                media.SourceInTicks += leftLength;

            var right = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = m.TrackId,
                TimelineStartTicks = timelineTicks,
                DurationTicks = m.TimelineEndTicks - timelineTicks,
                Content = content,
                Transform = m.Transform?.Clone() ?? new Transform(),
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
    /// Removes the item's whole link group and closes the gap: every remaining item that started
    /// at or after the group did shifts left by the group's span (clamped so nothing shifts to
    /// before where the group began). This is the multi-track generalization of the v1 "cut":
    /// items on <b>all</b> tracks shift, so cross-track sync is preserved.
    /// </summary>
    public static void RippleDelete(Project project, Guid itemId)
    {
        var members = GetLinkedItems(project, itemId);

        var start = members.Min(m => m.TimelineStartTicks);
        var span = members.Max(m => m.TimelineEndTicks) - start;

        foreach (var m in members)
            project.Items.Remove(m);

        foreach (var item in project.Items)
        {
            if (item.TimelineStartTicks >= start)
                item.TimelineStartTicks = Math.Max(start, item.TimelineStartTicks - span);
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
    /// such a mapping — text, images and solids have nothing to disagree about.</summary>
    private static bool Aligned(Item a, Item b) =>
        a.Content is not MediaContent ma || b.Content is not MediaContent mb ||
        a.TimelineStartTicks - ma.SourceInTicks == b.TimelineStartTicks - mb.SourceInTicks;

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
