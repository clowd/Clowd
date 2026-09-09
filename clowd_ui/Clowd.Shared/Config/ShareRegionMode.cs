using System.ComponentModel;

namespace Clowd.Config
{
    /// <summary>
    /// Whether sharing a region exists as a feature. Rendered by the Shared Region page as the
    /// tile selector at its top (<see cref="ModeSelectorAttribute"/>). Off removes every way to
    /// start a share — the tray item, the hotkey and the capture overlay's SHARE button — and
    /// hides the rest of the page.
    /// Persisted by name.
    /// </summary>
    public enum ShareRegionMode
    {
        [Description("On")]
        [ModeCaption("Part of your screen can be mirrored into a window a meeting app can share, from the capture overlay, the tray menu or a hotkey.")]
        On,

        [Description("Off")]
        [ModeCaption("Turns region sharing off entirely. The SHARE button, tray item and hotkey disappear.")]
        Off,
    }
}
