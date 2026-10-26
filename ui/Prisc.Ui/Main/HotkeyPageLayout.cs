using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Prisc.UI.Config
{
    /// <summary>
    /// One row on the Hotkeys page. <see cref="Label"/> is the short name shown beside the
    /// shortcuts — it reads on from its section's title ("Draw" / "On Screen"), so it leaves out
    /// what the title already says. Screen readers still get the full [DisplayName].
    /// </summary>
    public sealed record HotkeyRow(HotkeyId Id, string Label);

    /// <summary>One titled group of rows on the Hotkeys page.</summary>
    public sealed record HotkeySection(string Title, IReadOnlyList<HotkeyRow> Rows)
    {
        public IEnumerable<HotkeyId> Actions => Rows.Select(r => r.Id);
    }

    /// <summary>
    /// Which hotkey actions the Hotkeys page shows, grouped and in what order. Every
    /// <see cref="HotkeyId"/> appears exactly once (a test holds it to that, so a new action cannot
    /// be left off the page). Whether a row shows is its [VisibleWhen] on
    /// <see cref="Prisc.Config.SettingsHotkey"/>.
    /// </summary>
    public static class HotkeyPageLayout
    {
        /// <summary>The note above the sections on <paramref name="platform"/>, or null. On macOS:
        /// a MacBook's top row sends its printed functions, which the hook sees as system keys,
        /// not F-keys, so an F-key shortcut seems dead until the user knows about Fn.</summary>
        public static string PlatformNote(OSPlatform platform) =>
            platform == OSPlatform.OSX
                ? "On a MacBook the top row controls brightness, volume and the like unless you hold Fn, "
                  + "so an F-key shortcut needs Fn as well, or turn on “Use F1, F2, etc. keys as standard "
                  + "function keys” in System Settings → Keyboard → Keyboard Shortcuts… → Function Keys."
                : null;

        public static IReadOnlyList<HotkeySection> Sections { get; } = new[]
        {
            new HotkeySection("Capture", new[]
            {
                new HotkeyRow(HotkeyId.CaptureRegion, "Region"),
                new HotkeyRow(HotkeyId.CaptureFullscreen, "Active Screen"),
                new HotkeyRow(HotkeyId.CaptureActive, "Active Window"),
            }),
            // neither name repeats the title, so these two keep their full names
            new HotkeySection("Record & share", new[]
            {
                new HotkeyRow(HotkeyId.StartStopRecording, "Start / Stop Recording"),
                new HotkeyRow(HotkeyId.ShareRegion, "Share Region"),
            }),
            new HotkeySection("Draw", new[]
            {
                new HotkeyRow(HotkeyId.DrawOnScreen, "On Screen"),
                new HotkeyRow(HotkeyId.DrawOnScreenshot, "On Instant Screenshot"),
            }),
            // the tool windows, in the tray menu's order
            new HotkeySection("Open", new[]
            {
                new HotkeyRow(HotkeyId.ColorWheel, "Color Wheel"),
                new HotkeyRow(HotkeyId.NewImageEditor, "New Image Editor"),
                new HotkeyRow(HotkeyId.NewVideoEditor, "New Video Editor"),
            }),
            new HotkeySection("Upload", new[]
            {
                new HotkeyRow(HotkeyId.FileUpload, "From File"),
                new HotkeyRow(HotkeyId.ClipboardUpload, "Clipboard"),
            }),
        };
    }
}
