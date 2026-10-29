using System;
using System.Runtime.InteropServices;
using System.Threading;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Interop.Tests
{
    /// <summary>
    /// <see cref="GlobalKeyboardHook"/> against the real library: the event struct's layout, the first
    /// Status, refusals, a stop that drains and closes, and a dispose racing a blocked
    /// <see cref="GlobalKeyboardHook.NextEvent"/> (README R19). On macOS without Accessibility the hook
    /// starts down and says so, which these tests accept; they register only gestures nobody presses.
    /// </summary>
    public sealed class HotkeysTests
    {
        // the hotkey key table's values (HotkeyKey.g.cs, Avalonia's numbering)
        private const int F18 = 107;
        private const int F19 = 108;
        private const int LeftShift = 116;
        private const uint ControlAltShift = 2 | 1 | 4;

        private static bool HasBackend => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        [Fact]
        public void The_event_struct_matches_the_rust_layout()
        {
            Assert.Equal(48, Marshal.SizeOf<PriscHotkeysEvent>());
            Assert.Equal(0, (int)Marshal.OffsetOf<PriscHotkeysEvent>(nameof(PriscHotkeysEvent.kind)));
            Assert.Equal(24, (int)Marshal.OffsetOf<PriscHotkeysEvent>(nameof(PriscHotkeysEvent.flags)));
            Assert.Equal(28, (int)Marshal.OffsetOf<PriscHotkeysEvent>(nameof(PriscHotkeysEvent.hook_state)));
            Assert.Equal(32, (int)Marshal.OffsetOf<PriscHotkeysEvent>(nameof(PriscHotkeysEvent.reason)));
        }

        [Fact]
        public void No_backend_is_unsupported()
        {
            InteropTestGate.Require();
            if (HasBackend)
                return;

            var e = Assert.Throws<InteropException>(() => GlobalKeyboardHook.Create());
            Assert.Equal(InteropStatus.Unsupported, e.Status);
        }

        [Fact]
        public void A_hook_reports_its_state_refuses_bad_gestures_and_closes_after_stop()
        {
            InteropTestGate.Require();
            if (!HasBackend)
                return;

            using var hook = GlobalKeyboardHook.Create();
            Assert.Equal(InteropStatus.Ok, hook.NextEvent(0, out var first));
            Assert.Equal(HotkeysEventKind.Status, first.Kind);
            bool running = first.HookState == HookState.Running;
            if (OperatingSystem.IsWindows())
                Assert.True(running);
            else
                Assert.True(running || first.HookState == HookState.PermissionMissing, first.HookState.ToString());
            Assert.Equal(!running, first.IsRecoverable);
            Assert.Equal(running, first.Reason.Length == 0);

            Assert.False(hook.TryRegister(0, 0, out _, out var refusal));
            Assert.Equal(GestureRefusal.NoKey, refusal);
            Assert.False(hook.TryRegister(LeftShift, 0, out _, out refusal));
            Assert.Equal(GestureRefusal.ModifierOnly, refusal);
            Assert.False(hook.TryRegister(F19, 0x10, out _, out refusal));
            Assert.Equal(GestureRefusal.InvalidModifiers, refusal);
            Assert.True(hook.TryRegister(F19, ControlAltShift, out var id, out refusal));
            Assert.Equal((1u, GestureRefusal.None), (id, refusal));
            Assert.False(hook.TryRegister(F19, ControlAltShift, out _, out refusal));
            Assert.Equal(GestureRefusal.Duplicate, refusal);
            Assert.True(hook.TryRegister(F18, ControlAltShift, out id, out _));
            Assert.Equal(2u, id);
            hook.Unregister(1);
            hook.SetObserve(true);
            hook.SetObserve(false);

            Assert.Equal(running, hook.TryStartRecording(out var session));
            if (running)
                hook.StopRecording(session);

            hook.Stop();
            hook.Stop();
            InteropStatus status;
            do
                status = hook.NextEvent(5000, out _);
            while (status == InteropStatus.Ok);
            Assert.Equal(InteropStatus.Closed, status);
            Assert.False(hook.TryStartRecording(out _));
        }

        [Fact]
        public void Disposing_while_another_thread_waits_for_an_event_is_safe()
        {
            InteropTestGate.Require();
            if (!HasBackend)
                return;

            var hook = GlobalKeyboardHook.Create();
            Assert.Equal(InteropStatus.Ok, hook.NextEvent(0, out _));
            InteropStatus? last = null;
            Exception thrown = null;
            using var waiting = new ManualResetEventSlim();
            var reader = new Thread(() =>
            {
                try
                {
                    waiting.Set();
                    while (true)
                    {
                        last = hook.NextEvent(2000, out _);
                        if (last == InteropStatus.Closed)
                            return;
                    }
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }
            });
            reader.Start();
            waiting.Wait();
            Thread.Sleep(100);

            // Dispose stops first, so the blocked call returns; the destroy waits for its lease
            hook.Dispose();
            Assert.True(reader.Join(TimeSpan.FromSeconds(5)), "the reader is still blocked");
            Assert.True(last == InteropStatus.Closed || thrown is ObjectDisposedException, thrown?.ToString());
            Assert.Throws<ObjectDisposedException>(() => hook.Stop());
        }
    }
}
