using System;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    public class TransitionMathTests
    {
        private const long Sec = 10_000_000; // 100ns ticks

        private static Item NewItem(long start = 0, long duration = 10 * Sec) => new Item
        {
            Id = Guid.NewGuid(),
            TimelineStartTicks = start,
            DurationTicks = duration,
        };

        // ------------------------------------------------------------------------ entry progress

        [Fact]
        public void Entry_progress_at_boundaries()
        {
            var item = NewItem();
            item.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };

            Assert.Equal(0, TransitionMath.EntryProgress(item, 0));                    // t = 0
            Assert.Equal(0.5, TransitionMath.EntryProgress(item, 1 * Sec), 10);        // mid
            Assert.Equal(1, TransitionMath.EntryProgress(item, 2 * Sec));              // exactly DurationTicks
            Assert.Equal(1, TransitionMath.EntryProgress(item, 5 * Sec));              // past end
        }

        [Fact]
        public void Exit_progress_at_boundaries()
        {
            var item = NewItem(); // [0, 10s)
            item.Exit = new Transition { Kind = TransitionKind.Fade, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };

            Assert.Equal(1, TransitionMath.ExitProgress(item, 5 * Sec));               // before the exit window
            Assert.Equal(1, TransitionMath.ExitProgress(item, 8 * Sec));               // exactly DurationTicks from the end
            Assert.Equal(0.5, TransitionMath.ExitProgress(item, 9 * Sec), 10);         // mid
            Assert.Equal(0, TransitionMath.ExitProgress(item, 10 * Sec));              // item end
            Assert.Equal(0, TransitionMath.ExitProgress(item, 11 * Sec));              // past end
        }

        [Fact]
        public void Missing_none_or_zero_duration_transitions_are_complete()
        {
            var item = NewItem();
            Assert.Equal(1, TransitionMath.EntryProgress(item, 0));
            Assert.Equal(1, TransitionMath.ExitProgress(item, 10 * Sec));

            item.Entry = new Transition { Kind = TransitionKind.None, DurationTicks = 2 * Sec };
            Assert.Equal(1, TransitionMath.EntryProgress(item, 0));

            item.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = 0 };
            Assert.Equal(1, TransitionMath.EntryProgress(item, 0));
        }

        [Fact]
        public void Transition_duration_clamps_to_item_duration()
        {
            var item = NewItem(duration: 4 * Sec);
            item.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = 100 * Sec, Easing = TransitionEasing.Linear };

            // effective duration is the item's own 4s
            Assert.Equal(0.5, TransitionMath.EntryProgress(item, 2 * Sec), 10);
            Assert.Equal(1, TransitionMath.EntryProgress(item, 4 * Sec));
        }

        // ------------------------------------------------------------------------------- easing

        [Theory]
        [InlineData(TransitionEasing.Linear, 0.5, 0.5)]
        [InlineData(TransitionEasing.CubicIn, 0.5, 0.125)]
        [InlineData(TransitionEasing.CubicOut, 0.5, 0.875)]
        [InlineData(TransitionEasing.CubicInOut, 0.25, 0.0625)]
        [InlineData(TransitionEasing.CubicInOut, 0.75, 0.9375)]
        [InlineData(TransitionEasing.CubicInOut, 0.5, 0.5)]
        public void Easing_curves(TransitionEasing easing, double t, double expected)
        {
            Assert.Equal(expected, Easing.Apply(easing, t), 10);
        }

        [Theory]
        [InlineData(TransitionEasing.Linear)]
        [InlineData(TransitionEasing.CubicIn)]
        [InlineData(TransitionEasing.CubicOut)]
        [InlineData(TransitionEasing.CubicInOut)]
        public void Easing_endpoints_and_clamping(TransitionEasing easing)
        {
            Assert.Equal(0, Easing.Apply(easing, 0));
            Assert.Equal(1, Easing.Apply(easing, 1));
            Assert.Equal(0, Easing.Apply(easing, -0.5)); // clamped
            Assert.Equal(1, Easing.Apply(easing, 1.5));  // clamped
        }

        [Fact]
        public void Entry_progress_applies_easing()
        {
            var item = NewItem();
            item.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = 2 * Sec, Easing = TransitionEasing.CubicIn };
            Assert.Equal(0.125, TransitionMath.EntryProgress(item, 1 * Sec), 10);

            item.Exit = new Transition { Kind = TransitionKind.Fade, DurationTicks = 2 * Sec, Easing = TransitionEasing.CubicOut };
            // exit at 9s: raw shown = 0.5, eased CubicOut = 0.875
            Assert.Equal(0.875, TransitionMath.ExitProgress(item, 9 * Sec), 10);
        }

        // ----------------------------------------------------------------------------- evaluate

        [Fact]
        public void Evaluate_without_transitions_is_identity()
        {
            var fx = TransitionMath.Evaluate(NewItem(), 5 * Sec);
            Assert.Equal(1, fx.Opacity);
            Assert.Equal(0, fx.OffsetXFrac);
            Assert.Equal(0, fx.OffsetYFrac);
            Assert.False(fx.HasWipe);
        }

        [Fact]
        public void Evaluate_fade_multiplies_opacity()
        {
            var item = NewItem();
            item.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            Assert.Equal(0.5, TransitionMath.Evaluate(item, 1 * Sec).Opacity, 10);
            Assert.Equal(1, TransitionMath.Evaluate(item, 2 * Sec).Opacity);
        }

        [Fact]
        public void Evaluate_slide_directions()
        {
            var item = NewItem();

            // SlideLeft: travels left on entry — enters from the right (+x offset while hidden).
            item.Entry = new Transition { Kind = TransitionKind.SlideLeft, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            var fx = TransitionMath.Evaluate(item, Sec / 2); // shown 0.25, hidden 0.75
            Assert.Equal(0.75, fx.OffsetXFrac, 10);
            Assert.Equal(0, fx.OffsetYFrac);
            Assert.Equal(1, fx.Opacity);

            // ...and exits to the left (mirrored: -x offset).
            item.Entry = null;
            item.Exit = new Transition { Kind = TransitionKind.SlideLeft, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            fx = TransitionMath.Evaluate(item, 10 * Sec - Sec / 2); // shown 0.25, hidden 0.75
            Assert.Equal(-0.75, fx.OffsetXFrac, 10);

            item.Exit = new Transition { Kind = TransitionKind.SlideDown, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            fx = TransitionMath.Evaluate(item, 10 * Sec - Sec / 2);
            Assert.Equal(0.75, fx.OffsetYFrac, 10); // travels down on exit too

            item.Exit = null;
            item.Entry = new Transition { Kind = TransitionKind.SlideUp, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            fx = TransitionMath.Evaluate(item, Sec / 2);
            Assert.Equal(0.75, fx.OffsetYFrac, 10); // travels up: enters from below (+y)
        }

        [Fact]
        public void Evaluate_wipe_bands()
        {
            var item = NewItem();

            item.Entry = new Transition { Kind = TransitionKind.Wipe, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            var fx = TransitionMath.Evaluate(item, 1 * Sec); // shown 0.5
            Assert.True(fx.HasWipe);
            Assert.Equal(0, fx.WipeFromFrac, 10);
            Assert.Equal(0.5, fx.WipeToFrac, 10);

            // exactly at DurationTicks the wipe is complete — no clip at all.
            fx = TransitionMath.Evaluate(item, 2 * Sec);
            Assert.False(fx.HasWipe);

            item.Entry = null;
            item.Exit = new Transition { Kind = TransitionKind.Wipe, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            fx = TransitionMath.Evaluate(item, 9 * Sec); // shown 0.5 — hides from the left
            Assert.True(fx.HasWipe);
            Assert.Equal(0.5, fx.WipeFromFrac, 10);
            Assert.Equal(1, fx.WipeToFrac, 10);
        }

        [Fact]
        public void Evaluate_overlapping_entry_and_exit_combine()
        {
            // item shorter than the sum of its transitions: both active at once.
            var item = NewItem(duration: 2 * Sec);
            item.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            item.Exit = new Transition { Kind = TransitionKind.Fade, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };

            var fx = TransitionMath.Evaluate(item, 1 * Sec); // entry shown 0.5, exit shown 0.5
            Assert.Equal(0.25, fx.Opacity, 10);              // opacities multiply

            item.Entry = new Transition { Kind = TransitionKind.Wipe, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            item.Exit = new Transition { Kind = TransitionKind.Wipe, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            fx = TransitionMath.Evaluate(item, 1 * Sec);     // bands [0,0.5] and [0.5,1] intersect
            Assert.True(fx.HasWipe);
            Assert.Equal(0.5, fx.WipeFromFrac, 10);
            Assert.Equal(0.5, fx.WipeToFrac, 10);            // empty band — nothing visible
        }
    }
}
