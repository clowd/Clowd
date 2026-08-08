using Clowd.PlatformUtil;

namespace Clowd
{
    /// <summary>
    /// Supervises a scrolling capture: the overlay has already picked the region and the point the
    /// wheel will be aimed at, and hands both over here (the "scroll x,y,w,h px,py hwnd" marker,
    /// CAPTURE_PROTOCOL.md). The page owns the session directory from that moment on — it shows the
    /// border, drives the external <c>--scroll-drive</c> capturer, and either routes the finished
    /// session into the editor or deletes the directory. Nothing else may touch that directory.
    /// <para>Not an <see cref="IPage"/>: like the recording page it has no window of its own, and
    /// nothing outside it can meaningfully close it — the run ends when the driver says so. The
    /// app-exit path reaches an in-flight run through <c>ScrollCapturePage.ActiveInstance</c>, the
    /// same way it reaches an in-flight recording.</para>
    /// </summary>
    public interface IScrollCapturePage
    {
        void Open(ScreenRect region, ScreenPoint scrollPoint, long targetHwnd, string sessionDir);
    }
}
