using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Clowd.Config;

namespace Clowd.Util
{
    /// <summary>
    /// Repaints the Semi theme's blue ramp — and everything the theme paints out of it — with the
    /// accent the user picked in Settings (<see cref="SettingsGeneral.GetEffectiveAccentColor"/>),
    /// so the one color drives the whole app and not just the capture surfaces: primary button
    /// fills and label text, progress bars, selected and focused states, the editor's handles and
    /// focus rings.
    ///
    /// Overriding <c>SemiColorPrimary</c> alone does nothing for the controls: the theme resolves
    /// its per-control tokens (<c>ButtonSolidPrimaryBackground</c> and friends) out of the ramp
    /// when it is loaded, so by the time an override could be seen they already hold plain brushes.
    /// So the ramp is followed by value instead — every theme resource painted in one of the ten
    /// stock <c>SemiBlue</c> shades is re-published in the matching shade of the accent's own ramp.
    /// The overrides go into <see cref="Application.Resources"/>, which is searched ahead of the
    /// theme's dictionaries.
    ///
    /// Light and dark are built separately: Semi's ramp is not the same in both. The base is the
    /// same accent in each — it arrives darkened until white text on it reads, and that is what
    /// the solid buttons and checked boxes need in the dark theme too — lifted only if it would
    /// otherwise sink into the dark ground. Dark carries a second, lighter ramp for the tokens
    /// that paint accent text, since no one color can be both a fill under white text and text on
    /// a dark ground (see <see cref="BuildDarkTextRamp"/>).
    /// </summary>
    public static class AccentTheme
    {
        /// <summary>One resource to re-publish: where it lives, what it is called, which ramp step
        /// it was painted in, and how the theme holds it — a bare color, or a brush, whose Opacity
        /// has to be carried over. Semi tints with brush opacity rather than with the color's alpha
        /// (a selected row is the accent at 0.2), and a replacement brush at the default opacity
        /// turns every one of those washes into a solid block of accent.</summary>
        private readonly record struct Repaint(bool Dark, object Key, int Step, byte Alpha, double? Opacity);

        // Which ramp step each Semi primary token is fed from, matching how the stock theme wires
        // them. Dark has no pale steps to tint with, so its three "Light" tokens are the base at
        // a brush opacity (0.2 / 0.3 / 0.4 in the stock palette) — a wash over whatever is under
        // it rather than a lighter shade.
        private static readonly (string Name, int LightStep, int DarkStep, double DarkOpacity)[] PrimaryTokens =
        {
            ("SemiColorPrimary", 5, 5, 1),
            ("SemiColorPrimaryPointerover", 6, 6, 1),
            ("SemiColorPrimaryActive", 7, 7, 1),
            ("SemiColorPrimaryDisabled", 2, 2, 1),
            ("SemiColorPrimaryLight", 0, 5, 0.2),
            ("SemiColorPrimaryLightPointerover", 1, 5, 0.3),
            ("SemiColorPrimaryLightActive", 2, 5, 0.4),
        };

        private static List<Repaint> _plan;
        private static Color? _applied;
        private static bool _attached;

        /// <summary>Applies the current accent, and keeps applying it as the setting changes.</summary>
        public static void Attach()
        {
            Apply();

            var general = SettingsRoot.Current?.General;
            if (general == null || _attached)
                return;

            _attached = true;
            general.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(SettingsGeneral.AccentColor)
                    or nameof(SettingsGeneral.UseSystemAccentColor)
                    or nameof(SettingsGeneral.MaintainMinimumContrast))
                    Apply();
            };
        }

        /// <summary>Writes the accent over the theme's blue. Safe to call repeatedly: a fresh pair
        /// of variant dictionaries is published each time, and everything resolved through them
        /// repaints.</summary>
        public static void Apply()
        {
            var app = Application.Current;
            if (app == null)
                return;

            var accent = SettingsRoot.Current?.General?.GetEffectiveAccentColor() ?? AccentColors.Default;

            // the settings color picker streams a color per drag step, and most of them land on the
            // same contrast-corrected accent
            if (_applied == accent)
                return;

            _plan ??= BuildPlan(app);

            var lightRamp = BuildRamp(accent, dark: false);
            var darkRamp = BuildRamp(accent, dark: true);
            var darkText = BuildDarkTextRamp(darkRamp);

            // Filled off to the side and published in one assignment each. Writing the ~700 entries
            // into the live dictionaries instead raises a resource change per key, and the app
            // re-resolves every styled value on each one — which is seconds of frozen UI while the
            // color picker in Settings streams new colors.
            var light = new ResourceDictionary();
            var dark = new ResourceDictionary();

            WriteRamp(light, lightRamp, dark: false);
            WriteRamp(dark, darkRamp, dark: true);

            foreach (var repaint in _plan)
            {
                // One accent cannot be both a fill under white text and text on a dark ground:
                // darkened for the first, it has no contrast for the second (accent labels on the
                // default buttons sank into their gray). So on the dark theme anything the theme
                // paints *as* text takes the text ramp instead — the same hue, lifted until it
                // reads on the ground. Semi names those tokens consistently enough that the name
                // is the tell (the link colors being the one family spelled differently).
                var ramp = !repaint.Dark ? lightRamp
                    : IsTextKey(repaint.Key) ? darkText
                    : darkRamp;

                var color = ramp[repaint.Step];
                if (repaint.Alpha != 255)
                    color = Color.FromArgb(repaint.Alpha, color.R, color.G, color.B);

                var target = repaint.Dark ? dark : light;
                target[repaint.Key] = repaint.Opacity is { } opacity
                    ? new SolidColorBrush(color, opacity)
                    : color;
            }

            // The keyboard focus ring (Semi's AdornerLayerBorderBrush) is a pale accent tint in
            // the light theme and the raw accent in the dark one, where a darkened accent on a dark
            // ground all but disappears. The light tint reads on both, so dark borrows it.
            dark["AdornerLayerBorderBrush"] = new SolidColorBrush(lightRamp[2]);

            // The accent as *text or a line* on the window ground, for Clowd's own controls (the
            // theme's tokens get the same treatment by name, above): the accent itself in the light
            // theme, and the text ramp's base in the dark one, where the darkened accent that the
            // fills need would sink into the ground.
            light["ClowdAccentTextBrush"] = new SolidColorBrush(lightRamp[5]);
            dark["ClowdAccentTextBrush"] = new SolidColorBrush(darkText[5]);

            // Selected text in text boxes: Semi hard-codes a blue here (#0041C5 at 0.8) rather
            // than drawing it from the ramp, so the walk cannot find it. The selection foreground
            // is white, which the darkened accent is made for.
            light["TextBoxSelectionBackground"] = new SolidColorBrush(lightRamp[5], 0.8);
            dark["TextBoxSelectionBackground"] = new SolidColorBrush(darkRamp[5], 0.8);

            // The main window's selected navigation item. Semi's own choice (SemiColorPrimaryLight)
            // is the palest tint in the light theme, next to invisible on the window; one step up
            // reads. In the dark theme it is the accent itself, which as a solid block competes
            // with the primary button above the menu — so a wash of it over the ground instead.
            light["ClowdNavSelectedBrush"] = new SolidColorBrush(lightRamp[1]);
            dark["ClowdNavSelectedBrush"] = new SolidColorBrush(darkRamp[5], 0.4);

            app.Resources.ThemeDictionaries[ThemeVariant.Light] = light;
            app.Resources.ThemeDictionaries[ThemeVariant.Dark] = dark;

            // only once both are in: a throw above must not leave this accent marked as done
            _applied = accent;
        }

        private static void WriteRamp(ResourceDictionary dict, Color[] ramp, bool dark)
        {
            for (int i = 0; i < ramp.Length; i++)
            {
                // both spellings: the theme publishes each step as a color and as a brush
                dict["SemiBlue" + i + "Color"] = ramp[i];
                dict["SemiBlue" + i] = new SolidColorBrush(ramp[i]);
            }

            foreach (var (name, lightStep, darkStep, darkOpacity) in PrimaryTokens)
                dict[name] = dark
                    ? new SolidColorBrush(ramp[darkStep], darkOpacity)
                    : new SolidColorBrush(ramp[lightStep]);
        }

        /// <summary>
        /// Walks the theme's own dictionaries once and notes every resource painted in a stock
        /// SemiBlue shade, so a later <see cref="Apply"/> can re-publish it in the accent. Read
        /// from the style tree rather than through resource lookup, so it keeps seeing the stock
        /// values after our own overrides are in place.
        /// </summary>
        private static List<Repaint> BuildPlan(Application app)
        {
            var plan = new List<Repaint>();
            var lightSteps = new Dictionary<uint, int>();
            var darkSteps = new Dictionary<uint, int>();
            var visited = new HashSet<object>();

            // Pass 1: the stock ramps. Pass 2: everything painted out of them. Two passes because
            // the ramp is not guaranteed to be visited before its consumers.
            for (int pass = 0; pass < 2; pass++)
            {
                visited.Clear();
                foreach (var style in app.Styles)
                    if (style is Styles styles)
                        Walk(styles.Resources, ThemeVariant.Light, pass);
            }

            return plan;

            void Walk(IResourceProvider provider, ThemeVariant inherited, int pass)
            {
                if (provider is not IResourceDictionary dict || !visited.Add(dict))
                    return;

                foreach (var entry in dict.ThemeDictionaries)
                {
                    // Aquatic/Desert/Dusk/NightSky are Semi's extra variants — Clowd only ever
                    // resolves Light or Dark. Default carries the light values.
                    var variant = entry.Key;
                    if (variant == ThemeVariant.Dark)
                        Walk(entry.Value, ThemeVariant.Dark, pass);
                    else if (variant == ThemeVariant.Light || variant == ThemeVariant.Default)
                        Walk(entry.Value, ThemeVariant.Light, pass);
                }

                foreach (var merged in dict.MergedDictionaries)
                    Walk(merged, inherited, pass);

                var dark = inherited == ThemeVariant.Dark;
                var steps = dark ? darkSteps : lightSteps;

                foreach (var key in dict.Keys)
                {
                    if (!dict.TryGetResource(key, inherited, out var value))
                        continue;

                    var name = key as string;

                    if (pass == 0)
                    {
                        // the ramp itself, by name: "SemiBlue3Color" → step 3
                        if (name == null || !name.StartsWith("SemiBlue", StringComparison.Ordinal))
                            continue;

                        var digits = name.Substring("SemiBlue".Length).TrimEnd();
                        if (digits.EndsWith("Color", StringComparison.Ordinal))
                            digits = digits.Substring(0, digits.Length - "Color".Length);
                        if (digits.Length != 1 || !Int32.TryParse(digits, out var step))
                            continue;
                        if (TryGetColor(value, out var rampColor))
                            steps[Pack(rampColor)] = step;

                        continue;
                    }

                    // the ramp's own keys are rewritten wholesale by WriteRamp
                    if (name != null && name.StartsWith("SemiBlue", StringComparison.Ordinal))
                        continue;
                    if (name != null && name.StartsWith("SemiColorPrimary", StringComparison.Ordinal))
                        continue;

                    if (!TryGetColor(value, out var color) || !steps.TryGetValue(Pack(color), out var s))
                        continue;

                    plan.Add(new Repaint(dark, key, s, color.A, value is IBrush brush ? brush.Opacity : null));
                }
            }
        }

        /// <summary>A theme token that paints text (or a glyph standing in for text) in the accent,
        /// as opposed to filling something the text sits on. Semi's naming carries this for all
        /// but the link colors, which are named for their role instead.</summary>
        private static bool IsTextKey(object key) =>
            key is string name
            && (name.Contains("Foreground", StringComparison.Ordinal)
                || name.StartsWith("SemiColorLink", StringComparison.Ordinal));

        /// <summary>Matches on RGB alone: the theme tints with the ramp at reduced alpha in places,
        /// and those tints should follow the accent at the alpha they were given.</summary>
        private static uint Pack(Color c) => (uint) ((c.R << 16) | (c.G << 8) | c.B);

        private static bool TryGetColor(object value, out Color color)
        {
            switch (value)
            {
                case Color c:
                    color = c;
                    return true;
                case ISolidColorBrush b:
                    color = b.Color;
                    return true;
                default:
                    color = default;
                    return false;
            }
        }

        /// <summary>
        /// The ten-step ramp Semi expects, with the accent at step 5. The stock ramp is (to within
        /// a rounding step) the base color blended toward white below 5 and toward black above it,
        /// so the accent's ramp is built the same way and lands in the same places.
        /// </summary>
        private static Color[] BuildRamp(Color accent, bool dark)
        {
            // The accent as set (darkened until white text on it reads) is the base in both
            // themes: lightening it for dark made the solid buttons pale under their white labels.
            // It is only lifted when it would sink into the dark ground itself.
            var baseColor = dark ? LiftToContrast(accent, DarkGround, MinimumFillContrastWithDark) : accent;

            var ramp = new Color[10];
            ramp[5] = baseColor;

            // Steps 6 and 7 are the pointer-over and pressed fills, still under white text, so
            // they go toward black in both themes: the stock dark ramp runs them lighter, but a
            // fill that sits at 4.5:1 against white has no room to lighten (a 25% lift is 3.3:1,
            // a 50% one 2.1:1). Steps 8 and 9 are not fills anywhere that matters.
            var below = dark
                ? new[] { 0.72, 0.58, 0.42, 0.26, 0.12 } // toward black — dark tints are darker
                : new[] { 0.92, 0.80, 0.60, 0.40, 0.20 }; // toward white
            var above = new[] { 0.12, 0.28, 0.44, 0.60 }; // toward black

            var belowTarget = dark ? Colors.Black : Colors.White;

            for (int i = 0; i < 5; i++)
                ramp[i] = Mix(baseColor, belowTarget, below[i]);
            for (int i = 6; i < 10; i++)
                ramp[i] = Mix(baseColor, Colors.Black, above[i - 6]);

            return ramp;
        }

        /// <summary>
        /// The ramp the dark theme paints accent *text* from: the fill base lifted toward white
        /// until it reads at text contrast on the raised surfaces text actually sits on (a
        /// button's gray fill, not just the window), with the pointer-over and pressed steps
        /// lifted a little further (text brightens on hover where a fill darkens). Steps below
        /// the base are the fill ramp's — they are tints, not text.
        /// </summary>
        private static Color[] BuildDarkTextRamp(Color[] fills)
        {
            var text = LiftToContrast(fills[5], DarkRaisedGround, MinimumTextContrastWithDark);
            var ramp = new Color[10];
            for (int i = 0; i < 5; i++)
                ramp[i] = fills[i];
            ramp[5] = text;
            ramp[6] = Mix(text, Colors.White, 0.15);
            ramp[7] = Mix(text, Colors.White, 0.30);
            ramp[8] = Mix(text, Colors.White, 0.50);
            ramp[9] = Mix(text, Colors.White, 0.70);
            return ramp;
        }

        /// <summary>The dark theme's window ground (ApplicationBackgroundColor in AppResources).</summary>
        private static readonly Color DarkGround = Color.FromRgb(0x20, 0x20, 0x20);

        /// <summary>The lightest common surface accent text lands on in the dark theme: Semi's
        /// SemiColorFill0 (white at 0.12) over the ground — the default button's fill. Text that
        /// only clears the window ground reads as dim there.</summary>
        private static readonly Color DarkRaisedGround = Color.FromRgb(0x3B, 0x3B, 0x3B);

        /// <summary>The least contrast an accent *fill* may have against <see cref="DarkGround"/>:
        /// WCAG's floor for UI components. The setting's 4.5:1-against-white guard does not imply
        /// this (black passes it and fails here), though any accent with some brightness left —
        /// most of them — clears it as is; the lift mostly bites on near-black picks with the
        /// guard off.</summary>
        private const double MinimumFillContrastWithDark = 3.0;

        /// <summary>The least contrast accent *text* may have against <see cref="DarkRaisedGround"/>:
        /// WCAG's floor for body text. A fill that sits at 4.5:1 against white always needs a
        /// lift to get here — the two targets cannot be met by one color.</summary>
        private const double MinimumTextContrastWithDark = 4.5;

        /// <summary>
        /// <paramref name="color"/> mixed toward white just far enough to reach
        /// <paramref name="minimumContrast"/> against <paramref name="ground"/>, or unchanged when
        /// it already does.
        /// </summary>
        private static Color LiftToContrast(Color color, Color ground, double minimumContrast)
        {
            var groundLuminance = AccentColors.RelativeLuminance(ground);
            var targetLuminance = minimumContrast * (groundLuminance + 0.05) - 0.05;
            if (AccentColors.RelativeLuminance(color) >= targetLuminance)
                return color;

            // Luminance climbs monotonically with the mix amount, so bisect it.
            double lo = 0, hi = 1;
            for (int i = 0; i < 16; i++)
            {
                var mid = (lo + hi) / 2;
                if (AccentColors.RelativeLuminance(Mix(color, Colors.White, mid)) >= targetLuminance)
                    hi = mid;
                else
                    lo = mid;
            }

            return Mix(color, Colors.White, hi);
        }

        private static Color Mix(Color from, Color to, double amount)
        {
            byte Channel(byte a, byte b) => (byte) Math.Round(a + (b - a) * amount);
            return Color.FromRgb(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
        }
    }
}
