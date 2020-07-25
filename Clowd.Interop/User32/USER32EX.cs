using Clowd.Interop.DwmApi;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Clowd.Interop
{
    public partial class USER32EX
    {
        /// <summary>
        /// Returns a list of handles for windows of the class 'ToolbarWindow32'.
        /// </summary>
        /// <param name="parent">The parent window whose children should be searched.</param>
        /// <returns>A list of handles for windows of the class 'ToolbarWindow32'</returns>
        public static List<IntPtr> GetChildToolbarWindows(IntPtr parent)
        {
            List<IntPtr> result = new List<IntPtr>();
            GCHandle listHandle = GCHandle.Alloc(result);

            try
            {
                USER32.EnumWindowProc childProc = new USER32.EnumWindowProc(EnumToolbarWindow);
                USER32.EnumChildWindows(parent, childProc, GCHandle.ToIntPtr(listHandle));
            }
            finally
            {
                if (listHandle.IsAllocated)
                    listHandle.Free();
            }

            return result;
        }

        /// <summary>
        /// Enumerates windows with the class name 'ToolbarWindow32'.
        /// </summary>
        /// <param name="handle">A handle to a top-level window.</param>
        /// <param name="pointer">The application-defined value given in EnumWindows or EnumDesktopWindows.</param>
        /// <returns>To continue enumeration, the callback function must return TRUE; to stop enumeration, it must return FALSE.</returns>
        internal static bool EnumToolbarWindow(IntPtr handle, IntPtr pointer)
        {
            GCHandle gch = GCHandle.FromIntPtr(pointer);

            List<IntPtr> list = gch.Target as List<IntPtr>;

            if (list == null)
                throw new InvalidCastException("GCHandle Target could not be cast as List<IntPtr>");

            StringBuilder classname = new StringBuilder(128);

            USER32.GetClassName(handle, classname, classname.Capacity);

            if (classname.ToString() == "ToolbarWindow32")
                list.Add(handle);

            return true;
        }

        /// <summary>
        /// Returns a list of handles for windows of the class 'ToolbarWindow32'.
        /// </summary>
        /// <param name="parent">The handle of the parent window whose children should be searched.</param>
        /// <returns>A list of handles for windows of the class 'ToolbarWindow32'.</returns>
        public static List<IntPtr> GetChildButtonWindows(IntPtr parent)
        {
            List<IntPtr> result = new List<IntPtr>();
            GCHandle listHandle = GCHandle.Alloc(result);

            try
            {
                USER32.EnumWindowProc childProc = new USER32.EnumWindowProc(EnumButtonWindow);
                USER32.EnumChildWindows(parent, childProc, GCHandle.ToIntPtr(listHandle));
            }
            finally
            {
                if (listHandle.IsAllocated)
                    listHandle.Free();
            }

            return result;
        }

        public static List<IntPtr> GetChildWindows(IntPtr parent)
        {
            List<IntPtr> result = new List<IntPtr>();
            GCHandle listHandle = GCHandle.Alloc(result);

            try
            {
                USER32.EnumWindowProc childProc = new USER32.EnumWindowProc(EnumChildWindow);
                USER32.EnumChildWindows(parent, childProc, GCHandle.ToIntPtr(listHandle));
            }
            finally
            {
                if (listHandle.IsAllocated)
                    listHandle.Free();
            }

            return result;
        }

        internal static bool EnumChildWindow(IntPtr handle, IntPtr pointer)
        {
            GCHandle gch = GCHandle.FromIntPtr(pointer);

            List<IntPtr> list = gch.Target as List<IntPtr>;

            if (list == null)
                throw new InvalidCastException("GCHandle Target could not be cast as List<IntPtr>");

            list.Add(handle);

            return true;
        }

        public static Keys ConvertCharToVirtualKey(char ch)
        {
            short vkey = USER32.VkKeyScan(ch);
            Keys retval = (Keys)(vkey & 0xff);

            //int modifiers = vkey >> 8;
            //if ((modifiers & 1) != 0) retval |= Keys.Shift;
            //if ((modifiers & 2) != 0) retval |= Keys.Control;
            //if ((modifiers & 4) != 0) retval |= Keys.Alt;

            return retval;
        }

        /// <summary>
        /// Returns the correct window rectangle on Vista and greater by querying DWM 
        /// instead of user32 GetWindowRect, which will return the wrong value.
        /// </summary>
        /// <param name="handle">Window Handle</param>
        /// <returns>Window rectangle for the specified handle</returns>
        public static Rectangle GetTrueWindowBounds(IntPtr handle)
        {
            try
            {
                if (Environment.OSVersion.Version.Major >= 6)
                    if (0 == DWMAPI.DwmIsCompositionEnabled(out var dwmIsEnabled))
                        if (dwmIsEnabled && DWMWA_EXTENDED_FRAME_BOUNDS(handle, out var rectangle))
                            return rectangle;
            }
            catch (DllNotFoundException) { }

            // fallback to old style calls if we can't query the DWM
            if (!USER32.GetWindowRect(handle, out var rect))
                throw new Win32Exception();

            return Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
        }

        private static bool DWMWA_EXTENDED_FRAME_BOUNDS(IntPtr handle, out Rectangle rectangle)
        {
            RECT rect;
            var result = DWMAPI.DwmGetWindowAttribute(handle, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf(typeof(RECT)));
            rectangle = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
            return result == 0;
        }

        /// <summary>
        /// Returns the z-value of the window in relation to all other windows on the desktop
        /// </summary>
        /// <param name="hWnd">The window to find z-order of</param>
        /// <returns></returns>
        public static int GetWindowZOrder(IntPtr hWnd)
        {
            var z = 0;
            for (IntPtr h = hWnd; h != IntPtr.Zero; h = USER32.GetWindow(h, GETWINDOW_CMD.GW_HWNDPREV)) z++;
            return z;
        }

        public static string GetWindowCaption(IntPtr hWnd)
        {
            int length = USER32.GetWindowTextLength(hWnd);
            if (length == 0) return String.Empty;
            StringBuilder builder = new StringBuilder(length);
            USER32.GetWindowText(hWnd, builder, length + 1);
            return builder.ToString();
        }
        public static string GetWindowClassName(IntPtr hWnd)
        {
            StringBuilder stringBuilder = new StringBuilder(255);
            USER32.GetClassName(hWnd, stringBuilder, stringBuilder.Capacity);
            return stringBuilder.ToString().TrimEnd();
        }
        public static System.Windows.Forms.ScrollBars GetVisibleScrollbars(IntPtr hWnd)
        {
            long wndStyle = USER32.GetWindowLong(hWnd, WindowLongIndex.GWL_STYLE);
            bool hsVisible = (wndStyle & (long)WindowStyles.WS_HSCROLL) != 0;
            bool vsVisible = (wndStyle & (long)WindowStyles.WS_VSCROLL) != 0;

            if (hsVisible)
                return vsVisible ? System.Windows.Forms.ScrollBars.Both : System.Windows.Forms.ScrollBars.Horizontal;
            else
                return vsVisible ? System.Windows.Forms.ScrollBars.Vertical : System.Windows.Forms.ScrollBars.None;
        }

        /// <summary>
        /// Enumerates windows with the class name 'Button' and a blank window name.
        /// </summary>
        /// <param name="handle">A handle to a top-level window.</param>
        /// <param name="pointer">The application-defined value given in EnumWindows or EnumDesktopWindows.</param>
        /// <returns>To continue enumeration, the callback function must return TRUE; to stop enumeration, it must return FALSE.</returns>
        internal static bool EnumButtonWindow(IntPtr handle, IntPtr pointer)
        {
            GCHandle gch = GCHandle.FromIntPtr(pointer);

            List<IntPtr> list = gch.Target as List<IntPtr>;

            if (list == null)
                throw new InvalidCastException("GCHandle Target could not be cast as List<IntPtr>");

            StringBuilder classname = new StringBuilder(128);

            USER32.GetClassName(handle, classname, classname.Capacity);

            if (classname.ToString() == "Button" && USER32.GetWindowTextLength(handle) == 0)
                list.Add(handle);

            return true;
        }


        /// <summary>
        /// Checks whether the cursor is over the current window's client area.
        /// </summary>
        /// <param name="hWnd">Handle of the window to check.</param>
        /// <param name="wParam">Additional message information. The content of this parameter depends on the value of the Msg parameter (wParam).</param>
        /// <param name="lParam">Additional message information. The content of this parameter depends on the value of the Msg parameter (lParam).</param>
        /// <returns>True if the cursor is over the client area, false if not.</returns>
        public static bool IsOverClientArea(IntPtr hWnd, IntPtr wParam, IntPtr lParam)
        {
            IntPtr uHitTest = USER32.DefWindowProc(hWnd, (uint)WindowMessage.WM_NCHITTEST, wParam, lParam);
            if (uHitTest.ToInt32() == 0x1) // check if we're over the client area
                return true;
            return false;
        }

    }
}
