using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Clowd.Config;

namespace Clowd
{
    public static class AppStyles
    {
        private static WindowIcon _appIcon;
        private static WindowIcon _trayIcon;

        public static Color AccentColor
        {
            get
            {
                var app = Application.Current;
                if (app != null)
                {
                    // Semi theme accent token (SolidColorBrush per theme variant). AccentTheme has
                    // already written the user's accent over it, so this follows their choice.
                    if (app.TryGetResource("SemiColorPrimary", app.ActualThemeVariant, out var brushValue) && brushValue is ISolidColorBrush brush)
                        return brush.Color;
                    // Underlying palette color the primary brush is fed from.
                    if (app.TryGetResource("SemiBlue5Color", app.ActualThemeVariant, out var colorValue) && colorValue is Color c)
                        return c;
                }
                return Color.FromRgb(0, 120, 215);
            }
        }

        /// <summary>
        /// The accent of the capture surfaces — the overlay's button panel, and the C# windows and
        /// controls styled to match it: the recording/share border, the resize overlay, and the
        /// floating strips' "mode on" button fill (a <c>TrayButton</c> with <c>IsActive</c> set —
        /// today the share strip's Resize tile; the strips themselves are graphite, and the scrolling
        /// HUD never reaches the accent at all). The value is the OS accent (or the user's pick) put
        /// through <see cref="AccentColors.EnsureContrastWithWhite"/> — the same value
        /// <see cref="CaptureArguments"/> hands the overlay as <c>--accent-color</c>, so a Clowd
        /// window sitting beside the overlay is painted the same blue rather than a near-miss.
        ///
        /// The correction is not cosmetic. Every one of these surfaces puts white ink — glyphs and
        /// labels — directly on the accent fill, which a light accent leaves unreadable (issue #48).
        /// </summary>
        public static Color CaptureAccentColor
            => SettingsRoot.Current?.General?.GetEffectiveAccentColor() ?? AccentColors.Default;

        public static IBrush CheckerboardBrushSmall => Util.CheckerBrushes.Light;

        // The .ico's multi-resolution frames are ideal for the Windows taskbar/title bar, but
        // ico decoding isn't reliable across Avalonia's non-Windows backends, so the tray and
        // window icons load a plain PNG everywhere else. (The macOS dock/bundle icon is separate:
        // it comes from clowd-default.icns baked into the .app by vpk pack — see release.yml.)
        public static WindowIcon AppIcon
            => _appIcon ??= new WindowIcon(AssetLoader.Open(new Uri(OperatingSystem.IsWindows()
                ? "avares://Clowd.Ui/Assets/clowd-default.ico"
                : "avares://Clowd.Ui/Assets/clowd-default.png")));

        // Tray icon. The macOS menu bar wants the white glyph (it sits on a dark/translucent bar);
        // the Windows notification area keeps the full-color icon.
        public static WindowIcon TrayIcon
            => _trayIcon ??= new WindowIcon(AssetLoader.Open(new Uri(OperatingSystem.IsWindows()
                ? "avares://Clowd.Ui/Assets/clowd-default.ico"
                : "avares://Clowd.Ui/Assets/clowd-white.png")));

        public static string UiDateTimePattern
            => CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern + " " +
               CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
    }
}
