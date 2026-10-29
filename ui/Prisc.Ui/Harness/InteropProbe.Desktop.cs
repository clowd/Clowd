using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Prisc.Interop;

namespace Prisc.UI.Harness
{
    internal static partial class InteropProbe
    {
        /// <summary>The wrapper-level items of this area: one success, one documented failure and one
        /// round trip per export family, keyed <c>&lt;area&gt;.&lt;item&gt;</c>. Every desktop export is
        /// any-thread, so every item also runs from a pool thread. The screen items are the values the
        /// E2E oracle (screens.swift) checks: bounds, scale and refresh rate per display.</summary>
        static partial void ProbeDesktop(ProbeRun run)
        {
            // screens
            run.Item("desktop.screens.list", () => NativeScreens.List().Select(DesktopDescribe).ToList(), anyThread: true);
            run.Item("desktop.screens.primary", () => NativeScreens.Primary() is { } p
                ? DesktopDescribe(p)
                : ProbeOutcome.Unavailable("no primary screen"), anyThread: true);
            run.Item("desktop.screens.lookups_agree", () =>
            {
                if (NativeScreens.Primary() is not { } p)
                    return ProbeOutcome.Unavailable("no primary screen");
                var byPoint = NativeScreens.AtPoint(p.BoundsX + 1, p.BoundsY + 1);
                var byRect = NativeScreens.AtRect(p.BoundsX + 1, p.BoundsY + 1, 1, 1);
                if (byPoint != p || byRect != p)
                    throw new InvalidOperationException($"primary {p}, at_point {byPoint}, at_rect {byRect}");
                return true;
            }, anyThread: true);
            run.Item("desktop.screens.off_screen", () => new Dictionary<string, object>
            {
                ["none"] = NativeScreens.AtPoint(-1_000_000, -1_000_000) is null,
                ["nearest"] = NativeScreens.AtPoint(-1_000_000, -1_000_000, ScreenFallback.Nearest) is not null,
                ["primary"] = NativeScreens.AtPoint(-1_000_000, -1_000_000, ScreenFallback.Primary)?.IsPrimary == true,
            }, anyThread: true);
            run.Item("desktop.screens.bad_fallback", () => DesktopExpectFailure(() => NativeScreens.AtPoint(0, 0, (ScreenFallback)9),
                InteropStatus.InvalidArg), anyThread: true);

            // pixels and the grab (Screen Recording on macOS: a refusal is "denied")
            run.Item("desktop.pixels.read", () => ScreenCapture.ReadPixel(1, 1) is { } rgb
                ? "#" + rgb.ToString("X6", CultureInfo.InvariantCulture)
                : ProbeOutcome.Unavailable("no screen at (1, 1)"), anyThread: true);
            run.Item("desktop.pixels.read_off_screen", () => ScreenCapture.ReadPixel(-1_000_000, -1_000_000) is null, anyThread: true);
            run.Item("desktop.pixels.grab", () =>
            {
                using var image = ScreenCapture.Grab(0, 0, 16, 16);
                var pixels = image.Pixels;
                var opaque = true;
                for (int i = 3; i < pixels.Length; i += 4)
                    opaque &= pixels[i] == 0xFF;
                return new Dictionary<string, object>
                {
                    ["size"] = $"{image.Width}x{image.Height}",
                    ["stride"] = image.Stride,
                    ["dip_scale"] = image.DipScale,
                    ["opaque"] = opaque,
                };
            }, anyThread: true);
            run.Item("desktop.pixels.grab_empty", () => DesktopExpectFailure(() => ScreenCapture.Grab(0, 0, 0, 16),
                InteropStatus.InvalidArg), anyThread: true);

            // idle time and the animation preference
            run.Item("desktop.idle.seconds", () => IdleTime.SinceLastInput() is { } t
                ? Math.Round(t.TotalSeconds, 3)
                : ProbeOutcome.Unavailable("the OS gave no idle time"), anyThread: true);
            run.Item("desktop.system.animations_enabled", () => SystemPreferences.AnimationsEnabled(), anyThread: true);
        }

        /// <summary>A call that must fail with <paramref name="expected"/>: ok with the status when it
        /// does, an error when it succeeds or fails differently.</summary>
        private static object DesktopExpectFailure(Func<object> call, InteropStatus expected)
        {
            try
            {
                (call() as IDisposable)?.Dispose();
            }
            catch (InteropException e) when (e.Status == expected)
            {
                return e.Status.ToString();
            }
            throw new InvalidOperationException($"expected {expected}");
        }

        private static Dictionary<string, object> DesktopDescribe(ScreenInfo s) => new()
        {
            ["bounds"] = $"{s.BoundsX},{s.BoundsY} {s.BoundsWidth}x{s.BoundsHeight}",
            ["work_area"] = s.WorkAreaKnown ? $"{s.WorkX},{s.WorkY} {s.WorkWidth}x{s.WorkHeight}" : null,
            ["scale"] = Math.Round(s.Scale, 4),
            ["refresh_hz"] = Math.Round(s.RefreshHz, 3),
            ["display_id"] = s.DisplayId,
            ["primary"] = s.IsPrimary,
        };
    }
}
