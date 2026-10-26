using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// Two lanes on one tray: a full-size primary lane (<see cref="PrimaryExtent"/>, the button height)
    /// and a compact secondary lane (<see cref="SecondaryExtent"/>) beside it across the strip axis, a
    /// <see cref="Gap"/> apart. In a row the lanes stack as two rows; rotated, they stand side by side as
    /// two columns, the primary one first.
    /// <para>
    /// <c>Children[0]</c> is the primary lane and <c>Children[1]</c> the secondary; anything after them is
    /// ignored. Each lane gets exactly its extent across the axis and the deck's full length along it, so
    /// a lane that lays out start/centre/end groups (<see cref="TrayZonePanel"/>) spreads to the longer
    /// lane's length.
    /// </para>
    /// <para>
    /// The deck is the axis writer for everything inside it. <see cref="FloatingTray"/> pushes its
    /// orientation into its direct items only, and the deck is one item holding many, so on every
    /// rotation — and whenever a lane joins — it forwards the axis to every <see cref="ITrayOrientable"/>
    /// in its logical subtree. Lanes are expected to be filled before they join the deck.
    /// </para>
    /// </summary>
    public class TrayDeck : Panel, ITrayOrientable
    {
        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<TrayDeck, Orientation>(nameof(Orientation), Orientation.Horizontal);

        public static readonly StyledProperty<double> PrimaryExtentProperty =
            AvaloniaProperty.Register<TrayDeck, double>(nameof(PrimaryExtent), TrayTokens.PrimaryLaneExtent);

        public static readonly StyledProperty<double> SecondaryExtentProperty =
            AvaloniaProperty.Register<TrayDeck, double>(nameof(SecondaryExtent), TrayTokens.SecondaryLaneExtent);

        public static readonly StyledProperty<double> GapProperty =
            AvaloniaProperty.Register<TrayDeck, double>(nameof(Gap), TrayTokens.Gap);

        static TrayDeck()
        {
            AffectsMeasure<TrayDeck>(OrientationProperty, PrimaryExtentProperty, SecondaryExtentProperty, GapProperty);
            OrientationProperty.Changed.AddClassHandler<TrayDeck>((deck, _) => deck.PushOrientation());
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public double PrimaryExtent
        {
            get => GetValue(PrimaryExtentProperty);
            set => SetValue(PrimaryExtentProperty, value);
        }

        public double SecondaryExtent
        {
            get => GetValue(SecondaryExtentProperty);
            set => SetValue(SecondaryExtentProperty, value);
        }

        public double Gap
        {
            get => GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        protected override void ChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            base.ChildrenChanged(sender, e);
            PushOrientation();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var horizontal = Orientation == Orientation.Horizontal;
            double length = 0;
            for (var i = 0; i < Math.Min(Children.Count, 2); i++)
            {
                var extent = i == 0 ? PrimaryExtent : SecondaryExtent;
                var child = Children[i];
                child.Measure(horizontal
                    ? new Size(double.PositiveInfinity, extent)
                    : new Size(extent, double.PositiveInfinity));
                length = Math.Max(length, horizontal ? child.DesiredSize.Width : child.DesiredSize.Height);
            }

            var across = PrimaryExtent + Gap + SecondaryExtent;
            return horizontal ? new Size(length, across) : new Size(across, length);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var horizontal = Orientation == Orientation.Horizontal;
            var length = horizontal ? finalSize.Width : finalSize.Height;
            double offset = 0;
            for (var i = 0; i < Math.Min(Children.Count, 2); i++)
            {
                var extent = i == 0 ? PrimaryExtent : SecondaryExtent;
                Children[i].Arrange(horizontal
                    ? new Rect(0, offset, length, extent)
                    : new Rect(offset, 0, extent, length));
                offset += extent + Gap;
            }

            return finalSize;
        }

        /// <summary>A walk over a few dozen logical nodes on a rotation: no inherited property, no
        /// per-child subscription, nothing to unsubscribe — the same trade <see cref="FloatingTray"/>
        /// makes for its own items.</summary>
        private void PushOrientation()
        {
            var orientation = Orientation;
            foreach (var node in this.GetLogicalDescendants())
            {
                if (node is ITrayOrientable orientable)
                    orientable.Orientation = orientation;
            }
        }
    }
}
