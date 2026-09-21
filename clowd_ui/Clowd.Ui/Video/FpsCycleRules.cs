using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Clowd.UI
{
    /// <summary>
    /// The rules behind the FPS tile at the head of the recording and share strips: which frame
    /// rates a click cycles through on a given monitor, which one comes next, and how a measured
    /// rate is printed. Nothing in here reads settings or screens, so all of it is testable
    /// (<c>FpsCycleRulesTests</c>).
    /// <para>
    /// The cycle is the user's three configured presets (30, 60, 120 out of the box) capped and
    /// completed by the monitor's own refresh rate: a preset above what the panel can show is dropped (an 80 Hz screen offers 30,
    /// 60, 80 — never 120), the native rate itself is always the last stop, and a preset within
    /// <see cref="NativeTolerance"/> of the native rate is folded into it so the list never carries
    /// two entries that mean the same thing (a 59 Hz panel offers 30 and 59, not 30, 59, 60).
    /// </para>
    /// </summary>
    public static class FpsCycleRules
    {
        /// <summary>The stops used when the caller has no configured presets to offer (the spike
        /// harness, and any call made before the settings are loaded). The real cycle reads
        /// <c>SettingsRecording.FpsPresets</c>, which defaults to these same three.</summary>
        public static readonly IReadOnlyList<int> Presets = new[] { 30, 60, 120 };

        /// <summary>How close (in Hz) a preset has to be to the native rate to count as the same
        /// stop. 2 covers the 59 / 60 and 119 / 120 pairs that panels really report while keeping
        /// 72 and 75 Hz screens their distinct 60 stop.</summary>
        public const double NativeTolerance = 2.0;

        /// <summary>
        /// The frame rates a click walks through, ascending, for a monitor refreshing at
        /// <paramref name="nativeHz"/>. Never empty. An unknown native rate (zero, negative or not
        /// finite — the platform query failed) yields the bare presets: the strip still offers a
        /// choice, and a wrong 120 on a screen that cannot show it costs a bigger file, not a broken
        /// recording.
        /// </summary>
        public static IReadOnlyList<int> Options(double nativeHz) => Options(nativeHz, Presets);

        /// <summary>
        /// <see cref="Options(double)"/> over a caller's own presets — the user's configured three
        /// (issue #101). An empty or null list falls back to the built-in <see cref="Presets"/>, so
        /// the tile always has somewhere to cycle to; the list need not be sorted or distinct.
        /// </summary>
        public static IReadOnlyList<int> Options(double nativeHz, IReadOnlyList<int> presets)
        {
            if (presets == null || presets.Count == 0)
                presets = Presets;

            var sorted = presets.Distinct().OrderBy(p => p).ToList();

            if (!(nativeHz > 0) || Double.IsInfinity(nativeHz))
                return sorted;

            var native = (int)Math.Round(nativeHz);
            var options = new List<int>(sorted.Count + 1);
            foreach (var preset in sorted)
            {
                // strictly below the native rate AND not the native rate in disguise
                if (preset < native && Math.Abs(preset - native) > NativeTolerance)
                    options.Add(preset);
            }

            options.Add(native);
            return options;
        }

        /// <summary>
        /// The stop after <paramref name="current"/>: the lowest option above it, wrapping to the
        /// first. A current value that is not in the list at all (typed on the settings page, or
        /// left over from a monitor with a different rate) still lands on a real stop rather than
        /// being kept.
        /// </summary>
        public static int Next(int current, IReadOnlyList<int> options)
        {
            if (options == null || options.Count == 0)
                throw new ArgumentException("At least one option is required.", nameof(options));

            foreach (var option in options)
            {
                if (option > current)
                    return option;
            }

            return options[0];
        }

        /// <summary>
        /// The tile's number for a measured rate: a whole number, since the recorder's status
        /// reports fractions ("29.9") that would jitter the label every second for no information
        /// the user can act on. A rate that is not a number (nothing measured yet) prints as 0 —
        /// the honest reading for a recorder that has produced no frames.
        /// </summary>
        public static string FormatFps(double fps)
        {
            if (Double.IsNaN(fps) || Double.IsInfinity(fps) || fps < 0)
                fps = 0;

            return Math.Round(fps).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The tile's number for a configured (target) rate.</summary>
        public static string FormatFps(int fps) => fps.ToString(CultureInfo.InvariantCulture);
    }
}
