using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clowd.UI
{
    /// <summary>
    /// macOS camera enumeration via AVFoundation, the camera counterpart of
    /// <see cref="CoreAudioInterop"/> — and, like it, a deliberate reimplementation of what OBS's
    /// own enumerator does, so the ids we hand back are the ids obs-express can open.
    ///
    /// mac-avcapture publishes <c>AVCaptureDevice.uniqueID</c> verbatim as the device id and
    /// <c>localizedName</c> as the label (obs-studio plugins/mac-avcapture/plugin-properties.m),
    /// and it builds its list from an <see cref="AVCaptureDeviceDiscoverySession"/> over a
    /// specific set of device types, in two passes (video then muxed). Both are mirrored exactly
    /// below: the device type list is the thing to keep in step if OBS ever revises it.
    ///
    /// This exists because asking OBS costs 5 s on macOS — not the process spawn, and not
    /// AVFoundation (a discovery session answers in under 100 ms), but something inside
    /// mac-avcapture's property callback. Enumeration only; no device is opened, so this neither
    /// needs nor triggers the camera permission prompt.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class AvCaptureInterop
    {
        private const string AvFoundationLib =
            "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
        private const string FoundationLib =
            "/System/Library/Frameworks/Foundation.framework/Foundation";
        private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjCLib, EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(ObjCLib, EntryPoint = "sel_registerName")]
        private static extern IntPtr GetSelector([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector, IntPtr arg1);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(
            IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessageIndex(IntPtr receiver, IntPtr selector, nuint index);

        // +arrayWithObjects:count: — (const id *objects, NSUInteger count), declared separately
        // because the count is an integer rather than a pointer argument.
        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessageArray(
            IntPtr receiver, IntPtr selector, IntPtr objects, nuint count);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern nuint SendMessageCount(IntPtr receiver, IntPtr selector);

        /// <summary>Loads a framework's exported NSString* constant (the device-type and
        /// media-type symbols are pointers TO an NSString, hence the extra dereference).</summary>
        private static IntPtr ReadStringConstant(IntPtr library, string symbol)
        {
            var address = NativeLibrary.GetExport(library, symbol);
            return address == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(address);
        }

        /// <summary>
        /// The cameras mac-avcapture would offer, in its order: the video devices of its discovery
        /// session followed by the muxed ones. Returns null when AVFoundation could not be asked at
        /// all (so the caller can fall back), and an empty list when it answered "no cameras".
        /// </summary>
        public static List<CameraDeviceInfo> GetCameras()
        {
            try
            {
                var avFoundation = NativeLibrary.Load(AvFoundationLib);
                // not read from directly — loading it is what guarantees NSArray is registered
                // with the ObjC runtime before objc_getClass asks for it.
                NativeLibrary.Load(FoundationLib);

                // AVCaptureDeviceTypeDeskViewCamera is macOS 13+; the other two have always been
                // there. A missing symbol is dropped rather than fatal, which is exactly the
                // version check the plugin does with @available.
                var deviceTypes = new List<IntPtr>();
                foreach (var symbol in new[]
                         {
                             "AVCaptureDeviceTypeBuiltInWideAngleCamera",
                             "AVCaptureDeviceTypeExternalUnknown",
                             "AVCaptureDeviceTypeDeskViewCamera",
                         })
                {
                    var type = ReadStringConstant(avFoundation, symbol);
                    if (type != IntPtr.Zero)
                        deviceTypes.Add(type);
                }

                if (deviceTypes.Count == 0)
                {
                    Debug.WriteLine("AVFoundation exposed no camera device types; falling back.");
                    return null;
                }

                var typeArray = CreateArray(deviceTypes);
                if (typeArray == IntPtr.Zero)
                    return null;

                var video = ReadStringConstant(avFoundation, "AVMediaTypeVideo");
                var muxed = ReadStringConstant(avFoundation, "AVMediaTypeMuxed");

                // the same two passes, in the same order, as plugin-properties.m — a camera that
                // carries its own audio shows up only in the muxed session.
                var cameras = new List<CameraDeviceInfo>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                AppendDevices(cameras, seen, typeArray, video);
                AppendDevices(cameras, seen, typeArray, muxed);
                return cameras;
            }
            catch (Exception ex)
            {
                // a missing framework, a renamed selector, an ABI change: all of them mean "ask
                // the recorder instead", never "this machine has no cameras".
                Debug.WriteLine("AVFoundation camera enumeration failed: " + ex);
                SentryConfig.CaptureHandled(ex, "camera.avfoundation");
                return null;
            }
        }

        /// <summary>Runs one discovery session and appends what it found. A media type the runtime
        /// does not export is skipped — the other pass still stands.</summary>
        private static void AppendDevices(
            List<CameraDeviceInfo> cameras, HashSet<string> seen, IntPtr deviceTypes, IntPtr mediaType)
        {
            if (mediaType == IntPtr.Zero)
                return;

            var sessionClass = GetClass("AVCaptureDeviceDiscoverySession");
            if (sessionClass == IntPtr.Zero)
                return;

            // AVCaptureDevicePositionUnspecified == 0
            var session = SendMessage(
                sessionClass,
                GetSelector("discoverySessionWithDeviceTypes:mediaType:position:"),
                deviceTypes,
                mediaType,
                IntPtr.Zero);
            if (session == IntPtr.Zero)
                return;

            var devices = SendMessage(session, GetSelector("devices"));
            if (devices == IntPtr.Zero)
                return;

            var count = SendMessageCount(devices, GetSelector("count"));
            var objectAt = GetSelector("objectAtIndex:");
            var uniqueId = GetSelector("uniqueID");
            var localizedName = GetSelector("localizedName");

            for (nuint i = 0; i < count; i++)
            {
                var device = SendMessageIndex(devices, objectAt, i);
                if (device == IntPtr.Zero)
                    continue;

                var id = ReadString(SendMessage(device, uniqueId));
                if (String.IsNullOrEmpty(id))
                    continue;

                // a camera that reports both video and muxed would otherwise be listed twice; the
                // plugin tolerates that (it only ever looks for a match), a picker should not.
                if (!seen.Add(id))
                    continue;

                var name = ReadString(SendMessage(device, localizedName));
                cameras.Add(new CameraDeviceInfo(id, String.IsNullOrEmpty(name) ? id : name));
            }
        }

        /// <summary>Wraps the device-type pointers in the NSArray the discovery session wants.</summary>
        private static IntPtr CreateArray(List<IntPtr> items)
        {
            var arrayClass = GetClass("NSArray");
            if (arrayClass == IntPtr.Zero)
                return IntPtr.Zero;

            var buffer = Marshal.AllocHGlobal(IntPtr.Size * items.Count);
            try
            {
                for (var i = 0; i < items.Count; i++)
                    Marshal.WriteIntPtr(buffer, i * IntPtr.Size, items[i]);

                // +arrayWithObjects:count: autoreleases; nothing here spins a run loop or outlives
                // the enclosing call, so the pool the caller is on owns it.
                return SendMessageArray(
                    arrayClass,
                    GetSelector("arrayWithObjects:count:"),
                    buffer,
                    (nuint)items.Count);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>NSString* → string, via its UTF-8 representation.</summary>
        private static string ReadString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero)
                return null;

            var utf8 = SendMessage(nsString, GetSelector("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }
    }
}
