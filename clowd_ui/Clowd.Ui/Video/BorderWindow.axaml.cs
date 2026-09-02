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
    /// topmost transparent Window (not SystemThemedWindow): the 3px accent + 2px white inner
    /// border are inflated OUTSIDE the capture region so no border pixel can ever appear in the
    /// recording, and the window is made input-invisible natively (WS_EX_TRANSPARENT + layered +
    /// WM_NCHITTEST->HTTRANSPARENT on Windows; setIgnoresMouseEvents: on macOS).
    /// </summary>
    public partial class BorderWindow : Window
    {
        // 3px accent + 2px white inner line, in logical px. These two are the source of truth for BOTH
        // the outward inflation and the drawn thickness (see ApplyGeometry) — they used to be three
        // unlinked copies of one number and nothing in the build caught a mismatch.
        private const int AccentLogicalWidth = 3;
        private const int InnerLogicalWidth = 2;
        private const int BorderLogicalWidth = AccentLogicalWidth + InnerLogicalWidth;

        // The capture region in the platform capture coordinate space (§1.1): physical px in
        // virtual-desktop coordinates on Windows, CG points on macOS — which is also exactly what
        // Avalonia PixelPoint positioning uses on each platform, so no unit conversion here.
        // Not readonly only because of SetRegion; nothing else reassigns it.
        private ScreenRect _region;

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

            // matches the overlay the region was selected in, and is dark enough for the white
            // overlay text stroked with it (AppStyles.CaptureAccentColor).
            var accent = new SolidColorBrush(AppStyles.CaptureAccentColor);
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

        /// <summary>
        /// Moves the frame onto a new region, re-deriving the whole geometry (edge suppression
        /// included, since the region may have crossed onto another monitor) from the window's
        /// current scaling. The frame is purely a function of the region, so a setter is the whole
        /// border-side cost of the share-region resize mode.
        ///
        /// Every caller is ShareRegionPage. Resize mode is a swap (addendum 8.1): this window is
        /// hidden for the whole of it and ShareResizeWindow draws the frame the user watches follow
        /// their pointer, so what re-homes the border is the APPLIED region, written here just
        /// before it is shown again. The page does also call this once per accepted drag step, but
        /// on a window nobody can see, and only to stop the hidden border's idea of the region from
        /// drifting arbitrarily far from the live one; nothing visible depends on that write.
        ///
        /// The grab handles are NOT here: they live in ShareResizeWindow, a separate hit-testable
        /// sibling that takes the input and reports the drag to the page. This window stays
        /// click-through in every mode, and the reasons are mechanical rather than a matter of
        /// taste:
        ///
        /// - WindowNativeExtensions.AddExStyles registers an anonymous, unretained lambda with
        ///   Win32Properties.AddWindowStylesCallback. No equivalent delegate can ever be handed to
        ///   RemoveWindowStylesCallback, and removing a callback would not un-apply bits already on
        ///   the HWND anyway, so any runtime attempt to clear WS_EX_TRANSPARENT can be silently
        ///   re-OR'd back on by the next Avalonia style re-application.
        /// - WS_EX_LAYERED must stay live (with SetLayeredFullyOpaque) or the window is never
        ///   repainted at all, so the ex-style mask is not something to toggle casually.
        /// - Focusable="False" plus WS_EX_NOACTIVATE are exactly right for a passive frame and
        ///   exactly wrong for a window that must hold pointer capture through a drag.
        /// </summary>
        public void SetRegion(ScreenRect region)
        {
            _region = region ?? throw new ArgumentNullException(nameof(region));
            ApplyGeometry(RenderScaling);
        }

        /// <summary>Sets the centered overlay text ("WAIT…"/"PRESS\nSTART"); null or empty hides
        /// it. Newlines break it into centered lines, and the text is scaled down to fit a small
        /// capture region rather than being clipped by it.
        /// This text renders INSIDE the region, so it belongs only to sessions with a window in
        /// which nothing is being captured yet: a recording clears it before frames flow, and a
        /// share session — whose region is mirrored continuously from the moment the helper starts
        /// — has no such window and must never call it, or the words go into the meeting.
        /// ScrollCapturePage.cs:86-91 documents the same constraint for the same reason.</summary>
        public void SetOverlayText(string text)
        {
            var value = text ?? String.Empty;
            OverlayText.Text = value;
            OverlayBox.IsVisible = value.Length > 0;
        }

        /// <summary>
        /// Shows or hides the struck-through eye at the center of the region: the mark that the
        /// people watching a shared region are currently NOT seeing what is inside the frame.
        /// <para>
        /// This glyph renders INSIDE the region — the same place <see cref="SetOverlayText"/>'s
        /// words land, and the reason that method is off-limits to a share session. What makes it
        /// safe here is the one state it is shown in: it goes up only while the helper is
        /// obscuring the mirror, so the pixels it is drawn on are pixels nobody in the meeting is
        /// being shown. The caller owns that pairing, and must take the glyph down in the same step
        /// it stops obscuring — a stale eye would sit in the picture the meeting IS seeing, saying
        /// the opposite of the truth.
        /// </para>
        /// <para>
        /// It is worth being precise about "not seeing": <c>hide</c> replaces the region outright,
        /// but <c>blur</c> and <c>pixelate</c> only degrade it, so under those two the glyph does
        /// reach the meeting as a large soft shape. That is the intended reading — the region is
        /// obscured and something is deliberately covering it — rather than a leak, because the
        /// glyph carries no information the obscure mode was hiding.
        /// </para>
        /// </summary>
        public void SetHiddenIndicator(bool hidden)
        {
            HiddenBox.IsVisible = hidden;
        }

        private void OnOpened(object sender, EventArgs e)
        {
            // Mandatory: a window that gains WS_EX_LAYERED without a subsequent
            // SetLayeredWindowAttributes call is never repainted (design §4.2).
            WindowNativeExtensions.SetLayeredFullyOpaque(this);
            WindowNativeExtensions.SetIgnoresMouseEvents(this);
            // Raise above the menu bar BEFORE re-applying geometry: at the default level AppKit
            // constrains the frame away from the menu bar at Show(), so the position set below
            // only sticks once the level is lifted (issue #56).
            WindowNativeExtensions.SetCanCoverMenuBar(this);
            ApplyGeometry(RenderScaling);
        }

        private void OnScalingChanged(object sender, EventArgs e)
        {
            ApplyGeometry(RenderScaling);
        }

        /// <summary>
        /// Position first (capture space), then size at the given scaling. The border width is
        /// inflated outward in whole capture-space units plus 1 extra of slack, so logical→capture
        /// size rounding can never place a border pixel inside the recorded region (risk §6.4).
        /// Edges within ~2 logical px of a monitor edge are not rendered (or inflated) at all —
        /// the frame there would land under the macOS menu bar or on a neighboring display
        /// rather than visibly around the recording (issue #56).
        /// </summary>
        private void ApplyGeometry(double scaling)
        {
            // logical → capture space: physical px on Windows (× RenderScaling); on macOS the
            // region is CG points, which ARE logical units, so the factor is 1 regardless of Retina.
            var toCapture = OperatingSystem.IsMacOS() ? 1.0 : scaling;
            var inflate = (int)Math.Ceiling(BorderLogicalWidth * toCapture) + 1;

            var threshold = 2 * toCapture;
            bool left = true, top = true, right = true, bottom = true;
            try
            {
                // Screen bounds are in the same capture space as _region (see field comment).
                // Perpendicular-overlap guard: only a monitor the region actually spans against
                // can suppress an edge.
                foreach (var screen in Screens.All)
                {
                    var b = screen.Bounds;
                    var overlapsX = _region.Left < b.Right && b.X < _region.Right;
                    var overlapsY = _region.Top < b.Bottom && b.Y < _region.Bottom;
                    if (overlapsY && Math.Abs(_region.Left - b.X) <= threshold) left = false;
                    if (overlapsY && Math.Abs(_region.Right - b.Right) <= threshold) right = false;
                    if (overlapsX && Math.Abs(_region.Top - b.Y) <= threshold) top = false;
                    if (overlapsX && Math.Abs(_region.Bottom - b.Bottom) <= threshold) bottom = false;
                }
            }
            catch
            {
                // Screens is best-effort; without it every edge renders, as before.
            }

            int l = left ? inflate : 0, t = top ? inflate : 0;
            int r = right ? inflate : 0, b2 = bottom ? inflate : 0;

            Position = new PixelPoint(_region.X - l, _region.Y - t);
            Width = (_region.Width + l + r) / toCapture;
            Height = (_region.Height + t + b2) / toCapture;

            AccentBorder.BorderThickness = new Thickness(left ? AccentLogicalWidth : 0, top ? AccentLogicalWidth : 0,
                                                         right ? AccentLogicalWidth : 0, bottom ? AccentLogicalWidth : 0);
            InnerBorder.BorderThickness = new Thickness(left ? InnerLogicalWidth : 0, top ? InnerLogicalWidth : 0,
                                                        right ? InnerLogicalWidth : 0, bottom ? InnerLogicalWidth : 0);
        }
    }
}
