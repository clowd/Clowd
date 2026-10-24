using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Clowd.UI.Converters;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>How a palette bar's hex code behaves: plain text (Image), or a button that asks the
    /// tab to open the mini color flyout under it (Palette).</summary>
    public enum PaletteHexMode
    {
        Text,
        Button,
    }

    /// <summary>A Palette-mode hex code was clicked: which bar, its color, and the hex button itself,
    /// to anchor the flyout to.</summary>
    public sealed class PaletteHexClickedEventArgs : EventArgs
    {
        public PaletteHexClickedEventArgs(int index, Color color, Control anchor)
        {
            Index = index;
            Color = color;
            Anchor = anchor;
        }

        public int Index { get; }
        public Color Color { get; }
        public Control Anchor { get; }
    }

    /// <summary>
    /// The five horizontal palette bars the Image and Palette tabs share (brief §6). Each bar is
    /// filled with its color and carries its hex in contrast ink; hovering one shows Copy and Go to
    /// Simple at its trailing edge, and the hovered and selected bars wear the accent ring with a
    /// 2px gap. Clicking a bar (not its hex or buttons) selects it.
    /// </summary>
    /// <remarks>
    /// The tab owns the colors and the selection; this control only shows them and reports what
    /// the user did. Set <see cref="Colors"/> (five, or null for the empty placeholders),
    /// <see cref="SelectedIndex"/>, and optionally <see cref="BaseIndex"/> (the BASE tag),
    /// <see cref="HexMode"/> and <see cref="BottomContent"/> (the 38px slot under the bars).
    /// Copy and Copy all go straight to the clipboard; Go to Simple and a hex click are raised for
    /// the tab to act on. <see cref="HoveredIndex"/> follows the pointer (and keyboard focus) so the
    /// Palette wheel can halo the matching marker.
    /// </remarks>
    public partial class PaletteBars : UserControl
    {
        public const int BarCount = 5;

        public static readonly StyledProperty<string> CaptionProperty =
            AvaloniaProperty.Register<PaletteBars, string>(nameof(Caption));

        /// <summary>The header caption, already in capitals: FROM IMAGE, PALETTE.</summary>
        public string Caption
        {
            get => GetValue(CaptionProperty);
            set => SetValue(CaptionProperty, value);
        }

        public static readonly StyledProperty<IReadOnlyList<Color>> ColorsProperty =
            AvaloniaProperty.Register<PaletteBars, IReadOnlyList<Color>>(nameof(Colors));

        /// <summary>The five colors, top to bottom; null shows five empty placeholders. Assign a new
        /// list for every change (it is compared by reference); updating it live during a drag is
        /// cheap, since the bars are restyled in place rather than rebuilt.</summary>
        public IReadOnlyList<Color> Colors
        {
            get => GetValue(ColorsProperty);
            set => SetValue(ColorsProperty, value);
        }

        public static readonly StyledProperty<int> SelectedIndexProperty =
            AvaloniaProperty.Register<PaletteBars, int>(nameof(SelectedIndex), -1);

        /// <summary>The selected bar, or -1. Set by a click on a bar, or by the tab.</summary>
        public int SelectedIndex
        {
            get => GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }

        public static readonly StyledProperty<int> BaseIndexProperty =
            AvaloniaProperty.Register<PaletteBars, int>(nameof(BaseIndex), -1);

        /// <summary>The bar tagged BASE, or -1 for none (Image).</summary>
        public int BaseIndex
        {
            get => GetValue(BaseIndexProperty);
            set => SetValue(BaseIndexProperty, value);
        }

        public static readonly StyledProperty<PaletteHexMode> HexModeProperty =
            AvaloniaProperty.Register<PaletteBars, PaletteHexMode>(nameof(HexMode));

        public PaletteHexMode HexMode
        {
            get => GetValue(HexModeProperty);
            set => SetValue(HexModeProperty, value);
        }

        public static readonly StyledProperty<object> BottomContentProperty =
            AvaloniaProperty.Register<PaletteBars, object>(nameof(BottomContent));

        /// <summary>What sits in the 38px slot pinned under the bars: the Image tab's "To closest
        /// palette" button, the Palette tab's brightness strip. The slot collapses while empty.</summary>
        public object BottomContent
        {
            get => GetValue(BottomContentProperty);
            set => SetValue(BottomContentProperty, value);
        }

        public static readonly DirectProperty<PaletteBars, int> HoveredIndexProperty =
            AvaloniaProperty.RegisterDirect<PaletteBars, int>(nameof(HoveredIndex), o => o.HoveredIndex);

        private int _hoveredIndex = -1;

        /// <summary>The bar under the pointer (or holding keyboard focus), or -1.</summary>
        public int HoveredIndex
        {
            get => _hoveredIndex;
            private set => SetAndRaise(HoveredIndexProperty, ref _hoveredIndex, value);
        }

        /// <summary>Raised when <see cref="SelectedIndex"/> changes, whoever changed it.</summary>
        public event EventHandler SelectedIndexChanged;

        /// <summary>A bar's Go to Simple button was clicked, with that bar's color.</summary>
        public event EventHandler<Color> GoToSimpleRequested;

        /// <summary>A hex code was clicked (<see cref="PaletteHexMode.Button"/> only).</summary>
        public event EventHandler<PaletteHexClickedEventArgs> HexClicked;

        // the hover actions and the hex button sit on the bar's own color, so their fills follow
        // the bar's luminance rather than the theme: a dark chip on a light color, a light chip on
        // a dark one (brief §6), each with the ink that reads on it
        private static readonly IBrush ActionOnLight = new ImmutableSolidColorBrush(Color.FromArgb(0x8C, 0x16, 0x18, 0x1C));
        private static readonly IBrush ActionOnDark = new ImmutableSolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush HexHotOnLight = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush HexHotOnDark = new ImmutableSolidColorBrush(Color.FromArgb(0x47, 0x00, 0x00, 0x00));
        private static readonly IBrush TagOnLight = new ImmutableSolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush TagOnDark = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00));

        private readonly Bar[] _bars = new Bar[BarCount];

        public PaletteBars()
        {
            InitializeComponent();
            ColorDialogTips.Attach(btnCopyAll);

            for (var i = 0; i < BarCount; i++)
            {
                _bars[i] = new Bar(this, i);
                barsPanel.Children.Add(_bars[i].Host);
            }

            btnCopyAll.Click += async (_, _) =>
            {
                var colors = Colors;
                if (colors == null || colors.Count == 0)
                    return;

                await CopyAsync(String.Join(Environment.NewLine, colors.Select(c => ColorTextHelper.GetHex(c))));
                copyAllIcon.ShowCopied();
            };

            bottomSlot.IsVisible = false;
            UpdateAll();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (_bars[0] == null)
                return;

            if (change.Property == CaptionProperty)
            {
                captionText.Text = Caption;
            }
            else if (change.Property == BottomContentProperty)
            {
                bottomSlot.Content = BottomContent;
                bottomSlot.IsVisible = BottomContent != null;
            }
            else if (change.Property == SelectedIndexProperty)
            {
                UpdateAll();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (change.Property == ColorsProperty || change.Property == BaseIndexProperty
                     || change.Property == HexModeProperty)
            {
                UpdateAll();
            }
        }

        private void UpdateAll()
        {
            btnCopyAll.IsEnabled = Colors is { Count: > 0 };
            foreach (var bar in _bars)
                bar.Update();
        }

        private void SetHovered(int index, bool hovered)
        {
            if (hovered)
                HoveredIndex = index;
            else if (HoveredIndex == index)
                HoveredIndex = -1;

            _bars[index].Update();
        }

        private async System.Threading.Tasks.Task CopyAsync(string text)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await ClipboardImpl.SetClipboardText(clipboard, text);
        }

        /// <summary>One bar: the ring behind it, its fill, the hex (text or button), the BASE tag
        /// and the two hover actions, built once and restyled in place.</summary>
        private sealed class Bar
        {
            private readonly PaletteBars _owner;
            private readonly int _index;

            public readonly Panel Host;
            private readonly Border _ring;
            private readonly Border _fill;
            private readonly Rectangle _placeholder;
            private readonly TextBlock _hexText;
            private readonly Button _hexButton;
            private readonly TextBlock _hexButtonText;
            private readonly Border _tag;
            private readonly TextBlock _tagText;
            private readonly StackPanel _actions;
            private readonly Button _copy;
            private readonly LineIcon _copyIcon;
            private readonly Button _goToSimple;
            private readonly LineIcon _goIcon;

            public Bar(PaletteBars owner, int index)
            {
                _owner = owner;
                _index = index;

                _ring = new Border
                {
                    Margin = new Thickness(-4),
                    CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(2),
                    IsHitTestVisible = false,
                    IsVisible = false,
                };
                _ring.Bind(Border.BorderBrushProperty, Resource("ColorDialogAccentStrongBrush"));

                _placeholder = new Rectangle
                {
                    RadiusX = 8,
                    RadiusY = 8,
                    StrokeThickness = 1,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
                };
                _placeholder.Bind(Shape.FillProperty, Resource("ColorDialogBarChipBrush"));
                _placeholder.Bind(Shape.StrokeProperty, Resource("ColorDialogChipBorderBrush"));

                _hexText = new TextBlock
                {
                    FontSize = 13,
                    FontWeight = FontWeight.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _hexText.Bind(TextBlock.FontFamilyProperty, Resource("MonoFontFamily"));

                _hexButtonText = new TextBlock
                {
                    FontSize = 13,
                    FontWeight = FontWeight.Medium,
                };
                _hexButtonText.Bind(TextBlock.FontFamilyProperty, Resource("MonoFontFamily"));
                _hexButton = new Button
                {
                    Width = double.NaN,
                    Height = 28,
                    Padding = new Thickness(7, 0),
                    CornerRadius = new CornerRadius(6),
                    Content = _hexButtonText,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _hexButton.Bind(StyledElement.ThemeProperty, Resource("ColorDialogOnColorButton"));
                _hexButton.Click += (_, _) =>
                {
                    if (TryGetColor(out var color))
                        _owner.HexClicked?.Invoke(_owner, new PaletteHexClickedEventArgs(_index, color, _hexButton));
                };

                _tagText = new TextBlock
                {
                    Text = "BASE",
                    FontSize = 9.5,
                    FontWeight = FontWeight.Bold,
                    LetterSpacing = 0.76,
                };
                _tag = new Border
                {
                    Padding = new Thickness(6, 2),
                    CornerRadius = new CornerRadius(4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = _tagText,
                };

                _copyIcon = new LineIcon { Width = 16, Height = 16 };
                _copyIcon.Bind(LineIcon.DataProperty, Resource("ColorDialogIconCopy"));
                _copy = ActionButton(_copyIcon, "Copy");
                _copy.Click += async (_, _) =>
                {
                    if (!TryGetColor(out var color))
                        return;

                    await _owner.CopyAsync(ColorTextHelper.GetHex(color));
                    _copyIcon.ShowCopied();
                };

                _goIcon = new LineIcon { Width = 16, Height = 16 };
                _goIcon.Bind(LineIcon.DataProperty, Resource("ColorDialogIconArrowRight"));
                _goToSimple = ActionButton(_goIcon, "Open in Simple");
                _goToSimple.Click += (_, _) =>
                {
                    if (TryGetColor(out var color))
                        _owner.GoToSimpleRequested?.Invoke(_owner, color);
                };

                _actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                _actions.Children.Add(_copy);
                _actions.Children.Add(_goToSimple);
                DockPanel.SetDock(_actions, Dock.Right);

                var lead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                lead.Children.Add(_hexText);
                lead.Children.Add(_hexButton);
                lead.Children.Add(_tag);

                var content = new DockPanel { LastChildFill = true };
                content.Children.Add(_actions);
                content.Children.Add(lead);

                _fill = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Child = content,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                _fill.Bind(Border.BorderBrushProperty, Resource("ColorDialogBarEdgeBrush"));

                Host = new Panel
                {
                    Height = 54,
                    Background = Brushes.Transparent,
                    Focusable = true,
                    IsTabStop = true,
                };
                Host.Children.Add(_ring);
                Host.Children.Add(_placeholder);
                Host.Children.Add(_fill);

                Host.PointerEntered += (_, _) => _owner.SetHovered(_index, true);
                Host.PointerExited += (_, _) => _owner.SetHovered(_index, Host.IsKeyboardFocusWithin);
                Host.PropertyChanged += (_, e) =>
                {
                    if (e.Property == InputElement.IsKeyboardFocusWithinProperty)
                        _owner.SetHovered(_index, Host.IsKeyboardFocusWithin || Host.IsPointerOver);
                };

                // the bar's own press selects it; its buttons handle their presses themselves, so
                // a click on the hex or an action never reaches here
                _fill.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(_fill).Properties.IsLeftButtonPressed && _owner.Colors != null)
                        _owner.SelectedIndex = _index;
                };
                Host.KeyDown += (_, e) =>
                {
                    if (e.Source == Host && e.Key is (Key.Enter or Key.Space) && _owner.Colors != null)
                    {
                        _owner.SelectedIndex = _index;
                        e.Handled = true;
                    }
                };
            }

            // Bound rather than looked up: the bars are built in the constructor, before the control
            // is in a window, and most of these live in the window's styles (ColorDialogTheme).
            private static DynamicResourceExtension Resource(string key) => new(key);

            private Button ActionButton(LineIcon icon, string tip)
            {
                var button = new Button { Content = icon };
                button.Bind(StyledElement.ThemeProperty, Resource("ColorDialogOnColorButton"));
                ToolTip.SetTip(button, tip);
                ColorDialogTips.Attach(button);
                return button;
            }

            private bool TryGetColor(out Color color)
            {
                var colors = _owner.Colors;
                if (colors != null && _index < colors.Count)
                {
                    color = colors[_index];
                    return true;
                }

                color = default;
                return false;
            }

            public void Update()
            {
                var hasColor = TryGetColor(out var color);

                _placeholder.IsVisible = !hasColor;
                _fill.IsVisible = hasColor;
                Host.Focusable = hasColor;

                var hot = hasColor && (_owner.HoveredIndex == _index || _owner.SelectedIndex == _index);
                _ring.IsVisible = hot;

                if (!hasColor)
                    return;

                var light = ColorDialogInk.IsLight(color);
                var ink = light ? ColorDialogInk.Dark : ColorDialogInk.Light;
                var hex = ColorTextHelper.GetHex(color);

                _fill.Background = new SolidColorBrush(color);
                _fill.Padding = _owner.HexMode == PaletteHexMode.Button ? new Thickness(8, 0) : new Thickness(15, 0, 8, 0);

                _hexText.IsVisible = _owner.HexMode == PaletteHexMode.Text;
                _hexText.Text = hex;
                _hexText.Foreground = ink;

                _hexButton.IsVisible = _owner.HexMode == PaletteHexMode.Button;
                _hexButtonText.Text = hex;
                _hexButton.Foreground = ink;
                _hexButton.Background = hot ? (light ? HexHotOnLight : HexHotOnDark) : Brushes.Transparent;
                AutomationProperties.SetName(_hexButton, "Edit " + hex);

                _tag.IsVisible = _owner.BaseIndex == _index;
                _tag.Background = light ? TagOnLight : TagOnDark;
                _tagText.Foreground = ink;

                // hover (or keyboard focus) only: the bar's resting face is its color and its hex
                _actions.IsVisible = _owner.HoveredIndex == _index;
                var actionFill = light ? ActionOnLight : ActionOnDark;
                var actionInk = light ? ColorDialogInk.Light : ColorDialogInk.Dark;
                foreach (var button in new[] { _copy, _goToSimple })
                {
                    button.Background = actionFill;
                    button.Foreground = actionInk;
                }

                AutomationProperties.SetName(_copy, "Copy " + hex);
                AutomationProperties.SetName(_goToSimple, "Open " + hex + " in Simple");
            }
        }
    }
}
