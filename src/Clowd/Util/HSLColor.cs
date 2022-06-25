using System;
using System.Windows.Media;

namespace Clowd.Util
{
    public class HSLColor : ICloneable
    {
        public double Hue
        {
            get
            {
                var h = _hue * 60d;
                if (h < 0) h += 360;
                return h;
            }
            set
            {
                _hue = value;
                if (_hue > 359)
                {
                    _hue = 0;
                }
                if (_hue < 0)
                {
                    _hue = 359;
                }
                _hue /= 60d;
            }
        }

        public double Saturation
        {
            get { return _sat; }
            set { _sat = Math.Min(1, Math.Max(0, value)); }
        }

        public double Lightness
        {
            get { return _lum; }
            set { _lum = Math.Min(1, Math.Max(0, value)); }
        }

        public double Alpha
        {
            get { return _alpha; }
            set { _alpha = Math.Min(1, Math.Max(0, value)); }
        }

        private double _hue = 0d;
        private double _sat = 1d;
        private double _lum = 0.5d;
        private double _alpha = 1d;

        public HSLColor() { }

        private HSLColor(double H, double S, double L, double A)
        {
            _hue = H;
            _sat = S;
            _lum = L;
            _alpha = A;
        }

        public static HSLColor FromRGB(Color clr)
        {
            return FromRGB(clr.R, clr.G, clr.B, clr.A);
        }

        public static HSLColor FromRGB(byte R, byte G, byte B, byte A = 255)
        {
            double _R = (R / 255d);
            double _G = (G / 255d);
            double _B = (B / 255d);
            double _A = (A / 255d);

            double _Min = Math.Min(Math.Min(_R, _G), _B);
            double _Max = Math.Max(Math.Max(_R, _G), _B);
            double _Delta = _Max - _Min;

            double H = 0;
            double S = 0;
            double L = (double)((_Max + _Min) / 2.0d);

            if (_Delta != 0)
            {
                if (L < 0.5d)
                {
                    S = (double)(_Delta / (_Max + _Min));
                }
                else
                {
                    S = (double)(_Delta / (2.0d - _Max - _Min));
                }


                if (_R == _Max)
                {
                    H = (_G - _B) / _Delta;
                }
                else if (_G == _Max)
                {
                    H = 2d + (_B - _R) / _Delta;
                }
                else if (_B == _Max)
                {
                    H = 4d + (_R - _G) / _Delta;
                }
            }

            return new HSLColor(H, S, L, _A);
        }

        private double Hue_2_RGB(double v1, double v2, double vH)
        {
            if (vH < 0) vH += 1;
            if (vH > 1) vH -= 1;
            if ((6 * vH) < 1) return (v1 + (v2 - v1) * 6 * vH);
            if ((2 * vH) < 1) return (v2);
            if ((3 * vH) < 2) return (v1 + (v2 - v1) * ((2 / 3) - vH) * 6);
            return (v1);
        }

        public Color ToRGB()
        {
            byte r, g, b, a = (byte)Math.Round(_alpha * 255d);
            if (_sat == 0)
            {
                r = (byte)Math.Round(_lum * 255d);
                g = (byte)Math.Round(_lum * 255d);
                b = (byte)Math.Round(_lum * 255d);
            }
            else
            {
                double t1, t2;
                double th = _hue / 6.0d;

                if (_lum < 0.5d)
                {
                    t2 = _lum * (1d + _sat);
                }
                else
                {
                    t2 = (_lum + _sat) - (_lum * _sat);
                }

                t1 = 2d * _lum - t2;

                double tr, tg, tb;
                tr = th + (1.0d / 3.0d);
                tg = th;
                tb = th - (1.0d / 3.0d);

                tr = ColorCalc(tr, t1, t2);
                tg = ColorCalc(tg, t1, t2);
                tb = ColorCalc(tb, t1, t2);
                r = (byte)Math.Round(tr * 255d);
                g = (byte)Math.Round(tg * 255d);
                b = (byte)Math.Round(tb * 255d);
            }

            return Color.FromArgb(a, r, g, b);
        }

        private static double ColorCalc(double c, double t1, double t2)
        {
            if (c < 0) c += 1d;
            if (c > 1) c -= 1d;
            if (6.0d * c < 1.0d) return t1 + (t2 - t1) * 6.0d * c;
            if (2.0d * c < 1.0d) return t2;
            if (3.0d * c < 2.0d) return t1 + (t2 - t1) * (2.0d / 3.0d - c) * 6.0d;
            return t1;
        }

        public bool IsBoring()
        {
            //these are arbitrary, i just tweaked them for what i thought looked good.
            if (Lightness < 25 || Lightness > 70 || Saturation < 35)
            {
                return true;
            }

            return false;
        }

        public void Excite()
        {
            if (Saturation < 10)
            {
                // this is not saturated (no color, so a gray or black)
                // so display the official clowd color instead.
                Hue = 204;
                Saturation = 63;
                Lightness = 53;
            }
            else
            {
                //these are arbitrary, i just tweaked them for what i thought looked good.
                Saturation = Math.Max(Saturation, 45);
                Lightness = Math.Max(Lightness, 25);
                Lightness = Math.Min(Lightness, 70);
            }
        }

        public HSLColor Clone()
        {
            return new HSLColor(_hue, _sat, _lum, _alpha);
        }

        object ICloneable.Clone()
        {
            return new HSLColor(_hue, _sat, _lum, _alpha);
        }
    }
}
