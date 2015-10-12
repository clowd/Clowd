using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Clowd.Utilities
{
    public class AreoColor
    {
        /// <summary>
        /// Returns an exciting color based on the current system accent color, or the default clowd one 
        /// if the DWM is turned off or the color is boring.
        /// </summary>
        public static Color GetColor()
        {
            if (SysInfo.IsDWMEnabled)
            {
                if (SysInfo.IsWindows8OrLater) 
                {
                    var selected = GetImmersiveColor("ImmersiveStartSelectionBackground");
                    var hsl = HSLColor.FromRGB(selected);
                    if (hsl.IsBoring())
                    {
                        var secondary = GetImmersiveColor("ImmersiveStartBackground");
                        if(!HSLColor.FromRGB(secondary).IsBoring())
                        {
                            return secondary;
                        }
                        hsl.Excite();
                    }
                    return hsl.ToRGB();
                }
                else 
                {
                    DWM_COLORIZATION_PARAMS parameters;
                    DwmGetColorizationParameters(out parameters);
                    var targetColor = GetAeroColorFromNumeric(parameters.clrColor);
                    var baseColor = Color.FromRgb(217, 217, 217);
                    var color = BlendColor(targetColor, baseColor, (double)(100 - parameters.nIntensity));
                    var hsl = HSLColor.FromRGB(color);
                    if (hsl.IsBoring())
                        hsl.Excite();
                    return hsl.ToRGB();
                }
            }
            else
            {
                //with no params, this will return the clowd default color.
                return GetAeroColorFromNumeric();
            }
        }

        private static Color GetImmersiveColor(string immersiveColorName)
        {
            IntPtr pElementName = Marshal.StringToHGlobalUni(immersiveColorName);
            var colourset = GetImmersiveUserColorSetPreference(false, false);
            uint type = GetImmersiveColorTypeFromName(pElementName);
            Marshal.FreeCoTaskMem(pElementName);
            uint colourdword = GetImmersiveColorFromColorSetEx((uint)colourset, type, false, 0);
            byte[] colourbytes = new byte[4];
            colourbytes[0] = (byte)((0xFF000000 & colourdword) >> 24); // A
            colourbytes[1] = (byte)((0x00FF0000 & colourdword) >> 16); // B
            colourbytes[2] = (byte)((0x0000FF00 & colourdword) >> 8); // G
            colourbytes[3] = (byte)(0x000000FF & colourdword); // R
            Color color = Color.FromArgb(colourbytes[0], colourbytes[3], colourbytes[2], colourbytes[1]);
            return color;
        }
        private static Color GetAeroColorFromNumeric(uint color = 0)
        {
            const byte a = 255;
            byte r = (byte)((color >> 16) & 0xFF);
            byte g = (byte)((color >> 8) & 0xFF);
            byte b = (byte)(color & 0xFF);

            if ((r < 30 && b < 30 && g < 30) ||
                (r > 225 && b > 225 && g > 225))
            {
                r = 37;
                g = 97;
                b = 163;
            }

            return Color.FromArgb(a, r, g, b);
        }

        private static Color BlendColor(Color color1, Color color2, double color2Perc)
        {
            if ((color2Perc < 0) || (100 < color2Perc))
                throw new ArgumentOutOfRangeException("color2Perc");

            return Color.FromRgb(
                BlendColorChannel(color1.R, color2.R, color2Perc),
                BlendColorChannel(color1.G, color2.G, color2Perc),
                BlendColorChannel(color1.B, color2.B, color2Perc));
        }

        private static byte BlendColorChannel(double channel1, double channel2, double channel2Perc)
        {
            var buff = channel1 + (channel2 - channel1) * channel2Perc / 100D;
            return Math.Min((byte)Math.Round(buff), (byte)255);
        }
        //these are all UNDOCUMENTED imports. There is no guarentee that this will succeed or be accurate.
        //these must be used, because "DwmGetColorizationColor" is very inaccurate.
        //perhaps a fall-back could be coded in the future to use DwmGetColorizationColor if these fail.

        [DllImport("uxtheme.dll", EntryPoint = "#98", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
        private static extern UInt32 GetImmersiveUserColorSetPreference(Boolean forceCheckRegistry, Boolean skipCheckOnFail);

        [DllImport("uxtheme.dll", EntryPoint = "#94", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
        private static extern UInt32 GetImmersiveColorSetCount();

        [DllImport("uxtheme.dll", EntryPoint = "#95", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
        private static extern UInt32 GetImmersiveColorFromColorSetEx(UInt32 immersiveColorSet, UInt32 immersiveColorType,
            Boolean ignoreHighContrast, UInt32 highContrastCacheMode);

        [DllImport("uxtheme.dll", EntryPoint = "#96", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
        private static extern UInt32 GetImmersiveColorTypeFromName(IntPtr name);

        [DllImport("uxtheme.dll", EntryPoint = "#100", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
        private static extern IntPtr GetImmersiveColorNamedTypeByIndex(UInt32 index);

        private struct DWM_COLORIZATION_PARAMS
        {
            public uint clrColor;
            public uint clrAfterGlow;
            public uint nIntensity;
            public uint clrAfterGlowBalance;
            public uint clrBlurBalance;
            public uint clrGlassReflectionIntensity;
            public bool fOpaque;
        }

        [DllImport("dwmapi.dll", EntryPoint = "#127", PreserveSig = false)]
        private static extern void DwmGetColorizationParameters(out DWM_COLORIZATION_PARAMS parameters);

        [DllImport("dwmapi.dll", EntryPoint = "#131", PreserveSig = false)]
        private static extern void DwmSetColorizationParameters(ref DWM_COLORIZATION_PARAMS parameters, bool unknown);
    }
}
