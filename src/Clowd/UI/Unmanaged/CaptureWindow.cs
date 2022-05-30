using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;
using Vanara.PInvoke;

namespace Clowd.UI.Unmanaged
{
    public class CaptureWindowOptions
    {
        public Color AccentColor { get; set; }
        public bool AnimationDisabled { get; set; }
        public bool ObstructedWindowDisabled { get; set; }
        public bool TipsDisabled { get; set; }
    }

    enum CaptureType
    {
        Upload = 1,
        Photo = 2,
        Save = 3,
    }

    public static class CaptureWindow
    {
        private delegate void fnColorCapture([MarshalAs(UnmanagedType.U1)] byte r, [MarshalAs(UnmanagedType.U1)] byte g, [MarshalAs(UnmanagedType.U1)] byte b);
        private delegate void fnVideoCapture(RECT captureRegion);
        private delegate void fnSessionCapture([MarshalAs(UnmanagedType.LPWStr)] string sessionJsonPath, CaptureType captureType);
        private delegate void fnDisposed([MarshalAs(UnmanagedType.LPWStr)] string errorMessage);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct captureArgs
        {
            [MarshalAs(UnmanagedType.U1)] public byte colorR;
            [MarshalAs(UnmanagedType.U1)] public byte colorG;
            [MarshalAs(UnmanagedType.U1)] public byte colorB;
            [MarshalAs(UnmanagedType.Bool)] public bool animationDisabled;
            [MarshalAs(UnmanagedType.Bool)] public bool obstructedWindowDisabled;
            [MarshalAs(UnmanagedType.Bool)] public bool tipsDisabled;
            [MarshalAs(UnmanagedType.FunctionPtr)] public fnColorCapture lpfnColorCapture;
            [MarshalAs(UnmanagedType.FunctionPtr)] public fnVideoCapture lpfnVideoCapture;
            [MarshalAs(UnmanagedType.FunctionPtr)] public fnSessionCapture lpfnSessionCapture;
            [MarshalAs(UnmanagedType.FunctionPtr)] public fnDisposed lpfnDisposed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string sessionDirectory;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string createdUtc;
        };

        [DllImport(Constants.ClowdWinNativeLib)]
        private static extern void CaptureShow(ref captureArgs args);

        [DllImport(Constants.ClowdWinNativeLib)]
        private static extern void CaptureClose();

        private static fnColorCapture delColorCapture;
        private static fnVideoCapture delVideoCapture;
        private static fnSessionCapture delSessionCapture;
        private static fnDisposed delDisposed;

        static CaptureWindow()
        {
            delColorCapture = new fnColorCapture(ColorCaptureImpl);
            delVideoCapture = new fnVideoCapture(VideoCaptureImpl);
            delSessionCapture = new fnSessionCapture(SessionCaptureImpl);
            delDisposed = new fnDisposed(DisposedImpl);
        }

        public static void Show(CaptureWindowOptions options)
        {
            captureArgs args = new captureArgs
            {
                colorR = options.AccentColor.R,
                colorG = options.AccentColor.G,
                colorB = options.AccentColor.B,
                animationDisabled = options.AnimationDisabled,
                obstructedWindowDisabled = options.ObstructedWindowDisabled,
                tipsDisabled = options.TipsDisabled,
                sessionDirectory = SessionManager.Current.GetNextSessionDirectory(),
                createdUtc = DateTime.UtcNow.ToString("o"),
                lpfnColorCapture = delColorCapture,
                lpfnVideoCapture = delVideoCapture,
                lpfnSessionCapture = delSessionCapture,
                lpfnDisposed = delDisposed,
            };

            CaptureShow(ref args);
        }

        public static void Close()
        {
            CaptureClose();
        }

        private static void ColorCaptureImpl(byte r, byte g, byte b)
        {
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                NiceDialog.ShowColorDialogAsync(null, Color.FromRgb(r, g, b));
            });
        }

        private static void VideoCaptureImpl(RECT captureRegion)
        {
            var rect = ScreenRect.FromLTRB(captureRegion.Left, captureRegion.Top, captureRegion.Right, captureRegion.Bottom);
            if (!rect.IsEmpty())
            {
                App.Current.Dispatcher.InvokeAsync(() =>
                {
                    PageManager.Current.CreateVideoCapturePage().Open(rect);
                });
            }
        }

        private static void SessionCaptureImpl(string sessionJsonPath, CaptureType captureType)
        {
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                var session = SessionManager.Current.GetSessionFromPath(sessionJsonPath);
                if (session != null)
                {
                    if (captureType == CaptureType.Save)
                    {
                        NiceDialog.ShowSelectSaveFileDialog(null, "Save Screenshot", SettingsRoot.Current.General.LastSavePath, "screenshot", "png").ContinueWith(t =>
                        {
                            var filename = t.Result;
                            if (filename != null)
                            {
                                File.Copy(session.PreviewImgPath, filename);
                                if (SettingsRoot.Current.Capture.OpenSavedInExplorer)
                                    Platform.Current.RevealFileOrFolder(filename);
                                SettingsRoot.Current.General.LastSavePath = Path.GetDirectoryName(filename);
                                // delete only if saved as recovery is no longer needed
                                SessionManager.Current.DeleteSession(session); 
                            }
                        }, TaskScheduler.FromCurrentSynchronizationContext());
                    }
                    else
                    {
                        session.Name = "Capture";
                        EditorWindow.ShowSession(session);
                    }
                }
            });
        }

        private static void DisposedImpl(string errMessage)
        {
            if (!String.IsNullOrEmpty(errMessage))
            {
                App.Current.Dispatcher.InvokeAsync(() =>
                {
                    NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, errMessage, "An error occurred while showing screen capture window");
                });
            }
        }
    }
}
