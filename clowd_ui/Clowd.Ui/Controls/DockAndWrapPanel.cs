using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// A WrapPanel clone (verbatim WPF UVSize math) with one addition: when all children fit on a
    /// single line and the cross-axis item size is set, children marked with the DockToEnd
    /// attached property are right-aligned (or bottom-aligned when vertical) instead of flowing.
    /// </summary>
    public class DockAndWrapPanel : Panel
    {
        public static readonly AttachedProperty<bool> DockToEndProperty =
            AvaloniaProperty.RegisterAttached<DockAndWrapPanel, Control, bool>("DockToEnd", false);

        public static bool GetDockToEnd(Control c)
        {
            return c.GetValue(DockToEndProperty);
        }

        public static void SetDockToEnd(Control c, bool value)
        {
            c.SetValue(DockToEndProperty, value);
        }

        private static bool IsWidthHeightValid(double v)
        {
            return double.IsNaN(v) || (v >= 0.0d && !double.IsPositiveInfinity(v));
        }

        public static readonly StyledProperty<double> ItemWidthProperty =
            AvaloniaProperty.Register<DockAndWrapPanel, double>(nameof(ItemWidth), double.NaN, validate: IsWidthHeightValid);

        public double ItemWidth
        {
            get => GetValue(ItemWidthProperty);
            set => SetValue(ItemWidthProperty, value);
        }

        public static readonly StyledProperty<double> ItemHeightProperty =
            AvaloniaProperty.Register<DockAndWrapPanel, double>(nameof(ItemHeight), double.NaN, validate: IsWidthHeightValid);

        public double ItemHeight
        {
            get => GetValue(ItemHeightProperty);
            set => SetValue(ItemHeightProperty, value);
        }

        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<DockAndWrapPanel, Orientation>(nameof(Orientation), Orientation.Horizontal);

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        static DockAndWrapPanel()
        {
            AffectsMeasure<DockAndWrapPanel>(OrientationProperty, ItemWidthProperty, ItemHeightProperty);
        }

        private struct UVSize
        {
            internal UVSize(Orientation orientation, double width, double height)
            {
                U = V = 0d;
                _orientation = orientation;
                Width = width;
                Height = height;
            }

            internal UVSize(Orientation orientation)
            {
                U = V = 0d;
                _orientation = orientation;
            }

            internal double U;
            internal double V;
            private Orientation _orientation;

            internal double Width
            {
                get { return (_orientation == Orientation.Horizontal ? U : V); }
                set
                {
                    if (_orientation == Orientation.Horizontal) U = value;
                    else V = value;
                }
            }

            internal double Height
            {
                get { return (_orientation == Orientation.Horizontal ? V : U); }
                set
                {
                    if (_orientation == Orientation.Horizontal) V = value;
                    else U = value;
                }
            }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            UVSize curLineSize = new UVSize(Orientation);
            UVSize panelSize = new UVSize(Orientation);
            UVSize uvConstraint = new UVSize(Orientation, constraint.Width, constraint.Height);
            double itemWidth = ItemWidth;
            double itemHeight = ItemHeight;
            bool itemWidthSet = !double.IsNaN(itemWidth);
            bool itemHeightSet = !double.IsNaN(itemHeight);

            Size childConstraint = new Size(
                (itemWidthSet ? itemWidth : constraint.Width),
                (itemHeightSet ? itemHeight : constraint.Height));

            var children = Children;

            for (int i = 0, count = children.Count; i < count; i++)
            {
                Control child = children[i];
                if (child == null) continue;

                // Flow passes its own constraint to children
                child.Measure(childConstraint);

                // this is the size of the child in UV space
                UVSize sz = new UVSize(
                    Orientation,
                    (itemWidthSet ? itemWidth : child.DesiredSize.Width),
                    (itemHeightSet ? itemHeight : child.DesiredSize.Height));

                if (DoubleUtil.GreaterThan(curLineSize.U + sz.U, uvConstraint.U)) // need to switch to another line
                {
                    panelSize.U = Math.Max(curLineSize.U, panelSize.U);
                    panelSize.V += curLineSize.V;
                    curLineSize = sz;

                    if (DoubleUtil.GreaterThan(sz.U, uvConstraint.U)) // the element is wider then the constraint - give it a separate line
                    {
                        panelSize.U = Math.Max(sz.U, panelSize.U);
                        panelSize.V += sz.V;
                        curLineSize = new UVSize(Orientation);
                    }
                }
                else // continue to accumulate a line
                {
                    curLineSize.U += sz.U;
                    curLineSize.V = Math.Max(sz.V, curLineSize.V);
                }
            }

            // the last line size, if any should be added
            panelSize.U = Math.Max(curLineSize.U, panelSize.U);
            panelSize.V += curLineSize.V;

            // go from UV space to W/H space
            return new Size(panelSize.Width, panelSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int firstInLine = 0;
            double itemWidth = ItemWidth;
            double itemHeight = ItemHeight;
            double accumulatedV = 0;
            double itemU = (Orientation == Orientation.Horizontal ? itemWidth : itemHeight);
            UVSize curLineSize = new UVSize(Orientation);
            UVSize uvFinalSize = new UVSize(Orientation, finalSize.Width, finalSize.Height);
            bool itemWidthSet = !double.IsNaN(itemWidth);
            bool itemHeightSet = !double.IsNaN(itemHeight);
            bool useItemU = (Orientation == Orientation.Horizontal ? itemWidthSet : itemHeightSet);

            var canDock = Orientation == Orientation.Horizontal && itemHeightSet || Orientation == Orientation.Vertical && itemWidthSet;

            var children = Children;

            for (int i = 0, count = children.Count; i < count; i++)
            {
                Control child = children[i];
                if (child == null) continue;

                UVSize sz = new UVSize(
                    Orientation,
                    (itemWidthSet ? itemWidth : child.DesiredSize.Width),
                    (itemHeightSet ? itemHeight : child.DesiredSize.Height));

                if (DoubleUtil.GreaterThan(curLineSize.U + sz.U, uvFinalSize.U)) // need to switch to another line
                {
                    arrangeLine(accumulatedV, curLineSize.V, firstInLine, i, useItemU, itemU);

                    accumulatedV += curLineSize.V;
                    curLineSize = sz;

                    if (DoubleUtil.GreaterThan(sz.U, uvFinalSize.U)) // the element is wider then the constraint - give it a separate line
                    {
                        // switch to next line which only contain one element
                        arrangeLine(accumulatedV, sz.V, i, ++i, useItemU, itemU);

                        accumulatedV += sz.V;
                        curLineSize = new UVSize(Orientation);
                    }

                    firstInLine = i;
                }
                else // continue to accumulate a line
                {
                    curLineSize.U += sz.U;
                    curLineSize.V = Math.Max(sz.V, curLineSize.V);
                }
            }

            // arrange the last line, if any
            if (firstInLine < children.Count)
            {
                if (firstInLine == 0 && canDock)
                {
                    arrangeDockedLine(curLineSize.V, uvFinalSize.U, useItemU, itemU);
                }
                else
                {
                    arrangeLine(accumulatedV, curLineSize.V, firstInLine, children.Count, useItemU, itemU);
                }
            }

            return finalSize;
        }

        private void arrangeLine(double v, double lineV, int start, int end, bool useItemU, double itemU)
        {
            double u = 0;
            bool isHorizontal = (Orientation == Orientation.Horizontal);

            var children = Children;
            for (int i = start; i < end; i++)
            {
                Control child = children[i];
                if (child != null)
                {
                    UVSize childSize = new UVSize(Orientation, child.DesiredSize.Width, child.DesiredSize.Height);
                    double layoutSlotU = (useItemU ? itemU : childSize.U);
                    child.Arrange(new Rect(
                        (isHorizontal ? u : v),
                        (isHorizontal ? v : u),
                        (isHorizontal ? layoutSlotU : lineV),
                        (isHorizontal ? lineV : layoutSlotU)));
                    u += layoutSlotU;
                }
            }
        }

        private void arrangeDockedLine(double lineV, double maxU, bool useItemU, double itemU)
        {
            var children = Children.ToArray();
            bool isHorizontal = (Orientation == Orientation.Horizontal);
            double u = 0;
            double v = 0;

            for (int i = 0; i < children.Length; i++)
            {
                var child = children[i];
                var shouldDock = GetDockToEnd(child);
                if (shouldDock)
                    continue;

                if (child != null)
                {
                    UVSize childSize = new UVSize(Orientation, child.DesiredSize.Width, child.DesiredSize.Height);
                    double layoutSlotU = (useItemU ? itemU : childSize.U);
                    child.Arrange(new Rect(
                        (isHorizontal ? u : v),
                        (isHorizontal ? v : u),
                        (isHorizontal ? layoutSlotU : lineV),
                        (isHorizontal ? lineV : layoutSlotU)));

                    u += layoutSlotU;
                }
            }

            // traverse backwards and add the docked items to the right / bottom side of the panel
            u = maxU;

            for (int i = children.Length - 1; i >= 0; i--)
            {
                var child = children[i];
                var shouldDock = GetDockToEnd(child);
                if (!shouldDock)
                    continue;

                if (child != null)
                {
                    UVSize childSize = new UVSize(Orientation, child.DesiredSize.Width, child.DesiredSize.Height);
                    double layoutSlotU = (useItemU ? itemU : childSize.U);

                    u -= layoutSlotU;

                    child.Arrange(new Rect(
                        (isHorizontal ? u : v),
                        (isHorizontal ? v : u),
                        (isHorizontal ? layoutSlotU : lineV),
                        (isHorizontal ? lineV : layoutSlotU)));
                }
            }
        }

        private static class DoubleUtil
        {
            // Const values come from sdk\inc\crt\float.h
            internal const double DBL_EPSILON = 2.2204460492503131e-016; /* smallest such that 1.0+DBL_EPSILON != 1.0 */

            public static bool AreClose(double value1, double value2)
            {
                // in case they are Infinities (then epsilon check does not work)
                if (value1 == value2) return true;
                // This computes (|value1-value2| / (|value1| + |value2| + 10.0)) < DBL_EPSILON
                double eps = (Math.Abs(value1) + Math.Abs(value2) + 10.0) * DBL_EPSILON;
                double delta = value1 - value2;
                return (-eps < delta) && (eps > delta);
            }

            public static bool GreaterThan(double value1, double value2)
            {
                return (value1 > value2) && !AreClose(value1, value2);
            }
        }
    }
}
