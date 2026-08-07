using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clowd.Util
{
    /// <summary>
    /// Receives the Finder "Upload with Clowd" service declared in Info.plist (NSServices).
    /// macOS delivers a service invocation to the app process that declared it, via an
    /// Objective-C object registered with -[NSApplication setServicesProvider:] whose
    /// method name matches the plist's NSMessage. That class doesn't exist in a .NET app,
    /// so it is synthesized here at runtime: an NSObject subclass with a single
    /// uploadFiles:userData:error: method backed by a managed function pointer. The
    /// selected files arrive as NSURLs on the pasteboard.
    /// </summary>
    internal static class MacServicesProvider
    {
        // must match the Info.plist NSServices entry's NSPortName
        private const string PortName = "Clowd";

        private static Action<string[]> _callback;

        // both must stay rooted for the lifetime of the process: the delegate backs the
        // Obj-C method IMP, and the provider instance is only weakly held by AppKit.
        private static ServiceImp _imp;
        private static IntPtr _provider;

        private delegate void ServiceImp(IntPtr self, IntPtr sel, IntPtr pasteboard, IntPtr userData, IntPtr error);

        public static void Initialize(Action<string[]> onFilesReceived)
        {
            if (!OperatingSystem.IsMacOS() || _provider != IntPtr.Zero)
                return;

            _callback = onFilesReceived;

            var cls = objc_allocateClassPair(GetClass("NSObject"), "ClowdServicesProvider", 0);
            if (cls == IntPtr.Zero)
                return;

            _imp = UploadFiles;
            // type encoding: void return, self, _cmd, pasteboard, userData, NSString** error
            class_addMethod(cls, GetSelector("uploadFiles:userData:error:"),
                Marshal.GetFunctionPointerForDelegate(_imp), "v@:@@^@");
            objc_registerClassPair(cls);

            _provider = SendMessage(SendMessage(cls, GetSelector("alloc")), GetSelector("init"));

            // not -[NSApp setServicesProvider:]: that registers the listening port under the
            // process name ("Clowd.Ui", the executable), but delivery looks the port up by the
            // plist's NSPortName ("Clowd") — register under the advertised name explicitly.
            var portName = SendMessage(GetClass("NSString"), GetSelector("stringWithUTF8String:"), PortName);
            NSRegisterServicesProvider(_provider, portName);

            // flush the pasteboard server's services cache so a freshly updated bundle's
            // menu entry shows up without waiting for a re-login.
            NSUpdateDynamicServices();
        }

        private static void UploadFiles(IntPtr self, IntPtr sel, IntPtr pasteboard, IntPtr userData, IntPtr error)
        {
            // an exception must never unwind into the Obj-C service dispatch machinery
            try
            {
                var files = ReadFileUrls(pasteboard);
                if (files.Length > 0)
                    _callback?.Invoke(files);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MacServicesProvider: failed to handle service invocation: " + ex);
                SentryConfig.CaptureHandled(ex, "services.upload");
            }
        }

        private static string[] ReadFileUrls(IntPtr pasteboard)
        {
            // [pasteboard readObjectsForClasses:@[NSURL.class] options:nil]
            var classes = SendMessage(GetClass("NSArray"), GetSelector("arrayWithObject:"), GetClass("NSURL"));
            var urls = SendMessage(pasteboard, GetSelector("readObjectsForClasses:options:"), classes, IntPtr.Zero);
            if (urls == IntPtr.Zero)
                return Array.Empty<string>();

            var count = (long)SendMessage(urls, GetSelector("count"));
            var files = new List<string>((int)count);
            for (long i = 0; i < count; i++)
            {
                var url = SendMessage(urls, GetSelector("objectAtIndex:"), (IntPtr)i);
                var path = SendMessage(url, GetSelector("path"));
                if (path == IntPtr.Zero)
                    continue;

                var utf8 = SendMessage(path, GetSelector("UTF8String"));
                if (Marshal.PtrToStringUTF8(utf8) is { Length: > 0 } file)
                    files.Add(file);
            }

            return files.ToArray();
        }

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass(string name);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
        private static extern IntPtr GetSelector(string name);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern void objc_registerClassPair(IntPtr cls);

        [DllImport("/usr/lib/libobjc.dylib")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector, string utf8Arg);

        [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit")]
        private static extern void NSUpdateDynamicServices();

        [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit")]
        private static extern void NSRegisterServicesProvider(IntPtr provider, IntPtr portName);
    }
}
