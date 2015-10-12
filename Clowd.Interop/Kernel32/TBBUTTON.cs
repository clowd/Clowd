using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop.Kernel32
{
    /// <summary>
    /// Contains information about a button in a toolbar.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TBBUTTON
    {
        /// <summary>
        /// Zero-based index of the button image. Set this member to I_IMAGECALLBACK, and the toolbar will send the TBN_GETDISPINFO notification code to retrieve the image index when it is needed.
        /// </summary>
        public int iBitmap;

        /// <summary>
        /// Command identifier associated with the button. This identifier is used in a WM_COMMAND message when the button is chosen.
        /// </summary>
        public int idCommand;

        /// <summary>
        /// Contains the fsState and fsStyle fields (bytes) and padding (2 bytes on 32-bit systems, 6 bytes on 64-bit systems).
        /// Access the fields fsState and fsStyle to retrieve this information.
        /// </summary>
        public IntPtr fsStateStylePadding;

        /// <summary>
        /// Application-defined value.
        /// </summary>
        public IntPtr dwData;

        /// <summary>
        /// Zero-based index of the button string, or a pointer to a string buffer that contains text for the button.
        /// </summary>
        public IntPtr iString;

        /// <summary>
        /// Gets button state flags. This member can be a combination of the values listed in Toolbar Button States.
        /// </summary>
        public byte fsState
        {
            get
            {
                if (IntPtr.Size == 8)
                    return BitConverter.GetBytes(this.fsStateStylePadding.ToInt64())[0];
                else
                    return BitConverter.GetBytes(this.fsStateStylePadding.ToInt32())[0];
            }
        }

        /// <summary>
        /// Gets button style. This member can be a combination of the button style values listed in Toolbar Control and Button Styles.
        /// </summary>
        public byte fsStyle
        {
            get
            {
                if (IntPtr.Size == 8)
                    return BitConverter.GetBytes(this.fsStateStylePadding.ToInt64())[1];
                else
                    return BitConverter.GetBytes(this.fsStateStylePadding.ToInt32())[1];
            }
        }
    }
}
