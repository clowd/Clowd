using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Clowd.Config
{
    /// <summary>
    /// Plain settings container. Loading and saving is handled by <see cref="SettingsService"/>;
    /// constructing this type (or any category) has no side effects whatsoever.
    /// <see cref="Current"/> is assigned explicitly during application startup.
    /// </summary>
    public class SettingsRoot : SimpleNotifyObject
    {
        /// <summary>
        /// The application-wide settings instance. Assigned explicitly at startup
        /// (after <see cref="SettingsService.Load()"/>) — never from a constructor.
        /// </summary>
        [Browsable(false)]
        public static SettingsRoot Current { get; set; }

        public SettingsGeneral General { get; set; } = new SettingsGeneral();

        public SettingsHotkey Hotkeys { get; set; } = new SettingsHotkey();

        public SettingsCapture Capture { get; set; } = new SettingsCapture();

        public SettingsRecording Recording { get; set; } = new SettingsRecording();

        public SettingsShareRegion ShareRegion { get; set; } = new SettingsShareRegion();

        public SettingsEditor Editor { get; set; } = new SettingsEditor();

        public SettingsVideoEditor VideoEditor { get; set; } = new SettingsVideoEditor();

        public SettingsUpload Uploads { get; set; } = new SettingsUpload();

        /// <summary>
        /// Whether the "Upload with Clowd" shell entries (the Explorer verb and the Win11 sparse
        /// package) should be registered right now.
        /// <see cref="SettingsGeneral.RegisterExplorerContextMenu"/> is the user's preference, but
        /// the verb does nothing except start an upload, so with uploads off it would offer a
        /// feature that is turned off — every other upload entry point disappears in that state,
        /// and this one has to as well.
        /// </summary>
        [Browsable(false), JsonIgnore]
        public bool ShouldRegisterExplorerContextMenu =>
            General.RegisterExplorerContextMenu && Uploads.IsEnabled;
    }
}
