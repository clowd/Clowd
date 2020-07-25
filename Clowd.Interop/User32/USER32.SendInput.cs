using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop
{
    //public partial class USER32
    //{
    //    [DllImport("user32.dll")]
    //    internal static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);
    //}

    //[StructLayout(LayoutKind.Explicit)]
    //public struct si_INPUT
    //{
    //    [FieldOffset(0)]
    //    si_TYPE type;
    //    [FieldOffset(1)]
    //    si_MOUSEINPUT mi;
    //    [FieldOffset(1)]
    //    si_KEYBDINPUT ki;
    //    [FieldOffset(1)]
    //    si_HARDWAREINPUT hi;
    //}

    //public enum si_TYPE : uint
    //{
    //    /// <summary>
    //    /// The event is a mouse event. Use the mi structure of the union.
    //    /// </summary>
    //    INPUT_MOUSE = 0,
    //    /// <summary>
    //    /// The event is a keyboard event. Use the ki structure of the union.
    //    /// </summary>
    //    INPUT_KEYBOARD = 1,
    //    /// <summary>
    //    /// The event is a hardware event. Use the hi structure of the union.
    //    /// </summary>
    //    INPUT_HARDWARE = 2
    //}

    //[StructLayout(LayoutKind.Sequential)]
    //public struct si_MOUSEINPUT
    //{
    //    int dx;
    //    int dy;
    //    uint mouseData;
    //    uint dwFlags;
    //    /// <summary>
    //    /// The time stamp for the event, in milliseconds. If this parameter is 0, the system will provide its own time stamp.
    //    /// </summary>
    //    uint time;
    //    /// <summary>
    //    /// An additional value associated with the mouse event. An application calls GetMessageExtraInfo to obtain this extra information.
    //    /// </summary>
    //    IntPtr dwExtraInfo;
    //}
}
