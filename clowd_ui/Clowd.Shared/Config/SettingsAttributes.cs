using System;

namespace Clowd.Config
{
    // Markers that say something about a settings row in general rather than about one section's
    // subject matter, which is why they live here instead of beside whichever settings class first
    // needed them. The ones carrying real behaviour (VisibleWhen, DisabledWhen, SuggestedValues,
    // ModeSelector) keep their own files; these are bare markers with nothing to read.
    //
    // Selectors tied to one section's subject (the audio and camera device pickers) stay with that
    // section: they are not generic, they name a specific kind of hardware.

    /// <summary>
    /// Hides a settings row from the generated settings UI on macOS. The property still persists
    /// and is still applied where the platform honors it — used for the speaker device picker,
    /// which selects nothing on macOS: ScreenCaptureKit captures the whole system mix, so there
    /// is no output device to choose (obs-express ignores the id on macOS 13+).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class HiddenOnMacOSAttribute : Attribute
    {
    }

    /// <summary>
    /// The mirror of <see cref="HiddenOnMacOSAttribute"/>: hides a settings row everywhere except
    /// macOS. For a row whose subject only exists there, so leaving it visible would offer a
    /// switch that changes nothing. It means "everywhere except macOS", so it hides the row on
    /// Linux too.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class HiddenOnWindowsAttribute : Attribute
    {
    }

    /// <summary>
    /// Hides a settings row on Linux (X11 and Wayland alike), for a feature Linux cannot provide —
    /// the overlay forces it off there, or the helper behind it is not built for Linux. The value
    /// still persists, so a settings file shared with another OS keeps it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class HiddenOnLinuxAttribute : Attribute
    {
    }

    /// <summary>
    /// Hides a settings row only in a Linux Wayland session, for things that exist on X11 but not
    /// on Wayland: the capture overlay's own options (screenshots come from the desktop's
    /// screenshot tool there, see <see cref="ClowdPlatform.IsWayland"/>) and anything driven by
    /// the global keyboard hook, which never starts on Wayland. The value still persists.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class HiddenOnWaylandAttribute : Attribute
    {
    }

    /// <summary>
    /// Renders the rows of a nested settings object inline, as part of the parent's page, instead
    /// of as an object of its own.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class FlattenSettingsObjectAttribute : Attribute
    { }
}
