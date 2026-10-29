using System;
using System.Collections.Generic;
using System.Linq;
using Prisc;
using Prisc.UI.DrawOnScreen;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The keystroke overlay's held-key bookkeeping runs behind the hook on the event thread, so the
    /// OS key state it can query is newer than the edge it is handling. These pin that a key-up
    /// still queued behind a press never breaks a chord apart, while a key-up the hook never saw
    /// (secure desktop, lock screen) is still released.
    /// </summary>
    public class KeystrokeHeldKeysTests
    {
        private const int VkLCtrl = 0xA2, VkLShift = 0xA0, VkA = 0x41, VkB = 0x42, VkC = 0x43, VkL = 0x4C;

        // the event thread is behind: by the time it handles an edge, every key is already up
        private static readonly Func<IReadOnlyList<int>, ulong> AllUp = _ => 0;
        private static readonly Func<IReadOnlyList<int>, ulong> AllDown = _ => ulong.MaxValue;

        private static KeystrokeHeldKeys Started()
        {
            var keys = new KeystrokeHeldKeys();
            // the first edge after observing starts only primes the modifier snapshot
            keys.Up(0x20, HotkeyModifiers.None);
            return keys;
        }

        [Fact]
        public void LaggingCtrlCKeepsTheChord()
        {
            var keys = Started();
            Assert.Empty(keys.Down(VkLCtrl, HotkeyModifiers.Control, 0, AllUp));
            Assert.Empty(keys.Down(VkC, HotkeyModifiers.Control, 80, AllUp));
        }

        [Fact]
        public void LaggingRollingTypingKeepsBothKeys()
        {
            var keys = Started();
            Assert.Empty(keys.Down(VkA, HotkeyModifiers.None, 0, AllUp));
            Assert.Empty(keys.Down(VkB, HotkeyModifiers.None, 60, AllUp));
            keys.Up(VkA, HotkeyModifiers.None);
            Assert.Empty(keys.Down(VkC, HotkeyModifiers.None, 120, AllUp));
        }

        [Fact]
        public void ModifierWhoseUpWasLostIsReleasedByTheHooksSnapshot()
        {
            var keys = Started();
            Assert.Empty(keys.Down(VkLCtrl, HotkeyModifiers.Control, 0, AllDown));
            // the hook reconciled Ctrl away at this press: the OS query is not even consulted
            Assert.Equal(new[] { VkLCtrl }, keys.Down(VkA, HotkeyModifiers.None, 50, AllDown));
            Assert.Empty(keys.Down(VkB, HotkeyModifiers.None, 100, AllDown));
        }

        [Fact]
        public void ModifierStaysWhileTheHookStillHoldsItsFlag()
        {
            var keys = Started();
            Assert.Empty(keys.Down(VkLShift, HotkeyModifiers.Shift, 0, AllUp));
            Assert.Empty(keys.Down(VkA, HotkeyModifiers.Shift, 5000, AllUp));
            keys.Up(VkA, HotkeyModifiers.Shift);
            Assert.Empty(keys.Down(VkB, HotkeyModifiers.Shift | HotkeyModifiers.Control, 9000, AllUp));
        }

        [Fact]
        public void QuietKeyWhoseUpWasLostIsReleasedOnceTheOsSaysUp()
        {
            var keys = Started();
            Assert.Empty(keys.Down(VkL, HotkeyModifiers.None, 0, AllUp));
            // Win+L: L's key-up went to the lock screen
            Assert.Empty(keys.Down(VkA, HotkeyModifiers.None, KeystrokeHeldKeys.StaleMs - 1, AllUp));
            keys.Up(VkA, HotkeyModifiers.None);
            Assert.Empty(keys.Down(VkB, HotkeyModifiers.None, 10_000, AllDown));
            keys.Up(VkB, HotkeyModifiers.None);
            Assert.Equal(new[] { VkL }, keys.Down(VkC, HotkeyModifiers.None, 10_100, AllUp));
        }

        [Fact]
        public void AutoRepeatKeepsAHeldKeyFresh()
        {
            var keys = Started();
            Assert.Empty(keys.Down(VkA, HotkeyModifiers.None, 0, AllUp));
            Assert.Empty(keys.Down(VkA, HotkeyModifiers.None, 1400, AllUp));
            Assert.Empty(keys.Down(VkB, HotkeyModifiers.None, 1600, AllUp));
        }

        [Fact]
        public void KeysHeldBeforeObservingStartedAreNotTakenForModifiers()
        {
            // no edge yet: Ctrl was already held, so A's press cannot be told from a Ctrl press
            var keys = new KeystrokeHeldKeys();
            Assert.Empty(keys.Down(VkA, HotkeyModifiers.Control, 0, AllUp));
            Assert.Empty(keys.Down(VkB, HotkeyModifiers.None, 50, AllUp));
        }

        [Fact]
        public void StaleKeysAreAskedAboutTogetherOnceAndOnlyTheUpOnesAreReleased()
        {
            var keys = Started();
            Assert.Empty(keys.Down(VkL, HotkeyModifiers.None, 0, AllUp));
            Assert.Empty(keys.Down(VkB, HotkeyModifiers.None, 10, AllUp));
            Assert.Empty(keys.Down(VkLCtrl, HotkeyModifiers.Control, 20, AllUp));

            var asked = new List<int[]>();
            // L is still held, B's key-up was lost; Ctrl is a modifier and is never asked about
            ulong Query(IReadOnlyList<int> codes)
            {
                asked.Add(codes.ToArray());
                ulong mask = 0;
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i] == VkL)
                        mask |= 1UL << i;
                }
                return mask;
            }

            Assert.Equal(new[] { VkB }, keys.Down(VkC, HotkeyModifiers.Control, 5000, Query));
            var only = Assert.Single(asked);
            Assert.Equal(new[] { VkB, VkL }, only.OrderBy(c => c));

            // L stays held and is asked about again on the next press
            asked.Clear();
            Assert.Empty(keys.Down(VkA, HotkeyModifiers.Control, 5100, Query));
            Assert.Equal(new[] { VkL }, Assert.Single(asked));

            // nothing quiet for long enough: the OS is not asked at all
            var fresh = Started();
            asked.Clear();
            Assert.Empty(fresh.Down(VkA, HotkeyModifiers.None, 0, Query));
            Assert.Empty(fresh.Down(VkB, HotkeyModifiers.None, KeystrokeHeldKeys.StaleMs - 1, Query));
            Assert.Empty(asked);
        }

        [Fact]
        public void At_most_the_query_limit_is_asked_per_press()
        {
            var keys = Started();
            for (int code = 1; code <= KeystrokeHeldKeys.MaxQueried + 10; code++)
                Assert.Empty(keys.Down(1000 + code, HotkeyModifiers.None, 0, AllUp));

            int largest = 0;
            var released = keys.Down(VkA, HotkeyModifiers.None, 5000, codes =>
            {
                largest = Math.Max(largest, codes.Count);
                return 0;
            });

            Assert.Equal(KeystrokeHeldKeys.MaxQueried, largest);
            Assert.Equal(KeystrokeHeldKeys.MaxQueried, released.Count);
            // the rest go on the next press
            Assert.Equal(10, keys.Down(VkB, HotkeyModifiers.None, 5001, AllUp).Count);
        }
    }
}
