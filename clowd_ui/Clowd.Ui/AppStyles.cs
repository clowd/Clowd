using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Clowd.Config;

namespace Clowd
{
    public enum ResourceIcon
    {
        IconClowd,
        IconPhoto,
        IconVideo,
        IconCopy,
        IconCopySmall,
        IconSave,
        IconSaveSmall,
        IconSearch,
        IconReset,
        IconClose,
        IconPlay,
        IconStop,
        IconPause,
        IconSettings,
        IconDrawing,
        IconMicrophoneEnabled,
        IconMicrophoneDisabled,
        IconSpeakerEnabled,
        IconSpeakerDisabled,
        IconToolNone,
        IconToolPointer,
        IconToolRectangle,
        IconToolFilledRectangle,
        IconToolEllipse,
        IconToolLine,
        IconToolArrow,
        IconToolPolyLine,
        IconToolText,
        IconToolPixelate,
        IconToolErase,
        IconUndo,
        IconRedo,
        IconPinned,
        IconCrop,
        IconChevronDown,
        IconHamburgerMore,
        IconVideoMKV,
        IconVideoMP4,
        IconVideoGIF,
    }

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
                    // Semi theme accent token (SolidColorBrush per theme variant).
                    if (app.TryGetResource("SemiColorPrimary", app.ActualThemeVariant, out var brushValue) && brushValue is ISolidColorBrush brush)
                        return brush.Color;
                    // Underlying palette color the primary brush is fed from.
                    if (app.TryGetResource("SemiBlue5Color", app.ActualThemeVariant, out var colorValue) && colorValue is Color c)
                        return c;
                }
                return Color.FromRgb(0, 120, 215);
            }
        }

        public static IBrush AccentBackgroundBrush => new SolidColorBrush(AccentColor);

        /// <summary>
        /// The accent of the capture surfaces — the overlay's button panel, and the C# windows
        /// styled to match it (the recording toolbar, the recording border, the scrolling-capture
        /// status strip). Not <see cref="AccentColor"/>: that one follows the Semi theme and themes
        /// the ordinary app UI, while this is the OS accent (or the user's pick) put through
        /// <see cref="AccentColors.EnsureContrastWithWhite"/> — the same value
        /// <see cref="CaptureArguments"/> hands the overlay as <c>--accent-color</c>, so a Clowd
        /// window sitting beside the overlay is painted the same blue rather than a near-miss.
        ///
        /// The correction is not cosmetic. Every one of these surfaces draws white glyphs and
        /// labels directly on the accent fill, which a light accent leaves unreadable (issue #48).
        /// </summary>
        public static Color CaptureAccentColor
            => SettingsRoot.Current?.General?.GetEffectiveAccentColor() ?? AccentColors.Default;

        public static IBrush CaptureAccentBackgroundBrush => new SolidColorBrush(CaptureAccentColor);

        public static IBrush IdealBackgroundBrush => new SolidColorBrush(Color.FromRgb(55, 55, 55));

        public static IBrush IdealForegroundBrush => Brushes.White;

        public static IBrush CheckerboardBrushSmall => Util.CheckerBrushes.Light;

        public static bool IsDarkTheme => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

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

        // Sizes (and opacity) carried over from the WPF VectorIcons.xaml Path declarations.
        // The geometry data itself lives in Assets/VectorIcons.axaml as StreamGeometry resources.
        private readonly record struct IconInfo(double Width, double Height, double Opacity = 1.0);

        private static readonly Dictionary<ResourceIcon, IconInfo> _iconInfo = new()
        {
            { ResourceIcon.IconClowd, new IconInfo(16, 16) },
            { ResourceIcon.IconPhoto, new IconInfo(26, 26) },
            { ResourceIcon.IconVideo, new IconInfo(26, 26) },
            { ResourceIcon.IconCopy, new IconInfo(30, 30) },
            { ResourceIcon.IconCopySmall, new IconInfo(172, 172) },
            { ResourceIcon.IconSave, new IconInfo(30, 30) },
            { ResourceIcon.IconSaveSmall, new IconInfo(172, 172) },
            { ResourceIcon.IconSearch, new IconInfo(26, 26) },
            { ResourceIcon.IconReset, new IconInfo(26, 26) },
            { ResourceIcon.IconClose, new IconInfo(26, 26) },
            { ResourceIcon.IconPlay, new IconInfo(172, 172) },
            { ResourceIcon.IconStop, new IconInfo(24, 24) },
            { ResourceIcon.IconPause, new IconInfo(24, 24) },
            { ResourceIcon.IconSettings, new IconInfo(172, 172) },
            { ResourceIcon.IconDrawing, new IconInfo(24, 24) },
            { ResourceIcon.IconMicrophoneEnabled, new IconInfo(172, 172) },
            { ResourceIcon.IconMicrophoneDisabled, new IconInfo(172, 172, 0.2) },
            { ResourceIcon.IconSpeakerEnabled, new IconInfo(172, 172) },
            { ResourceIcon.IconSpeakerDisabled, new IconInfo(172, 172, 0.2) },
            { ResourceIcon.IconToolNone, new IconInfo(26, 26) },
            { ResourceIcon.IconToolPointer, new IconInfo(26, 26) },
            { ResourceIcon.IconToolRectangle, new IconInfo(26, 26) },
            { ResourceIcon.IconToolFilledRectangle, new IconInfo(26, 26) },
            { ResourceIcon.IconToolEllipse, new IconInfo(26, 26) },
            { ResourceIcon.IconToolLine, new IconInfo(26, 26) },
            { ResourceIcon.IconToolArrow, new IconInfo(26, 26) },
            { ResourceIcon.IconToolPolyLine, new IconInfo(26, 26) },
            { ResourceIcon.IconToolText, new IconInfo(26, 26) },
            { ResourceIcon.IconToolPixelate, new IconInfo(32, 32) },
            { ResourceIcon.IconToolErase, new IconInfo(22, 22) },
            { ResourceIcon.IconUndo, new IconInfo(26, 26) },
            { ResourceIcon.IconRedo, new IconInfo(26, 26) },
            { ResourceIcon.IconPinned, new IconInfo(172, 172) },
            { ResourceIcon.IconCrop, new IconInfo(172, 172) },
            { ResourceIcon.IconChevronDown, new IconInfo(32, 32) },
            { ResourceIcon.IconHamburgerMore, new IconInfo(26, 26) },
            { ResourceIcon.IconVideoMKV, new IconInfo(36, 24) },
            { ResourceIcon.IconVideoMP4, new IconInfo(36, 24) },
            { ResourceIcon.IconVideoGIF, new IconInfo(36, 24) },
        };

        /// <summary>
        /// Creates a new Path element for the requested icon. A new instance is returned on every
        /// call (replaces the WPF x:Shared="False" Path resources).
        /// </summary>
        public static Control GetIconElement(ResourceIcon icon)
        {
            var app = Application.Current;
            object value = null;
            app?.TryGetResource(icon.ToString(), app.ActualThemeVariant, out value);
            if (value is not Geometry geometry)
                throw new KeyNotFoundException($"Icon geometry resource '{icon}' was not found.");

            var info = _iconInfo.TryGetValue(icon, out var i) ? i : new IconInfo(26, 26);
            return new Path
            {
                Data = geometry,
                Fill = Brushes.White,
                Width = info.Width,
                Height = info.Height,
                Opacity = info.Opacity,
            };
        }
    }
}
