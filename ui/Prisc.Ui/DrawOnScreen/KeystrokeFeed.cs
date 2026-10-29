using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;
using Prisc.Interop;

namespace Prisc.UI.DrawOnScreen
{
    /// <summary>One key edge for the keystroke overlay, already marshalled to the UI thread.
    /// <paramref name="Character"/> is the translated character of a press, or null for a key that
    /// types nothing; <paramref name="TimeMs"/> is on <see cref="KeystrokeFeed.NowMs"/>'s clock.</summary>
    internal readonly record struct KeystrokeEdge(bool IsDown, int Code, string Character, double TimeMs);

    /// <summary>
    /// Feeds Draw on Screen's keystroke overlay from the app's one global keyboard hook
    /// (<see cref="IGlobalTriggerHost.ObserveKeys"/> — the overlay shares the hotkeys' hook rather
    /// than starting its own).
    /// <para>
    /// The event-thread half does what has to happen close to the moment of the key, mirroring the
    /// input-capture recorder so a live overlay and a recorded one read the same: it stamps the
    /// time, takes the character a press typed (translated inside the hook at the edge, under the
    /// foreground layout), and releases keys whose key-up was lost to the secure desktop or the lock
    /// screen (<see cref="KeystrokeHeldKeys"/>). Everything is then posted to the UI thread in order.
    /// </para>
    /// </summary>
    internal sealed class KeystrokeFeed : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Action<KeystrokeEdge> _sink;

        // event thread only: what the hook has seen go down and not yet come up
        private readonly KeystrokeHeldKeys _held = new();

        private IDisposable _subscription;

        /// <param name="sink">Receives every edge, in order, on the UI thread.</param>
        public KeystrokeFeed(Action<KeystrokeEdge> sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        /// <summary>The clock the edges are stamped on, for drawing against them.</summary>
        public double NowMs => _clock.Elapsed.TotalMilliseconds;

        /// <summary>Whether the feed is observing the hook.</summary>
        public bool IsRunning => _subscription != null;

        /// <summary>Starts observing. A no-op without a hotkey host (harnesses that never set
        /// one up) or when already running.</summary>
        public void Start()
        {
            if (_subscription != null)
                return;

            _subscription = HotkeyManager.Current?.Host?.ObserveKeys(OnKey);
        }

        /// <summary>Stops observing; edges already posted still arrive.</summary>
        public void Stop()
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        public void Dispose() => Stop();

        // hotkey event thread: runs right after the hook queued the edge; the character was
        // already translated in the hook at the edge. Auto-repeats arrive as presses and are passed
        // on as such (LiveKeystrokes drops them).
        private void OnKey(GlobalKeyEvent key)
        {
            var now = NowMs;
            var edges = new List<KeystrokeEdge>();

            if (key.IsDown)
            {
                // a held key whose key-up never reached the hook: release it before this press is
                // read against it
                foreach (var code in _held.Down(key.RawCode, key.Modifiers, now, KeysDown))
                    edges.Add(new KeystrokeEdge(false, code, null, now));

                edges.Add(new KeystrokeEdge(true, key.RawCode, key.Character, now));
            }
            else
            {
                _held.Up(key.RawCode, key.Modifiers);
                edges.Add(new KeystrokeEdge(false, key.RawCode, null, now));
            }

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var edge in edges)
                    _sink(edge);
            });
        }

        /// <summary>Which of the stale keys are still held right now (not at the edge being handled;
        /// see <see cref="KeystrokeHeldKeys"/>), in one native call. Event thread, so a failure is
        /// logged and read as "all held": the hook is then trusted, as when the OS cannot say.</summary>
        private static ulong KeysDown(IReadOnlyList<int> codes)
        {
            try
            {
                return NativeKeyboard.KeysDown(codes);
            }
            catch (InteropException ex)
            {
                Debug.WriteLine("[KeystrokeFeed] key state query failed: " + ex.Message);
                return ulong.MaxValue;
            }
        }
    }
}
