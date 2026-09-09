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
        [ModeCaption("Captures, recordings and files can be uploaded to the destinations enabled below, and their links copied to the clipboard.")]
        On,

        [Description("Off")]
        [ModeCaption("Turns uploading off entirely. The upload buttons, tray items and hotkeys disappear; anything already uploaded stays in Recents.")]
        Off,
    }
}
