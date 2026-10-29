using System;
using System.Threading.Tasks;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Interop.Tests
{
    /// <summary>The desktop family (screens, pixels, idle time, the animation preference): the
    /// struct layouts Rust pins, and one success, one failure and one round trip through the public
    /// wrappers. Every export here is any-thread, so xunit's pool threads are as good as any.</summary>
    public sealed class DesktopTests
    {
        [Fact]
        public unsafe void The_structs_have_the_layout_rust_pins()
        {
            PriscScreen s = default;
            byte* b = (byte*)&s;
            Assert.Equal(64, sizeof(PriscScreen));
            Assert.Equal(0, (byte*)&s.bounds - b);
            Assert.Equal(16, (byte*)&s.work_area - b);
            Assert.Equal(32, (byte*)&s.scale - b);
            Assert.Equal(40, (byte*)&s.refresh_hz - b);
            Assert.Equal(48, (byte*)&s.display_id - b);
            Assert.Equal(56, &s.is_primary - b);
            Assert.Equal(57, &s.work_area_known - b);
            Assert.Equal(58, s._reserved - b);

            PriscScreenImage i = default;
            byte* c = (byte*)&i;
            Assert.Equal(32, sizeof(PriscScreenImage));
            Assert.Equal(0, (byte*)&i.pixels - c);
            Assert.Equal(8, (byte*)&i.len - c);
            Assert.Equal(16, (byte*)&i.width - c);
            Assert.Equal(20, (byte*)&i.height - c);
            Assert.Equal(24, (byte*)&i.stride - c);
            Assert.Equal(28, (byte*)&i._reserved - c);
        }

        [Fact]
        public void The_screen_fallback_enum_matches_the_abi()
        {
            Assert.Equal(0, (int)ScreenFallback.None);
            Assert.Equal(1, (int)ScreenFallback.Nearest);
            Assert.Equal(2, (int)ScreenFallback.Primary);
        }

        [Fact]
        public async Task The_list_comes_from_a_pool_thread_and_has_one_primary()
        {
            InteropTestGate.Require();
            var screens = await Task.Run(NativeScreens.List);
            if (screens.Length == 0)
                Assert.Skip("this session reports no screens");

            Assert.Single(screens, s => s.IsPrimary);
            foreach (var s in screens)
            {
                Assert.True(s.BoundsWidth > 0 && s.BoundsHeight > 0, s.ToString());
                Assert.True(s.Scale > 0, s.ToString());
                Assert.True(s.RefreshHz == 0 || s.RefreshHz > 1, s.ToString());
                Assert.Equal(OperatingSystem.IsWindows(), s.WorkAreaKnown);
                Assert.True(s.WorkX >= s.BoundsX && s.WorkX + s.WorkWidth <= s.BoundsX + s.BoundsWidth, s.ToString());
                Assert.True(s.WorkY >= s.BoundsY && s.WorkY + s.WorkHeight <= s.BoundsY + s.BoundsHeight, s.ToString());
            }
        }

        [Fact]
        public void The_lookups_agree_and_off_screen_follows_the_fallback()
        {
            InteropTestGate.Require();
            if (NativeScreens.Primary() is not { } primary)
            {
                Assert.Skip("no primary screen");
                return;
            }

            Assert.True(primary.IsPrimary);
            Assert.Equal((0, 0), (primary.BoundsX, primary.BoundsY));

            var byPoint = NativeScreens.AtPoint(primary.BoundsX + 1, primary.BoundsY + 1);
            var byRect = NativeScreens.AtRect(primary.BoundsX + 1, primary.BoundsY + 1, 1, 1);
            Assert.Equal(primary, byPoint);
            Assert.Equal(byPoint, byRect);

            Assert.Null(NativeScreens.AtPoint(-1_000_000, -1_000_000));
            Assert.NotNull(NativeScreens.AtPoint(-1_000_000, -1_000_000, ScreenFallback.Nearest));
            Assert.True(NativeScreens.AtPoint(-1_000_000, -1_000_000, ScreenFallback.Primary)?.IsPrimary);
            Assert.Null(NativeScreens.AtRect(-1_000_000, -1_000_000, 5, 5));
        }

        [Fact]
        public void The_batch_refresh_rates_match_the_lookup_of_each_point()
        {
            InteropTestGate.Require();
            var screens = NativeScreens.List();
            var points = new (int X, int Y)[screens.Length + 1];
            for (int i = 0; i < screens.Length; i++)
                points[i] = (screens[i].BoundsX + screens[i].BoundsWidth / 2, screens[i].BoundsY + screens[i].BoundsHeight / 2);
            points[^1] = (-1_000_000, -1_000_000); // on no screen

            var rates = NativeScreens.RefreshRatesAt(points);
            Assert.Equal(points.Length, rates.Length);
            for (int i = 0; i < points.Length; i++)
                Assert.Equal(NativeScreens.AtPoint(points[i].X, points[i].Y)?.RefreshHz ?? 0, rates[i]);
            Assert.Empty(NativeScreens.RefreshRatesAt(ReadOnlySpan<(int, int)>.Empty));
        }

        [Fact]
        public void An_unknown_fallback_throws_invalid_arg_with_the_export_name()
        {
            InteropTestGate.Require();
            var e = Assert.Throws<InteropException>(() => NativeScreens.AtPoint(0, 0, (ScreenFallback)9));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
            Assert.Equal("prisc_screens_at_point", e.Operation);
        }

        [Fact]
        public unsafe void A_grab_is_opaque_bgra_and_copies_into_a_padded_buffer()
        {
            InteropTestGate.Require();
            if (NativeScreens.Primary() is not { } primary)
            {
                Assert.Skip("no primary screen");
                return;
            }

            ScreenImage image;
            try
            {
                image = ScreenCapture.Grab(primary.BoundsX, primary.BoundsY, 16, 16);
            }
            catch (InteropException e) when (e.Status is InteropStatus.Permission or InteropStatus.Unsupported)
            {
                Assert.Skip($"the screen cannot be read here: {e.Status}");
                return;
            }

            using (image)
            {
                int perUnit = image.Width / 16;
                Assert.True(perUnit >= 1);
                Assert.Equal(16 * perUnit, image.Width);
                Assert.Equal(16 * perUnit, image.Height);
                Assert.Equal(image.Width * 4, image.Stride);
                var pixels = image.Pixels;
                Assert.Equal(image.Stride * image.Height, pixels.Length);
                for (int i = 3; i < pixels.Length; i += 4)
                    Assert.Equal(0xFF, pixels[i]);

                int stride = image.Stride + 12;
                var padded = new byte[stride * image.Height];
                fixed (byte* p = padded)
                {
                    image.CopyTo((IntPtr)p, stride);
                }
                for (int y = 0; y < image.Height; y++)
                    Assert.True(pixels.Slice(y * image.Stride, image.Stride).SequenceEqual(padded.AsSpan(y * stride, image.Stride)));
                Assert.Throws<ArgumentOutOfRangeException>(() => image.CopyTo((IntPtr)1, image.Stride - 1));
            }

            // disposed: the pixels are gone and a second dispose is harmless
            Assert.Throws<ObjectDisposedException>(() => image.Pixels.Length);
            image.Dispose();
        }

        [Fact]
        public void A_grab_without_area_is_invalid_arg()
        {
            InteropTestGate.Require();
            var e = Assert.Throws<InteropException>(() => ScreenCapture.Grab(0, 0, 0, 10));
            Assert.True(e.Status is InteropStatus.InvalidArg or InteropStatus.Unsupported, e.Status.ToString());
        }

        [Fact]
        public void A_pixel_off_every_screen_is_null()
        {
            InteropTestGate.Require();
            try
            {
                Assert.Null(ScreenCapture.ReadPixel(-1_000_000, -1_000_000));
                if (NativeScreens.Primary() is { } primary)
                    Assert.True(ScreenCapture.ReadPixel(primary.BoundsX + 1, primary.BoundsY + 1) <= 0xFF_FF_FF);
            }
            catch (InteropException e) when (e.Status is InteropStatus.Permission or InteropStatus.Unsupported)
            {
                Assert.Skip($"the screen cannot be read here: {e.Status}");
            }
        }

        [Fact]
        public async Task The_idle_time_is_plausible_from_any_thread()
        {
            InteropTestGate.Require();
            var idle = await Task.Run(IdleTime.SinceLastInput);
            if (idle is not { } t)
            {
                Assert.Skip("the OS gave no idle time");
                return;
            }
            Assert.InRange(t.TotalSeconds, 0, 365 * 24 * 3600);
        }

        [Fact]
        public void The_animation_preference_answers()
        {
            InteropTestGate.Require();
            bool enabled = SystemPreferences.AnimationsEnabled();
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                Assert.True(enabled);
        }
    }
}
