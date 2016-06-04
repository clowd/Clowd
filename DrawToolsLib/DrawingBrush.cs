using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using DrawToolsLib.Annotations;

namespace DrawToolsLib
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
                var brush = new RadialGradientBrush(new GradientStopCollection(new GradientStop[]
                {
                    new GradientStop(_currentColor, 0),
                    new GradientStop(_currentColor, _hardness),
                    new GradientStop(Color.FromArgb(0,_currentColor.R,_currentColor.G,_currentColor.B), 1),
                }));
                return brush;
            }
        }
        public Size Size
        {
            get
            {
                return new Size(_radius * 2, _radius * 2);
            }
        }
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

        private Color _currentColor = Colors.Red;
        private int _radius = 16;
        private double _hardness = 0.7;

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
