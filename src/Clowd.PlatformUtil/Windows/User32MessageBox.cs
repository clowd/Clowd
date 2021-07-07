using System;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace Clowd.PlatformUtil.Windows
{
    public static class User32MessageBox
    {
        [DllImport("User32", EntryPoint = "MessageBox", SetLastError = true, CharSet = CharSet.Auto, BestFitMapping = false)]
        private static extern int MessageBoxShow(HandleRef hWnd, string text, string caption, int type);

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
            MessageBoxButton button,
            MessageBoxImage icon, MessageBoxResult defaultResult,
            MessageBoxOptions options)
        {
            return ShowCore(owner, messageBoxText, caption, button, icon, defaultResult, options);
        }

        public static MessageBoxResult Show(
            IntPtr owner,
            string messageBoxText,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            return ShowCore(owner, messageBoxText, caption, button, icon, defaultResult, 0);
        }

        public static MessageBoxResult Show(
            IntPtr owner,
            string messageBoxText,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon)
        {
            return ShowCore(owner, messageBoxText, caption, button, icon, 0, 0);
        }

        public static MessageBoxResult Show(
            IntPtr owner,
            string messageBoxText,
            string caption,
            MessageBoxButton button)
        {
            return ShowCore(owner, messageBoxText, caption, button, MessageBoxImage.None, 0, 0);
        }

        public static MessageBoxResult Show(IntPtr owner, string messageBoxText, string caption)
        {
            return ShowCore(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, 0, 0);
        }

        public static MessageBoxResult Show(IntPtr owner, string messageBoxText)
        {
            return ShowCore(owner, messageBoxText, String.Empty, MessageBoxButton.OK, MessageBoxImage.None, 0, 0);
        }

        internal static MessageBoxResult ShowCore(
            IntPtr owner,
            string messageBoxText,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon,
            MessageBoxResult defaultResult,
            MessageBoxOptions options)
        {
            if (!IsValidMessageBoxButton(button))
            {
                throw new InvalidEnumArgumentException("button", (int)button, typeof(MessageBoxButton));
            }
            if (!IsValidMessageBoxImage(icon))
            {
                throw new InvalidEnumArgumentException("icon", (int)icon, typeof(MessageBoxImage));
            }
            if (!IsValidMessageBoxResult(defaultResult))
            {
                throw new InvalidEnumArgumentException("defaultResult", (int)defaultResult, typeof(MessageBoxResult));
            }
            if (!IsValidMessageBoxOptions(options))
            {
                throw new InvalidEnumArgumentException("options", (int)options, typeof(MessageBoxOptions));
            }

            if ((options & (MessageBoxOptions.ServiceNotification | MessageBoxOptions.DefaultDesktopOnly)) != 0)
            {
                if (owner != IntPtr.Zero)
                {
                    throw new ArgumentException("Can't show a service notification with an owner.");
                }
            }
            else
            {
                if (owner == IntPtr.Zero)
                {
                    throw new ArgumentException("Must have a valid owner window.");
                }
            }

            int style = (int)button | (int)icon | (int)DefaultResultToButtonNumber(defaultResult, button) | (int)options;

            return Win32ToMessageBoxResult(MessageBoxShow(new HandleRef(null, owner), messageBoxText, caption, style));
        }

        private static int DefaultResultToButtonNumber(MessageBoxResult result, MessageBoxButton button)
        {
            if (result == 0) return DEFAULT_BUTTON1;

            switch (button)
            {
                case MessageBoxButton.OK:
                    return DEFAULT_BUTTON1;
                case MessageBoxButton.OKCancel:
                    if (result == MessageBoxResult.Cancel) return DEFAULT_BUTTON2;
                    return DEFAULT_BUTTON1;
                case MessageBoxButton.YesNo:
                    if (result == MessageBoxResult.No) return DEFAULT_BUTTON2;
                    return DEFAULT_BUTTON1;
                case MessageBoxButton.YesNoCancel:
                    if (result == MessageBoxResult.No) return DEFAULT_BUTTON2;
                    if (result == MessageBoxResult.Cancel) return DEFAULT_BUTTON3;
                    return DEFAULT_BUTTON1;
                default:
                    return DEFAULT_BUTTON1;
            }
        }

        private static MessageBoxResult Win32ToMessageBoxResult(int value)
        {
            switch (value)
            {
                case IDOK:
                    return MessageBoxResult.OK;
                case IDCANCEL:
                    return MessageBoxResult.Cancel;
                case IDYES:
                    return MessageBoxResult.Yes;
                case IDNO:
                    return MessageBoxResult.No;
                default:
                    return MessageBoxResult.No;
            }
        }

        private static bool IsValidMessageBoxButton(MessageBoxButton value)
        {
            return value == MessageBoxButton.OK
                || value == MessageBoxButton.OKCancel
                || value == MessageBoxButton.YesNo
                || value == MessageBoxButton.YesNoCancel;
        }

        private static bool IsValidMessageBoxImage(MessageBoxImage value)
        {
            return value == MessageBoxImage.Asterisk
                || value == MessageBoxImage.Error
                || value == MessageBoxImage.Exclamation
                || value == MessageBoxImage.Hand
                || value == MessageBoxImage.Information
                || value == MessageBoxImage.None
                || value == MessageBoxImage.Question
                || value == MessageBoxImage.Stop
                || value == MessageBoxImage.Warning;
        }

        private static bool IsValidMessageBoxResult(MessageBoxResult value)
        {
            return value == MessageBoxResult.Cancel
                || value == MessageBoxResult.No
                || value == MessageBoxResult.None
                || value == MessageBoxResult.OK
                || value == MessageBoxResult.Yes;
        }

        private static bool IsValidMessageBoxOptions(MessageBoxOptions value)
        {
            int mask = ~((int)MessageBoxOptions.ServiceNotification |
                         (int)MessageBoxOptions.DefaultDesktopOnly |
                         (int)MessageBoxOptions.RightAlign |
                         (int)MessageBoxOptions.RtlReading);

            if (((int)value & mask) == 0)
                return true;
            return false;
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
