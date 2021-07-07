using System;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace Clowd.PlatformUtil.Windows
{
    public static class User32MessageBox
    {
        [DllImport("User32", EntryPoint = "MessageBox", SetLastError = true, CharSet = CharSet.Auto, BestFitMapping = false)]
        private static extern int MessageBoxShow(IntPtr hWnd, string text, string caption, int type);

        private const int IDOK = 1;
        private const int IDCANCEL = 2;
        private const int IDYES = 6;
        private const int IDNO = 7;
        private const int DEFAULT_BUTTON1 = 0x00000000;
        private const int DEFAULT_BUTTON2 = 0x00000100;
        private const int DEFAULT_BUTTON3 = 0x00000200;

        public static MessageBoxResult Show(
            IntPtr owner,
            string messageBoxText,
            string caption,
            MessageBoxButtons button,
            MessageBoxIcon icon, MessageBoxResult defaultResult,
            MessageBoxOptions options)
        {
            return ShowCore(owner, messageBoxText, caption, button, icon, defaultResult, options);
        }

        public static MessageBoxResult Show(
            IntPtr owner,
            string messageBoxText,
            string caption,
            MessageBoxButtons button,
            MessageBoxIcon icon,
            MessageBoxResult defaultResult)
        {
            return ShowCore(owner, messageBoxText, caption, button, icon, defaultResult, 0);
        }

        public static MessageBoxResult Show(
            IntPtr owner,
            string messageBoxText,
            string caption,
            MessageBoxButtons button,
            MessageBoxIcon icon)
        {
            return ShowCore(owner, messageBoxText, caption, button, icon, 0, 0);
        }

        public static MessageBoxResult Show(
            IntPtr owner,
            string messageBoxText,
            string caption,
            MessageBoxButtons button)
        {
            return ShowCore(owner, messageBoxText, caption, button, MessageBoxIcon.None, 0, 0);
        }

        public static MessageBoxResult Show(IntPtr owner, string messageBoxText, string caption)
        {
            return ShowCore(owner, messageBoxText, caption, MessageBoxButtons.OK, MessageBoxIcon.None, 0, 0);
        }

        public static MessageBoxResult Show(IntPtr owner, string messageBoxText)
        {
            return ShowCore(owner, messageBoxText, String.Empty, MessageBoxButtons.OK, MessageBoxIcon.None, 0, 0);
        }

        internal static MessageBoxResult ShowCore(
            IntPtr owner,
            string messageBoxText,
            string caption,
            MessageBoxButtons button,
            MessageBoxIcon icon,
            MessageBoxResult defaultResult,
            MessageBoxOptions options)
        {

            // for positioning message box and custom button labels, see here
            // https://www.codeguru.com/cpp/w-p/win32/messagebox/article.php/c10873/MessageBox-with-Custom-Button-Captions.htm
            // need a CBT hook
            // https://stackoverflow.com/questions/1530561/set-location-of-messagebox

            if ((options & (MessageBoxOptions.ServiceNotification | MessageBoxOptions.DefaultDesktopOnly)) != 0)
            {
                if (owner != IntPtr.Zero)
                {
                    throw new ArgumentException("Can't show a service notification with an owner.");
                }
            }

            int style = (int)button | (int)icon | (int)DefaultResultToButtonNumber(defaultResult, button) | (int)options;

            return (MessageBoxResult)(MessageBoxShow(owner, messageBoxText, caption, style));
        }

        private static int DefaultResultToButtonNumber(MessageBoxResult result, MessageBoxButtons button)
        {
            if (result == 0) return DEFAULT_BUTTON1;

            switch (button)
            {
                case MessageBoxButtons.OK:
                    return DEFAULT_BUTTON1;
                case MessageBoxButtons.OKCancel:
                    if (result == MessageBoxResult.Cancel) return DEFAULT_BUTTON2;
                    return DEFAULT_BUTTON1;
                case MessageBoxButtons.YesNo:
                    if (result == MessageBoxResult.No) return DEFAULT_BUTTON2;
                    return DEFAULT_BUTTON1;
                case MessageBoxButtons.YesNoCancel:
                    if (result == MessageBoxResult.No) return DEFAULT_BUTTON2;
                    if (result == MessageBoxResult.Cancel) return DEFAULT_BUTTON3;
                    return DEFAULT_BUTTON1;
                default:
                    return DEFAULT_BUTTON1;
            }
        }
    }

    [Flags]
    public enum MessageBoxOptions
    {
        None = 0x00000000,
        ServiceNotification = 0x00200000,
        DefaultDesktopOnly = 0x00020000,
        RightAlign = 0x00080000,
        RtlReading = 0x00100000,
    }
}
