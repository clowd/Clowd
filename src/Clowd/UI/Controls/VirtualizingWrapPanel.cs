// largely from https://github.com/sbaeumlisberger/VirtualizingWrapPanel (MIT License)
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// A implementation of a wrap panel that supports virtualization and can be used in horizontal and vertical orientation.
    /// <p class="note">In order to work properly all items must have the same size.</p>
    /// </summary>
    public class VirtualizingWrapPanel : VirtualizingPanelBase
    {
        /// <summary>
        /// Gets or sets the spacing mode used when arranging the items. The default value is <see cref="SpacingMode.Uniform"/>.
        /// </summary>
        public SpacingMode SpacingMode { get => (SpacingMode)GetValue(SpacingModeProperty); set => SetValue(SpacingModeProperty, value); }
        public static readonly DependencyProperty SpacingModeProperty = DependencyProperty.Register(nameof(SpacingMode), typeof(SpacingMode), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(SpacingMode.Uniform, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>
        /// Gets or sets a value that specifies the orientation in which items are arranged. The default value is <see cref="Orientation.Vertical"/>.
        /// </summary>
        public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
        public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure, (obj, args) => ((VirtualizingWrapPanel)obj).Orientation_Changed()));

        /// <summary>
        /// Gets or sets a value that specifies the size of the items. The default value is <see cref="Size.Empty"/>. 
        /// If the value is <see cref="Size.Empty"/> the size of the items gots measured by the first realized item.
        /// </summary>
        public Size ItemSize { get => (Size)GetValue(ItemSizeProperty); set => SetValue(ItemSizeProperty, value); }
        public static readonly DependencyProperty ItemSizeProperty = DependencyProperty.Register(nameof(ItemSize), typeof(Size), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(Size.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>
        /// Gets or sets a value that specifies if the items get stretched to fill up remaining space. The default value is false.
        /// </summary>
        /// <remarks>
        /// The MaxWidth and MaxHeight properties of the ItemContainerStyle can be used to limit the stretching. 
        /// In this case the use of the remaining space will be determined by the SpacingMode property. 
        /// </remarks>
        public bool StretchItems { get => (bool)GetValue(StretchItemsProperty); set => SetValue(StretchItemsProperty, value); }
        public static readonly DependencyProperty StretchItemsProperty = DependencyProperty.Register(nameof(StretchItems), typeof(bool), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

        /// <summary>
        /// If true, the items will take up the full available width, meaning this WrapPanel will function similarly to a StackPanel.
        /// </summary>
        public bool StretchItemsToWidth { get => (bool)GetValue(StretchItemsToWidthProperty); set => SetValue(StretchItemsToWidthProperty, value); }
        public static readonly DependencyProperty StretchItemsToWidthProperty = DependencyProperty.Register(nameof(StretchItemsToWidth), typeof(bool), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
        
        protected Size childSize;

        protected int rowCount;

        protected int itemsPerRowCount;

        private void Orientation_Changed()
        {
            MouseWheelScrollDirection = Orientation == Orientation.Vertical ? ScrollDirection.Vertical : ScrollDirection.Horizontal;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            UpdateChildSize(availableSize);
            return base.MeasureOverride(availableSize);
        }

        private void UpdateChildSize(Size availableSize)
        {
            if (ItemsOwner is IHierarchicalVirtualizationAndScrollInfo groupItem
                && VirtualizingPanel.GetIsVirtualizingWhenGrouping(ItemsControl))
            {
                if (Orientation == Orientation.Vertical)
                {
                    availableSize.Width = groupItem.Constraints.Viewport.Size.Width;
                    availableSize.Width = Math.Max(availableSize.Width - (Margin.Left + Margin.Right), 0);
                }
                else
                {
                    availableSize.Height = groupItem.Constraints.Viewport.Size.Height;
                    availableSize.Height = Math.Max(availableSize.Height - (Margin.Top + Margin.Bottom), 0);
                }
            }

            if (ItemSize != Size.Empty)
            {
                childSize = ItemSize;
            }
            else if (InternalChildren.Count != 0)
            {
                childSize = InternalChildren[0].DesiredSize;
            }
            else
            {
                childSize = CalculateChildSize(availableSize);
            }

            if (double.IsInfinity(GetWidth(availableSize)))
            {
                itemsPerRowCount = Items.Count;
            }
            else
            {
                itemsPerRowCount = Math.Max(1, (int)Math.Floor(GetWidth(availableSize) / GetWidth(childSize)));
            }

            if (StretchItemsToWidth)
            {
                itemsPerRowCount = 1;
                childSize = new Size(availableSize.Width, childSize.Height);
            }

            rowCount = (int)Math.Ceiling((double)Items.Count / itemsPerRowCount);
        }

        private Size CalculateChildSize(Size availableSize)
        {
            if (Items.Count == 0)
            {
                return new Size(0, 0);
            }

            var startPosition = ItemContainerGenerator.GeneratorPositionFromIndex(0);
            using (ItemContainerGenerator.StartAt(startPosition, GeneratorDirection.Forward, true))
            {
                var child = (UIElement)ItemContainerGenerator.GenerateNext();
                AddInternalChild(child);
                ItemContainerGenerator.PrepareItemContainer(child);
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return child.DesiredSize;
            }
        }

        protected override Size CalculateExtent(Size availableSize)
        {
            double extentWidth = SpacingMode != SpacingMode.None && !double.IsInfinity(GetWidth(availableSize))
                ? GetWidth(availableSize)
                : GetWidth(childSize) * itemsPerRowCount;

            if (ItemsOwner is IHierarchicalVirtualizationAndScrollInfo groupItem)
            {
                if (Orientation == Orientation.Vertical)
                {
                    extentWidth = Math.Max(extentWidth - (Margin.Left + Margin.Right), 0);
                }
                else
                {
                    extentWidth = Math.Max(extentWidth - (Margin.Top + Margin.Bottom), 0);
                }
            }

            double extentHeight = GetHeight(childSize) * rowCount;
            return CreateSize(extentWidth, extentHeight);
        }

        protected void CalculateSpacing(Size finalSize, out double innerSpacing, out double outerSpacing)
        {
            Size childSize = CalculateChildArrangeSize(finalSize);

            double finalWidth = GetWidth(finalSize);

            double totalItemsWidth = Math.Min(GetWidth(childSize) * itemsPerRowCount, finalWidth);
            double unusedWidth = finalWidth - totalItemsWidth;

            switch (SpacingMode)
            {
                case SpacingMode.Uniform:
                    innerSpacing = outerSpacing = unusedWidth / (itemsPerRowCount + 1);
                    break;

                case SpacingMode.BetweenItemsOnly:
                    innerSpacing = unusedWidth / Math.Max(itemsPerRowCount - 1, 1);
                    outerSpacing = 0;
                    break;

                case SpacingMode.StartAndEndOnly:
                    innerSpacing = 0;
                    outerSpacing = unusedWidth / 2;
                    break;

                case SpacingMode.None:
                default:
                    innerSpacing = 0;
                    outerSpacing = 0;
                    break;
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double offsetX = GetX(Offset);
            double offsetY = GetY(Offset);

            /* When the items owner is a group item offset is handled by the parent panel. */
            if (ItemsOwner is IHierarchicalVirtualizationAndScrollInfo groupItem)
            {
                offsetY = 0;
            }

            Size childSize = CalculateChildArrangeSize(finalSize);

            CalculateSpacing(finalSize, out double innerSpacing, out double outerSpacing);

            for (int childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
            {
                UIElement child = InternalChildren[childIndex];

                int itemIndex = GetItemIndexFromChildIndex(childIndex);

                int columnIndex = itemIndex % itemsPerRowCount;
                int rowIndex = itemIndex / itemsPerRowCount;

                double x = outerSpacing + columnIndex * (GetWidth(childSize) + innerSpacing);
                double y = rowIndex * GetHeight(childSize);

                if (GetHeight(finalSize) == 0.0)
                {
                    /* When the parent panel is grouping and a cached group item is not 
                     * in the viewport it has no valid arrangement. That means that the 
                     * height/width is 0. Therefore the items should not be visible so 
                     * that they are not falsely displayed. */
                    child.Arrange(new Rect(0, 0, 0, 0));
                }
                else
                {
                    child.Arrange(CreateRect(x - offsetX, y - offsetY, childSize.Width, childSize.Height));
                }
            }

            return finalSize;
        }

        protected Size CalculateChildArrangeSize(Size finalSize)
        {
            if (StretchItems)
            {
                if (Orientation == Orientation.Vertical)
                {
                    double childMaxWidth = ReadItemContainerStyle(MaxWidthProperty, double.PositiveInfinity);
                    double maxPossibleChildWith = finalSize.Width / itemsPerRowCount;
                    double childWidth = Math.Min(maxPossibleChildWith, childMaxWidth);
                    return new Size(childWidth, childSize.Height);
                }
                else
                {
                    double childMaxHeight = ReadItemContainerStyle(MaxHeightProperty, double.PositiveInfinity);
                    double maxPossibleChildHeight = finalSize.Height / itemsPerRowCount;
                    double childHeight = Math.Min(maxPossibleChildHeight, childMaxHeight);
                    return new Size(childSize.Width, childHeight);
                }
            }
            else
            {
                return childSize;
            }
        }

        private T ReadItemContainerStyle<T>(DependencyProperty property, T fallbackValue) where T : notnull
        {
            var value = ItemsControl.ItemContainerStyle?.Setters.OfType<Setter>()
                .FirstOrDefault(setter => setter.Property == property)?.Value;
            return (T)(value ?? fallbackValue);
        }

        protected override ItemRange UpdateItemRange()
        {
            if (!IsVirtualizing)
            {
                return new ItemRange(0, Items.Count - 1);
            }

            int startIndex;
            int endIndex;

            if (ItemsOwner is IHierarchicalVirtualizationAndScrollInfo groupItem)
            {
                if (!VirtualizingPanel.GetIsVirtualizingWhenGrouping(ItemsControl))
                {
                    return new ItemRange(0, Items.Count - 1);
                }

                var offset = new Point(Offset.X, groupItem.Constraints.Viewport.Location.Y);

                int offsetRowIndex;
                double offsetInPixel;

                int rowCountInViewport;

                if (ScrollUnit == ScrollUnit.Item)
                {
                    offsetRowIndex = GetY(offset) >= 1 ? (int)GetY(offset) - 1 : 0; // ignore header
                    offsetInPixel = offsetRowIndex * GetHeight(childSize);
                }
                else
                {
                    offsetInPixel = Math.Min(Math.Max(GetY(offset) - GetHeight(groupItem.HeaderDesiredSizes.PixelSize), 0), GetHeight(Extent));
                    offsetRowIndex = GetRowIndex(offsetInPixel);
                }

                double viewportHeight = Math.Min(GetHeight(Viewport), Math.Max(GetHeight(Extent) - offsetInPixel, 0));

                rowCountInViewport = (int)Math.Ceiling((offsetInPixel + viewportHeight) / GetHeight(childSize)) - (int)Math.Floor(offsetInPixel / GetHeight(childSize));

                startIndex = offsetRowIndex * itemsPerRowCount;
                endIndex = Math.Min(((offsetRowIndex + rowCountInViewport) * itemsPerRowCount) - 1, Items.Count - 1);

                if (CacheLengthUnit == VirtualizationCacheLengthUnit.Pixel)
                {
                    double cacheBeforeInPixel = Math.Min(CacheLength.CacheBeforeViewport, offsetInPixel);
                    double cacheAfterInPixel = Math.Min(CacheLength.CacheAfterViewport, GetHeight(Extent) - viewportHeight - offsetInPixel);
                    int rowCountInCacheBefore = (int)(cacheBeforeInPixel / GetHeight(childSize));
                    int rowCountInCacheAfter = ((int)Math.Ceiling((offsetInPixel + viewportHeight + cacheAfterInPixel) / GetHeight(childSize))) - (int)Math.Ceiling((offsetInPixel + viewportHeight) / GetHeight(childSize));
                    startIndex = Math.Max(startIndex - rowCountInCacheBefore * itemsPerRowCount, 0);
                    endIndex = Math.Min(endIndex + rowCountInCacheAfter * itemsPerRowCount, Items.Count - 1);
                }
                else if (CacheLengthUnit == VirtualizationCacheLengthUnit.Item)
                {
                    startIndex = Math.Max(startIndex - (int)CacheLength.CacheBeforeViewport, 0);
                    endIndex = Math.Min(endIndex + (int)CacheLength.CacheAfterViewport, Items.Count - 1);
                }
            }
            else
            {
                double viewportSartPos = GetY(Offset);
                double viewportEndPos = GetY(Offset) + GetHeight(Viewport);

                if (CacheLengthUnit == VirtualizationCacheLengthUnit.Pixel)
                {
                    viewportSartPos = Math.Max(viewportSartPos - CacheLength.CacheBeforeViewport, 0);
                    viewportEndPos = Math.Min(viewportEndPos + CacheLength.CacheAfterViewport, GetHeight(Extent));
                }

                int startRowIndex = GetRowIndex(viewportSartPos);
                startIndex = startRowIndex * itemsPerRowCount;

                int endRowIndex = GetRowIndex(viewportEndPos);
                endIndex = Math.Min(endRowIndex * itemsPerRowCount + (itemsPerRowCount - 1), Items.Count - 1);

                if (CacheLengthUnit == VirtualizationCacheLengthUnit.Page)
                {
                    int itemsPerPage = endIndex - startIndex + 1;
                    startIndex = Math.Max(startIndex - (int)CacheLength.CacheBeforeViewport * itemsPerPage, 0);
                    endIndex = Math.Min(endIndex + (int)CacheLength.CacheAfterViewport * itemsPerPage, Items.Count - 1);
                }
                else if (CacheLengthUnit == VirtualizationCacheLengthUnit.Item)
                {
                    startIndex = Math.Max(startIndex - (int)CacheLength.CacheBeforeViewport, 0);
                    endIndex = Math.Min(endIndex + (int)CacheLength.CacheAfterViewport, Items.Count - 1);
                }
            }

            return new ItemRange(startIndex, endIndex);
        }

        private int GetRowIndex(double location)
        {
            int calculatedRowIndex = (int)Math.Floor(location / GetHeight(childSize));
            int maxRowIndex = (int)Math.Ceiling((double)Items.Count / (double)itemsPerRowCount);
            return Math.Max(Math.Min(calculatedRowIndex, maxRowIndex), 0);
        }

        protected override void BringIndexIntoView(int index)
        {
            if (index < 0 || index >= Items.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"The argument {nameof(index)} must be >= 0 and < the number of items.");
            }

            if (itemsPerRowCount == 0)
            {
                throw new InvalidOperationException();
            }

            var offset = (index / itemsPerRowCount) * GetHeight(childSize);

            if (Orientation == Orientation.Horizontal)
            {
                SetHorizontalOffset(offset);
            }
            else
            {
                SetVerticalOffset(offset);
            }
        }

        protected override double GetLineUpScrollAmount()
        {
            return -Math.Min(childSize.Height * ScrollLineDeltaItem, Viewport.Height);
        }

        protected override double GetLineDownScrollAmount()
        {
            return Math.Min(childSize.Height * ScrollLineDeltaItem, Viewport.Height);
        }

        protected override double GetLineLeftScrollAmount()
        {
            return -Math.Min(childSize.Width * ScrollLineDeltaItem, Viewport.Width);
        }

        protected override double GetLineRightScrollAmount()
        {
            return Math.Min(childSize.Width * ScrollLineDeltaItem, Viewport.Width);
        }

        protected override double GetMouseWheelUpScrollAmount()
        {
            return -Math.Min(childSize.Height * MouseWheelDeltaItem, Viewport.Height);
        }

        protected override double GetMouseWheelDownScrollAmount()
        {
            return Math.Min(childSize.Height * MouseWheelDeltaItem, Viewport.Height);
        }

        protected override double GetMouseWheelLeftScrollAmount()
        {
            return -Math.Min(childSize.Width * MouseWheelDeltaItem, Viewport.Width);
        }

        protected override double GetMouseWheelRightScrollAmount()
        {
            return Math.Min(childSize.Width * MouseWheelDeltaItem, Viewport.Width);
        }

        protected override double GetPageUpScrollAmount()
        {
            return -Viewport.Height;
        }

        protected override double GetPageDownScrollAmount()
        {
            return Viewport.Height;
        }

        protected override double GetPageLeftScrollAmount()
        {
            return -Viewport.Width;
        }

        protected override double GetPageRightScrollAmount()
        {
            return Viewport.Width;
        }

        /* orientation aware helper methods */

        protected double GetX(Point point) => Orientation == Orientation.Vertical ? point.X : point.Y;
        protected double GetY(Point point) => Orientation == Orientation.Vertical ? point.Y : point.X;

        protected double GetWidth(Size size) => Orientation == Orientation.Vertical ? size.Width : size.Height;
        protected double GetHeight(Size size) => Orientation == Orientation.Vertical ? size.Height : size.Width;

        protected Size CreateSize(double width, double height) => Orientation == Orientation.Vertical ? new Size(width, height) : new Size(height, width);
        protected Rect CreateRect(double x, double y, double width, double height) => Orientation == Orientation.Vertical ? new Rect(x, y, width, height) : new Rect(y, x, width, height);
    }

    /// <summary>
    /// Base class for panels which are supporting virtualization.
    /// </summary>
    public abstract class VirtualizingPanelBase : VirtualizingPanel, IScrollInfo
    {
        public static readonly DependencyProperty ScrollLineDeltaProperty = DependencyProperty.Register(nameof(ScrollLineDelta), typeof(double), typeof(VirtualizingPanelBase), new FrameworkPropertyMetadata(16.0));
        public static readonly DependencyProperty MouseWheelDeltaProperty = DependencyProperty.Register(nameof(MouseWheelDelta), typeof(double), typeof(VirtualizingPanelBase), new FrameworkPropertyMetadata(48.0));
        public static readonly DependencyProperty ScrollLineDeltaItemProperty = DependencyProperty.Register(nameof(ScrollLineDeltaItem), typeof(int), typeof(VirtualizingPanelBase), new FrameworkPropertyMetadata(1));
        public static readonly DependencyProperty MouseWheelDeltaItemProperty = DependencyProperty.Register(nameof(MouseWheelDeltaItem), typeof(int), typeof(VirtualizingPanelBase), new FrameworkPropertyMetadata(3));

        public ScrollViewer ScrollOwner { get; set; }

        public bool CanVerticallyScroll { get; set; }
        public bool CanHorizontallyScroll { get; set; }

        protected override bool CanHierarchicallyScrollAndVirtualizeCore => true;

        /// <summary>
        /// Scroll line delta for pixel based scrolling. The default value is 16 dp.
        /// </summary>
        public double ScrollLineDelta { get => (double)GetValue(ScrollLineDeltaProperty); set => SetValue(ScrollLineDeltaProperty, value); }

        /// <summary>
        /// Mouse wheel delta for pixel based scrolling. The default value is 48 dp.
        /// </summary>        
        public double MouseWheelDelta { get => (double)GetValue(MouseWheelDeltaProperty); set => SetValue(MouseWheelDeltaProperty, value); }

        /// <summary>
        /// Scroll line delta for item based scrolling. The default value is 1 item.
        /// </summary>
        public double ScrollLineDeltaItem { get => (int)GetValue(ScrollLineDeltaItemProperty); set => SetValue(ScrollLineDeltaItemProperty, value); }

        /// <summary>
        /// Mouse wheel delta for item based scrolling. The default value is 3 items.
        /// </summary> 
        public int MouseWheelDeltaItem { get => (int)GetValue(MouseWheelDeltaItemProperty); set => SetValue(MouseWheelDeltaItemProperty, value); }

        protected ScrollUnit ScrollUnit => GetScrollUnit(ItemsControl);

        /// <summary>
        /// The direction in which the panel scrolls when user turns the mouse wheel.
        /// </summary>
        protected ScrollDirection MouseWheelScrollDirection { get; set; } = ScrollDirection.Vertical;


        protected bool IsVirtualizing => GetIsVirtualizing(ItemsControl);

        protected VirtualizationMode VirtualizationMode => GetVirtualizationMode(ItemsControl);

        /// <summary>
        /// Returns true if the panel is in VirtualizationMode.Recycling, otherwise false.
        /// </summary>
        protected bool IsRecycling => VirtualizationMode == VirtualizationMode.Recycling;

        /// <summary>
        /// The cache length before and after the viewport. 
        /// </summary>
        protected VirtualizationCacheLength CacheLength { get; private set; }

        /// <summary>
        /// The Unit of the cache length. Can be Pixel, Item or Page. 
        /// When the ItemsOwner is a group item it can only be pixel or item.
        /// </summary>
        protected VirtualizationCacheLengthUnit CacheLengthUnit { get; private set; }


        /// <summary>
        /// The ItemsControl (e.g. ListView).
        /// </summary>
        protected ItemsControl ItemsControl => ItemsControl.GetItemsOwner(this);

        /// <summary>
        /// The ItemsControl (e.g. ListView) or if the ItemsControl is grouping a GroupItem.
        /// </summary>
        protected DependencyObject ItemsOwner
        {
            get
            {
                if (_itemsOwner is null)
                {
                    /* Use reflection to access internal method because the public 
                     * GetItemsOwner method does always return the itmes control instead 
                     * of the real items owner for example the group item when grouping */
                    MethodInfo getItemsOwnerInternalMethod = typeof(ItemsControl).GetMethod(
                        "GetItemsOwnerInternal",
                        BindingFlags.Static | BindingFlags.NonPublic,
                        null,
                        new Type[] { typeof(DependencyObject) },
                        null
                    )!;
                    _itemsOwner = (DependencyObject)getItemsOwnerInternalMethod.Invoke(null, new object[] { this })!;
                }

                return _itemsOwner;
            }
        }

        private DependencyObject _itemsOwner;

        protected ReadOnlyCollection<object> Items => ((ItemContainerGenerator)ItemContainerGenerator).Items;

        protected new IRecyclingItemContainerGenerator ItemContainerGenerator
        {
            get
            {
                if (_itemContainerGenerator is null)
                {
                    /* Because of a bug in the framework the ItemContainerGenerator 
                     * is null until InternalChildren accessed at least one time. */
                    var children = InternalChildren;
                    _itemContainerGenerator = (IRecyclingItemContainerGenerator)base.ItemContainerGenerator;
                }

                return _itemContainerGenerator;
            }
        }

        private IRecyclingItemContainerGenerator _itemContainerGenerator;

        public double ExtentWidth => Extent.Width;
        public double ExtentHeight => Extent.Height;
        protected Size Extent { get; private set; } = new Size(0, 0);

        public double HorizontalOffset => Offset.X;
        public double VerticalOffset => Offset.Y;
        protected Size Viewport { get; private set; } = new Size(0, 0);

        public double ViewportWidth => Viewport.Width;
        public double ViewportHeight => Viewport.Height;
        protected Point Offset { get; private set; } = new Point(0, 0);

        /// <summary>
        /// The range of items that a realized in viewport or cache.
        /// </summary>
        protected ItemRange ItemRange { get; set; }

        private Visibility previousVerticalScrollBarVisibility = Visibility.Collapsed;
        private Visibility previousHorizontalScrollBarVisibility = Visibility.Collapsed;

        protected virtual void UpdateScrollInfo(Size availableSize, Size extent)
        {
            bool invalidateScrollInfo = false;

            if (extent != Extent)
            {
                Extent = extent;
                invalidateScrollInfo = true;
            }

            if (availableSize != Viewport)
            {
                Viewport = availableSize;
                invalidateScrollInfo = true;
            }

            if (ViewportHeight != 0 && VerticalOffset != 0 && VerticalOffset + ViewportHeight + 1 >= ExtentHeight)
            {
                Offset = new Point(Offset.X, extent.Height - availableSize.Height);
                invalidateScrollInfo = true;
            }

            if (ViewportWidth != 0 && HorizontalOffset != 0 && HorizontalOffset + ViewportWidth + 1 >= ExtentWidth)
            {
                Offset = new Point(extent.Width - availableSize.Width, Offset.Y);
                invalidateScrollInfo = true;
            }

            if (invalidateScrollInfo)
            {
                ScrollOwner?.InvalidateScrollInfo();
            }
        }

        public virtual Rect MakeVisible(Visual visual, Rect rectangle)
        {
            Point pos = visual.TransformToAncestor(this).Transform(Offset);

            double scrollAmountX = 0;
            double scrollAmountY = 0;

            if (pos.X < Offset.X)
            {
                scrollAmountX = -(Offset.X - pos.X);
            }
            else if ((pos.X + rectangle.Width) > (Offset.X + Viewport.Width))
            {
                double notVisibleX = (pos.X + rectangle.Width) - (Offset.X + Viewport.Width);
                double maxScrollX = pos.X - Offset.X; // keep left of the visual visible
                scrollAmountX = Math.Min(notVisibleX, maxScrollX);
            }

            if (pos.Y < Offset.Y)
            {
                scrollAmountY = -(Offset.Y - pos.Y);
            }
            else if ((pos.Y + rectangle.Height) > (Offset.Y + Viewport.Height))
            {
                double notVisibleY = (pos.Y + rectangle.Height) - (Offset.Y + Viewport.Height);
                double maxScrollY = pos.Y - Offset.Y; // keep top of the visual visible
                scrollAmountY = Math.Min(notVisibleY, maxScrollY);
            }

            SetHorizontalOffset(Offset.X + scrollAmountX);
            SetVerticalOffset(Offset.Y + scrollAmountY);

            double visibleRectWidth = Math.Min(rectangle.Width, Viewport.Width);
            double visibleRectHeight = Math.Min(rectangle.Height, Viewport.Height);

            return new Rect(scrollAmountX, scrollAmountY, visibleRectWidth, visibleRectHeight);
        }

        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Replace:
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                    break;
                case NotifyCollectionChangedAction.Move:
                    RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                    break;
            }
        }

        protected int GetItemIndexFromChildIndex(int childIndex)
        {
            var generatorPosition = GetGeneratorPositionFromChildIndex(childIndex);
            return ItemContainerGenerator.IndexFromGeneratorPosition(generatorPosition);
        }

        protected virtual GeneratorPosition GetGeneratorPositionFromChildIndex(int childIndex)
        {
            return new GeneratorPosition(childIndex, 0);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            /* Sometimes when scrolling the scrollbar gets hidden without any reason. In this case the "IsMeasureValid" 
             * property of the ScrollOwner is false. To prevent a infinite circle the mesasure call is ignored. */
            if (ScrollOwner != null)
            {
                bool verticalScrollBarGotHidden = ScrollOwner.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                                                  && ScrollOwner.ComputedVerticalScrollBarVisibility != Visibility.Visible
                                                  && ScrollOwner.ComputedVerticalScrollBarVisibility != previousVerticalScrollBarVisibility;

                bool horizontalScrollBarGotHidden = ScrollOwner.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto
                                                    && ScrollOwner.ComputedHorizontalScrollBarVisibility != Visibility.Visible
                                                    && ScrollOwner.ComputedHorizontalScrollBarVisibility != previousHorizontalScrollBarVisibility;

                previousVerticalScrollBarVisibility = ScrollOwner.ComputedVerticalScrollBarVisibility;
                previousHorizontalScrollBarVisibility = ScrollOwner.ComputedHorizontalScrollBarVisibility;

                if (!ScrollOwner.IsMeasureValid && verticalScrollBarGotHidden || horizontalScrollBarGotHidden)
                {
                    return availableSize;
                }
            }

            var groupItem = ItemsOwner as IHierarchicalVirtualizationAndScrollInfo;

            Size extent;
            Size desiredSize;

            if (groupItem != null)
            {
                /* If the ItemsOwner is a group item the availableSize is ifinity. 
                 * Therfore the vieport size provided by the group item is used. */
                var viewportSize = groupItem.Constraints.Viewport.Size;
                var headerSize = groupItem.HeaderDesiredSizes.PixelSize;
                double availableWidth = Math.Max(viewportSize.Width - 5, 0); // left margin of 5 dp
                double availableHeight = Math.Max(viewportSize.Height - headerSize.Height, 0);
                availableSize = new Size(availableWidth, availableHeight);

                extent = CalculateExtent(availableSize);

                desiredSize = new Size(extent.Width, extent.Height);

                Extent = extent;
                Offset = groupItem.Constraints.Viewport.Location;
                Viewport = groupItem.Constraints.Viewport.Size;
                CacheLength = groupItem.Constraints.CacheLength;
                CacheLengthUnit = groupItem.Constraints.CacheLengthUnit; // can be Item or Pixel
            }
            else
            {
                extent = CalculateExtent(availableSize);
                double desiredWidth = Math.Min(availableSize.Width, extent.Width);
                double desiredHeight = Math.Min(availableSize.Height, extent.Height);
                desiredSize = new Size(desiredWidth, desiredHeight);

                UpdateScrollInfo(desiredSize, extent);
                CacheLength = GetCacheLength(ItemsOwner);
                CacheLengthUnit = GetCacheLengthUnit(ItemsOwner); // can be Page, Item or Pixel
            }

            ItemRange = UpdateItemRange();

            RealizeItems();
            VirtualizeItems();

            return desiredSize;
        }

        /// <summary>
        /// Realizes visible and cached items.
        /// </summary>
        protected virtual void RealizeItems()
        {
            var startPosition = ItemContainerGenerator.GeneratorPositionFromIndex(ItemRange.StartIndex);

            int childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

            using (ItemContainerGenerator.StartAt(startPosition, GeneratorDirection.Forward, true))
            {
                for (int i = ItemRange.StartIndex; i <= ItemRange.EndIndex; i++, childIndex++)
                {
                    UIElement child = (UIElement)ItemContainerGenerator.GenerateNext(out bool isNewlyRealized);
                    if (isNewlyRealized || /*recycled*/!InternalChildren.Contains(child))
                    {
                        if (childIndex >= InternalChildren.Count)
                        {
                            AddInternalChild(child);
                        }
                        else
                        {
                            InsertInternalChild(childIndex, child);
                        }

                        ItemContainerGenerator.PrepareItemContainer(child);

                        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    }

                    if (child is IHierarchicalVirtualizationAndScrollInfo groupItem)
                    {
                        groupItem.Constraints = new HierarchicalVirtualizationConstraints(
                            new VirtualizationCacheLength(0),
                            VirtualizationCacheLengthUnit.Item,
                            new Rect(0, 0, ViewportWidth, ViewportHeight));
                        child.Measure(new Size(ViewportWidth, ViewportHeight));
                    }
                }
            }
        }

        /// <summary>
        /// Virtualizes (cleanups) no longer visible or cached items.
        /// </summary>
        protected virtual void VirtualizeItems()
        {
            for (int childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
            {
                var generatorPosition = GetGeneratorPositionFromChildIndex(childIndex);

                int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(generatorPosition);

                if (itemIndex != -1 && !ItemRange.Contains(itemIndex))
                {
                    if (VirtualizationMode == VirtualizationMode.Recycling)
                    {
                        ItemContainerGenerator.Recycle(generatorPosition, 1);
                    }
                    else
                    {
                        ItemContainerGenerator.Remove(generatorPosition, 1);
                    }

                    RemoveInternalChildRange(childIndex, 1);
                }
            }
        }

        /// <summary>
        /// Calculates the extent that would be needed to show all items.
        /// </summary>
        protected abstract Size CalculateExtent(Size availableSize);

        /// <summary>
        /// Calculates the item range that is visible in the viewport or cached.
        /// </summary>
        protected abstract ItemRange UpdateItemRange();

        public void SetVerticalOffset(double offset)
        {
            if (offset < 0 || Viewport.Height >= Extent.Height)
            {
                offset = 0;
            }
            else if (offset + Viewport.Height >= Extent.Height)
            {
                offset = Extent.Height - Viewport.Height;
            }

            Offset = new Point(Offset.X, offset);
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();
        }

        public void SetHorizontalOffset(double offset)
        {
            if (offset < 0 || Viewport.Width >= Extent.Width)
            {
                offset = 0;
            }
            else if (offset + Viewport.Width >= Extent.Width)
            {
                offset = Extent.Width - Viewport.Width;
            }

            Offset = new Point(offset, Offset.Y);
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();
        }

        protected void ScrollVertical(double amount)
        {
            SetVerticalOffset(VerticalOffset + amount);
        }

        protected void ScrollHorizontal(double amount)
        {
            SetHorizontalOffset(HorizontalOffset + amount);
        }

        public void LineUp() => ScrollVertical(ScrollUnit == ScrollUnit.Pixel ? -ScrollLineDelta : GetLineUpScrollAmount());
        public void LineDown() => ScrollVertical(ScrollUnit == ScrollUnit.Pixel ? ScrollLineDelta : GetLineDownScrollAmount());
        public void LineLeft() => ScrollHorizontal(ScrollUnit == ScrollUnit.Pixel ? -ScrollLineDelta : GetLineLeftScrollAmount());
        public void LineRight() => ScrollHorizontal(ScrollUnit == ScrollUnit.Pixel ? ScrollLineDelta : GetLineRightScrollAmount());

        public void MouseWheelUp()
        {
            if (MouseWheelScrollDirection == ScrollDirection.Vertical)
            {
                ScrollVertical(ScrollUnit == ScrollUnit.Pixel ? -MouseWheelDelta : GetMouseWheelUpScrollAmount());
            }
            else
            {
                MouseWheelLeft();
            }
        }

        public void MouseWheelDown()
        {
            if (MouseWheelScrollDirection == ScrollDirection.Vertical)
            {
                ScrollVertical(ScrollUnit == ScrollUnit.Pixel ? MouseWheelDelta : GetMouseWheelDownScrollAmount());
            }
            else
            {
                MouseWheelRight();
            }
        }

        public void MouseWheelLeft() => ScrollHorizontal(ScrollUnit == ScrollUnit.Pixel ? -MouseWheelDelta : GetMouseWheelLeftScrollAmount());
        public void MouseWheelRight() => ScrollHorizontal(ScrollUnit == ScrollUnit.Pixel ? MouseWheelDelta : GetMouseWheelRightScrollAmount());

        public void PageUp() => ScrollVertical(ScrollUnit == ScrollUnit.Pixel ? -ViewportHeight : GetPageUpScrollAmount());
        public void PageDown() => ScrollVertical(ScrollUnit == ScrollUnit.Pixel ? ViewportHeight : GetPageDownScrollAmount());
        public void PageLeft() => ScrollHorizontal(ScrollUnit == ScrollUnit.Pixel ? -ViewportHeight : GetPageLeftScrollAmount());
        public void PageRight() => ScrollHorizontal(ScrollUnit == ScrollUnit.Pixel ? ViewportHeight : GetPageRightScrollAmount());

        protected abstract double GetLineUpScrollAmount();
        protected abstract double GetLineDownScrollAmount();
        protected abstract double GetLineLeftScrollAmount();
        protected abstract double GetLineRightScrollAmount();

        protected abstract double GetMouseWheelUpScrollAmount();
        protected abstract double GetMouseWheelDownScrollAmount();
        protected abstract double GetMouseWheelLeftScrollAmount();
        protected abstract double GetMouseWheelRightScrollAmount();

        protected abstract double GetPageUpScrollAmount();
        protected abstract double GetPageDownScrollAmount();
        protected abstract double GetPageLeftScrollAmount();
        protected abstract double GetPageRightScrollAmount();
    }

    public enum ScrollDirection
    {
        Vertical,
        Horizontal
    }
    
    public enum SpacingMode
    {
        /// <summary>
        /// Spacing is disabled and all items will be arranged as closely as possible.
        /// </summary>
        None,
        /// <summary>
        /// The remaining space is evenly distributed between the items on a layout row, as well as the start and end of each row.
        /// </summary>
        Uniform,
        /// <summary>
        /// The remaining space is evenly distributed between the items on a layout row, excluding the start and end of each row.
        /// </summary>
        BetweenItemsOnly,
        /// <summary>
        /// The remaining space is evenly distributed between start and end of each row.
        /// </summary>
        StartAndEndOnly
    }

    public struct ItemRange
    {
        public int StartIndex { get; }
        public int EndIndex { get; }

        public ItemRange(int startIndex, int endIndex) : this()
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
        }

        public bool Contains(int itemIndex)
        {
            return itemIndex >= StartIndex && itemIndex <= EndIndex;
        }
    }
}
