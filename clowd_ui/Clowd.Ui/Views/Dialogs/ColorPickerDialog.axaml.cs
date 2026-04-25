using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Clowd.Ui.Controls.ColorPicker;

namespace Clowd.Ui.Views.Dialogs;

public partial class ColorPickerDialog : Window
{
    private readonly bool _asDialog;
    private readonly HslRgbColor _currentColor;
    private readonly HslRgbColor _previousColor;

    private bool _handleTextEvents;
    private bool _syncingFromModel;

    private Rectangle? _prevCurrentFill;
    private Path? _prevOldPath;

    public ColorPickerDialog() : this(null, true) { }

    public ColorPickerDialog(Color initial)
        : this(HslRgbColor.FromColor(initial), true) { }

    public ColorPickerDialog(HslRgbColor? previous, bool asDialog)
    {
        _asDialog = asDialog;
        InitializeComponent();

        if (previous != null)
        {
            _previousColor = previous.Clone();
            _currentColor = previous.Clone();
        }
        else
        {
            _previousColor = HslRgbColor.Transparent;
            _currentColor = HslRgbColor.White;
        }

        _currentColor.PropertyChanged += OnCurrentColorPropertyChanged;

        BuildPalette();
        BuildPrevSwatch();
        WireTextBoxes();
        WireSliders();
        Wheel.CurrentColor = _currentColor;

        if (!_asDialog)
        {
            BtnOK.IsVisible = false;
            BtnCancel.Content = "Close";
            Title = "Clowd - Color Viewer";
            BtnCopyHex.IsEnabled = true;
            BtnCopyRgb.IsEnabled = true;
            BtnCopyHsl.IsEnabled = true;
        }
        else
        {
            Title = "Clowd - Color Picker";
            BtnCopyHex.IsEnabled = false;
            BtnCopyRgb.IsEnabled = false;
            BtnCopyHsl.IsEnabled = false;
        }

        UpdateBrushes();
    }

    public Task<Color?> ShowDialogAsync(Window owner) => ShowDialog<Color?>(owner);

    // ---- Construction helpers ----

    private void BuildPalette()
    {
        foreach (var c in PaintPalette.Colors)
        {
            var item = new ColorPaletteItem(c);
            item.Clicked += OnPaletteItemClicked;
            ColorPaletteHost.Children.Add(item);
        }
    }

    private void BuildPrevSwatch()
    {
        _prevCurrentFill = new Rectangle
        {
            Fill = new SolidColorBrush(_currentColor.ToColor()),
        };

        _prevOldPath = new Path
        {
            Data = Geometry.Parse("M 0,0 H 60 L 48,28 H 0 Z"),
            Fill = new SolidColorBrush(_previousColor.ToColor()),
        };
        _prevOldPath.PointerPressed += OnPrevColorClicked;

        PrevColorHost.Children.Add(_prevCurrentFill);
        PrevColorHost.Children.Add(_prevOldPath);
    }

    private void WireTextBoxes()
    {
        HandleSet(TxtHex, ColorTextHelper.FromHex, ApplyRgbFromColor);
        HandleSet(TxtR, s => int.Parse(s, CultureInfo.InvariantCulture), v => _currentColor.R = v);
        HandleSet(TxtG, s => int.Parse(s, CultureInfo.InvariantCulture), v => _currentColor.G = v);
        HandleSet(TxtB, s => int.Parse(s, CultureInfo.InvariantCulture), v => _currentColor.B = v);
        HandleSet(TxtA, s => double.Parse(s, CultureInfo.InvariantCulture), v => _currentColor.Alpha = v / 100d);
        HandleSet(TxtH, s => double.Parse(s, CultureInfo.InvariantCulture), v => _currentColor.Hue = v);
        HandleSet(TxtS, s => double.Parse(s, CultureInfo.InvariantCulture), v => _currentColor.Saturation = v / 100d);
        HandleSet(TxtL, s => double.Parse(s, CultureInfo.InvariantCulture), v => _currentColor.Lightness = v / 100d);

        foreach (var tb in new[] { TxtHex, TxtR, TxtG, TxtB, TxtA, TxtH, TxtS, TxtL })
        {
            tb.GotFocus += (_, _) => tb.SelectAll();
        }
    }

    private void HandleSet<T>(TextBox tb, Func<string, T> parse, Action<T> apply)
    {
        tb.TextChanged += (_, _) =>
        {
            if (!_handleTextEvents) return;
            try { apply(parse(tb.Text ?? string.Empty)); }
            catch { /* ignore parse errors while typing */ }
        };

        tb.LostFocus += (_, _) => UpdateTextComponents(skipFocused: false);
    }

    private void ApplyRgbFromColor(Color c)
    {
        // Mutate _currentColor in place so subscribers stay attached.
        _currentColor.R = c.R;
        _currentColor.G = c.G;
        _currentColor.B = c.B;
        _currentColor.Alpha = c.A / 255d;
    }

    private void WireSliders()
    {
        WireSlider(SldR, v => _currentColor.R = (int)Math.Round(v));
        WireSlider(SldG, v => _currentColor.G = (int)Math.Round(v));
        WireSlider(SldB, v => _currentColor.B = (int)Math.Round(v));
        WireSlider(SldA, v => _currentColor.Alpha = v);
        WireSlider(SldH, v => _currentColor.Hue = v);
        WireSlider(SldS, v => _currentColor.Saturation = v);
        WireSlider(SldL, v => _currentColor.Lightness = v);
    }

    private void WireSlider(ColorSlider slider, Action<double> apply)
    {
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != ColorSlider.ValueProperty || _syncingFromModel) return;
            apply((double)(e.NewValue ?? 0d));
        };
    }

    // ---- Model -> UI ----

    private void OnCurrentColorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _syncingFromModel = true;
        try
        {
            UpdateBrushes();
        }
        finally
        {
            _syncingFromModel = false;
        }
    }

    private void UpdateBrushes()
    {
        var rgb = _currentColor.ToColor();

        // R / G / B / A: 2-stop linear gradients matching WPF UpdateBrushes.
        SldR.SliderBrush = MakeLinearBrush(
            Color.FromArgb(rgb.A, 0,   rgb.G, rgb.B),
            Color.FromArgb(rgb.A, 255, rgb.G, rgb.B));
        SldG.SliderBrush = MakeLinearBrush(
            Color.FromArgb(rgb.A, rgb.R, 0,   rgb.B),
            Color.FromArgb(rgb.A, rgb.R, 255, rgb.B));
        SldB.SliderBrush = MakeLinearBrush(
            Color.FromArgb(rgb.A, rgb.R, rgb.G, 0),
            Color.FromArgb(rgb.A, rgb.R, rgb.G, 255));
        SldA.SliderBrush = MakeLinearBrush(
            Color.FromArgb(0,   rgb.R, rgb.G, rgb.B),
            Color.FromArgb(255, rgb.R, rgb.G, rgb.B));

        // S: gray -> fully saturated at current hue.
        var hslSat = _currentColor.Clone();
        hslSat.Saturation = 1;
        SldS.SliderBrush = MakeLinearBrush(
            Color.FromArgb(rgb.A, 128, 128, 128),
            hslSat.ToColor());

        // L: black -> hue at L=0.5 -> white.
        var hslMid = _currentColor.Clone();
        hslMid.Lightness = 0.5;
        SldL.SliderBrush = new LinearGradientBrush
        {
            StartPoint = RelativePoint.Parse("0%,50%"),
            EndPoint   = RelativePoint.Parse("100%,50%"),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(rgb.A, 0, 0, 0), 0),
                new GradientStop(hslMid.ToColor(), 0.5),
                new GradientStop(Color.FromArgb(rgb.A, 255, 255, 255), 1),
            },
        };

        // H: rainbow.
        SldH.SliderBrush = MakeHueRainbow();

        // Sync slider Values back from the model (guarded by _syncingFromModel).
        SldR.Value = _currentColor.R;
        SldG.Value = _currentColor.G;
        SldB.Value = _currentColor.B;
        SldA.Value = _currentColor.Alpha;
        SldH.Value = _currentColor.Hue;
        SldS.Value = _currentColor.Saturation;
        SldL.Value = _currentColor.Lightness;

        LblRgb.Text = ColorTextHelper.GetRgb(_currentColor);
        LblHsl.Text = ColorTextHelper.GetHsl(_currentColor);

        if (_prevCurrentFill != null)
            _prevCurrentFill.Fill = new SolidColorBrush(rgb);

        UpdateTextComponents();
    }

    private void UpdateTextComponents(bool skipFocused = true)
    {
        _handleTextEvents = false;
        try
        {
            var c = _currentColor;
            if (!skipFocused || !TxtHex.IsFocused) TxtHex.Text = ColorTextHelper.GetHex(c);
            if (!skipFocused || !TxtR.IsFocused)   TxtR.Text   = c.R.ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !TxtG.IsFocused)   TxtG.Text   = c.G.ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !TxtB.IsFocused)   TxtB.Text   = c.B.ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !TxtA.IsFocused)   TxtA.Text   = Math.Round(c.Alpha * 100).ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !TxtH.IsFocused)   TxtH.Text   = Math.Round(c.Hue).ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !TxtS.IsFocused)   TxtS.Text   = Math.Round(c.Saturation * 100).ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !TxtL.IsFocused)   TxtL.Text   = Math.Round(c.Lightness * 100).ToString(CultureInfo.InvariantCulture);

            if (_prevOldPath != null)
            {
                var revertable = _previousColor != HslRgbColor.Transparent && _previousColor != c;
                _prevOldPath.Cursor = revertable
                    ? new Cursor(StandardCursorType.Hand)
                    : new Cursor(StandardCursorType.Arrow);
            }
        }
        finally
        {
            _handleTextEvents = true;
        }
    }

    // ---- Brush helpers ----

    private static LinearGradientBrush MakeLinearBrush(Color start, Color end) => new()
    {
        StartPoint = RelativePoint.Parse("0%,50%"),
        EndPoint   = RelativePoint.Parse("100%,50%"),
        GradientStops =
        {
            new GradientStop(start, 0),
            new GradientStop(end, 1),
        },
    };

    private static LinearGradientBrush MakeHueRainbow()
    {
        const double s = 1d / 6d;
        return new LinearGradientBrush
        {
            StartPoint = RelativePoint.Parse("0%,50%"),
            EndPoint   = RelativePoint.Parse("100%,50%"),
            GradientStops =
            {
                new GradientStop(Colors.Red,     0),
                new GradientStop(Colors.Yellow,  s),
                new GradientStop(Colors.Lime,    s * 2),
                new GradientStop(Colors.Cyan,    s * 3),
                new GradientStop(Colors.Blue,    s * 4),
                new GradientStop(Colors.Magenta, s * 5),
                new GradientStop(Colors.Red,     s * 6),
            },
        };
    }

    // ---- Click handlers ----

    private void OnPaletteItemClicked(object? sender, ColorSelectedEventArgs e)
    {
        ApplyRgbFromColor(e.SelectedColor);
        if (e.ClickCount >= 2)
            Close((Color?)_currentColor.ToColor());
    }

    private void OnPrevColorClicked(object? sender, PointerPressedEventArgs e)
    {
        if (_previousColor == HslRgbColor.Transparent) return;
        _currentColor.R = _previousColor.R;
        _currentColor.G = _previousColor.G;
        _currentColor.B = _previousColor.B;
        _currentColor.Alpha = _previousColor.Alpha;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
        => Close((Color?)_currentColor.ToColor());

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close((Color?)null);

    private async void OnCopyHex(object? sender, RoutedEventArgs e)
        => await CopyAndClose(ColorTextHelper.GetHex(_currentColor));

    private async void OnCopyRgb(object? sender, RoutedEventArgs e)
        => await CopyAndClose(ColorTextHelper.GetRgb(_currentColor));

    private async void OnCopyHsl(object? sender, RoutedEventArgs e)
        => await CopyAndClose(ColorTextHelper.GetHsl(_currentColor));

    private async Task CopyAndClose(string text)
    {
        var clipboard = Clipboard;
        if (clipboard != null)
        {
            // Avalonia 12: SetTextAsync was removed; use the DataTransfer API
            // with the built-in DataFormat.Text format.
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, text);
            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
        }
        Close((Color?)null);
    }
}
