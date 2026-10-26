using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The shape of a path's <c>d</c> attribute with its numbers taken out: the verb sequence
    /// alone, which is what an animated path keeps constant while its coordinates move.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wallpaper files animate <c>d</c> as SMIL <c>values</c> lists — 177 complete path
    /// strings for the blob, 85 KB of text — and every value of one animation has the same verbs
    /// in the same order (the authoring script morphs coordinates, never structure). So a
    /// <c>d</c> is read once into a skeleton plus a flat <c>float[]</c>, the animation becomes a
    /// matrix of floats sharing that one skeleton, and a frame is <see cref="Build"/> over the
    /// interpolated row. Parsing text per frame is what this exists to avoid.
    /// </para>
    /// <para>
    /// The grammar is the corpus's: <c>M m L l H h V v C c Z z</c>, implicit repetition of the
    /// last verb, the implicit lineto after a moveto (every Big Sur mesh quad is
    /// <c>M x y  x y  x y  x y Z</c>), comma/whitespace/sign separators, and scientific notation
    /// (Monterey writes <c>1e-05</c>). Arcs, quadratics and smooth curves are not in any file and
    /// are rejected rather than approximated: <see cref="Parse"/> throws <see cref="FormatException"/>
    /// and the reader skips the element, so a shape is either drawn as authored or not at all.
    /// </para>
    /// </remarks>
    internal sealed class SvgPathSkeleton
    {
        private readonly byte[] _verbs;

        private SvgPathSkeleton(byte[] verbs, int numberCount)
        {
            _verbs = verbs;
            NumberCount = numberCount;
        }

        /// <summary>How many numbers <see cref="Build"/> consumes — the row length of an
        /// animation over this skeleton.</summary>
        internal int NumberCount { get; }

        /// <summary>How many verbs the path has after implicit repetition is spelled out.</summary>
        internal int VerbCount => _verbs.Length;

        /// <summary>
        /// A <c>d</c> string as its skeleton and its numbers. Throws <see cref="FormatException"/>
        /// on a verb outside the supported set, a number where none may follow (after
        /// <c>Z</c>), a missing argument, or data before the first verb.
        /// </summary>
        internal static (SvgPathSkeleton Skeleton, float[] Numbers) Parse(string d)
        {
            if (d == null)
                throw new FormatException("path data is missing");

            var verbs = new List<byte>();
            var numbers = new List<float>();
            int i = 0;
            char current = '\0';
            while (true)
            {
                SkipSeparators(d, ref i);
                if (i >= d.Length)
                    break;

                char ch = d[i];
                if (IsVerb(ch))
                {
                    current = ch;
                    i++;
                }
                else if (current == '\0')
                {
                    throw new FormatException("path data does not start with a command");
                }
                else if (current is 'Z' or 'z')
                {
                    throw new FormatException("a number follows a closepath");
                }
                else if (current == 'M')
                {
                    // SVG: the coordinate pairs after a moveto's first are implicit linetos.
                    current = 'L';
                }
                else if (current == 'm')
                {
                    current = 'l';
                }

                int arity = ArityOf(current);
                if (arity < 0)
                    throw new FormatException("unsupported path command '" + current + "'");

                for (int n = 0; n < arity; n++)
                {
                    if (!TryReadNumber(d, ref i, out float value))
                        throw new FormatException("command '" + current + "' is short of arguments");
                    numbers.Add(value);
                }
                verbs.Add((byte)current);
            }

            return (new SvgPathSkeleton(verbs.ToArray(), numbers.Count), numbers.ToArray());
        }

        /// <summary>True when the other skeleton has exactly these verbs — the condition under
        /// which two <c>d</c> values can be interpolated number for number.</summary>
        internal bool SameAs(SvgPathSkeleton other)
        {
            if (other == null || other._verbs.Length != _verbs.Length)
                return false;
            for (int i = 0; i < _verbs.Length; i++)
            {
                if (_verbs[i] != other._verbs[i])
                    return false;
            }
            return true;
        }

        /// <summary>The path for one row of numbers. The caller owns the result.</summary>
        internal SKPath Build(ReadOnlySpan<float> n)
        {
            if (n.Length < NumberCount)
                throw new ArgumentException("not enough numbers for this skeleton", nameof(n));

            var path = new SKPath();
            int k = 0;
            float cx = 0, cy = 0, sx = 0, sy = 0;
            foreach (byte verb in _verbs)
            {
                switch ((char)verb)
                {
                    case 'M':
                        cx = n[k]; cy = n[k + 1]; k += 2;
                        path.MoveTo(cx, cy);
                        sx = cx; sy = cy;
                        break;
                    case 'm':
                        cx += n[k]; cy += n[k + 1]; k += 2;
                        path.MoveTo(cx, cy);
                        sx = cx; sy = cy;
                        break;
                    case 'L':
                        cx = n[k]; cy = n[k + 1]; k += 2;
                        path.LineTo(cx, cy);
                        break;
                    case 'l':
                        cx += n[k]; cy += n[k + 1]; k += 2;
                        path.LineTo(cx, cy);
                        break;
                    case 'H':
                        cx = n[k]; k += 1;
                        path.LineTo(cx, cy);
                        break;
                    case 'h':
                        cx += n[k]; k += 1;
                        path.LineTo(cx, cy);
                        break;
                    case 'V':
                        cy = n[k]; k += 1;
                        path.LineTo(cx, cy);
                        break;
                    case 'v':
                        cy += n[k]; k += 1;
                        path.LineTo(cx, cy);
                        break;
                    case 'C':
                        path.CubicTo(n[k], n[k + 1], n[k + 2], n[k + 3], n[k + 4], n[k + 5]);
                        cx = n[k + 4]; cy = n[k + 5]; k += 6;
                        break;
                    case 'c':
                        path.CubicTo(cx + n[k], cy + n[k + 1], cx + n[k + 2], cy + n[k + 3],
                            cx + n[k + 4], cy + n[k + 5]);
                        cx += n[k + 4]; cy += n[k + 5]; k += 6;
                        break;
                    case 'Z':
                    case 'z':
                        path.Close();
                        cx = sx; cy = sy;
                        break;
                }
            }
            return path;
        }

        // ------------------------------------------------------------------------------ tokens

        /// <summary>Every SVG path verb, supported or not — an unsupported one must still end
        /// the previous command's argument run so it is reported as itself rather than as a
        /// short argument list.</summary>
        private static bool IsVerb(char c) => c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v'
            or 'C' or 'c' or 'Z' or 'z' or 'A' or 'a' or 'Q' or 'q' or 'S' or 's' or 'T' or 't';

        /// <summary>Numbers a verb takes; -1 for a verb outside the supported set.</summary>
        private static int ArityOf(char verb) => verb switch
        {
            'M' or 'm' or 'L' or 'l' => 2,
            'H' or 'h' or 'V' or 'v' => 1,
            'C' or 'c' => 6,
            'Z' or 'z' => 0,
            _ => -1,
        };

        private static void SkipSeparators(string s, ref int i)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ','))
                i++;
        }

        /// <summary>
        /// One number at <paramref name="i"/>: an optional sign, digits with an optional point,
        /// an optional exponent. A sign is also a separator (<c>-1.5-2.5</c> is two numbers), and
        /// an <c>e</c> is an exponent only when digits follow it, so the scan never runs into a
        /// verb. Separators before the number are skipped; false when no number is there.
        /// </summary>
        internal static bool TryReadNumber(string s, ref int i, out float value)
        {
            SkipSeparators(s, ref i);
            int start = i;
            int p = i;
            if (p < s.Length && (s[p] == '+' || s[p] == '-'))
                p++;
            int digits = 0;
            while (p < s.Length && char.IsDigit(s[p])) { p++; digits++; }
            if (p < s.Length && s[p] == '.')
            {
                p++;
                while (p < s.Length && char.IsDigit(s[p])) { p++; digits++; }
            }
            if (digits == 0)
            {
                value = 0;
                return false;
            }
            if (p < s.Length && (s[p] == 'e' || s[p] == 'E'))
            {
                int q = p + 1;
                if (q < s.Length && (s[q] == '+' || s[q] == '-'))
                    q++;
                if (q < s.Length && char.IsDigit(s[q]))
                {
                    while (q < s.Length && char.IsDigit(s[q]))
                        q++;
                    p = q;
                }
            }
            if (!float.TryParse(s.AsSpan(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
            i = p;
            return true;
        }

        /// <summary>Every number in a separated list (<c>"485.9 261.2"</c>), or null when
        /// anything in it is not a number.</summary>
        internal static float[] ParseNumbers(string text)
        {
            if (text == null)
                return null;
            var result = new List<float>();
            int i = 0;
            while (true)
            {
                SkipSeparators(text, ref i);
                if (i >= text.Length)
                    break;
                if (!TryReadNumber(text, ref i, out float value))
                    return null;
                result.Add(value);
            }
            return result.ToArray();
        }
    }
}
