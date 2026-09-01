using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Clowd.UI.Preview.Icons;
using SkiaSharp;

namespace Clowd.UI.Preview.Producers
{
    /// <summary>
    /// Lane A. Typesets the first few lines of a text session onto its tile, so a list of pasted
    /// snippets is distinguishable at a glance instead of being a column of identical lettered
    /// pages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The picture is deliberately a <i>document</i> and not a screenshot: a light page with a
    /// hairline border and small monospace ink, drawn at fixed colours. Fixed rather than themed
    /// because the tile is composited straight onto the Recent page's row background, which is light
    /// in one theme and dark in the other — the same reason every shipped icons8 icon is
    /// fixed-colour art. A page that inverted with the theme would stop reading as paper.
    /// </para>
    /// <para>
    /// Cheap enough that it is deliberately not disk-cached: reading 8 KB and drawing eight lines
    /// costs less than the PNG round trip would, and unlike a poster frame there is no decoder to
    /// open.
    /// </para>
    /// </remarks>
    public static class TextExcerptProducer
    {
        /// <summary>How much of the file to read. Eight lines of anything are inside the first few
        /// hundred bytes; the rest of this budget exists so a file that opens with a long licence
        /// banner, or with CRLF-heavy boilerplate, still yields something.</summary>
        private const int ReadBytes = 8 * 1024;

        /// <summary>How far in to look for a NUL before calling the file binary. A byte stream that
        /// is going to have an embedded zero has one early — headers, magic numbers, string tables —
        /// and scanning further would start rejecting legitimate text for one stray byte deep in a
        /// log.</summary>
        private const int BinarySniffBytes = 1024;

        private const int MaxLines = 8;
        private const int TabWidth = 4;

        /// <summary>A guard on pathological input, not a layout parameter: the layout truncates to
        /// what fits the tile long before this. It exists so a minified 8 KB single-line JSON file is
        /// not accumulated character by character into a string nothing will draw.</summary>
        private const int MaxLineChars = 512;

        // The page. Authored once, in the same spirit as the icon palettes: chosen to read as paper
        // over both the light and the dark row background rather than to match either of them.
        private static readonly SKColor PageColor = new SKColor(0xFA, 0xFA, 0xF7);
        private static readonly SKColor BorderColor = new SKColor(0xC4, 0xC8, 0xD0);
        private static readonly SKColor InkColor = new SKColor(0x2C, 0x2F, 0x36);

        // Physical pixels in the 220x150 tile, i.e. 2x logical. A 12px face with a 12px left margin
        // gives roughly 27 monospace columns, which is enough for a line of prose or code to be
        // recognizable at 110x75 logical without turning into grey mud.
        private const float FontSize = 12f;
        private const float MarginLeft = 12f;
        private const float MarginRight = 10f;
        private const float MarginTop = 11f;
        private const float MarginBottom = 6f;
        private const float BorderWidth = 2f;

        /// <summary>
        /// Produces the tile picture for a <see cref="PreviewSourceKind.Text"/> source, or null when
        /// there is nothing worth drawing — an empty file, one that is binary despite its extension,
        /// or one that cannot be read. Null falls through to the file-type icon, which is the right
        /// answer for all three.
        /// </summary>
        public static PreviewPixels Produce(in PreviewSource source, PreviewRequest request, CancellationToken ct)
        {
            if (String.IsNullOrEmpty(source.Path))
                return null;

            int width = request?.TargetWidth > 0 ? request.TargetWidth : PreviewFormat.TileWidth;
            int height = request?.TargetHeight > 0 ? request.TargetHeight : PreviewFormat.TileHeight;

            try
            {
                if (ct.IsCancellationRequested)
                    return null;

                var text = ReadHead(source.Path);
                if (text == null)
                    return null;

                var lines = FirstLines(text);
                if (lines.Length == 0)
                    return null;

                if (ct.IsCancellationRequested)
                    return null;

                return Draw(lines, width, height);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TextExcerptProducer: " + source.Path + " — " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The first <see cref="ReadBytes"/> of the file as text, or null when it is empty or looks
        /// binary.
        /// </summary>
        /// <remarks>
        /// Encoding is decided in the order the evidence is reliable: a byte-order mark is a
        /// statement and is believed; otherwise a strict UTF-8 decode is attempted, and only its
        /// failure — which for real content means a legacy single-byte encoding — falls back to
        /// Latin-1, which cannot fail and at least gets the ASCII spine of the file right. The binary
        /// sniff runs after the BOM check rather than before it, because a UTF-16 file is full of
        /// legitimate zero bytes and would otherwise be rejected as binary on its first ASCII
        /// character.
        /// </remarks>
        private static string ReadHead(string path)
        {
            byte[] buffer;
            int read;

            // ReadWrite | Delete because the payload may still be open: a text upload writes
            // content.txt and creates its row in the same breath, and a share violation here would be
            // negative-cached as "this session has no preview" for the next five minutes.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                buffer = new byte[ReadBytes];
                read = stream.ReadAtLeast(buffer, ReadBytes, throwOnEndOfStream: false);
            }

            if (read <= 0)
                return null;

            if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                return DecodeUtf8(buffer, 3, read - 3) ?? DecodeLatin1(buffer, 3, read - 3);

            if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
                return Decode(Encoding.Unicode, buffer, 2, read - 2);

            if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
                return Decode(Encoding.BigEndianUnicode, buffer, 2, read - 2);

            int sniff = Math.Min(read, BinarySniffBytes);
            for (int i = 0; i < sniff; i++)
            {
                if (buffer[i] == 0)
                    return null;
            }

            return DecodeUtf8(buffer, 0, read) ?? DecodeLatin1(buffer, 0, read);
        }

        /// <summary>Strict UTF-8, or null when the bytes are not valid UTF-8.</summary>
        private static string DecodeUtf8(byte[] buffer, int offset, int count)
        {
            try
            {
                return Decode(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    buffer, offset, count);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        private static string DecodeLatin1(byte[] buffer, int offset, int count)
            => Decode(Encoding.Latin1, buffer, offset, count);

        /// <summary>
        /// Decodes through a stateful <see cref="Decoder"/> with <c>flush: false</c>, which is the
        /// whole trick: the read almost certainly cut a multi-byte character in half at the 8 KB
        /// boundary, and an unflushed decoder holds that partial sequence back instead of reporting
        /// it as invalid — which under strict UTF-8 would throw and send a perfectly good file down
        /// the Latin-1 path.
        /// </summary>
        private static string Decode(Encoding encoding, byte[] buffer, int offset, int count)
        {
            if (count <= 0)
                return null;

            var decoder = encoding.GetDecoder();
            var chars = new char[encoding.GetMaxCharCount(count)];
            int written = decoder.GetChars(buffer, offset, count, chars, 0, flush: false);
            return written <= 0 ? null : new string(chars, 0, written);
        }

        /// <summary>
        /// The first <see cref="MaxLines"/> lines with something on them, tabs expanded and control
        /// characters neutralized.
        /// </summary>
        /// <remarks>
        /// Blank lines are skipped rather than drawn. There is room for eight lines in total, and a
        /// file that opens with a comment banner and two blank lines would otherwise spend a quarter
        /// of the picture on nothing. The trade is that the excerpt is not a faithful transcription
        /// of the file's opening — which it could not be at this size anyway.
        /// </remarks>
        private static string[] FirstLines(string text)
        {
            var lines = new string[MaxLines];
            int count = 0;
            var current = new StringBuilder(MaxLineChars);

            // The <= is deliberate: the last iteration runs with no character left and closes the
            // final line, so a file with no trailing newline is not silently one line shorter.
            for (int i = 0; i <= text.Length && count < MaxLines; i++)
            {
                char c = i == text.Length ? '\n' : text[i];

                if (c == '\r')
                    continue;

                if (c != '\n')
                {
                    if (current.Length >= MaxLineChars)
                        continue;

                    if (c == '\t')
                    {
                        // To the next tab stop rather than a fixed run of spaces, so indented code
                        // lines up the way it does in an editor.
                        current.Append(' ', TabWidth - (current.Length % TabWidth));
                    }
                    else
                    {
                        // Anything unprintable becomes a space rather than a missing-glyph box: one
                        // stray control byte in an otherwise fine log should not draw a row of tofu.
                        current.Append(Char.IsControl(c) ? ' ' : c);
                    }

                    continue;
                }

                var line = current.ToString().TrimEnd();
                current.Clear();

                if (line.Length > 0)
                    lines[count++] = line;
            }

            if (count == MaxLines)
                return lines;

            var trimmed = new string[count];
            Array.Copy(lines, trimmed, count);
            return trimmed;
        }

        private static PreviewPixels Draw(string[] lines, int width, int height)
        {
            // Premultiplied to render into, unpremultiplied on the way out — the page is fully
            // opaque, so this is a formality here, but it is the same one every other producer keeps.
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null)
                return null;

            var canvas = surface.Canvas;
            canvas.Clear(PageColor);

            using var font = new SKFont(IconFonts.Monospace, FontSize)
            {
                Edging = SKFontEdging.SubpixelAntialias,
                Subpixel = true,
            };

            using (var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = InkColor })
            {
                float lineHeight = font.Spacing > 0 ? font.Spacing : FontSize * 1.25f;
                float usableWidth = width - MarginLeft - MarginRight;

                // Monospace, so one advance is every advance and a column budget is exact. The clip
                // below is what makes this safe when it is not: the fallback chain can end at a
                // proportional face, and a long line there would otherwise run into the border.
                float advance = font.MeasureText("0");
                int columns = advance > 0 ? Math.Max(1, (int)(usableWidth / advance)) : MaxLineChars;

                int drawable = Math.Min(lines.Length,
                    Math.Max(1, (int)((height - MarginTop - MarginBottom) / lineHeight)));

                canvas.Save();
                canvas.ClipRect(new SKRect(MarginLeft, 0, width - MarginRight, height));

                // -Ascent puts the tallest glyph's TOP at the margin rather than its baseline, which
                // is what makes the visual top margin match the number.
                float baseline = MarginTop - font.Metrics.Ascent;

                for (int i = 0; i < drawable; i++)
                {
                    var line = lines[i];
                    if (line.Length > columns)
                        line = line.Substring(0, columns);

                    canvas.DrawText(line, MarginLeft, baseline, font, ink);
                    baseline += lineHeight;
                }

                canvas.Restore();
            }

            // Drawn last so the frame clips the text rather than the text overrunning the frame, and
            // inset by half its width so the stroke lands inside the tile instead of straddling its
            // edge and losing half of itself.
            using (var border = new SKPaint
            {
                IsAntialias = false,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = BorderWidth,
                Color = BorderColor,
            })
            {
                float inset = BorderWidth / 2f;
                canvas.DrawRect(new SKRect(inset, inset, width - inset, height - inset), border);
            }

            canvas.Flush();

            using var pixmap = surface.PeekPixels();
            return PreviewRaster.ToPixels(pixmap, width, height, PreviewKind.Photo);
        }
    }
}
