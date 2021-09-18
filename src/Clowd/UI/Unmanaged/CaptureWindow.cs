using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using Clowd.PlatformUtil;
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

    public class DxKeyDownEventArgs : EventArgs
    {
        public DxKeyDownEventArgs(int keyCode)
        {
            KeyCode = keyCode;
        }

        public int KeyCode { get; }
    }

    public class DxColorCapturedEventArgs : EventArgs
    {
        public DxColorCapturedEventArgs(Color color)
        {
            Color = color;
        }

        public Color Color { get; }
    }

    public class DxLayoutUpdatedEventArgs : EventArgs
    {
        public DxLayoutUpdatedEventArgs(bool captured, ScreenRect selection)
        {
            Selection = selection;
            Captured = captured;
        }

        public ScreenRect Selection { get; }
        public bool Captured { get; }
    }

    public class DxDisposedEventArgs : EventArgs
    {

        public DxDisposedEventArgs(string error) : this(error == null ? null : new Exception(error))
        { }

        public DxDisposedEventArgs(Exception error)
        {
            Error = error;
        }

        public Exception Error { get; }
    }

    public static class CaptureWindow
    {
        private delegate void fnKeyPressed(uint keyCode);
        private delegate void fnColorCaptured([MarshalAs(UnmanagedType.U1)] byte r, [MarshalAs(UnmanagedType.U1)] byte g, [MarshalAs(UnmanagedType.U1)] byte b);
        private delegate void fnLayoutUpdated([MarshalAs(UnmanagedType.Bool)] bool captured, RECT area);
        private delegate void fnDisposed([MarshalAs(UnmanagedType.LPWStr)] string errorMessage);

        private struct captureArgs
        {
            [MarshalAs(UnmanagedType.U1)] public byte colorR;
            [MarshalAs(UnmanagedType.U1)] public byte colorG;
            [MarshalAs(UnmanagedType.U1)] public byte colorB;
            [MarshalAs(UnmanagedType.Bool)] public bool animationDisabled;
            [MarshalAs(UnmanagedType.Bool)] public bool obstructedWindowDisabled;
            [MarshalAs(UnmanagedType.Bool)] public bool tipsDisabled;
            [MarshalAs(UnmanagedType.FunctionPtr)] public fnKeyPressed lpfnKeyPressed;
            [MarshalAs(UnmanagedType.FunctionPtr)] public fnColorCaptured lpfnColorCaptured;
            [MarshalAs(UnmanagedType.FunctionPtr)] public fnLayoutUpdated lpfnLayoutUpdated;
            [MarshalAs(UnmanagedType.FunctionPtr)] public fnDisposed lpfnDisposed;
        };

        //typedef struct captureArgs
        //{
        //    BYTE colorR;
        //    BYTE colorG;
        //    BYTE colorB;
        //    BOOL animationDisabled;
        //    BOOL obstructedWindowDisabled;
        //    BOOL tipsDisabled;
        //    fnKeyPressed lpfnKeyPressed;
        //    fnColorCaptured lpfnColorCaptured;
        //    fnLayoutUpdated lpfnLayoutUpdated;
        //    fnDisposed lpfnDisposed;
        //};

        [DllImport("Clowd.Win64")]
        private static extern void CaptureShow(captureArgs args);

        [DllImport("Clowd.Win64")]
        private static extern void CaptureReset();

        [DllImport("Clowd.Win64")]
        private static extern RECT CaptureGetSelectedArea();

        [DllImport("Clowd.Win64")]
        private static extern void CaptureClose();

        [DllImport("Clowd.Win64")]
        private static extern void CaptureWriteSessionToFile([MarshalAs(UnmanagedType.LPWStr)] string sessionDir, [MarshalAs(UnmanagedType.LPWStr)] string createdUtc);

        [DllImport("Clowd.Win64")]
        private static extern void CaptureWriteSessionToClipboard();

        private static fnKeyPressed delKeyPressed;
        private static fnColorCaptured delColorCaptured;
        private static fnLayoutUpdated delLayoutUpdated;
        private static fnDisposed delDisposed;

        static CaptureWindow()
        {
            delKeyPressed = new fnKeyPressed(KeyPressedImpl);
            delColorCaptured = new fnColorCaptured(ColorCapturedImpl);
            delLayoutUpdated = new fnLayoutUpdated(LayoutUpdatedImpl);
            delDisposed = new fnDisposed(DisposedImpl);
        }

        public static event EventHandler<DxKeyDownEventArgs> KeyDown;
        public static event EventHandler<DxDisposedEventArgs> Disposed;
        public static event EventHandler<DxLayoutUpdatedEventArgs> LayoutUpdated;
        public static event EventHandler<DxColorCapturedEventArgs> ColorCaptured;

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
                lpfnKeyPressed = delKeyPressed,
                lpfnColorCaptured = delColorCaptured,
                lpfnLayoutUpdated = delLayoutUpdated,
                lpfnDisposed = delDisposed,
            };

            CaptureShow(args);
        }

        public static void Reset()
        {
            CaptureReset();
        }

        public static ScreenRect GetSelection()
        {
            var area = CaptureGetSelectedArea();
            return ScreenRect.FromLTRB(area.left, area.top, area.right, area.bottom);
        }

        public static void Close()
        {
            CaptureClose();
        }

        public static void SaveSession(string sessionDir)
        {
            if (!Directory.Exists(sessionDir))
                throw new InvalidOperationException("Session directory must exist");

            var created = DateTime.UtcNow.ToString("o");
            CaptureWriteSessionToFile(sessionDir, created);
        }

        public static void WriteToClipboard()
        {
            CaptureWriteSessionToClipboard();
        }

        private static void KeyPressedImpl(uint keyCode)
        {
            KeyDown?.Invoke(null, new DxKeyDownEventArgs((int)keyCode));
        }

        private static void ColorCapturedImpl(byte r, byte g, byte b)
        {
            ColorCaptured?.Invoke(null, new DxColorCapturedEventArgs(Color.FromRgb(r, g, b)));
        }

        private static void LayoutUpdatedImpl(bool captured, RECT area)
        {
            LayoutUpdated?.Invoke(null, new DxLayoutUpdatedEventArgs(captured, ScreenRect.FromLTRB(area.left, area.top, area.right, area.bottom)));
        }

        private static void DisposedImpl(string error)
        {
            Disposed?.Invoke(null, new DxDisposedEventArgs(error));
        }
    }
}
