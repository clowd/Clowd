using System.ComponentModel;

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

        public SettingsEditor Editor { get; set; } = new SettingsEditor();

        public SettingsVideoEditor VideoEditor { get; set; } = new SettingsVideoEditor();

        public SettingsUpload Uploads { get; set; } = new SettingsUpload();
    }
}
