using System.ComponentModel;

namespace Prisc.Config
{
    /// <summary>
    /// How screen recording works, if at all. Rendered by the settings page as the mode banner
    /// at the top of the Recording page (<see cref="ModeSelectorAttribute"/>); the description of
    /// each member is its title, <see cref="ModeCaptionAttribute"/> its caption and
    /// <see cref="ModeIconAttribute"/> its glyph. Declaration order is chip order. Persisted by name.
    /// </summary>
    public enum RecordingMode
    {
        /// <summary>One track per stream (screen, each audio device, the webcam), so the recording
        /// opens in the video editor. What used to be "composition on".</summary>
        [Description("Studio Mode")]
        [ModeCaption("Records the screen, each audio device and the webcam as separate tracks.")]
        [ModeIcon("IconLayers")]
        Studio,

        /// <summary>A single flattened, ready-to-share video. What used to be "composition off".</summary>
        [Description("Instant Mode")]
        [ModeCaption("Records straight to a single video file that is ready to share.")]
        [ModeIcon("IconLightning")]
        Instant,

        /// <summary>No recording at all: the recording and video-editing entry points disappear from
        /// the capture overlay, the tray menu and the main window.</summary>
        [Description("Off")]
        [ModeCaption("Turns screen recording and the video editor off entirely.")]
        [ModeIcon("IconPower")]
        Off,
    }
}
