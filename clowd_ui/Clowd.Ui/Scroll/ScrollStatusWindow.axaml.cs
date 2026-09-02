using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// The scrolling capture HUD: a progress readout plus FINISH and CANCEL. Visually the
    /// recording toolbar (<see cref="FloatingToolbarWindow"/>) stripped to its load-bearing
    /// parts — WS_EX_NOACTIVATE so a click can never pull focus away from the window being
    /// scrolled (which would send the wheel somewhere else), WS_EX_TOOLWINDOW so it stays out of
    /// the taskbar, and a placement cascade around the capture region.
    /// <para>The one rule this window does not share with the toolbar: it is never shown inside
    /// the region. The driver photographs that rectangle after every scroll step, so a strip
    /// overlapping it by a single pixel would be stitched into the finished image. If nothing
    /// fits outside it, <see cref="TryShowNear"/> refuses and the run proceeds without a HUD —
    /// Esc and the automatic end detection still finish it.</para>
    /// </summary>
    public partial class ScrollStatusWindow : Window
    {
        // Mirror of the Width/Height declared in XAML, in logical px. Duplicated (rather than
        // read back off the window) because the placement search runs before the window has ever
        // been laid out — the whole point is to decide whether to show it at all.
        private const int WidthLogical = 300;
        private const int HeightLogical = 50;

        // Breathing room between the strip and the region, in logical px. The border window
        // inflates itself outward by ceil(BorderLogicalWidth * scaling) + 1, which is now roughly
        // 6 logical px — the frame draws a 3px accent over a 2px inner line, where it used to draw
        // 2px over 1px for roughly 4. This constant is an independent copy of that clearance and
        // does NOT track it: BorderWindow's constants are private to that file and nothing in the
        // build catches a divergence, so it is set deliberately wider than the inflation to leave
        // real clear space between the frame and the strip rather than merely avoiding an overlap.
        private const int GapLogical = 10;

        public event EventHandler FinishClicked;
        public event EventHandler CancelClicked;

        // the capture region, in the platform capture space (physical px in virtual-desktop
        // coordinates on Windows, CG points on macOS) — which is also what PixelPoint uses.
        private ScreenRect _region;

        public ScrollStatusWindow()
        {
            InitializeComponent();

            // never steal focus from the window being scrolled; no taskbar/alt-tab entry.
            WindowNativeExtensions.AddExStyles(this, WindowNativeExtensions.WS_EX_NOACTIVATE | WindowNativeExtensions.WS_EX_TOOLWINDOW);

            // a DPI change moves the strip in capture space even though the region has not
            // moved, and can take the placement that fitted away entirely.
            ScalingChanged += (s, e) => Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded);
        }

        /// <summary>Sets the two-line readout: what has been captured, and what is happening
        /// (or how to stop it). Either line may be null to blank it.</summary>
        public void SetStatus(string primary, string secondary)
        {
            StatusText.Text = primary ?? String.Empty;
            HintText.Text = secondary ?? String.Empty;
        }

        /// <summary>
        /// Shows the strip outside <paramref name="region"/>, or returns false without ever
        /// showing it when no placement clears the region on the region's own monitor. The
        /// position is applied BEFORE <see cref="Window.Show"/>, so there is no frame in which
        /// the strip exists somewhere provisional.
        /// </summary>
        public bool TryShowNear(ScreenRect region)
        {
            _region = region ?? throw new ArgumentNullException(nameof(region));

            // pre-show guess, exactly as BorderWindow does it: the window has no RenderScaling of
            // its own yet, and Reposition below corrects from the real value once it does.
            var scaling = 1.0;
            try
            {
                scaling = Screens.ScreenFromPoint(new PixelPoint(region.Center.X, region.Center.Y))?.Scaling ?? 1.0;
            }
            catch
            {
                // Screens is best-effort pre-show; a wrong guess costs a reposition, not a frame
                // inside the region — the placement is re-checked below.
            }

            if (!TryComputePlacement(region, scaling, out var position))
                return false;

            Position = position;
            Show();

            Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded);
            return true;
        }

        /// <summary>Re-places the strip from the window's actual scaling. Hides it outright if
        /// the placement that fitted at the guessed scaling no longer does — being invisible is
        /// always better than being in the picture.</summary>
        private void Reposition()
        {
            if (_region == null || !IsVisible)
                return;

            var scaling = OperatingSystem.IsMacOS() ? 1.0 : RenderScaling;
            if (TryComputePlacement(_region, scaling, out var position))
                Position = position;
            else
                Hide();
        }

        /// <summary>
        /// The FloatingToolbarWindow cascade (below → right → left) plus an "above" rung, minus
        /// its last resort of sitting inside the region. A candidate is accepted only if it fits
        /// entirely on the region's monitor and misses the region entirely; the second test is
        /// the invariant, the first only keeps the strip reachable.
        /// </summary>
        private bool TryComputePlacement(ScreenRect region, double scaling, out PixelPoint position)
        {
            position = default;

            // logical → capture space: physical px on Windows; on macOS the region is in CG
            // points, which ARE logical units, so the factor is 1 even on Retina.
            var toCapture = OperatingSystem.IsMacOS() ? 1.0 : scaling;
            var w = (int)Math.Ceiling(WidthLogical * toCapture);
            var h = (int)Math.Ceiling(HeightLogical * toCapture);
            var gap = (int)Math.Ceiling(GapLogical * toCapture);

            Screen screen = null;
            try
            {
                screen = Screens.ScreenFromPoint(new PixelPoint(region.Center.X, region.Center.Y)) ?? Screens.Primary;
            }
            catch
            {
                // no screen enumeration means no placement we can defend; refuse rather than
                // guess a position that might land in the region.
            }

            if (screen == null)
                return false;

            var b = screen.Bounds;
            var bounds = new ScreenRect(b.X, b.Y, b.Width, b.Height);

            // the part of the region actually on this monitor, so a selection spanning two
            // displays is placed against the edge the strip can reach.
            var selection = region.Intersect(bounds);
            if (selection.IsEmpty())
                selection = region;

            var centerX = Clamp(selection.Left + selection.Width / 2 - w / 2, bounds.Left, bounds.Right - w);
            var centerY = Clamp(selection.Top + selection.Height / 2 - h / 2, bounds.Top, bounds.Bottom - h);

            var candidates = new[]
            {
                new ScreenRect(centerX, selection.Bottom + gap, w, h),      // below
                new ScreenRect(selection.Right + gap, centerY, w, h),       // right
                new ScreenRect(selection.Left - gap - w, centerY, w, h),    // left
                new ScreenRect(centerX, selection.Top - gap - h, w, h),     // above
            };

            foreach (var candidate in candidates)
            {
                if (candidate.Left < bounds.Left || candidate.Top < bounds.Top ||
                    candidate.Right > bounds.Right || candidate.Bottom > bounds.Bottom)
                    continue;

                // the load-bearing test: whatever the arithmetic above produced, it does not
                // overlap the rectangle the driver is about to photograph.
                if (candidate.IntersectsWith(region))
                    continue;

                position = new PixelPoint(candidate.Left, candidate.Top);
                return true;
            }

            return false;
        }

        /// <summary>Math.Clamp with a monitor narrower than the strip tolerated: the caller's
        /// bounds check rejects that candidate anyway, but Math.Clamp would throw first.</summary>
        private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, Math.Max(min, max));

        private void FinishButtonClicked(object sender, RoutedEventArgs e)
        {
            FinishClicked?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButtonClicked(object sender, RoutedEventArgs e)
        {
            CancelClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
