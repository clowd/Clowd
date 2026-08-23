using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// A strip of mutually exclusive segments — one ToggleButton per option, each tagged with the
    /// value it stands for — that collapses into a single dropdown when the space it is given cannot
    /// hold them side by side. The dropdown is labeled with the option currently selected and its
    /// menu offers the same choices, so a caller declares its segments once and the collapsed
    /// presentation follows.
    /// </summary>
    /// <remarks>
    /// Written from <see cref="CollapsingButtonBar"/>'s playbook, and the reasons it gives apply here
    /// too: the collapse decision is taken in arrange as well as measure (a host that measures with
    /// an infinite width — a Grid Auto column — only settles the real width once the row has been
    /// arranged), and collapsed segments keep their place in the tree, because they are what the menu
    /// is generated from. They are hidden with opacity rather than IsVisible, which belongs to the
    /// caller.
    ///
    /// Where this one differs: it is single-select, so the collapsed state has to say *which* option
    /// is active and cannot be that bar's anonymous ⋮ button; and its segments carry text rather than
    /// icons, so it brings its own chrome (Controls/CollapsingSegmentedBar.axaml) instead of taking
    /// style classes from the caller — the usual host is the window's page header, outside the styles
    /// of the page the segments belong to.
    ///
    /// Note that the dropdown is itself Children[0] — the segments are declared after it — so index
    /// into Children by identity, not position.
    /// </remarks>
    public class CollapsingSegmentedBar : Panel
    {
        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<CollapsingSegmentedBar, double>(nameof(Spacing), 1d);

        /// <summary>Gap between the segments when they are shown side by side. A hairline by default:
        /// the segments are drawn as one joined strip, so anything more would break the pill up.</summary>
        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        public static readonly StyledProperty<object> SelectedValueProperty =
            AvaloniaProperty.Register<CollapsingSegmentedBar, object>(
                nameof(SelectedValue), defaultBindingMode: BindingMode.TwoWay);

        /// <summary>The Tag of the selected segment. A value matching no segment selects the first
        /// one instead — the strip always has exactly one option active.</summary>
        public object SelectedValue
        {
            get => GetValue(SelectedValueProperty);
            set => SetValue(SelectedValueProperty, value);
        }

        /// <summary>Raised whenever <see cref="SelectedValue"/> changes, however it was changed.</summary>
        public event EventHandler SelectionChanged;

        static CollapsingSegmentedBar()
        {
            AffectsMeasure<CollapsingSegmentedBar>(SpacingProperty);
        }

        private static readonly Uri _stylesUri = new("avares://Clowd.Ui/Controls/CollapsingSegmentedBar.axaml");

        private readonly Button _dropdown;
        private readonly TextBlock _dropdownLabel;
        private readonly MenuFlyout _flyout;
        private readonly List<(MenuItem Item, ToggleButton Segment)> _menu = new();
        private bool _menuDirty = true;

        public CollapsingSegmentedBar()
        {
            Styles.Add(new StyleInclude(_stylesUri) { Source = _stylesUri });

            _flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

            _dropdownLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

            _dropdown = new Button
            {
                Flyout = _flyout,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { _dropdownLabel, BuildChevron() },
                },
            };

            // the segments are declared by the caller and land after this one; arrange places every
            // child explicitly, so the order in Children does not matter.
            Children.Add(_dropdown);
            Children.CollectionChanged += OnChildrenChanged;
        }

        private Control BuildChevron()
        {
            // 32px canvas (AppStyles icon table) stated explicitly, as the icons elsewhere in the app
            // do it, so the Viewbox scales the whole canvas rather than the glyph's bounding box.
            var path = new Path
            {
                Width = 32,
                Height = 32,
                Data = FindGeometry("IconChevronDown"),
            };

            path.Bind(Shape.FillProperty, this.GetResourceObservable("SemiColorText1"));

            return new Viewbox
            {
                Width = 9,
                Height = 9,
                VerticalAlignment = VerticalAlignment.Center,
                Child = path,
            };
        }

        private static Geometry FindGeometry(string key)
        {
            var app = Application.Current;
            if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is Geometry geometry)
                return geometry;

            return null;
        }

        /// <summary>The caller's segments, in declaration order. The dropdown is a Button, so it
        /// never turns up here.</summary>
        private IEnumerable<ToggleButton> Segments()
        {
            foreach (var child in Children)
            {
                if (child is ToggleButton segment)
                    yield return segment;
            }
        }

        private void OnChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // cheaper to re-hook the (handful of) segments than to track adds and removes: a handler
            // subscribed twice would set the same value twice, so removing first keeps it at one.
            foreach (var segment in Segments())
            {
                segment.Click -= SegmentClicked;
                segment.Click += SegmentClicked;
                segment.PropertyChanged -= SegmentPropertyChanged;
                segment.PropertyChanged += SegmentPropertyChanged;
            }

            _menuDirty = true;
            SyncSegmentPositions();
            EnsureSelection();
        }

        private void SegmentPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            // a segment the caller hides is not part of the strip any more, so the rounded ends may
            // have to move to different segments.
            if (e.Property == IsVisibleProperty)
                SyncSegmentPositions();
        }

        /// <summary>Marks the ends of the strip, which is how the theme knows which corners to round
        /// and which segment draws the left edge of the joined outline. Both classes land on the
        /// same segment when only one of them is showing.</summary>
        private void SyncSegmentPositions()
        {
            ToggleButton previous = null;

            foreach (var segment in Segments())
            {
                if (!segment.IsVisible)
                {
                    segment.Classes.Remove("first");
                    segment.Classes.Remove("last");
                    continue;
                }

                segment.Classes.Set("first", previous == null);
                segment.Classes.Set("last", true);

                previous?.Classes.Set("last", false);
                previous = segment;
            }
        }

        /// <summary>Falls back to the first segment when <see cref="SelectedValue"/> names none of
        /// them — a strip with nothing active reads as broken, and the first segment is the "show
        /// everything" one by convention.</summary>
        private void EnsureSelection()
        {
            ToggleButton first = null;

            foreach (var segment in Segments())
            {
                first ??= segment;
                if (Equals(segment.Tag, SelectedValue))
                {
                    SyncSelection();
                    return;
                }
            }

            if (first != null)
                SetCurrentValue(SelectedValueProperty, first.Tag);
        }

        private void SegmentClicked(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            SetCurrentValue(SelectedValueProperty, (sender as Control)?.Tag);

            // clicking the active segment has already unchecked it by the time we get here, and the
            // property did not change — so re-assert the check state either way.
            SyncSelection();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property != SelectedValueProperty)
                return;

            SyncSelection();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Points the check state, the collapsed label and the menu's tick at the selected
        /// segment.</summary>
        private void SyncSelection()
        {
            foreach (var segment in Segments())
            {
                var selected = Equals(segment.Tag, SelectedValue);

                if (segment.IsChecked != selected)
                    segment.IsChecked = selected;

                if (!selected)
                    continue;

                var label = LabelOf(segment);
                _dropdownLabel.Text = label;
                ToolTip.SetTip(_dropdown, label);
                AutomationProperties.SetName(_dropdown, label);
            }

            foreach (var (item, segment) in _menu)
                item.Icon = Equals(segment.Tag, SelectedValue) ? BuildCheckGlyph() : null;
        }

        private void RebuildMenu()
        {
            _menuDirty = false;
            _menu.Clear();

            var items = new List<Control>();
            foreach (var segment in Segments())
            {
                var item = new MenuItem { Header = LabelOf(segment) };

                // the segment stays the source of truth for whether the option applies right now.
                item.Bind(MenuItem.IsVisibleProperty, segment.GetObservable(IsVisibleProperty));
                item.Bind(MenuItem.IsEnabledProperty, segment.GetObservable(IsEnabledProperty));

                var source = segment;
                item.Click += (_, _) =>
                {
                    SetCurrentValue(SelectedValueProperty, source.Tag);
                    SyncSelection();
                };

                items.Add(item);
                _menu.Add((item, source));
            }

            _flyout.ItemsSource = items;
            SyncSelection();
        }

        /// <summary>Tick shown beside the active option in the collapsed menu. Stretched rather than
        /// drawn on its stated canvas: the checkmark fills its own bounding box, so there is no
        /// padding around it to preserve.</summary>
        private Control BuildCheckGlyph()
        {
            var path = new Path
            {
                Data = FindGeometry("IconCheckmark"),
                Stretch = Stretch.Uniform,
            };

            path.Bind(Shape.FillProperty, this.GetResourceObservable("SemiColorText1"));

            return new Viewbox { Width = 12, Height = 12, Child = path };
        }

        private static string LabelOf(ToggleButton segment)
        {
            if (segment.Content is string text && !String.IsNullOrEmpty(text))
                return text;

            if (ToolTip.GetTip(segment) is string tip && !String.IsNullOrEmpty(tip))
                return tip;

            return AutomationProperties.GetName(segment) ?? "";
        }

        /// <summary>Width the whole strip needs, and how many of its segments are showing.</summary>
        private (double Width, int Count) MeasureStrip()
        {
            double width = 0;
            var count = 0;

            foreach (var segment in Segments())
            {
                if (!segment.IsVisible)
                    continue;

                width += segment.DesiredSize.Width;
                count++;
            }

            if (count > 1)
                width += Spacing * (count - 1);

            return (width, count);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double height = 0;
            foreach (var segment in Segments())
            {
                if (!segment.IsVisible)
                    continue;

                segment.Measure(Size.Infinity);
                height = Math.Max(height, segment.DesiredSize.Height);
            }

            var (width, count) = MeasureStrip();

            if (_menuDirty)
                RebuildMenu();

            // measured before the early return below (arrange runs whatever measure left behind, and
            // it arranges the dropdown either way) and after the menu is built, because the label the
            // dropdown carries is what makes it as wide as it is.
            _dropdown.Measure(Size.Infinity);

            // nothing to choose between: take up no space, not even the dropdown's.
            if (count == 0)
                return default;

            height = Math.Max(height, _dropdown.DesiredSize.Height);

            // a host that passes a real constraint gets the answer now, and the strip gives up the
            // width it was not going to use; a Grid Auto column measures with infinity, so there the
            // decision falls to arrange instead.
            var fits = width <= availableSize.Width;
            return new Size(fits ? width : _dropdown.DesiredSize.Width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (width, count) = MeasureStrip();

            // half a pixel of slack: the width a Grid hands back is the one it measured, and
            // rounding it against itself must not collapse a strip that actually fits.
            var collapsed = count > 0 && width > finalSize.Width + 0.5;

            SetShown(_dropdown, collapsed);
            if (collapsed)
            {
                var w = Math.Min(_dropdown.DesiredSize.Width, finalSize.Width);
                _dropdown.Arrange(new Rect(0, 0, w, finalSize.Height));
            }
            else
            {
                _dropdown.Arrange(default);
            }

            // the strip fills the slot from its left edge; where that slot sits is the host's
            // business (the Recent header aligns the whole bar right).
            double x = 0;

            foreach (var segment in Segments())
            {
                if (!segment.IsVisible)
                    continue;

                SetShown(segment, !collapsed);

                if (collapsed)
                {
                    segment.Arrange(default);
                    continue;
                }

                segment.Arrange(new Rect(x, 0, segment.DesiredSize.Width, finalSize.Height));
                x += segment.DesiredSize.Width + Spacing;
            }

            return finalSize;
        }

        /// <summary>Hides a child without touching IsVisible (see the type's remarks) and without
        /// invalidating layout — this runs from arrange. Focus goes with it, so a collapsed segment
        /// cannot be tabbed to and pressed while it is not on screen.</summary>
        private static void SetShown(Control child, bool shown)
        {
            child.Opacity = shown ? 1 : 0;
            child.IsHitTestVisible = shown;
            child.Focusable = shown;
        }
    }
}
