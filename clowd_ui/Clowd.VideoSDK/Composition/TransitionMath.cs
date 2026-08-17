using System;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The evaluated effect of an item's entry/exit transitions at one instant, in
    /// content-independent units so <see cref="FrameComposer"/> can apply it to any picture:
    /// an opacity multiplier, a positional offset in fractions of the item's own rendered
    /// extent, and (for wipes) the visible horizontal band of the item rect.
    /// </summary>
    public readonly struct ItemEffects
    {
        /// <summary>Multiplier on the item's opacity, 0..1.</summary>
        public double Opacity { get; init; }

        /// <summary>Slide offset as a fraction of the item's rendered width (positive = right).</summary>
        public double OffsetXFrac { get; init; }

        /// <summary>Slide offset as a fraction of the item's rendered height (positive = down).</summary>
        public double OffsetYFrac { get; init; }

        /// <summary>True when a wipe clip is active; the visible band is
        /// [<see cref="WipeFromFrac"/>, <see cref="WipeToFrac"/>] of the item rect's width.</summary>
        public bool HasWipe { get; init; }

        public double WipeFromFrac { get; init; }

        public double WipeToFrac { get; init; }

        /// <summary>No effect: fully opaque, no offset, no wipe.</summary>
        public static ItemEffects Identity => new ItemEffects { Opacity = 1, WipeToFrac = 1 };
    }

    /// <summary>
    /// Pure evaluation of entry/exit transition progress — the piece of the compositor that is
    /// testable without any pixels. Progress is expressed as the eased <b>shown fraction</b> of
    /// the item: 1 = fully present (transition complete / not started), 0 = fully out.
    /// </summary>
    public static class TransitionMath
    {
        /// <summary>
        /// Eased shown-fraction of the entry transition at <paramref name="timeTicks"/>:
        /// 0 at the item start, 1 at (and after) the transition's end. Returns 1 when the item
        /// has no active entry transition. The transition duration is clamped to the item's own
        /// duration.
        /// </summary>
        public static double EntryProgress(Item item, long timeTicks)
        {
            ArgumentNullException.ThrowIfNull(item);
            var tr = item.Entry;
            if (!IsActive(tr))
                return 1;

            long d = Math.Min(tr.DurationTicks, item.DurationTicks);
            if (d <= 0)
                return 1;

            long elapsed = timeTicks - item.TimelineStartTicks;
            double raw = elapsed <= 0 ? 0 : elapsed >= d ? 1 : elapsed / (double)d;
            return Easing.Apply(tr.Easing, raw);
        }

        /// <summary>
        /// Eased shown-fraction of the exit transition at <paramref name="timeTicks"/>:
        /// 1 at (and before) the transition's start, 0 at the item end and past it. Returns 1
        /// when the item has no active exit transition. The transition duration is clamped to
        /// the item's own duration.
        /// </summary>
        public static double ExitProgress(Item item, long timeTicks)
        {
            ArgumentNullException.ThrowIfNull(item);
            var tr = item.Exit;
            if (!IsActive(tr))
                return 1;

            long d = Math.Min(tr.DurationTicks, item.DurationTicks);
            if (d <= 0)
                return 1;

            long remaining = item.TimelineEndTicks - timeTicks;
            double raw = remaining <= 0 ? 0 : remaining >= d ? 1 : remaining / (double)d;
            return Easing.Apply(tr.Easing, raw);
        }

        /// <summary>
        /// Combines the entry and exit transitions of <paramref name="item"/> at
        /// <paramref name="timeTicks"/> into one <see cref="ItemEffects"/>. When both overlap
        /// (an item shorter than its transitions) opacities multiply, slide offsets add, and
        /// wipe bands intersect.
        /// </summary>
        public static ItemEffects Evaluate(Item item, long timeTicks)
        {
            ArgumentNullException.ThrowIfNull(item);

            double opacity = 1, dx = 0, dy = 0;
            bool hasWipe = false;
            double wipeFrom = 0, wipeTo = 1;

            // Ramp transitions modulate an effect/audio quantity over time (speed, zoom, volume);
            // they have no visual identity of their own, so the picture evaluation skips them.
            if (IsActive(item.Entry) && item.Entry.Kind != TransitionKind.Ramp)
            {
                double shown = EntryProgress(item, timeTicks);
                if (shown < 1)
                    Apply(item.Entry.Kind, shown, isExit: false, ref opacity, ref dx, ref dy, ref hasWipe, ref wipeFrom, ref wipeTo);
            }

            if (IsActive(item.Exit) && item.Exit.Kind != TransitionKind.Ramp)
            {
                double shown = ExitProgress(item, timeTicks);
                if (shown < 1)
                    Apply(item.Exit.Kind, shown, isExit: true, ref opacity, ref dx, ref dy, ref hasWipe, ref wipeFrom, ref wipeTo);
            }

            return new ItemEffects
            {
                Opacity = opacity,
                OffsetXFrac = dx,
                OffsetYFrac = dy,
                HasWipe = hasWipe,
                WipeFromFrac = wipeFrom,
                WipeToFrac = wipeTo,
            };
        }

        /// <summary>
        /// The combined effect of an entry/exit pair whose eased shown-fractions the caller has
        /// already worked out — what the keystroke overlay needs, since its animated units are
        /// <b>rows</b>, not items: a row has no timeline span to derive progress from, but it
        /// animates with exactly the same kinds. Slides ramp opacity here as well as offsetting:
        /// a row that slides out from behind its neighbour at full opacity reads as a glitch,
        /// where a whole picture sliding off the frame does not.
        /// </summary>
        public static ItemEffects EvaluateShown(TransitionKind entryKind, double entryShown,
            TransitionKind exitKind, double exitShown)
        {
            double opacity = 1, dx = 0, dy = 0;
            bool hasWipe = false;
            double wipeFrom = 0, wipeTo = 1;

            if (IsAnimated(entryKind) && entryShown < 1)
            {
                Apply(entryKind, entryShown, isExit: false, ref opacity, ref dx, ref dy, ref hasWipe, ref wipeFrom, ref wipeTo);
                if (IsSlide(entryKind))
                    opacity *= Math.Clamp(entryShown, 0, 1);
            }

            if (IsAnimated(exitKind) && exitShown < 1)
            {
                Apply(exitKind, exitShown, isExit: true, ref opacity, ref dx, ref dy, ref hasWipe, ref wipeFrom, ref wipeTo);
                if (IsSlide(exitKind))
                    opacity *= Math.Clamp(exitShown, 0, 1);
            }

            return new ItemEffects
            {
                Opacity = opacity,
                OffsetXFrac = dx,
                OffsetYFrac = dy,
                HasWipe = hasWipe,
                WipeFromFrac = wipeFrom,
                WipeToFrac = wipeTo,
            };
        }

        /// <summary>Kinds that move pixels. Ramp modulates an effect quantity and has no picture
        /// of its own; None is the absence of one.</summary>
        public static bool IsAnimated(TransitionKind kind)
            => kind != TransitionKind.None && kind != TransitionKind.Ramp;

        public static bool IsSlide(TransitionKind kind) => kind is TransitionKind.SlideLeft
            or TransitionKind.SlideRight or TransitionKind.SlideUp or TransitionKind.SlideDown;

        private static bool IsActive(Transition tr)
            => tr != null && tr.Kind != TransitionKind.None && tr.DurationTicks > 0;

        /// <summary>
        /// The direction of a slide/wipe is the direction the picture travels on <b>entry</b>;
        /// the exit mirrors it automatically — the picture keeps traveling the same way and
        /// leaves through the opposite side it came in from. SlideLeft therefore enters from the
        /// right (offset +hidden on entry) and exits to the left (offset -hidden). A wipe
        /// travels left-to-right: entry reveals the band [0, shown], exit hides from the left,
        /// leaving [1-shown, 1] visible.
        /// </summary>
        private static void Apply(TransitionKind kind, double shown, bool isExit,
            ref double opacity, ref double dx, ref double dy,
            ref bool hasWipe, ref double wipeFrom, ref double wipeTo)
        {
            double hidden = 1 - shown;
            switch (kind)
            {
                case TransitionKind.Fade:
                    opacity *= shown;
                    break;

                case TransitionKind.SlideLeft:
                    dx += isExit ? -hidden : hidden;
                    break;

                case TransitionKind.SlideRight:
                    dx += isExit ? hidden : -hidden;
                    break;

                case TransitionKind.SlideUp:
                    dy += isExit ? -hidden : hidden;
                    break;

                case TransitionKind.SlideDown:
                    dy += isExit ? hidden : -hidden;
                    break;

                case TransitionKind.Wipe:
                    hasWipe = true;
                    if (isExit)
                        wipeFrom = Math.Max(wipeFrom, hidden);
                    else
                        wipeTo = Math.Min(wipeTo, shown);
                    break;
            }
        }
    }
}
