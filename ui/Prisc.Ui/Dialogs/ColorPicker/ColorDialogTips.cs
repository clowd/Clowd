using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.VisualTree;
using Clowd.UI.Controls;
using Clowd.UI.Controls.Tray;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// Tips in the dialog's body. The body is one StripToolTips host (ColorDialog's content grid), so
    /// they are the editors' bar tips: one popup wearing the tray chip, on the tray's delay. Each opens
    /// beside the nearest ancestor (or the owner itself) that names a <see cref="SideProperty"/>,
    /// clear of that whole region, so a swatch's tip never covers the other swatches and a palette
    /// bar's never covers the other bars.
    /// </summary>
    public static class ColorDialogTips
    {
        /// <summary>The side of this region a tip from inside it opens on.</summary>
        public static readonly AttachedProperty<Dock?> SideProperty =
            AvaloniaProperty.RegisterAttached<Control, Dock?>("Side", typeof(ColorDialogTips));

        public static Dock? GetSide(Control control) => control.GetValue(SideProperty);

        public static void SetSide(Control control, Dock? value) => control.SetValue(SideProperty, value);

        public static readonly CustomPopupPlacementCallback Placement = p =>
        {
            Control region = null;
            for (var v = p.Target as Visual; v != null; v = v.GetVisualParent())
            {
                if (v is Control c && GetSide(c) != null)
                {
                    region = c;
                    break;
                }
            }

            var side = region != null ? GetSide(region).Value : Dock.Bottom;
            StripToolTips.Place(p, region, side is Dock.Top or Dock.Bottom, side is Dock.Top or Dock.Left);
        };

        /// <summary>Gives <paramref name="owner"/>'s tip the body's placement and delay.</summary>
        public static void Attach(Control owner)
        {
            ToolTip.SetPlacement(owner, PlacementMode.Custom);
            ToolTip.SetCustomPopupPlacementCallback(owner, Placement);
            ToolTip.SetHorizontalOffset(owner, 0);
            ToolTip.SetVerticalOffset(owner, 0);
            ToolTip.SetShowDelay(owner, TrayTokens.ToolTipShowDelayMs);
        }
    }
}
