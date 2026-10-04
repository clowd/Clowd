using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>Which group of a <see cref="TrayZonePanel"/> a child belongs to.</summary>
    public enum TrayZone
    {
        /// <summary>Packed from the leading edge (left in a row, top in a column).</summary>
        Start,

        /// <summary>Centred in the free space between the start and end groups.</summary>
        Center,

        /// <summary>Packed against the trailing edge.</summary>
        End,
    }

    /// <summary>
    /// The pure arithmetic behind <see cref="TrayZonePanel"/>, kept free of Avalonia so it can be tested
    /// without a platform.
    /// </summary>
    public static class TrayZoneLayout
    {
        /// <summary>
        /// Positions three consecutive groups along a <paramref name="total"/>-long axis: start at 0, end
        /// against the trailing edge, centre in the middle of the space left between them. When the
        /// groups do not fit, the centre never slides back over the start group and the end group is
        /// pushed past <paramref name="total"/> rather than over the centre: overflow is clipped at the
        /// trailing edge, never painted as overlapping groups.
        /// </summary>
        public static (double startPos, double centerPos, double endPos) Arrange(
            double total, double startLen, double centerLen, double endLen)
        {
            var free = total - startLen - endLen;
            var centerPos = Math.Max(startLen, startLen + (free - centerLen) / 2);
            var endPos = Math.Max(total - endLen, centerPos + centerLen);
            return (0, centerPos, endPos);
        }
    }

    /// <summary>
    /// A one-line panel with three groups: <see cref="TrayZone.Start"/> children packed from the leading
    /// edge, <see cref="TrayZone.End"/> children packed against the trailing one, and
    /// <see cref="TrayZone.Center"/> children centred in what is left between them. Within a group the
    /// children keep their order, <see cref="Spacing"/> apart, and every child is centred across the axis
    /// at its own size. It is the flexbox "group, spacer, group, spacer, group" row as one panel, so a
    /// compact lane needs no invisible stretchy children that would also have to be rotated.
    /// <para>
    /// Desired length is the three groups plus spacing, so on its own the panel packs tight; it spreads
    /// only when its parent arranges it longer (a <see cref="TrayDeck"/> lane does).
    /// </para>
    /// </summary>
    public class TrayZonePanel : Panel, ITrayOrientable
    {
        public static readonly AttachedProperty<TrayZone> ZoneProperty =
            AvaloniaProperty.RegisterAttached<TrayZonePanel, Control, TrayZone>("Zone", TrayZone.Start);

        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<TrayZonePanel, double>(nameof(Spacing));

        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<TrayZonePanel, Orientation>(nameof(Orientation), Orientation.Horizontal);

        static TrayZonePanel()
        {
            AffectsMeasure<TrayZonePanel>(SpacingProperty, OrientationProperty);
            AffectsParentMeasure<TrayZonePanel>(ZoneProperty);
        }

        public static TrayZone GetZone(Control control) => control.GetValue(ZoneProperty);

        public static void SetZone(Control control, TrayZone value) => control.SetValue(ZoneProperty, value);

        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var horizontal = Orientation == Orientation.Horizontal;
            var childAvailable = horizontal
                ? new Size(double.PositiveInfinity, availableSize.Height)
                : new Size(availableSize.Width, double.PositiveInfinity);

            double across = 0;
            foreach (var child in Children)
            {
                child.Measure(childAvailable);
                across = Math.Max(across, horizontal ? child.DesiredSize.Height : child.DesiredSize.Width);
            }

            var groups = MeasureGroups(horizontal);
            var along = groups.Start + groups.Center + groups.End;
            return horizontal ? new Size(along, across) : new Size(across, along);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var horizontal = Orientation == Orientation.Horizontal;
            var total = horizontal ? finalSize.Width : finalSize.Height;
            var across = horizontal ? finalSize.Height : finalSize.Width;

            var groups = MeasureGroups(horizontal);
            var (startPos, centerPos, endPos) = TrayZoneLayout.Arrange(total, groups.Start, groups.Center, groups.End);

            ArrangeGroup(TrayZone.Start, startPos, across, horizontal);
            ArrangeGroup(TrayZone.Center, centerPos, across, horizontal);
            // the end group's length carries its leading gap (see MeasureGroups), so its first child
            // starts that gap after the position the layout hands back
            ArrangeGroup(TrayZone.End, endPos + groups.EndLead, across, horizontal);
            return finalSize;
        }

        /// <summary>
        /// Each group's length along the axis, with the spacing that separates two non-empty groups
        /// folded in: the start group carries a trailing gap when anything follows it, the end group a
        /// leading gap when a centre group precedes it. So the pure layout needs no spacing parameter, and the
        /// three lengths sum to exactly the panel's desired length.
        /// </summary>
        private (double Start, double Center, double End, double EndLead) MeasureGroups(bool horizontal)
        {
            double start = 0, center = 0, end = 0;
            int startCount = 0, centerCount = 0, endCount = 0;
            foreach (var child in Children)
            {
                if (!child.IsVisible)
                    continue;

                var length = horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
                switch (GetZone(child))
                {
                    case TrayZone.Center:
                        center += length;
                        centerCount++;
                        break;
                    case TrayZone.End:
                        end += length;
                        endCount++;
                        break;
                    default:
                        start += length;
                        startCount++;
                        break;
                }
            }

            var spacing = Spacing;
            var startTrail = startCount > 0 && centerCount + endCount > 0 ? spacing : 0;
            // with no centre group the start group's trailing gap already separates start from end
            var endLead = endCount > 0 && centerCount > 0 ? spacing : 0;
            start += Gaps(startCount) + startTrail;
            center += Gaps(centerCount);
            end += Gaps(endCount) + endLead;
            return (start, center, end, endLead);

            double Gaps(int count) => count > 1 ? (count - 1) * spacing : 0;
        }

        private void ArrangeGroup(TrayZone zone, double position, double across, bool horizontal)
        {
            foreach (var child in Children)
            {
                if (GetZone(child) != zone || !child.IsVisible)
                    continue;

                var size = child.DesiredSize;
                if (horizontal)
                {
                    child.Arrange(new Rect(position, (across - size.Height) / 2, size.Width, size.Height));
                    position += size.Width + Spacing;
                }
                else
                {
                    child.Arrange(new Rect((across - size.Width) / 2, position, size.Width, size.Height));
                    position += size.Height + Spacing;
                }
            }
        }
    }
}
