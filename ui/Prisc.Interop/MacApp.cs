namespace Prisc.Interop
{
    /// <summary>The macOS application object (<c>prisc_app_set_activation_policy_macos</c>).</summary>
    public static unsafe class MacApp
    {
        /// <summary>
        /// Sets the activation policy and, when <paramref name="activateIfRegular"/> and the policy is
        /// <see cref="ActivationPolicy.Regular"/>, brings the app to the front in the same call (an
        /// Accessory to Regular flip alone leaves the new window behind the frontmost app). Setting
        /// the current policy again is a no-op. macOS main thread only: throws
        /// <see cref="InteropException"/> with <see cref="InteropStatus.State"/> elsewhere, and
        /// <see cref="InteropStatus.Unsupported"/> off macOS.
        /// </summary>
        public static void SetActivationPolicy(ActivationPolicy policy, bool activateIfRegular)
        {
            InteropLibrary.Require();
            Native.Check(NativeMethods.prisc_app_set_activation_policy_macos((int)policy, activateIfRegular ? (byte)1 : (byte)0),
                "prisc_app_set_activation_policy_macos");
        }
    }
}
