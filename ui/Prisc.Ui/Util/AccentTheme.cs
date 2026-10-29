using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Prisc.Config;

namespace Prisc.Util
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
    /// The ramps themselves are <c>prisc_rust_core::color::palette</c>'s
    /// (<see cref="AccentColors.Palette"/>): light and dark are built separately, since Semi's
    /// ramp is not the same in both, and dark carries a second, lighter ramp for the tokens that
    /// paint accent text, since no one color can be both a fill under white text and text on a
    /// dark ground.
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

            // the ramps are prisc_rust_core::color::palette's, one native call per accent
            var palette = SettingsRoot.Current?.General?.GetAccentPalette()
                ?? AccentColors.Palette(AccentColors.PriscBlue, maintainContrast: true);
            var accent = palette.Effective;

            // the settings color picker streams a color per drag step, and most of them land on the
            // same contrast-corrected accent
            if (_applied == accent)
                return;

            _plan ??= BuildPlan(app);

            var lightRamp = palette.Light;
            var darkRamp = palette.Dark;
            var darkText = palette.DarkText;

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

            // The accent as *text or a line* on the window ground, for Prisc's own controls (the
            // theme's tokens get the same treatment by name, above): the accent itself in the light
            // theme, and the text ramp's base in the dark one, where the darkened accent that the
            // fills need would sink into the ground.
            light["PriscAccentTextBrush"] = new SolidColorBrush(lightRamp[5]);
            dark["PriscAccentTextBrush"] = new SolidColorBrush(darkText[5]);

            // Selected text in text boxes: Semi hard-codes a blue here (#0041C5 at 0.8) rather
            // than drawing it from the ramp, so the walk cannot find it. The selection foreground
            // is white, which the darkened accent is made for.
            light["TextBoxSelectionBackground"] = new SolidColorBrush(lightRamp[5], 0.8);
            dark["TextBoxSelectionBackground"] = new SolidColorBrush(darkRamp[5], 0.8);

            // The main window's selected navigation item. Semi's own choice (SemiColorPrimaryLight)
            // is the palest tint in the light theme, next to invisible on the window; one step up
            // reads. In the dark theme it is the accent itself, which as a solid block competes
            // with the primary button above the menu — so a wash of it over the ground instead.
            light["PriscNavSelectedBrush"] = new SolidColorBrush(lightRamp[1]);
            dark["PriscNavSelectedBrush"] = new SolidColorBrush(darkRamp[5], 0.4);

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
                    // Aquatic/Desert/Dusk/NightSky are Semi's extra variants — Prisc only ever
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
    }
}
