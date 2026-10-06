using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// A WrapPanel clone (verbatim WPF UVSize math) with one addition: when all children fit on a
    /// single line and the cross-axis item size is set, children marked with the DockToEnd
    /// attached property are right-aligned (or bottom-aligned when vertical) instead of flowing.
    ///
    /// With <see cref="HideOverflow"/> set it never wraps: the strip stays one line, the docked
    /// children keep the end of it, and the flowing children that do not fit are hidden, with the
    /// child marked <see cref="IsOverflowButtonProperty"/> shown after the last one that does —
    /// the host opens its "more" menu from that button and asks <see cref="IsOverflowed"/> what
    /// belongs in it. <see cref="PinnedChild"/> keeps the last flowing slot whenever its own place
    /// has been cut off.
    /// </summary>
    /// <remarks>
    /// Overflowed children keep their place in Children and are hidden with opacity, hit testing
    /// and tab stops rather than IsVisible: that is decided in arrange, which must not invalidate
    /// layout, and IsVisible belongs to the caller anyway (as in <see cref="CollapsingButtonBar"/>).
    /// </remarks>
    public class DockAndWrapPanel : Panel
    {
        public static readonly StyledProperty<bool> HideOverflowProperty =
            AvaloniaProperty.Register<DockAndWrapPanel, bool>(nameof(HideOverflow));

        /// <summary>Keep the strip to one line and hide what does not fit, instead of wrapping.</summary>
        public bool HideOverflow
        {
            get => GetValue(HideOverflowProperty);
            set => SetValue(HideOverflowProperty, value);
        }

        public static readonly StyledProperty<Control> PinnedChildProperty =
            AvaloniaProperty.Register<DockAndWrapPanel, Control>(nameof(PinnedChild));

        /// <summary>A flowing child that, under <see cref="HideOverflow"/>, takes the last visible
        /// slot when it would otherwise be hidden. Ignored when it is not one of the children, or
        /// when its own place in the order is visible anyway.</summary>
        public Control PinnedChild
        {
            get => GetValue(PinnedChildProperty);
            set => SetValue(PinnedChildProperty, value);
        }

        public static readonly AttachedProperty<bool> IsOverflowButtonProperty =
            AvaloniaProperty.RegisterAttached<DockAndWrapPanel, Control, bool>("IsOverflowButton", false);

        /// <summary>Marks the child shown only while <see cref="HideOverflow"/> is hiding
        /// something. It is never part of the flow, so where it sits in Children does not matter.</summary>
        public static bool GetIsOverflowButton(Control c)
        {
            return c.GetValue(IsOverflowButtonProperty);
        }

        public static void SetIsOverflowButton(Control c, bool value)
        {
            c.SetValue(IsOverflowButtonProperty, value);
        }

        /// <summary>The children the last arrange hid, overflow button included.</summary>
        private HashSet<Control> _hidden = new HashSet<Control>();

        /// <summary>Whether <paramref name="child"/> is a flowing child the last arrange had to
        /// hide for want of room — one that belongs in the host's "more" menu.</summary>
        public bool IsOverflowed(Control child)
        {
            return child != null && _hidden.Contains(child) && !GetIsOverflowButton(child);
        }

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
            AffectsMeasure<DockAndWrapPanel>(OrientationProperty, ItemWidthProperty, ItemHeightProperty, HideOverflowProperty);
            AffectsArrange<DockAndWrapPanel>(PinnedChildProperty);
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
            if (HideOverflow)
                return MeasureSingleLine(constraint);

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

                // only ever shown by HideOverflow
                if (GetIsOverflowButton(child)) continue;

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
            if (HideOverflow)
                return ArrangeSingleLine(finalSize);

            // wrapping hides nothing but the overflow button
            var hidden = new HashSet<Control>();
            foreach (var child in Children)
            {
                if (child != null && GetIsOverflowButton(child))
                {
                    hidden.Add(child);
                    child.Arrange(default);
                }
            }
            ApplyHidden(hidden);

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
                if (child == null || GetIsOverflowButton(child)) continue;

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
                if (child != null && !GetIsOverflowButton(child))
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
                if (shouldDock || GetIsOverflowButton(child))
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
                if (!shouldDock || GetIsOverflowButton(child))
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

        /// <summary>A child's slot in UV space: the item size where one is set, else its own.</summary>
        private UVSize SlotSize(Control child)
        {
            return new UVSize(
                Orientation,
                double.IsNaN(ItemWidth) ? child.DesiredSize.Width : ItemWidth,
                double.IsNaN(ItemHeight) ? child.DesiredSize.Height : ItemHeight);
        }

        /// <summary>The <see cref="HideOverflow"/> measure: one line, as long as everything on it
        /// or as long as the constraint allows, whichever is shorter.</summary>
        private Size MeasureSingleLine(Size constraint)
        {
            Size childConstraint = new Size(
                double.IsNaN(ItemWidth) ? constraint.Width : ItemWidth,
                double.IsNaN(ItemHeight) ? constraint.Height : ItemHeight);

            UVSize line = new UVSize(Orientation);
            UVSize uvConstraint = new UVSize(Orientation, constraint.Width, constraint.Height);

            foreach (var child in Children)
            {
                if (child == null) continue;

                child.Measure(childConstraint);

                var sz = SlotSize(child);
                line.V = Math.Max(line.V, sz.V);

                // the overflow button only appears once the line is already full
                if (!GetIsOverflowButton(child))
                    line.U += sz.U;
            }

            line.U = Math.Min(line.U, uvConstraint.U);
            return new Size(line.Width, line.Height);
        }

        /// <summary>The <see cref="HideOverflow"/> arrange: the docked children at the end, as many
        /// flowing children from the start as fit before them, the pinned child in the last slot if
        /// it was cut off, then the overflow button. Everything else is hidden.</summary>
        private Size ArrangeSingleLine(Size finalSize)
        {
            UVSize uvFinalSize = new UVSize(Orientation, finalSize.Width, finalSize.Height);

            var flow = new List<Control>();
            var docked = new List<Control>();
            Control more = null;
            double lineV = 0, flowU = 0, dockedU = 0;

            foreach (var child in Children)
            {
                if (child == null) continue;

                var sz = SlotSize(child);
                lineV = Math.Max(lineV, sz.V);

                if (GetIsOverflowButton(child))
                {
                    more ??= child;
                }
                else if (GetDockToEnd(child))
                {
                    docked.Add(child);
                    dockedU += sz.U;
                }
                else
                {
                    flow.Add(child);
                    flowU += sz.U;
                }
            }

            var room = uvFinalSize.U - dockedU;
            var shown = flow;

            if (DoubleUtil.GreaterThan(flowU, room))
            {
                if (more != null)
                    room -= SlotSize(more).U;

                shown = new List<Control>();
                double used = 0;
                foreach (var child in flow)
                {
                    var u = SlotSize(child).U;
                    if (DoubleUtil.GreaterThan(used + u, room))
                        break;

                    shown.Add(child);
                    used += u;
                }

                // the pinned child takes the last slot, giving up as many of the ones before it as
                // it needs to fit; the rest of the order is untouched
                var pinned = PinnedChild;
                if (pinned != null && flow.Contains(pinned) && !shown.Contains(pinned))
                {
                    var pinnedU = SlotSize(pinned).U;
                    while (shown.Count > 0 && DoubleUtil.GreaterThan(used + pinnedU, room))
                    {
                        used -= SlotSize(shown[shown.Count - 1]).U;
                        shown.RemoveAt(shown.Count - 1);
                    }

                    if (!DoubleUtil.GreaterThan(used + pinnedU, room))
                        shown.Add(pinned);
                }
            }
            else
            {
                more = null; // nothing is cut off, so there is nothing for it to open
            }

            var hidden = new HashSet<Control>();
            foreach (var child in Children)
            {
                if (child == null || child == more || docked.Contains(child) || shown.Contains(child))
                    continue;

                hidden.Add(child);
                child.Arrange(default);
            }

            double pos = 0;
            foreach (var child in shown)
                pos = ArrangeSlot(child, pos, lineV);
            if (more != null)
                ArrangeSlot(more, pos, lineV);

            pos = uvFinalSize.U;
            for (int i = docked.Count - 1; i >= 0; i--)
            {
                pos -= SlotSize(docked[i]).U;
                ArrangeSlot(docked[i], pos, lineV);
            }

            ApplyHidden(hidden);
            return finalSize;
        }

        /// <summary>Arranges one child of the single line at <paramref name="u"/> and returns where
        /// the next one starts.</summary>
        private double ArrangeSlot(Control child, double u, double lineV)
        {
            var slotU = SlotSize(child).U;
            bool isHorizontal = (Orientation == Orientation.Horizontal);
            child.Arrange(new Rect(
                (isHorizontal ? u : 0),
                (isHorizontal ? 0 : u),
                (isHorizontal ? slotU : lineV),
                (isHorizontal ? lineV : slotU)));
            return u + slotU;
        }

        /// <summary>Hides the children in <paramref name="hidden"/> and gives back the ones the
        /// previous arrange hid that are not in it — including any that have since left the panel,
        /// so a child re-added later does not come back invisible. Touches nothing layout reads,
        /// since this runs from arrange.</summary>
        private void ApplyHidden(HashSet<Control> hidden)
        {
            foreach (var child in _hidden)
            {
                if (hidden.Contains(child))
                    continue;

                child.ClearValue(OpacityProperty);
                child.ClearValue(IsHitTestVisibleProperty);
                child.ClearValue(KeyboardNavigation.IsTabStopProperty);
            }

            foreach (var child in hidden)
            {
                if (_hidden.Contains(child))
                    continue;

                child.Opacity = 0;
                child.IsHitTestVisible = false;
                KeyboardNavigation.SetIsTabStop(child, false);
            }

            _hidden = hidden;
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
