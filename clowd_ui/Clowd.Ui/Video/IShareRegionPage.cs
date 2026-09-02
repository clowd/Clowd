using Clowd.PlatformUtil;

namespace Clowd
{
    /// <summary>
    /// Supervises one "share region" session: the overlay has already picked the rectangle and
    /// exited (the "share x,y,w,h" marker, CAPTURE_PROTOCOL.md), and from here the page owns the
    /// external <c>clowd_share_region</c> helper that mirrors those pixels into a plain window a
    /// meeting app can be pointed at. Unlike a recording or a scrolling capture there is no session
    /// directory involved at all — a share produces no files — so the region is the entire hand-off.
    /// <para>Not an <see cref="IPage"/>, for the same reason
    /// <see cref="IScrollCapturePage"/> is not: the page has no window of its own (it borrows the
    /// recording session's border and toolbar), and nothing outside it can meaningfully close it —
    /// the session ends when the user presses CANCEL or when the helper's process goes away. The
    /// app-exit path reaches a live share through <c>ShareRegionPage.ActiveInstance</c>, exactly the
    /// way it reaches an in-flight recording.</para>
    /// </summary>
    public interface IShareRegionPage
    {
        void Open(ScreenRect region);
    }
}
