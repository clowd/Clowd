using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Metadata;
using Avalonia.Threading;

namespace Clowd.UI.Controls
{
    /// <summary>One choice a <see cref="ModeSelector"/> offers: the value it stands for, its title
    /// (the chip's label, and the banner's headline while it is active), the caption written under
    /// that headline and the glyph in the banner's icon disc.</summary>
    public sealed class ModeOption
    {
        public object Value { get; set; }
        public string Title { get; set; }
        public string Caption { get; set; }

        /// <summary>The glyph drawn in the banner's disc while this option is active — any
        /// geometry, fitted to the disc by its own bounds, so the canvas it was drawn on does not
        /// matter. Null shows the title's initial instead.</summary>
        public Geometry Icon { get; set; }
    }

    /// <summary>
    /// The "status banner" a settings page opens with: one full-width card that says which mode
    /// the page's feature is in — a big icon, the mode's name and caption — with a pill of chips on
    /// the right to change it. Meant for the one choice a page hinges on (the recording mode; an
    /// on/off switch for a whole feature). The settings factory builds one for any enum property
    /// carrying [ModeSelector], and a hand-written page can declare the same thing in XAML:
    /// <code>
    /// &lt;controls:ModeSelector SelectedValue="{Binding Mode}" FeatureName="Recording"&gt;
    ///     &lt;controls:ModeOption Value="{x:Static config:RecordingMode.Studio}" Title="Studio Mode"
    ///                         Caption="…" Icon="{StaticResource IconLayers}" /&gt;
    /// &lt;/controls:ModeSelector&gt;
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para><b>Two looks.</b> The banner is "running" for every option but the enum member named
    /// <c>Off</c>: accent-tinted, accent-outlined, a soft accent glow behind the icon. Off turns it
    /// neutral, with a faint diagonal hatch. Both looks are always built, the off one underneath
    /// and the running one stacked over it as a single layer; the control only flips its
    /// <c>:off</c> pseudo-class and ModeSelector.axaml fades that top layer out (or back in). The
    /// running look dissolves to reveal the off look beneath it rather than every brush easing on
    /// its own — eased one by one, the card surface and the tint drift apart mid-way and the
    /// banner flashes pale between the two states. The icon disc's fill is layered the same way.</para>
    /// <para><b>Stacked content.</b> Every option's icon sits in the disc at once, and every
    /// option's headline and caption in one grid cell — only the active one visible. Swapping
    /// modes flips the icon over like a card (the old glyph turns edge-on, the new one turns in
    /// from the other side) and crossfades the text, which rises a few pixels; the text cell is
    /// always as tall as the tallest option's text, so switching never makes the page below
    /// jump.</para>
    /// <para><b>Selection.</b> Exactly one chip is active, always: the control re-asserts the
    /// check state after every click (the CollapsingSegmentedBar way), and a
    /// <see cref="SelectedValue"/> matching no option falls back to the first one — a selector
    /// with nothing active reads as broken. The chips are a radio group for the keyboard: one tab
    /// stop (the active chip), arrows to move through them, Home/End for the ends.</para>
    /// <para><b>Width.</b> The banner stretches to whatever it is given (the settings pages give
    /// it the width of the sections under it). When that is too narrow for the headline beside
    /// the chips, the chips move to a row of their own under the text rather than squeezing it,
    /// and the chips themselves wrap before they would clip.</para>
    /// </remarks>
    public class ModeSelector : Control
    {
        public static readonly StyledProperty<object> SelectedValueProperty =
            AvaloniaProperty.Register<ModeSelector, object>(
                nameof(SelectedValue), defaultBindingMode: BindingMode.TwoWay);

        /// <summary>The <see cref="ModeOption.Value"/> of the active option.</summary>
        public object SelectedValue
        {
            get => GetValue(SelectedValueProperty);
            set => SetValue(SelectedValueProperty, value);
        }

        public static readonly StyledProperty<string> FeatureNameProperty =
            AvaloniaProperty.Register<ModeSelector, string>(nameof(FeatureName));

        /// <summary>
        /// The feature the choice switches, as the page names it ("Draw on Screen"): the banner's
        /// eyebrow, over the active option's title. Optional — without it there is no eyebrow.
        /// </summary>
        public string FeatureName
        {
            get => GetValue(FeatureNameProperty);
            set => SetValue(FeatureNameProperty, value);
        }

        /// <summary>The options, in chip order. The content property, so XAML children land here.</summary>
        [Content]
        public AvaloniaList<ModeOption> Options { get; } = new();

        /// <summary>Raised whenever <see cref="SelectedValue"/> changes, however it was changed.</summary>
        public event EventHandler SelectionChanged;

        private static readonly Uri _stylesUri = new("avares://Clowd.Ui/Controls/ModeSelector.axaml");

        // ---- geometry ----

        /// <summary>The banner's inner padding: a little more on the left, where the icon disc
        /// sits against the rounded corner.</summary>
        private static readonly Thickness ContentPadding = new(18, 16, 16, 16);

        private const double BannerRadius = 14;
        private const double DiscSize = 48;
        private const double GlyphSize = 22;

        /// <summary>The gaps either side of the text block (disc ↔ text ↔ chips).</summary>
        private static readonly Thickness TextMargin = new(14, 0, 16, 0);

        /// <summary>Below this much room for the headline, the chips move under the text. Enough
        /// for the longest headline today ("Instant Mode" at 18px bold) with room to spare, so the
        /// caption under it isn't squeezed into a sliver before the chips make room.</summary>
        private const double MinTextWidth = 200;

        /// <summary>The glow's square (its mask fades it out to a circle) and where its center
        /// lands: a little above and inside the icon disc, so the light reads as coming from the
        /// top-left corner rather than haloing the disc.</summary>
        private const double GlowSize = 320;
        private static readonly Point GlowCenter = new(40, 28);

        // ---- motion ----

        /// <summary>Each half of the icon flip: turning the old glyph edge-on, then the new one in
        /// from the other side.</summary>
        private static readonly TimeSpan FlipHalfDuration = TimeSpan.FromMilliseconds(170);

        /// <summary>The flip's perspective distance. Small enough that the glyph's near edge
        /// visibly grows as it turns — the "card being turned over" read — without the far edge
        /// pinching to a point.</summary>
        private const double FlipDepth = 90;

        private static readonly TimeSpan TextDuration = TimeSpan.FromMilliseconds(300);

        /// <summary>The incoming headline waits this long, so the outgoing one has visibly begun
        /// to leave before the two overlap — a crossfade that starts in the same frame reads as
        /// one smeared block of text.</summary>
        private static readonly TimeSpan TextEnterDelay = TimeSpan.FromMilliseconds(70);

        // Every pose of a layer is the same list of operations, so transitions between them
        // interpolate operation by operation instead of falling back to a decomposed matrix.
        // TransformOperations are immutable, so one instance each serves every layer of every
        // selector.
        private static readonly ITransform TextRest = TransformOperations.Parse("translateY(0px)");
        private static readonly ITransform TextEnterFrom = TransformOperations.Parse("translateY(5px)");
        private static readonly ITransform TextExitTo = TransformOperations.Parse("translateY(-5px)");

        // ---- parts ----

        private readonly Panel _root;
        private readonly Grid _layout;
        private readonly Grid _glyphs;
        private readonly StackPanel _text;
        private readonly TextBlock _eyebrow;
        private readonly Grid _headlines;
        private readonly WrapPanel _chips;
        private readonly Panel _chipGroup;

        /// <summary>Everything one option contributes to the banner, kept together so selection
        /// can address an option's chip, glyph and headline by the same index.</summary>
        private sealed record Slot(ModeOption Option, RadioButton Chip, Control Glyph, Control Headline);

        private readonly List<Slot> _slots = new();

        /// <summary>The slot whose glyph and headline are showing (or on their way in); -1 for
        /// none.</summary>
        private int _shown = -1;

        /// <summary>The incoming headline's delayed reveal, cancelled if the selection moves on
        /// before it fires.</summary>
        private IDisposable _pendingReveal;

        /// <summary>The headline <see cref="_pendingReveal"/> will show, so a detach in the
        /// meantime can show it at once instead of leaving the text cell blank.</summary>
        private Control _pendingHeadline;

        private bool _narrow;
        private bool _attached;

        // ---- icon flip ----

        /// <summary>The flip turns the whole glyph cell about its vertical axis; the glyphs inside
        /// only ever show or hide, at the moment the cell is edge-on.</summary>
        private readonly Rotate3DTransform _flip = new() { Depth = FlipDepth };

        /// <summary>The slot whose glyph the disc shows — the one on the visible face of the
        /// flip, which trails <see cref="_shown"/> by half a flip while one runs.</summary>
        private int _glyphShown = -1;

        /// <summary>When the flip in progress started; null when the icon is at rest.</summary>
        private TimeSpan? _flipStart;

        /// <summary>Whether the flip in progress has passed edge-on and shown its new face.</summary>
        private bool _flipSwapped;

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public ModeSelector()
        {
            Styles.Add(new StyleInclude(_stylesUri) { Source = _stylesUri });

            // ground, in two looks stacked: the off look at the bottom (a neutral veil and the
            // hatch), and over it the running look — the card, the accent tint, the glow and the
            // accent outline — as one layer the styles fade as a whole. Each look's fills sit in a
            // Border that clips them to its rounded corners (Border clips to its corner radius
            // when ClipToBounds is set). The glow lives on a Canvas so its oversized, negatively
            // placed square adds nothing to the banner's measured size.
            var hatch = new Rectangle { IsHitTestVisible = false };
            hatch.Classes.Add("ModeHatch");

            var offGround = new Border
            {
                CornerRadius = new CornerRadius(BannerRadius),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                IsHitTestVisible = false,
                Child = hatch,
            };
            offGround.Classes.Add("ModeOffGround");

            var glow = new Border { Width = GlowSize, Height = GlowSize, IsHitTestVisible = false };
            glow.Classes.Add("ModeGlow");
            Canvas.SetLeft(glow, GlowCenter.X - GlowSize / 2);
            Canvas.SetTop(glow, GlowCenter.Y - GlowSize / 2);

            var tint = new Border { IsHitTestVisible = false };
            tint.Classes.Add("ModeTint");

            // the section surface, over a ground of the window's own (opaque) background: the
            // running look has to cover the off look completely while it is shown, whether or not
            // the theme's section surface is itself opaque.
            var card = new Border { IsHitTestVisible = false };
            card.Classes.Add("ModeCard");

            var onGround = new Border
            {
                CornerRadius = new CornerRadius(BannerRadius),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = new Panel
                {
                    Children =
                    {
                        card,
                        tint,
                        new Canvas { IsHitTestVisible = false, Children = { glow } },
                    },
                },
            };
            onGround.Classes.Add("ModeOnGround");

            // the accent outline is its own border over the card's, at a fixed opacity: the 55%
            // it is drawn at cannot be put on a DynamicResource brush.
            var ring = new Border
            {
                CornerRadius = new CornerRadius(BannerRadius),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
            };
            ring.Classes.Add("ModeRing");

            var onLayer = new Panel { IsHitTestVisible = false, Children = { onGround, ring } };
            onLayer.Classes.Add("ModeOnLayer");

            // icon disc: the off disc with the running (accent) disc over it, faded like the
            // banner's own layers, and every option's glyph stacked in one cell on top. The flip
            // turns that cell, never the disc.
            _glyphs = new Grid { RenderTransform = _flip, IsHitTestVisible = false };
            _glyphs.Classes.Add("ModeGlyphs");

            var offDisc = new Border
            {
                CornerRadius = new CornerRadius(DiscSize / 2),
                BorderThickness = new Thickness(1),
            };
            offDisc.Classes.Add("ModeIconDiscOff");

            var onDisc = new Border { CornerRadius = new CornerRadius(DiscSize / 2) };
            onDisc.Classes.Add("ModeIconDiscOn");

            var disc = new Panel
            {
                Width = DiscSize,
                Height = DiscSize,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { offDisc, onDisc, _glyphs },
            };

            // text: the eyebrow over every option's headline + caption, stacked in one cell
            _eyebrow = new TextBlock { IsVisible = false };
            _eyebrow.Classes.Add("ModeEyebrow");
            _headlines = new Grid();
            _text = new StackPanel
            {
                Margin = TextMargin,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _eyebrow, _headlines },
            };

            // chips: a pill of a translucent card wash under a hairline frame. The tunnel handler
            // runs before the chips see the key, so the arrows mean "next option" here rather than
            // whatever directional focus navigation would make of them.
            _chips = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 2, LineSpacing = 2 };
            _chips.AddHandler(KeyDownEvent, OnChipsKeyDown, RoutingStrategies.Tunnel);

            var wash = new Border { CornerRadius = new CornerRadius(99), IsHitTestVisible = false };
            wash.Classes.Add("ModeChipWash");
            var frame = new Border
            {
                CornerRadius = new CornerRadius(99),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                Child = _chips,
            };
            frame.Classes.Add("ModeChipFrame");
            _chipGroup = new Panel { VerticalAlignment = VerticalAlignment.Center, Children = { wash, frame } };

            _layout = new Grid
            {
                Margin = ContentPadding,
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
            };
            _layout.Children.Add(disc);
            Grid.SetColumn(_text, 1);
            _layout.Children.Add(_text);
            _layout.Children.Add(_chipGroup);
            PlaceChips(narrow: false);

            _root = new Panel { Children = { offGround, onLayer, _layout } };

            // the one child, in both trees: logical so the styles above reach every part, visual
            // so it draws.
            LogicalChildren.Add(_root);
            VisualChildren.Add(_root);

            Options.CollectionChanged += OnOptionsChanged;
        }

        /// <summary>The geometry behind a VectorIcons key, or null when the key is empty or does
        /// not resolve — for callers (the settings factory) that name a mode's icon by key.</summary>
        public static Geometry FindIcon(string key)
        {
            var app = Application.Current;
            if (String.IsNullOrEmpty(key) || app == null)
                return null;

            return app.TryGetResource(key, app.ActualThemeVariant, out var res) ? res as Geometry : null;
        }

        // ---- layout ----

        /// <summary>
        /// Decides, for the width on offer, whether the chips fit beside the headline or go under
        /// it, then lays the banner out. Decided here rather than left to the grid because a grid
        /// cannot move a child between rows: an Auto column would simply squeeze the headline
        /// down to nothing first.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            UpdateChipPlacement(availableSize.Width);
            _root.Measure(availableSize);
            return _root.DesiredSize;
        }

        /// <summary>The decision is taken again with the final width: a host that measured with
        /// an infinite width (a Grid Auto column) only settles the real one here — the
        /// CollapsingSegmentedBar lesson.</summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            if (UpdateChipPlacement(finalSize.Width))
                _root.Measure(finalSize);

            _root.Arrange(new Rect(finalSize));
            return finalSize;
        }

        /// <summary>Moves the chips beside or under the text for <paramref name="width"/>; true
        /// when that changed where they are.</summary>
        private bool UpdateChipPlacement(double width)
        {
            var narrow = false;
            if (!Double.IsInfinity(width))
            {
                _chipGroup.Measure(Size.Infinity);
                var room = width - ContentPadding.Left - ContentPadding.Right - DiscSize
                           - TextMargin.Left - TextMargin.Right - _chipGroup.DesiredSize.Width;
                narrow = room < MinTextWidth;
            }

            if (narrow == _narrow)
                return false;

            PlaceChips(narrow);
            return true;
        }

        private void PlaceChips(bool narrow)
        {
            _narrow = narrow;

            // narrow: a second row under the text, starting where the text starts (the disc keeps
            // the first row to itself). Wide: the third column, centered against the text.
            Grid.SetRow(_chipGroup, narrow ? 1 : 0);
            Grid.SetColumn(_chipGroup, narrow ? 1 : 2);
            Grid.SetColumnSpan(_chipGroup, narrow ? 2 : 1);
            _chipGroup.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _chipGroup.Margin = narrow ? new Thickness(TextMargin.Left, 12, 0, 0) : default;
        }

        // ---- options ----

        private void OnOptionsChanged(object sender, NotifyCollectionChangedEventArgs e) => Rebuild();

        /// <summary>Recreates every option's chip, glyph and headline from <see cref="Options"/>.
        /// Cheap and rare (a page builds its selector once), so no attempt is made to patch
        /// individual options.</summary>
        private void Rebuild()
        {
            CancelPendingReveal();

            _slots.Clear();
            _chips.Children.Clear();
            _glyphs.Children.Clear();
            _headlines.Children.Clear();
            _shown = -1;
            _glyphShown = -1;
            _flipStart = null;
            _flip.AngleY = 0;

            foreach (var option in Options)
            {
                var slot = new Slot(option, BuildChip(option), BuildGlyph(option), BuildHeadline(option));
                _slots.Add(slot);
                _chips.Children.Add(slot.Chip);
                _glyphs.Children.Add(slot.Glyph);
                _headlines.Children.Add(slot.Headline);
            }

            UpdateEyebrow();

            // no fallback here: options are usually added one at a time, and a stored value that
            // happens not to be the first option would be "matching nothing" for a moment — and
            // written back over through a two-way binding. The fallback waits for attachment.
            if (_attached)
                EnsureSelection();
            else
                SyncSelection();
        }

        /// <summary>Whether <paramref name="value"/> is the "feature off" option — the one that
        /// turns the banner neutral. By name, so any enum (or a plain "Off" string from XAML)
        /// qualifies without the control knowing the type.</summary>
        private static bool IsOffValue(object value) =>
            value != null && String.Equals(value.ToString(), "Off", StringComparison.Ordinal);

        /// <summary>A chip's label: the title without a trailing " Mode", which the chip row
        /// does not need to say three times (Studio / Instant / Off).</summary>
        private static string ChipLabel(string title)
        {
            const string suffix = " Mode";
            return title != null && title.Length > suffix.Length && title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? title.Substring(0, title.Length - suffix.Length)
                : title;
        }

        private RadioButton BuildChip(ModeOption option)
        {
            // no GroupName: radio buttons without one are grouped by their parent, which is
            // exactly this selector's chip panel — two selectors on a page never interfere.
            var chip = new RadioButton { Content = ChipLabel(option.Title), Tag = option.Value };
            chip.Classes.Add("ModeChip");
            if (IsOffValue(option.Value))
                chip.Classes.Add("off");

            AutomationProperties.SetName(chip, option.Title);
            if (!String.IsNullOrEmpty(option.Caption))
                AutomationProperties.SetHelpText(chip, option.Caption);

            chip.Click += ChipClicked;
            return chip;
        }

        private static Control BuildGlyph(ModeOption option)
        {
            Control glyph;
            if (option.Icon != null)
            {
                // A Viewbox around an unsized, uniformly stretched Path: the path measures at its
                // geometry's own tight bounds and the Viewbox scales that to fit, centered. A Path
                // given a fixed square instead pins narrow glyphs (the lightning bolt) to the
                // square's left edge, and an explicit canvas size would not suit icons drawn on
                // different canvases.
                var path = new Path { Data = option.Icon, Stretch = Stretch.Uniform };
                path.Classes.Add("ModeIconGlyph");
                glyph = new Viewbox
                {
                    MaxWidth = GlyphSize,
                    MaxHeight = GlyphSize,
                    Stretch = Stretch.Uniform,
                    Child = path,
                };
            }
            else
            {
                var initial = String.IsNullOrEmpty(option.Title) ? "?" : option.Title.Substring(0, 1).ToUpperInvariant();
                glyph = new TextBlock { Text = initial };
                glyph.Classes.Add("ModeIconInitial");
            }

            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.IsHitTestVisible = false;

            // hidden until it is on the flip's visible face; no transitions of its own — it
            // appears and disappears only while the cell is edge-on, where the switch can't be seen
            glyph.IsVisible = false;
            return glyph;
        }

        private static Control BuildHeadline(ModeOption option)
        {
            var title = new TextBlock { Text = option.Title };
            title.Classes.Add("ModeTitle");

            var stack = new StackPanel { Spacing = 2, IsHitTestVisible = false, Children = { title } };

            // a caption-less option still takes part in the stack (its headline alone), and the
            // cell is as tall as the tallest option either way
            if (!String.IsNullOrEmpty(option.Caption))
            {
                var caption = new TextBlock { Text = option.Caption };
                caption.Classes.Add("ModeCaption");
                stack.Children.Add(caption);
            }

            stack.Opacity = 0;
            stack.RenderTransform = TextEnterFrom;
            stack.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TextDuration, Easing = new CubicEaseOut() },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TextDuration, Easing = new CubicEaseOut() },
            };
            return stack;
        }

        private void UpdateEyebrow()
        {
            var feature = FeatureName;
            _eyebrow.Text = feature?.ToUpperInvariant();
            _eyebrow.IsVisible = !String.IsNullOrEmpty(feature);
            AutomationProperties.SetName(_chips, String.IsNullOrEmpty(feature) ? "Mode" : feature);
        }

        // ---- attachment ----

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

            // a headline still waiting on its delay would otherwise never be shown: by the next
            // attach the selection has not changed, so nothing would reveal it. A flip in flight
            // is finished at once for the same reason — its frames stop arriving off screen.
            var headline = _pendingHeadline;
            CancelPendingReveal();
            if (headline != null)
                Reveal(headline, TextEnterFrom, TextRest);

            SnapGlyph();
        }

        private void CancelPendingReveal()
        {
            _pendingReveal?.Dispose();
            _pendingReveal = null;
            _pendingHeadline = null;
        }

        // ---- selection ----

        private void ChipClicked(object sender, RoutedEventArgs e)
        {
            SetCurrentValue(SelectedValueProperty, (sender as Control)?.Tag);

            // a click on the active chip changes nothing, and a radio button only ever checks
            // itself — but re-assert the whole group either way, so the chips can never disagree
            // with SelectedValue.
            SyncSelection();
        }

        /// <summary>The chips as a radio group: Left/Up and Right/Down step through the options
        /// (wrapping at the ends), Home and End jump to them. Moving selects as well as focuses,
        /// the way a radio group does.</summary>
        private void OnChipsKeyDown(object sender, KeyEventArgs e)
        {
            var count = _slots.Count;
            if (count == 0 || e.KeyModifiers != KeyModifiers.None)
                return;

            var current = Math.Max(0, IndexOfSelected());
            var target = e.Key switch
            {
                Key.Left or Key.Up => (current - 1 + count) % count,
                Key.Right or Key.Down => (current + 1) % count,
                Key.Home => 0,
                Key.End => count - 1,
                _ => -1,
            };
            if (target < 0)
                return;

            e.Handled = true;
            SetCurrentValue(SelectedValueProperty, _slots[target].Option.Value);
            _slots[target].Chip.Focus(NavigationMethod.Directional);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == FeatureNameProperty)
            {
                UpdateEyebrow();
                return;
            }

            if (change.Property != SelectedValueProperty)
                return;

            SyncSelection();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private int IndexOfSelected()
        {
            for (int i = 0; i < _slots.Count; i++)
                if (Equals(_slots[i].Option.Value, SelectedValue))
                    return i;

            return -1;
        }

        /// <summary>Falls back to the first option when <see cref="SelectedValue"/> matches none of
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

        /// <summary>Brings every part of the banner in line with <see cref="SelectedValue"/>: the
        /// chips' check and tab stop, the running/off look, and which glyph and headline show.</summary>
        private void SyncSelection()
        {
            var selected = IndexOfSelected();

            for (int i = 0; i < _slots.Count; i++)
            {
                var chip = _slots[i].Chip;
                var on = i == selected;
                if (chip.IsChecked != on)
                    chip.IsChecked = on;

                // roving tab stop: Tab lands on the active chip and leaves the group from there;
                // the arrows do the moving inside it. With nothing selected yet, the first chip
                // keeps the group reachable.
                chip.IsTabStop = on || (selected < 0 && i == 0);
            }

            PseudoClasses.Set(":off", selected >= 0 && IsOffValue(_slots[selected].Option.Value));

            if (selected == _shown)
                return;

            CancelPendingReveal();

            if (_shown >= 0 && _shown < _slots.Count)
                Conceal(_slots[_shown].Headline, TextExitTo);

            _shown = selected;

            // the icon flips over to the new glyph on screen; before that the first frame simply
            // carries it
            if (_attached && _glyphShown >= 0 && TopLevel.GetTopLevel(this) != null)
                StartFlip();
            else
                SnapGlyph();

            if (selected < 0)
                return;

            var incoming = _slots[selected];

            // the headline waits a beat (TextEnterDelay) — but only on screen: before the banner
            // is shown there is nothing to cross-fade, and the first frame must already carry the
            // text.
            if (_attached)
            {
                _pendingHeadline = incoming.Headline;
                _pendingReveal = DispatcherTimer.RunOnce(() =>
                {
                    _pendingReveal = null;
                    _pendingHeadline = null;
                    Reveal(incoming.Headline, TextEnterFrom, TextRest);
                }, TextEnterDelay);
            }
            else
            {
                Reveal(incoming.Headline, TextEnterFrom, TextRest);
            }
        }

        /// <summary>
        /// Brings a stacked layer into view from <paramref name="from"/> to
        /// <paramref name="rest"/>. A layer still on its way out (partly visible) simply turns
        /// round from where it is; one fully out of sight is first put back at the entrance pose
        /// with its transitions lifted, so it does not visibly travel there from the pose it last
        /// left in.
        /// </summary>
        /// <remarks>Before the control is on screen Avalonia runs no transitions at all, so the
        /// same calls simply set the end state — which is how the first frame comes up already
        /// settled.</remarks>
        private static void Reveal(Control layer, ITransform from, ITransform rest)
        {
            // Opacity reads the animated value mid-transition, so this is "currently invisible"
            if (layer.Opacity < 0.02)
            {
                var transitions = layer.Transitions;
                layer.Transitions = null;
                layer.RenderTransform = from;
                layer.Opacity = 0;
                layer.Transitions = transitions;
            }

            layer.RenderTransform = rest;
            layer.Opacity = 1;
        }

        private static void Conceal(Control layer, ITransform exitTo)
        {
            layer.RenderTransform = exitTo;
            layer.Opacity = 0;
        }

        // ---- icon flip ----

        /// <summary>
        /// Starts turning the icon over to <see cref="_shown"/>'s glyph. A flip already under way
        /// is left to run: its second half lands on whatever <see cref="_shown"/> is by the time
        /// it gets there, and if the selection moved again after that, <see cref="OnFlipFrame"/>
        /// flips once more from rest — so rapid clicks queue at most one extra turn instead of
        /// jerking the icon back mid-way.
        /// </summary>
        private void StartFlip()
        {
            if (_flipStart != null)
                return;

            _flipStart = _clock.Elapsed;
            _flipSwapped = false;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFlipFrame);
        }

        private void OnFlipFrame(TimeSpan _)
        {
            if (_flipStart is not { } start)
                return;

            var half = FlipHalfDuration.TotalMilliseconds;
            var t = (_clock.Elapsed - start).TotalMilliseconds;

            if (t < half)
            {
                // first half: the old glyph turns away, accelerating like a card lifted off the
                // table. Stopped short of a true 90° — exactly edge-on, the perspective matrix is
                // singular.
                var p = t / half;
                _flip.AngleY = 89 * p * p;
            }
            else if (t < 2 * half)
            {
                // edge-on: the other face is now the new glyph, in its own state's colors. Swapped
                // once per flip — a selection that moves on during the second half waits for the
                // next flip rather than changing the face that is already turning in.
                if (!_flipSwapped)
                {
                    _flipSwapped = true;
                    ShowGlyph(_shown);
                }

                // second half: the new face turns in from the far side and settles
                var p = 1 - (t - half) / half;
                _flip.AngleY = -89 * p * p * p;
            }
            else
            {
                _flip.AngleY = 0;
                if (!_flipSwapped)
                    ShowGlyph(_shown);

                if (_glyphShown == _shown)
                {
                    _flipStart = null;
                    return;
                }

                // the selection moved on while this flip was landing: turn over once more
                _flipStart = _clock.Elapsed;
                _flipSwapped = false;
            }

            // keep the frames coming until the flip lands; off screen there is no top level and
            // the detach handler has already snapped it
            var top = TopLevel.GetTopLevel(this);
            if (top == null)
            {
                SnapGlyph();
                return;
            }

            if (_flipStart != null)
                top.RequestAnimationFrame(OnFlipFrame);
        }

        /// <summary>Ends any flip and shows <see cref="_shown"/>'s glyph face-on, at once.</summary>
        private void SnapGlyph()
        {
            _flipStart = null;
            _flip.AngleY = 0;
            ShowGlyph(_shown);
        }

        /// <summary>Puts <paramref name="index"/>'s glyph (and its state's colors) on the visible
        /// face. The colors follow the glyph rather than the banner's <c>:off</c> state, which
        /// changes at the click: the outgoing glyph keeps its own colors while it turns away.</summary>
        private void ShowGlyph(int index)
        {
            for (int i = 0; i < _slots.Count; i++)
                _slots[i].Glyph.IsVisible = i == index;

            _glyphs.Classes.Set("off", index >= 0 && index < _slots.Count && IsOffValue(_slots[index].Option.Value));
            _glyphShown = index;
        }
    }
}
