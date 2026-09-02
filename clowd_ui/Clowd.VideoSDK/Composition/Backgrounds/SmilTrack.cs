using System;
using System.Globalization;
using System.Xml.Linq;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// One SMIL animation as a pure function of loop phase: a monotonic <c>keyTimes</c> list and
    /// a flat <c>values</c> matrix, sampled by linear interpolation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A track holds no clock and no playback state. <see cref="Sample"/> takes the phase — where
    /// in [0, 1) the loop is — and returns the interpolated row; the caller derives the phase from
    /// the project timeline (see <c>BackgroundRenderer.PhaseOf</c>), so the editor preview, an
    /// export and an inspector tile that ask for the same instant get the same numbers. This is
    /// the structural guarantee behind the wallpapers' WYSIWYG: there is nothing here that could
    /// drift.
    /// </para>
    /// <para>
    /// The accepted form is exactly the one the authoring scripts emit: <c>values</c> plus
    /// <c>keyTimes</c> (never <c>from</c>/<c>to</c>), <c>calcMode</c> absent or <c>linear</c>,
    /// <c>repeatCount="indefinite"</c>, and a <c>dur</c>. The keyTimes are non-uniform (the
    /// scripts space them by distance travelled for constant-speed motion), so sampling is an
    /// interval search, not <c>floor(phase * N)</c>. No wraparound branch exists: the files
    /// close their loops themselves (first value equals last, last keyTime is 1.0), and a test
    /// holds every shipped animation to that.
    /// </para>
    /// </remarks>
    internal sealed class SmilTrack
    {
        private SmilTrack(float[] keyTimes, float[] values, int stride, long durationTicks)
        {
            KeyTimes = keyTimes;
            Values = values;
            Stride = stride;
            DurationTicks = durationTicks;
        }

        /// <summary>N monotonic instants in [0, 1].</summary>
        internal float[] KeyTimes { get; }

        /// <summary>N rows of <see cref="Stride"/> numbers, flattened.</summary>
        internal float[] Values { get; }

        /// <summary>Numbers per value: 1 for <c>cx</c>, 2 for a translate, 74 for the blob's <c>d</c>.</summary>
        internal int Stride { get; }

        /// <summary>The animation's <c>dur</c> in 100ns ticks — the loop period it expects.</summary>
        internal long DurationTicks { get; }

        /// <summary>Number of keyframes.</summary>
        internal int Count => KeyTimes.Length;

        /// <summary>
        /// The interpolated row at <paramref name="phase"/> (clamped into [0, 1)): the last
        /// keyTime at or before the phase is found by binary search and the row is lerped toward
        /// the next. <paramref name="dst"/> must hold <see cref="Stride"/> floats.
        /// </summary>
        internal void Sample(double phase, Span<float> dst)
        {
            int n = KeyTimes.Length;
            if (n == 1)
            {
                Values.AsSpan(0, Stride).CopyTo(dst);
                return;
            }

            if (double.IsNaN(phase) || phase < 0)
                phase = 0;
            else if (phase >= 1)
                phase = 1 - 1e-9;

            int lo = 0, hi = n - 2;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (KeyTimes[mid] <= phase)
                    lo = mid;
                else
                    hi = mid - 1;
            }

            int i = lo;
            double span = KeyTimes[i + 1] - KeyTimes[i];
            double u = span > 0 ? (phase - KeyTimes[i]) / span : 0;
            if (u < 0) u = 0;
            else if (u > 1) u = 1;

            int a = i * Stride, b = (i + 1) * Stride;
            for (int c = 0; c < Stride; c++)
                dst[c] = (float)(Values[a + c] + (Values[b + c] - Values[a + c]) * u);
        }

        /// <summary>
        /// The track an <c>&lt;animate&gt;</c> / <c>&lt;animateTransform&gt;</c> element
        /// describes, or null with the reason when it is outside the accepted form.
        /// <paramref name="parse"/> turns one entry of <c>values</c> into a row of
        /// <paramref name="stride"/> numbers, or null to reject it.
        /// </summary>
        internal static SmilTrack TryParse(XElement animation, int stride, Func<string, float[]> parse,
            out string rejection)
        {
            rejection = null;

            string calcMode = (string)animation.Attribute("calcMode");
            if (calcMode != null && calcMode != "linear")
            {
                rejection = "calcMode '" + calcMode + "' is not linear";
                return null;
            }
            if ((string)animation.Attribute("repeatCount") != "indefinite")
            {
                rejection = "repeatCount is not indefinite";
                return null;
            }
            if ((string)animation.Attribute("keySplines") != null)
            {
                rejection = "keySplines are not supported";
                return null;
            }

            long dur = Ticks((string)animation.Attribute("dur"));
            if (dur <= 0)
            {
                rejection = "dur is missing or zero";
                return null;
            }

            string valuesText = (string)animation.Attribute("values");
            if (string.IsNullOrWhiteSpace(valuesText))
            {
                rejection = "no values list (from/to animation)";
                return null;
            }
            var entries = valuesText.Split(';', StringSplitOptions.RemoveEmptyEntries);
            int n = entries.Length;
            if (n == 0)
            {
                rejection = "empty values list";
                return null;
            }

            var values = new float[n * stride];
            for (int i = 0; i < n; i++)
            {
                var row = parse(entries[i].Trim());
                if (row == null || row.Length != stride)
                {
                    rejection = "value " + i.ToString(CultureInfo.InvariantCulture) + " does not match the first";
                    return null;
                }
                row.CopyTo(values, i * stride);
            }

            float[] keyTimes;
            string keyTimesText = (string)animation.Attribute("keyTimes");
            if (keyTimesText == null)
            {
                // SMIL's default for linear: evenly spaced.
                keyTimes = new float[n];
                for (int i = 0; i < n; i++)
                    keyTimes[i] = n == 1 ? 0 : i / (float)(n - 1);
            }
            else
            {
                var parts = keyTimesText.Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != n)
                {
                    rejection = "keyTimes count differs from values count";
                    return null;
                }
                keyTimes = new float[n];
                for (int i = 0; i < n; i++)
                {
                    if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out keyTimes[i]))
                    {
                        rejection = "keyTimes entry " + i.ToString(CultureInfo.InvariantCulture) + " is not a number";
                        return null;
                    }
                    if (i > 0 && keyTimes[i] < keyTimes[i - 1])
                    {
                        rejection = "keyTimes are not monotonic";
                        return null;
                    }
                }
                if (n > 1 && (keyTimes[0] != 0 || keyTimes[n - 1] != 1))
                {
                    rejection = "keyTimes must run from 0 to 1";
                    return null;
                }
            }

            return new SmilTrack(keyTimes, values, stride, dur);
        }

        /// <summary>An SMIL duration (<c>60s</c>, <c>1.6s</c>, <c>400ms</c>) in 100ns ticks.</summary>
        internal static long Ticks(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
            text = text.Trim();
            double ms;
            if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
                ms = Num(text.Substring(0, text.Length - 2));
            else if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                ms = Num(text.Substring(0, text.Length - 1)) * 1000.0;
            else
                ms = Num(text) * 1000.0;
            return (long)Math.Round(ms * TimeSpan.TicksPerMillisecond);
        }

        private static double Num(string text)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
