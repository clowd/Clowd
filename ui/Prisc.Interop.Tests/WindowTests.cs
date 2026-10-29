using System;
using System.Threading.Tasks;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Interop.Tests
{
    /// <summary>The window chrome and cursor family through the public wrappers: the layouts Rust
    /// pins, the flag values, the caller errors that throw, the macOS main-thread rule (xunit never
    /// runs on the main thread, so a macOS chrome call here must be refused before the bogus handle is
    /// touched) and the off-OS answers. The effect of each call on a real window is checked by the Rust
    /// tests (main_thread_window on macOS, tests/window.rs on Windows) and by the interop probe.</summary>
    public sealed class WindowTests
    {
        /// <summary>No window: a handle no window system hands out, never dereferenced by a call
        /// that is refused first.</summary>
        private const nint Bogus = 1;

        [Fact]
        public unsafe void The_structs_have_the_layout_rust_pins()
        {
            PriscRect r = default;
            byte* b = (byte*)&r;
            Assert.Equal(16, sizeof(PriscRect));
            Assert.Equal(0, (byte*)&r.x - b);
            Assert.Equal(4, (byte*)&r.y - b);
            Assert.Equal(8, (byte*)&r.width - b);
            Assert.Equal(12, (byte*)&r.height - b);

            PriscPoint p = default;
            Assert.Equal(8, sizeof(PriscPoint));
            Assert.Equal(4, (byte*)&p.y - (byte*)&p);
        }

        [Fact]
        public void The_overlay_flags_are_the_rust_bits()
        {
            Assert.True(typeof(OverlayFlags).IsDefined(typeof(FlagsAttribute), false));
            Assert.Equal(0u, (uint)OverlayFlags.None);
            Assert.Equal(1u, (uint)OverlayFlags.CoverMenuBar);
            Assert.Equal(2u, (uint)OverlayFlags.OpaqueLayered);
            Assert.Equal(4u, (uint)OverlayFlags.ClickThrough);
            Assert.Equal(8u, (uint)OverlayFlags.TakesMouse);
        }

        [Fact]
        public void A_zero_handle_is_a_caller_bug_on_every_thread()
        {
            InteropTestGate.Require();
            void Invalid(Action call, string operation)
            {
                var e = Assert.Throws<InteropException>(call);
                Assert.Equal(InteropStatus.InvalidArg, e.Status);
                Assert.Equal(operation, e.Operation);
            }

            Invalid(() => WindowChrome.PrepareOverlay(0, OverlayFlags.CoverMenuBar), "prisc_window_prepare_overlay");
            Invalid(() => WindowChrome.SetClickThrough(0, true), "prisc_window_set_click_through");
            Invalid(() => WindowChrome.RaiseTopmostNoActivate(0), "prisc_window_raise_topmost_no_activate");
            Invalid(() => WindowChrome.SetExcludedFromCapture(0, true), "prisc_window_set_excluded_from_capture");
            Invalid(() => WindowChrome.IsForeground(0), "prisc_window_is_foreground");
            Invalid(() => WindowChrome.TakeForeground(0), "prisc_window_take_foreground");
            Invalid(() => WindowChrome.SetBounds(0, 0, 0, 10, 10), "prisc_window_set_bounds");
        }

        [Fact]
        public void Unknown_flags_and_negative_sizes_are_refused_before_the_window_is_touched()
        {
            InteropTestGate.Require();
            Assert.Equal(InteropStatus.InvalidArg,
                Assert.Throws<InteropException>(() => WindowChrome.PrepareOverlay(Bogus, (OverlayFlags)16)).Status);
            Assert.Equal(InteropStatus.InvalidArg,
                Assert.Throws<InteropException>(() => WindowChrome.SetBounds(Bogus, 0, 0, -1, 10)).Status);
        }

        [Fact]
        public async Task The_macos_chrome_calls_refuse_a_thread_other_than_main()
        {
            InteropTestGate.Require();
            if (!OperatingSystem.IsMacOS())
                Assert.Skip("the main-thread rule is macOS only");

            await Task.Run(() =>
            {
                void State(Action call)
                {
                    var e = Assert.Throws<InteropException>(call);
                    Assert.Equal(InteropStatus.State, e.Status);
                    Assert.Contains("main thread", e.Message);
                }

                State(() => WindowChrome.PrepareOverlay(Bogus, OverlayFlags.CoverMenuBar));
                State(() => WindowChrome.SetClickThrough(Bogus, true));
                State(() => WindowChrome.RaiseTopmostNoActivate(Bogus));
                State(() => WindowChrome.SetExcludedFromCapture(Bogus, true));
                State(() => WindowChrome.IsForeground(Bogus));
                State(() => WindowChrome.SetTrafficLightPosition(Bogus, 14, 14));
            });
        }

        [Fact]
        public void The_calls_an_os_lacks_answer_not_applied()
        {
            InteropTestGate.Require();
            if (OperatingSystem.IsWindows())
            {
                Assert.False(WindowChrome.SetTrafficLightPosition(Bogus, 14, 14));
                return;
            }

            // the neutral calls whose macOS arm is deferred this phase, and the Windows-only ones;
            // none touches the bogus handle
            Assert.False(WindowChrome.TakeForeground(Bogus));
            Assert.False(WindowChrome.SetBounds(Bogus, 0, 0, 10, 10));
            Assert.Equal(0, WindowChrome.DefaultWindowProc(Bogus, 0, 0, 0));
            Assert.Null(ForegroundWatch.Start(() => { }));
        }

        [Fact]
        public void A_windows_foreground_watch_starts_and_stops_on_its_thread()
        {
            InteropTestGate.Require();
            if (!OperatingSystem.IsWindows())
                Assert.Skip("the foreground watch is Windows only");

            // an out-of-context hook needs a message loop only to deliver, not to be created
            var watch = ForegroundWatch.Start(() => { });
            Assert.NotNull(watch);
            watch.Dispose();
            watch.Dispose();
        }

        [Fact]
        public void The_cursor_position_is_plausible_from_any_thread()
        {
            InteropTestGate.Require();
            if (!NativeCursor.TryGetPosition(out var x, out var y))
            {
                Assert.True(x == 0 && y == 0, "a failed read reports the origin");
                return;
            }

            // a million pixels either way is no desktop
            Assert.InRange(x, -1_000_000, 1_000_000);
            Assert.InRange(y, -1_000_000, 1_000_000);
        }
    }
}
