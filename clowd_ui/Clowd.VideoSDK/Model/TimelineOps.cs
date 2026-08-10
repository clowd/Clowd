using System;
using System.Collections.Generic;
using System.Linq;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// The editing operations, and the <b>only</b> place <see cref="Item.LinkGroupId"/> semantics
/// live — timeline control, keyboard shortcuts and tests all come through here, so link behaviour
/// cannot drift between entry points. Every operation resolves the target item's link group first
/// and applies to all members (an unlinked item is a group of one).
///
/// Operations clamp rather than corrupt: a move or trim that would push any group member out of
/// bounds — before the timeline origin, under <see cref="MinSegmentTicks"/>, or before the start
/// of its source — is reduced to the largest amount every member can absorb, and the applied
/// amount is returned so callers can reflect it. Operations that cannot be partially applied
/// (<see cref="Split"/>) reject instead.
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
    /// Moves the in-point of the item's link group: positive <paramref name="deltaTicks"/> shrinks
    /// the items from the start, negative extends them earlier. Clamped so every member keeps at
    /// least <see cref="MinSegmentTicks"/>, starts at or after 0, and — for media — never rewinds
    /// before the start of its source (<see cref="MediaContent.SourceInTicks"/> stays ≥ 0).
    /// Media in-points move with the trim so the same source frame stays under the cut. Returns
    /// the delta actually applied.
    /// </summary>
    public static long TrimStart(Project project, Guid itemId, long deltaTicks)
    {
        var members = GetLinkedItems(project, itemId);

        foreach (var m in members)
        {
            var maxShrink = m.DurationTicks - MinSegmentTicks;
            if (deltaTicks > maxShrink)
                deltaTicks = Math.Max(0, maxShrink);

            var maxExtend = m.TimelineStartTicks;
            if (m.Content is MediaContent media)
                maxExtend = Math.Min(maxExtend, media.SourceInTicks);
            if (deltaTicks < -maxExtend)
                deltaTicks = -maxExtend;
        }

        if (deltaTicks == 0)
            return 0;

        foreach (var m in members)
        {
            m.TimelineStartTicks += deltaTicks;
            m.DurationTicks -= deltaTicks;
            if (m.Content is MediaContent media)
                media.SourceInTicks += deltaTicks;
        }

        return deltaTicks;
    }

    /// <summary>
    /// Moves the out-point of the item's link group: positive <paramref name="deltaTicks"/>
    /// lengthens the items, negative shortens them, clamped so every member keeps at least
    /// <see cref="MinSegmentTicks"/>. Extension past the end of a media source is allowed — the
    /// compositor holds the last frame, matching VFR gap behaviour. Returns the delta actually
    /// applied.
    /// </summary>
    public static long TrimEnd(Project project, Guid itemId, long deltaTicks)
    {
        var members = GetLinkedItems(project, itemId);

        foreach (var m in members)
        {
            var maxShrink = m.DurationTicks - MinSegmentTicks;
            if (deltaTicks < -maxShrink)
                deltaTicks = Math.Min(0, -maxShrink);
        }

        if (deltaTicks == 0)
            return 0;

        foreach (var m in members)
            m.DurationTicks += deltaTicks;

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
