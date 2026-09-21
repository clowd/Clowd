using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace Clowd.Config
{
    /// <summary>
    /// The three frame rates the recording strip's FPS tile cycles through. They are settings
    /// rather than constants because the tile is the only place most recordings choose a rate, and
    /// a cycle of fixed stops cannot reach the one rate a given user actually records at (issue
    /// #101: 48 fps was only reachable by opening the settings page).
    /// <para>
    /// The values are a user's preference, not a promise about any monitor: the cycle the tile
    /// really offers is these capped and completed by the screen's refresh rate
    /// (<c>FpsCycleRules.Options</c>). Duplicates and ordering are tolerated here and resolved by
    /// <see cref="Values"/>, so typing 60 into two boxes shortens the cycle rather than breaking it.
    /// </para>
    /// </summary>
    public class FpsPresets : SimpleNotifyObject
    {
        /// <summary>The bounds a preset box accepts, matching <c>SettingsRecording.Fps</c>'s own
        /// [Range]: a preset the recorder could not be configured with is not a preset.</summary>
        public const int MinFps = 1;

        public const int MaxFps = 240;

        public int First
        {
            get => _first;
            set => Set(ref _first, Clamp(value), nameof(First), nameof(Values));
        }

        public int Second
        {
            get => _second;
            set => Set(ref _second, Clamp(value), nameof(Second), nameof(Values));
        }

        public int Third
        {
            get => _third;
            set => Set(ref _third, Clamp(value), nameof(Third), nameof(Values));
        }

        /// <summary>The presets as the cycle consumes them: ascending and without duplicates, and
        /// never empty (the three fields are always in range, so at least one survives).</summary>
        [Browsable(false), JsonIgnore]
        public IReadOnlyList<int> Values =>
            new[] { _first, _second, _third }.Distinct().OrderBy(f => f).ToList();

        private static int Clamp(int fps) => Math.Clamp(fps, MinFps, MaxFps);

        private int _first = 30;
        private int _second = 60;
        private int _third = 120;
    }
}
