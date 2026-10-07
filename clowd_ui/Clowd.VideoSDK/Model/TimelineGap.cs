using System;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// An empty stretch of one row between two items, or between the origin and the row's first
/// item: <c>[StartTicks, EndTicks)</c> with nothing on <see cref="TrackId"/> in it. Found by
/// <see cref="TimelineOps.FindGap"/> and handed back to the gap operations, which re-find it
/// first and refuse a gap the project no longer has (the row changed under an open menu).
/// </summary>
/// <param name="LeftItemId">The item ending at <paramref name="StartTicks"/>, or null for the
/// leading gap before a row's first item.</param>
/// <param name="RightItemId">The item starting at <paramref name="EndTicks"/>. Never empty: the
/// open run past a row's last item is the end of the row, not a gap.</param>
public readonly record struct TimelineGap(Guid TrackId, long StartTicks, long EndTicks,
    Guid? LeftItemId, Guid RightItemId)
{
    public long DurationTicks => EndTicks - StartTicks;
}
