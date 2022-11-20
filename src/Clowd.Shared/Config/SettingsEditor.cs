using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RT.Serialization;
using RT.Util;

namespace Clowd
{
    public enum ToolType
    {
        None,
        Pointer,
        Rectangle,
        FilledRectangle,
        Ellipse,
        Line,
        Arrow,
        PolyLine,
        Text,
        Count,
        Pixelate,
    };

    namespace Config
    {
        [ClassifyIgnoreIfDefault, ClassifyIgnoreIfEmpty]
        public class SavedToolSettings : SimpleNotifyObject
        {
            public bool AutoColor
            {
                get => _autoColor ?? true;
                set => Set(ref _autoColor, value);
            }

            public Color ObjectColor
            {
                get => _objectColor ?? Colors.Red;
                set => Set(ref _objectColor, value);
            }

            public double LineWidth
            {
                get => _lineWidth ?? 2d;
                set => Set(ref _lineWidth, value);
            }

            public string FontFamily
            {
                get => _fontFamily ?? "Segoe UI";
                set => Set(ref _fontFamily, value);
            }

            public double FontSize
            {
                get => _fontSize ?? 12d;
                set => Set(ref _fontSize, value);
            }

            public FontStyle FontStyle
            {
                get => _fontStyle ?? FontStyles.Normal;
                set => Set(ref _fontStyle, value);
            }

            public FontWeight FontWeight
            {
                get => _fontWeight ?? FontWeights.Normal;
                set => Set(ref _fontWeight, value);
            }

            public FontStretch FontStretch
            {
                get => _fontStretch ?? FontStretches.Normal;
                set => Set(ref _fontStretch, value);
            }

            public double BlurRadius
            {
                get => _blurRadius ?? 8d;
                set => Set(ref _blurRadius, value);
            }

            private FontStretch? _fontStretch;
            private FontWeight? _fontWeight;
            private FontStyle? _fontStyle;
            private double? _fontSize;
            private string _fontFamily;
            private double? _lineWidth;
            private Color? _objectColor;
            private bool? _autoColor;
            private double? _blurRadius;
        }

        public class SettingsEditor : CategoryBase
        {
            public bool RestoreSessionsOnClowdStart
            {
                get => _restoreSessionsOnClowdStart;
                set => Set(ref _restoreSessionsOnClowdStart, value);
            }

            public Color CanvasBackground
            {
                get => _canvasBackground;
                set => Set(ref _canvasBackground, value);
            }

            [Browsable(false)]
            public int StartupPadding
            {
                get => _startupPadding;
                set => Set(ref _startupPadding, value);
            }

            [DisplayName("Tool preferences")]
            public AutoDictionary<ToolType, SavedToolSettings> Tools
            {
                get => _tools;
                set => Set(ref _tools, value);
            }

            public TimeOption DeleteSessionsAfter
            {
                get => _deleteSessionsAfter;
                set => Set(ref _deleteSessionsAfter, value);
            }

            private Color _canvasBackground = Colors.Transparent;
            private int _startupPadding = 30;
            private TimeOption _deleteSessionsAfter = new TimeOption(30, TimeOptionUnit.Days);
            private AutoDictionary<ToolType, SavedToolSettings> _tools = new AutoDictionary<ToolType, SavedToolSettings>();
            private bool _restoreSessionsOnClowdStart = true;

            public SettingsEditor()
            {
                Subscribe(Tools);
            }
        }
    }
}
