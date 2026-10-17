using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>One key edge for the keystroke overlay, already marshalled to the UI thread.
    /// <paramref name="Character"/> is the translated character of a press, or null for a key that
    /// types nothing; <paramref name="TimeMs"/> is on <see cref="KeystrokeFeed.NowMs"/>'s clock.</summary>
    internal readonly record struct KeystrokeEdge(bool IsDown, int Code, string Character, double TimeMs);

    /// <summary>
    /// Feeds Draw on Screen's keystroke overlay from the app's one global keyboard hook
    /// (<see cref="IGlobalTriggerHost.ObserveKeys"/> — libuiohook runs a single hook per process,
    /// so the overlay shares the hotkeys' rather than starting its own).
    /// <para>
    /// The hook-thread half does what has to happen at the moment of the key, mirroring the
    /// input-capture recorder so a live overlay and a recorded one read the same: it stamps the
    /// time, translates the character a press types (the hook's own KeyTyped events are switched
    /// off app-wide, see <c>App.SetupGlobalHotkeys</c>), and releases keys the OS no longer holds
    /// — a key-up lost to the secure desktop or the lock screen would otherwise read as held
    /// forever and turn all later typing into chords. Everything is then posted to the UI thread
    /// in order.
    /// </para>
    /// </summary>
    internal sealed class KeystrokeFeed : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Action<KeystrokeEdge> _sink;

        // hook thread only: what the hook has seen go down and not yet come up
        private readonly HashSet<int> _held = new();

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

            _subscription = HotkeyManager.Current?.Host?.ObserveKeys(OnHookKey);
        }

        /// <summary>Stops observing; edges already posted still arrive.</summary>
        public void Stop()
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        public void Dispose() => Stop();

        // hook thread
        private void OnHookKey(GlobalKeyEvent key)
        {
            var now = NowMs;
            List<KeystrokeEdge> edges = null;

            if (key.IsDown)
            {
                // a key the OS says is up never got its key-up through to the hook: release it
                // before this press is read against it
                foreach (var code in _held)
                {
                    if (code != key.RawCode && !KeyInput.IsPhysicallyDown(code))
                        (edges ??= new()).Add(new KeystrokeEdge(false, code, null, now));
                }

                if (edges != null)
                {
                    foreach (var edge in edges)
                        _held.Remove(edge.Code);
                }

                _held.Add(key.RawCode);
                (edges ??= new()).Add(new KeystrokeEdge(true, key.RawCode, KeyInput.Translate(key.RawCode), now));
            }
            else
            {
                _held.Remove(key.RawCode);
                edges = new() { new KeystrokeEdge(false, key.RawCode, null, now) };
            }

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var edge in edges)
                    _sink(edge);
            });
        }

        /// <summary>The platform half: the character a key types, and whether a key is really held.
        /// Hook thread.</summary>
        private static class KeyInput
        {
            /// <summary>
            /// Best-effort character for a press, or null. Windows mirrors the recorder's
            /// translate_char: the keyboard state is rebuilt from the async modifier state (the
            /// hook thread's own queue never sees the keystrokes, so GetKeyboardState is stale), the
            /// layout is the foreground window's, and flag 0x4 keeps ToUnicodeEx from clobbering
            /// the foreground app's dead-key state. macOS asks CoreGraphics to type the key under
            /// the current layout and modifier flags. Control characters are dropped either way.
            /// </summary>
            public static string Translate(int code)
            {
                try
                {
                    string text = null;
                    if (OperatingSystem.IsWindows())
                        text = TranslateWindows(code);
                    else if (OperatingSystem.IsMacOS())
                        text = TranslateMac(code);

                    if (string.IsNullOrEmpty(text))
                        return null;
                    foreach (var c in text)
                    {
                        if (char.IsControl(c))
                            return null;
                    }
                    return text;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[KeystrokeFeed] key translation failed: " + ex.Message);
                    return null;
                }
            }

            public static bool IsPhysicallyDown(int code)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                        return (GetAsyncKeyState(code) & 0x8000) != 0;
                    if (OperatingSystem.IsMacOS())
                        return CGEventSourceKeyState(CombinedSessionState, (ushort)code);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[KeystrokeFeed] key state query failed: " + ex.Message);
                }

                // unknown: trust the hook
                return true;
            }

            private static string TranslateWindows(int vk)
            {
                var state = new byte[256];
                foreach (var modifier in new[] { VK_SHIFT, VK_CONTROL, VK_MENU })
                {
                    if ((GetAsyncKeyState(modifier) & 0x8000) != 0)
                        state[modifier] = 0x80;
                }
                // toggle state (low bit): capitalization
                if ((GetKeyState(VK_CAPITAL) & 1) != 0)
                    state[VK_CAPITAL] = 1;

                var foreground = GetForegroundWindow();
                var layout = GetKeyboardLayout(GetWindowThreadProcessId(foreground, IntPtr.Zero));
                var scan = MapVirtualKeyExW((uint)vk, MAPVK_VK_TO_VSC, layout);

                var buffer = new char[8];
                int n = ToUnicodeEx((uint)vk, scan, state, buffer, buffer.Length, 0x4, layout);
                return n < 1 ? null : new string(buffer, 0, Math.Min(n, buffer.Length));
            }

            private static string TranslateMac(int code)
            {
                var ev = CGEventCreateKeyboardEvent(IntPtr.Zero, (ushort)code, true);
                if (ev == IntPtr.Zero)
                    return null;
                try
                {
                    // the live modifier flags, caps lock included, so the synthetic event types
                    // what the real one did
                    CGEventSetFlags(ev, CGEventSourceFlagsState(CombinedSessionState));
                    var buffer = new char[8];
                    CGEventKeyboardGetUnicodeString(ev, (nuint)buffer.Length, out var length, buffer);
                    int n = (int)Math.Min(length, (nuint)buffer.Length);
                    return n < 1 ? null : new string(buffer, 0, n);
                }
                finally
                {
                    CFRelease(ev);
                }
            }

            private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14;
            private const uint MAPVK_VK_TO_VSC = 0;

            [DllImport("user32.dll")]
            private static extern short GetAsyncKeyState(int vKey);

            [DllImport("user32.dll")]
            private static extern short GetKeyState(int nVirtKey);

            [DllImport("user32.dll")]
            private static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

            [DllImport("user32.dll")]
            private static extern IntPtr GetKeyboardLayout(uint idThread);

            [DllImport("user32.dll")]
            private static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr dwhkl);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
                [Out] char[] pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

            private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
            private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

            // kCGEventSourceStateCombinedSessionState
            private const int CombinedSessionState = 0;

            [DllImport(CoreGraphics)]
            [return: MarshalAs(UnmanagedType.U1)]
            private static extern bool CGEventSourceKeyState(int stateId, ushort key);

            [DllImport(CoreGraphics)]
            private static extern ulong CGEventSourceFlagsState(int stateId);

            [DllImport(CoreGraphics)]
            private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey,
                [MarshalAs(UnmanagedType.U1)] bool keyDown);

            [DllImport(CoreGraphics)]
            private static extern void CGEventSetFlags(IntPtr ev, ulong flags);

            [DllImport(CoreGraphics, CharSet = CharSet.Unicode)]
            private static extern void CGEventKeyboardGetUnicodeString(IntPtr ev, nuint maxLength,
                out nuint actualLength, [Out] char[] unicodeString);

            [DllImport(CoreFoundation)]
            private static extern void CFRelease(IntPtr cf);
        }
    }
}
