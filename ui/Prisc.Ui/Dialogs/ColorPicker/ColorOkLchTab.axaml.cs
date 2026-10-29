using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Prisc.Interop;
using Prisc.UI.Converters;

namespace Prisc.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// The color dialog's OKLCH tab: oklch.com's controls in four equal quadrants. The first holds
    /// the color itself (its swatch, hex and <c>oklch()</c> text) and alpha; the other three each
    /// edit one of lightness, chroma and hue, with a gamut chart that runs along that channel above
    /// its field and slider (lightness across, chroma up, hue across).
    /// </summary>
    /// <remarks>
    /// The tab edits the OKLCH value itself, which may lie outside sRGB. Its Selected color — the
    /// swatch, the hex, and what OK returns — is the nearest sRGB color (chroma lowered until it
    /// fits, <see cref="OkLchColor.ToColor"/>), and the swatch says so when that differs from what
    /// was asked for. Slider tracks, like the charts, are left empty where sRGB cannot follow.
    /// </remarks>
    public partial class ColorOkLchTab : ColorDialogTab
    {
        private const int TrackSamples = 256;

        private OkLchColor _color = new(1, 0, 0);

        // false while UpdateFields writes the boxes, so the echo is not read back as input
        private bool _handleTextEvents;

        // the color to put back if an eyedropper drag ends without a pick
        private OkLchColor? _eyedropperRestore;

        // one row of pixels per slider, repainted in place as the other channels move
        private readonly WriteableBitmap _trackL = NewTrack(), _trackC = NewTrack(), _trackH = NewTrack();

        public ColorOkLchTab()
        {
            InitializeComponent();

            sliderC.ValueMax = OkLchColor.ChromaMax;

            chartL.Picked += OnChartPicked;
            chartC.Picked += OnChartPicked;
            chartH.Picked += OnChartPicked;

            // ValueChanged only fires on user drag, so syncing slider positions back in Update
            // cannot loop
            sliderL.ValueChanged += (_, v) => Set(_color with { L = v / 100 });
            sliderC.ValueChanged += (_, v) => Set(_color with { C = v });
            sliderH.ValueChanged += (_, v) => Set(_color with { H = v });
            sliderA.ValueChanged += (_, v) => Set(_color with { Alpha = v });

            // value guard: the hex is the sRGB fallback, so taking it back as input would pull an
            // out-of-gamut color into sRGB and lose the hue of a gray; only a different color counts
            HandleSet(txtHex, ColorTextHelper.FromHex, c =>
            {
                if (c != _color.ToColor())
                    SetColor(c);
            });
            HandleSet(txtOkLch, ParseOkLch, Set);
            HandleSet(txtL, ParsePercent, v => Set(_color with { L = Math.Clamp(v / 100, 0, 1) }));
            HandleSet(txtC, ParseNumber, v => Set(_color with { C = Math.Clamp(v, 0, OkLchColor.ChromaMax) }));
            HandleSet(txtH, ParseNumber, v => Set(_color with { H = OkLchColor.NormalizeHue(v) }));
            HandleSet(txtA, ParsePercent, v => Set(_color with { Alpha = Math.Clamp(v / 100, 0, 1) }));

            btnCopyHex.Click += async (_, _) => await Copy(ColorTextHelper.GetHex(_color.ToColor()), copyHexIcon, btnCopyHex);
            btnCopyOkLch.Click += async (_, _) => await Copy(_color.ToString(), copyOkLchIcon, btnCopyOkLch);
            ColorDialogTips.Attach(btnCopyHex);
            ColorDialogTips.Attach(btnCopyOkLch);

            Update();
        }

        /// <summary>Loads <paramref name="color"/> as the current color. A gray keeps the hue the tab
        /// had, as the Simple tab's does, so dragging chroma back up returns to it.</summary>
        public void SetColor(Color color)
        {
            var next = OkLchColor.FromColor(color);
            if (next.IsAchromatic)
                next = next with { H = _color.H };
            Set(next);
        }

        protected internal override bool AcceptsEyedropper => true;

        protected internal override void BeginEyedropper() => _eyedropperRestore = _color;

        protected internal override void PreviewEyedropper(Color sampled) => SetColor(sampled);

        protected internal override void EndEyedropper(Color? picked)
        {
            if (picked is { } color)
                SetColor(color);
            else if (_eyedropperRestore is { } restore)
                Set(restore);

            _eyedropperRestore = null;
        }

        private void OnChartPicked(object sender, EventArgs e)
        {
            var chart = (OkLchChart)sender;
            Set(_color with { L = chart.Lightness, C = chart.Chroma, H = chart.Hue });
        }

        private void Set(OkLchColor color)
        {
            _color = color;
            Update();
        }

        /// <summary>Moves every chart marker and slider to the current color, repaints the tracks
        /// that depend on it, refreshes the swatch and fields, and reports the new selection.</summary>
        private void Update()
        {
            var (l, c, h) = (_color.L, _color.C, _color.H);
            var mapped = _color.ToColor(out var inGamut);
            var opaque = Color.FromRgb(mapped.R, mapped.G, mapped.B);

            chartL.SetValues(l, c, h);
            chartC.SetValues(l, c, h);
            chartH.SetValues(l, c, h);

            sliderL.SliderBrush = Track(_trackL, OkLchChannel.Lightness, l, c, h);
            sliderC.SliderBrush = Track(_trackC, OkLchChannel.Chroma, l, c, h);
            sliderH.SliderBrush = Track(_trackH, OkLchChannel.Hue, l, c, h);
            sliderA.SliderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, opaque.R, opaque.G, opaque.B), 0), new GradientStop(opaque, 1) },
            };

            sliderL.Value = l * 100;
            sliderC.Value = c;
            sliderH.Value = h;
            sliderA.Value = _color.Alpha;

            swatch.Background = new SolidColorBrush(mapped);
            gamutNote.IsVisible = !inGamut;
            // a translucent fill shows the light checkerboard through it, so judge the ink on that
            var a = mapped.A / 255d;
            byte Over(byte v) => (byte)Math.Round(v * a + 230 * (1 - a));
            gamutNote.Foreground = ColorDialogInk.For(Color.FromRgb(Over(mapped.R), Over(mapped.G), Over(mapped.B)));

            UpdateFields();
            SetSelectedColor(mapped);
        }

        private static WriteableBitmap NewTrack() =>
            new(new PixelSize(TrackSamples, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        /// <summary>Paints a slider's track across its channel's range (left to right: lightness 0
        /// to 1, chroma 0 to <see cref="OkLchColor.ChromaMax"/>, hue 0 to 360) at the other two
        /// channels' values, transparent wherever sRGB has no such color: one native call
        /// (<see cref="OkLch.PaintTrack"/>).</summary>
        private static IBrush Track(WriteableBitmap bitmap, OkLchChannel channel, double l, double c, double h)
        {
            var row = new int[TrackSamples];
            OkLch.PaintTrack(channel, l, c, h, MemoryMarshal.Cast<int, uint>(row.AsSpan()));

            using (var buffer = bitmap.Lock())
                Marshal.Copy(row, 0, buffer.Address, TrackSamples);

            // a fresh brush each time, so the slider sees a change and repaints
            return new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        }

        private void UpdateFields(bool skipFocused = true)
        {
            _handleTextEvents = false;
            if (!skipFocused || !txtHex.IsFocused) txtHex.Text = ColorTextHelper.GetHex(_color.ToColor());
            if (!skipFocused || !txtOkLch.IsFocused) txtOkLch.Text = _color.ToString();
            if (!skipFocused || !txtL.IsFocused) txtL.Text = OkLchColor.FormatLightness(_color.L);
            if (!skipFocused || !txtC.IsFocused) txtC.Text = OkLchColor.FormatChroma(_color.C);
            if (!skipFocused || !txtH.IsFocused) txtH.Text = OkLchColor.FormatHue(_color.H);
            if (!skipFocused || !txtA.IsFocused) txtA.Text = OkLchColor.FormatAlpha(_color.Alpha);
            _handleTextEvents = true;
        }

        private async Task Copy(string text, LineIcon icon, Button button)
        {
            if (Host == null)
                return;

            await Host.CopyTextAsync(text);
            icon.ShowCopied();
            ColorSimpleTab.FlashCopied(button);
        }

        private static OkLchColor ParseOkLch(string text) =>
            OkLchColor.TryParse(text, out var color) ? color : throw new FormatException("Invalid oklch color");

        // the fields show CSS numbers ("0.1574") in any locale, so read them back the same way; a
        // non-finite entry ("NaN", "1e999") is refused like any other bad text and snaps back
        private static double ParseNumber(string text) =>
            OkLchColor.TryParseNumber(text, out var value) ? value : throw new FormatException("Invalid number");

        private static double ParsePercent(string text) => ParseNumber((text ?? "").Trim().TrimEnd('%'));

        private void HandleSet<T>(TextBox txt, Func<string, T> parse, Action<T> set)
        {
            txt.TextChanged += (s, e) =>
            {
                try
                {
                    // TextChanged is dispatcher-posted, so the flag alone cannot mask programmatic
                    // writes; UpdateFields only writes unfocused boxes, so a change while unfocused
                    // is always an echo (see ColorSimpleTab.HandleSet)
                    if (!_handleTextEvents || !txt.IsFocused) return;
                    set(parse(txt.Text));
                }
                catch {; }
            };

            // the value is live while typing; leaving the field settles its text to the value
            // actually in effect (a half-typed or out-of-range entry snaps back)
            txt.LostFocus += (s, e) => UpdateFields(false);
        }
    }
}
