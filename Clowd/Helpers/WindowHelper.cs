//using System;
//using System.Collections.Generic;
//using System.Runtime.InteropServices;
//using System.Text;
///// <summary>Contains a method to get all the open windows.</summary>

//public static class WindowHelper
//{
//    /// <summary>Returns a dictionary that contains the handle and title of all the open windows.</summary>
//    /// <returns>A dictionary that contains the handle and title of all the open windows.</returns>

//    public static IDictionary<IntPtr, string> GetOpenWindows()
//    {
//        IntPtr lShellWindow = GetShellWindow();
//        Dictionary<IntPtr, string> lWindows = new Dictionary<IntPtr, string>();

//        EnumWindowsProc tmpproc = delegate(IntPtr hWnd, int lParam)
//        {
//            if (hWnd == lShellWindow) return true;
//            if (!IsWindowVisible(hWnd)) return true;

//            int lLength = GetWindowTextLength(hWnd);
//            if (lLength == 0) return true;

//            StringBuilder lBuilder = new StringBuilder(lLength);
//            GetWindowText(hWnd, lBuilder, lLength + 1);

//            lWindows[hWnd] = lBuilder.ToString();
//            return true;

//        };
//        EnumWindows(tmpproc, 0);

//        return lWindows;
//    }

//    delegate bool EnumWindowsProc(IntPtr hWnd, int lParam);

//    [DllImport("USER32.DLL")]
//    static extern bool EnumWindows(EnumWindowsProc enumFunc, int lParam);

//    [DllImport("USER32.DLL")]
//    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

//    [DllImport("USER32.DLL")]
//    static extern int GetWindowTextLength(IntPtr hWnd);

//    [DllImport("USER32.DLL")]
//    static extern bool IsWindowVisible(IntPtr hWnd);

//    [DllImport("USER32.DLL")]
//    static extern IntPtr GetShellWindow();
//}