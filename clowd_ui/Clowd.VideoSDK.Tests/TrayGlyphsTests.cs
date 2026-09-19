using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Clowd.UI.Controls.Tray;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The tray icon set is pure data copied out of the design spec, and a typo in it is the kind of
    /// defect nothing else catches: a malformed path silently draws nothing (or draws off-canvas), and
    /// these strips only ever appear over a live recording, where nobody is looking at the icons.
    ///
    /// <para>
    /// Every assertion here reads the path STRINGS only. The combined
    /// <c>TrayGlyph.StrokeGeometry</c>/<c>FillGeometry</c> call
    /// <c>StreamGeometry.Parse</c>, which needs an <c>IPlatformRenderInterface</c>; this test project has
    /// no Avalonia platform at all, so touching either property would throw. That is exactly why the
    /// geometry is built lazily — if a future change makes the geometry eager, these tests break, which
    /// is the intended tripwire.
    /// </para>
    /// </summary>
    public class TrayGlyphsTests
    {
        private const string AllowedChars = "MmLlHhVvCcSsQqAaZz0123456789 ,.-";

        private static IEnumerable<string> AllPaths(TrayGlyph glyph) =>
            glyph.StrokePaths.Concat(glyph.FillPaths);

        [Fact]
        public void All_lists_every_glyph_exactly_once()
        {
            // 18 glyphs in spec §13; the grip dots are generated, not an icon.
            Assert.Equal(18, TrayGlyphs.All.Count);
            Assert.Equal(TrayGlyphs.All.Count, TrayGlyphs.All.Select(g => g.Name).Distinct().Count());
            Assert.All(TrayGlyphs.All, g => Assert.NotNull(g));
        }

        [Fact]
        public void Every_glyph_draws_something()
        {
            foreach (var glyph in TrayGlyphs.All)
                Assert.True(AllPaths(glyph).Any(), glyph.Name + " has no path at all");
        }

        [Fact]
        public void Every_path_is_well_formed_svg_path_data()
        {
            foreach (var glyph in TrayGlyphs.All)
            {
                foreach (var path in AllPaths(glyph))
                {
                    Assert.False(string.IsNullOrWhiteSpace(path), glyph.Name + " has an empty path");

                    // A path that does not open with a moveto has no defined start point: the parser
                    // either throws or starts at the origin, which puts the glyph in the wrong place.
                    Assert.True(path[0] == 'M' || path[0] == 'm',
                        glyph.Name + " path does not start with a moveto: " + path);

                    foreach (var c in path)
                        Assert.True(AllowedChars.IndexOf(c) >= 0,
                            glyph.Name + " path contains the unsupported character '" + c + "': " + path);
                }
            }
        }

        [Fact]
        public void Absolute_coordinates_stay_on_the_canvas()
        {
            // The canvas is 24 units and the icon is scaled by bounds/24, so an absolute coordinate far
            // outside it means a mistyped digit — a glyph drawn mostly out of its own box. Relative
            // commands are exempt: arc radii and sweeps legitimately exceed the canvas size.
            foreach (var glyph in TrayGlyphs.All)
            {
                foreach (var path in AllPaths(glyph))
                {
                    foreach (var (command, value) in Numbers(glyph, path))
                    {
                        if (char.IsLower(command))
                            continue;

                        Assert.InRange(value, -24, 48);
                    }
                }
            }
        }

        [Fact]
        public void Stroke_width_is_the_spec_weight()
        {
            foreach (var glyph in TrayGlyphs.All)
            {
                // The rotate arrow is the one exception: it is drawn in a 14 px button, where 1.8 units
                // would come out under a pixel wide.
                var expected = ReferenceEquals(glyph, TrayGlyphs.Rotate) ? 2.4 : 1.8;
                Assert.Equal(expected, glyph.StrokeWidth);
            }

            Assert.Equal(2.4, TrayGlyphs.Rotate.StrokeWidth);
        }

        [Fact]
        public void Media_glyphs_are_fill_only()
        {
            foreach (var glyph in new[] { TrayGlyphs.Play, TrayGlyphs.Pause, TrayGlyphs.Stop, TrayGlyphs.Rec })
            {
                Assert.Empty(glyph.StrokePaths);
                Assert.NotEmpty(glyph.FillPaths);
            }

            Assert.Equal(2, TrayGlyphs.Pause.FillPaths.Count);
        }

        [Fact]
        public void Speaker_glyphs_have_a_filled_body_and_stroked_waves()
        {
            foreach (var glyph in new[] { TrayGlyphs.Spk, TrayGlyphs.SpkOff })
            {
                Assert.NotEmpty(glyph.StrokePaths);
                Assert.Single(glyph.FillPaths);
            }
        }

        [Fact]
        public void Every_other_glyph_is_stroke_only()
        {
            var mixed = new[]
            {
                TrayGlyphs.Spk, TrayGlyphs.SpkOff,
                TrayGlyphs.Play, TrayGlyphs.Pause, TrayGlyphs.Stop, TrayGlyphs.Rec,
            };

            foreach (var glyph in TrayGlyphs.All)
            {
                if (mixed.Contains(glyph))
                    continue;

                Assert.NotEmpty(glyph.StrokePaths);
                Assert.Empty(glyph.FillPaths);
            }
        }

        [Fact]
        public void Converted_shapes_kept_their_geometry()
        {
            // The spec writes four of its glyphs as rect/circle/polygon; they were converted to path data
            // at authoring time, so these pin the conversions that have a checkable closed form.
            Assert.Contains("M5,12 a7,7 0 1 0 14,0 a7,7 0 1 0 -14,0 Z", TrayGlyphs.Rec.FillPaths);   // circle(12,12,r7)
            Assert.Contains("M7,4 L19,12 L7,20 Z", TrayGlyphs.Play.FillPaths);                        // polygon 7,4 19,12 7,20

            // rect x6 y6 w12 h12 rx2.5 → starts at x+rx, runs w−2rx, turns with an rx arc.
            Assert.StartsWith("M8.5,6 h7 a2.5,2.5 0 0 1 2.5,2.5 v7", TrayGlyphs.Stop.FillPaths[0]);

            // Every converted shape is a closed subpath.
            foreach (var path in new[] { TrayGlyphs.Rec.FillPaths[0], TrayGlyphs.Play.FillPaths[0], TrayGlyphs.Stop.FillPaths[0] })
                Assert.EndsWith("Z", path);
        }

        /// <summary>
        /// Walks a path string yielding each number with the command letter it belongs to. Written out
        /// rather than regex-matched because SVG allows numbers to run together ("1.5.5", "1-2") and the
        /// data here does exactly that.
        /// </summary>
        private static IEnumerable<(char Command, double Value)> Numbers(TrayGlyph glyph, string path)
        {
            var command = '\0';
            var i = 0;
            while (i < path.Length)
            {
                var c = path[i];
                if (char.IsLetter(c))
                {
                    command = c;
                    i++;
                    continue;
                }

                if (c == ' ' || c == ',')
                {
                    i++;
                    continue;
                }

                var start = i;
                if (c == '-' || c == '+')
                    i++;

                var seenDot = false;
                var seenDigit = false;
                while (i < path.Length)
                {
                    var d = path[i];
                    if (d >= '0' && d <= '9')
                    {
                        seenDigit = true;
                        i++;
                        continue;
                    }

                    if (d == '.' && !seenDot)
                    {
                        seenDot = true;
                        i++;
                        continue;
                    }

                    break;
                }

                var token = path.Substring(start, i - start);
                Assert.True(seenDigit && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                    glyph.Name + " path has the unparseable number '" + token + "': " + path);

                yield return (command, double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
        }
    }
}
