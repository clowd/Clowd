using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;
using Clowd.Util;
using Vanara.PInvoke;

namespace Clowd.UI.Unmanaged
{
    public class CaptureWindowOptions
    {
        public Color AccentColor { get; set; }
        public bool AnimationDisabled { get; set; }
        public bool ObstructedWindowDisabled { get; set; }
        public bool TipsDisabled { get; set; }
        public ScreenRect InitialRect { get; set; }
        public bool CopyCursorToClipboard { get; set; }
    }

    enum CaptureType
    {
        Upload = 1,
        Photo = 2,
        Save = 3,
    }

    public class CaptureWindowLoadedEventArgs : EventArgs
    {
        public string PrimaryGpu { get; }

        public CaptureWindowLoadedEventArgs(string primaryGpu)
        {
            PrimaryGpu = primaryGpu;
        }
    }

    public static class CaptureWindow
    {
        public static event EventHandler Disposed;

        public static event EventHandler<CaptureWindowLoadedEventArgs> Loaded;

        private delegate void fnColorCapture([MarshalAs(UnmanagedType.U1)] byte r, [MarshalAs(UnmanagedType.U1)] byte g, [MarshalAs(UnmanagedType.U1)] byte b);

        private delegate void fnVideoCapture(RECT captureRegion);

        private delegate void fnSessionCapture([MarshalAs(UnmanagedType.LPWStr)] string sessionJsonPath, CaptureType captureType);

        private delegate void fnDisposed([MarshalAs(UnmanagedType.LPWStr)] string errorMessage);

        private delegate void fnLoaded([MarshalAs(UnmanagedType.LPWStr)] string primaryGpu);

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
            [MarshalAs(UnmanagedType.FunctionPtr)] public fnLoaded lpfnLoaded;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string sessionDirectory;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string createdUtc;
            [MarshalAs(UnmanagedType.Struct)] public RECT initialRect;
            [MarshalAs(UnmanagedType.Bool)] public bool copyCursor;
        };

        [DllImport(Constants.ClowdNativeLibName)]
        private static extern void CaptureShow(ref captureArgs args);

        [DllImport(Constants.ClowdNativeLibName)]
        private static extern void CaptureClose();

        private static fnColorCapture delColorCapture;
        private static fnVideoCapture delVideoCapture;
        private static fnSessionCapture delSessionCapture;
        private static fnDisposed delDisposed;
        private static fnLoaded delLoaded;

        static CaptureWindow()
        {
            delColorCapture = new fnColorCapture(ColorCaptureImpl);
            delVideoCapture = new fnVideoCapture(VideoCaptureImpl);
            delSessionCapture = new fnSessionCapture(SessionCaptureImpl);
            delDisposed = new fnDisposed(DisposedImpl);
            delLoaded = new fnLoaded(LoadedImpl);
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
                lpfnLoaded = delLoaded,
                initialRect = options.InitialRect,
                copyCursor = options.CopyCursorToClipboard,
            };

            CaptureShow(ref args);
        }

        public static void Close()
        {
            CaptureClose();
        }

        private static void ColorCaptureImpl(byte r, byte g, byte b)
        {
            App.Current.Dispatcher.DispatchWithErrorHandling(() =>
            {
                NiceDialog.ShowColorViewer(Color.FromRgb(r, g, b));
            });
        }

        private static void VideoCaptureImpl(RECT captureRegion)
        {
            var rect = ScreenRect.FromLTRB(captureRegion.Left, captureRegion.Top, captureRegion.Right, captureRegion.Bottom);
            if (!rect.IsEmpty())
            {
                App.Current.Dispatcher.DispatchWithErrorHandling(() =>
                {
                    PageManager.Current.CreateNewVideoCapturePage(rect);
                });
            }
        }

        private static void SessionCaptureImpl(string sessionJsonPath, CaptureType captureType)
        {
            App.Current.Dispatcher.DispatchWithErrorHandling(async () =>
            {
                var session = SessionManager.Current.GetSessionFromPath(sessionJsonPath);
                if (session != null)
                {
                    session.Name = "Screenshot";
                    session.CanvasBackground = SettingsRoot.Current.Editor.CanvasBackground;
                    if (captureType == CaptureType.Save)
                    {
                        var frame = BitmapFrame.Create(new Uri(session.PreviewImgPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        var savedPath = await NiceDialog.ShowSaveImageDialog(null, frame, SettingsRoot.Current.General.LastSavePath, SettingsRoot.Current.Capture.FilenamePattern);
                        if (savedPath != null)
                        {
                            SettingsRoot.Current.General.LastSavePath = Path.GetDirectoryName(savedPath);
                            SessionManager.Current.DeleteSession(session);
                            if (SettingsRoot.Current.Capture.OpenSavedInExplorer)
                                Platform.Current.RevealFileOrFolder(savedPath);
                        }
                    }
                    else if (captureType == CaptureType.Upload)
                    {
                        UploadManager.UploadSession(session);
                    }
                    else
                    {
                        EditorWindow.ShowSession(session);
                    }
                }
            });
        }

        private static void DisposedImpl(string errMessage)
        {
            App.Current.Dispatcher.DispatchWithErrorHandling(() =>
            {
                if (!String.IsNullOrEmpty(errMessage))
                {
                    NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, errMessage, "An error occurred while showing screen capture window");
                }

                Disposed?.Invoke(null, new EventArgs());
            });
        }

        private static void LoadedImpl(string primarygpu)
        {
            App.Current.Dispatcher.DispatchWithErrorHandling(() =>
            {
                Loaded?.Invoke(null, new CaptureWindowLoadedEventArgs(primarygpu));
            });
        }
    }
}
