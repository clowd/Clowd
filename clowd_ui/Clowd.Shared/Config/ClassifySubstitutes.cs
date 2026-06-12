using System;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using RT.Serialization;

namespace Clowd.Config
{
    /// <summary>
    /// String-based Classify substitutes for Avalonia value types (WPF-era shapes where free).
    /// The undo XML-diff in UndoManager.GetChangedXmlNodes depends on the element shape — these
    /// substitutes keep one XML element per property, which preserves merge semantics.
    /// </summary>
    public static class ClassifySubstitutes
    {
        private static readonly object _lock = new object();
        private static bool _registered;

        /// <summary>
        /// Installs the substitutes into <see cref="Classify.DefaultOptions"/>. Idempotent; called
        /// from the static constructors of UndoManager and SettingsRoot.
        /// </summary>
        public static void EnsureRegistered()
        {
            lock (_lock)
            {
                if (_registered)
                    return;

                Classify.DefaultOptions = AddTo(Classify.DefaultOptions ?? new ClassifyOptions());
                _registered = true;
            }
        }

        /// <summary>
        /// Returns a fresh <see cref="ClassifyOptions"/> with all substitutes added.
        /// </summary>
        public static ClassifyOptions CreateOptions()
        {
            return AddTo(new ClassifyOptions());
        }

        /// <summary>
        /// Adds all substitutes to the provided options instance and returns it.
        /// </summary>
        public static ClassifyOptions AddTo(ClassifyOptions options)
        {
            options.AddTypeSubstitution(new ColorSubstitute());
            options.AddTypeSubstitution(new PointSubstitute());
            options.AddTypeSubstitution(new SizeSubstitute());
            options.AddTypeSubstitution(new RectSubstitute());
            options.AddTypeSubstitution(new PixelRectSubstitute());
            options.AddTypeSubstitution(new FontStyleSubstitute());
            options.AddTypeSubstitution(new FontWeightSubstitute());
            options.AddTypeSubstitution(new FontStretchSubstitute());
            options.AddTypeSubstitution(new KeySubstitute());
            options.AddTypeSubstitution(new KeyModifiersSubstitute());
            return options;
        }

        private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);

        private static bool TrySplit(string instance, int count, out double[] values)
        {
            values = null;
            if (string.IsNullOrWhiteSpace(instance))
                return false;

            var parts = instance.Split(',');
            if (parts.Length != count)
                return false;

            var result = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                    return false;
            }

            values = result;
            return true;
        }

        /// <summary>Color ↔ "#AARRGGBB" (accepts "#RRGGBB"). Parse failure → Black.</summary>
        private sealed class ColorSubstitute : IClassifySubstitute<Color, string>
        {
            public Color FromSubstitute(string instance)
            {
                try
                {
                    if (instance == null || !instance.StartsWith("#") || (instance.Length != 7 && instance.Length != 9))
                        return Colors.Black;
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
                return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", instance.A, instance.R, instance.G, instance.B);
            }
        }

        /// <summary>Point ↔ "x,y".</summary>
        private sealed class PointSubstitute : IClassifySubstitute<Point, string>
        {
            public Point FromSubstitute(string instance)
            {
                if (TrySplit(instance, 2, out var v))
                    return new Point(v[0], v[1]);
                return default;
            }

            public string ToSubstitute(Point instance)
            {
                return Fmt(instance.X) + "," + Fmt(instance.Y);
            }
        }

        /// <summary>Size ↔ "w,h".</summary>
        private sealed class SizeSubstitute : IClassifySubstitute<Size, string>
        {
            public Size FromSubstitute(string instance)
            {
                if (TrySplit(instance, 2, out var v))
                    return new Size(v[0], v[1]);
                return default;
            }

            public string ToSubstitute(Size instance)
            {
                return Fmt(instance.Width) + "," + Fmt(instance.Height);
            }
        }

        /// <summary>Rect ↔ "x,y,w,h".</summary>
        private sealed class RectSubstitute : IClassifySubstitute<Rect, string>
        {
            public Rect FromSubstitute(string instance)
            {
                if (TrySplit(instance, 4, out var v))
                    return new Rect(v[0], v[1], v[2], v[3]);
                return default;
            }

            public string ToSubstitute(Rect instance)
            {
                return Fmt(instance.X) + "," + Fmt(instance.Y) + "," + Fmt(instance.Width) + "," + Fmt(instance.Height);
            }
        }

        /// <summary>PixelRect ↔ "x,y,w,h" (replaces WPF Int32Rect).</summary>
        private sealed class PixelRectSubstitute : IClassifySubstitute<PixelRect, string>
        {
            public PixelRect FromSubstitute(string instance)
            {
                if (string.IsNullOrWhiteSpace(instance))
                    return default;

                var parts = instance.Split(',');
                if (parts.Length != 4)
                    return default;

                var v = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
                        return default;
                }

                return new PixelRect(v[0], v[1], v[2], v[3]);
            }

            public string ToSubstitute(PixelRect instance)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", instance.X, instance.Y, instance.Width, instance.Height);
            }
        }

        /// <summary>FontStyle ↔ enum name string (parse failure → Normal).</summary>
        private sealed class FontStyleSubstitute : IClassifySubstitute<FontStyle, string>
        {
            public FontStyle FromSubstitute(string instance)
            {
                if (instance != null && Enum.TryParse<FontStyle>(instance, true, out var result))
                    return result;
                return FontStyle.Normal;
            }

            public string ToSubstitute(FontStyle instance)
            {
                return instance.ToString();
            }
        }

        /// <summary>FontWeight ↔ enum name string (parse failure → Normal).</summary>
        private sealed class FontWeightSubstitute : IClassifySubstitute<FontWeight, string>
        {
            public FontWeight FromSubstitute(string instance)
            {
                if (instance != null && Enum.TryParse<FontWeight>(instance, true, out var result))
                    return result;
                return FontWeight.Normal;
            }

            public string ToSubstitute(FontWeight instance)
            {
                return instance.ToString();
            }
        }

        /// <summary>FontStretch ↔ enum name string (parse failure → Normal).</summary>
        private sealed class FontStretchSubstitute : IClassifySubstitute<FontStretch, string>
        {
            public FontStretch FromSubstitute(string instance)
            {
                if (instance != null && Enum.TryParse<FontStretch>(instance, true, out var result))
                    return result;
                return FontStretch.Normal;
            }

            public string ToSubstitute(FontStretch instance)
            {
                return instance.ToString();
            }
        }

        /// <summary>Key ↔ name string (parse failure → None).</summary>
        private sealed class KeySubstitute : IClassifySubstitute<Key, string>
        {
            public Key FromSubstitute(string instance)
            {
                if (instance != null && Enum.TryParse<Key>(instance, true, out var result))
                    return result;
                return Key.None;
            }

            public string ToSubstitute(Key instance)
            {
                return instance.ToString();
            }
        }

        /// <summary>KeyModifiers ↔ name string (parse failure → None).</summary>
        private sealed class KeyModifiersSubstitute : IClassifySubstitute<KeyModifiers, string>
        {
            public KeyModifiers FromSubstitute(string instance)
            {
                if (instance != null && Enum.TryParse<KeyModifiers>(instance, true, out var result))
                    return result;
                return KeyModifiers.None;
            }

            public string ToSubstitute(KeyModifiers instance)
            {
                return instance.ToString();
            }
        }
    }
}
