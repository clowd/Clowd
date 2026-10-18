using System.ComponentModel;

namespace Clowd.Config
{
    /// <summary>
    /// Whether uploading exists as a feature. Rendered by the Uploads page as the tile selector at
    /// its top (<see cref="ModeSelectorAttribute"/>). Off removes every way to start an upload —
    /// the tray items, the hotkeys, the capture overlay's UPLOAD button, the Recent rows' and the
    /// image editor's upload buttons — while what has already been uploaded stays in Recents.
    /// Persisted by name.
    /// </summary>
    public enum UploadsMode
    {
        [Description("On")]
        [ModeCaption("Uploads captures, recordings and files to the destinations enabled below.")]
        On,

        [Description("Off")]
        [ModeCaption("Turns uploading off entirely.")]
        Off,
    }
}
