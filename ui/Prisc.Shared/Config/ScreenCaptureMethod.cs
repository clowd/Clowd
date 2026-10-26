using System;
using System.ComponentModel;

namespace Clowd.Config
{
    /// <summary>
    /// Which OS API backs display capture. Mirrors the values obs-express and clowd_share_region
    /// both accept for <c>--capture-method</c> — one enum for both because the two binaries share
    /// the flag, its spellings and its default deliberately, so a user who pins one can pin both.
    /// <para>Windows only: neither binary has a choice to make on macOS, where ScreenCaptureKit is
    /// the only path, so the rows carrying this are marked [HiddenOnMacOS].</para>
    /// <para>Declaration order is dropdown order, so the default goes first.</para>
    /// </summary>
    public enum ScreenCaptureMethod
    {
        /// <summary>Let the capture plugin pick.</summary>
        [Description("Auto")]
        Auto,

        /// <summary>DXGI desktop duplication. Draws no capture border on any Windows version.</summary>
        [Description("DXGI (desktop duplication)")]
        Dxgi,

        /// <summary>Windows Graphics Capture. Draws a yellow capture border around the captured
        /// display on Windows 10, which cannot be suppressed there.</summary>
        [Description("WGC (Windows Graphics Capture)")]
        Wgc,
    }

    public static class ScreenCaptureMethodExtensions
    {
        /// <summary>
        /// The spelling the helpers' <c>--capture-method</c> parser accepts. Written out rather
        /// than lowercasing <see cref="Enum.ToString()"/> so renaming a member here cannot silently
        /// change a command line the Rust side would then reject at spawn time.
        /// </summary>
        public static string ToCliValue(this ScreenCaptureMethod method)
        {
            switch (method)
            {
                case ScreenCaptureMethod.Dxgi: return "dxgi";
                case ScreenCaptureMethod.Wgc: return "wgc";
                default: return "auto";
            }
        }
    }
}
