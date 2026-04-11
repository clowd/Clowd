using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Clowd.Ui.Controls;

/// <summary>
/// Avalonia port of the legacy WPF <c>DockAndWrapPanel</c> from
/// <c>Clowd.UI.Controls</c>. Behaves like a horizontal/vertical
/// <see cref="WrapPanel"/> with two extras:
///
/// <list type="bullet">
/// <item>Children may carry the attached <see cref="DockToEndProperty"/>
/// flag. When the panel's content fits on a single line, those children
/// are pulled to the trailing edge of that line (right side for
/// horizontal orientation), while non-flagged children flow normally
/// from the leading edge.</item>
/// <item>If the content overflows onto multiple lines, the dock-to-end
/// behaviour is suppressed and every child wraps left-to-right just
/// like a regular <see cref="WrapPanel"/>.</item>
/// </list>
///
/// Honors <see cref="ItemWidth"/> / <see cref="ItemHeight"/> the same way
/// the WPF version does — when set, every child gets a uniform slot
/// regardless of its desired size.
/// </summary>
public class DockAndWrapPanel : Panel
{
    public static readonly AttachedProperty<bool> DockToEndProperty =
        AvaloniaProperty.RegisterAttached<DockAndWrapPanel, Control, bool>("DockToEnd");

    public static bool GetDockToEnd(Control element) => element.GetValue(DockToEndProperty);
    public static void SetDockToEnd(Control element, bool value) => element.SetValue(DockToEndProperty, value);

    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<DockAndWrapPanel, double>(nameof(ItemWidth), double.NaN);

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<DockAndWrapPanel, double>(nameof(ItemHeight), double.NaN);

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
        AffectsMeasure<DockAndWrapPanel>(ItemWidthProperty, ItemHeightProperty, OrientationProperty);
        AffectsParentMeasure<DockAndWrapPanel>(DockToEndProperty);
    }

    /// <summary>
    /// Two-axis size where U is the "primary" axis (Width when horizontal,
    /// Height when vertical) and V is the cross axis. Lets the measure /
    /// arrange code be written once and reused for both orientations.
    /// </summary>
    private struct UVSize
    {
        internal double U;
        internal double V;
        private readonly Orientation _orientation;

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

        internal double Width
        {
            get => _orientation == Orientation.Horizontal ? U : V;
            set { if (_orientation == Orientation.Horizontal) U = value; else V = value; }
        }

        internal double Height
        {
            get => _orientation == Orientation.Horizontal ? V : U;
            set { if (_orientation == Orientation.Horizontal) V = value; else U = value; }
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var orientation = Orientation;
        var curLineSize = new UVSize(orientation);
        var panelSize = new UVSize(orientation);
        var uvConstraint = new UVSize(orientation, constraint.Width, constraint.Height);
        double itemWidth = ItemWidth;
        double itemHeight = ItemHeight;
        bool itemWidthSet = !double.IsNaN(itemWidth);
        bool itemHeightSet = !double.IsNaN(itemHeight);

        var childConstraint = new Size(
            itemWidthSet ? itemWidth : constraint.Width,
            itemHeightSet ? itemHeight : constraint.Height);

        foreach (var child in Children)
        {
            child.Measure(childConstraint);

            var sz = new UVSize(
                orientation,
                itemWidthSet ? itemWidth : child.DesiredSize.Width,
                itemHeightSet ? itemHeight : child.DesiredSize.Height);

            if (GreaterThan(curLineSize.U + sz.U, uvConstraint.U)) // need to wrap
            {
                panelSize.U = Math.Max(curLineSize.U, panelSize.U);
                panelSize.V += curLineSize.V;
                curLineSize = sz;

                if (GreaterThan(sz.U, uvConstraint.U)) // single oversized child gets its own line
                {
                    panelSize.U = Math.Max(sz.U, panelSize.U);
                    panelSize.V += sz.V;
                    curLineSize = new UVSize(orientation);
                }
            }
            else // accumulate on the current line
            {
                curLineSize.U += sz.U;
                curLineSize.V = Math.Max(sz.V, curLineSize.V);
            }
        }

        // Flush the trailing line.
        panelSize.U = Math.Max(curLineSize.U, panelSize.U);
        panelSize.V += curLineSize.V;

        return new Size(panelSize.Width, panelSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var orientation = Orientation;
        int firstInLine = 0;
        double itemWidth = ItemWidth;
        double itemHeight = ItemHeight;
        double accumulatedV = 0;
        double itemU = orientation == Orientation.Horizontal ? itemWidth : itemHeight;
        var curLineSize = new UVSize(orientation);
        var uvFinalSize = new UVSize(orientation, finalSize.Width, finalSize.Height);
        bool itemWidthSet = !double.IsNaN(itemWidth);
        bool itemHeightSet = !double.IsNaN(itemHeight);
        bool useItemU = orientation == Orientation.Horizontal ? itemWidthSet : itemHeightSet;

        // Dock-to-end is only meaningful when we know how thick a row/column is —
        // i.e. ItemHeight is set for horizontal orientation, ItemWidth for vertical.
        bool canDock = (orientation == Orientation.Horizontal && itemHeightSet) ||
                       (orientation == Orientation.Vertical && itemWidthSet);

        var children = Children;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var sz = new UVSize(
                orientation,
                itemWidthSet ? itemWidth : child.DesiredSize.Width,
                itemHeightSet ? itemHeight : child.DesiredSize.Height);

            if (GreaterThan(curLineSize.U + sz.U, uvFinalSize.U)) // wrap
            {
                ArrangeLine(accumulatedV, curLineSize.V, firstInLine, i, useItemU, itemU, orientation);

                accumulatedV += curLineSize.V;
                curLineSize = sz;

                if (GreaterThan(sz.U, uvFinalSize.U))
                {
                    // single child larger than the constraint — give it its own row
                    ArrangeLine(accumulatedV, sz.V, i, ++i, useItemU, itemU, orientation);
                    accumulatedV += sz.V;
                    curLineSize = new UVSize(orientation);
                }

                firstInLine = i;
            }
            else
            {
                curLineSize.U += sz.U;
                curLineSize.V = Math.Max(sz.V, curLineSize.V);
            }
        }

        // Trailing line: only do dock-to-end when everything fits on a single
        // line (firstInLine still at 0). Otherwise we'd shuffle children that
        // are already mid-flow, which mismatches the WPF original.
        if (firstInLine < children.Count)
        {
            if (firstInLine == 0 && canDock)
            {
                ArrangeDockedLine(curLineSize.V, uvFinalSize.U, useItemU, itemU, orientation);
            }
            else
            {
                ArrangeLine(accumulatedV, curLineSize.V, firstInLine, children.Count, useItemU, itemU, orientation);
            }
        }

        return finalSize;
    }

    private void ArrangeLine(double v, double lineV, int start, int end, bool useItemU, double itemU, Orientation orientation)
    {
        double u = 0;
        bool isHorizontal = orientation == Orientation.Horizontal;
        var children = Children;
        for (int i = start; i < end; i++)
        {
            var child = children[i];
            var childSize = new UVSize(orientation, child.DesiredSize.Width, child.DesiredSize.Height);
            double layoutSlotU = useItemU ? itemU : childSize.U;
            child.Arrange(new Rect(
                isHorizontal ? u : v,
                isHorizontal ? v : u,
                isHorizontal ? layoutSlotU : lineV,
                isHorizontal ? lineV : layoutSlotU));
            u += layoutSlotU;
        }
    }

    private void ArrangeDockedLine(double lineV, double maxU, bool useItemU, double itemU, Orientation orientation)
    {
        var children = Children;
        bool isHorizontal = orientation == Orientation.Horizontal;
        double u = 0;
        const double v = 0; // single line, no accumulated v

        // Forward pass: non-docked children flow from the leading edge.
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (GetDockToEnd(child)) continue;

            var childSize = new UVSize(orientation, child.DesiredSize.Width, child.DesiredSize.Height);
            double layoutSlotU = useItemU ? itemU : childSize.U;
            child.Arrange(new Rect(
                isHorizontal ? u : v,
                isHorizontal ? v : u,
                isHorizontal ? layoutSlotU : lineV,
                isHorizontal ? lineV : layoutSlotU));
            u += layoutSlotU;
        }

        // Reverse pass: docked children fill from the trailing edge backwards
        // so that the LAST docked child in document order ends up rightmost
        // (matches the WPF original).
        u = maxU;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var child = children[i];
            if (!GetDockToEnd(child)) continue;

            var childSize = new UVSize(orientation, child.DesiredSize.Width, child.DesiredSize.Height);
            double layoutSlotU = useItemU ? itemU : childSize.U;
            u -= layoutSlotU;
            child.Arrange(new Rect(
                isHorizontal ? u : v,
                isHorizontal ? v : u,
                isHorizontal ? layoutSlotU : lineV,
                isHorizontal ? lineV : layoutSlotU));
        }
    }

    // Tolerant comparison ported from the WPF DoubleUtil helper. Avoids
    // wrapping a row when the running total only exceeds the constraint by
    // floating-point noise.
    private static bool GreaterThan(double a, double b) => a > b && !AreClose(a, b);

    private static bool AreClose(double a, double b)
    {
        if (a == b) return true;
        const double dblEpsilon = 2.2204460492503131e-016;
        double eps = (Math.Abs(a) + Math.Abs(b) + 10.0) * dblEpsilon;
        double delta = a - b;
        return -eps < delta && eps > delta;
    }
}
