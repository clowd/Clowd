using System;
using System.Runtime.InteropServices;

namespace Clowd.Interop
{
    public class MouseOperations
    {
        [Flags]
        public enum MouseEventFlags
        {
            MOUSEEVENTF_ABSOLUTE = 0x00008000,
            MOUSEEVENTF_LEFTDOWN = 0x00000002,
            MOUSEEVENTF_LEFTUP = 0x00000004,
            MOUSEEVENTF_MIDDLEDOWN = 0x00000020,
            MOUSEEVENTF_MIDDLEUP = 0x00000040,
            MOUSEEVENTF_MOVE = 0x00000001,
            MOUSEEVENTF_RIGHTDOWN = 0x00000008,
            MOUSEEVENTF_RIGHTUP = 0x00000010
        }

        [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out MousePoint lpMousePoint);

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);

        public static void SetCursorPosition(int x, int y)
        {
            SetCursorPos(x, y);
        }

        public static void SetCursorPosition(MousePoint point)
        {
            SetCursorPos(point.X, point.Y);
        }

        public static MousePoint GetCursorPosition()
        {
            MousePoint currentMousePoint;
            var gotPoint = GetCursorPos(out currentMousePoint);
            if (!gotPoint) { currentMousePoint = new MousePoint(0, 0); }
            return currentMousePoint;
        }

        public static void MouseEvent(MouseEventFlags value)
        {
            MousePoint position = GetCursorPosition();

            mouse_event
                ((int)value,
                 position.X,
                 position.Y,
                 0,
                 0)
                ;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MousePoint
        {
            public int X;
            public int Y;

            public MousePoint(int x, int y)
            {
                X = x;
                Y = y;
            }
        }
    }
}