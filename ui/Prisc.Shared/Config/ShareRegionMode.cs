using System.ComponentModel;

namespace Prisc.Config
{
    /// <summary>
    /// Whether sharing a region exists as a feature. Rendered by the Shared Region page as the
    /// mode banner at its top (<see cref="ModeSelectorAttribute"/>). Off removes every way to
    /// start a share — the tray item, the hotkey and the capture overlay's SHARE button — and
    /// hides the rest of the page.
    /// Persisted by name.
    /// </summary>
    public enum ShareRegionMode
    {
        [Description("On")]
        [ModeCaption("Mirrors part of your screen into a window a meeting app can share.")]
        [ModeIcon("IconNavShareRegion")]
        On,

        [Description("Off")]
        [ModeCaption("Turns region sharing off entirely.")]
        [ModeIcon("IconPower")]
        Off,
    }
}
