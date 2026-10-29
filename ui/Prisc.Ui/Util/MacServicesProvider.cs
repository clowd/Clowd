using System;
using System.Diagnostics;
using Prisc.Interop;

namespace Prisc.Util
{
    /// <summary>
    /// Receives the Finder "Upload with Prisc" service declared in Info.plist (NSServices).
    /// macOS delivers a service invocation to the app process that declared it, through an
    /// Objective-C object registered under the plist's NSPortName whose method matches the
    /// plist's NSMessage (uploadFiles:userData:error:). prisc_interop defines that object
    /// (<see cref="MacServices"/>); the selected files arrive here as paths.
    /// </summary>
    internal static class MacServicesProvider
    {
        // must match the Info.plist NSServices entry's NSPortName
        private const string PortName = "Prisc";

        private static bool _registered;

        /// <summary>Registers the provider once per process (macOS only; UI thread).</summary>
        public static void Initialize(Action<string[]> onFilesReceived)
        {
            if (!OperatingSystem.IsMacOS() || _registered)
                return;

            try
            {
                MacServices.RegisterProvider(PortName, files => Deliver(onFilesReceived, files));
                _registered = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MacServicesProvider: failed to register the Services provider: " + ex);
                SentryConfig.CaptureHandled(ex, "services.register");
            }
        }

        private static void Deliver(Action<string[]> onFilesReceived, string[] files)
        {
            // an exception must never unwind into the Services dispatch
            try
            {
                onFilesReceived(files);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MacServicesProvider: failed to handle service invocation: " + ex);
                SentryConfig.CaptureHandled(ex, "services.upload");
            }
        }
    }
}
