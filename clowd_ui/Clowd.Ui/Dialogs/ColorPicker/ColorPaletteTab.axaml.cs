using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using Clowd.UI.Converters;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// The color dialog's Palette tab (brief §7.2): a harmony preset and a base, generating five
    /// colors through <see cref="ColorHarmony"/>. Every edit — a marker dragged on the wheel, a
    /// color changed in the mini flyout, the brightness strip — works out the base that edit
    /// implies and regenerates the whole palette from it, so the preset's relationship always holds.
    /// </summary>
    /// <remarks>
    /// Selected color: the selected bar's, the base by default (brief §7.2). Dragging a marker or
    /// opening a bar's flyout selects that bar, so the top bar's Selected line and the copy buttons
    /// follow whatever is being edited. The tab is seeded the first time it is shown, from the
    /// dialog's initial color, unless something (the Image tab's "To closest palette") has loaded
    /// a harmony into it first.
    /// </remarks>
    public partial class ColorPaletteTab : ColorDialogTab
    {
        /// <summary>The mockup's opening palette: also the fallback for a seed with no hue to speak of.</summary>
        private static readonly HsvColor DefaultBase = new(200, 0.8, 0.94);

        /// <summary>Below this saturation a seed color's hue is noise (or, for a gray, simply 0), and
        /// every member of its harmony would come out gray too.</summary>
        private const double MinSeedSaturation = 0.12;

        public HarmonyPreset Preset { get; private set; } = HarmonyPreset.SplitComplementary;

        public HsvColor Base { get; private set; } = DefaultBase;

        private HsvColor[] _palette;

        private readonly RadioButton[] _presetButtons;

        // false until the first show or LoadHarmony, whichever comes first
        private bool _seeded;

        // the member the open flyout is editing, or -1
        private int _flyoutIndex = -1;

        // the base and selected bar to put back if an eyedropper drag ends without a pick
        private (HsvColor Base, int SelectedIndex)? _eyedropperRestore;

        public ColorPaletteTab()
        {
            InitializeComponent();

            _presetButtons = ColorHarmony.Presets.Select(CreatePresetButton).ToArray();
            for (var i = 0; i < _presetButtons.Length; i++)
            {
                Grid.SetColumn(_presetButtons[i], i % 4 * 2);
                Grid.SetRow(_presetButtons[i], i / 4 * 2);
                presetGrid.Children.Add(_presetButtons[i]);
            }

            presetGrid.AddHandler(KeyDownEvent, OnPresetKeyDown, RoutingStrategies.Tunnel);

            bars.BaseIndex = ColorHarmony.BaseIndex;
            bars.SelectedIndexChanged += (_, _) => SyncSelection();
            bars.GoToSimpleRequested += (_, color) => Host?.ShowInSimple(color);
            bars.HexClicked += (_, e) => OpenFlyout(e.Index, e.Anchor);
            bars.PropertyChanged += (_, e) =>
            {
                if (e.Property == PaletteBars.HoveredIndexProperty)
                    UpdateHighlight();
            };

            wheel.BaseIndex = ColorHarmony.BaseIndex;
            wheel.DragStarted += (_, index) => bars.SelectedIndex = index;
            wheel.MemberDragged += (_, e) =>
            {
                var member = _palette[e.Index];
                ApplyBase(ColorHarmony.BaseFor(Preset, e.Index, new HsvColor(e.Hue, e.Saturation, member.Value)));
            };

            brightnessSlider.ValueChanged += (_, value) => ApplyBase(Base with { Value = value });

            miniColor.Canceled += (_, _) => flyoutPopup.IsOpen = false;
            flyoutPopup.Closed += (_, _) =>
            {
                _flyoutIndex = -1;
                UpdateHighlight();
            };

            LoadHarmony(Preset, Base);
            _seeded = false;
        }

        /// <summary>Shows <paramref name="preset"/> generated from <paramref name="baseColor"/>, with
        /// the base bar selected. What the window's <see cref="ColorDialog.ShowInPalette"/> calls.</summary>
        public void LoadHarmony(HarmonyPreset preset, HsvColor baseColor)
        {
            _seeded = true;
            flyoutPopup.IsOpen = false;
            Preset = preset;
            ApplyBase(baseColor);
            UpdatePresetButtons();
            bars.SelectedIndex = ColorHarmony.BaseIndex;
        }

        /// <summary>Switches the preset, keeping the base (a preset button, or a keyboard move
        /// through the grid). The selected bar stays selected.</summary>
        public void SetPreset(HarmonyPreset preset)
        {
            if (preset == Preset)
                return;

            flyoutPopup.IsOpen = false;
            Preset = preset;
            ApplyBase(Base);
            UpdatePresetButtons();
        }

        protected internal override void OnShown()
        {
            if (_seeded)
                return;

            var seed = Host?.InitialColor is { } initial ? HsvColor.FromColor(initial) : DefaultBase;
            LoadHarmony(Preset, seed.Saturation < MinSeedSaturation ? DefaultBase : seed);
        }

        protected internal override bool HandleEscape()
        {
            if (!flyoutPopup.IsOpen)
                return false;

            miniColor.Cancel();
            return true;
        }

        protected internal override bool AcceptsEyedropper => true;

        /// <summary>The top bar's eyedropper sets the base, live, with the base bar selected so the
        /// Selected chip shows the sample; a cancel puts back the base and the selection.</summary>
        protected internal override void BeginEyedropper()
        {
            flyoutPopup.IsOpen = false;
            _eyedropperRestore = (Base, bars.SelectedIndex);
            bars.SelectedIndex = ColorHarmony.BaseIndex;
        }

        protected internal override void PreviewEyedropper(Color sampled) => ApplyBase(HsvColor.FromColor(sampled));

        protected internal override void EndEyedropper(Color? picked)
        {
            if (picked is { } color)
            {
                ApplyBase(HsvColor.FromColor(color));
            }
            else if (_eyedropperRestore is { } restore)
            {
                ApplyBase(restore.Base);
                bars.SelectedIndex = restore.SelectedIndex;
            }

            _eyedropperRestore = null;
        }

        /// <summary>Makes <paramref name="baseColor"/> the base and regenerates everything shown
        /// from it: the bars, the markers, the brightness strip and the preset glyphs.</summary>
        private void ApplyBase(HsvColor baseColor)
        {
            Base = baseColor.Normalized();
            _palette = ColorHarmony.Generate(Preset, Base);

            bars.Colors = _palette.Select(c => c.ToColor()).ToArray();
            wheel.Palette = _palette;

            brightnessSlider.Value = Base.Value;
            brightnessSlider.SliderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.Black, 0),
                    new GradientStop((Base with { Value = 1 }).ToColor(), 1),
                },
            };

            foreach (var button in _presetButtons)
                ((HarmonyGlyph)button.Content).BaseHue = Base.Hue;

            SyncSelection();
        }

        private void SyncSelection()
        {
            var colors = bars.Colors;
            var index = bars.SelectedIndex;
            SetSelectedColor(colors != null && index >= 0 && index < colors.Count ? colors[index] : null);
            UpdateHighlight();
        }

        /// <summary>The halo goes to the bar being edited in the flyout, else the one under the
        /// pointer, else the selected one.</summary>
        private void UpdateHighlight()
        {
            wheel.HighlightIndex = _flyoutIndex >= 0 ? _flyoutIndex
                : bars.HoveredIndex >= 0 ? bars.HoveredIndex
                : bars.SelectedIndex;
        }

        private RadioButton CreatePresetButton(HarmonyPreset preset)
        {
            var name = ColorHarmony.DisplayName(preset);
            var button = new RadioButton
            {
                Content = new HarmonyGlyph
                {
                    Preset = preset,
                    [!HarmonyGlyph.LineBrushProperty] = new DynamicResourceExtension("ColorDialogGlyphLineBrush"),
                },
                [!ThemeProperty] = new DynamicResourceExtension("HarmonyPresetButton"),
            };
            ToolTip.SetTip(button, name);
            ColorDialogTips.Attach(button);
            AutomationProperties.SetName(button, name);
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true)
                    SetPreset(preset);
            };
            return button;
        }

        /// <summary>Checks the current preset's button and names it in the label. Only the checked
        /// button is a tab stop: the grid is one radio group, entered once and walked with arrows.</summary>
        private void UpdatePresetButtons()
        {
            for (var i = 0; i < _presetButtons.Length; i++)
            {
                var on = ColorHarmony.Presets[i] == Preset;
                _presetButtons[i].IsChecked = on;
                _presetButtons[i].IsTabStop = on;
            }

            harmonyLabel.Inlines = new InlineCollection
            {
                new Run("Harmony · "),
                new Run(ColorHarmony.DisplayName(Preset))
                {
                    FontWeight = FontWeight.SemiBold,
                    [!TextElement.ForegroundProperty] = new DynamicResourceExtension("ColorDialogTextBrush"),
                },
            };
        }

        /// <summary>Arrow keys walk the 4 x 2 grid, selecting as they go (a radio group): Left and
        /// Right step through all eight in order, wrapping; Up and Down swap rows.</summary>
        private void OnPresetKeyDown(object sender, KeyEventArgs e)
        {
            var count = _presetButtons.Length;
            var index = Array.IndexOf(ColorHarmony.Presets.ToArray(), Preset);
            var next = e.Key switch
            {
                Key.Left => (index + count - 1) % count,
                Key.Right => (index + 1) % count,
                Key.Up or Key.Down => (index + count / 2) % count,
                Key.Home => 0,
                Key.End => count - 1,
                _ => -1,
            };

            if (next < 0)
                return;

            SetPreset(ColorHarmony.Presets[next]);
            _presetButtons[next].Focus(NavigationMethod.Directional);
            e.Handled = true;
        }

        /// <summary>Opens the mini color picker under bar <paramref name="index"/>'s hex, editing
        /// that member. The bar becomes the selection, and its marker keeps the halo while the
        /// flyout is open.</summary>
        private void OpenFlyout(int index, Control anchor)
        {
            bars.SelectedIndex = index;
            _flyoutIndex = index;
            UpdateHighlight();

            // the member's color as the flyout opens, and the base that made it: Cancel hands the
            // color back once the popup has already closed, and the palette is put back exactly
            // from the base rather than re-derived from a color that has been through 8-bit RGB
            var original = _palette[index].ToColor();
            var restoreBase = Base;

            miniColor.ParentWindow = Host;
            miniColor.Reset(original, color =>
            {
                if (color == original)
                    ApplyBase(restoreBase);
                else
                    MoveMember(index, color);
            });
            flyoutPopup.PlacementTarget = anchor;
            flyoutPopup.IsOpen = true;
        }

        /// <summary>The snapshot harness's flyout (<see cref="ColorDialogSnapshot"/>): open on bar
        /// <paramref name="index"/>, in the window's overlay layer so it renders with the window and
        /// never opens a popup window of its own.</summary>
        internal void OpenFlyoutForSnapshot(int index)
        {
            var anchor = bars.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.IsVisible && AutomationProperties.GetName(b) == "Edit " + ColorTextHelper.GetHex(_palette[index].ToColor()));
            if (anchor == null)
                return;

            flyoutPopup.ShouldUseOverlayLayer = true;
            OpenFlyout(index, anchor);
        }

        /// <summary>
        /// The flyout's color, live (it is Realtime): the edited member moves there and the other
        /// four re-derive from the preset, exactly as a marker drag does. The picker's own HSL is
        /// read for the hue rather than the RGB it hands over, so taking a member to gray does not
        /// swing the whole palette round to red. Alpha is not part of a harmony and is dropped.
        /// </summary>
        private void MoveMember(int index, Color color)
        {
            var hsl = miniColor.CurrentColor;
            var member = hsl != null && hsl.ToColor() == color
                ? new HsvColor(hsl.Hue, ColorWheel.HsvSaturation(hsl), ColorWheel.HsvValue(hsl))
                : HsvColor.FromColor(color);

            ApplyBase(ColorHarmony.BaseFor(Preset, index, member));
        }
    }
}
