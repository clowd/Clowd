using System.ComponentModel;

namespace Clowd.Config
{
    /// <summary>
    /// Remembered view state for the video editor window. Everything here is
    /// <see cref="BrowsableAttribute">[Browsable(false)]</see>: none of it is a preference the user
    /// sets on the settings page, it is just what the window looked like last time (the same deal
    /// as <see cref="SettingsEditor.SidebarWidth"/> and
    /// <see cref="SettingsGeneral.MainWindowBounds"/>).
    /// </summary>
    public class SettingsVideoEditor : SimpleNotifyObject
    {
        /// <summary>Width of the properties sidebar; matches the image editor's default.</summary>
        [Browsable(false)]
        public double SidebarWidth
        {
            get => _sidebarWidth;
            set => Set(ref _sidebarWidth, value);
        }

        /// <summary>Last window placement as "x,y,width,height" (physical pixels), restored on open
        /// when it still intersects a connected screen — the exact format and semantics of
        /// <see cref="SettingsGeneral.MainWindowBounds"/>. Null until the window has been opened once.</summary>
        [Browsable(false)]
        public string WindowBounds
        {
            get => _windowBounds;
            set => Set(ref _windowBounds, value);
        }

        /// <summary>Whether the window was maximized when it was last closed. Tracked separately
        /// from <see cref="WindowBounds"/>, which always holds the *restored* placement.</summary>
        [Browsable(false)]
        public bool WindowMaximized
        {
            get => _windowMaximized;
            set => Set(ref _windowMaximized, value);
        }

        private double _sidebarWidth = 230;
        private string _windowBounds;
        private bool _windowMaximized;
    }
}
