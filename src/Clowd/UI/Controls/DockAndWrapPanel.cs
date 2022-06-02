using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace Clowd.UI.Controls
{
    public class DockAndWrapPanel : Panel
    {
        public static readonly DependencyProperty DockToEndProperty =
            DependencyProperty.RegisterAttached(
                "DockToEnd",
                typeof(bool),
                typeof(DockAndWrapPanel),
                new FrameworkPropertyMetadata(false));

        public static bool GetDockToEnd(DependencyObject d)
        {
            return (bool)d.GetValue(DockToEndProperty);
        }

        public static void SetDockToEnd(DependencyObject d, bool value)
        {
            d.SetValue(DockToEndProperty, value);
        }

        public DockAndWrapPanel() : base()
        {
            _orientation = Orientation.Horizontal;
        }

        private static bool IsWidthHeightValid(object value)
        {
            double v = (double)value;
            return (DoubleUtil.IsNaN(v)) || (v >= 0.0d && !Double.IsPositiveInfinity(v));
        }

        public static readonly DependencyProperty ItemWidthProperty =
            DependencyProperty.Register(
                "ItemWidth",
                typeof(double),
                typeof(DockAndWrapPanel),
                new FrameworkPropertyMetadata(
                    Double.NaN,
                    FrameworkPropertyMetadataOptions.AffectsMeasure),
                new ValidateValueCallback(IsWidthHeightValid));

        [TypeConverter(typeof(LengthConverter))]
        public double ItemWidth
        {
            get { return (double)GetValue(ItemWidthProperty); }
            set { SetValue(ItemWidthProperty, value); }
        }


        public static readonly DependencyProperty ItemHeightProperty =
            DependencyProperty.Register(
                "ItemHeight",
                typeof(double),
                typeof(DockAndWrapPanel),
                new FrameworkPropertyMetadata(
                    Double.NaN,
                    FrameworkPropertyMetadataOptions.AffectsMeasure),
                new ValidateValueCallback(IsWidthHeightValid));

        [TypeConverter(typeof(LengthConverter))]
        public double ItemHeight
        {
            get { return (double)GetValue(ItemHeightProperty); }
            set { SetValue(ItemHeightProperty, value); }
        }

        public static readonly DependencyProperty OrientationProperty =
            StackPanel.OrientationProperty.AddOwner(
                typeof(DockAndWrapPanel),
                new FrameworkPropertyMetadata(
                    Orientation.Horizontal,
                    FrameworkPropertyMetadataOptions.AffectsMeasure,
                    new PropertyChangedCallback(OnOrientationChanged)));

        public Orientation Orientation
        {
            get { return _orientation; }
            set { SetValue(OrientationProperty, value); }
        }

        private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            DockAndWrapPanel p = (DockAndWrapPanel)d;
            p._orientation = (Orientation)e.NewValue;
        }

        private Orientation _orientation;

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
            bool itemWidthSet = !DoubleUtil.IsNaN(itemWidth);
            bool itemHeightSet = !DoubleUtil.IsNaN(itemHeight);

            Size childConstraint = new Size(
                (itemWidthSet ? itemWidth : constraint.Width),
                (itemHeightSet ? itemHeight : constraint.Height));

            UIElementCollection children = InternalChildren;

            for (int i = 0, count = children.Count; i < count; i++)
            {
                UIElement child = children[i] as UIElement;
                if (child == null) continue;

                //Flow passes its own constrint to children
                child.Measure(childConstraint);

                //this is the size of the child in UV space
                UVSize sz = new UVSize(
                    Orientation,
                    (itemWidthSet ? itemWidth : child.DesiredSize.Width),
                    (itemHeightSet ? itemHeight : child.DesiredSize.Height));

                if (DoubleUtil.GreaterThan(curLineSize.U + sz.U, uvConstraint.U)) //need to switch to another line
                {
                    panelSize.U = Math.Max(curLineSize.U, panelSize.U);
                    panelSize.V += curLineSize.V;
                    curLineSize = sz;

                    if (DoubleUtil.GreaterThan(sz.U, uvConstraint.U)) //the element is wider then the constrint - give it a separate line                    
                    {
                        panelSize.U = Math.Max(sz.U, panelSize.U);
                        panelSize.V += sz.V;
                        curLineSize = new UVSize(Orientation);
                    }
                }
                else //continue to accumulate a line
                {
                    curLineSize.U += sz.U;
                    curLineSize.V = Math.Max(sz.V, curLineSize.V);
                }
            }

            //the last line size, if any should be added
            panelSize.U = Math.Max(curLineSize.U, panelSize.U);
            panelSize.V += curLineSize.V;

            //go from UV space to W/H space
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
            bool itemWidthSet = !DoubleUtil.IsNaN(itemWidth);
            bool itemHeightSet = !DoubleUtil.IsNaN(itemHeight);
            bool useItemU = (Orientation == Orientation.Horizontal ? itemWidthSet : itemHeightSet);

            var canDock = Orientation == Orientation.Horizontal && itemHeightSet || Orientation == Orientation.Vertical && itemWidthSet;

            UIElementCollection children = InternalChildren;

            for (int i = 0, count = children.Count; i < count; i++)
            {
                UIElement child = children[i] as UIElement;
                if (child == null) continue;

                UVSize sz = new UVSize(
                    Orientation,
                    (itemWidthSet ? itemWidth : child.DesiredSize.Width),
                    (itemHeightSet ? itemHeight : child.DesiredSize.Height));

                if (DoubleUtil.GreaterThan(curLineSize.U + sz.U, uvFinalSize.U)) //need to switch to another line
                {
                    arrangeLine(accumulatedV, curLineSize.V, firstInLine, i, useItemU, itemU);

                    accumulatedV += curLineSize.V;
                    curLineSize = sz;

                    if (DoubleUtil.GreaterThan(sz.U, uvFinalSize.U)) //the element is wider then the constraint - give it a separate line                    
                    {
                        //switch to next line which only contain one element
                        arrangeLine(accumulatedV, sz.V, i, ++i, useItemU, itemU);

                        accumulatedV += sz.V;
                        curLineSize = new UVSize(Orientation);
                    }

                    firstInLine = i;
                }
                else //continue to accumulate a line
                {
                    curLineSize.U += sz.U;
                    curLineSize.V = Math.Max(sz.V, curLineSize.V);
                }
            }

            //arrange the last line, if any
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

            UIElementCollection children = InternalChildren;
            for (int i = start; i < end; i++)
            {
                UIElement child = children[i] as UIElement;
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
            var children = InternalChildren.Cast<UIElement>().ToArray();
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
            internal const float FLT_MIN = 1.175494351e-38F; /* Number close to zero, where float.MinValue is -float.MaxValue */

            public static bool AreClose(double value1, double value2)
            {
                //in case they are Infinities (then epsilon check does not work)
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

            public static bool LessThanOrClose(double value1, double value2)
            {
                return (value1 < value2) || AreClose(value1, value2);
            }

            public static bool GreaterThanOrClose(double value1, double value2)
            {
                return (value1 > value2) || AreClose(value1, value2);
            }

#if !PBTCOMPILER

            [StructLayout(LayoutKind.Explicit)]
            private struct NanUnion
            {
                [FieldOffset(0)] internal double DoubleValue;
                [FieldOffset(0)] internal UInt64 UintValue;
            }

            // The standard CLR double.IsNaN() function is approximately 100 times slower than our own wrapper,
            // so please make sure to use DoubleUtil.IsNaN() in performance sensitive code.
            // PS item that tracks the CLR improvement is DevDiv Schedule : 26916.
            // IEEE 754 : If the argument is any value in the range 0x7ff0000000000001L through 0x7fffffffffffffffL 
            // or in the range 0xfff0000000000001L through 0xffffffffffffffffL, the result will be NaN.         
            public static bool IsNaN(double value)
            {
                NanUnion t = new NanUnion();
                t.DoubleValue = value;

                UInt64 exp = t.UintValue & 0xfff0000000000000;
                UInt64 man = t.UintValue & 0x000fffffffffffff;

                return (exp == 0x7ff0000000000000 || exp == 0xfff0000000000000) && (man != 0);
            }
#endif
        }
    }
}
