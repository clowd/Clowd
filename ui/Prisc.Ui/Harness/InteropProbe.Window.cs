using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Prisc.Interop;
using Prisc.UI.Helpers;

namespace Prisc.UI.Harness
{
    internal static partial class InteropProbe
    {
        /// <summary>The wrapper-level items of this area: one success, one documented failure and one
        /// round trip per export family, keyed <c>&lt;area&gt;.&lt;item&gt;</c>. The window items run on
        /// the probe window on the UI thread (the macOS main thread, which every chrome export but
        /// take_foreground requires); the cursor also runs from a pool thread. A call the OS does not
        /// have answers false (the wrappers' "not applied"), so every item is "ok" on both OSes and
        /// its value tells the two apart.</summary>
        static partial void ProbeWindow(ProbeRun run)
        {
            var window = run.Window;
            nint Handle() => WindowNativeExtensions.NativeWindow(window);

            run.Item("window.handle", () => Handle() != 0);

            // overlay setup: the open-time call every overlay window makes, and a refused flag
            run.Item("window.prepare_overlay", () => WindowChrome.PrepareOverlay(Handle(), OverlayFlags.CoverMenuBar));
            run.Item("window.prepare_overlay_unknown_flag", () =>
                WindowExpectFailure(() => WindowChrome.PrepareOverlay(Handle(), (OverlayFlags)16), InteropStatus.InvalidArg));
            run.Item("window.zero_handle", () =>
                WindowExpectFailure(() => WindowChrome.RaiseTopmostNoActivate(0), InteropStatus.InvalidArg));

            // the live toggles, each returned to the probe window's starting state
            run.Item("window.click_through_round_trip", () => new Dictionary<string, object>
            {
                ["on"] = WindowChrome.SetClickThrough(Handle(), true),
                ["off"] = WindowChrome.SetClickThrough(Handle(), false),
            });
            run.Item("window.capture_exclusion_round_trip", () => new Dictionary<string, object>
            {
                ["excluded"] = WindowChrome.SetExcludedFromCapture(Handle(), true),
                ["included"] = WindowChrome.SetExcludedFromCapture(Handle(), false),
            });
            run.Item("window.raise_topmost_no_activate", () => WindowChrome.RaiseTopmostNoActivate(Handle()));

            // the keyboard foreground: a read, the macOS main-thread rule, and the Windows take
            run.Item("window.is_foreground", () => WindowChrome.IsForeground(Handle()));
            run.Item("window.is_foreground_off_main_thread", () =>
            {
                var handle = Handle();
                var off = Task.Run(() =>
                {
                    try
                    {
                        return (object)WindowChrome.IsForeground(handle);
                    }
                    catch (InteropException e)
                    {
                        return e.Status.ToString();
                    }
                }).GetAwaiter().GetResult();
                // macOS refuses with State before touching the window; elsewhere any thread may ask
                var expected = OperatingSystem.IsMacOS() ? InteropStatus.State.ToString() : null;
                if (expected != null && !Equals(off, expected))
                    throw new InvalidOperationException($"expected {expected} off the main thread, got {off}");
                return off;
            });
            run.Item("window.take_foreground", () => WindowChrome.TakeForeground(Handle()));

            // geometry: the window's own bounds again, so nothing moves (false on macOS this phase)
            run.Item("window.set_bounds", () =>
            {
                var scaling = window.RenderScaling;
                return WindowChrome.SetBounds(Handle(), window.Position.X, window.Position.Y,
                                              (int)(window.Bounds.Width * scaling), (int)(window.Bounds.Height * scaling));
            });

            // the one-OS exports
            run.Item("window.traffic_lights", () => WindowChrome.SetTrafficLightPosition(Handle(), 14, 14));
            run.Item("window.default_proc", () => (long)WindowChrome.DefaultWindowProc(Handle(), 0 /* WM_NULL */, 0, 0));
            run.Item("window.foreground_watch", () =>
            {
                using var watch = ForegroundWatch.Start(() => { });
                return watch != null;
            });

            // the cursor needs no window and no particular thread
            run.Item("cursor.position", () =>
                NativeCursor.TryGetPosition(out var x, out var y) ? $"{x},{y}" : ProbeOutcome.Unavailable("the OS gave no cursor position"),
                anyThread: true);
        }

        /// <summary>A call that must fail with <paramref name="expected"/>: ok with the status when it
        /// does, an error when it succeeds or fails differently.</summary>
        private static object WindowExpectFailure(Func<object> call, InteropStatus expected)
        {
            try
            {
                call();
            }
            catch (InteropException e) when (e.Status == expected)
            {
                return e.Status.ToString();
            }
            throw new InvalidOperationException($"expected {expected}");
        }
    }
}
