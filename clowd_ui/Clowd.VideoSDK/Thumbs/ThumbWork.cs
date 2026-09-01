using System;
using System.Threading;

namespace Clowd.VideoSDK.Thumbs
{
    /// <summary>
    /// The public door onto the process-wide thumbnail worker — one <see cref="ThreadPriority.BelowNormal"/>
    /// thread shared by everything in the app that decodes a picture nobody is waiting on.
    ///
    /// <para>
    /// The scheduler itself stays internal (it hands out <see cref="ThumbWorkHandle"/>s and knows
    /// about the editor's bands); what leaves the assembly is the already-public
    /// <see cref="IThumbWorkQueue"/> slice, which is exactly enqueue-and-abandon plus the
    /// <see cref="IThumbWorkQueue.HasPendingBelow"/> yield check. No <c>InternalsVisibleTo</c>
    /// grant is involved.
    /// </para>
    ///
    /// <para>
    /// <b>Band discipline.</b> The editor owns 10 (waveform), 20 (keyframe pass) and 30
    /// (refinement); those are the bands that turn a blank timeline row into a real one while the
    /// user watches. Anything decorative — the recents list's poster frames and project composites —
    /// queues at 40 and up so it can never outrank an open editor's timeline, and long items poll
    /// <c>HasPendingBelow(40)</c> and park themselves, because the single thread is non-preemptive
    /// and ordering otherwise only applies to work that has not started yet.
    /// </para>
    /// </summary>
    public static class ThumbWork
    {
        /// <summary>The process-wide queue. The thread behind it starts on the first enqueue and
        /// retires itself when the queue goes idle, so touching this property costs nothing.</summary>
        public static IThumbWorkQueue Shared => ThumbWorkScheduler.Shared;

        /// <summary>Band for a preview the user is currently looking at.</summary>
        public const int RecentsVisiblePriority = 40;

        /// <summary>Band for a preview realized just outside the viewport, so a scroll lands on a
        /// drawn tile rather than a blank one.</summary>
        public const int RecentsBufferPriority = 50;
    }
}
