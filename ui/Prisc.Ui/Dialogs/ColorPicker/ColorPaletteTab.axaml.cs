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
    /// The color dialog's Palette tab (brief §7.2): a <see cref="HarmonyPalette"/>, a preset and a
    /// base generating five colors. Every edit — a marker dragged on the wheel, a color changed in
    /// the mini flyout, the brightness strip — moves one member (<see cref="HarmonyPalette.WithMember"/>):
    /// its hue turns the whole palette, so the preset's angles always hold, while its saturation and
    /// brightness are its own. Editing the base takes every member with it.
    /// </summary>
    /// <remarks>
    /// Selected color: the selected bar's, the base by default (brief §7.2). Dragging a marker or
    /// opening a bar's flyout selects that bar, so the top bar's Selected line, the copy buttons and
    /// the brightness strip follow whatever is being edited. The tab is seeded the first time it is shown, from the
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

        public HarmonyPalette Palette { get; private set; } = new(HarmonyPreset.SplitComplementary, DefaultBase);

        public HarmonyPreset Preset => Palette.Preset;

        private readonly RadioButton[] _presetButtons;

        // false until the first show or LoadHarmony, whichever comes first
        private bool _seeded;

        // the member the open flyout is editing, or -1
        private int _flyoutIndex = -1;

        // the palette and selected bar to put back if an eyedropper drag ends without a pick
        private (HarmonyPalette Palette, int SelectedIndex)? _eyedropperRestore;

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
            bars.SelectedIndexChanged += (_, _) =>
            {
                SyncSelection();
                UpdateBrightnessSlider();
            };
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
                var member = Palette.Colors[e.Index];
                Apply(Palette.WithMember(e.Index, new HsvColor(e.Hue, e.Saturation, member.Value)));
            };

            brightnessSlider.ValueChanged += (_, value) =>
            {
                var index = SelectedMember;
                Apply(Palette.WithMember(index, Palette.Colors[index] with { Value = value }));
            };

            miniColor.Canceled += (_, _) => flyoutPopup.IsOpen = false;
            flyoutPopup.Closed += (_, _) =>
            {
                _flyoutIndex = -1;
                UpdateHighlight();
            };

            LoadHarmony(Palette);
            _seeded = false;
        }

        /// <summary>Shows <paramref name="palette"/>, with the base bar selected. What the window's
        /// <see cref="ColorDialog.ShowInPalette"/> calls.</summary>
        public void LoadHarmony(HarmonyPalette palette)
        {
            _seeded = true;
            flyoutPopup.IsOpen = false;
            Apply(palette);
            UpdatePresetButtons();
            bars.SelectedIndex = ColorHarmony.BaseIndex;
        }

        /// <summary>Switches the preset, keeping the base (a preset button, or a keyboard move
        /// through the grid). Members' own tones start over from the new preset's. The selected bar
        /// stays selected.</summary>
        public void SetPreset(HarmonyPreset preset)
        {
            if (preset == Preset)
                return;

            flyoutPopup.IsOpen = false;
            Apply(Palette.WithPreset(preset));
            UpdatePresetButtons();
        }

        protected internal override void OnShown()
        {
            if (_seeded)
                return;

            var seed = Host?.InitialColor is { } initial ? HsvColor.FromColor(initial) : DefaultBase;
            LoadHarmony(new HarmonyPalette(Preset, seed.Saturation < MinSeedSaturation ? DefaultBase : seed));
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
        /// Selected chip shows the sample; a cancel puts back the palette and the selection.</summary>
        protected internal override void BeginEyedropper()
        {
            flyoutPopup.IsOpen = false;
            _eyedropperRestore = (Palette, bars.SelectedIndex);
            bars.SelectedIndex = ColorHarmony.BaseIndex;
        }

        protected internal override void PreviewEyedropper(Color sampled) => SampleBase(sampled);

        protected internal override void EndEyedropper(Color? picked)
        {
            if (picked is { } color)
            {
                SampleBase(color);
            }
            else if (_eyedropperRestore is { } restore)
            {
                Apply(restore.Palette);
                bars.SelectedIndex = restore.SelectedIndex;
            }

            _eyedropperRestore = null;
        }

        // the sampled color as the new base, the members' tones kept
        private void SampleBase(Color color) => Apply(Palette.WithBase(HsvColor.FromColor(color)));

        /// <summary>The member being edited: the selected bar's, the base when nothing is.</summary>
        private int SelectedMember =>
            bars.SelectedIndex is >= 0 and < ColorHarmony.ColorCount ? bars.SelectedIndex : ColorHarmony.BaseIndex;

        /// <summary>Shows <paramref name="palette"/>: the bars, the markers, the brightness strip and
        /// the preset glyphs.</summary>
        private void Apply(HarmonyPalette palette)
        {
            Palette = palette;

            bars.Colors = Palette.Colors.Select(c => c.ToColor()).ToArray();
            wheel.Palette = Palette.Colors;

            foreach (var button in _presetButtons)
                ((HarmonyGlyph)button.Content).BaseHue = Palette.Base.Hue;

            UpdateBrightnessSlider();
            SyncSelection();
        }

        /// <summary>The brightness strip is the selected member's: its value, over a ramp from black
        /// to that member at full brightness.</summary>
        private void UpdateBrightnessSlider()
        {
            var member = Palette.Colors[SelectedMember];
            brightnessSlider.Value = member.Value;
            brightnessSlider.SliderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.Black, 0),
                    new GradientStop((member with { Value = 1 }).ToColor(), 1),
                },
            };
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

            // the member's color as the flyout opens, and the palette it was in: Cancel hands the
            // color back once the popup has already closed, and the palette is put back exactly
            // rather than re-derived from a color that has been through 8-bit RGB
            var original = Palette.Colors[index].ToColor();
            var restore = Palette;

            miniColor.ParentWindow = Host;
            miniColor.Reset(original, color =>
            {
                if (color == original)
                    Apply(restore);
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
                .FirstOrDefault(b => b.IsVisible && AutomationProperties.GetName(b) == "Edit " + ColorTextHelper.GetHex(Palette.Colors[index].ToColor()));
            if (anchor == null)
                return;

            flyoutPopup.ShouldUseOverlayLayer = true;
            OpenFlyout(index, anchor);
        }

        /// <summary>
        /// The flyout's color, live (it is Realtime): the edited member moves there, exactly as a
        /// marker drag does (its hue turns the palette; its saturation and brightness are its own). The picker's own HSL is
        /// read for the hue rather than the RGB it hands over, so taking a member to gray does not
        /// swing the whole palette round to red. Alpha is not part of a harmony and is dropped.
        /// </summary>
        private void MoveMember(int index, Color color)
        {
            var hsl = miniColor.CurrentColor;
            var member = hsl != null && hsl.ToColor() == color
                ? new HsvColor(hsl.Hue, ColorWheel.HsvSaturation(hsl), ColorWheel.HsvValue(hsl))
                : HsvColor.FromColor(color);

            Apply(Palette.WithMember(index, member));
        }
    }
}
