using System.Linq;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Draw on Screen's live keystroke overlay: key edges in, the editor's own runs and drawing out.
    /// The segmentation itself is <see cref="KeyboardLayoutTests"/>' business; these pin what the
    /// live buffer adds on top — repeat dropping, forgetting history only at a quiet moment, the
    /// idle schedule — and which way the shared renderer stacks.
    /// </summary>
    public class LiveKeystrokesTests
    {
        private const int VkCtrl = 162, VkK = 75, VkA = 65, VkB = 66;

        private static LiveKeystrokes NewKeys(KeystrokeFilter filter = KeystrokeFilter.None) => new()
        {
            Style = new KeyboardContent { LingerMs = 1000, PauseBreakMs = 1000, Filter = filter },
            Entry = new Transition { Kind = TransitionKind.SlideUp, DurationTicks = 300 * 10_000 },
            Exit = new Transition { Kind = TransitionKind.Fade, DurationTicks = 300 * 10_000 },
        };

        private static void Tap(LiveKeystrokes keys, int code, string ch, double t)
        {
            keys.KeyDown(code, ch, t);
            keys.KeyUp(code, t + 30);
        }

        [Fact]
        public void Typing_forms_one_run_as_it_is_typed()
        {
            var keys = NewKeys();
            Tap(keys, VkA, "a", 0);
            Tap(keys, VkB, "b", 100);

            var run = Assert.Single(keys.CurrentRuns);
            Assert.Equal("ab", run.FullText);
            Assert.False(run.IsChord);
        }

        [Fact]
        public void A_held_key_repeating_is_one_press()
        {
            var keys = NewKeys();
            keys.KeyDown(VkA, "a", 0);
            keys.KeyDown(VkA, "a", 500);
            keys.KeyDown(VkA, "a", 530);
            keys.KeyUp(VkA, 560);

            Assert.Equal("a", Assert.Single(keys.CurrentRuns).FullText);
        }

        [Fact]
        public void Modifier_and_key_draw_as_a_chord()
        {
            var keys = NewKeys();
            keys.KeyDown(VkCtrl, null, 0);
            Tap(keys, VkK, null, 50);
            keys.KeyUp(VkCtrl, 120);

            var run = Assert.Single(keys.CurrentRuns);
            Assert.True(run.IsChord);
            Assert.Equal("Ctrl+K", run.FullText);
        }

        [Fact]
        public void History_is_forgotten_once_everything_on_screen_has_left()
        {
            var keys = NewKeys();
            Tap(keys, VkA, "a", 0);
            // long past linger + exit and the new-row gap, nothing held
            Tap(keys, VkB, "b", 5000);

            Assert.Equal("b", Assert.Single(keys.CurrentRuns).FullText);
        }

        [Fact]
        public void A_modifier_held_through_a_long_pause_still_chords()
        {
            // the quiet-moment reset must not drop the modifier's own key-down
            var keys = NewKeys();
            keys.KeyDown(VkCtrl, null, 0);
            Tap(keys, VkK, null, 5000);

            var run = Assert.Single(keys.CurrentRuns);
            Assert.True(run.IsChord);
            Assert.Equal("Ctrl+K", run.FullText);
        }

        [Fact]
        public void Shortcuts_filter_shows_no_plain_typing()
        {
            var keys = NewKeys(KeystrokeFilter.Shortcuts);
            Tap(keys, VkA, "a", 0);
            Assert.Empty(keys.CurrentRuns);

            keys.KeyDown(VkCtrl, null, 100);
            Tap(keys, VkK, null, 150);
            Assert.Equal("Ctrl+K", Assert.Single(keys.CurrentRuns).FullText);
        }

        [Fact]
        public void Filter_change_resegments_the_same_history()
        {
            var keys = NewKeys();
            Tap(keys, VkA, "a", 0);
            Assert.Single(keys.CurrentRuns);

            keys.Style.Filter = KeystrokeFilter.Shortcuts;
            Assert.Empty(keys.CurrentRuns);
        }

        [Fact]
        public void NextChange_animates_then_waits_for_the_linger_then_idles()
        {
            var keys = NewKeys();
            Assert.Null(keys.NextChangeMs(0));

            Tap(keys, VkA, "a", 0);
            // entry (300ms) playing: every frame
            Assert.Equal(100, keys.NextChangeMs(100));
            // settled: nothing moves until the linger (1000ms after the last press) runs out
            Assert.Equal(1000, keys.NextChangeMs(500));
            // leaving: every frame again
            Assert.Equal(1100, keys.NextChangeMs(1100));
            // gone
            Assert.Null(keys.NextChangeMs(2000));
        }

        [Fact]
        public void Clear_forgets_shown_and_held_keys()
        {
            var keys = NewKeys();
            keys.KeyDown(VkCtrl, null, 0);
            Tap(keys, VkK, null, 50);

            keys.Clear();

            Assert.Empty(keys.CurrentRuns);
            Assert.Empty(keys.HeldKeys);
        }

        [Theory]
        [InlineData(KeystrokeStacking.Up)]
        [InlineData(KeystrokeStacking.Down)]
        public void Rows_grow_away_from_the_anchor_in_the_stacking_direction(KeystrokeStacking stacking)
        {
            var keys = NewKeys();
            keys.Style.BackgroundColor = 0xFFFF0000;
            keys.Entry = null; // no slide: rows sit exactly on their slots
            Tap(keys, VkA, "a", 0);
            Tap(keys, VkB, "b", 1050); // past the new-row gap: its own row, the first still leaving

            const int size = 400, anchorY = 200;
            using var bitmap = new SKBitmap(size, size);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                keys.Draw(canvas, 1100, 1f, size / 2f, anchorY, size, KeystrokeAlignment.Center, stacking, 1.0);
            }

            bool InkIn(int y0, int y1) => Enumerable.Range(y0, y1 - y0)
                .Any(y => Enumerable.Range(0, size).Any(x => bitmap.GetPixel(x, y).Alpha > 0));

            if (stacking == KeystrokeStacking.Up)
            {
                Assert.True(InkIn(0, anchorY));
                Assert.False(InkIn(anchorY + 1, size));
            }
            else
            {
                Assert.True(InkIn(anchorY, size));
                Assert.False(InkIn(0, anchorY - 1));
            }
        }

        [Theory]
        [InlineData(KeystrokeAlignment.Left)]
        [InlineData(KeystrokeAlignment.Right)]
        public void Lines_hang_off_the_anchor_on_their_alignment_side(KeystrokeAlignment alignment)
        {
            var keys = NewKeys();
            keys.Style.BackgroundColor = 0xFFFF0000;
            keys.Entry = null;
            Tap(keys, VkA, "a", 0);

            const int size = 400, anchorX = 200;
            using var bitmap = new SKBitmap(size, size);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                keys.Draw(canvas, 500, 1f, anchorX, 300, size, alignment, KeystrokeStacking.Up, 1.0);
            }

            bool InkIn(int x0, int x1) => Enumerable.Range(x0, x1 - x0)
                .Any(x => Enumerable.Range(0, size).Any(y => bitmap.GetPixel(x, y).Alpha > 0));

            if (alignment == KeystrokeAlignment.Left)
            {
                Assert.True(InkIn(anchorX, size));
                Assert.False(InkIn(0, anchorX - 1));
            }
            else
            {
                Assert.True(InkIn(0, anchorX));
                Assert.False(InkIn(anchorX + 1, size));
            }
        }
    }
}
