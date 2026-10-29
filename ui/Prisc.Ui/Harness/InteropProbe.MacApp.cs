using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Prisc.Interop;

namespace Prisc.UI.Harness
{
    internal static partial class InteropProbe
    {
        /// <summary>The wrapper-level items of this area: one success, one documented failure and one
        /// round trip per export family, keyed <c>&lt;area&gt;.&lt;item&gt;</c>. The permission items are
        /// any-thread and query only: a request is made only for a kind that is already usable, which by
        /// contract never prompts. The activation policy and the Services provider need the macOS main
        /// thread, so they run on the UI thread here, plus one call from a pool thread that must be
        /// refused; off macOS they answer "unsupported".</summary>
        static partial void ProbeMacApp(ProbeRun run)
        {
            // permissions: the four states (the E2E oracle `perms` reads the same TCC answers)
            foreach (var kind in new[] { PermissionKind.ScreenRecording, PermissionKind.Accessibility, PermissionKind.Microphone, PermissionKind.Camera })
                run.Item("macapp.permissions.state." + MacAppSnake(kind), () => Permissions.State(kind).ToString(), anyThread: true);
            run.Item("macapp.permissions.state_agrees_with_facade", () =>
            {
                var screen = Permissions.State(PermissionKind.ScreenRecording).IsUsable();
                var ax = Permissions.State(PermissionKind.Accessibility).IsUsable();
                if (screen != MacPermissions.HasScreenRecording || ax != MacPermissions.HasAccessibility)
                    throw new InvalidOperationException($"wrapper {screen}/{ax}, facade {MacPermissions.HasScreenRecording}/{MacPermissions.HasAccessibility}");
                return true;
            }, anyThread: true);
            run.Item("macapp.permissions.ensure_usable", () =>
            {
                var result = new Dictionary<string, object>();
                foreach (var kind in new[] { PermissionKind.ScreenRecording, PermissionKind.Accessibility })
                {
                    var before = Permissions.State(kind);
                    if (!before.IsUsable())
                    {
                        result[MacAppSnake(kind)] = "skipped: not granted, a request would prompt";
                        continue;
                    }
                    var after = Permissions.Ensure(kind);
                    if (after != before)
                        throw new InvalidOperationException($"{kind}: {before} before, {after} after an ensure");
                    result[MacAppSnake(kind)] = after.ToString();
                }
                return result;
            }, anyThread: true);
            run.Item("macapp.permissions.ensure_microphone", () =>
                MacAppExpectFailure(() => Permissions.Ensure(PermissionKind.Microphone), InteropStatus.InvalidArg), anyThread: true);
            run.Item("macapp.permissions.unknown_kind", () =>
                MacAppExpectFailure(() => Permissions.State((PermissionKind)9), InteropStatus.InvalidArg), anyThread: true);

            // the activation policy: Prisc starts as Accessory (ShowInDock = false), so setting it again
            // is the no-op success; an unknown policy is refused on every OS; a pool thread is refused
            run.Item("macapp.app.set_activation_policy", () =>
            {
                MacApp.SetActivationPolicy(ActivationPolicy.Accessory, activateIfRegular: true);
                return true;
            });
            run.Item("macapp.app.unknown_policy", () =>
                MacAppExpectFailure(() => MacApp.SetActivationPolicy((ActivationPolicy)7, false), InteropStatus.InvalidArg));
            run.Item("macapp.app.off_main_thread", () =>
                MacAppOffMain(() => MacApp.SetActivationPolicy(ActivationPolicy.Accessory, false)));

            // the Services provider, under a probe-only port so a resident Prisc keeps its own
            run.Item("macapp.services.off_main_thread", () =>
                MacAppOffMain(() => MacServices.RegisterProvider("PriscInteropProbe", _ => { })));
            run.Item("macapp.services.register", () =>
            {
                MacServices.RegisterProvider("PriscInteropProbe", _ => { });
                return true;
            });
            run.Item("macapp.services.second_registration", () =>
                MacAppExpectFailure(() => MacServices.RegisterProvider("PriscInteropProbe", _ => { }), InteropStatus.State));
            run.Item("macapp.services.empty_port", () =>
                MacAppExpectFailure(() => MacServices.RegisterProvider("", _ => { }), InteropStatus.InvalidArg));
        }

        private static string MacAppSnake(PermissionKind kind) => kind switch
        {
            PermissionKind.ScreenRecording => "screen_recording",
            PermissionKind.Accessibility => "accessibility",
            PermissionKind.Microphone => "microphone",
            PermissionKind.Camera => "camera",
            _ => kind.ToString(),
        };

        /// <summary>A main-thread call made from a pool thread: on macOS it must be refused with
        /// <see cref="InteropStatus.State"/>; elsewhere it answers "unsupported" as on the UI thread.</summary>
        private static object MacAppOffMain(Action call)
        {
            var status = Task.Run(() =>
            {
                try
                {
                    call();
                    return (InteropStatus?)null;
                }
                catch (InteropException e)
                {
                    return e.Status;
                }
            }).GetAwaiter().GetResult();
            var expected = OperatingSystem.IsMacOS() ? InteropStatus.State : InteropStatus.Unsupported;
            if (status != expected)
                throw new InvalidOperationException($"expected {expected} from a pool thread, got {status?.ToString() ?? "success"}");
            return status.ToString();
        }

        /// <summary>A call that must fail with <paramref name="expected"/>: ok with the status when it
        /// does, an error when it succeeds. Any other failure propagates to the item (Unsupported off
        /// macOS reads "unsupported").</summary>
        private static object MacAppExpectFailure(Action call, InteropStatus expected)
        {
            try
            {
                call();
            }
            catch (InteropException e) when (e.Status == expected)
            {
                return e.Status.ToString();
            }
            throw new InvalidOperationException($"expected {expected}, the call succeeded");
        }
    }
}
