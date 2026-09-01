using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clowd.UI
{
    /// <summary>
    /// Windows camera enumeration via DirectShow, the camera counterpart of the WASAPI path in
    /// <see cref="AudioDeviceManager"/>. Unlike audio — where the endpoint id obs-express takes is
    /// the system's own MMDevice id — a DirectShow camera id is OBS's invention, so this has to
    /// reproduce it rather than pass a system value through.
    ///
    /// win-dshow's id is <c>encode(FriendlyName) + ":" + encode(DevicePath)</c>, where encode
    /// replaces <c>#</c> with <c>#22</c> and then <c>:</c> with <c>#3A</c> (obs-studio
    /// plugins/win-dshow/encode-dstr.hpp EncodeDeviceId + win-dshow.cpp AddDevice). Both halves
    /// come from the moniker's property bag, which is what libdshowcapture reads too
    /// (deps/libdshowcapture/src/source/dshow-enum.cpp EnumDevice).
    /// </summary>
    /// <remarks>
    /// One deliberate divergence: libdshowcapture also binds each moniker to an IBaseFilter, then
    /// requires a video capture output pin and at least one enumerable capability, dropping the
    /// device otherwise. That filtering instantiates every camera driver — the cost this class
    /// exists to avoid — so it is not reproduced. The effect is that a ghost or capture-less
    /// device can appear here and not in OBS's own list; picking one is not a new failure mode,
    /// because the recorder rejects a webcam it cannot open and VideoCapturePage already turns the
    /// toggle back off and says so (NotifyWebcamRejected). Nothing here can produce a WRONG id for
    /// a camera that does work, which is the property that matters.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal static class DirectShowInterop
    {
        // CLSID_SystemDeviceEnum / CLSID_VideoInputDeviceCategory (strmiids)
        private static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        private static readonly Guid PropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            in Guid clsid, IntPtr outer, uint context, in Guid iid, out ICreateDevEnum instance);

        [ComImport]
        [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator(in Guid category, out IEnumMoniker enumerator, int flags);
        }

        [ComImport]
        [Guid("00000102-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumMoniker
        {
            [PreserveSig]
            int Next(int count, [MarshalAs(UnmanagedType.LPArray)] IMoniker[] monikers, IntPtr fetched);
        }

        [ComImport]
        [Guid("0000000f-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMoniker
        {
            // only BindToStorage is called; the preceding slots must still be declared so the
            // vtable offsets line up. IPersist (3) + IPersistStream (4) + BindToObject.
            void GetClassID(out Guid classId);
            [PreserveSig] int IsDirty();
            void Load(IntPtr stream);
            void Save(IntPtr stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
            void GetSizeMax(out long size);
            void BindToObject(IntPtr bindContext, IMoniker left, in Guid iid, out IntPtr result);
            void BindToStorage(IntPtr bindContext, IMoniker left, in Guid iid,
                [MarshalAs(UnmanagedType.Interface)] out IPropertyBag result);
        }

        [ComImport]
        [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag
        {
            [PreserveSig]
            int Read([MarshalAs(UnmanagedType.LPWStr)] string name,
                [MarshalAs(UnmanagedType.Struct)] ref object value, IntPtr errorLog);
        }

        /// <summary>
        /// The cameras win-dshow would offer, with the ids it expects back. Returns null when
        /// DirectShow could not be asked at all (so the caller can fall back), and an empty list
        /// when it answered "no cameras".
        /// </summary>
        public static List<CameraDeviceInfo> GetCameras()
        {
            ICreateDevEnum devices = null;
            IEnumMoniker enumerator = null;

            try
            {
                // CLSCTX_INPROC_SERVER
                var hr = CoCreateInstance(SystemDeviceEnum, IntPtr.Zero, 1, typeof(ICreateDevEnum).GUID, out devices);
                if (hr < 0 || devices == null)
                {
                    Debug.WriteLine($"Could not create the DirectShow device enumerator (0x{hr:X8}).");
                    return null;
                }

                // S_FALSE (1) means the category exists but is empty — a machine with no camera,
                // which is an answer, not a failure. Only a hard error falls back.
                hr = devices.CreateClassEnumerator(VideoInputDeviceCategory, out enumerator, 0);
                if (hr < 0)
                {
                    Debug.WriteLine($"Could not enumerate DirectShow video devices (0x{hr:X8}).");
                    return null;
                }

                var cameras = new List<CameraDeviceInfo>();
                if (hr != 0 || enumerator == null)
                    return cameras;

                var buffer = new IMoniker[1];
                while (enumerator.Next(1, buffer, IntPtr.Zero) == 0)
                {
                    var moniker = buffer[0];
                    buffer[0] = null;
                    if (moniker == null)
                        continue;

                    try
                    {
                        var camera = ReadDevice(moniker);
                        if (camera != null)
                            cameras.Add(camera);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(moniker);
                    }
                }

                return cameras;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DirectShow camera enumeration failed: " + ex);
                SentryConfig.CaptureHandled(ex, "camera.directshow");
                return null;
            }
            finally
            {
                if (enumerator != null)
                    Marshal.ReleaseComObject(enumerator);
                if (devices != null)
                    Marshal.ReleaseComObject(devices);
            }
        }

        /// <summary>One moniker → one camera, or null when it carries no usable name (which is
        /// where libdshowcapture gives up on it too).</summary>
        private static CameraDeviceInfo ReadDevice(IMoniker moniker)
        {
            IPropertyBag properties = null;
            try
            {
                moniker.BindToStorage(IntPtr.Zero, null, PropertyBag, out properties);
                if (properties == null)
                    return null;

                var name = ReadProperty(properties, "FriendlyName");
                if (String.IsNullOrEmpty(name))
                    return null;

                // absent on some virtual devices, and legitimately empty then: EncodeDeviceId
                // still writes the trailing colon, so the id keeps its shape either way.
                var path = ReadProperty(properties, "DevicePath") ?? "";

                return new CameraDeviceInfo(Encode(name) + ":" + Encode(path), name);
            }
            catch (Exception ex)
            {
                // one unreadable device must not lose the rest of the list
                Debug.WriteLine("Skipped an unreadable DirectShow video device: " + ex.Message);
                return null;
            }
            finally
            {
                if (properties != null)
                    Marshal.ReleaseComObject(properties);
            }
        }

        private static string ReadProperty(IPropertyBag properties, string name)
        {
            object value = null;
            return properties.Read(name, ref value, IntPtr.Zero) == 0 ? value as string : null;
        }

        /// <summary>encode_dstr (encode-dstr.hpp). Order matters and is not alphabetical: '#' is
        /// escaped first, so the '#' introduced by the ':' rule is not escaped again.</summary>
        private static string Encode(string value) => value.Replace("#", "#22").Replace(":", "#3A");
    }
}
