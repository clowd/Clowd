using RT.Util.Serialization;
using System;
using System.Globalization;
using System.Windows.Media;

namespace Clowd.Utilities
{
    /// <summary>
    /// Enables <see cref="Classify"/> to save color properties as strings of a human-editable form.
    /// </summary>
    sealed class ClassifyColorTypeOptions : ClassifyTypeOptions,
        IClassifySubstitute<Color, string>
    {
        Color IClassifySubstitute<Color, string>.FromSubstitute(string instance) { return FromSubstituteD(instance); }
        private Color FromSubstituteD(string instance)
        {
            try
            {
                if (!instance.StartsWith("#") || (instance.Length != 7 && instance.Length != 9))
                    throw new Exception();
                int alpha = instance.Length == 7 ? 255 : int.Parse(instance.Substring(1, 2), NumberStyles.HexNumber);
                int r = int.Parse(instance.Substring(instance.Length == 7 ? 1 : 3, 2), NumberStyles.HexNumber);
                int g = int.Parse(instance.Substring(instance.Length == 7 ? 3 : 5, 2), NumberStyles.HexNumber);
                int b = int.Parse(instance.Substring(instance.Length == 7 ? 5 : 7, 2), NumberStyles.HexNumber);
                return Color.FromArgb((byte)alpha, (byte)r, (byte)g, (byte)b);
            }
            catch
            {
                return Colors.Black;
            }
        }

        public string ToSubstitute(Color instance)
        {
            return instance.A == 255
                ? string.Format("#{0:X2}{1:X2}{2:X2}", instance.R, instance.G, instance.B)
                : string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", instance.A, instance.R, instance.G, instance.B);
        }
    }
}
