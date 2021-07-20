using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32.SafeHandles;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace Clowd.Drawing
{
    public class DrawingBrush : INotifyPropertyChanged
    {
        public Color Color
        {
            get { return _currentColor; }
            set
            {
                if (value.Equals(_currentColor)) return;
                _currentColor = value;
                OnPropertyChanged(nameof(Color));
                OnPropertyChanged(nameof(Brush));
            }
        }
        public Brush Brush
        {
            get
            {
                if (Type == DrawingBrushType.Circle)
                {
                    return new RadialGradientBrush(new GradientStopCollection(new GradientStop[]
                    {
                        new GradientStop(_currentColor, 0),
                        new GradientStop(_currentColor, _hardness),
                        new GradientStop(Color.FromArgb(0, _currentColor.R, _currentColor.G, _currentColor.B), 1),
                    }));
                }
                return new SolidColorBrush(Color);
            }
        }
        public Size Size => new Size(_radius * 2, _radius * 2);
        public int Radius
        {
            get { return _radius; }
            set
            {
                if (value.Equals(_radius)) return;
                _radius = value;
                OnPropertyChanged(nameof(Radius));
                OnPropertyChanged(nameof(Size));
                OnPropertyChanged(nameof(Brush));
            }
        }
        public double Hardness
        {
            get { return _hardness; }
            set
            {
                if (value.Equals(_hardness)) return;
                _hardness = value;
                OnPropertyChanged(nameof(Hardness));
                OnPropertyChanged(nameof(Brush));
            }
        }
        public DrawingBrushType Type
        {
            get { return _type; }
            set
            {
                if (value == _type) return;
                _type = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Brush));
            }
        }

        private Color _currentColor = Colors.Red;
        private int _radius = 16;
        private double _hardness = 0.7;
        private DrawingBrushType _type = DrawingBrushType.Circle;

        public DrawingBrush()
        {
        }

        public DrawingBrush(DrawingBrushType type, int radius, Color color, int hardness)
        {
            _type = type;
            _radius = radius;
            _hardness = hardness;
            _currentColor = color;
        }

        public Cursor GetBrushCursor(DrawingCanvas canvas)
        {
            var diameter = (int)((_radius * 2) * canvas.ContentScale);
            using (Bitmap curBit = new Bitmap(diameter + 3, diameter + 3))
            {
                using (var bgPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(175, 255, 255, 255), 3))
                using (var fgPen = new System.Drawing.Pen(System.Drawing.Color.Black, 1))
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(curBit))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.High;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    if (Type == DrawingBrushType.Circle)
                    {
                        g.DrawEllipse(bgPen, 1, 1, diameter, diameter);
                        g.DrawEllipse(fgPen, 1, 1, diameter, diameter);
                    }
                    else if (Type == DrawingBrushType.Block)
                    {
                        g.DrawRectangle(bgPen, 1, 1, diameter, diameter);
                        g.DrawRectangle(fgPen, 1, 1, diameter, diameter);
                    }
                }

                return CreateCursorNoResize(curBit, diameter / 2 + 2, diameter / 2 + 2);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #region INTEROP
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetIconInfo(IntPtr hIcon, ref IconInfo pIconInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr CreateIconIndirect(ref IconInfo icon);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon([In] IntPtr hIcon);

        private static Cursor CreateCursorNoResize(Bitmap bmp, int xHotSpot, int yHotSpot)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                ptr = bmp.GetHicon();
                IconInfo tmp = new IconInfo();
                GetIconInfo(ptr, ref tmp);
                tmp.xHotspot = xHotSpot;
                tmp.yHotspot = yHotSpot;
                tmp.fIcon = false;

                var cursorPtr = CreateIconIndirect(ref tmp);
                SafeIconHandle panHandle = new SafeIconHandle(cursorPtr);
                return System.Windows.Interop.CursorInteropHelper.Create(panHandle);
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    DestroyIcon(ptr);
            }
        }

        private struct IconInfo
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        private class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeIconHandle(IntPtr hIcon)
                : base(true)
            {
                this.SetHandle(hIcon);
            }

            protected override bool ReleaseHandle()
            {
                return DestroyIcon(this.handle);
            }
        }
        #endregion
    }

    public enum DrawingBrushType
    {
        Circle,
        Block
    }
}
