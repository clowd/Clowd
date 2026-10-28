using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Prisc.Config;
using Prisc.Drawing;
using Prisc.Drawing.Graphics;
using Prisc.UI.Converters;
using Prisc.UI.Helpers;

namespace Prisc.UI.Controls
{
    /// <summary>A style value a field edits, named apart from where it is stored: the canvas
    /// (whose properties proxy the current tool's settings or the selected graphic) calls the fill
    /// <c>ObjectFill</c>, a <see cref="SavedToolSettings"/> calls it <c>FillColor</c>.</summary>
    public enum StyleValue
    {
        Color,
        Fill,
        AutoFill,
        Stroke,
        FontFamily,
        FontSize,
        FontStyle,
        FontWeight,
        FontStretch,
        Scale,
        Angle,
        BlurRadius,
        CornerRadius,
        DashStyle,
        ObscureMode,
    }

    /// <summary>One field of a style: the skill that brings it on, its label, the values it edits
    /// (and a reset puts back together), and for the editor bar the value its reset dot returns
    /// to, when it has one.</summary>
    public sealed record ToolStyleField(Skill Skill, string Label, StyleValue[] Values, object BarDefault = null);

    /// <summary>
    /// The style fields of a drawing tool or graphic — which control each skill gets, its label,
    /// and what it is bound to — built once for both places that edit a style: the image editor's
    /// property bar (each field a horizontal item of the wrapping bar) and the settings window's
    /// tool defaults (each field a row of a form). The hosts only lay the fields out.
    /// </summary>
    /// <remarks>
    /// Sizes and chrome are left to the host's styles, keyed on the classes the controls carry
    /// (<c>swatchOuter</c>/<c>colorSwatch</c>/<c>colorFill</c>, <c>fieldButton</c>,
    /// <c>wideField</c>): the editor sizes them per chrome (Modern/Compact), which a local value
    /// set here would outrank.
    /// </remarks>
    public sealed class ToolStyleFields
    {
        /// <summary>Every field, in property-bar order.</summary>
        public static readonly IReadOnlyList<ToolStyleField> All = new ToolStyleField[]
        {
            new(Skill.Stroke, "Stroke", new[] { StyleValue.Stroke }),
            new(Skill.Color, "Color", new[] { StyleValue.Color }),
            new(Skill.Fill, "Fill", new[] { StyleValue.Fill, StyleValue.AutoFill }),
            new(Skill.Font, "Font", new[] { StyleValue.FontFamily, StyleValue.FontSize, StyleValue.FontStyle, StyleValue.FontWeight, StyleValue.FontStretch }),
            new(Skill.FontFamily, "Font", new[] { StyleValue.FontFamily }),
            new(Skill.Scale, "Scale", new[] { StyleValue.Scale }, BarDefault: 1d),
            new(Skill.Angle, "Angle", new[] { StyleValue.Angle }, BarDefault: 0d),
            new(Skill.BlurRadius, "Blur", new[] { StyleValue.BlurRadius }),
            new(Skill.Radius, "Radius", new[] { StyleValue.CornerRadius }),
            new(Skill.DashStyle, "Dash", new[] { StyleValue.DashStyle }),
            new(Skill.ObscureMode, "Mode", new[] { StyleValue.ObscureMode }),
        };

        /// <summary>Where the values live on a <see cref="DrawingCanvas"/>.</summary>
        public static readonly IReadOnlyDictionary<StyleValue, string> CanvasPaths = new Dictionary<StyleValue, string>
        {
            [StyleValue.Color] = nameof(DrawingCanvas.ObjectColor),
            [StyleValue.Fill] = nameof(DrawingCanvas.ObjectFill),
            [StyleValue.AutoFill] = nameof(DrawingCanvas.ObjectFillAuto),
            [StyleValue.Stroke] = nameof(DrawingCanvas.LineWidth),
            [StyleValue.FontFamily] = nameof(DrawingCanvas.TextFontFamilyName),
            [StyleValue.FontSize] = nameof(DrawingCanvas.TextFontSize),
            [StyleValue.FontStyle] = nameof(DrawingCanvas.TextFontStyle),
            [StyleValue.FontWeight] = nameof(DrawingCanvas.TextFontWeight),
            [StyleValue.FontStretch] = nameof(DrawingCanvas.TextFontStretch),
            [StyleValue.Scale] = nameof(DrawingCanvas.ObjectScale),
            [StyleValue.Angle] = nameof(DrawingCanvas.ObjectAngle),
            [StyleValue.BlurRadius] = nameof(DrawingCanvas.BlurRadius),
            [StyleValue.CornerRadius] = nameof(DrawingCanvas.CornerRadius),
            [StyleValue.DashStyle] = nameof(DrawingCanvas.DashStyle),
            [StyleValue.ObscureMode] = nameof(DrawingCanvas.ObscureMode),
        };

        /// <summary>Where the values live on a <see cref="SavedToolSettings"/> (no angle: a tool
        /// cannot preset one).</summary>
        public static readonly IReadOnlyDictionary<StyleValue, string> SettingsPaths = new Dictionary<StyleValue, string>
        {
            [StyleValue.Color] = nameof(SavedToolSettings.ObjectColor),
            [StyleValue.Fill] = nameof(SavedToolSettings.FillColor),
            [StyleValue.AutoFill] = nameof(SavedToolSettings.AutoFill),
            [StyleValue.Stroke] = nameof(SavedToolSettings.LineWidth),
            [StyleValue.FontFamily] = nameof(SavedToolSettings.FontFamily),
            [StyleValue.FontSize] = nameof(SavedToolSettings.FontSize),
            [StyleValue.FontStyle] = nameof(SavedToolSettings.FontStyle),
            [StyleValue.FontWeight] = nameof(SavedToolSettings.FontWeight),
            [StyleValue.FontStretch] = nameof(SavedToolSettings.FontStretch),
            [StyleValue.Scale] = nameof(SavedToolSettings.Scale),
            [StyleValue.BlurRadius] = nameof(SavedToolSettings.BlurRadius),
            [StyleValue.CornerRadius] = nameof(SavedToolSettings.CornerRadius),
            [StyleValue.DashStyle] = nameof(SavedToolSettings.DashStyle),
            [StyleValue.ObscureMode] = nameof(SavedToolSettings.ObscureMode),
        };

        private readonly object _source;
        private readonly IReadOnlyDictionary<StyleValue, string> _paths;
        private readonly Func<Skill, BindingBase> _hasSkill;
        private readonly Action<Control, Color, Action<Color>> _openColorPicker;

        /// <param name="source">The object the fields bind to.</param>
        /// <param name="paths">Where each value lives on <paramref name="source"/>.</param>
        /// <param name="hasSkill">A binding to whether a skill is on, for what shows within a
        /// field (the fill's Auto box) and, in the bar, for the field as a whole.</param>
        /// <param name="openColorPicker">Opens the host's mini color picker at a swatch, seeded
        /// with the current color, writing every change through the callback.</param>
        public ToolStyleFields(object source, IReadOnlyDictionary<StyleValue, string> paths,
            Func<Skill, BindingBase> hasSkill, Action<Control, Color, Action<Color>> openColorPicker)
        {
            _source = source;
            _paths = paths;
            _hasSkill = hasSkill;
            _openColorPicker = openColorPicker;
        }

        public object Get(StyleValue value) => _source.GetType().GetProperty(_paths[value])!.GetValue(_source);

        public void Set(StyleValue value, object v) => _source.GetType().GetProperty(_paths[value])!.SetValue(_source, v);

        private Binding Bind(StyleValue value, BindingMode mode = BindingMode.TwoWay, IValueConverter converter = null)
            => new(_paths[value]) { Source = _source, Mode = mode, Converter = converter };

        /// <summary>A field as an item of the editor's property bar: label, control and (for the
        /// fields that have one) reset dot side by side, shown while its skill is.</summary>
        public StackPanel BuildBarItem(ToolStyleField field)
        {
            var item = new StackPanel();
            item.Bind(Visual.IsVisibleProperty, _hasSkill(field.Skill));
            item.Children.Add(new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(BuildEditor(field));

            if (field.BarDefault != null)
            {
                var dot = new ResetDefaultButton { DefaultValue = field.BarDefault, Margin = new Thickness(4, 0, 0, 0) };
                dot.Bind(ResetDefaultButton.CurrentValueProperty, Bind(field.Values[0]));
                item.Children.Add(dot);
            }

            return item;
        }

        /// <summary>The control that edits <paramref name="field"/>, without a label.</summary>
        public Control BuildEditor(ToolStyleField field)
        {
            switch (field.Skill)
            {
                case Skill.Stroke:
                    return Spinner(StyleValue.Stroke, min: 0, max: null, step: 1, suffix: "px");
                case Skill.Color:
                    return Swatch(StyleValue.Color);
                case Skill.Fill:
                    return FillEditor();
                case Skill.Font:
                    return FontButton(familyOnly: false);
                case Skill.FontFamily:
                    return FontButton(familyOnly: true);
                case Skill.Scale:
                    return Spinner(StyleValue.Scale, GraphicStickyNote.MinScale, GraphicStickyNote.MaxScale, 0.1, "×", snap: false);
                case Skill.Angle:
                    return Spinner(StyleValue.Angle, min: -180, max: 180, step: 10, suffix: "°");
                case Skill.BlurRadius:
                    return Spinner(StyleValue.BlurRadius, min: 1, max: null, step: 1, suffix: "px");
                case Skill.Radius:
                {
                    // a window's corner radius runs to three digits plus the unit, which sits
                    // tight at the default field width
                    var spinner = Spinner(StyleValue.CornerRadius, min: 0, max: null, step: 1, suffix: "px");
                    spinner.Classes.Add("wideField");
                    return spinner;
                }
                case Skill.DashStyle:
                    return DropDown(StyleValue.DashStyle, DrawingCanvas.DashStyleValues, tip: null);
                case Skill.ObscureMode:
                    return DropDown(StyleValue.ObscureMode, DrawingCanvas.ObscureModeValues, "How obscured regions are rendered");
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field.Skill, "No style field for this skill.");
            }
        }

        private ThemedSpinner Spinner(StyleValue value, double? min, double? max, double step, string suffix, bool snap = true)
        {
            var spinner = new ThemedSpinner
            {
                Min = min,
                Max = max,
                SpinAmount = step,
                SnapToWholeNumber = snap,
                Suffix = suffix,
            };
            spinner.Bind(ThemedSpinner.ValueProperty, Bind(value));
            return spinner;
        }

        private ThemedDropDown DropDown(StyleValue value, System.Collections.IEnumerable items, string tip)
        {
            var dropDown = new ThemedDropDown { ItemsSource = items };
            dropDown.Bind(ThemedDropDown.SelectedItemProperty, Bind(value));
            if (tip != null)
                ToolTip.SetTip(dropDown, tip);
            return dropDown;
        }

        /// <summary>
        /// A color swatch: the color over a checkerboard (so alpha shows) inside a white then a
        /// black hairline, legible on any background. A plain Border rather than a Button (a
        /// button theme would fight the checker and the fill), so keyboard activation is wired by
        /// hand — Enter only: the editor swallows Space window-wide as the pan modifier.
        /// </summary>
        private Border Swatch(StyleValue value)
        {
            var swatch = new Border
            {
                Background = Util.CheckerBrushes.Light,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = true,
                Child = new Border(),
            };
            swatch.Classes.Add("colorSwatch");
            swatch.Child.Classes.Add("colorFill");
            ((Border)swatch.Child).Bind(Border.BackgroundProperty, Bind(value, BindingMode.OneWay, new ColorToBrushConverter()));

            void Open() => _openColorPicker(swatch, (Color)Get(value), c => Set(value, c));
            swatch.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                Open();
            };
            swatch.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                e.Handled = true;
                Open();
            };

            var outer = new Border
            {
                Margin = new Thickness(4, 0, 0, 0),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = swatch,
            };
            outer.Classes.Add("swatchOuter");
            return outer;
        }

        private Control FillEditor()
        {
            var swatch = Swatch(StyleValue.Fill);
            var auto = new CaptionedCheckBox { Caption = "Auto", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(auto, "New text picks its own pastel note-card fill");
            auto.Bind(CaptionedCheckBox.IsCheckedProperty, Bind(StyleValue.AutoFill));
            auto.Bind(Visual.IsVisibleProperty, _hasSkill(Skill.AutoFill));

            // auto-fill picks the fill itself, so there is no swatch to pick (only for the tool: a
            // selected text's fill stays editable, and the box is hidden then)
            swatch.Bind(Visual.IsVisibleProperty, new MultiBinding
            {
                Converter = new FuncMultiValueConverter<object, bool>(v =>
                {
                    var values = v.ToArray();
                    return values[0] is not true || values[1] is not true;
                }),
                Bindings = { _hasSkill(Skill.AutoFill), Bind(StyleValue.AutoFill, BindingMode.OneWay) },
            });

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { swatch, auto },
            };
        }

        /// <summary>The font button, its face shown in the font itself. The full font (text) picks
        /// face, size and style; family-only (sticky notes, which size their own text) the face.</summary>
        private Button FontButton(bool familyOnly)
        {
            var button = new Button();
            button.Classes.Add("fieldButton");
            button.Bind(ContentControl.ContentProperty, Bind(StyleValue.FontFamily, BindingMode.OneWay));
            button.Bind(TemplatedControl.FontFamilyProperty, Bind(StyleValue.FontFamily, BindingMode.OneWay, new StringToFontFamilyConverter()));

            if (familyOnly)
            {
                button.Click += async (_, _) =>
                {
                    var family = await NiceDialog.ShowFontFamilyDialogAsync(button, (string)Get(StyleValue.FontFamily));
                    if (family != null)
                        Set(StyleValue.FontFamily, family);
                };
                return button;
            }

            button.Bind(TemplatedControl.FontStretchProperty, Bind(StyleValue.FontStretch, BindingMode.OneWay));
            button.Bind(TemplatedControl.FontStyleProperty, Bind(StyleValue.FontStyle, BindingMode.OneWay));
            button.Click += async (_, _) =>
            {
                var result = await NiceDialog.ShowFontDialogAsync(button,
                    (string)Get(StyleValue.FontFamily),
                    (double)Get(StyleValue.FontSize),
                    (FontStyle)Get(StyleValue.FontStyle),
                    (FontWeight)Get(StyleValue.FontWeight));

                if (result != null)
                {
                    Set(StyleValue.FontFamily, result.TextFontFamilyName);
                    Set(StyleValue.FontSize, result.TextFontSize);
                    Set(StyleValue.FontStyle, result.TextFontStyle);
                    Set(StyleValue.FontWeight, result.TextFontWeight);
                }
            };
            return button;
        }
    }
}
