using System.Collections.Generic;
using Prisc.Interop;

namespace Prisc.UI.Harness
{
    internal static partial class InteropProbe
    {
        /// <summary>The supervised hook through <see cref="GlobalKeyboardHook"/>: its first Status (the
        /// state its first start left: "denied" when macOS has not granted Accessibility, which never
        /// counts as an error), and one round trip of the handle (a documented refusal, a registration
        /// that is held whatever the state, and a stop that drains to Closed). Each run makes its own
        /// short-lived hook and registers only Control+Alt+Shift+F19, which nobody presses.</summary>
        static partial void ProbeHotkeys(ProbeRun run)
        {
            run.Item("hotkeys.state", () =>
            {
                using var hook = GlobalKeyboardHook.Create();
                hook.NextEvent(2000, out var first);
                var value = new Dictionary<string, object>
                {
                    ["kind"] = first.Kind.ToString(),
                    ["state"] = first.HookState.ToString(),
                    ["recoverable"] = first.IsRecoverable,
                    ["reason"] = first.Reason,
                };
                return first.HookState == HookState.PermissionMissing
                    ? new ProbeOutcome("denied", value, Error: first.Reason)
                    : first.Kind == HotkeysEventKind.Status ? value : new ProbeOutcome("error", value, Error: "no initial Status");
            }, anyThread: true);

            run.Item("hotkeys.lifecycle", () =>
            {
                using var hook = GlobalKeyboardHook.Create();
                hook.NextEvent(2000, out _);
                hook.TryRegister(0, 0, out _, out var noKey);
                // F19 with Control+Alt+Shift, by the hotkey key table's values
                bool registered = hook.TryRegister(108, 2 | 1 | 4, out var id, out _);
                hook.SetObserve(true);
                hook.Stop();
                InteropStatus end;
                do
                    end = hook.NextEvent(5000, out _);
                while (end == InteropStatus.Ok);
                return new Dictionary<string, object>
                {
                    ["refusal"] = noKey.ToString(),
                    ["registered"] = registered,
                    ["id"] = id,
                    ["after_stop"] = end.ToString(),
                };
            }, anyThread: true);
        }
    }
}
