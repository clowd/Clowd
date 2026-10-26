using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// A right-aligned strip of icon buttons that collapses into a single ⋮ button when the space it
    /// is given cannot hold them all. The ⋮ button opens a menu listing the same actions: each item
    /// takes its label from the button's automation name (falling back to its ToolTip.Tip), clones
    /// the button's icon, tracks its IsVisible/IsEnabled, and on click re-raises Click on the button
    /// it came from — so a caller declares its buttons once and the overflow menu follows.
    /// </summary>
    /// <remarks>
    /// The collapse decision is made in arrange rather than measure, because the usual host is a
    /// Grid Auto column, and a Grid measures those with an infinite width — the width the bar
    /// actually gets is only known once the row has been arranged. A finite measure constraint is
    /// honored too, for hosts that do pass one.
    ///
    /// Collapsed buttons keep their place in the tree (they are the overflow menu's source of truth)
    /// and are hidden with opacity instead of IsVisible: IsVisible belongs to the caller, which
    /// typically binds it to the row's data.
    ///
    /// Note that the ⋮ button is itself Children[0] — the bar's own children are declared after it —
    /// so index into Children by identity, not position.
    /// </remarks>
    public class CollapsingButtonBar : Panel
    {
        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<CollapsingButtonBar, double>(nameof(Spacing), 2d);

        /// <summary>Gap between the buttons when they are shown side by side.</summary>
        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        public static readonly StyledProperty<string> ButtonClassesProperty =
            AvaloniaProperty.Register<CollapsingButtonBar, string>(nameof(ButtonClasses));

        /// <summary>Space-separated style classes to put on the ⋮ button, so it can pick up the same
        /// chrome as the buttons it replaces (which the caller styles by class).</summary>
        public string ButtonClasses
        {
            get => GetValue(ButtonClassesProperty);
            set => SetValue(ButtonClassesProperty, value);
        }

        public static readonly StyledProperty<double> ReservedWidthProperty =
            AvaloniaProperty.Register<CollapsingButtonBar, double>(nameof(ReservedWidth));

        /// <summary>Width to leave for whatever shares the row with the bar: the strip collapses as
        /// soon as it cannot fit in the space its host offers <em>minus</em> this. Only meaningful with
        /// a host that measures the bar against a real width (a DockPanel does; a Grid Auto column
        /// does not — see the type's remarks).</summary>
        public double ReservedWidth
        {
            get => GetValue(ReservedWidthProperty);
            set => SetValue(ReservedWidthProperty, value);
        }

        public static readonly StyledProperty<string> OverflowLabelProperty =
            AvaloniaProperty.Register<CollapsingButtonBar, string>(nameof(OverflowLabel), "More actions");

        /// <summary>Tooltip / automation name of the ⋮ button.</summary>
        public string OverflowLabel
        {
            get => GetValue(OverflowLabelProperty);
            set => SetValue(OverflowLabelProperty, value);
        }

        static CollapsingButtonBar()
        {
            AffectsMeasure<CollapsingButtonBar>(SpacingProperty, ReservedWidthProperty);
        }

        private readonly Button _overflow;
        private readonly MenuFlyout _flyout;
        private bool _menuDirty = true;

        public CollapsingButtonBar()
        {
            _flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

            _overflow = new Button
            {
                Content = BuildOverflowGlyph(),
                Flyout = _flyout,
                VerticalAlignment = VerticalAlignment.Center,
                // ButtonClasses normally sizes this (the row-action classes state an explicit
                // Width/Height); the floor is so a caller that sets none still gets a target that can
                // be seen and clicked rather than a zero-sized one.
                MinWidth = 24,
                MinHeight = 24,
            };

            ApplyOverflowLabel();

            // the strip's own children are declared by the caller in XAML and land after this one;
            // arrange places every child explicitly, so the order in Children does not matter.
            Children.Add(_overflow);
            Children.CollectionChanged += OnChildrenChanged;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ButtonClassesProperty)
            {
                _overflow.Classes.Clear();
                var classes = change.GetNewValue<string>();
                if (!String.IsNullOrWhiteSpace(classes))
                    _overflow.Classes.AddRange(classes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            else if (change.Property == OverflowLabelProperty)
            {
                ApplyOverflowLabel();
            }
        }

        private void ApplyOverflowLabel()
        {
            var label = OverflowLabel;
            ToolTip.SetTip(_overflow, label);
            AutomationProperties.SetName(_overflow, label);
        }

        private void OnChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            _menuDirty = true;
        }

        private Control BuildOverflowGlyph()
        {
            // no Stretch (so: None) and the canvas size stated, exactly as the row icons this stands
            // beside do it. The dots only span 4px of the 24px canvas, so stretching them would scale
            // that sliver to the full width and leave the glyph jammed against the left edge instead
            // of centered where the canvas puts it.
            var path = new Path
            {
                Width = 24,
                Height = 24,
                Data = FindGeometry("IconDotsVertical"),
            };

            // matches the row-action icons the bar stands in for, and follows the theme.
            path.Bind(Shape.FillProperty, this.GetResourceObservable("SemiColorText1"));

            return new Viewbox { Child = path };
        }

        private static Geometry FindGeometry(string key)
        {
            var app = Application.Current;
            if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is Geometry geometry)
                return geometry;

            return null;
        }

        /// <summary>The caller's buttons, in declaration order.</summary>
        private IEnumerable<Control> Actions()
        {
            foreach (var child in Children)
            {
                if (!ReferenceEquals(child, _overflow))
                    yield return child;
            }
        }

        /// <summary>Width the whole strip needs, and how many of its buttons are showing.</summary>
        private (double Width, int Count) MeasureStrip()
        {
            double width = 0;
            var count = 0;

            foreach (var child in Actions())
            {
                if (!child.IsVisible)
                    continue;

                width += child.DesiredSize.Width;
                count++;
            }

            if (count > 1)
                width += Spacing * (count - 1);

            return (width, count);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _overflow.Measure(Size.Infinity);

            double height = 0;
            foreach (var child in Actions())
            {
                if (!child.IsVisible)
                    continue;

                child.Measure(Size.Infinity);
                height = Math.Max(height, child.DesiredSize.Height);
            }

            var (width, count) = MeasureStrip();

            // no action applies to this row at all: take up no space, not even the ⋮ button's.
            if (count == 0)
                return default;

            height = Math.Max(height, _overflow.DesiredSize.Height);

            if (_menuDirty)
                RebuildMenu();

            // a host that passes a real constraint gets the answer now, and the strip gives up the
            // width it was not going to use; a Grid Auto column measures with infinity, so there the
            // decision falls to arrange instead.
            var fits = width <= Math.Max(0, availableSize.Width - ReservedWidth);
            return new Size(fits ? width : _overflow.DesiredSize.Width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (width, count) = MeasureStrip();

            // half a pixel of slack: the width a Grid hands back is the one it measured, and
            // rounding it against itself must not collapse a strip that actually fits.
            var collapsed = count > 0 && width > finalSize.Width + 0.5;

            SetShown(_overflow, collapsed);
            if (collapsed)
            {
                var w = Math.Min(_overflow.DesiredSize.Width, finalSize.Width);
                _overflow.Arrange(new Rect(finalSize.Width - w, 0, w, finalSize.Height));
            }
            else
            {
                _overflow.Arrange(default);
            }

            // the strip hugs the right edge of whatever slot it was given.
            var x = Math.Max(0, finalSize.Width - width);

            foreach (var child in Actions())
            {
                if (!child.IsVisible)
                    continue;

                SetShown(child, !collapsed);

                if (collapsed)
                {
                    child.Arrange(default);
                    continue;
                }

                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
                x += child.DesiredSize.Width + Spacing;
            }

            return finalSize;
        }

        /// <summary>Hides a child without touching IsVisible (see the type's remarks) and without
        /// invalidating layout — this runs from arrange.</summary>
        private static void SetShown(Control child, bool shown)
        {
            child.Opacity = shown ? 1 : 0;
            child.IsHitTestVisible = shown;
        }

        private void RebuildMenu()
        {
            _menuDirty = false;

            var items = new List<Control>();
            foreach (var action in Actions())
            {
                var item = new MenuItem
                {
                    Header = LabelOf(action),
                    Icon = CloneIcon(action),
                };

                // the button stays the source of truth for whether the action applies right now.
                item.Bind(MenuItem.IsVisibleProperty, action.GetObservable(IsVisibleProperty));
                item.Bind(MenuItem.IsEnabledProperty, action.GetObservable(IsEnabledProperty));

                var source = action;
                item.Click += (_, _) => source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                items.Add(item);
            }

            _flyout.ItemsSource = items;
        }

        /// <summary>The menu item's text. The automation name comes first because it is always the
        /// short action label; a tooltip is free to be a sentence explaining why the button is
        /// disabled, which is no kind of menu item.</summary>
        private static string LabelOf(Control action)
        {
            if (AutomationProperties.GetName(action) is { Length: > 0 } name)
                return name;

            return ToolTip.GetTip(action) as string ?? "";
        }

        /// <summary>A fresh copy of the button's glyph for its menu item — a Path can only live in
        /// one place in the tree, so the geometry is re-hosted rather than moved.</summary>
        private static Control CloneIcon(Control action)
        {
            var source = FindPath(action);
            if (source?.Data == null)
                return null;

            return new Viewbox
            {
                Width = 16,
                Height = 16,
                Child = new Path
                {
                    Data = source.Data,
                    Fill = source.Fill,
                    Width = source.Width,
                    Height = source.Height,
                    Stretch = source.Stretch,
                },
            };
        }

        private static Path FindPath(Control action)
        {
            return action switch
            {
                Button { Content: Path direct } => direct,
                Button { Content: Viewbox { Child: Path boxed } } => boxed,
                _ => (action as ContentControl)?.Content as Path,
            };
        }
    }
}
