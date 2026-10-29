using System;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Interop.Tests
{
    /// <summary>The macOS app integration and permission wrappers. Query only: a request is made only
    /// for a kind that is already usable, which never prompts. xUnit never runs on the macOS main
    /// thread, so the main-thread exports are checked for their refusal here and for their effect in
    /// the Rust main_thread_macapp target and the interop probe.</summary>
    public sealed class MacAppTests
    {
        private static readonly PermissionKind[] Kinds =
            [PermissionKind.ScreenRecording, PermissionKind.Accessibility, PermissionKind.Microphone, PermissionKind.Camera];

        private static readonly InteropStatus MainThreadRefusal = OperatingSystem.IsMacOS() ? InteropStatus.State : InteropStatus.Unsupported;

        [Fact]
        public void The_enums_carry_the_rust_values()
        {
            Assert.Equal([0, 1, 2], new[] { (int)ActivationPolicy.Regular, (int)ActivationPolicy.Accessory, (int)ActivationPolicy.Prohibited });
            Assert.Equal([0, 1, 2, 3], Array.ConvertAll(Kinds, k => (int)k));
            Assert.Equal([0, 1, 2, 3], new[]
            {
                (int)PermissionState.Granted, (int)PermissionState.Denied, (int)PermissionState.NotDetermined,
                (int)PermissionState.NotApplicable,
            });
        }

        [Fact]
        public void Every_kind_has_a_state_that_is_not_applicable_only_off_macos()
        {
            InteropTestGate.Require();
            foreach (var kind in Kinds)
            {
                var state = Permissions.State(kind);
                Assert.True(Enum.IsDefined(state), $"{kind}: {state}");
                Assert.Equal(!OperatingSystem.IsMacOS(), state == PermissionState.NotApplicable);
            }
        }

        [Fact]
        public void Screen_recording_and_accessibility_are_granted_or_denied_on_macos()
        {
            InteropTestGate.Require();
            if (!OperatingSystem.IsMacOS())
                Assert.Skip("macOS only");
            foreach (var kind in new[] { PermissionKind.ScreenRecording, PermissionKind.Accessibility })
                Assert.Contains(Permissions.State(kind), new[] { PermissionState.Granted, PermissionState.Denied });
        }

        [Fact]
        public void Ensure_on_a_usable_kind_returns_it_without_prompting()
        {
            InteropTestGate.Require();
            var checkedAny = false;
            foreach (var kind in new[] { PermissionKind.ScreenRecording, PermissionKind.Accessibility })
            {
                var before = Permissions.State(kind);
                if (!before.IsUsable())
                    continue; // a request would prompt
                Assert.Equal(before, Permissions.Ensure(kind));
                checkedAny = true;
            }
            if (!checkedAny)
                Assert.Skip("neither Screen Recording nor Accessibility is granted to this test host");
        }

        [Theory]
        [InlineData(PermissionKind.Microphone)]
        [InlineData(PermissionKind.Camera)]
        [InlineData((PermissionKind)9)]
        public void Ensure_refuses_the_kinds_it_cannot_request(PermissionKind kind)
        {
            InteropTestGate.Require();
            var e = Assert.Throws<InteropException>(() => Permissions.Ensure(kind));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
            Assert.Equal("prisc_permission_request", e.Operation);
        }

        [Fact]
        public void An_unknown_kind_is_invalid_arg()
        {
            InteropTestGate.Require();
            var e = Assert.Throws<InteropException>(() => Permissions.State((PermissionKind)(-1)));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
        }

        [Fact]
        public void Usable_means_granted_or_not_applicable()
        {
            Assert.True(PermissionState.Granted.IsUsable());
            Assert.True(PermissionState.NotApplicable.IsUsable());
            Assert.False(PermissionState.Denied.IsUsable());
            Assert.False(PermissionState.NotDetermined.IsUsable());
        }

        [Fact]
        public void The_activation_policy_is_refused_off_the_main_thread()
        {
            InteropTestGate.Require();
            var e = Assert.Throws<InteropException>(() => MacApp.SetActivationPolicy(ActivationPolicy.Accessory, false));
            Assert.Equal(MainThreadRefusal, e.Status);
            var unknown = Assert.Throws<InteropException>(() => MacApp.SetActivationPolicy((ActivationPolicy)3, false));
            Assert.Equal(InteropStatus.InvalidArg, unknown.Status);
        }

        [Fact]
        public void The_services_provider_is_refused_off_the_main_thread_and_checks_its_arguments()
        {
            InteropTestGate.Require();
            var e = Assert.Throws<InteropException>(() => MacServices.RegisterProvider("PriscInteropTests", _ => { }));
            Assert.Equal(MainThreadRefusal, e.Status);
            var empty = Assert.Throws<InteropException>(() => MacServices.RegisterProvider("", _ => { }));
            Assert.Equal(InteropStatus.InvalidArg, empty.Status);
            Assert.Throws<ArgumentNullException>(() => MacServices.RegisterProvider("PriscInteropTests", null!));
        }
    }
}
