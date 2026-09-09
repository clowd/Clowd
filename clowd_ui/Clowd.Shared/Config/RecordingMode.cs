using System.ComponentModel;

namespace Clowd.Config
{
    /// <summary>
    /// How screen recording works, if at all. Rendered by the settings page as the big tile
    /// selector at the top of the Recording page (<see cref="ModeSelectorAttribute"/>); the
    /// description of each member is the tile's title and <see cref="ModeCaptionAttribute"/> its
    /// caption. Declaration order is tile order. Persisted by name.
    /// </summary>
    public enum RecordingMode
    {
        /// <summary>One track per stream (screen, each audio device, the webcam), so the recording
        /// opens in the video editor. What used to be "composition on".</summary>
        [Description("Studio Mode")]
        [ModeCaption("Records the screen, each audio device and the webcam as separate tracks. Recordings open in the video editor, where they can be trimmed, arranged and rendered.")]
        Studio,

        /// <summary>A single flattened, ready-to-share video. What used to be "composition off".</summary>
        [Description("Instant Mode")]
        [ModeCaption("Records straight to a single video file that is ready to share. Smaller and simpler, but the recording cannot be edited afterwards.")]
        Instant,

        /// <summary>No recording at all: the recording and video-editing entry points disappear from
        /// the capture overlay, the tray menu and the main window.</summary>
        [Description("Off")]
        [ModeCaption("Turns screen recording and the video editor off entirely. They disappear from the capture overlay, the tray menu and the main window.")]
        Off,
    }
}
