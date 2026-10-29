using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Prisc.PlatformUtil;
using Prisc.UI.Helpers;
using Prisc.Util;
using Prisc.VideoSDK;
using Prisc.VideoSDK.Media;

namespace Prisc.UI.Harness
{
    internal static partial class InteropProbe
    {
        /// <summary>
        /// One item per public C# facade the migration keeps (their signatures do not change in any
        /// work package), so the same probe run on the old C# code and on the new native code is the
        /// equality oracle. Read-only for the area work packages; wrapper-level items go in the area
        /// partials.
        /// </summary>
        private static void Facades(ProbeRun run)
        {
            var window = run.Window;

            // desktop: screens, pixels, idle, cursor
            run.Item("facade.screens.all", () => DesktopScreens.All(window).Select(Describe).ToList());
            run.Item("facade.screens.primary", () => Describe(DesktopScreens.Primary(window)));
            run.Item("facade.screens.from_point", () =>
            {
                var primary = DesktopScreens.Primary(window);
                return primary == null ? null : Describe(DesktopScreens.FromPoint(window, primary.Bounds.Center));
            });
            run.Item("facade.screens.from_rect", () =>
            {
                var primary = DesktopScreens.Primary(window);
                return primary == null ? null : Describe(DesktopScreens.FromRect(window, new PixelRect(primary.Bounds.Position, new PixelSize(1, 1))));
            });
            run.Item("facade.pixels.supported", () => ScreenPixels.IsSupported, anyThread: true);
            run.Item("facade.pixels.permitted", () => ScreenPixels.IsPermitted, anyThread: true);
            run.Item("facade.pixels.read", () =>
            {
                if (!ScreenPixels.IsSupported)
                    return ProbeOutcome.Unsupported("no screen pixels on this OS");
                if (!ScreenPixels.IsPermitted)
                    return ProbeOutcome.Denied("Screen Recording is not granted");
                var color = ScreenPixels.GetColorAt(new PixelPoint(1, 1));
                return color is { } c ? Hex(c) : ProbeOutcome.Unavailable("no color at (1, 1)");
            });
            run.Item("facade.pixels.grab", () =>
            {
                if (!ScreenPixels.IsSupported)
                    return ProbeOutcome.Unsupported("no screen grab on this OS");
                if (!ScreenPixels.IsPermitted)
                    return ProbeOutcome.Denied("Screen Recording is not granted");
                using var bitmap = ScreenPixels.Grab(new PixelRect(0, 0, 16, 16));
                return bitmap == null ? ProbeOutcome.Unavailable("grab returned nothing") : $"{bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}";
            });
            run.Item("facade.idle.seconds", () =>
            {
                var idle = IdleMonitor.GetSystemIdleTime();
                return idle is { } t ? Math.Round(t.TotalSeconds, 3) : ProbeOutcome.Unavailable("the OS gave no idle time");
            }, anyThread: true);
            run.Item("facade.cursor.position", () =>
                CursorPosition.TryGet(out var pt) ? $"{pt.X},{pt.Y}" : ProbeOutcome.Unavailable("no cursor position"));

            // window chrome, on the probe window (the members every caller keeps)
            run.Item("facade.window.is_foreground", () => WindowNativeExtensions.IsForeground(window));
            run.Item("facade.window.raise_topmost_no_activate", () =>
            {
                WindowNativeExtensions.RaiseTopmostNoActivate(window);
                return true;
            });
            run.Item("facade.window.excluded_from_capture", () =>
            {
                WindowNativeExtensions.SetExcludedFromScreenCapture(window, true);
                WindowNativeExtensions.SetExcludedFromScreenCapture(window, false);
                return true;
            });
            run.Item("facade.window.traffic_lights", () =>
            {
                if (!OperatingSystem.IsMacOS())
                    return ProbeOutcome.Unsupported("macOS only");
                WindowNativeExtensions.SetTrafficLightPosition(window, 14, 14);
                return true;
            });
            run.Item("facade.window.watch_foreground", () =>
            {
                using var watch = WindowNativeExtensions.WatchForeground(() => { });
                return watch != null;
            });

            // permissions (query only: never requests)
            run.Item("facade.permissions.screen_recording", () => MacPermissions.HasScreenRecording, anyThread: true);
            run.Item("facade.permissions.accessibility", () => MacPermissions.HasAccessibility, anyThread: true);

            // gestures
            run.Item("facade.gesture.parse", () => HotkeyGesture.Parse("ctrl+shift+a").ToSerializedString(), anyThread: true);
            run.Item("facade.gesture.display", () => HotkeyGesture.Parse("Control+Alt+PrintScreen").ToString(), anyThread: true);
            run.Item("facade.gesture.reserved", () => new Dictionary<string, object>
            {
                ["windows.meta_l"] = HotkeyGesture.IsSystemReserved(HotkeyGesture.Parse("Meta+L"), OSPlatform.Windows),
                ["macos.meta_space"] = HotkeyGesture.IsSystemReserved(HotkeyGesture.Parse("Meta+Space"), OSPlatform.OSX),
                ["windows.ctrl_shift_a"] = HotkeyGesture.IsSystemReserved(HotkeyGesture.Parse("Control+Shift+A"), OSPlatform.Windows),
            }, anyThread: true);

            // pure logic
            run.Item("facade.accent.default", () => Hex(AccentColors.Default), anyThread: true);
            run.Item("facade.accent.contrast", () => Hex(AccentColors.EnsureContrastWithWhite(Color.FromRgb(0xFF, 0xD7, 0x00))), anyThread: true);
            run.Item("facade.accent.comet", () =>
            {
                var (body, head) = AccentColors.CometColors(AccentColors.PriscBlue);
                return new[] { Hex(body), Hex(head) };
            }, anyThread: true);
            run.Item("facade.oklch.round_trip", () =>
            {
                var lch = OkLchColor.FromColor(Color.FromRgb(0x3B, 0x97, 0xD2));
                return new Dictionary<string, object>
                {
                    ["l"] = Math.Round(lch.L, 6),
                    ["c"] = Math.Round(lch.C, 6),
                    ["h"] = Math.Round(lch.H, 4),
                    ["back"] = Hex(lch.ToColor()),
                };
            }, anyThread: true);
            run.Item("facade.share_region.clamp", () => Describe(ShareRegionGeometry.Clamp(new ScreenRect(10, 20, 5, 5))), anyThread: true);
            run.Item("facade.share_region.drag", () => Describe(ShareRegionGeometry.ApplyDrag(new ScreenRect(100, 100, 400, 300), 3, 50, -20)), anyThread: true);
            run.Item("facade.h264", () => new[] { VideoEncoder.Software, VideoEncoder.Nvenc, VideoEncoder.VideoToolbox }
                .Select(encoder => H264EncoderSettings.For(encoder, 23, 1920, 1080, 30000, 1001).Description)
                .ToList(), anyThread: true);

            // the FFmpeg libraries of the recorder payload (PRISC_HELPERS_DIR or beside the app),
            // resolved the way every production caller resolves them
            run.Item("facade.ffmpeg.load", () => FFmpegLoader.TryInitialize(FFmpegLoader.FindBesideRecorder)
                ? FFmpegLoader.LibrariesDirectory
                : ProbeOutcome.Unavailable(FFmpegLoader.FailureReason ?? "FFmpeg is not available"));
        }

        private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private static string Describe(PixelRect r) => $"{r.X},{r.Y} {r.Width}x{r.Height}";

        private static string Describe(ScreenRect r) => $"{r.X},{r.Y} {r.Width}x{r.Height}";

        private static Dictionary<string, object> Describe(DesktopScreen screen) => screen == null
            ? null
            : new Dictionary<string, object>
            {
                ["bounds"] = Describe(screen.Bounds),
                ["work_area"] = Describe(screen.WorkingArea),
                ["scaling"] = Math.Round(screen.Scaling, 4),
                ["refresh_hz"] = Math.Round(screen.RefreshRate, 3).ToString(CultureInfo.InvariantCulture),
                ["primary"] = screen.IsPrimary,
            };
    }
}
