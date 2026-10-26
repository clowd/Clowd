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
    /// (<c>FpsCycleRules.Options</c>). Duplicates, zeros (an emptied box) and ordering are all
    /// tolerated here and resolved downstream, so typing 60 into two boxes shortens the cycle
    /// rather than breaking it.
    /// </para>
    /// </summary>
    public class FpsPresets : SimpleNotifyObject
    {
        /// <summary>The bounds a preset box accepts. Deliberately wider than the rates any panel
        /// can show, so a box can hold a rate for a monitor the user does not have plugged in right
        /// now; the cycle itself drops what the current screen cannot reach
        /// (<c>FpsCycleRules.MaxCycleFps</c>).
        /// <para>
        /// The floor is 0, not 1: 0 is how an emptied box is stored, and it means "no preset here"
        /// — the cycle drops it, so a user who wants two stops instead of three clears one box
        /// rather than being forced to invent a third rate.
        /// </para></summary>
        public const int MinFps = 0;

        public const int MaxFps = 999;

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

        /// <summary>The presets as the cycle consumes them: ascending and without duplicates. Can
        /// be empty of usable stops (all three boxes cleared); <c>FpsCycleRules.Options</c> decides
        /// what a tile with no presets offers.</summary>
        [Browsable(false), JsonIgnore]
        public IReadOnlyList<int> Values =>
            new[] { _first, _second, _third }.Distinct().OrderBy(f => f).ToList();

        private static int Clamp(int fps) => Math.Clamp(fps, MinFps, MaxFps);

        private int _first = 30;
        private int _second = 60;
        private int _third = 120;
    }
}
