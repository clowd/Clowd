using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// Click-through accent frame drawn around the region being recorded (design §4.2). A plain
    /// topmost transparent Window (not SystemThemedWindow): the 2px accent + 1px white inner
    /// border are inflated OUTSIDE the capture region so no border pixel can ever appear in the
    /// recording, and the window is made input-invisible natively (WS_EX_TRANSPARENT + layered +
    /// WM_NCHITTEST->HTTRANSPARENT on Windows; setIgnoresMouseEvents: on macOS).
    /// </summary>
    public partial class BorderWindow : Window
    {
        // 2px accent + 1px white inner line, in logical px.
        private const int BorderLogicalWidth = 3;

        // The capture region in the platform capture coordinate space (§1.1): physical px in
        // virtual-desktop coordinates on Windows, CG points on macOS — which is also exactly what
        // Avalonia PixelPoint positioning uses on each platform, so no unit conversion here.
        private readonly ScreenRect _region;

        // satisfies the XAML compiler's runtime-loader check (AVLN3001); a border window is only
        // ever constructed with a real capture region.
        [Obsolete("Runtime-loader signature only — use BorderWindow(ScreenRect).", error: true)]
        public BorderWindow()
        {
            throw new NotSupportedException("BorderWindow requires a capture region.");
        }

        public BorderWindow(ScreenRect captureRegion)
        {
            _region = captureRegion ?? throw new ArgumentNullException(nameof(captureRegion));

            InitializeComponent();

            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

            var accent = new SolidColorBrush(AppStyles.AccentColor);
            AccentBorder.BorderBrush = accent;
            OverlayText.Stroke = accent;

            // Ex-styles must be registered before Show() so they apply from the first frame:
            // click-through (TRANSPARENT), layered (see the mandatory Opened call below), no
            // taskbar/alt-tab entry (TOOLWINDOW) and never stealing focus (NOACTIVATE).
            WindowNativeExtensions.AddExStyles(this,
                                               WindowNativeExtensions.WS_EX_TRANSPARENT | WindowNativeExtensions.WS_EX_LAYERED |
                                               WindowNativeExtensions.WS_EX_TOOLWINDOW | WindowNativeExtensions.WS_EX_NOACTIVATE);
            WindowNativeExtensions.AddHitTestTransparentHook(this);

            // Pre-show geometry from the region's screen scaling as a best guess; Opened re-applies
            // from the window's ACTUAL RenderScaling, which is the only trustworthy value (Windows
            // assigns per-monitor-v2 DPI by majority intersection, not by the region's center).
            var guessScaling = 1.0;
            try
            {
                guessScaling = Screens.ScreenFromPoint(new PixelPoint(_region.Center.X, _region.Center.Y))?.Scaling ?? 1.0;
            }
            catch
            {
                // Screens is best-effort pre-show; Opened corrects regardless.
            }

            ApplyGeometry(guessScaling);

            Opened += OnOpened;
            ScalingChanged += OnScalingChanged;
        }

        /// <summary>Sets the centered overlay text ("WAIT…"/"START"); null or empty hides it.</summary>
        public void SetOverlayText(string text)
        {
            var value = text ?? String.Empty;
            OverlayText.Text = value;
            OverlayText.IsVisible = value.Length > 0;
        }

        private void OnOpened(object sender, EventArgs e)
        {
            // Mandatory: a window that gains WS_EX_LAYERED without a subsequent
            // SetLayeredWindowAttributes call is never repainted (design §4.2).
            WindowNativeExtensions.SetLayeredFullyOpaque(this);
            WindowNativeExtensions.SetIgnoresMouseEvents(this);
            ApplyGeometry(RenderScaling);
        }

        private void OnScalingChanged(object sender, EventArgs e)
        {
            ApplyGeometry(RenderScaling);
        }

        /// <summary>
        /// Position first (physical px), then size at the given scaling. The border width is
        /// inflated outward in whole physical px plus 1 extra px of slack, so logical→physical
        /// size rounding can never place a border pixel inside the recorded region (risk §6.4).
        /// </summary>
        private void ApplyGeometry(double scaling)
        {
            var inflate = (int)Math.Ceiling(BorderLogicalWidth * scaling) + 1;

            Position = new PixelPoint(_region.X - inflate, _region.Y - inflate);
            Width = (_region.Width + inflate * 2) / scaling;
            Height = (_region.Height + inflate * 2) / scaling;
        }
    }
}
