using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Styling;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Rendering;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// A sticky note lays itself out: it is a square of at least the default side, the text is drawn
    /// as large as fits, and only text that no longer fits at the smallest font size grows the note.
    /// Scale multiplies all of it. Also pins the plain-text side of the split: new text is drawn in
    /// its object color with no card, while text loaded from a document saved before the split is
    /// still the note card it was.
    /// </summary>
    public class GraphicStickyNoteTests
    {
        private static GraphicStickyNote Make(string body, double scale = 1) =>
            new GraphicStickyNote(Colors.LightYellow, new Point(200, 200), scale, 0, null, body);

        private static void AssertSquare(GraphicStickyNote g)
        {
            var b = g.UnrotatedBounds;
            Assert.True(Math.Abs(b.Width - b.Height) < 1.0001, $"not square: {b}");
        }

        [AvaloniaFact]
        public void ShortText_IsLargest_OnTheDefaultSquare_CenteredOnItsPoint()
        {
            var g = Make("Hi");
            Assert.Equal(162, g.Right - g.Left, 6);
            Assert.Equal(162, g.Bottom - g.Top, 6);
            Assert.Equal(new Point(200, 200), new Point((g.Left + g.Right) / 2, (g.Top + g.Bottom) / 2));
            Assert.Equal(27, g.FittedFontSize);
        }

        [AvaloniaFact]
        public void LongerText_ShrinksTheFont_BeforeTheNoteGrows()
        {
            var g = Make("Remember to check the logs on the staging server before lunch");
            Assert.True(g.FittedFontSize < 27, $"font {g.FittedFontSize}");
            Assert.True(g.FittedFontSize >= 13.5, $"font {g.FittedFontSize}");
            Assert.Equal(162, g.Right - g.Left, 6);
        }

        [AvaloniaFact]
        public void TextTooLongForTheSmallestFont_GrowsTheNote_AndStaysSquare()
        {
            var g = Make(String.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit amet", 20)));
            Assert.True(g.Right - g.Left > 162);

            // the note grows a step at a time, so the grown note may have room for a touch more
            Assert.InRange(g.FittedFontSize, 13.5, 15.5);
            AssertSquare(g);

            // the text fits inside the paper, inset on every side
            var text = g.TextRect;
            Assert.True(text.Top >= g.Top && text.Bottom <= g.Bottom, $"text {text} note {g.UnrotatedBounds}");
        }

        [AvaloniaFact]
        public void Shrinking_TheText_ShrinksTheNote_BackToTheDefault()
        {
            var g = Make(String.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit amet", 20)));
            g.Body = "ok";
            Assert.Equal(162, g.Right - g.Left, 6);
            Assert.Equal(27, g.FittedFontSize);
        }

        [AvaloniaFact]
        public void AnUnbreakableWord_TooWideAtTheSmallestFont_IsBroken_NotGrownFor()
        {
            var g = Make(new string('W', 120));
            Assert.Equal(13.5, g.FittedFontSize);
            AssertSquare(g);
            Assert.True(g.Right - g.Left < 1000, "grew for the word's width instead of wrapping it");

        }

        [AvaloniaFact]
        public void ALongWordBetweenOthers_IsBrokenAcrossLines_NotElided()
        {
            // FormattedText's default word-ellipsis trimming draws this as "hello / … / world"
            var g = Make("hello Supercalifragilisticexpialidociousness world");
            var lines = g.TextRect.Height / (g.FittedFontSize * 1.2);
            Assert.True(lines > 3.5, $"{lines:0.0} lines, the long word was elided rather than wrapped");
        }

        [AvaloniaFact]
        public void ADogEaredNote_KeepsItsTextAboveTheFold()
        {
            const int dogEar = 5;
            var body = "This padding looks off by a few pixels compared to the design mock, can we align it with the header?";
            var g = new GraphicStickyNote(Colors.LightYellow, new Point(200, 200), 1, dogEar, null, body);
            var side = g.Right - g.Left;
            Assert.True(g.TextRect.Bottom <= g.Bottom - side * 0.14, $"text {g.TextRect} runs into the fold of {g.UnrotatedBounds}");
        }

        [AvaloniaFact]
        public void Scale_MultipliesTheSquareAndTheFontLimits()
        {
            var g = Make("Hi", 2);
            Assert.Equal(324, g.Right - g.Left, 6);
            Assert.Equal(54, g.FittedFontSize);

            g.Scale = 0.5;
            Assert.Equal(81, g.Right - g.Left, 6);
            Assert.Equal(13.5, g.FittedFontSize);

            g.Scale = 100; // clamped
            Assert.Equal(GraphicStickyNote.MaxScale, g.Scale);
        }

        [AvaloniaFact]
        public void Note_RoundTrips_AndRefitsTheSameAfterLoad()
        {
            var g = new GraphicStickyNote(Colors.Pink, new Point(50, 60), 1.5, 3, "Arial", "Ship it\nthen celebrate") { Angle = 12 };
            var bytes = GraphicsSerializer.SerializeToUtf8Bytes(new GraphicBase[] { g });
            var r = Assert.IsType<GraphicStickyNote>(Assert.Single(GraphicsSerializer.DeserializeFromUtf8Bytes(bytes)));

            Assert.Equal(g.Body, r.Body);
            Assert.Equal(1.5, r.Scale);
            Assert.Equal(3, r.LiftStyle);
            Assert.Equal("Arial", r.FontName);
            Assert.Equal(GraphicStickyNote.NoteWeight, r.FontWeight);
            Assert.Equal(g.Angle, r.Angle);
            Assert.Equal(g.UnrotatedBounds, r.UnrotatedBounds);
            Assert.Equal(g.FittedFontSize, r.FittedFontSize);
        }

        [AvaloniaFact]
        public void InPlaceEditor_WrapsInsideTheNote_AndTracksTheFittedFont()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var window = new Window { Width = 1200, Height = 900, Content = canvas };

            // the test app has no theme, and an untemplated ScrollViewer lays its content out in
            // the viewport; the app's theme scrolls it, which is what can run text off in one line
            var scrollTheme = new ControlTheme(typeof(ScrollViewer));
            scrollTheme.Setters.Add(new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ScrollViewer>((_, ns) =>
            {
                var presenter = new ScrollContentPresenter
                {
                    Name = "PART_ContentPresenter",
                    [!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty),
                };
                presenter.RegisterInNameScope(ns);
                return presenter;
            })));
            window.Resources[typeof(ScrollViewer)] = scrollTheme;
            window.Show();

            var note = Make("x");
            canvas.GraphicsList.Add(note);
            canvas.ToolText.CreateTextBox(note, canvas, true);
            var editor = Assert.Single(canvas.Children.OfType<TextBox>());

            editor.Text = String.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit amet", 6));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // the note refit around the text, and the editor followed it: same font size, wrapped at
            // the note's text width rather than running on in one line
            Assert.Equal(note.FittedFontSize, editor.FontSize);
            var text = note.TextRect;
            Assert.Equal(text.Width, editor.Bounds.Width, 3);
            Assert.True(editor.Bounds.Height > editor.FontSize * 3, $"editor did not wrap: {editor.Bounds}");
            Assert.True(editor.Bounds.Height <= note.Bottom - note.Top, $"editor {editor.Bounds} overflows note {note.UnrotatedBounds}");

            window.Close();
        }

        [Theory]
        [InlineData(0xFF, 0xF1, 0xB8)] // butter
        [InlineData(0xFB, 0xD5, 0xE2)] // rose
        [InlineData(0xCD, 0xEF, 0xDF)] // mint
        [InlineData(0xD3, 0xE6, 0xFD)] // sky
        [InlineData(0x30, 0x20, 0x60)] // a dark paper gets pale ink
        public void Ink_IsTheShadeOfThePaper_AtStrongContrast(byte r, byte g, byte b)
        {
            var paper = Color.FromRgb(r, g, b);
            var ink = GraphicStickyNote.GetInk(paper);
            Assert.True(RenderResources.GetContrastRatio(ink, paper) >= GraphicStickyNote.InkContrast, $"{ink} on {paper}");
            Assert.True(Math.Abs(HslHue(ink) - HslHue(paper)) < 12, $"hue {HslHue(ink)} vs {HslHue(paper)}");
        }

        private static double HslHue(Color c) => new HslColor(c).H;

        [AvaloniaFact]
        public void NewNotes_AreTiltedVerySlightly_EachDifferently()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var angles = Enumerable.Range(0, 40).Select(_ => new GraphicStickyNote(canvas, new Point(100, 100)).Angle).ToArray();

            Assert.All(angles, a => Assert.InRange(Math.Abs(a), 0, GraphicStickyNote.MaxPlacementTilt));
            Assert.True(angles.Distinct().Count() > 5, "placement tilt is not random");
        }

        [Fact]
        public void NextLook_NeverRepeatsThePreviousOne_AndUsesThemAll()
        {
            var seen = new bool[GraphicStickyNote.LiftStyleCount];
            var last = GraphicStickyNote.NextLook();
            for (int i = 0; i < 500; i++)
            {
                var look = GraphicStickyNote.NextLook();
                Assert.NotEqual(last, look);
                Assert.InRange(look, 0, GraphicStickyNote.LiftStyleCount - 1);
                seen[look] = true;
                last = look;
            }

            Assert.All(seen, Assert.True);
        }

        [AvaloniaFact]
        public void NewText_IsDrawnInItsForeground_WithATightInset_UntilItHasAFill()
        {
            var g = new GraphicText(Colors.Transparent, 2, new Point(10, 10), 0, "hello") { Foreground = Colors.Red };
            Assert.Equal(Colors.Red, g.TextColor);
            Assert.Equal(GraphicText.PlainTextPadding, g.Padding);
            var plainWidth = g.Right - g.Left;

            // a visible fill gets the roomier inset, and the box grows to match
            g.ObjectColor = Colors.LightYellow;
            Assert.Equal(GraphicText.TextPadding, g.Padding);
            Assert.Equal(plainWidth + (GraphicText.TextPadding - GraphicText.PlainTextPadding) * 2, g.Right - g.Left, 6);

            Assert.True(g.DropShadowEffect); // the fill casts one

            g.ObjectColor = Colors.Transparent;
            Assert.Equal(plainWidth, g.Right - g.Left, 6);
            Assert.False(g.DropShadowEffect); // bare letters do not
        }

        [AvaloniaFact]
        public void TextFromTheCanvas_TakesItsTextColorAndFill()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
            var canvas = new DrawingCanvas { Tool = ToolType.None, ObjectColor = Colors.Blue, ObjectFill = Colors.Transparent };
            var g = new GraphicText(canvas, new Point(10, 10));
            Assert.Equal(Colors.Blue, g.Foreground);
            Assert.False(g.HasFill);
        }

        [AvaloniaFact]
        public void TextFromBeforeTheForegroundColor_StillLoadsAsANoteCard()
        {
            var pastel = Color.FromRgb(0xFF, 0xF1, 0xB8);
            var bytes = GraphicsSerializer.SerializeToUtf8Bytes(new GraphicBase[] { new GraphicText(pastel, 2, new Point(10, 10), 0, "hello") { Foreground = Colors.Red } });

            // documents saved before text had a foreground color have no such field at all; their
            // object color is the note's fill, as it still is
            var json = JsonNode.Parse(bytes)!.AsArray();
            var obj = json[0]!.AsObject();
            var key = obj.Select(p => p.Key).Single(k => k.Contains("foreground", StringComparison.OrdinalIgnoreCase));
            obj.Remove(key);

            var r = Assert.IsType<GraphicText>(Assert.Single(GraphicsSerializer.DeserializeFromUtf8Bytes(Encoding.UTF8.GetBytes(json.ToJsonString()))));
            Assert.Equal(Colors.Black, r.TextColor); // as shipped: black text on the fill
            Assert.Equal(pastel, r.ObjectColor);
            Assert.Equal(GraphicText.TextPadding, r.Padding);
        }
    }
}
