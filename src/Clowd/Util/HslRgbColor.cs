using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using NAudio.Wave;
using Vanara.PInvoke;
using YamlDotNet.Core.Tokens;

namespace Clowd.Util
{
    public sealed class HslRgbColor : SimpleNotifyObject, ICloneable, IEquatable<HslRgbColor>
    {
        public static HslRgbColor White => new HslRgbColor(255, 255, 255, 1d);

        public static HslRgbColor Black => new HslRgbColor(0, 0, 0, 1d);

        public static HslRgbColor Transparent => new HslRgbColor(0, 0, 0, 0);

        public double Hue
        {
            get => _h;
            set
            {
                if (Set(ref _h, Clamp(value, 0, 360)))
                    UpdateRgb();
            }
        }

        public double Saturation
        {
            get => _s;
            set
            {
                if (Set(ref _s, Clamp(value)))
                    UpdateRgb();
            }
        }

        public double Lightness
        {
            get => _l;
            set
            {
                if (Set(ref _l, Clamp(value)))
                    UpdateRgb();
            }
        }

        public double Alpha
        {
            get => _a;
            set => Set(ref _a, Clamp(value));
        }

        public int R
        {
            get => _r;
            set
            {
                if (Set(ref _r, ClampToByte(value)))
                    UpdateHsl();
            }
        }

        public int G
        {
            get => _g;
            set
            {
                if (Set(ref _g, ClampToByte(value)))
                    UpdateHsl();
            }
        }

        public int B
        {
            get => _b;
            set
            {
                if (Set(ref _b, ClampToByte(value)))
                    UpdateHsl();
            }
        }

        private double _h = 0d;
        private double _s = 1d;
        private double _l = 0.5d;
        private double _a = 1d;
        private byte _r = 0;
        private byte _g = 0;
        private byte _b = 0;

        public HslRgbColor(double h, double s, double l, double a)
        {
            _h = Clamp(h, 0, 360);
            _s = Clamp(s);
            _l = Clamp(l);
            _a = Clamp(a);
            UpdateRgb();
        }

        public HslRgbColor(int r, int g, int b, double a)
        {
            _r = ClampToByte(r);
            _g = ClampToByte(g);
            _b = ClampToByte(b);
            _a = Clamp(a);
            UpdateHsl();
        }

        public System.Drawing.Color ToDrawingColor()
        {
            return System.Drawing.Color.FromArgb(ClampToByte((int)Math.Round(_a * 255d)), _r, _g, _b);
        }

        public static HslRgbColor FromColor(System.Drawing.Color color)
        {
            return new HslRgbColor(color.R, color.G, color.B, color.A);
        }

        public Color ToColor()
        {
            return Color.FromArgb(ClampToByte((int)Math.Round(_a * 255d)), _r, _g, _b);
        }

        public static HslRgbColor FromColor(Color color)
        {
            return new HslRgbColor(color.R, color.G, color.B, color.A);
        }

        private void UpdateRgb()
        {
            double hue = _h, saturation = _s, lightness = _l;

            while (hue >= 360.0) hue -= 360.0;
            while (hue < 0.0) hue += 360.0;

            saturation = saturation < 0.0 ? 0.0 : saturation;
            saturation = saturation > 1.0 ? 1.0 : saturation;

            lightness = lightness < 0.0 ? 0.0 : lightness;
            lightness = lightness > 1.0 ? 1.0 : lightness;

            double chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
            double h1 = hue / 60;
            double x = chroma * (1 - Math.Abs((h1 % 2) - 1));
            double m = lightness - (0.5 * chroma);
            double r1, g1, b1;

            if (h1 < 1)
            {
                r1 = chroma;
                g1 = x;
                b1 = 0;
            }
            else if (h1 < 2)
            {
                r1 = x;
                g1 = chroma;
                b1 = 0;
            }
            else if (h1 < 3)
            {
                r1 = 0;
                g1 = chroma;
                b1 = x;
            }
            else if (h1 < 4)
            {
                r1 = 0;
                g1 = x;
                b1 = chroma;
            }
            else if (h1 < 5)
            {
                r1 = x;
                g1 = 0;
                b1 = chroma;
            }
            else
            {
                r1 = chroma;
                g1 = 0;
                b1 = x;
            }

            Set(ref _r, (byte)Math.Round(255 * (r1 + m)), nameof(R));
            Set(ref _g, (byte)Math.Round(255 * (g1 + m)), nameof(G));
            Set(ref _b, (byte)Math.Round(255 * (b1 + m)), nameof(B));
        }

        private void UpdateHsl()
        {
            double red = _r / 255d;
            double green = _g / 255d;
            double blue = _b / 255d;

            double c_min = Math.Min(Math.Min(red, green), blue);
            double c_max = Math.Max(Math.Max(red, green), blue);
            double c_delta = c_max - c_min;

            double hue = 0;
            double saturation = 0;
            double lightness = (double)((c_max + c_min) / 2.0d);

            if (lightness == 0 || lightness == 1)
            {
                Set(ref _l, lightness, nameof(Lightness));
                return;
            }

            if (c_delta != 0)
            {
                if (lightness < 0.5d)
                {
                    saturation = (double)(c_delta / (c_max + c_min));
                }
                else
                {
                    saturation = (double)(c_delta / (2.0d - c_max - c_min));
                }

                if (red == c_max)
                {
                    hue = (green - blue) / c_delta;
                }
                else if (green == c_max)
                {
                    hue = 2d + (blue - red) / c_delta;
                }
                else if (blue == c_max)
                {
                    hue = 4d + (red - green) / c_delta;
                }
            }

            hue *= 60d;
            while (hue >= 360.0) hue -= 360.0;
            while (hue < 0.0) hue += 360.0;

            Set(ref _h, hue, nameof(Hue));
            Set(ref _s, saturation, nameof(Saturation));
            Set(ref _l, lightness, nameof(Lightness));
        }

        private byte ClampToByte(int v) => (byte)Math.Min(255, Math.Max(0, v));

        private double Clamp(double v, double min = 0d, double max = 1d) => Math.Min(max, Math.Max(min, v));

        object ICloneable.Clone() => Clone();

        public HslRgbColor Clone() => new HslRgbColor(_h, _s, _l, _a);

        public override bool Equals(object obj)
        {
            if (obj is HslRgbColor clr)
                return Equals(clr);

            return false;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = _h.GetHashCode();
                hashCode = (hashCode * 397) ^ _s.GetHashCode();
                hashCode = (hashCode * 397) ^ _l.GetHashCode();
                hashCode = (hashCode * 397) ^ _a.GetHashCode();
                return hashCode;
            }
        }

        public bool Equals(HslRgbColor other)
        {
            return _h == other._h
                && _s == other._s
                && _l == other._l
                && _a == other._a;
        }

        public static bool operator ==(HslRgbColor obj1, HslRgbColor obj2)
        {
            if (ReferenceEquals(obj1, obj2))
                return true;
            if (ReferenceEquals(obj1, null))
                return false;
            if (ReferenceEquals(obj2, null))
                return false;
            return obj1.Equals(obj2);
        }

        public static bool operator !=(HslRgbColor obj1, HslRgbColor obj2) => !(obj1 == obj2);
    }
}
