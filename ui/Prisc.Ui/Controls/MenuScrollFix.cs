using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// Suppresses the phantom scroll that Avalonia-drawn menus grow at fractional display
    /// scalings. Attach it with the <c>Enabled</c> attached property; AppResources.axaml turns it
    /// on for every <see cref="ContextMenu"/> and <see cref="MenuFlyoutPresenter"/> in the app.
    /// </summary>
    /// <remarks>
    /// Avalonia sizes the menu's window to the menu's desired height in DIPs, but the Win32
    /// backend converts that with a truncating cast (<c>WindowImpl.Resize</c> does
    /// <c>(int)(value.Height * RenderScaling)</c>), so at scalings such as 125% or 150% the
    /// window can come back up to one physical pixel shorter than requested. The menu is then
    /// arranged into that smaller ClientSize, its extent ends up a fraction of a pixel taller
    /// than the viewport, and Avalonia's MenuScrollingVisibilityConverter - which compares extent
    /// and viewport for exact equality - shows the up/down scroll chevrons for overflow the user
    /// cannot even see.
    ///
    /// When the overflow is inside that rounding noise we turn scrolling off for the menu, so it
    /// renders in full with no chevrons. A menu that genuinely does not fit on screen overflows by
    /// a whole item and keeps its scroll.
    ///
    /// This covers the Windows tray menu as well: <c>TrayIconImpl</c> shows it as a managed
    /// MenuFlyoutPresenter in a SizeToContent window, which is arranged from the same truncated
    /// ClientSize. macOS exports the tray menu to a real NSMenu, so nothing there matches.
    /// </remarks>
    public static class MenuScrollFix
    {
        /// <summary>
        /// Vertical overflow (in DIPs) small enough to be window rounding rather than content.
        /// The truncating resize alone can never lose more than one DIP, but a menu that opens on
        /// a monitor whose scaling differs from the parent window's has its layout rounded to the
        /// wrong pixel grid as well, so leave room for a little more drift than that. The
        /// tolerance only ever clips the bottom of the last item's padding, never its text.
        /// </summary>
        private const double RoundingToleranceDip = 2.0;

        public static readonly AttachedProperty<bool> EnabledProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(MenuScrollFix));

        static MenuScrollFix()
        {
            EnabledProperty.Changed.AddClassHandler<Control, bool>(
                (control, args) => OnEnabledChanged(control, args.NewValue.GetValueOrDefault()));
        }

        public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);

        public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);

        private static void OnEnabledChanged(Control menu, bool enabled)
        {
            // LayoutUpdated only fires while the menu is attached to a top level, so it doubles as
            // the "menu is open" signal — a ContextMenu is attached to its PopupRoot on open, and
            // the tray menu's presenter is built fresh for each right-click.
            menu.LayoutUpdated -= OnLayoutUpdated;
            menu.DetachedFromVisualTree -= OnDetached;

            if (!enabled)
                return;

            menu.LayoutUpdated += OnLayoutUpdated;
            menu.DetachedFromVisualTree += OnDetached;
        }

        private static void OnLayoutUpdated(object sender, EventArgs e)
        {
            // Once scrolling is off the extent no longer tells us anything useful, so the decision
            // stands until the menu closes.
            if (FindScrollViewer(sender) is not { VerticalScrollBarVisibility: ScrollBarVisibility.Auto } sv)
                return;

            var overflow = sv.Extent.Height - sv.Viewport.Height;
            if (overflow > 0 && overflow <= RoundingToleranceDip)
                sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }

        private static void OnDetached(object sender, VisualTreeAttachmentEventArgs e)
        {
            // A ContextMenu keeps its template across opens, so the next open has to measure
            // itself again — it may land on a monitor with different scaling.
            if (FindScrollViewer(sender) is { } sv)
                sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        private static ScrollViewer FindScrollViewer(object menu) =>
            menu is Visual visual
                ? visual.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
                : null;
    }
}
