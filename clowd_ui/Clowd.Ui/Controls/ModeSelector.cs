using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Metadata;

namespace Clowd.UI.Controls
{
    /// <summary>One choice a <see cref="ModeSelector"/> offers: the value it stands for, the
    /// title on its tile and the caption underneath.</summary>
    public sealed class ModeOption
    {
        public object Value { get; set; }
        public string Title { get; set; }
        public string Caption { get; set; }
    }

    /// <summary>
    /// A row of large, captioned tiles of which exactly one is active — the "big selector" for the
    /// choice a settings page hinges on (the recording mode today; an on/off switch for a whole
    /// feature tomorrow). The settings factory builds one for any enum property carrying
    /// [ModeSelector], and a hand-written page can declare the same thing in XAML:
    /// <code>
    /// &lt;controls:ModeSelector SelectedValue="{Binding Mode}"&gt;
    ///     &lt;controls:ModeOption Value="{x:Static config:RecordingMode.Studio}" Title="Studio" Caption="…" /&gt;
    /// &lt;/controls:ModeSelector&gt;
    /// </code>
    /// </summary>
    /// <remarks>
    /// The tiles are equal-width columns of a single row — the control is a <see cref="UniformGrid"/>
    /// — so it takes whatever width it is given and the captions wrap to fit. Selection is
    /// exclusive by construction (the control re-asserts the check state after every click, the
    /// CollapsingSegmentedBar way), and a <see cref="SelectedValue"/> matching no option falls
    /// back to the first one: a selector with nothing active reads as broken.
    /// </remarks>
    public class ModeSelector : UniformGrid
    {
        public static readonly StyledProperty<object> SelectedValueProperty =
            AvaloniaProperty.Register<ModeSelector, object>(
                nameof(SelectedValue), defaultBindingMode: BindingMode.TwoWay);

        /// <summary>The <see cref="ModeOption.Value"/> of the active tile.</summary>
        public object SelectedValue
        {
            get => GetValue(SelectedValueProperty);
            set => SetValue(SelectedValueProperty, value);
        }

        /// <summary>The tiles, in order. The content property, so XAML children land here.</summary>
        [Content]
        public AvaloniaList<ModeOption> Options { get; } = new();

        /// <summary>Raised whenever <see cref="SelectedValue"/> changes, however it was changed.</summary>
        public event EventHandler SelectionChanged;

        private static readonly Uri _stylesUri = new("avares://Clowd.Ui/Controls/ModeSelector.axaml");

        private const double TileSpacing = 10;

        public ModeSelector()
        {
            Styles.Add(new StyleInclude(_stylesUri) { Source = _stylesUri });

            Rows = 1;
            Options.CollectionChanged += OnOptionsChanged;
        }

        private void OnOptionsChanged(object sender, NotifyCollectionChangedEventArgs e) => Rebuild();

        /// <summary>Recreates the tiles from <see cref="Options"/>. Cheap and rare (a page builds its
        /// selector once), so no attempt is made to patch individual tiles.</summary>
        private void Rebuild()
        {
            Children.Clear();

            for (int i = 0; i < Options.Count; i++)
            {
                var tile = BuildTile(Options[i]);
                // UniformGrid has no spacing of its own; the gap is a margin on every tile but the first.
                tile.Margin = new Thickness(i == 0 ? 0 : TileSpacing, 0, 0, 0);
                Children.Add(tile);
            }

            Columns = Math.Max(1, Options.Count);

            // no fallback here: options are usually added one at a time, and a stored value that
            // happens not to be the first option would be "matching nothing" for a moment — and
            // written back over through a two-way binding. The fallback waits for attachment.
            if (_attached)
                EnsureSelection();
            else
                SyncSelection();
        }

        private bool _attached;

        /// <summary>Attachment is the first point at which the options and the binding are both
        /// in place, so it is where a value matching no option is finally corrected.</summary>
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            EnsureSelection();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _attached = false;
        }

        private ToggleButton BuildTile(ModeOption option)
        {
            var title = new TextBlock
            {
                Text = option.Title,
                FontWeight = FontWeight.SemiBold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.Classes.Add("ModeTitle");

            // the tick sits in the title row's right-hand column and is only shown on the active
            // tile — by the styles, not by a local IsVisible here, which would outrank them — so
            // the title never moves when it appears.
            var check = new Path
            {
                Data = FindGeometry("IconCheckmark"),
                Stretch = Stretch.Uniform,
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            check.Classes.Add("ModeCheck");
            check.Bind(Shape.FillProperty, this.GetResourceObservable("ClowdAccentTextBrush"));

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(title);
            Grid.SetColumn(check, 1);
            header.Children.Add(check);

            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(header);

            if (!String.IsNullOrEmpty(option.Caption))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = option.Caption,
                    FontSize = 12,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var tile = new ToggleButton { Tag = option.Value, Content = stack };
            AutomationProperties.SetName(tile, option.Title);
            tile.Click += TileClicked;
            return tile;
        }

        private static Geometry FindGeometry(string key)
        {
            var app = Application.Current;
            if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is Geometry geometry)
                return geometry;

            return null;
        }

        private void TileClicked(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            SetCurrentValue(SelectedValueProperty, (sender as Control)?.Tag);

            // clicking the active tile has already unchecked it by the time we get here, and the
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

        /// <summary>Falls back to the first tile when <see cref="SelectedValue"/> matches none of
        /// them. Only ever called with the full option set in place (see <see cref="Rebuild"/>):
        /// the write goes through whatever binding the caller attached.</summary>
        private void EnsureSelection()
        {
            if (Options.Count == 0)
                return;

            if (Options.Any(o => Equals(o.Value, SelectedValue)))
                SyncSelection();
            else
                SetCurrentValue(SelectedValueProperty, Options[0].Value);
        }

        private void SyncSelection()
        {
            foreach (var tile in Children.OfType<ToggleButton>())
            {
                var selected = Equals(tile.Tag, SelectedValue);
                if (tile.IsChecked != selected)
                    tile.IsChecked = selected;
            }
        }
    }
}
