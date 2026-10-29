using System;

namespace Prisc.Interop
{
    /// <summary>
    /// The supervised global keyboard hook (<c>prisc_hotkeys_*</c>, README section 4.8): registered
    /// hotkeys, one-shot gesture recording and the keystroke observe stream, from one hook that comes
    /// back by itself where it can (macOS: a missing or stale Accessibility grant, a tap that could
    /// not be made or that died). Every change of state arrives as a <see cref="HotkeysEventKind.Status"/>
    /// event; the first event is always one.
    /// </summary>
    /// <remarks>
    /// Every method may be called from any thread; <see cref="NextEvent"/> blocks, so one dedicated
    /// thread pulls the events. <see cref="Stop"/> makes a blocked <see cref="NextEvent"/> drain and
    /// return <see cref="InteropStatus.Closed"/>; <c>Dispose</c> stops first and frees the handle
    /// once no call is in flight (README R19).
    /// </remarks>
    public sealed unsafe class GlobalKeyboardHook : InteropHandle
    {
        private GlobalKeyboardHook() { }

        /// <summary>Starts the hook. Throws <see cref="InteropException"/>
        /// (<see cref="InteropStatus.Unsupported"/> where there is no hook backend) or
        /// <see cref="InteropUnavailableException"/>. A hook that is down is not a failure: the first
        /// event says so.</summary>
        public static GlobalKeyboardHook Create()
        {
            InteropLibrary.Require();
            var hook = new GlobalKeyboardHook();
            PriscHotkeys* raw = null;
            Native.Check(NativeMethods.prisc_hotkeys_create(&raw), nameof(NativeMethods.prisc_hotkeys_create));
            hook.Adopt((IntPtr)raw);
            return hook;
        }

        /// <summary>Registers <paramref name="key"/> + <paramref name="modifiers"/> (the hotkey key
        /// table's values) under an id that survives hook restarts. False, with the
        /// <paramref name="refusal"/>, when the gesture itself cannot be a global hotkey; while the
        /// hook is down a registration is accepted and goes live when it is back.</summary>
        public bool TryRegister(int key, uint modifiers, out uint id, out GestureRefusal refusal)
        {
            using var lease = Lease();
            uint outId = 0;
            int outRefusal = 0;
            int status = NativeMethods.prisc_hotkeys_register((PriscHotkeys*)lease.Handle, key, modifiers, &outId, &outRefusal);
            id = outId;
            refusal = (GestureRefusal)outRefusal;
            if (status == (int)InteropStatus.InvalidArg && refusal != GestureRefusal.None)
                return false;
            Native.Check(status, nameof(NativeMethods.prisc_hotkeys_register));
            return true;
        }

        /// <summary>Unregisters <paramref name="id"/>; a no-op for an unknown one.</summary>
        public void Unregister(uint id)
        {
            using var lease = Lease();
            Native.Check(NativeMethods.prisc_hotkeys_unregister((PriscHotkeys*)lease.Handle, id),
                nameof(NativeMethods.prisc_hotkeys_unregister));
        }

        /// <summary>Turns the observe stream (<see cref="HotkeysEventKind.Key"/> events) on or off;
        /// holds across hook restarts.</summary>
        public void SetObserve(bool observe)
        {
            using var lease = Lease();
            Native.Check(NativeMethods.prisc_hotkeys_set_observe((PriscHotkeys*)lease.Handle, observe ? (byte)1 : (byte)0),
                nameof(NativeMethods.prisc_hotkeys_set_observe));
        }

        /// <summary>Arms a one-shot recording. False while the hook is down.</summary>
        public bool TryStartRecording(out uint session)
        {
            using var lease = Lease();
            uint outSession = 0;
            int status = NativeMethods.prisc_hotkeys_start_recording((PriscHotkeys*)lease.Handle, &outSession);
            session = outSession;
            if (status == (int)InteropStatus.State)
                return false;
            Native.Check(status, nameof(NativeMethods.prisc_hotkeys_start_recording));
            return true;
        }

        /// <summary>Cancels <paramref name="session"/> if it is still armed.</summary>
        public void StopRecording(uint session)
        {
            using var lease = Lease();
            Native.Check(NativeMethods.prisc_hotkeys_stop_recording((PriscHotkeys*)lease.Handle, session),
                nameof(NativeMethods.prisc_hotkeys_stop_recording));
        }

        /// <summary>Waits up to <paramref name="timeoutMs"/> for the next event:
        /// <see cref="InteropStatus.Ok"/> with <paramref name="hotkeyEvent"/> filled,
        /// <see cref="InteropStatus.Timeout"/>, or <see cref="InteropStatus.Closed"/> once stopped and
        /// drained. Throws <see cref="InteropException"/> on a failure.</summary>
        public InteropStatus NextEvent(uint timeoutMs, out HotkeyEvent hotkeyEvent)
        {
            using var lease = Lease();
            PriscHotkeysEvent raw = default;
            var status = Native.Check(NativeMethods.prisc_hotkeys_next_event((PriscHotkeys*)lease.Handle, timeoutMs, &raw),
                nameof(NativeMethods.prisc_hotkeys_next_event));
            // the reason is ours to free, whatever we make of the event
            string reason = Utf8.Take(ref raw.reason);
            hotkeyEvent = status == InteropStatus.Ok ? HotkeyEvent.From(raw, reason) : default;
            return status;
        }

        /// <summary>Stops the hook and joins its threads, so its gestures reach other apps again.
        /// Idempotent; a blocked <see cref="NextEvent"/> then drains and reports
        /// <see cref="InteropStatus.Closed"/>.</summary>
        public void Stop()
        {
            using var lease = Lease();
            Native.Check(NativeMethods.prisc_hotkeys_stop((PriscHotkeys*)lease.Handle), nameof(NativeMethods.prisc_hotkeys_stop));
        }

        protected override void Dispose(bool disposing)
        {
            // stop first, so a NextEvent in flight returns and its lease lets the destroy run
            if (disposing && !IsInvalid && !IsClosed)
            {
                try
                {
                    Stop();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InteropException)
                {
                }
            }
            base.Dispose(disposing);
        }

        protected override int Destroy(IntPtr h) => NativeMethods.prisc_hotkeys_destroy((PriscHotkeys*)h);
    }

    /// <summary>One event of a <see cref="GlobalKeyboardHook"/>.</summary>
    /// <param name="Kind">What happened.</param>
    /// <param name="Id">Hotkey: the registration id; RecordingModifiers, Recorded: the recording
    /// session; otherwise 0.</param>
    /// <param name="Key">Recorded, Key: the hotkey key table's value (0 = none).</param>
    /// <param name="Modifiers">The modifier bits (Alt 1, Control 2, Shift 4, Meta 8).</param>
    /// <param name="RawCode">Key: the Windows virtual-key code or macOS CGKeyCode.</param>
    /// <param name="Character">Key: what a key-down types, or null.</param>
    /// <param name="Flags">Down and Repeat (Hotkey, Key), Recoverable (Status).</param>
    /// <param name="HookState">Status: the hook's state.</param>
    /// <param name="Reason">Status with a down state: why, in the hook's words; otherwise empty.</param>
    public readonly record struct HotkeyEvent(HotkeysEventKind Kind, uint Id, int Key, uint Modifiers, int RawCode,
        string? Character, HotkeysEventFlags Flags, HookState HookState, string Reason)
    {
        public bool IsDown => (Flags & HotkeysEventFlags.Down) != 0;

        public bool IsRepeat => (Flags & HotkeysEventFlags.Repeat) != 0;

        public bool IsRecoverable => (Flags & HotkeysEventFlags.Recoverable) != 0;

        internal static HotkeyEvent From(in PriscHotkeysEvent raw, string reason) => new(
            (HotkeysEventKind)raw.kind, raw.id, raw.key, raw.modifiers, (int)raw.raw_code,
            raw.ch == 0 || raw.ch > 0x10FFFF || raw.ch is >= 0xD800 and <= 0xDFFF ? null : char.ConvertFromUtf32((int)raw.ch),
            (HotkeysEventFlags)raw.flags, (HookState)raw.hook_state, reason);
    }
}
