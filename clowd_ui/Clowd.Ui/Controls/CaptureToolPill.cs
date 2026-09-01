using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The small rounded pill that floats just outside a <see cref="CaptureToolButton"/> and opens
    /// that button's device picker. It deliberately sits OFF the gray strip rather than being
    /// welded to the tile: the tile grid is the overlay's look and a split-button rail inside it
    /// reads as a seventh kind of button, while a detached pill reads as an annotation on the tile
    /// it points at.
    ///
    /// The host owns its geometry — the pill lies along the panel's stacking axis and moves to
    /// whichever side the strip's placement leaves free, so both its size and its
    /// <see cref="Direction"/> are set by FloatingToolbarWindow rather than by this theme.
    /// </summary>
    public class CaptureToolPill : Button
    {
        /// <summary>Which way the chevron points, i.e. which side of the strip the pill floats on:
        /// <see cref="Dock.Bottom"/> under a horizontal row, <see cref="Dock.Left"/> or
        /// <see cref="Dock.Right"/> beside a vertical column.</summary>
        public static readonly StyledProperty<Dock> DirectionProperty =
            AvaloniaProperty.Register<CaptureToolPill, Dock>(nameof(Direction), Dock.Bottom);

        public Dock Direction
        {
            get => GetValue(DirectionProperty);
            set => SetValue(DirectionProperty, value);
        }

        static CaptureToolPill()
        {
            ControlThemes.EnsureRegistered();
        }
    }
}
