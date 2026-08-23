using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Minimal Metal device creation for the headless Skia GPU backend on macOS: the system
    /// default MTLDevice plus one MTLCommandQueue, obtained via objc_msgSend — enough to fill a
    /// <c>GRMtlBackendContext</c> (which on the plain net10.0 TFM takes raw Device/Queue handles).
    /// Compile-safe on Windows; all call sites are guarded by <see cref="OperatingSystem.IsMacOS"/>.
    /// NOTE: written on a Windows machine — untested on real macOS hardware until the
    /// cross-platform verification pass.
    /// </summary>
    internal static class MetalBackend
    {
        private const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";
        private const string LibObjC = "/usr/lib/libobjc.dylib";

        [DllImport(MetalFramework)]
        private static extern IntPtr MTLCreateSystemDefaultDevice();

        [DllImport(LibObjC)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [SupportedOSPlatform("macos")]
        public static bool TryCreateDevice(out IntPtr device, out IntPtr queue, out string failureReason)
        {
            device = IntPtr.Zero;
            queue = IntPtr.Zero;
            failureReason = null;

            try
            {
                device = MTLCreateSystemDefaultDevice();
                if (device == IntPtr.Zero)
                {
                    failureReason = "MTLCreateSystemDefaultDevice returned nil.";
                    return false;
                }

                queue = objc_msgSend(device, sel_registerName("newCommandQueue"));
                if (queue == IntPtr.Zero)
                {
                    Release(device);
                    device = IntPtr.Zero;
                    failureReason = "newCommandQueue returned nil.";
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                failureReason = "Metal not available: " + ex.Message;
                return false;
            }
        }

        /// <summary>objc release on an owned (+1) reference.</summary>
        [SupportedOSPlatform("macos")]
        public static void Release(IntPtr objcObject)
        {
            if (objcObject != IntPtr.Zero)
                objc_msgSend(objcObject, sel_registerName("release"));
        }
    }
}
